using System;
using System.Collections.Generic;
using OurSummerCar.Environment;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Player
{
    public class PlayerManager
    {
        public static PlayerManager Instance { get; private set; }

        private readonly NetworkTransport transport;
        private readonly Dictionary<int, RemotePuppet> puppets = new Dictionary<int, RemotePuppet>();
        private readonly Dictionary<int, int> connectionToPlayer = new Dictionary<int, int>();

        private Transform cachedLocalPlayer;
        private CharacterController cachedLocalPlayerCc;
        private float nextSendTime;
        private const float SendInterval = 0.05f; // 20 раз в секунду (20 Hz)
        private static Camera cachedMainCam;

        public string LocalNickname { get; set; } = "Player";
        private readonly Dictionary<int, string> playerNicknames = new Dictionary<int, string>();
        private float nextNicknameSyncTime = 0f;
        private float drinkBeerTimer = 0f;
        private bool isTrackerSubscribed = false;

        public int ActivePuppetsCount => puppets.Count;
        public IEnumerable<RemotePuppet> AllPuppets => puppets.Values;
        public Vector3 LastKnownLocalPosition { get; private set; }

        public RemotePuppet GetPuppet(int playerId)
        {
            puppets.TryGetValue(playerId, out var puppet);
            return puppet;
        }

        public event Action<string> OnLog;

        public PlayerManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
            this.transport.OnClientDisconnected += HandleClientDisconnected;
        }

        private void HandleClientConnected(int connectionId)
        {
            SendLocalPlayerNickname(LocalNickname);
        }

        public void SendLocalPlayerNickname(string nickname = null)
        {
            if (!string.IsNullOrEmpty(nickname)) LocalNickname = nickname;
            if (!transport.IsRunning) return;

            int myId = transport.IsServer ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.HandshakeRequest))
            {
                packet.WriteInt32(myId);
                packet.WriteString(LocalNickname ?? (myId == 0 ? "Хост" : "Игрок"));
                transport.Send(packet);
            }
        }

        public void Update()
        {
            // Автоматическое подключение нативного трекера анимаций WreckMP
            if (OurSummerCar.IsInGameWorld)
            {
                if (LocalPlayerAnimationTracker.Instance == null)
                {
                    var existingTracker = GameObject.Find("OSC_LocalPlayerAnimationTracker");
                    if (existingTracker != null)
                    {
                        UnityEngine.Object.DestroyImmediate(existingTracker);
                    }
                    var trackerObj = new GameObject("OSC_LocalPlayerAnimationTracker");
                    trackerObj.AddComponent<LocalPlayerAnimationTracker>();
                    isTrackerSubscribed = false;
                }

                if (LocalPlayerAnimationTracker.Instance != null && !isTrackerSubscribed)
                {
                    LocalPlayerAnimationTracker.Instance.OnGestureAction += HandleLocalGestureAction;
                    LocalPlayerAnimationTracker.Instance.OnSwearAction += HandleLocalSwearAction;
                    isTrackerSubscribed = true;
                }
            }

            float dt = Time.deltaTime;
            if (drinkBeerTimer > 0f) drinkBeerTimer -= dt;

            foreach (var kvp in puppets)
            {
                kvp.Value.UpdateInterpolation(dt);
            }

            if (!transport.IsRunning) return;

            if (Time.time >= nextNicknameSyncTime)
            {
                nextNicknameSyncTime = Time.time + 4.0f;
                SendLocalPlayerNickname(LocalNickname);
            }

            if (Time.time >= nextSendTime)
            {
                nextSendTime = Time.time + SendInterval;
                SendLocalPlayerTransform();
            }
        }

        private void HandleLocalGestureAction(byte gestureIndex, bool isStart, byte extraParam)
        {
            if (!transport.IsRunning) return;

            int myId = transport.IsServer ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.PlayerGesture))
            {
                packet.WriteInt32(myId);
                packet.WriteByte(gestureIndex);
                packet.WriteBoolean(isStart);
                packet.WriteByte(extraParam);
                transport.Send(packet);
            }
        }

        private void HandleLocalSwearAction(byte swearId)
        {
            if (!transport.IsRunning) return;

            int myId = transport.IsServer ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.PlayerSwear))
            {
                packet.WriteInt32(myId);
                packet.WriteByte(swearId);
                transport.Send(packet);
            }
        }

        private void SendLocalPlayerTransform()
        {
            if (!OurSummerCar.IsInGameWorld) return;

            Transform player = GetLocalPlayerTransform();
            if (player == null) return;

            Vector3 pos = player.position;
            LastKnownLocalPosition = pos;

            if (cachedMainCam == null) cachedMainCam = Camera.main;

            // Во время ходьбы rotY совпадает с телом, а в транспорте камера может свободно вращаться независимо от корпуса
            float rotY = (cachedMainCam != null) ? cachedMainCam.transform.eulerAngles.y : player.eulerAngles.y;
            float pitch = 0f;
            float leanAngle = 0f;

            if (cachedMainCam != null)
            {
                pitch = cachedMainCam.transform.eulerAngles.x;
                if (pitch > 180f) pitch -= 360f;

                float z = cachedMainCam.transform.localEulerAngles.z;
                if (z > 180f) z -= 360f;
                leanAngle = z;
            }

            bool isLeaningQ = Input.GetKey(KeyCode.Q);
            try
            {
                if (!isLeaningQ) isLeaningQ = cInput.GetButton("ReachLeft");
            }
            catch { }

            if (isLeaningQ)
            {
                leanAngle = 0f; // Q наклон вперед обрабатывается через animState |= 64, крен (roll) должен быть строго 0!
            }
            else if (Mathf.Abs(leanAngle) < 0.5f)
            {
                if (Input.GetKey(KeyCode.E)) leanAngle = -15f;
            }

            // Хост отправляет ID = 0, Клиент отправляет ID = 1
            int myId = transport.IsServer ? 0 : 1;

            // Определение анимаций локального игрока через нативный трекер WreckMP
            var tracker = LocalPlayerAnimationTracker.Instance;
            bool isSmoking = tracker != null ? tracker.IsSmoking : Input.GetKey(KeyCode.I);
            byte crouchLevel = tracker != null ? tracker.CurrentCrouchLevel : (byte)(Input.GetKey(KeyCode.C) ? 1 : 0);
            bool isPassenger = tracker != null ? tracker.IsPlayerInCar : false;
            bool isSleepingInBed = (SleepSyncManager.Instance != null && SleepSyncManager.Instance.IsLocalLayingDown);
            bool isDriving = Vehicles.VehiclePhysicsManager.Instance != null && Vehicles.VehiclePhysicsManager.Instance.IsLocalPlayerDriving();
            bool isAirborne = !isPassenger && !isDriving && !isSleepingInBed && cachedLocalPlayerCc != null && !cachedLocalPlayerCc.isGrounded;

            byte animState = 0;
            if (isSmoking) animState |= 1;
            if (crouchLevel == 1) animState |= 2;
            else if (crouchLevel >= 2) animState |= 32;
            if (isPassenger) animState |= 4;
            if (PlayerActionsManager.Instance != null && PlayerActionsManager.Instance.IsLocalPeeing) animState |= 8;
            if (drinkBeerTimer > 0f) animState |= 16;
            if (isLeaningQ) animState |= 64;
            if (isSleepingInBed) animState |= 128;

            using (var packet = new PacketWriter(PacketType.PlayerSync))
            {
                packet.WriteInt32(myId);
                packet.WriteSingle(pos.x);
                packet.WriteSingle(pos.y);
                packet.WriteSingle(pos.z);
                packet.WriteSingle(rotY);
                packet.WriteSingle(pitch);
                packet.WriteByte(animState);
                packet.WriteSingle(leanAngle);
                packet.WriteBoolean(isAirborne);

                transport.Send(packet);
            }
        }

        private Transform GetLocalPlayerTransform()
        {
            if (!OurSummerCar.IsInGameWorld) return null;

            if (cachedLocalPlayer != null && cachedLocalPlayer.gameObject.activeInHierarchy && (cachedLocalPlayer.name == "PLAYER" || cachedLocalPlayer.CompareTag("Player")))
            {
                if (cachedLocalPlayerCc == null)
                {
                    cachedLocalPlayerCc = cachedLocalPlayer.GetComponent<CharacterController>();
                }
                return cachedLocalPlayer;
            }

            GameObject playerObj = GameObject.Find("PLAYER");
            if (playerObj == null)
            {
                playerObj = GameObject.FindWithTag("Player");
            }

            if (playerObj != null)
            {
                cachedLocalPlayer = playerObj.transform;
                cachedLocalPlayerCc = playerObj.GetComponent<CharacterController>();
                LastKnownLocalPosition = cachedLocalPlayer.position;
            }
            else
            {
                cachedLocalPlayer = null;
                cachedLocalPlayerCc = null;
            }

            return cachedLocalPlayer;
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (!OurSummerCar.IsInGameWorld) return;

            // 0. Синхронизация никнеймов игроков
            if (reader.Type == PacketType.HandshakeRequest)
            {
                int senderPlayerId = reader.ReadInt32();
                string remoteNick = reader.ReadString();

                int myId = transport.IsServer ? 0 : 1;
                if (senderPlayerId == myId) return;

                if (!string.IsNullOrEmpty(remoteNick))
                {
                    bool isNew = !playerNicknames.TryGetValue(senderPlayerId, out string existing) || existing != remoteNick;
                    playerNicknames[senderPlayerId] = remoteNick;
                    if (puppets.TryGetValue(senderPlayerId, out var p))
                    {
                        p.SetNickname(remoteNick);
                    }
                    if (isNew)
                    {
                        OnLog?.Invoke($"[Игроки] В игре: <color=yellow>{remoteNick}</color> (ID: {senderPlayerId})");
                    }
                }

                if (transport.IsServer)
                {
                    using (var response = new PacketWriter(PacketType.HandshakeResponse))
                    {
                        response.WriteInt32(myId);
                        response.WriteString(LocalNickname ?? "Хост");
                        transport.Send(response, connectionId);
                    }
                }
                return;
            }
            else if (reader.Type == PacketType.HandshakeResponse)
            {
                int senderPlayerId = reader.ReadInt32();
                string remoteNick = reader.ReadString();

                int myId = transport.IsServer ? 0 : 1;
                if (senderPlayerId == myId) return;

                if (!string.IsNullOrEmpty(remoteNick))
                {
                    bool isNew = !playerNicknames.TryGetValue(senderPlayerId, out string existing) || existing != remoteNick;
                    playerNicknames[senderPlayerId] = remoteNick;
                    if (puppets.TryGetValue(senderPlayerId, out var p))
                    {
                        p.SetNickname(remoteNick);
                    }
                    if (isNew)
                    {
                        OnLog?.Invoke($"[Игроки] Хост: <color=lime>{remoteNick}</color>");
                    }
                }
                return;
            }

            // 1. События жестов персонажа
            if (reader.Type == PacketType.PlayerGesture)
            {
                int senderPlayerId = reader.ReadInt32();
                byte gestureIndex = reader.ReadByte();
                bool isStart = reader.ReadBoolean();
                byte extraParam = reader.ReadByte();

                int myId = transport.IsServer ? 0 : 1;
                if (senderPlayerId == myId) return;

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.PlayerGesture))
                    {
                        relay.WriteInt32(senderPlayerId);
                        relay.WriteByte(gestureIndex);
                        relay.WriteBoolean(isStart);
                        relay.WriteByte(extraParam);
                        transport.Send(relay, connectionId);
                    }
                }

                if (puppets.TryGetValue(senderPlayerId, out var puppet))
                {
                    if (isStart) puppet.TriggerGesture(gestureIndex, extraParam);
                    else puppet.StopGesture(gestureIndex);
                }
                return;
            }

            // 2. Потоковая синхронизация положения и локомоции
            if (reader.Type != PacketType.PlayerSync) return;

            int playerId = reader.ReadInt32();
            float posX = reader.ReadSingle();
            float posY = reader.ReadSingle();
            float posZ = reader.ReadSingle();
            float rotY = reader.ReadSingle();
            float pitch = reader.ReadSingle();
            byte animState = reader.ReadByte();
            float leanAngle = (reader.RemainingBytes >= 4) ? reader.ReadSingle() : 0f;
            bool isAirborne = (reader.RemainingBytes >= 1) ? reader.ReadBoolean() : false;

            int currentMyId = transport.IsServer ? 0 : 1;
            // Игнорируем любые эхо собственных пакетов
            if (playerId == currentMyId) return;

            // Валидация ID игрока (в P2P 0 - Хост, 1+ - Клиенты)
            if (playerId < 0 || playerId > 32) return;

            connectionToPlayer[connectionId] = playerId;

            // Если хост получил пакет от клиента — пересылает другим клиентам
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.PlayerSync))
                {
                    relay.WriteInt32(playerId);
                    relay.WriteSingle(posX);
                    relay.WriteSingle(posY);
                    relay.WriteSingle(posZ);
                    relay.WriteSingle(rotY);
                    relay.WriteSingle(pitch);
                    relay.WriteByte(animState);
                    relay.WriteSingle(leanAngle);
                    relay.WriteBoolean(isAirborne);
                    transport.Send(relay, connectionId);
                }
            }

            Vector3 newPos = new Vector3(posX, posY, posZ);

            // Если кукла еще не создана ИЛИ была уничтожена при перезагрузке сцены
            if (!puppets.TryGetValue(playerId, out var puppetObj) || puppetObj.RootObject == null)
            {
                if (puppetObj != null) puppetObj.Destroy();
                puppetObj = new RemotePuppet(playerId, newPos, Quaternion.Euler(0, rotY, 0));
                puppets[playerId] = puppetObj;
                if (playerNicknames.TryGetValue(playerId, out string nick))
                {
                    puppetObj.SetNickname(nick);
                }
                puppetObj.UpdateTarget(newPos, rotY, pitch, animState, leanAngle, isAirborne);

                string displayName = playerNicknames.TryGetValue(playerId, out string n) ? n : ((playerId == 0) ? "Хоста" : $"Игрока #{playerId}");
                OnLog?.Invoke($"[Мир] Создана 3D-модель {displayName} на ({posX:F1}, {posY:F1}, {posZ:F1})");
            }
            else
            {
                puppetObj.UpdateTarget(newPos, rotY, pitch, animState, leanAngle, isAirborne);
            }
        }

        private void HandleClientDisconnected(int connectionId)
        {
            int targetPlayerId = connectionId;
            if (connectionToPlayer.TryGetValue(connectionId, out int mappedId))
            {
                targetPlayerId = mappedId;
                connectionToPlayer.Remove(connectionId);
            }

            if (puppets.TryGetValue(targetPlayerId, out var puppet))
            {
                puppet.Destroy();
                puppets.Remove(targetPlayerId);
                OnLog?.Invoke($"[Мир] Кукла игрока #{targetPlayerId} удалена.");
            }
        }

        public void SetDrinkingBeer(float duration = 2.5f)
        {
            if (Items.ItemManager.IsBagUnpackCooldown) return;
            drinkBeerTimer = duration;
        }

        public void TriggerRemoteDrinkBeer(int connectionId)
        {
            if (Items.ItemManager.IsBagUnpackCooldown) return;
            int targetPlayerId = connectionId;
            if (transport.IsClient)
            {
                targetPlayerId = 0; // На клиенте удаленным игроком всегда является хост с ID 0
            }
            else if (connectionToPlayer.TryGetValue(connectionId, out int mappedId))
            {
                targetPlayerId = mappedId;
            }
            else
            {
                targetPlayerId = (connectionId > 0) ? connectionId : 1;
            }

            if (puppets.TryGetValue(targetPlayerId, out var puppet))
            {
                puppet.TriggerGesture(7);
            }
            else
            {
                foreach (var p in puppets.Values)
                {
                    p.TriggerGesture(7);
                    break;
                }
            }
        }


        public void Reset()
        {
            ClearAllPuppets();
            playerNicknames.Clear();
            if (LocalPlayerAnimationTracker.Instance != null && isTrackerSubscribed)
            {
                LocalPlayerAnimationTracker.Instance.OnGestureAction -= HandleLocalGestureAction;
                LocalPlayerAnimationTracker.Instance.OnSwearAction -= HandleLocalSwearAction;
            }
            isTrackerSubscribed = false;
        }

        public void ClearAllPuppets()
        {
            foreach (var p in puppets.Values)
            {
                p?.Destroy();
            }
            puppets.Clear();
            connectionToPlayer.Clear();
            cachedLocalPlayer = null;
            cachedLocalPlayerCc = null;

            // Дополнительная синхронная зачистка любых оставшихся объектов-кукол в сцене
            var hostPuppet = GameObject.Find("RemotePuppet_Host");
            if (hostPuppet != null) UnityEngine.Object.DestroyImmediate(hostPuppet);

            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allObjects.Length; i++)
            {
                if (allObjects[i] != null && allObjects[i].name.StartsWith("RemotePuppet_"))
                {
                    UnityEngine.Object.DestroyImmediate(allObjects[i]);
                }
            }
        }
    }
}
