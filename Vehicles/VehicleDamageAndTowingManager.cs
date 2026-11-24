using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Вспомогательный компонент для перехвата сетевых столкновений и повреждений кузова
    /// </summary>
    public class VehicleCollisionReporter : MonoBehaviour
    {
        private string vehicleName;
        private float lastReportTime = 0f;

        public void Initialize(string name)
        {
            vehicleName = name;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.relativeVelocity.magnitude < 2.5f) return;
            if (Time.time - lastReportTime < 0.25f) return; // Защита от спама контактами в одном фрейме

            lastReportTime = Time.time;
            Vector3 hitPoint = collision.contacts.Length > 0 ? collision.contacts[0].point : transform.position;
            VehicleDamageAndTowingManager.Instance?.SendCrashImpact(vehicleName, hitPoint, collision.relativeVelocity);
        }
    }

    /// <summary>
    /// Менеджер сетевой синхронизации повреждений кузова (CarDamage, Deformable),
    /// буксировочного троса (TowingRope) и прицепа трактора (Flatbed trailer).
    /// </summary>
    public class VehicleDamageAndTowingManager : MonoBehaviour
    {
        public static VehicleDamageAndTowingManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Буксировочный трос
        private PlayMakerFSM ropeFsm;
        private bool isRopeHooked = false;
        public bool IsRopeHooked => isRopeHooked;
        private bool isSuppressingRope = false;

        // Повреждения автомобилей
        private readonly List<CarDamage> trackedCarDamages = new List<CarDamage>();
        private readonly List<Deformable> trackedDeformables = new List<Deformable>();

        // Прицеп трактора (Flatbed)
        private PlayMakerFSM flatbedTipFsm;
        private PlayMakerFSM flatbedHitchFsm;

        // Жилой автоприцеп (CARAVAN)
        private PlayMakerFSM caravanDetachFsm;
        private bool isSuppressingCaravan = false;

        // Шланги Gifu
        private PlayMakerFSM activateHoseFsm;
        private PlayMakerFSM hoseCouplerFsm;
        private bool isSuppressingHose = false;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanVehiclesAndRope();
            OnLog?.Invoke("[Транспорт] Менеджер буксировки, прицепов и повреждений инициализирован");
        }

        public void Reset()
        {
            trackedCarDamages.Clear();
            trackedDeformables.Clear();
            ropeFsm = null;
            isRopeHooked = false;
            isSuppressingRope = false;
            flatbedTipFsm = null;
            flatbedHitchFsm = null;
            caravanDetachFsm = null;
            isSuppressingCaravan = false;
            activateHoseFsm = null;
            hoseCouplerFsm = null;
            isSuppressingHose = false;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void ScanVehiclesAndRope()
        {
            try
            {
                // 1. Поиск FSM буксировочного троса
                var ropeGo = GameObject.Find("FPSCamera/TowingRope/Rope") ?? GameObject.Find("TowingRope/Rope");
                if (ropeGo != null)
                {
                    ropeFsm = PlayMakerUtilities.FindFsmOnGameObject(ropeGo, "Status");
                    if (ropeFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(ropeFsm, "MP_SYNC_ATTACH", "Set joint");
                        PlayMakerUtilities.AddSafeGlobalTransition(ropeFsm, "MP_SYNC_DETACH", "Destroy joint");
                        PlayMakerUtilities.AddSafeGlobalTransition(ropeFsm, "MP_SYNC_SNAP", "Snap off");

                        ropeFsm.InsertAction("Set joint", () =>
                        {
                            if (isSuppressingRope) return;
                            var h1 = ropeFsm.FsmVariables.FindFsmGameObject("Hook1");
                            var h2 = ropeFsm.FsmVariables.FindFsmGameObject("Hook2");
                            string p1 = (h1 != null && h1.Value != null) ? GetHierarchyPath(h1.Value.transform) : string.Empty;
                            string p2 = (h2 != null && h2.Value != null) ? GetHierarchyPath(h2.Value.transform) : string.Empty;
                            isRopeHooked = true;
                            SendRopePacket(1, p1, p2);
                            OnLog?.Invoke($"[Трос] Трос зацеплен локально: {h1?.Value?.name} <-> {h2?.Value?.name}");
                        }, -1, false);

                        ropeFsm.InsertAction("Destroy joint", () =>
                        {
                            if (isSuppressingRope) return;
                            isRopeHooked = false;
                            SendRopePacket(0, string.Empty, string.Empty);
                            OnLog?.Invoke("[Трос] Трос отцеплен локально");
                        }, -1, false);

                        ropeFsm.InsertAction("Snap off", () =>
                        {
                            if (isSuppressingRope) return;
                            isRopeHooked = false;
                            SendRopePacket(2, string.Empty, string.Empty);
                            OnLog?.Invoke("[Трос] Трос оборван!");
                        }, -1, false);
                    }
                }

                // 2. Сбор всех компонентов CarDamage и Deformable в мире
                trackedCarDamages.Clear();
                var damages = UnityEngine.Object.FindObjectsOfType<CarDamage>();
                if (damages != null)
                {
                    trackedCarDamages.AddRange(damages);
                    for (int i = 0; i < damages.Length; i++)
                    {
                        var cd = damages[i];
                        if (cd != null && cd.GetComponent<VehicleCollisionReporter>() == null)
                        {
                            var rep = cd.gameObject.AddComponent<VehicleCollisionReporter>();
                            rep.Initialize(cd.gameObject.name);
                        }
                    }
                }

                trackedDeformables.Clear();
                var defs = UnityEngine.Object.FindObjectsOfType<Deformable>();
                if (defs != null)
                {
                    trackedDeformables.AddRange(defs);
                }

                // 3. FSM гидравлики и сцепки прицепа FLATBED
                var flatbedGo = GameObject.Find("FLATBED");
                if (flatbedGo != null)
                {
                    flatbedTipFsm = PlayMakerUtilities.FindFsmOnGameObject(flatbedGo, "Tip") ??
                                    PlayMakerUtilities.FindFsmOnGameObject(flatbedGo, "Hydraulics");
                    flatbedHitchFsm = PlayMakerUtilities.FindFsmOnGameObject(flatbedGo, "Hitch") ??
                                      PlayMakerUtilities.FindFsmOnGameObject(flatbedGo, "Attach");
                }

                // 4. Жилой кемпинг-прицеп CARAVAN
                var caravanGo = GameObject.Find("CARAVAN") ?? GameObject.Find("Caravan");
                if (caravanGo != null)
                {
                    caravanDetachFsm = PlayMakerUtilities.FindFsmOnGameObject(caravanGo, "Detach");
                    if (caravanDetachFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(caravanDetachFsm, "MP_SYNC_DETACH", "Detach");
                        caravanDetachFsm.InsertAction("Detach", () =>
                        {
                            if (isSuppressingCaravan) return;
                            SendCaravanTrailerSync(0); // 0 = Detach
                            OnLog?.Invoke("[Прицеп] Жилой фургон CARAVAN сорван / отцеплен!");
                        }, 0, false);
                    }
                }

                // 5. Шланги ассенизатора Gifu (стойка и муфта насоса)
                var gifuGo = GameObject.Find("GIFU(750/450psi)") ?? GameObject.Find("Gifu");
                if (gifuGo != null)
                {
                    var actHoseTr = PlayMakerUtilities.FindChildRecursive(gifuGo.transform, "ActivateHose");
                    if (actHoseTr != null)
                    {
                        activateHoseFsm = PlayMakerUtilities.FindFsmOnGameObject(actHoseTr.gameObject, "Use");
                        if (activateHoseFsm != null)
                        {
                            PlayMakerUtilities.AddSafeGlobalTransition(activateHoseFsm, "MP_SYNC_REMOVE", "Remove part");
                            PlayMakerUtilities.AddSafeGlobalTransition(activateHoseFsm, "MP_SYNC_ASSEMBLE", "Put hose back");

                            activateHoseFsm.InsertAction("Remove part", () =>
                            {
                                if (isSuppressingHose) return;
                                SendGifuHoseSync(1);
                                OnLog?.Invoke("[Gifu] Шланг откачки вынут из стойки");
                            }, 0, false);

                            activateHoseFsm.InsertAction("Put hose back", () =>
                            {
                                if (isSuppressingHose) return;
                                SendGifuHoseSync(2);
                                OnLog?.Invoke("[Gifu] Шланг откачки убран в стойку");
                            }, 0, false);
                        }
                    }

                    var couplerTr = PlayMakerUtilities.FindChildRecursive(gifuGo.transform, "hose coupler(xxxxx)");
                    if (couplerTr != null)
                    {
                        hoseCouplerFsm = PlayMakerUtilities.FindFsmOnGameObject(couplerTr.gameObject, "Attach");
                        if (hoseCouplerFsm != null)
                        {
                            PlayMakerUtilities.AddSafeGlobalTransition(hoseCouplerFsm, "MP_SYNC_ATTACH", "Attach");
                            PlayMakerUtilities.AddSafeGlobalTransition(hoseCouplerFsm, "MP_SYNC_DETACH", "Hose off");

                            hoseCouplerFsm.InsertAction("Attach", () =>
                            {
                                if (isSuppressingHose) return;
                                SendGifuHoseSync(3);
                                OnLog?.Invoke("[Gifu] Муфта шланга зафиксирована на штуцере насоса");
                            }, 0, false);

                            hoseCouplerFsm.InsertAction("Hose off", () =>
                            {
                                if (isSuppressingHose) return;
                                SendGifuHoseSync(4);
                                OnLog?.Invoke("[Gifu] Муфта шланга отсоединена от штуцера насоса");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Транспорт] Ошибка сканирования троса и повреждений: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;
        }

        private void SendRopePacket(byte action, string hook1, string hook2)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TowingRopeSync))
            {
                packet.WriteByte(action); // 1 = Attach, 0 = Detach, 2 = Snap
                packet.WriteString(hook1);
                packet.WriteString(hook2);
                transport.Send(packet);
            }
        }

        public void SendCrashImpact(string vehicleName, Vector3 hitPoint, Vector3 impactVelocity)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleCrashSync))
            {
                packet.WriteString(vehicleName);
                packet.WriteSingle(hitPoint.x);
                packet.WriteSingle(hitPoint.y);
                packet.WriteSingle(hitPoint.z);
                packet.WriteSingle(impactVelocity.x);
                packet.WriteSingle(impactVelocity.y);
                packet.WriteSingle(impactVelocity.z);
                transport.Send(packet);
            }
        }

        public void SendVehicleRepair(string vehicleName)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleRepairSync))
            {
                packet.WriteString(vehicleName);
                transport.Send(packet);
            }
        }

        public void SendCaravanTrailerSync(byte action)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CaravanTrailerSync))
            {
                packet.WriteByte(action);
                transport.Send(packet);
            }
        }

        public void SendGifuHoseSync(byte action)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.GifuHoseSync))
            {
                packet.WriteByte(action);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.TowingRopeSync:
                    HandleRopeSync(reader);
                    break;

                case PacketType.VehicleCrashSync:
                    HandleCrashSync(reader);
                    break;

                case PacketType.VehicleRepairSync:
                    HandleRepairSync(reader);
                    break;

                case PacketType.CaravanTrailerSync:
                    HandleCaravanSync(reader);
                    break;

                case PacketType.GifuHoseSync:
                    HandleGifuHoseSync(reader);
                    break;
            }
        }

        private void HandleRopeSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            string h1Path = reader.ReadString();
            string h2Path = reader.ReadString();

            if (ropeFsm == null)
            {
                ScanVehiclesAndRope();
                if (ropeFsm == null) return;
            }

            isSuppressingRope = true;
            try
            {
                if (action == 1) // Attach
                {
                    var h1 = GameObject.Find(h1Path);
                    var h2 = GameObject.Find(h2Path);

                    var hook1Var = ropeFsm.FsmVariables.FindFsmGameObject("Hook1");
                    var hook2Var = ropeFsm.FsmVariables.FindFsmGameObject("Hook2");
                    var attachedVar = ropeFsm.FsmVariables.FindFsmBool("Attached");

                    if (hook1Var != null && h1 != null) hook1Var.Value = h1;
                    if (hook2Var != null && h2 != null) hook2Var.Value = h2;
                    if (attachedVar != null) attachedVar.Value = true;

                    isRopeHooked = true;
                    ropeFsm.SendEvent("MP_SYNC_ATTACH");
                    OnLog?.Invoke($"[Трос] Сетевая сцепка троса: {h1?.name} <-> {h2?.name}");
                }
                else if (action == 0) // Detach
                {
                    var attachedVar = ropeFsm.FsmVariables.FindFsmBool("Attached");
                    if (attachedVar != null) attachedVar.Value = false;
                    isRopeHooked = false;
                    ropeFsm.SendEvent("MP_SYNC_DETACH");
                }
                else if (action == 2) // Snap off
                {
                    var attachedVar = ropeFsm.FsmVariables.FindFsmBool("Attached");
                    if (attachedVar != null) attachedVar.Value = false;
                    isRopeHooked = false;
                    ropeFsm.SendEvent("MP_SYNC_SNAP");
                    OnLog?.Invoke("[Трос] Буксировочный трос оборван!");
                }
            }
            finally
            {
                isSuppressingRope = false;
            }
        }

        private void HandleCrashSync(PacketReader reader)
        {
            string vehicleName = reader.ReadString();
            Vector3 hitPoint = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 vel = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            for (int i = 0; i < trackedCarDamages.Count; i++)
            {
                var cd = trackedCarDamages[i];
                if (cd != null && cd.gameObject.name.Equals(vehicleName, StringComparison.OrdinalIgnoreCase))
                {
                    cd.repair = false;
                    break;
                }
            }

            for (int i = 0; i < trackedDeformables.Count; i++)
            {
                var def = trackedDeformables[i];
                if (def != null && def.gameObject.name.Equals(vehicleName, StringComparison.OrdinalIgnoreCase))
                {
                    def.Repair(0f, hitPoint, 0.6f);
                    break;
                }
            }
        }

        private void HandleRepairSync(PacketReader reader)
        {
            string vehicleName = reader.ReadString();

            for (int i = 0; i < trackedCarDamages.Count; i++)
            {
                var cd = trackedCarDamages[i];
                if (cd != null && (string.IsNullOrEmpty(vehicleName) || cd.gameObject.name.IndexOf(vehicleName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    cd.repair = true;
                }
            }

            for (int i = 0; i < trackedDeformables.Count; i++)
            {
                var def = trackedDeformables[i];
                if (def != null && (string.IsNullOrEmpty(vehicleName) || def.gameObject.name.IndexOf(vehicleName, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    def.Repair(1f);
                }
            }

            OnLog?.Invoke($"[Транспорт] Кузов {vehicleName} полностью выправлен и отремонтирован!");
        }

        private void HandleCaravanSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            if (caravanDetachFsm == null)
            {
                ScanVehiclesAndRope();
                if (caravanDetachFsm == null) return;
            }

            isSuppressingCaravan = true;
            try
            {
                if (action == 0) // Detach
                {
                    caravanDetachFsm.SendEvent("MP_SYNC_DETACH");
                    OnLog?.Invoke("[Прицеп] Сетевой срыв жилого фургона CARAVAN!");
                }
            }
            finally
            {
                isSuppressingCaravan = false;
            }
        }

        private void HandleGifuHoseSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            if (activateHoseFsm == null || hoseCouplerFsm == null)
            {
                ScanVehiclesAndRope();
            }

            isSuppressingHose = true;
            try
            {
                switch (action)
                {
                    case 1:
                        activateHoseFsm?.SendEvent("MP_SYNC_REMOVE");
                        OnLog?.Invoke("[Gifu] Шланг откачки вынут из стойки");
                        break;
                    case 2:
                        activateHoseFsm?.SendEvent("MP_SYNC_ASSEMBLE");
                        OnLog?.Invoke("[Gifu] Шланг откачки убран в стойку");
                        break;
                    case 3:
                        hoseCouplerFsm?.SendEvent("MP_SYNC_ATTACH");
                        OnLog?.Invoke("[Gifu] Муфта шланга зафиксирована на штуцере");
                        break;
                    case 4:
                        hoseCouplerFsm?.SendEvent("MP_SYNC_DETACH");
                        OnLog?.Invoke("[Gifu] Муфта шланга отсоединена от штуцера");
                        break;
                }
            }
            finally
            {
                isSuppressingHose = false;
            }
        }

        private string GetHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;
            string path = current.name;
            while (current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }
    }
}
