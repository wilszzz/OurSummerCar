using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Boat
{
    internal sealed class BoatMooringSystem
    {
        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly BoatMotorSync motorSync;

        private readonly List<Collider> dockColliders = new List<Collider>();
        private PlayMakerFSM nativeMooringFsm;
        private bool suppressNativeMoorFsm;
        private float nextDockScanTime;

        private bool isMoored;
        private Vector3 boatLocalAnchor = BoatConstants.BowCleatLocalOffset;
        private Vector3 worldDockAnchor;
        private float ropeLength = 3.0f;

        private ConfigurableJoint mooringJoint;
        private LineRenderer ropeRenderer;
        private GameObject ropeVisualObj;
        private const int RopeSegments = 10;

        public bool IsMoored => isMoored;

        public BoatMooringSystem(OscHostContext context, string ownerModuleId, BoatMotorSync motorSync)
        {
            ctx = context;
            moduleId = ownerModuleId;
            this.motorSync = motorSync;
        }

        public void Reset()
        {
            DestroyPhysicsJoint();
            DestroyVisualRope();
            dockColliders.Clear();
            nativeMooringFsm = null;
            suppressNativeMoorFsm = false;
            isMoored = false;
            boatLocalAnchor = BoatConstants.BowCleatLocalOffset;
            worldDockAnchor = Vector3.zero;
            ropeLength = 3.0f;
            nextDockScanTime = 0f;
        }

        public void Discover()
        {
            DiscoverNativeMooringFsm();
            ScanDockAnchors();
        }

        public void HandleTeleport(VehicleTeleportEvent evt)
        {
            if (!motorSync.IsBoatEvent(evt.VehicleId, evt.VehicleTransform)) return;
            if (!isMoored) return;

            if ((evt.NewPosition - worldDockAnchor).sqrMagnitude > BoatConstants.MoorTeleportDetachDistance * BoatConstants.MoorTeleportDetachDistance)
            {
                DetachMooring(BoatMooringAction.Snap, notifyRemote: true);
            }
        }

        public void TickUpdate(bool isLocalPlayerSeatedOnBoat)
        {
            if (motorSync.BoatTransform == null) return;

            if (Time.time >= nextDockScanTime)
            {
                nextDockScanTime = Time.time + 6.0f;
                if (dockColliders.Count == 0) ScanDockAnchors();
                if (nativeMooringFsm == null) DiscoverNativeMooringFsm();
            }

            if (Input.GetKeyDown(KeyCode.M) && CanLocalPlayerToggleMooring(isLocalPlayerSeatedOnBoat))
            {
                if (isMoored)
                {
                    DetachMooring(BoatMooringAction.Detach, notifyRemote: true);
                }
                else
                {
                    TryAttachToNearestDock();
                }
            }

            if (isMoored)
            {
                SyncJointOwnershipState();
                UpdateRopeVisual();
                CheckMooringTensionSnap();
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            BoatMooringAction action = (BoatMooringAction)reader.ReadByte();
            int initiatorId = reader.ReadInt32();
            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;

            if (action == BoatMooringAction.Attach || action == BoatMooringAction.Snapshot)
            {
                bool moored = reader.ReadBoolean();
                Vector3 localAnchor = reader.ReadVector3();
                Vector3 worldAnchor = reader.ReadVector3();
                float length = reader.ReadSingle();

                if (action == BoatMooringAction.Attach && initiatorId == localId) return;

                if (moored)
                {
                    ApplyMooring(localAnchor, worldAnchor, length);
                }
                else
                {
                    DetachMooring(BoatMooringAction.Detach, notifyRemote: false);
                }
            }
            else if (action == BoatMooringAction.Detach || action == BoatMooringAction.Snap)
            {
                if (initiatorId == localId) return;
                DetachMooring(action, notifyRemote: false);
            }
        }

        public void SendSnapshotTo(int targetClientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning || !ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.BoatMooringSync))
            {
                p.WriteByte((byte)BoatMooringAction.Snapshot);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(isMoored);
                p.WriteVector3(boatLocalAnchor);
                p.WriteVector3(worldDockAnchor);
                p.WriteSingle(ropeLength);
                ctx.Transport.SendTo(p, targetClientId);
            }
        }

        private void DiscoverNativeMooringFsm()
        {
            if (nativeMooringFsm != null || motorSync.BoatObject == null) return;

            var fsms = motorSync.BoatObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;
                string n = fsm.gameObject.name;
                string fn = fsm.FsmName;
                if (BoatHelpers.ContainsIgnoreCase(n, "moor") ||
                    BoatHelpers.ContainsIgnoreCase(n, "rope") ||
                    BoatHelpers.ContainsIgnoreCase(fn, "moor") ||
                    BoatHelpers.ContainsIgnoreCase(fn, "rope"))
                {
                    if (!ctx.Ownership.TryClaim(fsm, moduleId, "Boat_MooringFsm")) continue;
                    fsm.InitializeSafe();
                    nativeMooringFsm = fsm;
                    if (fsm.HasState("Attach"))
                    {
                        fsm.InsertAction("Attach", () =>
                        {
                            if (suppressNativeMoorFsm) { suppressNativeMoorFsm = false; return; }
                            if (!isMoored) TryAttachToNearestDock();
                        }, 0, true);
                    }
                    if (fsm.HasState("Detach"))
                    {
                        fsm.InsertAction("Detach", () =>
                        {
                            if (suppressNativeMoorFsm) { suppressNativeMoorFsm = false; return; }
                            if (isMoored) DetachMooring(BoatMooringAction.Detach, notifyRemote: true);
                        }, 0, true);
                    }
                    break;
                }
            }
        }

        private void ScanDockAnchors()
        {
            dockColliders.Clear();
            var allCols = UnityEngine.Object.FindObjectsOfType<Collider>();
            for (int i = 0; i < allCols.Length; i++)
            {
                Collider c = allCols[i];
                if (c == null || c.isTrigger) continue;

                string n = c.gameObject.name;
                string rootN = c.transform.root != null ? c.transform.root.name : string.Empty;
                if (BoatHelpers.ContainsIgnoreCase(n, "laituri") ||
                    BoatHelpers.ContainsIgnoreCase(n, "pier") ||
                    BoatHelpers.ContainsIgnoreCase(n, "dock") ||
                    BoatHelpers.ContainsIgnoreCase(n, "jetty") ||
                    BoatHelpers.ContainsIgnoreCase(n, "bollard") ||
                    BoatHelpers.ContainsIgnoreCase(n, "moor") ||
                    BoatHelpers.ContainsIgnoreCase(rootN, "laituri") ||
                    BoatHelpers.ContainsIgnoreCase(rootN, "pier") ||
                    BoatHelpers.ContainsIgnoreCase(rootN, "dock") ||
                    BoatHelpers.ContainsIgnoreCase(rootN, "cottage"))
                {
                    dockColliders.Add(c);
                }
            }
        }

        private bool CanLocalPlayerToggleMooring(bool isSeatedOnBoat)
        {
            if (motorSync.BoatTransform == null) return false;
            if (isSeatedOnBoat) return true;

            Transform camTr = Camera.main != null ? Camera.main.transform : null;
            if (camTr == null)
            {
                GameObject player = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (player != null) camTr = player.transform;
            }

            if (camTr == null) return false;
            return (camTr.position - motorSync.BoatTransform.position).sqrMagnitude <= BoatConstants.PlayerInteractDistance * BoatConstants.PlayerInteractDistance;
        }

        private void TryAttachToNearestDock()
        {
            if (motorSync.BoatTransform == null) return;

            Vector3 bowWorld = motorSync.BoatTransform.TransformPoint(BoatConstants.BowCleatLocalOffset);
            Vector3 sternWorld = motorSync.BoatTransform.TransformPoint(BoatConstants.SternCleatLocalOffset);

            Vector3 bestLocalCleat = BoatConstants.BowCleatLocalOffset;
            Vector3 bestDockPoint = Vector3.zero;
            float bestDistSq = BoatConstants.MoorSearchRadius * BoatConstants.MoorSearchRadius;
            bool found = false;

            // 1. Поиск по кэшированным причалам/пирсам
            for (int i = 0; i < dockColliders.Count; i++)
            {
                Collider dc = dockColliders[i];
                if (dc == null || !dc.enabled || !dc.gameObject.activeInHierarchy) continue;

                EvaluateAnchorCandidate(dc, bowWorld, BoatConstants.BowCleatLocalOffset, ref bestDistSq, ref bestLocalCleat, ref bestDockPoint, ref found);
                EvaluateAnchorCandidate(dc, sternWorld, BoatConstants.SternCleatLocalOffset, ref bestDistSq, ref bestLocalCleat, ref bestDockPoint, ref found);
            }

            // 2. Fallback: статические коллайдеры пирсов/береговых опор в радиусе поиска
            if (!found)
            {
                SearchOverlapAnchors(bowWorld, BoatConstants.BowCleatLocalOffset, ref bestDistSq, ref bestLocalCleat, ref bestDockPoint, ref found);
                SearchOverlapAnchors(sternWorld, BoatConstants.SternCleatLocalOffset, ref bestDistSq, ref bestLocalCleat, ref bestDockPoint, ref found);
            }

            if (!found)
            {
                ctx?.Log?.Invoke("[Boat/Mooring] Поблизости нет причала или опоры для привязки лодки (радиус 6.5м)");
                return;
            }

            float initialDist = Vector3.Distance(motorSync.BoatTransform.TransformPoint(bestLocalCleat), bestDockPoint);
            float clampedLen = Mathf.Clamp(initialDist + 0.65f, 1.8f, BoatConstants.MoorSearchRadius + 0.5f);

            ApplyMooring(bestLocalCleat, bestDockPoint, clampedLen);
            SendMooringAction(BoatMooringAction.Attach);
            ctx?.Log?.Invoke($"[Boat/Mooring] Лодка привязана к причалу (длина троса {clampedLen:F1}м)");
        }

        private void EvaluateAnchorCandidate(
            Collider col,
            Vector3 cleatWorld,
            Vector3 cleatLocal,
            ref float bestDistSq,
            ref Vector3 bestLocalCleat,
            ref Vector3 bestDockPoint,
            ref bool found)
        {
            Vector3 closest = col.ClosestPointOnBounds(cleatWorld);
            float dSq = (closest - cleatWorld).sqrMagnitude;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestLocalCleat = cleatLocal;
                bestDockPoint = closest;
                found = true;
            }
        }

        private void SearchOverlapAnchors(
            Vector3 cleatWorld,
            Vector3 cleatLocal,
            ref float bestDistSq,
            ref Vector3 bestLocalCleat,
            ref Vector3 bestDockPoint,
            ref bool found)
        {
            Collider[] overlaps = Physics.OverlapSphere(cleatWorld, BoatConstants.MoorSearchRadius);
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider col = overlaps[i];
                if (col == null || col.isTrigger) continue;
                if (motorSync.BoatTransform != null && (col.transform == motorSync.BoatTransform || col.transform.IsChildOf(motorSync.BoatTransform))) continue;

                string rootN = col.transform.root != null ? col.transform.root.name : string.Empty;
                if (rootN.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase) ||
                    rootN.StartsWith("RemotePuppet", StringComparison.OrdinalIgnoreCase) ||
                    BoatHelpers.ContainsIgnoreCase(col.gameObject.name, "water") ||
                    BoatHelpers.ContainsIgnoreCase(col.gameObject.name, "terrain"))
                {
                    continue;
                }

                // Предпочитаем статические объекты причала на уровне борта или чуть выше
                Vector3 closest = col.ClosestPointOnBounds(cleatWorld);
                if (Mathf.Abs(closest.y - cleatWorld.y) > 2.2f) continue;

                float dSq = (closest - cleatWorld).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestLocalCleat = cleatLocal;
                    bestDockPoint = closest;
                    found = true;
                }
            }
        }

        private void ApplyMooring(Vector3 localAnchor, Vector3 worldAnchor, float length)
        {
            isMoored = true;
            boatLocalAnchor = localAnchor;
            worldDockAnchor = worldAnchor;
            ropeLength = Mathf.Clamp(length, 1.5f, 8.0f);

            SyncJointOwnershipState();
            EnsureVisualRope();
            UpdateRopeVisual();
        }

        private void DetachMooring(BoatMooringAction reason, bool notifyRemote)
        {
            if (!isMoored && mooringJoint == null && ropeVisualObj == null) return;

            isMoored = false;
            DestroyPhysicsJoint();
            DestroyVisualRope();

            if (notifyRemote)
            {
                SendMooringAction(reason);
            }

            if (reason == BoatMooringAction.Snap)
            {
                ctx?.Log?.Invoke("[Boat/Mooring] Швартовочный трос лодки лопнул от натяжения!");
            }
            else
            {
                ctx?.Log?.Invoke("[Boat/Mooring] Лодка отвязана от причала");
            }
        }

        private void SyncJointOwnershipState()
        {
            Rigidbody boatRb = motorSync.BoatRigidbody;
            if (boatRb == null || !isMoored) return;

            bool shouldSimulateJoint = !boatRb.isKinematic;
            if (shouldSimulateJoint && mooringJoint == null)
            {
                mooringJoint = motorSync.BoatObject.AddComponent<ConfigurableJoint>();
                mooringJoint.autoConfigureConnectedAnchor = false;
                mooringJoint.anchor = boatLocalAnchor;
                mooringJoint.connectedBody = null;
                mooringJoint.connectedAnchor = worldDockAnchor;
                mooringJoint.xMotion = ConfigurableJointMotion.Limited;
                mooringJoint.yMotion = ConfigurableJointMotion.Limited;
                mooringJoint.zMotion = ConfigurableJointMotion.Limited;
                mooringJoint.linearLimit = new SoftJointLimit { limit = ropeLength };
                mooringJoint.linearLimitSpring = new SoftJointLimitSpring { spring = 1800f, damper = 180f };
                mooringJoint.angularXMotion = ConfigurableJointMotion.Free;
                mooringJoint.angularYMotion = ConfigurableJointMotion.Free;
                mooringJoint.angularZMotion = ConfigurableJointMotion.Free;
                mooringJoint.enableCollision = true;
            }
            else if (!shouldSimulateJoint && mooringJoint != null)
            {
                DestroyPhysicsJoint();
            }
        }

        private void CheckMooringTensionSnap()
        {
            if (motorSync.BoatTransform == null || motorSync.BoatRigidbody == null) return;
            if (motorSync.BoatRigidbody.isKinematic) return;

            Vector3 cleatWorld = motorSync.BoatTransform.TransformPoint(boatLocalAnchor);
            float dist = Vector3.Distance(cleatWorld, worldDockAnchor);
            if (dist > ropeLength + BoatConstants.MoorSnapExtraDistance)
            {
                DetachMooring(BoatMooringAction.Snap, notifyRemote: true);
            }
        }

        private void EnsureVisualRope()
        {
            if (ropeVisualObj != null) return;

            ropeVisualObj = new GameObject("OSC_BoatMooringRope");
            ropeRenderer = ropeVisualObj.AddComponent<LineRenderer>();
            ropeRenderer.SetVertexCount(RopeSegments);
            ropeRenderer.SetWidth(0.035f, 0.035f);
            ropeRenderer.useWorldSpace = true;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Diffuse");
            if (shader != null)
            {
                ropeRenderer.material = new Material(shader);
                ropeRenderer.material.color = new Color(0.68f, 0.56f, 0.38f, 1f);
            }
            ropeRenderer.SetColors(new Color(0.68f, 0.56f, 0.38f, 1f), new Color(0.55f, 0.45f, 0.30f, 1f));
        }

        private void UpdateRopeVisual()
        {
            if (ropeRenderer == null || motorSync.BoatTransform == null) return;

            Vector3 start = motorSync.BoatTransform.TransformPoint(boatLocalAnchor);
            Vector3 end = worldDockAnchor;
            float dist = Vector3.Distance(start, end);
            float slack = Mathf.Max(0f, ropeLength - dist);
            float sag = Mathf.Clamp(slack * 0.45f, 0.05f, 0.65f);

            for (int i = 0; i < RopeSegments; i++)
            {
                float t = i / (float)(RopeSegments - 1);
                Vector3 pt = Vector3.Lerp(start, end, t);
                pt.y -= Mathf.Sin(t * Mathf.PI) * sag;
                ropeRenderer.SetPosition(i, pt);
            }
        }

        private void DestroyPhysicsJoint()
        {
            if (mooringJoint != null)
            {
                UnityEngine.Object.Destroy(mooringJoint);
                mooringJoint = null;
            }
        }

        private void DestroyVisualRope()
        {
            if (ropeVisualObj != null)
            {
                UnityEngine.Object.Destroy(ropeVisualObj);
                ropeVisualObj = null;
            }
            ropeRenderer = null;
        }

        private void SendMooringAction(BoatMooringAction action)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.BoatMooringSync))
            {
                p.WriteByte((byte)action);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                if (action == BoatMooringAction.Attach)
                {
                    p.WriteBoolean(isMoored);
                    p.WriteVector3(boatLocalAnchor);
                    p.WriteVector3(worldDockAnchor);
                    p.WriteSingle(ropeLength);
                }
                ctx.Transport.Send(p);
            }
        }
    }
}
