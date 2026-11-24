using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using OurSummerCar.Player;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Данные отдельного пассажирского места в автомобиле
    /// </summary>
    public class PassengerSeatInstance
    {
        public GameObject VehicleObject;
        public int VehicleHash;
        public int SeatIndex;
        public Vector3 TriggerOffset;
        public Vector3 HeadPivotOffset;
        public GameObject SeatObject;
        public PlayMakerFSM TriggerFsm;
        public bool IsOccupiedByLocal;
        public bool IsOccupiedByRemote;
        public int OccupiedPlayerId = -1;
    }

    /// <summary>
    /// Конфигурация пассажирского сиденья для автомобиля
    /// </summary>
    public struct VehicleSeatConfig
    {
        public string PrimaryName;
        public string[] Fallbacks;
        public int SeatIndex;
        public Vector3 TriggerOffset;
        public Vector3 HeadPivotOffset;

        public VehicleSeatConfig(string primaryName, string[] fallbacks, int seatIndex, Vector3 triggerOffset, Vector3 headPivotOffset)
        {
            PrimaryName = primaryName;
            Fallbacks = fallbacks;
            SeatIndex = seatIndex;
            TriggerOffset = triggerOffset;
            HeadPivotOffset = headPivotOffset;
        }
    }

    /// <summary>
    /// Менеджер пассажирских мест во всем транспорте (Hayosiko, Satsuma, Gifu, Ferndale, Ruscko, Flatbed).
    /// Построен по архитектурной модели WreckMP (NetVehicle.AddPassengerSeat) с клонированием префаба PlayerFunctions.
    /// </summary>
    public class VehiclePassengerManager : MonoBehaviour
    {
        public static VehiclePassengerManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        private GameObject masterTemplate = null;
        private float nextSeatScanTime = 0f;

        // Реестр всех созданных сидений
        private readonly List<PassengerSeatInstance> allSeats = new List<PassengerSeatInstance>();
        private readonly Dictionary<long, PassengerSeatInstance> seatsLookup = new Dictionary<long, PassengerSeatInstance>();

        // Активные удаленные пассажиры: playerId -> PassengerSeatInstance
        private readonly Dictionary<int, PassengerSeatInstance> remotePassengerSeats = new Dictionary<int, PassengerSeatInstance>();

        // Конфигурация всех сидений по точным координатам WreckMP
        private static readonly VehicleSeatConfig[] SeatConfigs = new VehicleSeatConfig[]
        {
            // HAYOSIKO (Фургон)
            new VehicleSeatConfig("HAYOSIKO(1500kg, 250)", new[] { "Hayosiko", "HAYOSIKO" }, 0,
                new Vector3(0.436f, 0.876f, 0.779f), new Vector3(0.334f, 1.291f, 0.791f)),
            new VehicleSeatConfig("HAYOSIKO(1500kg, 250)", new[] { "Hayosiko", "HAYOSIKO" }, 1,
                new Vector3(0.036f, 0.876f, 0.779f), new Vector3(0.334f, 1.291f, 0.791f)),

            // SATSUMA (Сацума)
            new VehicleSeatConfig("SATSUMA(557kg, 248)", new[] { "SATSUMA(580kg, 87)", "Satsuma", "SATSUMA" }, 0,
                new Vector3(0.282f, 0.307f, -0.067f), new Vector3(0.300f, 0.532f, 0.020f)),
            new VehicleSeatConfig("SATSUMA(557kg, 248)", new[] { "SATSUMA(580kg, 87)", "Satsuma", "SATSUMA" }, 1,
                new Vector3(-0.282f, 0.307f, -0.567f), new Vector3(-0.300f, 0.532f, -0.480f)),
            new VehicleSeatConfig("SATSUMA(557kg, 248)", new[] { "SATSUMA(580kg, 87)", "Satsuma", "SATSUMA" }, 2,
                new Vector3(0.282f, 0.307f, -0.567f), new Vector3(0.300f, 0.532f, -0.480f)),

            // GIFU (Ассенизатор)
            new VehicleSeatConfig("GIFU(750/450psi)", new[] { "Gifu", "GIFU" }, 0,
                new Vector3(0.696f, 1.759f, 2.831f), new Vector3(0.700f, 1.291f, 0.791f)),
            new VehicleSeatConfig("GIFU(750/450psi)", new[] { "Gifu", "GIFU" }, 1,
                new Vector3(0.696f, 1.759f, 2.331f), new Vector3(0.696f, 1.759f, 2.331f)),
            new VehicleSeatConfig("GIFU(750/450psi)", new[] { "Gifu", "GIFU" }, 2,
                new Vector3(0f, 1.759f, 2.331f), new Vector3(0f, 1.759f, 2.331f)),
            new VehicleSeatConfig("GIFU(750/450psi)", new[] { "Gifu", "GIFU" }, 3,
                new Vector3(-0.704f, 1.759f, 2.331f), new Vector3(-0.704f, 1.759f, 2.331f)),

            // FERNDALE
            new VehicleSeatConfig("FERNDALE(1630kg)", new[] { "Ferndale", "FERNDALE" }, 0,
                new Vector3(0.513f, 0.669f, -0.259f), new Vector3(0.394f, 0.805f, -0.217f)),
            new VehicleSeatConfig("FERNDALE(1630kg)", new[] { "Ferndale", "FERNDALE" }, 1,
                new Vector3(-0.513f, 0.669f, -0.959f), new Vector3(-0.394f, 0.805f, -0.959f)),
            new VehicleSeatConfig("FERNDALE(1630kg)", new[] { "Ferndale", "FERNDALE" }, 2,
                new Vector3(0f, 0.669f, -0.959f), new Vector3(0f, 0.805f, -0.959f)),
            new VehicleSeatConfig("FERNDALE(1630kg)", new[] { "Ferndale", "FERNDALE" }, 3,
                new Vector3(0.513f, 0.669f, -0.959f), new Vector3(0.394f, 0.805f, -0.959f)),

            // RUSCKO
            new VehicleSeatConfig("RCO_RUSCKO12(270)", new[] { "Ruscko", "RUSCKO", "RCO_RUSCKO12" }, 0,
                new Vector3(0.26f, 0.325f, -0.087f), new Vector3(0.224f, 0.855f, -0.363f)),

            // FLATBED
            new VehicleSeatConfig("FLATBED", new[] { "Flatbed" }, 0,
                new Vector3(0.50f, 1.0f, 0f), new Vector3(0.50f, 1.45f, 0f)),

            // BOAT (Лодка: 2 пассажирских места по WreckMP)
            new VehicleSeatConfig("BOAT", new[] { "Boat", "BOAT" }, 0,
                new Vector3(0f, 0f, 0.1f), new Vector3(1f, 0f, 0f)),
            new VehicleSeatConfig("BOAT", new[] { "Boat", "BOAT" }, 1,
                new Vector3(0f, 0f, -1.1f), new Vector3(1f, 0f, 0f))
        };

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;
            transport.OnClientDisconnected += HandleClientDisconnected;

            FindOrCreateMasterTemplate();
            ScanAndCreatePassengerSeats();

            OnLog?.Invoke("[Пассажиры] Менеджер пассажирских мест WreckMP инициализирован");
        }

        public void Reset()
        {
            for (int i = 0; i < allSeats.Count; i++)
            {
                allSeats[i].IsOccupiedByLocal = false;
                allSeats[i].IsOccupiedByRemote = false;
                allSeats[i].OccupiedPlayerId = -1;
            }
            remotePassengerSeats.Clear();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
                transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            if (masterTemplate != null)
            {
                Destroy(masterTemplate);
                masterTemplate = null;
            }
        }

        private void Update()
        {
            if (Time.time >= nextSeatScanTime)
            {
                nextSeatScanTime = Time.time + 3.0f;
                if (masterTemplate == null)
                {
                    FindOrCreateMasterTemplate();
                }
                ScanAndCreatePassengerSeats();
            }
        }

        private void FindOrCreateMasterTemplate()
        {
            if (masterTemplate != null) return;

            GameObject donor = FindDonorTemplate();
            if (donor != null)
            {
                masterTemplate = Instantiate(donor);
                masterTemplate.name = "MP_PassengerFunctions_MasterTemplate";
                masterTemplate.SetActive(false);
                DontDestroyOnLoad(masterTemplate);
                OnLog?.Invoke($"[Пассажиры] Найден базовый донор-префаб PlayerFunctions: {donor.name}");
            }
        }

        private static GameObject FindDonorTemplate()
        {
            // 1. Поиск в автомобиле NPC Amikset/KYLAJANI
            GameObject template = GameObject.Find("NPC_CARS")?.transform.Find("Amikset/KYLAJANI/LOD/PlayerFunctions")?.gameObject;
            if (template != null) return template;

            // 2. Fallbacks
            GameObject npcCars = GameObject.Find("NPC_CARS");
            if (npcCars != null)
            {
                var pf = PlayMakerUtilities.FindChildRecursive(npcCars.transform, "PlayerFunctions");
                if (pf != null) return pf.gameObject;
            }

            GameObject kylajani = GameObject.Find("KYLAJANI") ?? GameObject.Find("Amis") ?? GameObject.Find("AMIS2");
            if (kylajani != null)
            {
                var pf = PlayMakerUtilities.FindChildRecursive(kylajani.transform, "PlayerFunctions");
                if (pf != null) return pf.gameObject;
            }

            GameObject pena = GameObject.Find("PENA") ?? GameObject.Find("FITTITAN");
            if (pena != null)
            {
                var pf = PlayMakerUtilities.FindChildRecursive(pena.transform, "PlayerFunctions");
                if (pf != null) return pf.gameObject;
            }

            return null;
        }

        public void ScanAndCreatePassengerSeats()
        {
            if (masterTemplate == null) return;

            for (int i = 0; i < SeatConfigs.Length; i++)
            {
                var cfg = SeatConfigs[i];
                GameObject vehGo = GameObject.Find(cfg.PrimaryName);
                if (vehGo == null && cfg.Fallbacks != null)
                {
                    for (int f = 0; f < cfg.Fallbacks.Length; f++)
                    {
                        vehGo = GameObject.Find(cfg.Fallbacks[f]);
                        if (vehGo != null) break;
                    }
                }

                if (vehGo == null) continue;

                // Для лодки принудительно уменьшаем нативный PlayerTrigger, чтобы он не перекрывал пассажирские места (модель WreckMP)
                if (vehGo.name.StartsWith("BOAT", StringComparison.OrdinalIgnoreCase) || vehGo.name.StartsWith("Boat"))
                {
                    var pt = vehGo.transform.Find("GFX/Triggers/PlayerTrigger");
                    if (pt != null)
                    {
                        var ptCol = pt.GetComponent<BoxCollider>();
                        if (ptCol != null)
                        {
                            ptCol.center = Vector3.forward * -1.38f;
                            ptCol.size = new Vector3(0.8f, 0.4f, 3.7f);
                        }
                    }
                }

                int vehHash = SceneHasher.GetHash(vehGo);
                long key = ((long)vehHash << 32) | (long)(uint)cfg.SeatIndex;

                if (!seatsLookup.ContainsKey(key))
                {
                    AddPassengerSeat(vehGo, vehHash, cfg.SeatIndex, cfg.TriggerOffset, cfg.HeadPivotOffset);
                }
            }
        }

        public void AddPassengerSeat(GameObject vehGo, int vehHash, int seatIndex, Vector3 triggerOffset, Vector3 headPivotOffset)
        {
            if (masterTemplate == null || vehGo == null) return;

            try
            {
                GameObject seatObj = Instantiate(masterTemplate);
                seatObj.name = $"MPPlayerFunctions_{seatIndex}";

                // 1. Обезвреживаем Death FSM на голове
                Transform dhp = seatObj.transform.Find("DriverHeadPivot");
                if (dhp != null)
                {
                    dhp.localPosition = Vector3.zero;
                    var rb = dhp.GetComponent<Rigidbody>();
                    if (rb != null) rb.isKinematic = true;
                    var death = dhp.GetComponent<PlayMakerFSM>();
                    if (death != null && (death.FsmName == "Death" || dhp.name == "DriverHeadPivot"))
                    {
                        Destroy(death);
                    }
                }

                // 2. Настраиваем камеру пассажира
                Transform headPivot = seatObj.transform.childCount > 1 ? seatObj.transform.GetChild(1) : dhp;
                if (headPivot != null)
                {
                    headPivot.gameObject.SetActive(false);
                    headPivot.localPosition = headPivotOffset;
                    var joint = headPivot.GetComponent<ConfigurableJoint>();
                    if (joint != null) Destroy(joint);
                    headPivot.gameObject.SetActive(true);
                }

                // 3. Удаляем лишние дочерние элементы
                if (seatObj.transform.childCount > 0)
                {
                    Destroy(seatObj.transform.GetChild(0).gameObject);
                }
                seatObj.transform.SetParent(vehGo.transform, false);

                // 4. Настраиваем триггер взаимодействия на сиденье
                Transform trigger = seatObj.transform.Find("PlayerTrigger/DriveTrigger");
                if (trigger == null)
                {
                    trigger = PlayMakerUtilities.FindChildRecursive(seatObj.transform, "DriveTrigger");
                }

                if (trigger != null)
                {
                    trigger.localPosition = triggerOffset;
                }

                Transform playerTrigger = seatObj.transform.Find("PlayerTrigger");
                if (playerTrigger != null)
                {
                    playerTrigger.localPosition = Vector3.zero;
                    var ptFsm = playerTrigger.GetComponent<PlayMakerFSM>();
                    if (ptFsm != null) Destroy(ptFsm);
                    var ptBox = playerTrigger.GetComponent<BoxCollider>();
                    if (ptBox != null) Destroy(ptBox);
                }

                PlayMakerFSM dtFsm = trigger != null ? trigger.GetComponent<PlayMakerFSM>() : null;
                if (dtFsm == null)
                {
                    OnLog?.Invoke($"[Пассажиры] Ошибка: DriveTrigger FSM не найден в {seatObj.name} для {vehGo.name}");
                    return;
                }

                dtFsm.name = "PassengerTrigger";
                if (dtFsm.transform.parent != null)
                {
                    dtFsm.transform.parent.name = "PlayerOffset";
                }
                dtFsm.Initialize();

                var col = dtFsm.GetComponent<CapsuleCollider>();
                if (col != null) col.radius = 0.25f;

                // 5. Привязка физики авто к FSM пассажира
                var checkSpeed = dtFsm.GetState("Check speed");
                if (checkSpeed != null)
                {
                    for (int a = 0; a < checkSpeed.Actions.Length; a++)
                    {
                        var gv = checkSpeed.Actions[a] as GetVelocity;
                        if (gv != null)
                        {
                            gv.gameObject = new FsmOwnerDefault
                            {
                                OwnerOption = OwnerDefaultOption.SpecifyGameObject,
                                GameObject = new FsmGameObject { Value = vehGo }
                            };
                            break;
                        }
                    }
                }

                var inCarState = dtFsm.GetState("Player in car");
                if (inCarState != null)
                {
                    for (int a = 0; a < inCarState.Actions.Length; a++)
                    {
                        var strVal = inCarState.Actions[a] as SetStringValue;
                        if (strVal != null)
                        {
                            strVal.stringValue = "Passenger_" + vehGo.name;
                            break;
                        }
                    }
                }

                var seatInstance = new PassengerSeatInstance
                {
                    VehicleObject = vehGo,
                    VehicleHash = vehHash,
                    SeatIndex = seatIndex,
                    TriggerOffset = triggerOffset,
                    HeadPivotOffset = headPivotOffset,
                    SeatObject = seatObj,
                    TriggerFsm = dtFsm
                };

                // Блокировка повторной посадки (если место занято другим игроком)
                if (dtFsm.HasState("Press return"))
                {
                    dtFsm.InsertAction("Press return", () =>
                    {
                        if (seatInstance.IsOccupiedByRemote)
                        {
                            dtFsm.SendEvent("FINISHED");
                        }
                    }, 0, false);
                }

                // 6. Сетевые хуки входа/выхода
                dtFsm.InsertAction("Reset view", () =>
                {
                    seatInstance.IsOccupiedByLocal = true;
                    if (LocalPlayerAnimationTracker.Instance != null)
                    {
                        LocalPlayerAnimationTracker.Instance.IsPlayerInCar = true;
                    }
                    SendPassengerMode(vehHash, seatIndex, true);
                }, -1, false);

                dtFsm.InsertAction("Create player", () =>
                {
                    seatInstance.IsOccupiedByLocal = false;
                    if (LocalPlayerAnimationTracker.Instance != null)
                    {
                        LocalPlayerAnimationTracker.Instance.IsPlayerInCar = false;
                    }
                    SendPassengerMode(vehHash, seatIndex, false);
                }, -1, false);

                seatObj.SetActive(true);
                dtFsm.gameObject.SetActive(true);

                long key = ((long)vehHash << 32) | (long)(uint)seatIndex;
                seatsLookup[key] = seatInstance;
                allSeats.Add(seatInstance);

                OnLog?.Invoke($"[Пассажиры] Создано место #{seatIndex} в {vehGo.name} (триггер: {triggerOffset}, голова: {headPivotOffset})");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Пассажиры Ошибка] Не удалось создать сиденье #{seatIndex} в {vehGo.name}: {ex.Message}");
            }
        }

        public void SendPassengerMode(int vehHash, int seatIndex, bool isPassenger)
        {
            if (transport == null || !transport.IsRunning) return;

            int myId = transport.IsServer ? 0 : 1;

            using (var packet = new PacketWriter(PacketType.VehiclePassengerMode))
            {
                packet.WriteInt32(vehHash);
                packet.WriteByte((byte)seatIndex);
                packet.WriteBoolean(isPassenger);
                packet.WriteInt32(myId);
                transport.Send(packet);
            }

            OnLog?.Invoke($"[Пассажир] Локальный игрок {(isPassenger ? "сел на" : "покинул")} место #{seatIndex} автомобиля #{vehHash:X}");
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.VehiclePassengerMode)
            {
                HandleVehiclePassengerMode(reader, connectionId);
            }
        }

        private void HandleVehiclePassengerMode(PacketReader reader, int connectionId)
        {
            int vehHash = reader.ReadInt32();
            byte seatIndex = reader.ReadByte();
            bool isPassenger = reader.ReadBoolean();
            int playerId = reader.ReadInt32();

            // 1. Обновляем состояние занятости сиденья
            long key = ((long)vehHash << 32) | (long)(uint)seatIndex;
            if (seatsLookup.TryGetValue(key, out var seat))
            {
                seat.IsOccupiedByRemote = isPassenger;
                seat.OccupiedPlayerId = isPassenger ? playerId : -1;

                if (isPassenger)
                {
                    remotePassengerSeats[playerId] = seat;
                }
                else
                {
                    remotePassengerSeats.Remove(playerId);
                }
            }

            // 2. Применяем к кукле удаленного игрока (анимация passenger + фиксация по координатам сиденья)
            ApplyPassengerToPuppet(playerId, vehHash, seatIndex, isPassenger);

            // 3. Сервер ретранслирует пакет остальным клиентам
            if (transport.IsServer)
            {
                using (var packet = new PacketWriter(PacketType.VehiclePassengerMode))
                {
                    packet.WriteInt32(vehHash);
                    packet.WriteByte(seatIndex);
                    packet.WriteBoolean(isPassenger);
                    packet.WriteInt32(playerId);
                    transport.Send(packet, connectionId);
                }
            }

            string who = playerId == 0 ? "Хост" : $"Клиент #{playerId}";
            OnLog?.Invoke($"[Пассажир] {who} {(isPassenger ? "сел на" : "покинул")} место #{seatIndex} автомобиля #{vehHash:X}");
        }

        private void ApplyPassengerToPuppet(int playerId, int vehHash, int seatIndex, bool isPassenger)
        {
            if (PlayerManager.Instance == null) return;
            var puppet = PlayerManager.Instance.GetPuppet(playerId);
            if (puppet == null) return;

            long key = ((long)vehHash << 32) | (long)(uint)seatIndex;
            if (seatsLookup.TryGetValue(key, out var seat) && seat.VehicleObject != null)
            {
                puppet.SetPassengerState(isPassenger, seat.VehicleObject.transform, seat.TriggerOffset, seatIndex);
            }
            else
            {
                var vehObj = SceneHasher.FindObjectByHash(vehHash);
                puppet.SetPassengerState(isPassenger, vehObj?.transform, Vector3.zero, seatIndex);
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (!transport.IsServer) return;

            // Отправляем вновь подключенному клиенту все занятые пассажирские места
            for (int i = 0; i < allSeats.Count; i++)
            {
                var seat = allSeats[i];
                if (seat.IsOccupiedByLocal)
                {
                    using (var packet = new PacketWriter(PacketType.VehiclePassengerMode))
                    {
                        packet.WriteInt32(seat.VehicleHash);
                        packet.WriteByte((byte)seat.SeatIndex);
                        packet.WriteBoolean(true);
                        packet.WriteInt32(0); // Хост
                        transport.SendTo(packet, clientId);
                    }
                }
                else if (seat.IsOccupiedByRemote && seat.OccupiedPlayerId >= 0)
                {
                    using (var packet = new PacketWriter(PacketType.VehiclePassengerMode))
                    {
                        packet.WriteInt32(seat.VehicleHash);
                        packet.WriteByte((byte)seat.SeatIndex);
                        packet.WriteBoolean(true);
                        packet.WriteInt32(seat.OccupiedPlayerId);
                        transport.SendTo(packet, clientId);
                    }
                }
            }
        }

        private void HandleClientDisconnected(int clientId)
        {
            for (int i = 0; i < allSeats.Count; i++)
            {
                var seat = allSeats[i];
                if (seat.IsOccupiedByRemote && seat.OccupiedPlayerId == clientId)
                {
                    seat.IsOccupiedByRemote = false;
                    seat.OccupiedPlayerId = -1;
                }
            }
            remotePassengerSeats.Remove(clientId);
        }

        public static bool TryGetRemotePassengerSeat(int playerId, out Transform vehTransform, out Vector3 seatLocalOffset)
        {
            vehTransform = null;
            seatLocalOffset = Vector3.zero;

            if (Instance == null) return false;

            if (Instance.remotePassengerSeats.TryGetValue(playerId, out var seat))
            {
                if (seat.VehicleObject != null)
                {
                    vehTransform = seat.VehicleObject.transform;
                    seatLocalOffset = seat.TriggerOffset;
                    return true;
                }
            }

            return false;
        }
    }
}
