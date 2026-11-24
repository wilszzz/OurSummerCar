using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    public class TrackedNpc
    {
        public byte Id;
        public string Name;
        public GameObject GameObject;
        public Transform Transform;
        public Rigidbody Rigidbody;
        public List<PlayMakerFSM> DisabledFsms = new List<PlayMakerFSM>();

        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 TargetVelocity;
        public bool IsActive;
    }

    /// <summary>
    /// Сетевой менеджер трафика и NPC:
    /// Автобус (BUS), машины Amis, Fittan, поезд (TRAIN), шоссейный трафик (VehiclesHighway) и полиция (Police).
    /// 
    /// Архитектура:
    /// 1. Хост авторитарен: выполняет ванильную логику ИИ, спавна и маршрутов.
    /// 2. Хост транслирует точные мировые координаты, вращение и скорость активных машин трафика и автобуса (12.5 Гц).
    /// 3. Клиент отключает локальные автономные FSM управления движением (Route/Drive/AI),
    ///    сохраняя визуальные меши, колеса, звуки двигателя и клаксона.
    /// 4. Клиент плавно позиционирует транспорт в полном соответствии с позициями хоста.
    /// 5. Точка полицейской засады синхронизируется, исключая разные места контроля скорости.
    /// </summary>
    public class NpcTrafficManager : MonoBehaviour
    {
        public static NpcTrafficManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        private readonly Dictionary<byte, TrackedNpc> trackedNpcs = new Dictionary<byte, TrackedNpc>();
        private GameObject vehiclesHighwayGo;
        private PlayMakerFSM policeFsm;
        private FsmEvent[] policeClientEvents;

        private bool isInitialized = false;
        private float nextDiscoveryTime = 0f;
        private float nextBroadcastTime = 0f;
        private bool lastHighwayActive = false;

        public void Reset()
        {
            isInitialized = false;
            trackedNpcs.Clear();
            vehiclesHighwayGo = null;
            policeFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            TryDiscoverNpcs();
            isInitialized = true;
            OnLog?.Invoke("[NPC-Трафик] Менеджер трафика и автобусов инициализирован");
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        private void HandleClientConnected(int connectionId)
        {
            if (transport.IsServer && vehiclesHighwayGo != null)
            {
                // Первичная синхронизация состояния шоссе
                using (var packet = new PacketWriter(PacketType.NpcTrafficSync))
                {
                    packet.WriteByte(255); // Маркер системного флага шоссе
                    packet.WriteBoolean(vehiclesHighwayGo.activeSelf);
                    transport.Send(packet, connectionId);
                }
            }
        }

        private void Update()
        {
            if (!isInitialized || !OurSummerCar.IsInGameWorld) return;

            if (Time.time > nextDiscoveryTime)
            {
                nextDiscoveryTime = Time.time + 3.0f;
                TryDiscoverNpcs();
            }

            if (transport.IsServer)
            {
                UpdateServerBroadcast();
            }
            else
            {
                UpdateClientInterpolation();
            }
        }

        private void TryDiscoverNpcs()
        {
            // 1. Автобус (BUS / Bus) - ID 0
            if (!trackedNpcs.ContainsKey(0) || trackedNpcs[0].GameObject == null)
            {
                var bus = GameObject.Find("BUS") ?? GameObject.Find("Bus") ?? GameObject.Find("bus(270)");
                if (bus != null)
                {
                    RegisterNpc(0, "Автобус", bus);
                }
            }

            // 2. Amis машины - ID 1
            if (!trackedNpcs.ContainsKey(1) || trackedNpcs[1].GameObject == null)
            {
                var amis = GameObject.Find("NPC_CARS/Amikset/KYLAJANI") ?? GameObject.Find("AMIS2") ?? GameObject.Find("Amis");
                if (amis != null)
                {
                    RegisterNpc(1, "Amis", amis);
                }
            }

            // 3. Fittan - ID 2
            if (!trackedNpcs.ContainsKey(2) || trackedNpcs[2].GameObject == null)
            {
                var fittan = GameObject.Find("FITTAN") ?? GameObject.Find("Fittan");
                if (fittan != null)
                {
                    RegisterNpc(2, "Fittan", fittan);
                }
            }

            // 4. Поезд (TRAIN / Train) - ID 3
            if (!trackedNpcs.ContainsKey(3) || trackedNpcs[3].GameObject == null)
            {
                var train = GameObject.Find("TRAIN") ?? GameObject.Find("Train");
                if (train != null)
                {
                    RegisterNpc(3, "Поезд", train);
                }
            }

            // 5. Шоссейные машины (VehiclesHighway) - ID 10 + index
            if (vehiclesHighwayGo == null)
            {
                var traffic = GameObject.Find("TRAFFIC");
                if (traffic != null)
                {
                    var vh = traffic.transform.Find("VehiclesHighway");
                    if (vh != null)
                    {
                        vehiclesHighwayGo = vh.gameObject;
                    }

                    var pol = traffic.transform.Find("Police");
                    if (pol != null && policeFsm == null)
                    {
                        HookPolice(pol.GetComponent<PlayMakerFSM>());
                    }
                }
            }

            if (vehiclesHighwayGo != null)
            {
                int count = vehiclesHighwayGo.transform.childCount;
                for (int i = 0; i < count; i++)
                {
                    if (10 + i >= 254) break; // Резервируем ID 254 и 255 для системных маркеров пакетов
                    byte id = (byte)(10 + i);
                    if (!trackedNpcs.ContainsKey(id) || trackedNpcs[id].GameObject == null)
                    {
                        var child = vehiclesHighwayGo.transform.GetChild(i).gameObject;
                        RegisterNpc(id, $"Шоссе #{i}", child);
                    }
                }
            }
        }

        private void RegisterNpc(byte id, string name, GameObject go)
        {
            var tracked = new TrackedNpc
            {
                Id = id,
                Name = name,
                GameObject = go,
                Transform = go.transform,
                Rigidbody = go.GetComponent<Rigidbody>()
            };

            // На клиенте отключаем ванильные FSM автономного вождения
            if (!transport.IsServer)
            {
                var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    var fsm = fsms[i];
                    string fn = fsm.FsmName.ToLower();
                    if (fn.Contains("drive") || fn.Contains("route") || fn.Contains("ai") || 
                        fn.Contains("move") || fn.Contains("logic") || fn.Contains("drivetrain"))
                    {
                        fsm.enabled = false;
                        tracked.DisabledFsms.Add(fsm);
                    }
                }

                if (tracked.Rigidbody != null)
                {
                    tracked.Rigidbody.isKinematic = true;
                }
            }

            trackedNpcs[id] = tracked;
        }

        private void HookPolice(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            policeFsm = fsm;
            policeFsm.Initialize();

            if (transport.IsServer)
            {
                // Хост отслеживает спавн полиции в 4 точках
                policeFsm.InsertAction("State 3", () => SendPoliceSpawn(0), 0, false);
                policeFsm.InsertAction("State 4", () => SendPoliceSpawn(1), 0, false);
                policeFsm.InsertAction("State 5", () => SendPoliceSpawn(2), 0, false);
                policeFsm.InsertAction("State 6", () => SendPoliceSpawn(3), 0, false);
            }
            else
            {
                // Клиент слушает события хоста и отключает локальный случайный выбор
                policeClientEvents = new FsmEvent[]
                {
                    policeFsm.AddEvent("MP_SPAWN0"),
                    policeFsm.AddEvent("MP_SPAWN1"),
                    policeFsm.AddEvent("MP_SPAWN2"),
                    policeFsm.AddEvent("MP_SPAWN3")
                };
                policeFsm.AddGlobalTransition(policeClientEvents[0], "State 3");
                policeFsm.AddGlobalTransition(policeClientEvents[1], "State 4");
                policeFsm.AddGlobalTransition(policeClientEvents[2], "State 5");
                policeFsm.AddGlobalTransition(policeClientEvents[3], "State 6");

                var cop1State = policeFsm.GetState("Cop1");
                if (cop1State != null && cop1State.Actions.Length > 2)
                {
                    cop1State.Actions[2].Enabled = false;
                }
                var state1 = policeFsm.GetState("State 1");
                if (state1 != null && state1.Actions.Length > 0)
                {
                    state1.Actions[0].Enabled = false;
                }
            }
        }

        private void SendPoliceSpawn(byte index)
        {
            using (var packet = new PacketWriter(PacketType.NpcPoliceSync))
            {
                packet.WriteByte(index);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Полиция] Засада активирована в позиции #{index}");
        }

        private void UpdateServerBroadcast()
        {
            if (Time.time < nextBroadcastTime) return;
            nextBroadcastTime = Time.time + 0.08f; // ~12.5 Гц

            // Проверка смены активности шоссе
            if (vehiclesHighwayGo != null)
            {
                bool hwActive = vehiclesHighwayGo.activeSelf;
                if (hwActive != lastHighwayActive)
                {
                    lastHighwayActive = hwActive;
                    using (var packet = new PacketWriter(PacketType.NpcTrafficSync))
                    {
                        packet.WriteByte(255);
                        packet.WriteBoolean(hwActive);
                        transport.Send(packet);
                    }
                }
            }

            // Сбор активных машин для стриминга
            List<TrackedNpc> activeNpcs = new List<TrackedNpc>();
            foreach (var kvp in trackedNpcs)
            {
                var npc = kvp.Value;
                if (npc.GameObject != null && npc.GameObject.activeInHierarchy)
                {
                    activeNpcs.Add(npc);
                }
            }

            if (activeNpcs.Count == 0) return;

            using (var packet = new PacketWriter(PacketType.NpcTrafficSync))
            {
                packet.WriteByte((byte)activeNpcs.Count);

                for (int i = 0; i < activeNpcs.Count; i++)
                {
                    var npc = activeNpcs[i];
                    packet.WriteByte(npc.Id);
                    packet.WriteBoolean(npc.GameObject.activeSelf);

                    Vector3 pos = npc.Transform.position;
                    Quaternion rot = npc.Transform.rotation;
                    Vector3 vel = (npc.Rigidbody != null) ? npc.Rigidbody.velocity : Vector3.zero;

                    packet.WriteSingle(pos.x); packet.WriteSingle(pos.y); packet.WriteSingle(pos.z);
                    packet.WriteSingle(rot.x); packet.WriteSingle(rot.y); packet.WriteSingle(rot.z); packet.WriteSingle(rot.w);
                    packet.WriteSingle(vel.x); packet.WriteSingle(vel.y); packet.WriteSingle(vel.z);
                }

                transport.Send(packet);
            }
        }

        private void UpdateClientInterpolation()
        {
            float dt = Time.deltaTime;
            foreach (var kvp in trackedNpcs)
            {
                var npc = kvp.Value;
                if (npc.GameObject == null || !npc.GameObject.activeSelf) continue;

                if (npc.TargetPosition != Vector3.zero)
                {
                    npc.Transform.position = Vector3.MoveTowards(npc.Transform.position, npc.TargetPosition, 40f * dt);
                    npc.Transform.rotation = Quaternion.Slerp(npc.Transform.rotation, npc.TargetRotation, 15f * dt);
                }
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.NpcTrafficSync)
            {
                HandleNpcTrafficSync(reader);
            }
            else if (reader.Type == PacketType.NpcPoliceSync)
            {
                HandleNpcPoliceSync(reader);
            }
        }

        private void HandleNpcTrafficSync(PacketReader reader)
        {
            byte firstByte = reader.ReadByte();

            // Системный пакет шоссе
            if (firstByte == 255)
            {
                bool hwActive = reader.ReadBoolean();
                if (vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf != hwActive)
                {
                    vehiclesHighwayGo.SetActive(hwActive);
                }
                return;
            }

            int count = firstByte;
            for (int i = 0; i < count; i++)
            {
                byte id = reader.ReadByte();
                bool active = reader.ReadBoolean();

                float px = reader.ReadSingle(); float py = reader.ReadSingle(); float pz = reader.ReadSingle();
                float rx = reader.ReadSingle(); float ry = reader.ReadSingle(); float rz = reader.ReadSingle(); float rw = reader.ReadSingle();
                float vx = reader.ReadSingle(); float vy = reader.ReadSingle(); float vz = reader.ReadSingle();

                if (trackedNpcs.TryGetValue(id, out TrackedNpc npc))
                {
                    if (npc.GameObject != null)
                    {
                        if (npc.GameObject.activeSelf != active)
                        {
                            npc.GameObject.SetActive(active);
                        }

                        if (active)
                        {
                            Vector3 targetPos = new Vector3(px, py, pz);
                            Quaternion targetRot = new Quaternion(rx, ry, rz, rw);
                            Vector3 vel = new Vector3(vx, vy, vz);

                            npc.TargetPosition = targetPos;
                            npc.TargetRotation = targetRot;
                            npc.TargetVelocity = vel;

                            // Если дистанция слишком велика (первичный спавн или скачок), мгновенно телепортируем
                            if (Vector3.Distance(npc.Transform.position, targetPos) > 10.0f)
                            {
                                npc.Transform.position = targetPos;
                                npc.Transform.rotation = targetRot;
                            }

                            if (npc.Rigidbody != null)
                            {
                                npc.Rigidbody.velocity = vel;
                            }
                        }
                    }
                }
            }
        }

        private void HandleNpcPoliceSync(PacketReader reader)
        {
            byte index = reader.ReadByte();
            if (policeFsm != null && policeClientEvents != null && index >= 0 && index < policeClientEvents.Length)
            {
                policeFsm.Fsm.Event(policeClientEvents[index]);
                OnLog?.Invoke($"[Полиция] Локальная засада синхронизирована с хостом (#{index})");
            }
        }
    }
}
