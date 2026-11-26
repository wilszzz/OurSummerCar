using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Компонент-репортер столкновений кузова.
    /// Срабатывает ТОЛЬКО для локально симулируемого транспорта (rb != null && !rb.isKinematic)
    /// и подавляется во время телепортов (BetterCheatBox / лаг-снапы).
    /// </summary>
    internal sealed class VehicleCollisionReporter : MonoBehaviour
    {
        private VehicleDamageAndGlassSync owner;
        private VehicleCatalogEntry vehicleEntry;
        private Rigidbody cachedRb;
        private float lastReportTime;

        public void Initialize(VehicleDamageAndGlassSync ownerService, VehicleCatalogEntry entry)
        {
            owner = ownerService;
            vehicleEntry = entry;
            cachedRb = GetComponent<Rigidbody>() ?? GetComponentInParent<Rigidbody>();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (owner == null || vehicleEntry == null || collision == null) return;
            if (cachedRb == null) cachedRb = GetComponent<Rigidbody>() ?? GetComponentInParent<Rigidbody>();

            // Не отправляем столкновения от удалённо интерполируемого кинематического транспорта
            if (cachedRb == null || cachedRb.isKinematic) return;

            // Подавляем ложные удары сразу после телепорта BetterCheatBox или лаг-снапа
            if (Time.time < vehicleEntry.SuppressCollisionUntilTime) return;

            if (collision.relativeVelocity.magnitude < 2.5f) return;
            if ((Time.time - lastReportTime) < 0.25f) return;

            lastReportTime = Time.time;
            Vector3 hitPoint = collision.contacts != null && collision.contacts.Length > 0
                ? collision.contacts[0].point
                : transform.position;
            owner.NotifyLocalCrash(vehicleEntry, hitPoint, collision.relativeVelocity);
        }
    }

    /// <summary>
    /// Синхронизация повреждений кузова (CarDamage + все дочерние Deformable) и разрушения/ремонта стёкол/лобового стекла
    /// с подавлением ложных коллизий при телепортации (PacketType.VehicleDamageSync = 74).
    /// </summary>
    internal sealed class VehicleDamageAndGlassSync
    {
        private sealed class GlassEntry
        {
            public int GlassHash;
            public string HierarchyPath;
            public VehicleCatalogEntry Vehicle;
            public GameObject GlassObject;
            public PlayMakerFSM ShatterFsm;
            public Renderer GlassRenderer;
            public Collider GlassCollider;
            public bool IsBroken;
            public bool SuppressNext;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly VehicleCatalogEntry[] catalog;

        private readonly List<CarDamage> trackedCarDamages = new List<CarDamage>();
        private readonly List<Deformable> trackedDeformables = new List<Deformable>();
        private readonly Dictionary<int, GlassEntry> glassByHash = new Dictionary<int, GlassEntry>();
        private readonly HashSet<int> scannedGlassObjects = new HashSet<int>();
        private readonly HashSet<byte> scannedVehicleIds = new HashSet<byte>();
        private readonly Dictionary<int, bool> lastCarDamageRepairState = new Dictionary<int, bool>();
        private readonly Dictionary<byte, float> suppressRepairDetectUntilByVehId = new Dictionary<byte, float>();

        private float nextScanTime;
        private float nextGlassPollTime;

        public int TrackedDamageCount => trackedCarDamages.Count + trackedDeformables.Count;
        public int TrackedGlassCount => glassByHash.Count;
        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public VehicleDamageAndGlassSync(OscHostContext context, string ownerModuleId, VehicleCatalogEntry[] vehicleCatalog)
        {
            ctx = context;
            moduleId = ownerModuleId;
            catalog = vehicleCatalog;
        }

        public void Reset()
        {
            trackedCarDamages.Clear();
            trackedDeformables.Clear();
            glassByHash.Clear();
            scannedGlassObjects.Clear();
            scannedVehicleIds.Clear();
            lastCarDamageRepairState.Clear();
            suppressRepairDetectUntilByVehId.Clear();
            nextScanTime = 0f;
            nextGlassPollTime = 0f;
        }

        public void OnLevelLoaded()
        {
            ScanDamageAndGlass();
        }

        public void OnUpdate()
        {
            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + 10f;
                if (trackedCarDamages.Count == 0 || glassByHash.Count == 0 || HasUnscannedBoundVehicle())
                {
                    ScanDamageAndGlass();
                }
            }

            if (IsNetworkRunning && Time.time >= nextGlassPollTime)
            {
                nextGlassPollTime = Time.time + 0.25f;
                PollGlassStateChanges();
                PollBodyRepairChanges();
            }
        }

        private bool HasUnscannedBoundVehicle()
        {
            if (catalog == null) return false;
            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (veh != null && veh.IsBound && !scannedVehicleIds.Contains(veh.VehicleId))
                {
                    return true;
                }
            }
            return false;
        }

        public void HandleTeleportEvent(VehicleTeleportEvent evt)
        {
            float until = Time.time + 0.75f;
            VehicleCatalogEntry entry = ControlsHelpers.FindVehicleById(catalog, evt.VehicleId)
                ?? ControlsHelpers.FindVehicleByTransform(catalog, evt.VehicleTransform);
            if (entry != null)
            {
                entry.SuppressCollisionUntilTime = until;
            }
        }

        public void HandlePartAssembled(PartAssembledEvent evt)
        {
            scannedVehicleIds.Clear();
            nextScanTime = Time.time + 0.35f;
        }

        public void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            foreach (var kv in glassByHash)
            {
                GlassEntry g = kv.Value;
                if (g == null || !g.IsBroken) continue;
                using (var p = new PacketWriter(PacketType.VehicleDamageSync))
                {
                    p.WriteByte((byte)DamageSubType.GlassBreak);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteInt32(g.GlassHash);
                    p.WriteString(g.HierarchyPath ?? string.Empty);
                    ctx.Transport.SendTo(p, clientId);
                }
            }
        }

        public void ScanDamageAndGlass()
        {
            trackedCarDamages.Clear();
            CarDamage[] damages = UnityEngine.Object.FindObjectsOfType<CarDamage>();
            if (damages != null)
            {
                for (int i = 0; i < damages.Length; i++)
                {
                    CarDamage cd = damages[i];
                    if (cd == null) continue;
                    trackedCarDamages.Add(cd);
                    int cdId = cd.GetInstanceID();
                    if (!lastCarDamageRepairState.ContainsKey(cdId))
                    {
                        lastCarDamageRepairState[cdId] = cd.repair;
                    }

                    VehicleCatalogEntry veh = ControlsHelpers.FindVehicleByTransform(catalog, cd.transform);
                    if (veh != null && cd.GetComponent<VehicleCollisionReporter>() == null)
                    {
                        var rep = cd.gameObject.AddComponent<VehicleCollisionReporter>();
                        rep.Initialize(this, veh);
                    }
                }
            }

            trackedDeformables.Clear();
            Deformable[] defs = UnityEngine.Object.FindObjectsOfType<Deformable>();
            if (defs != null)
            {
                trackedDeformables.AddRange(defs);
            }

            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (veh.IsBound)
                {
                    scannedVehicleIds.Add(veh.VehicleId);
                    ScanVehicleGlass(veh);
                }
            }
        }

        private void ScanVehicleGlass(VehicleCatalogEntry veh)
        {
            if (veh == null || veh.RootObject == null) return;
            Transform[] transforms = veh.RootObject.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null) continue;
                string name = t.name;
                if (!IsGlassObjectName(name)) continue;

                int objId = t.gameObject.GetInstanceID();
                if (scannedGlassObjects.Contains(objId)) continue;
                scannedGlassObjects.Add(objId);

                PlayMakerFSM shatterFsm = FindGlassFsm(t.gameObject);
                Renderer rend = t.GetComponent<Renderer>();
                Collider col = t.GetComponent<Collider>();
                if (shatterFsm == null && rend == null && col == null) continue;

                string path = ControlsHelpers.GetHierarchyPath(t);
                int glassHash = SceneHasher.GetStableHash(path);
                if (glassByHash.ContainsKey(glassHash)) continue;

                var entry = new GlassEntry
                {
                    GlassHash = glassHash,
                    HierarchyPath = path,
                    Vehicle = veh,
                    GlassObject = t.gameObject,
                    ShatterFsm = shatterFsm,
                    GlassRenderer = rend,
                    GlassCollider = col,
                    IsBroken = !t.gameObject.activeSelf || (rend != null && !rend.enabled)
                };

                if (shatterFsm != null && ctx.Ownership.TryClaim(shatterFsm, moduleId, $"Glass_{veh.DisplayName}_{name}"))
                {
                    shatterFsm.InitializeSafe();
                    HookGlassFsm(entry, shatterFsm);
                }

                glassByHash[glassHash] = entry;
            }
        }

        private static bool IsGlassObjectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return ControlsHelpers.ContainsOrdinalIgnoreCase(name, "windshield") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(name, "windscreen") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(name, "glass") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(name, "window") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(name, "tuulilasi") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(name, "ikkuna");
        }

        private static PlayMakerFSM FindGlassFsm(GameObject go)
        {
            PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
            if (fsms == null) return null;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;
                if (fsm.FsmName == "Break" || fsm.FsmName == "Damage" ||
                    fsm.FsmName == "Shatter" || fsm.FsmName == "Glass" ||
                    fsm.FsmName == "Use" || fsm.FsmName == "Collision")
                {
                    fsm.InitializeSafe();
                    if (fsm.HasState("Shatter") || fsm.HasState("Break") ||
                        fsm.HasState("Broken") || fsm.HasState("Destroy") || fsm.HasState("Death"))
                    {
                        return fsm;
                    }
                }
            }
            return null;
        }

        private void HookGlassFsm(GlassEntry entry, PlayMakerFSM fsm)
        {
            string breakState = fsm.HasState("Shatter") ? "Shatter"
                : (fsm.HasState("Break") ? "Break"
                : (fsm.HasState("Broken") ? "Broken"
                : (fsm.HasState("Destroy") ? "Destroy"
                : (fsm.HasState("Death") ? "Death" : null))));

            if (breakState != null)
            {
                fsm.AddSafeGlobalTransition("MP_SHATTER", breakState);
                fsm.InsertAction(breakState, () =>
                {
                    if (entry.SuppressNext)
                    {
                        entry.SuppressNext = false;
                        return;
                    }
                    if (Time.time < entry.Vehicle.SuppressCollisionUntilTime) return;
                    if (!entry.IsBroken)
                    {
                        entry.IsBroken = true;
                        SendGlassPacket(DamageSubType.GlassBreak, entry);
                    }
                }, 0, true);
            }

            string repairState = fsm.HasState("Repair") ? "Repair"
                : (fsm.HasState("Intact") ? "Intact"
                : (fsm.HasState("Wait") ? "Wait" : null));

            if (repairState != null)
            {
                fsm.AddSafeGlobalTransition("MP_REPAIR", repairState);
                fsm.InsertAction(repairState, () =>
                {
                    if (entry.SuppressNext)
                    {
                        entry.SuppressNext = false;
                        return;
                    }
                    if (entry.IsBroken)
                    {
                        entry.IsBroken = false;
                        SendGlassPacket(DamageSubType.GlassRepair, entry);
                    }
                }, 0, true);
            }
        }

        private void PollGlassStateChanges()
        {
            foreach (var kv in glassByHash)
            {
                GlassEntry g = kv.Value;
                if (g == null || g.GlassObject == null || g.Vehicle == null) continue;
                if (g.Vehicle.MainRigidbody == null || g.Vehicle.MainRigidbody.isKinematic) continue;
                if (Time.time < g.Vehicle.SuppressCollisionUntilTime) continue;

                bool brokenNow = !g.GlassObject.activeSelf ||
                                 (g.GlassRenderer != null && !g.GlassRenderer.enabled) ||
                                 (g.GlassCollider != null && !g.GlassCollider.enabled);

                if (brokenNow == g.IsBroken) continue;
                g.IsBroken = brokenNow;
                SendGlassPacket(brokenNow ? DamageSubType.GlassBreak : DamageSubType.GlassRepair, g);
            }
        }

        private void PollBodyRepairChanges()
        {
            if (trackedCarDamages.Count == 0) return;

            HashSet<byte> sentVehIds = null;
            for (int i = 0; i < trackedCarDamages.Count; i++)
            {
                CarDamage cd = trackedCarDamages[i];
                if (cd == null) continue;

                int cdId = cd.GetInstanceID();
                bool prev = lastCarDamageRepairState.TryGetValue(cdId, out bool prevVal) && prevVal;
                bool now = cd.repair;
                lastCarDamageRepairState[cdId] = now;

                if (prev || !now) continue;

                VehicleCatalogEntry veh = ControlsHelpers.FindVehicleByTransform(catalog, cd.transform);
                if (veh == null) continue;

                if (suppressRepairDetectUntilByVehId.TryGetValue(veh.VehicleId, out float until) && Time.time < until)
                {
                    continue;
                }

                if (sentVehIds == null) sentVehIds = new HashSet<byte>();
                if (sentVehIds.Add(veh.VehicleId))
                {
                    SendVehicleRepair(veh);
                }
            }
        }

        internal void NotifyLocalCrash(VehicleCatalogEntry veh, Vector3 hitPoint, Vector3 impactVelocity)
        {
            if (!IsNetworkRunning || veh == null) return;

            using (var p = new PacketWriter(PacketType.VehicleDamageSync))
            {
                p.WriteByte((byte)DamageSubType.BodyCrash);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(veh.VehicleId);
                p.WriteString(veh.RootObject != null ? veh.RootObject.name : veh.DisplayName);
                p.WriteVector3(hitPoint);
                p.WriteVector3(impactVelocity);
                ctx.Transport.Send(p);
            }
        }

        public void SendVehicleRepair(VehicleCatalogEntry veh)
        {
            if (!IsNetworkRunning || veh == null) return;
            using (var p = new PacketWriter(PacketType.VehicleDamageSync))
            {
                p.WriteByte((byte)DamageSubType.BodyRepair);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(veh.VehicleId);
                p.WriteString(veh.RootObject != null ? veh.RootObject.name : veh.DisplayName);
                ctx.Transport.Send(p);
            }
        }

        private void SendGlassPacket(DamageSubType subType, GlassEntry g)
        {
            if (!IsNetworkRunning || g == null) return;
            using (var p = new PacketWriter(PacketType.VehicleDamageSync))
            {
                p.WriteByte((byte)subType);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(g.GlassHash);
                p.WriteString(g.HierarchyPath ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 5) return;
            var subType = (DamageSubType)reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case DamageSubType.BodyCrash:
                {
                    byte vehId = reader.ReadByte();
                    string vehName = reader.ReadString();
                    Vector3 hitPoint = reader.ReadVector3();
                    Vector3 impactVelocity = reader.ReadVector3();
                    ApplyRemoteBodyCrash(vehId, vehName, hitPoint, impactVelocity);
                    break;
                }
                case DamageSubType.BodyRepair:
                {
                    byte vehId = reader.ReadByte();
                    string vehName = reader.ReadString();
                    ApplyRemoteBodyRepair(vehId, vehName);
                    break;
                }
                case DamageSubType.GlassBreak:
                case DamageSubType.GlassRepair:
                {
                    int glassHash = reader.ReadInt32();
                    string path = reader.ReadString();
                    ApplyRemoteGlassState(glassHash, path, broken: subType == DamageSubType.GlassBreak);
                    break;
                }
            }
        }

        private void ApplyRemoteBodyCrash(byte vehId, string vehName, Vector3 hitPoint, Vector3 impactVelocity)
        {
            VehicleCatalogEntry veh = ControlsHelpers.FindVehicleById(catalog, vehId);
            Transform vehRoot = veh?.RootObject != null ? veh.RootObject.transform : null;

            float speed = impactVelocity.magnitude;
            float radius = Mathf.Clamp(0.45f + speed * 0.025f, 0.45f, 1.35f);
            float dentMagnitude = Mathf.Clamp((speed - 2.0f) * 0.012f, 0.01f, 0.16f);

            // Исправленный цикл БЕЗ break: применяем ко ВСЕМ matching CarDamage и дочерним Deformable этого авто
            for (int i = 0; i < trackedCarDamages.Count; i++)
            {
                CarDamage cd = trackedCarDamages[i];
                if (cd == null) continue;
                if (!MatchesVehicle(cd.transform, vehRoot, vehName)) continue;

                cd.repair = false;
                lastCarDamageRepairState[cd.GetInstanceID()] = false;
                // Если авто сейчас кинематическое, OnCollisionEnter на клиенте не сработает — деформируем меш напрямую
                if (veh != null && veh.MainRigidbody != null && veh.MainRigidbody.isKinematic)
                {
                    ApplyLocalMeshDent(cd.meshFilters, hitPoint, impactVelocity, radius, Mathf.Min(dentMagnitude, cd.maxDeform > 0.01f ? cd.maxDeform : 0.2f));
                }
            }

            for (int i = 0; i < trackedDeformables.Count; i++)
            {
                Deformable def = trackedDeformables[i];
                if (def == null) continue;
                if (!MatchesVehicle(def.transform, vehRoot, vehName)) continue;

                def.Repair(0f, hitPoint, radius);
                if (def.meshFilter != null && veh != null && veh.MainRigidbody != null && veh.MainRigidbody.isKinematic)
                {
                    ApplyLocalMeshDent(new[] { def.meshFilter }, hitPoint, impactVelocity, radius, Mathf.Min(dentMagnitude, def.MaxVertexMov > 0.01f ? def.MaxVertexMov : 0.2f));
                }
            }
        }

        private void ApplyRemoteBodyRepair(byte vehId, string vehName)
        {
            VehicleCatalogEntry veh = ControlsHelpers.FindVehicleById(catalog, vehId);
            Transform vehRoot = veh?.RootObject != null ? veh.RootObject.transform : null;
            suppressRepairDetectUntilByVehId[vehId] = Time.time + 0.5f;
            if (veh != null) suppressRepairDetectUntilByVehId[veh.VehicleId] = Time.time + 0.5f;

            for (int i = 0; i < trackedCarDamages.Count; i++)
            {
                CarDamage cd = trackedCarDamages[i];
                if (cd != null && MatchesVehicle(cd.transform, vehRoot, vehName))
                {
                    cd.repair = true;
                    lastCarDamageRepairState[cd.GetInstanceID()] = true;
                }
            }

            for (int i = 0; i < trackedDeformables.Count; i++)
            {
                Deformable def = trackedDeformables[i];
                if (def != null && MatchesVehicle(def.transform, vehRoot, vehName))
                {
                    def.Repair(1f);
                }
            }
        }

        private void ApplyRemoteGlassState(int glassHash, string path, bool broken)
        {
            GlassEntry entry = null;
            if (!glassByHash.TryGetValue(glassHash, out entry) && !string.IsNullOrEmpty(path))
            {
                foreach (var kv in glassByHash)
                {
                    if (string.Equals(kv.Value.HierarchyPath, path, StringComparison.Ordinal))
                    {
                        entry = kv.Value;
                        break;
                    }
                }
            }

            if (entry == null) return;
            entry.IsBroken = broken;

            if (entry.ShatterFsm != null)
            {
                entry.SuppressNext = true;
                try
                {
                    entry.ShatterFsm.SendEvent(broken ? "MP_SHATTER" : "MP_REPAIR");
                }
                finally
                {
                    entry.SuppressNext = false;
                }
            }

            if (entry.GlassRenderer != null) entry.GlassRenderer.enabled = !broken;
            if (entry.GlassCollider != null) entry.GlassCollider.enabled = !broken;
        }

        private static bool MatchesVehicle(Transform compTr, Transform vehRoot, string vehName)
        {
            if (compTr == null) return false;
            if (vehRoot != null)
            {
                return compTr == vehRoot || compTr.IsChildOf(vehRoot);
            }
            if (!string.IsNullOrEmpty(vehName))
            {
                return compTr.root.name.Equals(vehName, StringComparison.OrdinalIgnoreCase) ||
                       compTr.gameObject.name.Equals(vehName, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static void ApplyLocalMeshDent(MeshFilter[] filters, Vector3 worldHitPoint, Vector3 impactVelocity, float radius, float maxDent)
        {
            if (filters == null || filters.Length == 0 || maxDent <= 0.001f) return;

            Vector3 dirWorld = impactVelocity.sqrMagnitude > 0.01f ? impactVelocity.normalized : Vector3.down;
            float radiusSq = radius * radius;

            for (int f = 0; f < filters.Length; f++)
            {
                MeshFilter mf = filters[f];
                if (mf == null || mf.mesh == null) continue;
                Mesh mesh = mf.mesh;
                if (!mesh.isReadable) continue;

                Transform tr = mf.transform;
                Vector3 localHit = tr.InverseTransformPoint(worldHitPoint);
                Vector3 localDir = tr.InverseTransformDirection(dirWorld);
                Vector3[] verts = mesh.vertices;
                if (verts == null || verts.Length == 0) continue;

                bool modified = false;
                for (int v = 0; v < verts.Length; v++)
                {
                    Vector3 diff = verts[v] - localHit;
                    float distSq = diff.sqrMagnitude;
                    if (distSq < radiusSq)
                    {
                        float falloff = 1f - Mathf.Sqrt(distSq) / radius;
                        verts[v] += localDir * (maxDent * falloff);
                        modified = true;
                    }
                }

                if (modified)
                {
                    mesh.vertices = verts;
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                }
            }
        }
    }
}
