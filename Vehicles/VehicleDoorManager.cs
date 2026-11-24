using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    public class TrackedVehicleDoor
    {
        public int Hash;
        public PlayMakerFSM Fsm;
        public FsmBool DoorOpenVar;
        public GameObject DoorObject;
        public int Axis = 1; // 0=X, 1=Y, 2=Z
        public float ClosedAngle = 0f;
        public bool IsHayosikoSidedoor = false;
        public bool IsHoodOrBoot = false;
        public bool SuppressEcho = false;
        public float LastSentAngle = -999f;
        public float TargetAngle = -999f; // Для плавной сетевой интерполяции на 60 FPS

        public int OwnerId = 0; // По умолчанию хост (0)
    }

    /// <summary>
    /// Сетевой менеджер интерактивных дверей, капотов и багажников всех автомобилей
    /// (Satsuma, Hayosiko, Kekmet, Gifu, Jonnez, Ferndale, Ruscko).
    /// 
    /// Симметричная P2P-модель: любой игрок может мгновенно открывать, закрывать
    /// и двигать двери авто с полной синхронизацией физических углов.
    /// Реализовано по архитектуре WreckMP (FsmVehicleDoor.cs + FsmNetVehicle.cs).
    /// </summary>
    public class VehicleDoorManager
    {
        public static VehicleDoorManager Instance { get; private set; }

        private sealed class DoorFixedUpdater : MonoBehaviour
        {
            public VehicleDoorManager Manager;

            private void FixedUpdate()
            {
                if (Manager != null)
                {
                    Manager.OnFixedUpdate();
                }
            }
        }

        private readonly NetworkTransport transport;
        private readonly Dictionary<int, TrackedVehicleDoor> trackedDoors = new Dictionary<int, TrackedVehicleDoor>();
        private readonly List<TrackedVehicleDoor> doorList = new List<TrackedVehicleDoor>();

        private DoorFixedUpdater fixedUpdater;
        private bool isInitialized = false;
        private bool satsumaDoorsRegistered = false;
        private bool allVehiclesRegistered = false;
        private readonly HashSet<string> registeredVehicles = new HashSet<string>();
        private float scanTimer = 5f;
        private const float ScanInterval = 5f;

        public event Action<string> OnLog;
        public int RegisteredDoorsCount => trackedDoors.Count;

        public void Reset()
        {
            isInitialized = false;
            satsumaDoorsRegistered = false;
            allVehiclesRegistered = false;
            registeredVehicles.Clear();
            for (int i = 0; i < doorList.Count; i++)
            {
                doorList[i].TargetAngle = -999f;
                doorList[i].LastSentAngle = -999f;
                doorList[i].OwnerId = 0;
            }
            trackedDoors.Clear();
            doorList.Clear();

            if (fixedUpdater != null)
            {
                if (fixedUpdater.gameObject != null)
                {
                    UnityEngine.Object.Destroy(fixedUpdater.gameObject);
                }
                fixedUpdater = null;
            }
        }

        public VehicleDoorManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        private void EnsureFixedUpdater()
        {
            if (fixedUpdater != null) return;

            var existing = GameObject.Find("OurSummerCar_VehicleDoorFixedUpdater");
            if (existing != null)
            {
                fixedUpdater = existing.GetComponent<DoorFixedUpdater>() ?? existing.AddComponent<DoorFixedUpdater>();
            }
            else
            {
                var go = new GameObject("OurSummerCar_VehicleDoorFixedUpdater");
                GameObject.DontDestroyOnLoad(go);
                fixedUpdater = go.AddComponent<DoorFixedUpdater>();
            }
            fixedUpdater.Manager = this;
        }

        public void InitializeVehicleDoors()
        {
            EnsureFixedUpdater();
            ScanAndRegisterDoors();
            isInitialized = true;
            OnLog?.Invoke($"[Транспорт] Подключено дверей и капотов: {trackedDoors.Count}");
        }

        public void Update()
        {
            if (!isInitialized || !transport.IsRunning) return;

            EnsureFixedUpdater();

            // Повторная попытка сканирования только если не все машины успели загрузиться при старте
            if (!allVehiclesRegistered)
            {
                scanTimer -= Time.deltaTime;
                if (scanTimer <= 0f)
                {
                    scanTimer = ScanInterval;
                    ScanAndRegisterDoors();
                }
            }
        }

        internal void OnFixedUpdate()
        {
            if (!isInitialized || transport == null || !transport.IsRunning) return;

            SyncOpenDoorAngles();
            ApplyRemoteDoorAngles();
        }

        private void ScanAndRegisterDoors()
        {
            try
            {
                // 1. Детали кузова Сацумы (в базе данных деталей и на автомобиле)
                if (!satsumaDoorsRegistered)
                {
                    RegisterSatsumaDoor("Database/DatabaseBody/Door_Left");
                    RegisterSatsumaDoor("Database/DatabaseBody/Door_Right");
                    RegisterSatsumaDoor("Database/DatabaseBody/Bootlid/Handles");
                    RegisterSatsumaDoor("Database/DatabaseBody/Hood");
                    satsumaDoorsRegistered = true;
                }

                // 2. Поиск по всем автомобилям на сцене
                string[] vehicleNames = new string[]
                {
                    "SATSUMA(557kg, 248)",
                    "HAYOSIKO(1500kg, 250)",
                    "KEKMET(350-400kg, 1972)",
                    "RCO_RUSCKO12(270)",
                    "FLATBED",
                    "FERNDALE(360-400kg, 1973)",
                    "JONNEZ ES(70kg)"
                };

                for (int v = 0; v < vehicleNames.Length; v++)
                {
                    string vName = vehicleNames[v];
                    if (registeredVehicles.Contains(vName)) continue;

                    GameObject veh = GameObject.Find(vName);
                    if (veh == null) continue;

                    registeredVehicles.Add(vName);

                    // Специальная сдвижная и задняя двери Хайосико
                    if (vName.StartsWith("HAYOSIKO"))
                    {
                        Transform sideDoor = veh.transform.Find("SideDoor/door/Collider");
                        if (sideDoor != null)
                        {
                            var sideFsm = sideDoor.GetComponent<PlayMakerFSM>();
                            if (sideFsm != null)
                            {
                                RegisterDoorFsm(sideFsm, true);
                            }
                        }

                        Transform backDoor = veh.transform.Find("RearDoor/doorear") ??
                                             veh.transform.Find("RearDoor") ??
                                             veh.transform.Find("doorear") ??
                                             PlayMakerUtilities.FindChildRecursive(veh.transform, "doorear") ??
                                             PlayMakerUtilities.FindChildRecursive(veh.transform, "RearDoor") ??
                                             veh.transform.Find("Backdoor/Collider") ??
                                             veh.transform.Find("Backdoor/door/Collider") ??
                                             PlayMakerUtilities.FindChildRecursive(veh.transform, "Backdoor");
                        if (backDoor != null)
                        {
                            var backFsm = backDoor.GetComponent<PlayMakerFSM>() ?? backDoor.GetComponentInChildren<PlayMakerFSM>();
                            if (backFsm != null && backFsm.FsmName == "Use")
                            {
                                RegisterDoorFsm(backFsm, false);
                            }
                        }
                    }

                    // Ручки и двери остальных автомобилей
                    var fsms = veh.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        var fsm = fsms[i];
                        if (fsm == null || fsm.FsmName != "Use") continue;

                        string tName = fsm.transform.name;
                        string tNameLower = tName.ToLower();

                        if (tName == "Handle" || tName == "Handles" || tNameLower.Contains("door") || tNameLower.Contains("doorear") || tNameLower.Contains("bootlid") || tNameLower.Contains("hood") || tNameLower.Contains("backdoor") || tNameLower.Contains("reardoor"))
                        {
                            RegisterDoorFsm(fsm, false);
                        }
                    }
                }

                if (registeredVehicles.Count >= vehicleNames.Length)
                {
                    allVehiclesRegistered = true;
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleDoorManager] Ошибка сканирования: {ex.Message}");
            }
        }

        private void RegisterSatsumaDoor(string dbPath)
        {
            GameObject obj = GameObject.Find(dbPath);
            if (obj == null)
            {
                string shortName = dbPath.Substring(dbPath.LastIndexOf('/') + 1);
                var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    if (allGo[i].name == shortName)
                    {
                        obj = allGo[i];
                        break;
                    }
                }
            }

            if (obj != null)
            {
                var fsm = obj.GetComponent<PlayMakerFSM>();
                if (fsm != null && fsm.FsmName == "Use")
                {
                    RegisterDoorFsm(fsm, false);
                }
            }
        }

        private void RegisterDoorFsm(PlayMakerFSM fsm, bool isHayosikoSidedoor)
        {
            if (fsm == null) return;
            int hash = SceneHasher.GetHash(fsm.gameObject);
            if (trackedDoors.ContainsKey(hash)) return;

            fsm.Initialize();

            var tracked = new TrackedVehicleDoor
            {
                Hash = hash,
                Fsm = fsm,
                IsHayosikoSidedoor = isHayosikoSidedoor
            };

            if (isHayosikoSidedoor)
            {
                SetupSidedoor(tracked);
            }
            else
            {
                SetupStandardDoor(tracked);
            }

            trackedDoors[hash] = tracked;
            doorList.Add(tracked);
        }

        private void SetupSidedoor(TrackedVehicleDoor tracked)
        {
            PlayMakerFSM fsm = tracked.Fsm;
            tracked.DoorOpenVar = fsm.FsmVariables.FindFsmBool("Open");
            tracked.DoorObject = fsm.gameObject;
            tracked.IsHayosikoSidedoor = true;

            FsmEvent mpOpen = fsm.AddEvent("MP_OPEN");
            fsm.AddGlobalTransition(mpOpen, "Open door");

            FsmEvent mpClose = fsm.AddEvent("MP_CLOSE");
            fsm.AddGlobalTransition(mpClose, "Close door");

            fsm.InsertAction("Open door", () => OnLocalDoorAction(tracked, true), 0, false);
            fsm.InsertAction("Close door", () => OnLocalDoorAction(tracked, false), 0, false);
            fsm.Initialize();
        }

        private void SetupStandardDoor(TrackedVehicleDoor tracked)
        {
            PlayMakerFSM fsm = tracked.Fsm;
            tracked.DoorOpenVar = fsm.FsmVariables.FindFsmBool("Open");

            var doorGoVar = fsm.FsmVariables.FindFsmGameObject("Door");
            var bootVar = fsm.FsmVariables.FindFsmGameObject("Bootlid");

            if (doorGoVar != null && doorGoVar.Value != null)
            {
                tracked.DoorObject = doorGoVar.Value;
            }
            else if (bootVar != null && bootVar.Value != null)
            {
                tracked.DoorObject = bootVar.Value;
            }
            else
            {
                tracked.DoorObject = fsm.gameObject;
            }

            tracked.IsHoodOrBoot = fsm.HasState("Open hood") || (bootVar != null && bootVar.Value != null);

            string openState = tracked.IsHoodOrBoot
                ? (fsm.HasState("Open hood") ? "Open hood" : (fsm.HasState("Open door") ? "Open door" : "Open"))
                : (fsm.HasState("Open door") ? "Open door" : (fsm.HasState("Open") ? "Open" : "Open hood"));

            string closeState;
            if (tracked.IsHoodOrBoot)
            {
                closeState = fsm.HasState("Drop") ? "Drop"
                    : (fsm.HasState("State 2") ? "State 2"
                    : (fsm.HasState("Sound") ? "Sound" : "Close"));
            }
            else
            {
                closeState = fsm.HasState("Sound") ? "Sound"
                    : (fsm.HasState("Close door") ? "Close door"
                    : (fsm.HasState("Shut") ? "Shut" : "Close"));
            }

            // Определение оси вращения через GetRotation действие PlayMaker
            FsmState stOpen = fsm.GetState(openState);
            if (stOpen != null && stOpen.Actions != null)
            {
                for (int a = 0; a < stOpen.Actions.Length; a++)
                {
                    if (stOpen.Actions[a] is GetRotation getRot)
                    {
                        if (getRot.xAngle != null && !getRot.xAngle.IsNone) tracked.Axis = 0;
                        else if (getRot.yAngle != null && !getRot.yAngle.IsNone) tracked.Axis = 1;
                        else if (getRot.zAngle != null && !getRot.zAngle.IsNone) tracked.Axis = 2;
                        break;
                    }
                }
            }

            // Сохраняем исходный угол закрытого состояния (например 270° для Hayosiko doorl)
            tracked.ClosedAngle = GetDoorAngle(tracked);

            FsmEvent mpOpen = fsm.AddEvent("MP_OPEN");
            fsm.AddGlobalTransition(mpOpen, openState);

            FsmEvent mpClose = fsm.AddEvent("MP_CLOSE");
            fsm.AddGlobalTransition(mpClose, closeState);

            fsm.InsertAction(openState, () => OnLocalDoorAction(tracked, true), 0, false);
            fsm.InsertAction(closeState, () => OnLocalDoorAction(tracked, false), 0, false);

            if (!tracked.IsHoodOrBoot)
            {
                // Переход на владение при удержании/тяге двери
                if (fsm.HasState("Open door 2"))
                {
                    fsm.InsertAction("Open door 2", () => RequestDoorOwnership(tracked), 0, false);
                }
                else if (fsm.HasState("Open door 3"))
                {
                    fsm.InsertAction("Open door 3", () => RequestDoorOwnership(tracked), 0, false);
                }
            }

            fsm.Initialize();
        }

        private static float GetDoorAngle(TrackedVehicleDoor d)
        {
            if (d == null || d.DoorObject == null) return 0f;
            Vector3 euler = d.DoorObject.transform.localEulerAngles;
            return d.Axis == 0 ? euler.x : (d.Axis == 1 ? euler.y : euler.z);
        }

        private static void SetDoorAngle(TrackedVehicleDoor d, float angle)
        {
            if (d == null || d.DoorObject == null) return;
            Vector3 euler = d.DoorObject.transform.localEulerAngles;
            if (d.Axis == 0) euler.x = angle;
            else if (d.Axis == 1) euler.y = angle;
            else euler.z = angle;
            d.DoorObject.transform.localEulerAngles = euler;
        }

        private int MyPlayerId => (transport != null && transport.IsServer) ? 0 : 1;

        public void RequestDoorOwnership(TrackedVehicleDoor door)
        {
            if (door == null || transport == null || !transport.IsRunning) return;
            int myId = MyPlayerId;
            if (door.OwnerId == myId) return;

            door.OwnerId = myId;
            SendDoorOwnership(door.Hash, myId);
        }

        private void SendDoorOwnership(int hash, int ownerId)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleDoorOwnership))
            {
                packet.WriteInt32(hash);
                packet.WriteInt32(ownerId);
                transport.Send(packet);
            }
        }

        private void OnLocalDoorAction(TrackedVehicleDoor door, bool open)
        {
            if (door.SuppressEcho)
            {
                door.SuppressEcho = false;
                return;
            }

            if (!transport.IsRunning) return;

            door.LastSentAngle = -999f;
            if (!open)
            {
                door.TargetAngle = -999f;
            }

            RequestDoorOwnership(door);
            SendDoorToggle(door.Hash, open);
            OnLog?.Invoke($"[Транспорт] Дверь/капот {door.Fsm.gameObject.name} -> {(open ? "Открыта" : "Закрыта")}");
        }

        private void SyncOpenDoorAngles()
        {
            if (!transport.IsRunning) return;

            int myId = MyPlayerId;

            for (int i = 0; i < doorList.Count; i++)
            {
                var d = doorList[i];
                if (d.DoorObject == null || d.DoorOpenVar == null || !d.DoorOpenVar.Value) continue;

                // 1. Исключаем сдвижную дверь фургона Hayosiko: она двигается по направляющим, а не вращается
                if (d.IsHayosikoSidedoor) continue;

                // 2. Отправлять углы двери имеет право ТОЛЬКО игрок-владелец (OwnerId == myId)
                if (d.OwnerId != myId) continue;

                float currentAngle = GetDoorAngle(d);

                if (Mathf.Abs(Mathf.DeltaAngle(currentAngle, d.LastSentAngle)) > 0.25f)
                {
                    d.LastSentAngle = currentAngle;
                    SendDoorRotation(d.Hash, currentAngle);
                }
            }
        }

        private void SendDoorToggle(int hash, bool open)
        {
            using (var packet = new PacketWriter(PacketType.VehicleDoorToggle))
            {
                packet.WriteInt32(hash);
                packet.WriteBoolean(open);
                transport.Send(packet);
            }
        }

        private void SendDoorRotation(int hash, float angle)
        {
            using (var packet = new PacketWriter(PacketType.VehicleDoorRotation))
            {
                packet.WriteInt32(hash);
                packet.WriteSingle(angle);
                transport.Send(packet);
            }
        }

        private void ApplyRemoteDoorAngles()
        {
            int myId = MyPlayerId;
            for (int i = 0; i < doorList.Count; i++)
            {
                var d = doorList[i];
                if (d.IsHayosikoSidedoor || d.DoorObject == null) continue;
                if (d.OwnerId == myId) continue; // Локальный игрок управляет физикой шарнира сам
                if (d.DoorOpenVar == null || !d.DoorOpenVar.Value) continue;
                if (d.TargetAngle < -900f) continue;

                SetDoorAngle(d, d.TargetAngle);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.VehicleDoorToggle)
            {
                int hash = reader.ReadInt32();
                bool open = reader.ReadBoolean();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleDoorToggle))
                    {
                        relay.WriteInt32(hash);
                        relay.WriteBoolean(open);
                        transport.Send(relay, connectionId);
                    }
                }

                if (trackedDoors.TryGetValue(hash, out var door))
                {
                    door.SuppressEcho = true;
                    door.OwnerId = transport.IsServer ? 1 : 0;
                    door.LastSentAngle = -999f;
                    if (!open)
                    {
                        door.TargetAngle = -999f;
                        if (!door.IsHayosikoSidedoor && door.DoorObject != null)
                        {
                            SetDoorAngle(door, door.ClosedAngle);
                        }
                    }

                    // Запускаем нативный FSM (не перезаписываем DoorOpenVar до MP_OPEN)
                    door.Fsm.SendEvent(open ? "MP_OPEN" : "MP_CLOSE");
                    OnLog?.Invoke($"[Транспорт] Удаленный игрок {(open ? "открыл" : "закрыл")} дверь {door.Fsm.gameObject.name}");
                }
            }
            else if (reader.Type == PacketType.VehicleDoorRotation)
            {
                int hash = reader.ReadInt32();
                float angle = reader.ReadSingle();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleDoorRotation))
                    {
                        relay.WriteInt32(hash);
                        relay.WriteSingle(angle);
                        transport.Send(relay, connectionId);
                    }
                }

                if (trackedDoors.TryGetValue(hash, out var door))
                {
                    if (door.IsHayosikoSidedoor) return; // Сдвижную дверь не вращаем!
                    if (door.DoorObject == null) return;
                    if (door.OwnerId == MyPlayerId) return;
                    if (door.DoorOpenVar == null || !door.DoorOpenVar.Value) return;

                    // Применяется в FixedUpdate() для отсутствия конфликтов с PhysX HingeJoint
                    door.TargetAngle = angle;
                }
            }
            else if (reader.Type == PacketType.VehicleDoorOwnership)
            {
                int hash = reader.ReadInt32();
                int ownerId = reader.ReadInt32();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleDoorOwnership))
                    {
                        relay.WriteInt32(hash);
                        relay.WriteInt32(ownerId);
                        transport.Send(relay, connectionId);
                    }
                }

                if (trackedDoors.TryGetValue(hash, out var door))
                {
                    door.OwnerId = ownerId;
                }
            }
            else if (reader.Type == PacketType.VehicleDoorSnapshot)
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int hash = reader.ReadInt32();
                    bool open = reader.ReadBoolean();
                    float angle = reader.ReadSingle();

                    if (trackedDoors.TryGetValue(hash, out var door))
                    {
                        door.SuppressEcho = true;
                        if (open)
                        {
                            door.Fsm.SendEvent("MP_OPEN");
                            if (door.DoorObject != null && !door.IsHayosikoSidedoor)
                            {
                                door.TargetAngle = angle;
                                SetDoorAngle(door, angle);
                            }
                        }
                    }
                }
            }
        }

        private void HandleClientConnected(int clientId)
        {
            // Отправляем новому игроку снимок всех открытых дверей
            var openDoors = doorList.Where(d => d.DoorOpenVar != null && d.DoorOpenVar.Value).ToList();
            if (openDoors.Count > 0)
            {
                using (var packet = new PacketWriter(PacketType.VehicleDoorSnapshot))
                {
                    packet.WriteInt32(openDoors.Count);
                    for (int i = 0; i < openDoors.Count; i++)
                    {
                        var d = openDoors[i];
                        packet.WriteInt32(d.Hash);
                        packet.WriteBoolean(true);
                        float angle = 0f;
                        if (!d.IsHayosikoSidedoor && d.DoorObject != null)
                        {
                            Vector3 euler = d.DoorObject.transform.localEulerAngles;
                            angle = d.Axis == 0 ? euler.x : (d.Axis == 1 ? euler.y : euler.z);
                        }
                        packet.WriteSingle(angle);
                    }
                    transport.Send(packet);
                }
            }
        }
    }
}
