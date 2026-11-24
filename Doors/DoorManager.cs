using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Doors
{
    public class PM_Hook : FsmStateAction
    {
        private readonly Action action;

        public PM_Hook(Action action)
        {
            this.action = action;
        }

        public override void OnEnter()
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[PM_Hook Error] {ex.Message}");
            }
            Finish();
        }
    }

    public class TrackedDoor
    {
        public int Index;
        public GameObject GameObject;
        public Transform Handle;
        public PlayMakerFSM Fsm;
        public FsmBool DoorOpenVar;
        public FsmEvent ToggleEvent;
    }

    public class DoorManager
    {
        public static DoorManager Instance { get; private set; }

        private readonly NetworkTransport transport;
        private readonly List<TrackedDoor> trackedDoors = new List<TrackedDoor>();
        private bool[] isLocalAction;
        private bool isInitialized = false;

        // Гаражные ворота
        private Transform garageLeft;
        private Transform garageRight;
        private Rigidbody garageLeftRb;
        private Rigidbody garageRightRb;
        private PlayMakerFSM garageLeftFsm;
        private PlayMakerFSM garageRightFsm;
        private FsmFloat garageLeftFsmVar;
        private FsmFloat garageRightFsmVar;

        // Аналоговая синхронизация углов (регулировка по градусам вокруг оси Z)
        private float lastSentLeftAngle = -999f;
        private float lastSentRightAngle = -999f;
        private float targetLeftAngle = 0f;
        private float targetRightAngle = 0f;
        private float velLeft = 0f;
        private float velRight = 0f;
        private float lastLocalMoveTimeLeft = 0f;
        private float lastLocalMoveTimeRight = 0f;
        private float lastNetworkUpdateTimeLeft = 0f;
        private float lastNetworkUpdateTimeRight = 0f;
        private float nextGarageCheckTime = 0f;
        private float nextGarageSearchTime = 0f;
        private bool garageInitialized = false;

        public event Action<string> OnLog;
        public int RegisteredDoorsCount => trackedDoors.Count;

        public void Reset()
        {
            isInitialized = false;
            garageInitialized = false;
            trackedDoors.Clear();
            garageLeft = null;
            garageRight = null;
            garageLeftRb = null;
            garageRightRb = null;
            garageLeftFsm = null;
            garageRightFsm = null;
            garageLeftFsmVar = null;
            garageRightFsmVar = null;
            lastSentLeftAngle = -999f;
            lastSentRightAngle = -999f;
            lastLocalMoveTimeLeft = 0f;
            lastLocalMoveTimeRight = 0f;
            lastNetworkUpdateTimeLeft = 0f;
            lastNetworkUpdateTimeRight = 0f;
        }

        public DoorManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeDoors()
        {
            if (isInitialized) return;

            trackedDoors.Clear();

            // 1. Поиск всех межкомнатных дверей сцены по модели WreckMP (исключая створки ворот DoorLeft/DoorRight)
            List<GameObject> list = (from x in Resources.FindObjectsOfTypeAll<GameObject>()
                                     where (x.name.StartsWith("Door") && !x.name.StartsWith("DoorLeft") && !x.name.StartsWith("DoorRight")) ||
                                           (x.name == "coll" && x.transform.parent != null && x.transform.parent.name == "door" && x.transform.root.name == "COMBINE(350-400psi)")
                                     select x).ToList();

            var fridge = GameObject.Find("YARD/Building/KITCHEN/Fridge/Pivot/Handle");
            if (fridge != null)
            {
                list.Add(fridge);
            }

            // Детерминированная сортировка по пути в иерархии (гарантирует одинаковый порядок на хосте и клиенте)
            list.Sort((a, b) => string.CompareOrdinal(SceneHasher.GetScenePath(a.transform), SceneHasher.GetScenePath(b.transform)));

            int hookedCount = 0;
            for (int i = 0; i < list.Count; i++)
            {
                GameObject gameObject = list[i];
                Transform handle = gameObject.transform.Find("Pivot/Handle") ??
                                   gameObject.transform.Find("Handle") ??
                                   gameObject.transform;

                PlayMakerFSM fsm = null;
                foreach (var f in handle.GetComponents<PlayMakerFSM>())
                {
                    if (f.FsmName == "Use")
                    {
                        fsm = f;
                        break;
                    }
                }

                if (fsm != null)
                {
                    fsm.Fsm.InitData();

                    if (fsm.FsmEvents.Any(e => e.Name == "OPENDOOR"))
                    {
                        // Удаляем Rigidbody ТОЛЬКО у самого объекта двери, не трогая дочерние ручки/замки
                        var rb = gameObject.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            UnityEngine.Object.Destroy(rb);
                        }

                        int doorIdx = trackedDoors.Count;
                        FsmBool doorOpen = fsm.FsmVariables.FindFsmBool("DoorOpen");

                        // Добавляем глобальный переход MP_TOGGLEDOOR -> Check position
                        FsmEvent toggleEvent = AddGlobalTransition(fsm, "MP_TOGGLEDOOR", "Check position");

                        // Внедряем хук в состояние Check position
                        InsertAction(fsm, "Check position", () =>
                        {
                            if (doorIdx < isLocalAction.Length && isLocalAction[doorIdx])
                            {
                                bool targetIsOpen = !doorOpen.Value;
                                SendDoorToggle(doorIdx, targetIsOpen);
                            }

                            if (doorIdx < isLocalAction.Length)
                            {
                                isLocalAction[doorIdx] = true;
                            }
                        }, 0);

                        trackedDoors.Add(new TrackedDoor
                        {
                            Index = doorIdx,
                            GameObject = gameObject,
                            Handle = handle,
                            Fsm = fsm,
                            DoorOpenVar = doorOpen,
                            ToggleEvent = toggleEvent
                        });

                        hookedCount++;
                    }
                }
            }

            isLocalAction = new bool[trackedDoors.Count];
            for (int i = 0; i < isLocalAction.Length; i++)
            {
                isLocalAction[i] = true;
            }

            // 2. Инициализация гаражных ворот
            FindGarageDoors();

            isInitialized = (hookedCount > 0);
            OnLog?.Invoke($"[Двери] Подключено межкомнатных дверей: {hookedCount}");
        }

        private void FindGarageDoors()
        {
            if (garageInitialized) return;
            if (Time.time < nextGarageSearchTime) return;
            nextGarageSearchTime = Time.time + 30.0f; // Защита от частых сканирований

            Transform garageDoors = null;
            var yard = GameObject.Find("YARD");
            if (yard != null)
            {
                garageDoors = yard.transform.Find("Building/Garage/GarageDoors");
            }
            if (garageDoors == null)
            {
                var gdObj = GameObject.Find("GarageDoors");
                if (gdObj != null) garageDoors = gdObj.transform;
            }

            if (garageDoors != null)
            {
                garageLeft = garageDoors.Find("DoorLeft");
                garageRight = garageDoors.Find("DoorRight");

                if (garageLeft != null)
                {
                    Transform coll = garageLeft.Find("Coll") ?? garageLeft;
                    foreach (var f in coll.GetComponents<PlayMakerFSM>())
                    {
                        if (f.FsmName == "Use")
                        {
                            garageLeftFsm = f;
                            break;
                        }
                    }
                }

                if (garageRight != null)
                {
                    Transform coll = garageRight.Find("Coll") ?? garageRight;
                    foreach (var f in coll.GetComponents<PlayMakerFSM>())
                    {
                        if (f.FsmName == "Use")
                        {
                            garageRightFsm = f;
                            break;
                        }
                    }
                }
            }

            if (garageLeft == null || garageRight == null)
            {
                return;
            }

            if (garageLeft != null && garageRight != null)
            {
                garageLeftRb = garageLeft.GetComponent<Rigidbody>();
                garageRightRb = garageRight.GetComponent<Rigidbody>();

                targetLeftAngle = garageLeft.localEulerAngles.z;
                targetRightAngle = garageRight.localEulerAngles.z;
                lastSentLeftAngle = targetLeftAngle;
                lastSentRightAngle = targetRightAngle;

                if (garageLeftFsm != null)
                {
                    garageLeftFsm.Fsm.InitData();
                    garageLeftFsmVar = garageLeftFsm.FsmVariables.FindFsmFloat("Rotation");

                    InsertAction(garageLeftFsm, "Open", () =>
                    {
                        lastLocalMoveTimeLeft = Time.time;
                    }, 0);

                    InsertAction(garageLeftFsm, "Close", () =>
                    {
                        lastLocalMoveTimeLeft = Time.time;
                    }, 0);
                }

                if (garageRightFsm != null)
                {
                    garageRightFsm.Fsm.InitData();
                    garageRightFsmVar = garageRightFsm.FsmVariables.FindFsmFloat("Rotation");

                    InsertAction(garageRightFsm, "Open", () =>
                    {
                        lastLocalMoveTimeRight = Time.time;
                    }, 0);

                    InsertAction(garageRightFsm, "Close", () =>
                    {
                        lastLocalMoveTimeRight = Time.time;
                    }, 0);
                }

                garageInitialized = true;
                OnLog?.Invoke($"[Гараж] Ворота успешно подключены (Аналоговый режим Z)! (Left: {garageLeft.name}, Right: {garageRight.name})");
            }
        }

        public void Update()
        {
            if (!isInitialized)
            {
                InitializeDoors();
            }

            if (!garageInitialized || garageLeft == null || garageRight == null)
            {
                FindGarageDoors();
                if (!garageInitialized) return;
            }

            // 1. Плавная интерполяция створок на принимающей стороне (SmoothDamp по градусам)
            InterpolateRemoteDoors();

            // 2. Периодическая проверка и отправка локального движения створок (20 Гц / каждые 50 мс)
            if (Time.time >= nextGarageCheckTime && transport != null && transport.IsRunning)
            {
                nextGarageCheckTime = Time.time + 0.05f;
                UpdateLocalGarageMovement();
            }
        }

        private void UpdateLocalGarageMovement()
        {
            float curLeft = garageLeft.localEulerAngles.z;
            float curRight = garageRight.localEulerAngles.z;

            // Проверяем, удерживает ли игрок створку мышью в текущий момент
            bool isLeftInteracting = (garageLeftFsm != null && (garageLeftFsm.ActiveStateName == "Open" || garageLeftFsm.ActiveStateName == "Close"));
            bool isRightInteracting = (garageRightFsm != null && (garageRightFsm.ActiveStateName == "Open" || garageRightFsm.ActiveStateName == "Close"));

            float deltaLeft = Math.Abs(Mathf.DeltaAngle(curLeft, lastSentLeftAngle));
            float deltaRight = Math.Abs(Mathf.DeltaAngle(curRight, lastSentRightAngle));

            // Створка считается локально движущейся при активном взаимодействии или пока продолжается физическое вращение (>0.3 град)
            bool isLeftMoving = deltaLeft > 0.3f;
            bool isRightMoving = deltaRight > 0.3f;

            if (isLeftInteracting || isLeftMoving)
            {
                lastLocalMoveTimeLeft = Time.time;
            }

            if (isRightInteracting || isRightMoving)
            {
                lastLocalMoveTimeRight = Time.time;
            }

            bool isLeftLocal = isLeftInteracting || isLeftMoving || (Time.time - lastLocalMoveTimeLeft < 0.8f);
            bool isRightLocal = isRightInteracting || isRightMoving || (Time.time - lastLocalMoveTimeRight < 0.8f);

            bool leftDelta = deltaLeft > 0.3f;
            bool rightDelta = deltaRight > 0.3f;

            if ((isLeftLocal && leftDelta) || (isRightLocal && rightDelta))
            {
                lastSentLeftAngle = curLeft;
                lastSentRightAngle = curRight;

                using (var packet = new PacketWriter(PacketType.GarageDoorSync))
                {
                    packet.WriteSingle(curLeft);
                    packet.WriteSingle(curRight);
                    packet.WriteBoolean(isLeftLocal);
                    packet.WriteBoolean(isRightLocal);
                    transport.Send(packet);
                }
            }
        }

        private void InterpolateRemoteDoors()
        {
            // Плавное следование левой створки за сетевой целью (SmoothDampAngle без микро-рывков)
            bool isLeftInteracting = (garageLeftFsm != null && (garageLeftFsm.ActiveStateName == "Open" || garageLeftFsm.ActiveStateName == "Close"));
            bool isLeftLocal = isLeftInteracting || (Time.time - lastLocalMoveTimeLeft < 0.8f);

            if (!isLeftLocal && Time.time - lastNetworkUpdateTimeLeft < 3.0f)
            {
                float cur = garageLeft.localEulerAngles.z;
                float delta = Math.Abs(Mathf.DeltaAngle(cur, targetLeftAngle));
                if (delta > 0.05f)
                {
                    float next = Mathf.SmoothDampAngle(cur, targetLeftAngle, ref velLeft, 0.06f, 540f, Time.deltaTime);
                    ApplyGarageAngle(garageLeft, garageLeftRb, next, garageLeftFsmVar, false);
                }
                else
                {
                    velLeft = 0f;
                    if (delta > 0.001f)
                    {
                        ApplyGarageAngle(garageLeft, garageLeftRb, targetLeftAngle, garageLeftFsmVar, true);
                    }
                }
            }

            // Плавное следование правой створки за сетевой целью (SmoothDampAngle без микро-рывков)
            bool isRightInteracting = (garageRightFsm != null && (garageRightFsm.ActiveStateName == "Open" || garageRightFsm.ActiveStateName == "Close"));
            bool isRightLocal = isRightInteracting || (Time.time - lastLocalMoveTimeRight < 0.8f);

            if (!isRightLocal && Time.time - lastNetworkUpdateTimeRight < 3.0f)
            {
                float cur = garageRight.localEulerAngles.z;
                float delta = Math.Abs(Mathf.DeltaAngle(cur, targetRightAngle));
                if (delta > 0.05f)
                {
                    float next = Mathf.SmoothDampAngle(cur, targetRightAngle, ref velRight, 0.06f, 540f, Time.deltaTime);
                    ApplyGarageAngle(garageRight, garageRightRb, next, garageRightFsmVar, false);
                }
                else
                {
                    velRight = 0f;
                    if (delta > 0.001f)
                    {
                        ApplyGarageAngle(garageRight, garageRightRb, targetRightAngle, garageRightFsmVar, true);
                    }
                }
            }
        }

        private void ApplyGarageAngle(Transform door, Rigidbody rb, float angleZ, FsmFloat fsmVar, bool resetVelocity = false)
        {
            if (door == null) return;

            // Локальный игрок двигает створку исключительно чистой нативной физикой MSC (без перехвата координат самого себя)
            if (door == garageLeft)
            {
                bool isLeftInteracting = (garageLeftFsm != null && (garageLeftFsm.ActiveStateName == "Open" || garageLeftFsm.ActiveStateName == "Close"));
                if (isLeftInteracting || (Time.time - lastLocalMoveTimeLeft < 0.8f))
                {
                    return;
                }
            }
            else if (door == garageRight)
            {
                bool isRightInteracting = (garageRightFsm != null && (garageRightFsm.ActiveStateName == "Open" || garageRightFsm.ActiveStateName == "Close"));
                if (isRightInteracting || (Time.time - lastLocalMoveTimeRight < 0.8f))
                {
                    return;
                }
            }

            Vector3 curLocal = door.localEulerAngles;
            door.localEulerAngles = new Vector3(curLocal.x, curLocal.y, angleZ);

            if (rb != null)
            {
                if (resetVelocity)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                rb.MoveRotation(door.rotation);
            }

            if (fsmVar != null)
            {
                fsmVar.Value = angleZ;
            }
        }

        private static float ToSignedAngle(float angle)
        {
            float a = angle % 360f;
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
        }

        private void SendDoorToggle(int doorIndex, bool targetIsOpen)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.DoorToggle))
            {
                packet.WriteInt32(doorIndex);
                packet.WriteBoolean(targetIsOpen);
                transport.Send(packet);
            }

            string name = doorIndex < trackedDoors.Count ? trackedDoors[doorIndex].GameObject.name : $"Door#{doorIndex}";
            OnLog?.Invoke($"[Двери] Отправлено переключение: {name} -> {(targetIsOpen ? "ОТКР" : "ЗАКР")}");
        }

        private void HandleClientConnected(int connectionId)
        {
            if (transport.IsServer)
            {
                // Снимок межкомнатных дверей
                using (var packet = new PacketWriter(PacketType.DoorSnapshot))
                {
                    packet.WriteInt32(trackedDoors.Count);
                    for (int i = 0; i < trackedDoors.Count; i++)
                    {
                        var door = trackedDoors[i];
                        bool isOpen = door.DoorOpenVar != null ? door.DoorOpenVar.Value : false;
                        packet.WriteBoolean(isOpen);
                    }
                    transport.Send(packet);
                }
                OnLog?.Invoke($"[Двери] Отправлен снимок состояний ({trackedDoors.Count} дверей) игроку #{connectionId}");

                // Снимок гаражных ворот
                if (garageLeft != null && garageRight != null)
                {
                    using (var packet = new PacketWriter(PacketType.GarageDoorSync))
                    {
                        packet.WriteSingle(garageLeft.localEulerAngles.z);
                        packet.WriteSingle(garageRight.localEulerAngles.z);
                        packet.WriteBoolean(true);
                        packet.WriteBoolean(true);
                        transport.Send(packet);
                    }
                    OnLog?.Invoke($"[Гараж] Отправлен снимок углов ворот ({ToSignedAngle(garageLeft.localEulerAngles.z):F1}°, {ToSignedAngle(garageRight.localEulerAngles.z):F1}°) игроку #{connectionId}");
                }
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.DoorToggle:
                    HandleDoorToggle(reader, connectionId);
                    break;
                case PacketType.DoorSnapshot:
                    HandleDoorSnapshot(reader);
                    break;
                case PacketType.GarageDoorSync:
                    HandleGarageDoorSync(reader, connectionId);
                    break;
            }
        }

        private void HandleDoorToggle(PacketReader reader, int connectionId)
        {
            int doorIndex = reader.ReadInt32();
            bool targetIsOpen = reader.ReadBoolean();

            // Ретранслируем другим клиентам БЕЗ эха отправителю
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.DoorToggle))
                {
                    relay.WriteInt32(doorIndex);
                    relay.WriteBoolean(targetIsOpen);
                    transport.Send(relay, connectionId);
                }
            }

            if (doorIndex < 0 || doorIndex >= trackedDoors.Count) return;

            var door = trackedDoors[doorIndex];
            if (door.Fsm == null || door.DoorOpenVar == null) return;

            isLocalAction[doorIndex] = false;
            door.DoorOpenVar.Value = !targetIsOpen;
            door.Fsm.Fsm.Event(door.ToggleEvent);

            OnLog?.Invoke($"[Двери] Применено: {door.GameObject.name} -> {(targetIsOpen ? "ОТКР" : "ЗАКР")}");
        }

        private void HandleDoorSnapshot(PacketReader reader)
        {
            int count = reader.ReadInt32();
            int applied = 0;

            for (int i = 0; i < count; i++)
            {
                bool hostIsOpen = reader.ReadBoolean();
                if (i < trackedDoors.Count)
                {
                    var door = trackedDoors[i];
                    if (door.DoorOpenVar != null && door.DoorOpenVar.Value != hostIsOpen)
                    {
                        isLocalAction[i] = false;
                        door.DoorOpenVar.Value = !hostIsOpen;
                        door.Fsm.Fsm.Event(door.ToggleEvent);
                        applied++;
                    }
                }
            }

            OnLog?.Invoke($"[Двери] Снимок хоста применен ({applied} дверей синхронизировано)");
        }

        private void HandleGarageDoorSync(PacketReader reader, int connectionId)
        {
            float netLeft = reader.ReadSingle();
            float netRight = reader.ReadSingle();
            bool leftActive = reader.ReadBoolean();
            bool rightActive = reader.ReadBoolean();

            // Ретранслируем другим клиентам БЕЗ эха отправителю
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.GarageDoorSync))
                {
                    relay.WriteSingle(netLeft);
                    relay.WriteSingle(netRight);
                    relay.WriteBoolean(leftActive);
                    relay.WriteBoolean(rightActive);
                    transport.Send(relay, connectionId);
                }
            }

            if (!garageInitialized || garageLeft == null || garageRight == null)
            {
                FindGarageDoors();
                if (!garageInitialized) return;
            }

            if (leftActive)
            {
                bool isLeftInteracting = (garageLeftFsm != null && (garageLeftFsm.ActiveStateName == "Open" || garageLeftFsm.ActiveStateName == "Close"));
                bool isLeftLocal = isLeftInteracting || (Time.time - lastLocalMoveTimeLeft < 0.8f);

                if (!isLeftLocal)
                {
                    targetLeftAngle = netLeft;
                    lastNetworkUpdateTimeLeft = Time.time;
                    lastSentLeftAngle = netLeft;
                }
            }

            if (rightActive)
            {
                bool isRightInteracting = (garageRightFsm != null && (garageRightFsm.ActiveStateName == "Open" || garageRightFsm.ActiveStateName == "Close"));
                bool isRightLocal = isRightInteracting || (Time.time - lastLocalMoveTimeRight < 0.8f);

                if (!isRightLocal)
                {
                    targetRightAngle = netRight;
                    lastNetworkUpdateTimeRight = Time.time;
                    lastSentRightAngle = netRight;
                }
            }
        }

        private static FsmEvent AddGlobalTransition(PlayMakerFSM pm, string eventName, string toStateName)
        {
            pm.Fsm.InitData();
            FsmEvent fsmEvent = FsmEvent.GetFsmEvent(eventName) ?? new FsmEvent(eventName);

            var events = pm.FsmEvents.ToList();
            if (!events.Contains(fsmEvent))
            {
                events.Add(fsmEvent);
                pm.Fsm.Events = events.ToArray();
            }

            var transitions = pm.Fsm.GlobalTransitions != null ? pm.Fsm.GlobalTransitions.ToList() : new List<FsmTransition>();
            for (int i = 0; i < transitions.Count; i++)
            {
                if (transitions[i] != null && transitions[i].FsmEvent != null && transitions[i].FsmEvent.Name == eventName && transitions[i].ToState == toStateName)
                {
                    return fsmEvent;
                }
            }

            transitions.Add(new FsmTransition
            {
                FsmEvent = fsmEvent,
                ToState = toStateName
            });
            pm.Fsm.GlobalTransitions = transitions.ToArray();
            pm.Fsm.InitData();
            return fsmEvent;
        }

        private static void InsertAction(PlayMakerFSM pm, string stateName, Action action, int index = 0)
        {
            var state = pm.FsmStates.FirstOrDefault(x => x.Name == stateName);
            if (state == null) return;

            var list = state.Actions.ToList();
            var hook = new PM_Hook(action);
            if (index < 0 || index >= list.Count)
            {
                list.Add(hook);
            }
            else
            {
                list.Insert(index, hook);
            }
            state.Actions = list.ToArray();
        }
    }
}
