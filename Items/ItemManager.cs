using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Doors;
using OurSummerCar.Network;
using OurSummerCar.Player;
using OurSummerCar.Vehicles;
using UnityEngine;

namespace OurSummerCar.Items
{
    /// <summary>
    /// Высокопроизводительный менеджер синхронизации свободных предметов,
    /// удержания в руках (Grabbing), плавной интерполяции физики (Smooth Sync)
    /// и распаковки пакетов (Shopping Bags).
    /// </summary>
    public class ItemManager
    {
        public static ItemManager Instance { get; private set; }

        // Прямой запрет на анимацию питья пива и употребления напитков во время и после распаковки пакетов
        public static float LastBagUnpackTime { get; set; } = -100f;
        public static bool IsBagUnpackCooldown => (Time.time - LastBagUnpackTime) < 4.0f;

        private readonly NetworkTransport transport;
        private readonly PlayerManager playerManager;

        // Поиск и кэширование рук игрока
        private Transform itemPivotTransform;
        private PlayMakerFSM localPickUpFsm;
        private FsmGameObject pickedObjectVar;
        private FsmGameObject pickedToolVar;
        private bool handSearched = false;

        // Состояние локального удержания предмета
        private GameObject lastLocalHeldItem;
        private int lastLocalHeldHash = 0;
        private readonly List<Collider> localDisabledColliders = new List<Collider>();

        // Отслеживание удерживаемых предметов другими игроками: playerId -> itemHash
        private readonly Dictionary<int, int> heldItemsByPlayer = new Dictionary<int, int>();

        // Локальные предметы, движущиеся под нашей физической ответственностью (брошенные нами или сдвинутые дверью/игроком)
        private class LocalMovingTracker
        {
            public int Hash;
            public GameObject Object;
            public Rigidbody Rigidbody;
            public float StartTime; // Время старта броска (для защитного периода grace period)
            public float NextSendTime;
            public float StopTrackingTime;
            public float StillTime;
        }
        private readonly Dictionary<int, LocalMovingTracker> localMovingItems = new Dictionary<int, LocalMovingTracker>();

        // Удаленные предметы, интерполируемые локально (плавный полет на 60 FPS без рывков)
        private class RemoteInterpolatedTracker
        {
            public int Hash;
            public GameObject Object;
            public Rigidbody Rigidbody;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public float LastPacketTime;
        }
        private readonly Dictionary<int, RemoteInterpolatedTracker> remoteMovingItems = new Dictionary<int, RemoteInterpolatedTracker>();

        // Переиспользуемые коллекции для исключения GC аллокаций в Update
        private readonly List<int> cachedLocalKeys = new List<int>();
        private readonly List<int> cachedRemoteKeys = new List<int>();
        private readonly List<int> cachedBagKeys = new List<int>();
        private float nextSanitizeTime = 0f;

        // Реестр перемещенных предметов мира для снимка подключающимся игрокам (Late-Join)
        private struct WorldItemState
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }
        private readonly Dictionary<int, WorldItemState> movedWorldItems = new Dictionary<int, WorldItemState>();

        private void RecordMovedWorldItem(int hash, Vector3 pos, Quaternion rot)
        {
            movedWorldItems[hash] = new WorldItemState { Position = pos, Rotation = rot };
        }

        // Фабрики нативного создания предметов игры (Spawner / CreateItems / Shoppingbag) по модели WreckMP
        public class Creator
        {
            public string Name;
            public PlayMakerFSM Fsm;
            public FsmString NewItemID;
            public FsmFloat Condition;
            public FsmInt ObjectNumberInt;
            public FsmGameObject New;
            public FsmEvent SpawnItem;
            public FsmEvent CreateItem;
            public bool DoSync = true;
            public bool IsShopbag;
            public GameObject LastProcessedObject = null;

            public Creator(PlayMakerFSM fsm, bool isShopbag = false)
            {
                Name = fsm.FsmName;
                IsShopbag = isShopbag;
                Fsm = fsm;
                fsm.Initialize();
                Condition = fsm.FsmVariables.FindFsmFloat("Condition");
                ObjectNumberInt = fsm.FsmVariables.FindFsmInt("ObjectNumberInt");
                New = fsm.FsmVariables.FindFsmGameObject("New");
                NewItemID = fsm.FsmVariables.FindFsmString("ID");

                CreateItem = fsm.AddSafeEvent("MP_CREATEITEM");
                fsm.AddSafeGlobalTransition(CreateItem, fsm.HasState("Create") ? "Create" : "Add ID");

                SpawnItem = fsm.AddSafeEvent("MP_SPAWNITEM");
                fsm.AddSafeGlobalTransition(SpawnItem, fsm.HasState("Spawn") ? "Spawn" : "Create product");
                fsm.Initialize();

                if (fsm.HasState("Spawn"))
                {
                    fsm.InsertAction("Spawn", () => ItemManager.Instance?.OnItemCreated(this), -1, false);
                }
                if (fsm.HasState("Create product"))
                {
                    fsm.InsertAction("Create product", () => ItemManager.Instance?.OnItemCreated(this), -1, false);
                }
                if (fsm.HasState("Create"))
                {
                    fsm.InsertAction("Create", () => ItemManager.Instance?.OnItemCreated(this), 5, false);
                }
                if (fsm.HasState("Add ID"))
                {
                    fsm.InsertAction("Add ID", () => ItemManager.Instance?.OnItemCreated(this), 5, false);
                }
            }
        }

        private readonly List<Creator> creators = new List<Creator>();
        private bool spawnerInitialized = false;

        // Позиционирование предметов при удаленном спавне
        private bool isApplyingRemoteSpawn = false;
        private Vector3 lastRemoteSpawnPos;
        private Quaternion lastRemoteSpawnRot;
        private Vector3 lastRemoteSpawnVel;
        private Vector3 lastRemoteSpawnAngVel;
        private int lastRemoteSpawnHash;

        // Отслеживание пакетов с покупками (Shopping Bags)
        private class TrackedShoppingBag
        {
            public GameObject Bag;
            public int Hash;
            public Vector3 LastPosition;
            public PlayMakerFSM Fsm;
            public PlayMakerFSM ProductSpawnerLogic;
            public FsmGameObject CurrentBagVar;
            public PlayMakerArrayListProxy ItemsProxy;
            public PlayMakerArrayListProxy SprayProxy;
            public bool IsHooked;
        }

        private readonly Dictionary<int, TrackedShoppingBag> trackedBags = new Dictionary<int, TrackedShoppingBag>();
        private readonly HashSet<int> unpackedBags = new HashSet<int>();
        private float nextBagScanTime = 0f;

        // Отслеживание ящиков с пивом (Beer Cases)
        private class TrackedBeerCase
        {
            public GameObject CaseObject;
            public int Hash;
            public PlayMakerFSM Fsm;
            public Transform BottlesParent;
            public FsmInt DestroyedBottlesVar;
            public bool IsHooked;
        }

        private readonly Dictionary<int, TrackedBeerCase> trackedBeerCases = new Dictionary<int, TrackedBeerCase>();
        private readonly HashSet<int> isRemoteBeerDrink = new HashSet<int>();

        // Снимок мира для вновь подключившихся клиентов
        private bool hasRequestedSnapshot = false;
        private float nextSnapshotRequestTime = 0f;

        // Обнаружение спавна предметов читбоксом или модами
        private float nextCheatSpawnScanTime = 0f;
        private readonly Dictionary<string, GameObject> cachedTemplates = new Dictionary<string, GameObject>();

        public event Action<string> OnLog;
        public int TrackedItemsCount => heldItemsByPlayer.Count + localMovingItems.Count + remoteMovingItems.Count;

        public ItemManager(NetworkTransport transport, PlayerManager playerManager)
        {
            Instance = this;
            this.transport = transport;
            this.playerManager = playerManager;

            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
            this.transport.OnClientDisconnected += HandleClientDisconnected;
        }

        public void InitializeItems()
        {
            // 1. Инициализация фабрик создания предметов MSC (CreateItems / Shoppingbag)
            InitializeSpawner();

            // 2. Предварительное детерминированное индексирование всех физических предметов сцены
            int indexedCount = SceneHasher.IndexSceneItems(IsValidPortableItem);
            OnLog?.Invoke($"[Предметы] Проиндексировано {indexedCount} предметов мира.");

            // 3. Первичный поиск рук игрока
            handSearched = false;
            FindPlayerHandReferences();

            // 4. Первичный поиск всех пакетов с покупками в мире
            FindAllInitialShoppingBags();

            // 5. Первичный поиск всех ящиков с пивом в мире
            FindAllInitialBeerCases();
        }

        private void InitializeSpawner()
        {
            if (spawnerInitialized) return;

            var spawnerGo = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(x => x.name == "Spawner" && x.transform.root == x.transform);
            if (spawnerGo == null) return;

            try
            {
                Transform tCreateItems = spawnerGo.transform.Find("CreateItems");
                if (tCreateItems != null)
                {
                    var fsms = tCreateItems.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        creators.Add(new Creator(fsms[i], false));
                    }
                }

                Transform tCreateSpraycans = spawnerGo.transform.Find("CreateSpraycans");
                if (tCreateSpraycans != null)
                {
                    var fsms = tCreateSpraycans.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        creators.Add(new Creator(fsms[i], false));
                    }
                }

                Transform tCreateMooseMeat = spawnerGo.transform.Find("CreateMooseMeat");
                if (tCreateMooseMeat != null)
                {
                    var fsms = tCreateMooseMeat.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        creators.Add(new Creator(fsms[i], false));
                    }
                }

                Transform tCreateShoppingbag = spawnerGo.transform.Find("CreateShoppingbag");
                if (tCreateShoppingbag != null)
                {
                    var fsms = tCreateShoppingbag.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        creators.Add(new Creator(fsms[i], true));
                    }
                }

                spawnerInitialized = true;
                OnLog?.Invoke($"[Spawner] Инициализировано {creators.Count} фабрик предметов игры (CreateItems/Shoppingbag).");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Spawner Init Error] {ex.Message}");
            }
        }

        public void Update()
        {
            // 0. Повторная инициализация Spawner, если он загрузился с задержкой
            if (!spawnerInitialized)
            {
                InitializeSpawner();
            }

            // 1. Поиск рук игрока, если еще не найдены
            if (localPickUpFsm == null && itemPivotTransform == null)
            {
                FindPlayerHandReferences();
            }

            // 2. Отслеживание локального взятия и выбрасывания предметов из рук
            UpdateLocalHandGrabbing();

            // 3. Синхронизация физики предметов, летящих или катящихся локально
            UpdateLocalMovingPhysics();

            // 4. Плавная интерполяция движения удаленных предметов на 60 FPS
            UpdateRemoteInterpolation();

            // 5. Проверка внешних толчков предметов отключена (исключает цепную реакцию переключений isKinematic)

            // 6. Периодический мониторинг пакетов с покупками, ящиков с пивом и нормализация масштабов предметов
            if (Time.time >= nextBagScanTime)
            {
                nextBagScanTime = Time.time + 2.0f;
                ScanNearbyShoppingBags();
                ScanNearbyBeerCases();
                SanitizeDistortedItemScales();
            }

            // 7. Клиент: автоматический запрос снимка предметов после подключения
            if (transport.IsClient && !hasRequestedSnapshot && Time.time >= nextSnapshotRequestTime)
            {
                nextSnapshotRequestTime = Time.time + 3.0f;
                RequestSnapshot();
            }

            // 8. Фоновое обнаружение предметов, заспавненных CheatBox или модами в обход FSM (вокруг игрока)
            if (Time.time >= nextCheatSpawnScanTime && transport != null && transport.IsRunning)
            {
                nextCheatSpawnScanTime = Time.time + 15.0f;
                ScanCheatSpawnedItems();
            }
        }

        #region Hand Grabbing Detection

        private bool FindPlayerHandReferences()
        {
            if (localPickUpFsm != null && itemPivotTransform != null) return true;

            GameObject player = GameObject.Find("PLAYER");
            if (player == null) return false;

            // 1. Поиск зоны ItemPivot под камерой игрока
            if (itemPivotTransform == null)
            {
                var transforms = player.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i].name == "ItemPivot")
                    {
                        itemPivotTransform = transforms[i];
                        break;
                    }
                }
            }

            // 2. Поиск настоящего FSM PickUp (содержащего FsmGameObject PickedObject)
            if (localPickUpFsm == null)
            {
                var fsms = player.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    var f = fsms[i];
                    if (f.FsmName == "PickUp")
                    {
                        var pickedVar = f.FsmVariables.FindFsmGameObject("PickedObject");
                        if (pickedVar != null)
                        {
                            localPickUpFsm = f;
                            pickedObjectVar = pickedVar;
                            pickedToolVar = f.FsmVariables.FindFsmGameObject("PickedTool");
                            break;
                        }
                    }
                }
            }

            if (!handSearched && (localPickUpFsm != null || itemPivotTransform != null))
            {
                handSearched = true;
                string fsmName = localPickUpFsm != null ? localPickUpFsm.gameObject.name : "None";
                string pivotName = itemPivotTransform != null ? itemPivotTransform.name : "None";
                OnLog?.Invoke($"[Руки] Система перехвата инициализирована (FSM: {fsmName}, Pivot: {pivotName})");
                return true;
            }

            return false;
        }

        public GameObject GetCurrentHeldItem()
        {
            if (pickedObjectVar != null && pickedObjectVar.Value != null)
            {
                return pickedObjectVar.Value;
            }

            if (pickedToolVar != null && pickedToolVar.Value != null)
            {
                return pickedToolVar.Value;
            }

            if (itemPivotTransform != null && itemPivotTransform.childCount > 0)
            {
                Transform child = itemPivotTransform.GetChild(0);
                if (child != null && child.gameObject.activeSelf)
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private void UpdateLocalHandGrabbing()
        {
            GameObject currentItem = GetCurrentHeldItem();

            // 1. Игрок только что взял предмет в руки
            if (lastLocalHeldItem == null && currentItem != null)
            {
                if (IsValidPortableItem(currentItem))
                {
                    int itemHash = 0;
                    if (!SceneHasher.TryGetHash(currentItem, out itemHash))
                    {
                        itemHash = SceneHasher.RegisterObject(currentItem);
                    }

                    lastLocalHeldItem = currentItem;
                    lastLocalHeldHash = itemHash;

                    int myId = transport.IsServer ? 0 : 1;
                    heldItemsByPlayer[myId] = itemHash;

                    // Убираем из списков движения
                    localMovingItems.Remove(itemHash);
                    remoteMovingItems.Remove(itemHash);

                    using (var packet = new PacketWriter(PacketType.ItemPickUp))
                    {
                        packet.WriteInt32(itemHash);
                        packet.WriteInt32(myId);
                        packet.WriteString(currentItem.name);
                        transport.Send(packet);
                    }

                    if (currentItem.name.StartsWith("JONNEZ"))
                    {
                        var jColl = currentItem.transform.Find("Colliders/Coll");
                        if (jColl != null) jColl.gameObject.SetActive(false);

                        VehiclePhysicsManager.Instance?.SetVehicleCarriedLocally(currentItem, true);
                    }

                    OnLog?.Invoke($"[Руки] Взят предмет: {currentItem.name} (#{itemHash})");
                }
            }
            // 2. Игрок только что бросил или отпустил предмет
            else if (lastLocalHeldItem != null && currentItem == null)
            {
                int myId = transport.IsServer ? 0 : 1;
                heldItemsByPlayer.Remove(myId);

                if (lastLocalHeldItem != null)
                {
                    // Гарантируем отсоединение от руки/камеры в корень сцены
                    if (lastLocalHeldItem.transform.parent != null && 
                        (lastLocalHeldItem.transform.parent == itemPivotTransform || lastLocalHeldItem.transform.parent.name == "ItemPivot"))
                    {
                        lastLocalHeldItem.transform.SetParent(null, true);
                    }
                    if (lastLocalHeldItem.transform.localScale.sqrMagnitude > 3.5f || lastLocalHeldItem.transform.localScale.sqrMagnitude < 0.2f)
                    {
                        lastLocalHeldItem.transform.localScale = Vector3.one;
                    }

                    bool isJonnez = lastLocalHeldItem.name.StartsWith("JONNEZ");
                    if (isJonnez)
                    {
                        var jColl = lastLocalHeldItem.transform.Find("Colliders/Coll");
                        if (jColl != null) jColl.gameObject.SetActive(true);

                        VehiclePhysicsManager.Instance?.SetVehicleCarriedLocally(lastLocalHeldItem, false);
                    }
                    else
                    {
                        // Гарантируем включение всех коллайдеров брошенного предмета (чтобы игрок снова мог взаимодействовать с ним)
                        var colliders = lastLocalHeldItem.GetComponentsInChildren<Collider>(true);
                        for (int c = 0; c < colliders.Length; c++)
                        {
                            if (colliders[c] != null) colliders[c].enabled = true;
                        }

                        for (int c = 0; c < localDisabledColliders.Count; c++)
                        {
                            if (localDisabledColliders[c] != null) localDisabledColliders[c].enabled = true;
                        }
                        localDisabledColliders.Clear();
                    }

                    Vector3 pos = lastLocalHeldItem.transform.position;
                    Quaternion rot = lastLocalHeldItem.transform.rotation;
                    Vector3 vel = Vector3.zero;
                    Vector3 angVel = Vector3.zero;

                    var rb = lastLocalHeldItem.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        // Принудительно гарантируем включение физики и гравитации после броска
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.WakeUp();

                        // Точечная оптимизация брошенного динамического предмета (CCD на время полета)
                        Environment.PhysicsOptimizer.Instance?.OptimizeRigidbody(rb, true);

                        vel = rb.velocity;
                        angVel = rb.angularVelocity;
                    }

                    using (var packet = new PacketWriter(PacketType.ItemDrop))
                    {
                        packet.WriteInt32(lastLocalHeldHash);
                        packet.WriteInt32(myId);
                        packet.WriteSingle(pos.x);
                        packet.WriteSingle(pos.y);
                        packet.WriteSingle(pos.z);
                        packet.WriteSingle(rot.x);
                        packet.WriteSingle(rot.y);
                        packet.WriteSingle(rot.z);
                        packet.WriteSingle(rot.w);
                        packet.WriteSingle(vel.x);
                        packet.WriteSingle(vel.y);
                        packet.WriteSingle(vel.z);
                        packet.WriteSingle(angVel.x);
                        packet.WriteSingle(angVel.y);
                        packet.WriteSingle(angVel.z);
                        transport.Send(packet);
                    }

                    // Начинаем высокочастотный трекинг физики полета предмета (40 Hz) — для JONNEZ физикой управляет VehiclePhysicsManager
                    if (rb != null && !isJonnez)
                    {
                        localMovingItems[lastLocalHeldHash] = new LocalMovingTracker
                        {
                            Hash = lastLocalHeldHash,
                            Object = lastLocalHeldItem,
                            Rigidbody = rb,
                            StartTime = Time.time,
                            NextSendTime = 0f, // немедленная первая передача
                            StopTrackingTime = Time.time + 10.0f,
                            StillTime = 0f
                        };
                    }

                    OnLog?.Invoke($"[Руки] Брошен предмет: {lastLocalHeldItem.name}");
                }

                lastLocalHeldItem = null;
                lastLocalHeldHash = 0;
            }
        }

        /// <summary>
        /// Вызывается при успешной сборке/установке детали в слот (FSM Assemble).
        /// Исключает деталь из физики рук и трекеров интерполяции/движения,
        /// чтобы сетевой интерполятор не телепортировал установленную деталь обратно на пол.
        /// Не отправляет ItemDrop!
        /// </summary>
        public void OnPartAssembled(GameObject partObj, int partHash)
        {
            if (partObj == null && partHash == 0) return;

            if (partHash == 0 && partObj != null)
            {
                SceneHasher.TryGetHash(partObj, out partHash);
            }
            else if (partObj == null && partHash != 0)
            {
                partObj = SceneHasher.FindObjectByHash(partHash);
            }

            // 1. Трогаем локальное состояние рук ТОЛЬКО если локальный игрок держал ЭТУ конкретную деталь
            bool isLocalHoldingPart =
                (partObj != null && lastLocalHeldItem == partObj) ||
                (partHash != 0 && lastLocalHeldHash == partHash) ||
                (partObj != null && pickedObjectVar != null && pickedObjectVar.Value == partObj);

            if (isLocalHoldingPart)
            {
                int myId = transport.IsServer ? 0 : 1;
                heldItemsByPlayer.Remove(myId);

                localDisabledColliders.Clear();
                lastLocalHeldItem = null;
                lastLocalHeldHash = 0;

                // Ванильное состояние "Assemble" уже отцепило деталь из рук.
                // Отправляем "DROP_PART" только если PickedObject все еще залип на этой детали.
                if (localPickUpFsm != null && pickedObjectVar != null && partObj != null && pickedObjectVar.Value == partObj)
                {
                    localPickUpFsm.SendEvent("DROP_PART");
                }
            }

            // 2. Очистка из списка удерживаемых удаленными игроками
            if (partHash != 0)
            {
                var keysToRemove = new List<int>();
                foreach (var kvp in heldItemsByPlayer)
                {
                    if (kvp.Value == partHash)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    heldItemsByPlayer.Remove(keysToRemove[i]);
                }

                // 3. Удаление из списков движения физики
                localMovingItems.Remove(partHash);
                remoteMovingItems.Remove(partHash);
            }

            // Если объект передан напрямую, также убеждаемся в выключении локального трекинга если хэши различались
            if (partObj != null && SceneHasher.TryGetHash(partObj, out int exactHash) && exactHash != partHash)
            {
                localMovingItems.Remove(exactHash);
                remoteMovingItems.Remove(exactHash);
            }
        }

        #endregion

        #region Physics Authority & Interpolation

        /// <summary>
        /// Отслеживает предметы, которые мы бросили или толкнули, и передает их с частотой 40 Гц.
        /// </summary>
        private void UpdateLocalMovingPhysics()
        {
            if (localMovingItems.Count == 0) return;

            float now = Time.time;
            cachedLocalKeys.Clear();
            foreach (var k in localMovingItems.Keys) cachedLocalKeys.Add(k);

            for (int i = 0; i < cachedLocalKeys.Count; i++)
            {
                int hash = cachedLocalKeys[i];
                if (!localMovingItems.TryGetValue(hash, out var item)) continue;

                if (item.Object == null || item.Rigidbody == null)
                {
                    localMovingItems.Remove(hash);
                    continue;
                }

                float flightDuration = now - item.StartTime;

                // Защитный период полета: исключает преждевременное засыпание и зависание предмета в воздухе
                if (flightDuration > 1.2f)
                {
                    bool isSlow = item.Rigidbody.velocity.sqrMagnitude < 0.01f && item.Rigidbody.angularVelocity.sqrMagnitude < 0.03f;
                    if (isSlow)
                    {
                        item.StillTime += Time.deltaTime;
                    }
                    else
                    {
                        item.StillTime = 0f;
                    }

                    // Переходим в покой ТОЛЬКО если предмет реально лежал неподвижно 0.6 секунды или истек общий таймаут (10с)
                    bool shouldSleep = item.StillTime >= 0.6f || now >= item.StopTrackingTime;

                    if (shouldSleep)
                    {
                        // Запоминаем конечное положение перемещенного предмета для снимка мира
                        RecordMovedWorldItem(item.Hash, item.Object.transform.position, item.Object.transform.rotation);

                        // Отправляем финальный авторитетный пакет покоя
                        SendItemSync(item.Hash, item.Object.transform.position, item.Object.transform.rotation, true);
                        item.Rigidbody.isKinematic = false;
                        item.Rigidbody.useGravity = true;
                        if (item.Rigidbody.collisionDetectionMode == CollisionDetectionMode.Continuous)
                        {
                            item.Rigidbody.collisionDetectionMode = CollisionDetectionMode.Discrete;
                        }
                        item.Rigidbody.Sleep();
                        localMovingItems.Remove(hash);
                        continue;
                    }
                }

                // Передаем координаты с частотой 20 раз в секунду (каждые 50 мс, модель WreckMP)
                if (now >= item.NextSendTime)
                {
                    item.NextSendTime = now + 0.05f;
                    SendItemSync(item.Hash, item.Object.transform.position, item.Object.transform.rotation, false);
                }
            }
        }

        /// <summary>
        /// Плавная 60 FPS интерполяция для предметов, летящих по данным другого игрока.
        /// </summary>
        private void UpdateRemoteInterpolation()
        {
            if (remoteMovingItems.Count == 0) return;

            float dt = Time.deltaTime;
            float lerpSpeed = dt * 28f; // Высокая скорость реакции сглаживания
            float now = Time.time;

            cachedRemoteKeys.Clear();
            foreach (var k in remoteMovingItems.Keys) cachedRemoteKeys.Add(k);

            for (int i = 0; i < cachedRemoteKeys.Count; i++)
            {
                int hash = cachedRemoteKeys[i];
                if (!remoteMovingItems.TryGetValue(hash, out var tracker)) continue;

                if (tracker.Object == null)
                {
                    remoteMovingItems.Remove(hash);
                    continue;
                }

                // Таймаут безопасности: если от хоста/клиента долго нет пакетов (2.5 с)
                if (now - tracker.LastPacketTime > 2.5f)
                {
                    tracker.Object.transform.position = tracker.TargetPosition;
                    tracker.Object.transform.rotation = tracker.TargetRotation;
                    if (tracker.Rigidbody != null)
                    {
                        tracker.Rigidbody.isKinematic = false;
                        tracker.Rigidbody.useGravity = true;
                        tracker.Rigidbody.WakeUp();
                    }
                    RecordMovedWorldItem(hash, tracker.TargetPosition, tracker.TargetRotation);
                    remoteMovingItems.Remove(hash);
                    continue;
                }

                // Плавная интерполяция к целевой позиции без рывков
                tracker.Object.transform.position = Vector3.Lerp(
                    tracker.Object.transform.position,
                    tracker.TargetPosition,
                    lerpSpeed
                );
                tracker.Object.transform.rotation = Quaternion.Slerp(
                    tracker.Object.transform.rotation,
                    tracker.TargetRotation,
                    lerpSpeed
                );
            }
        }

        private void SendItemSync(int itemHash, Vector3 pos, Quaternion rot, bool isSleeping)
        {
            int myId = transport.IsServer ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.ItemSync))
            {
                packet.WriteInt32(itemHash);
                packet.WriteInt32(myId);
                packet.WriteSingle(pos.x);
                packet.WriteSingle(pos.y);
                packet.WriteSingle(pos.z);
                packet.WriteSingle(rot.x);
                packet.WriteSingle(rot.y);
                packet.WriteSingle(rot.z);
                packet.WriteSingle(rot.w);
                packet.WriteBoolean(isSleeping);
                transport.Send(packet);
            }
        }

        #endregion

        #region WreckMP Item Creation & Spawning Sync

        private static int spawnCounter = 0;

        public void OnItemCreated(Creator creator)
        {
            if (isApplyingRemoteSpawn) return;
            if (creator == null || creator.New == null) return;
            GameObject value = creator.New.Value;
            if (value == null) return;

            // Защита от дублирования одного и того же объекта из-за нескольких состояний FSM
            if (creator.LastProcessedObject == value) return;
            creator.LastProcessedObject = value;

            string id = (creator.NewItemID != null && !string.IsNullOrEmpty(creator.NewItemID.Value)) 
                        ? creator.NewItemID.Value 
                        : value.name;
            string uniqueId = id + "_" + (transport != null && transport.IsServer ? "H" : "C") + "_" + (++spawnCounter);
            int hash = SceneHasher.GetStableHash(uniqueId);

            SceneHasher.RegisterDynamicObject(hash, value);

            var rb = value.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
            }

            CheckCreatedObject(value, hash);

            // Если создание было запущено локально и это не пакет:
            if (creator.DoSync && !creator.IsShopbag)
            {
                Vector3 pos = value.transform.position;
                Quaternion rot = value.transform.rotation;
                Vector3 vel = rb != null ? rb.velocity : Vector3.zero;
                Vector3 angVel = rb != null ? rb.angularVelocity : Vector3.zero;

                // Отправляем пакет создания предмета второму игроку (работает и от хоста, и от клиента)
                SendSpawnItem(creator.Name, hash, id, pos, rot, vel, angVel);
                string who = (transport != null && transport.IsServer) ? "Хост" : "Клиент";
                OnLog?.Invoke($"[Спавн] {who} создал предмет: {creator.Name} (#{hash:X}, ID: {id}) в точке ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");

                // Добавляем созданный предмет в трекер активного движения, чтобы траектория падения
                // и окончательная точка приземления на пол синхронизировались 20 раз/сек и фиксировались идентично у обоих игроков!
                if (rb != null)
                {
                    localMovingItems[hash] = new LocalMovingTracker
                    {
                        Hash = hash,
                        Object = value,
                        Rigidbody = rb,
                        StartTime = Time.time,
                        StillTime = 0f,
                        NextSendTime = Time.time + 0.05f,
                        StopTrackingTime = Time.time + 8f
                    };
                }
            }

            creator.DoSync = true;
        }

        private void CheckCreatedObject(GameObject go, int hash)
        {
            if (go == null) return;
            string nameLower = go.name.ToLower();
            if (nameLower.Contains("beer") && nameLower.Contains("case"))
            {
                TrackBeerCase(go);
            }
            else if (nameLower.StartsWith("shopping bag"))
            {
                TrackShoppingBag(go);
            }
            ConsumableManager.Instance?.CheckCreatedConsumable(go);
        }

        private void SendSpawnItem(string creatorName, int hash, string id, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
        {
            using (var packet = new PacketWriter(PacketType.SpawnItem))
            {
                packet.WriteString(creatorName);
                packet.WriteInt32(hash);
                packet.WriteString(id);
                packet.WriteSingle(pos.x); packet.WriteSingle(pos.y); packet.WriteSingle(pos.z);
                packet.WriteSingle(rot.x); packet.WriteSingle(rot.y); packet.WriteSingle(rot.z); packet.WriteSingle(rot.w);
                packet.WriteSingle(vel.x); packet.WriteSingle(vel.y); packet.WriteSingle(vel.z);
                packet.WriteSingle(angVel.x); packet.WriteSingle(angVel.y); packet.WriteSingle(angVel.z);
                transport.Send(packet);
            }
        }

        private void HandleSpawnItem(PacketReader reader, int connectionId)
        {
            string creatorName = reader.ReadString();
            int itemHash = reader.ReadInt32();
            string itemId = reader.ReadString();
            float px = reader.ReadSingle(); float py = reader.ReadSingle(); float pz = reader.ReadSingle();
            float rx = reader.ReadSingle(); float ry = reader.ReadSingle(); float rz = reader.ReadSingle(); float rw = reader.ReadSingle();
            float vx = reader.ReadSingle(); float vy = reader.ReadSingle(); float vz = reader.ReadSingle();
            float ax = reader.ReadSingle(); float ay = reader.ReadSingle(); float az = reader.ReadSingle();

            Vector3 pos = new Vector3(px, py, pz);
            Quaternion rot = new Quaternion(rx, ry, rz, rw);
            Vector3 vel = new Vector3(vx, vy, vz);
            Vector3 angVel = new Vector3(ax, ay, az);

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.SpawnItem))
                {
                    relay.WriteString(creatorName);
                    relay.WriteInt32(itemHash);
                    relay.WriteString(itemId);
                    relay.WriteSingle(px); relay.WriteSingle(py); relay.WriteSingle(pz);
                    relay.WriteSingle(rx); relay.WriteSingle(ry); relay.WriteSingle(rz); relay.WriteSingle(rw);
                    relay.WriteSingle(vx); relay.WriteSingle(vy); relay.WriteSingle(vz);
                    relay.WriteSingle(ax); relay.WriteSingle(ay); relay.WriteSingle(az);
                    transport.Send(relay, connectionId);
                }
            }

            // Дедупликация: если предмет с таким хэшем уже существует в мире, выходим!
            if (SceneHasher.FindObjectByHash(itemHash) != null)
            {
                return;
            }

            bool spawned = false;
            for (int i = 0; i < creators.Count; i++)
            {
                if (string.Equals(creators[i].Name, creatorName, StringComparison.OrdinalIgnoreCase))
                {
                    isApplyingRemoteSpawn = true;
                    lastRemoteSpawnPos = pos;
                    lastRemoteSpawnRot = rot;
                    lastRemoteSpawnVel = vel;
                    lastRemoteSpawnAngVel = angVel;
                    lastRemoteSpawnHash = itemHash;
                    try
                    {
                        creators[i].DoSync = false;

                        if (creators[i].SpawnItem != null)
                        {
                            creators[i].Fsm.SendEvent(creators[i].SpawnItem.Name);
                        }

                        // Гарантия позиционирования и регистрации объекта
                        if (creators[i].New != null && creators[i].New.Value != null)
                        {
                            var newObj = creators[i].New.Value;
                            newObj.transform.position = pos;
                            newObj.transform.rotation = rot;
                            var rb = newObj.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                rb.isKinematic = false;
                                rb.useGravity = true;
                                rb.velocity = vel;
                                rb.angularVelocity = angVel;
                                rb.WakeUp();
                                Environment.PhysicsOptimizer.Instance?.OptimizeRigidbody(rb, true);
                            }
                            SceneHasher.RegisterDynamicObject(itemHash, newObj);
                            CheckCreatedObject(newObj, itemHash);

                            // Регистрируем в remoteMovingItems, чтобы траектория падения следовала за авторитетом спавнера
                            remoteMovingItems[itemHash] = new RemoteInterpolatedTracker
                            {
                                Hash = itemHash,
                                Object = newObj,
                                Rigidbody = rb,
                                TargetPosition = pos,
                                TargetRotation = rot,
                                LastPacketTime = Time.time
                            };
                        }

                        spawned = true;
                        OnLog?.Invoke($"[Спавн] Создан сетевой предмет: {creatorName} (#{itemHash:X}) в точке ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[Спавн Ошибка] Не удалось создать {creatorName}: {ex.Message}");
                    }
                    finally
                    {
                        isApplyingRemoteSpawn = false;
                    }
                    break;
                }
            }

            // Fallback: спавн предметов через CheatBox или внешние моды
            if (!spawned)
            {
                try
                {
                    var template = FindTemplate(creatorName);
                    if (template != null)
                    {
                        var newObj = UnityEngine.Object.Instantiate(template, pos, rot) as GameObject;
                        if (newObj != null)
                        {
                            newObj.name = itemId;
                            newObj.SetActive(true);

                            var rb = newObj.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                rb.isKinematic = false;
                                rb.useGravity = true;
                                rb.velocity = vel;
                                rb.angularVelocity = angVel;
                                rb.WakeUp();
                                Environment.PhysicsOptimizer.Instance?.OptimizeRigidbody(rb, true);
                            }

                            SceneHasher.RegisterDynamicObject(itemHash, newObj);
                            CheckCreatedObject(newObj, itemHash);

                            remoteMovingItems[itemHash] = new RemoteInterpolatedTracker
                            {
                                Hash = itemHash,
                                Object = newObj,
                                Rigidbody = rb,
                                TargetPosition = pos,
                                TargetRotation = rot,
                                LastPacketTime = Time.time
                            };

                            OnLog?.Invoke($"[Спавн Читом] Создан сетевой предмет читбокса: {creatorName} (#{itemHash:X}) в точке ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                        }
                    }
                    else
                    {
                        OnLog?.Invoke($"[Спавн Ошибка] Не найден шаблон предмета читбокса: {creatorName}");
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[Спавн Ошибка] Исключение при спавне читом {creatorName}: {ex.Message}");
                }
            }
        }

        private GameObject FindTemplate(string templateName)
        {
            if (string.IsNullOrEmpty(templateName)) return null;

            if (cachedTemplates.TryGetValue(templateName, out var cached) && cached != null)
            {
                return cached;
            }

            // 1. Поиск прямого объекта на сцене
            GameObject direct = GameObject.Find(templateName) ?? GameObject.Find(templateName + "(Clone)");
            if (direct != null)
            {
                cachedTemplates[templateName] = direct;
                return direct;
            }

            // 2. Поиск в Creators (если один из PlayMaker FSM хранит префаб)
            for (int i = 0; i < creators.Count; i++)
            {
                if (creators[i].New != null && creators[i].New.Value != null)
                {
                    var val = creators[i].New.Value;
                    if (val.name == templateName || val.name == templateName + "(Clone)")
                    {
                        cachedTemplates[templateName] = val;
                        return val;
                    }
                }
            }

            // 3. Поиск среди всех объектов в памяти игры (включая префабы Resources и Spawner)
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                var candidate = all[i];
                if (candidate == null) continue;
                if (candidate.name == templateName || candidate.name == templateName + "(Clone)")
                {
                    cachedTemplates[templateName] = candidate;
                    return candidate;
                }
            }

            // 4. Поиск по началу имени (fallback)
            for (int i = 0; i < all.Length; i++)
            {
                var candidate = all[i];
                if (candidate == null) continue;
                if (candidate.name.StartsWith(templateName, StringComparison.OrdinalIgnoreCase))
                {
                    cachedTemplates[templateName] = candidate;
                    return candidate;
                }
            }

            return null;
        }

        private void ScanCheatSpawnedItems()
        {
            try
            {
                Vector3 playerPos = playerManager != null ? playerManager.LastKnownLocalPosition : Vector3.zero;
                if (playerPos == Vector3.zero)
                {
                    var p = GameObject.Find("PLAYER");
                    if (p != null) playerPos = p.transform.position;
                }
                if (playerPos == Vector3.zero) return;

                var cols = Physics.OverlapSphere(playerPos, 10f);
                var seenRbs = new HashSet<Rigidbody>();

                for (int i = 0; i < cols.Length; i++)
                {
                    var col = cols[i];
                    if (col == null) continue;
                    var rb = col.attachedRigidbody;
                    if (rb == null || !seenRbs.Add(rb)) continue;
                    if (rb.isKinematic || rb.mass >= 150f) continue;
                    var go = rb.gameObject;
                    if (!go.activeInHierarchy) continue;
                    if (!go.name.EndsWith("(Clone)")) continue;

                    // Исключаем куклу игрока и сам транспорт
                    if (go.transform.root.name.StartsWith("PLAYER")) continue;
                    string rootName = go.transform.root.name;
                    if (rootName.StartsWith("SATSUMA") || rootName.StartsWith("HAYOSIKO") ||
                        rootName.StartsWith("RCO_RUSCKO") || rootName.StartsWith("FERNDALE") ||
                        rootName.StartsWith("KEKMET") || rootName.StartsWith("GIFU") ||
                        rootName.StartsWith("JONNEZ") || rootName.StartsWith("FLATBED") ||
                        rootName.StartsWith("BOAT")) continue;

                    int hash;
                    if (!SceneHasher.TryGetHash(go, out hash))
                    {
                        // Новый предмет, заспавненный читом или модом!
                        string templateName = go.name.Replace("(Clone)", "").Trim();
                        string uniqueId = templateName + "_" + (transport.IsServer ? "H" : "C") + "_" + (++spawnCounter);
                        hash = SceneHasher.GetStableHash(uniqueId);
                        SceneHasher.RegisterDynamicObject(hash, go);

                        CheckCreatedObject(go, hash);

                        Vector3 pos = go.transform.position;
                        Quaternion rot = go.transform.rotation;
                        Vector3 vel = rb.velocity;
                        Vector3 angVel = rb.angularVelocity;

                        SendSpawnItem(templateName, hash, go.name, pos, rot, vel, angVel);

                        localMovingItems[hash] = new LocalMovingTracker
                        {
                            Hash = hash,
                            Object = go,
                            Rigidbody = rb,
                            StartTime = Time.time,
                            StillTime = 0f,
                            NextSendTime = Time.time + 0.05f,
                            StopTrackingTime = Time.time + 8f
                        };

                        string who = transport.IsServer ? "Хост" : "Клиент";
                        OnLog?.Invoke($"[Спавн Читом] {who} заспавнил предмет: {templateName} (#{hash:X}, {go.name}) в точке ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Спавн Читом Ошибка] {ex.Message}");
            }
        }

        #endregion

        #region Shopping Bag Sync (WreckMP Model)

        private void FindAllInitialShoppingBags()
        {
            try
            {
                var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
                for (int i = 0; i < rbs.Length; i++)
                {
                    if (rbs[i] == null || rbs[i].gameObject == null) continue;
                    GameObject go = rbs[i].gameObject;
                    if (go.name.StartsWith("shopping bag"))
                    {
                        TrackShoppingBag(go);
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ShoppingBag Init Error] {ex.Message}");
            }
        }

        private void SanitizeDistortedItemScales()
        {
            try
            {
                if (Time.time < nextSanitizeTime) return;
                nextSanitizeTime = Time.time + 5.0f;

                // 1. Исправляем зарегистрированные объекты в SceneHasher (пакеты, пиво, еда и т.д.)
                foreach (var go in SceneHasher.GetAllRegisteredObjects())
                {
                    if (go == null) continue;
                    if (go.transform.localScale.sqrMagnitude > 3.5f || go.transform.localScale.sqrMagnitude < 0.2f)
                    {
                        string n = go.name;
                        if (n.Contains("itemx") || n.Contains("beer") || n.Contains("shopping") || n.Contains("(Clone)"))
                        {
                            go.transform.localScale = Vector3.one;
                        }
                    }
                }

                // 2. Исправляем физические предметы в радиусе 15 метров
                Vector3 playerPos = playerManager.LastKnownLocalPosition;
                if (playerPos != Vector3.zero)
                {
                    var colliders = Physics.OverlapSphere(playerPos, 15f);
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        var col = colliders[i];
                        if (col == null) continue;
                        GameObject go = col.gameObject;
                        if (go == null) continue;
                        if (go.transform.localScale.sqrMagnitude > 3.5f || go.transform.localScale.sqrMagnitude < 0.2f)
                        {
                            string n = go.name;
                            if (n.Contains("itemx") || n.Contains("beer") || n.Contains("shopping bag") || n.Contains("(Clone)"))
                            {
                                go.transform.localScale = Vector3.one;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void ScanNearbyShoppingBags()
        {
            try
            {
                Vector3 playerPos = playerManager.LastKnownLocalPosition;
                if (playerPos == Vector3.zero) return;

                var colliders = Physics.OverlapSphere(playerPos, 15f);
                for (int i = 0; i < colliders.Length; i++)
                {
                    var col = colliders[i];
                    if (col == null) continue;
                    GameObject go = col.gameObject;
                    if (go != null && go.name.StartsWith("shopping bag"))
                    {
                        TrackShoppingBag(go);
                    }
                }
            }
            catch { }
        }

        private void TrackShoppingBag(GameObject bag)
        {
            if (bag == null) return;
            int bagHash = SceneHasher.GetHash(bag);
            if (unpackedBags.Contains(bagHash)) return;

            if (trackedBags.TryGetValue(bagHash, out var existing) && existing.IsHooked) return;

            PlayMakerFSM fsm = bag.GetPlayMaker("Use");
            if (fsm == null) return;

            FsmGameObject psVar = fsm.FsmVariables.FindFsmGameObject("ProductSpawner");
            if (psVar == null) return;
            if (psVar.Value == null)
            {
                GameObject pnl = GameObject.Find("ProductNameList");
                if (pnl == null)
                {
                    GameObject spawner = GameObject.Find("Spawner");
                    if (spawner != null)
                    {
                        Transform t = spawner.transform.Find("ProductNameList");
                        if (t != null) pnl = t.gameObject;
                    }
                }
                if (pnl != null)
                {
                    psVar.Value = pnl;
                }
                else
                {
                    return;
                }
            }

            PlayMakerFSM psLogic = psVar.Value.GetPlayMaker("Logic");
            if (psLogic == null) return;

            FsmGameObject currentBagVar = psLogic.FsmVariables.FindFsmGameObject("CurrentBag");

            var proxies = bag.GetComponents<PlayMakerArrayListProxy>();
            PlayMakerArrayListProxy itemsProxy = proxies.FirstOrDefault(x => x.referenceName == "Items");
            PlayMakerArrayListProxy sprayProxy = proxies.FirstOrDefault(x => x.referenceName == "Spraycans");

            var tracked = new TrackedShoppingBag
            {
                Bag = bag,
                Hash = bagHash,
                Fsm = fsm,
                ProductSpawnerLogic = psLogic,
                CurrentBagVar = currentBagVar,
                ItemsProxy = itemsProxy,
                SprayProxy = sprayProxy,
                LastPosition = bag.transform.position
            };

            trackedBags[bagHash] = tracked;
            HookShoppingBagFsm(tracked);
        }

        private void HookShoppingBagFsm(TrackedShoppingBag tracked)
        {
            if (tracked.IsHooked || tracked.Fsm == null) return;
            int bagHash = tracked.Hash;
            GameObject bag = tracked.Bag;
            PlayMakerFSM bagFsm = tracked.Fsm;

            // 1. Убеждаемся, что нативное действие спавна всегда активно (нативно спавнит тот игрок, кто открывает)
            UpdateBagNativeSpawnerState(bagFsm);

            // 2. Хук на "Spawn all" (распаковать все)
            bagFsm.InsertAction("Spawn all", () =>
            {
                LastBagUnpackTime = Time.time;
                if (tracked.CurrentBagVar != null)
                {
                    tracked.CurrentBagVar.Value = bag;
                }

                string who = transport.IsServer ? "Хост" : "Клиент";
                OnLog?.Invoke($"[Пакет] {who} распаковывает пакет #{bagHash:X}");
            }, 0, false);

            // 3. Хук на "Spawn one" (извлечь один предмет)
            bagFsm.InsertAction("Spawn one", () =>
            {
                LastBagUnpackTime = Time.time;
                if (tracked.CurrentBagVar != null)
                {
                    tracked.CurrentBagVar.Value = bag;
                }

                string who = transport.IsServer ? "Хост" : "Клиент";
                OnLog?.Invoke($"[Пакет] {who} извлекает предмет из пакета #{bagHash:X}");
            }, 0, false);

            // 4. Хук на реальное завершение опустошения пакета (когда нативный spawner отправил GARBAGE)
            if (bagFsm.HasState("Is garbage"))
            {
                bagFsm.InsertAction("Is garbage", () =>
                {
                    Vector3 bagPos = bag != null ? bag.transform.position : Vector3.zero;
                    SendShoppingBagAction(bagHash, ShoppingBagActionType.Destroy, bagPos);
                    unpackedBags.Add(bagHash);
                    trackedBags.Remove(bagHash);
                    OnLog?.Invoke($"[Пакет] Пакет #{bagHash:X} полностью опустошен и превращен в мусор.");
                }, 0, false);
            }

            tracked.IsHooked = true;
        }

        private void UpdateBagNativeSpawnerState(PlayMakerFSM bagFsm)
        {
            if (bagFsm == null) return;
            var sSpawnAll = bagFsm.GetState("Spawn all");
            if (sSpawnAll != null)
            {
                for (int a = 0; a < sSpawnAll.Actions.Length; a++) sSpawnAll.Actions[a].Enabled = true;
            }

            var sSpawnOne = bagFsm.GetState("Spawn one");
            if (sSpawnOne != null)
            {
                for (int a = 0; a < sSpawnOne.Actions.Length; a++) sSpawnOne.Actions[a].Enabled = true;
            }
        }

        private void SendShoppingBagAction(int bagHash, ShoppingBagActionType action, Vector3 pos = default(Vector3))
        {
            using (var packet = new PacketWriter(PacketType.ShoppingBagAction))
            {
                packet.WriteInt32(bagHash);
                packet.WriteByte((byte)action);
                packet.WriteSingle(pos.x);
                packet.WriteSingle(pos.y);
                packet.WriteSingle(pos.z);
                transport.Send(packet);
            }
        }

        private void SendShoppingBagUpdateList(int bagHash, PlayMakerArrayListProxy items, PlayMakerArrayListProxy sprays)
        {
            using (var packet = new PacketWriter(PacketType.ShoppingBagAction))
            {
                packet.WriteInt32(bagHash);
                packet.WriteByte((byte)ShoppingBagActionType.UpdateList);

                int itemsCount = (items != null && items._arrayList != null) ? items._arrayList.Count : 0;
                int spraysCount = (sprays != null && sprays._arrayList != null) ? sprays._arrayList.Count : 0;

                packet.WriteInt32(itemsCount);
                for (int i = 0; i < itemsCount; i++)
                {
                    packet.WriteInt32((int)items._arrayList[i]);
                }

                packet.WriteInt32(spraysCount);
                for (int i = 0; i < spraysCount; i++)
                {
                    packet.WriteInt32((int)sprays._arrayList[i]);
                }

                transport.Send(packet);
            }
        }

        private TrackedShoppingBag FindTrackedShoppingBag(int bagHash, Vector3 bagPos)
        {
            if (trackedBags.TryGetValue(bagHash, out var tracked) && tracked != null && tracked.Bag != null)
            {
                return tracked;
            }

            // Fallback 1: Поиск по близости координат (до 5 метров)
            if (bagPos != Vector3.zero && trackedBags.Count > 0)
            {
                float minDist = float.MaxValue;
                TrackedShoppingBag nearest = null;
                foreach (var tb in trackedBags.Values)
                {
                    if (tb != null && tb.Bag != null)
                    {
                        float d = (tb.Bag.transform.position - bagPos).sqrMagnitude;
                        if (d < minDist && d < 25f)
                        {
                            minDist = d;
                            nearest = tb;
                        }
                    }
                }
                if (nearest != null) return nearest;
            }

            // Fallback 2: Поиск в сцене по имени "shopping bag" в радиусе 4 метров
            if (bagPos != Vector3.zero)
            {
                var colliders = Physics.OverlapSphere(bagPos, 4f);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] != null && colliders[i].gameObject != null && colliders[i].gameObject.name.StartsWith("shopping bag"))
                    {
                        TrackShoppingBag(colliders[i].gameObject);
                        int hash = SceneHasher.GetHash(colliders[i].gameObject);
                        if (trackedBags.TryGetValue(hash, out var newlyTracked))
                        {
                            return newlyTracked;
                        }
                    }
                }
            }

            return null;
        }

        private void HandleShoppingBagAction(PacketReader reader, int connectionId)
        {
            int bagHash = reader.ReadInt32();
            ShoppingBagActionType action = (ShoppingBagActionType)reader.ReadByte();
            Vector3 bagPos = Vector3.zero;
            if (reader.UnreadBytes >= 12)
            {
                bagPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            // Релей для клиентов, если пакет пришел на хост от клиента
            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.ShoppingBagAction))
                {
                    relay.WriteInt32(bagHash);
                    relay.WriteByte((byte)action);
                    relay.WriteSingle(bagPos.x); relay.WriteSingle(bagPos.y); relay.WriteSingle(bagPos.z);
                    transport.Send(relay, connectionId);
                }
            }

            TrackedShoppingBag tracked = FindTrackedShoppingBag(bagHash, bagPos);

            if (action == ShoppingBagActionType.Destroy)
            {
                LastBagUnpackTime = Time.time;
                foreach (var kvp in heldItemsByPlayer)
                {
                    if (kvp.Value == bagHash)
                    {
                        RemotePuppet p = playerManager.GetPuppet(kvp.Key);
                        if (p != null) p.ReleaseItem(null);
                    }
                }

                if (tracked != null && tracked.Bag != null)
                {
                    tracked.Fsm.SendEvent("GARBAGE");
                    unpackedBags.Add(tracked.Hash);
                    trackedBags.Remove(tracked.Hash);
                    OnLog?.Invoke($"[Пакет] Пакет #{tracked.Hash:X} превращен в мусор.");
                }
                else
                {
                    GameObject bag = SceneHasher.FindObjectByHash(bagHash);
                    if (bag != null)
                    {
                        var fsm = bag.GetPlayMaker("Use");
                        if (fsm != null) fsm.SendEvent("GARBAGE");
                    }
                }
            }
            else if (action == ShoppingBagActionType.UpdateList)
            {
                int itemsCount = reader.ReadInt32();
                int[] items = new int[itemsCount];
                for (int i = 0; i < itemsCount; i++) items[i] = reader.ReadInt32();

                int spraysCount = reader.ReadInt32();
                int[] sprays = new int[spraysCount];
                for (int i = 0; i < spraysCount; i++) sprays[i] = reader.ReadInt32();

                if (tracked != null && tracked.Bag != null)
                {
                    if (tracked.ItemsProxy != null && tracked.ItemsProxy._arrayList != null)
                    {
                        tracked.ItemsProxy._arrayList.Clear();
                        for (int i = 0; i < itemsCount; i++)
                        {
                            tracked.ItemsProxy._arrayList.Add(items[i]);
                        }
                    }
                    if (tracked.SprayProxy != null && tracked.SprayProxy._arrayList != null)
                    {
                        tracked.SprayProxy._arrayList.Clear();
                        for (int i = 0; i < spraysCount; i++)
                        {
                            tracked.SprayProxy._arrayList.Add(sprays[i]);
                        }
                    }
                }
            }
        }

        #endregion

        // ==============================================================================================
        // === LOCKED: PERMANENT BEER CASE SYNC (DO NOT MODIFY UNDER ANY CIRCUMSTANCE - VERIFIED WORKING) ===
        // ==============================================================================================
        #region Beer Case Sync (Permanent Verified Model)

        private void FindAllInitialBeerCases()
        {
            try
            {
                var rbs = Resources.FindObjectsOfTypeAll<Rigidbody>();
                for (int i = 0; i < rbs.Length; i++)
                {
                    if (rbs[i] == null || rbs[i].gameObject == null) continue;
                    GameObject go = rbs[i].gameObject;
                    string nameLower = go.name.ToLower();
                    if (nameLower.Contains("beer") && nameLower.Contains("case"))
                    {
                        TrackBeerCase(go);
                    }
                }

                // Гарантированный поиск дефолтного ящика в доме
                GameObject defaultBeerCase = GameObject.Find("ITEMS/beercase0") ?? GameObject.Find("beercase0");
                if (defaultBeerCase != null)
                {
                    TrackBeerCase(defaultBeerCase);
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[BeerCase Init Error] {ex.Message}");
            }
        }

        private void ScanNearbyBeerCases()
        {
            try
            {
                Vector3 playerPos = playerManager.LastKnownLocalPosition;
                if (playerPos == Vector3.zero) return;

                var colliders = Physics.OverlapSphere(playerPos, 15f);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] == null) continue;
                    GameObject go = colliders[i].gameObject;
                    if (go != null)
                    {
                        string nameLower = go.name.ToLower();
                        if (nameLower.Contains("beer") && nameLower.Contains("case"))
                        {
                            TrackBeerCase(go);
                        }
                    }
                }
            }
            catch { }
        }

        private void TrackBeerCase(GameObject beerCase)
        {
            if (beerCase == null) return;
            string nameLower = beerCase.name.ToLower();
            if (!nameLower.Contains("beer") || !nameLower.Contains("case")) return;

            int caseHash = SceneHasher.GetHash(beerCase);

            if (!trackedBeerCases.TryGetValue(caseHash, out var tracked))
            {
                PlayMakerFSM fsm = null;
                foreach (var f in beerCase.GetComponents<PlayMakerFSM>())
                {
                    if (f.FsmName == "Use")
                    {
                        fsm = f;
                        break;
                    }
                }

                Transform bottles = beerCase.transform.Find("Bottles") ?? beerCase.transform.Find("Bottle");
                FsmInt destroyedVar = fsm != null ? fsm.FsmVariables.FindFsmInt("DestroyedBottles") : null;

                tracked = new TrackedBeerCase
                {
                    CaseObject = beerCase,
                    Hash = caseHash,
                    Fsm = fsm,
                    BottlesParent = bottles,
                    DestroyedBottlesVar = destroyedVar
                };
                trackedBeerCases[caseHash] = tracked;

                HookBeerCaseFsm(tracked);
            }
            else
            {
                if (tracked.CaseObject != null && !tracked.IsHooked)
                {
                    HookBeerCaseFsm(tracked);
                }
            }
        }

        private void HookBeerCaseFsm(TrackedBeerCase tracked)
        {
            if (tracked.IsHooked || tracked.Fsm == null) return;

            tracked.Fsm.Fsm.InitData();
            int caseHash = tracked.Hash;

            tracked.Fsm.InsertAction("Remove bottle", () =>
            {
                if (isRemoteBeerDrink.Contains(caseHash)) return;

                int remaining = (tracked.BottlesParent != null) ? tracked.BottlesParent.childCount - 1 : 0;
                int destroyed = (tracked.DestroyedBottlesVar != null) ? tracked.DestroyedBottlesVar.Value + 1 : (24 - remaining);

                SendBeerCaseDrink(caseHash, remaining, destroyed);
                OnLog?.Invoke($"[Пиво] Выпито пиво из ящика #{caseHash:X} (осталось: {remaining})");
            }, 0, false);

            tracked.IsHooked = true;
        }

        private void SendBeerCaseDrink(int caseHash, int remaining, int destroyed)
        {
            using (var packet = new PacketWriter(PacketType.BeerCaseDrink))
            {
                packet.WriteInt32(caseHash);
                packet.WriteInt32(remaining);
                packet.WriteInt32(destroyed);
                transport.Send(packet);
            }
        }

        private void HandleBeerCaseDrink(PacketReader reader, int connectionId)
        {
            int caseHash = reader.ReadInt32();
            int remaining = reader.ReadInt32();
            int destroyed = reader.ReadInt32();

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.BeerCaseDrink))
                {
                    relay.WriteInt32(caseHash);
                    relay.WriteInt32(remaining);
                    relay.WriteInt32(destroyed);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemoteBeerCaseDrink(caseHash, remaining, destroyed);
        }

        private void ApplyRemoteBeerCaseDrink(int caseHash, int remaining, int destroyed)
        {
            isRemoteBeerDrink.Add(caseHash);
            try
            {
                TrackedBeerCase tracked;
                if (!trackedBeerCases.TryGetValue(caseHash, out tracked) || tracked.CaseObject == null)
                {
                    GameObject go = SceneHasher.FindObjectByHash(caseHash);
                    if (go == null)
                    {
                        if (trackedBeerCases.Count == 1)
                        {
                            foreach (var kvp in trackedBeerCases)
                            {
                                if (kvp.Value != null && kvp.Value.CaseObject != null)
                                {
                                    tracked = kvp.Value;
                                    break;
                                }
                            }
                        }
                    }

                    if (tracked == null && go == null)
                    {
                        GameObject defaultBeer = GameObject.Find("ITEMS/beercase0") ?? GameObject.Find("beercase0");
                        if (defaultBeer != null)
                        {
                            go = defaultBeer;
                        }
                    }

                    if (tracked == null && go != null)
                    {
                        TrackBeerCase(go);
                        trackedBeerCases.TryGetValue(caseHash, out tracked);
                        if (tracked == null && trackedBeerCases.Count > 0)
                        {
                            foreach (var kvp in trackedBeerCases)
                            {
                                if (kvp.Value != null && kvp.Value.CaseObject != null)
                                {
                                    tracked = kvp.Value;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (tracked != null && tracked.CaseObject != null)
                {
                    // 1. Нативный вызов MSC SUSKI (звук распития пива, анимация и уменьшение счетчика)
                    if (tracked.Fsm != null)
                    {
                        tracked.Fsm.SendEvent("SUSKI");
                    }

                    // 2. Гарантированное удаление бутылки из иерархии Transform
                    if (tracked.BottlesParent == null)
                    {
                        tracked.BottlesParent = tracked.CaseObject.transform.Find("Bottles") ?? tracked.CaseObject.transform.Find("Bottle");
                    }

                    if (tracked.BottlesParent != null)
                    {
                        while (tracked.BottlesParent.childCount > remaining && tracked.BottlesParent.childCount > 0)
                        {
                            Transform lastBottle = tracked.BottlesParent.GetChild(tracked.BottlesParent.childCount - 1);
                            if (lastBottle != null)
                            {
                                UnityEngine.Object.Destroy(lastBottle.gameObject);
                            }
                        }
                    }

                    if (tracked.DestroyedBottlesVar != null)
                    {
                        tracked.DestroyedBottlesVar.Value = destroyed;
                    }

                    OnLog?.Invoke($"[Пиво] Другой игрок выпил пиво из ящика #{caseHash:X} (осталось: {remaining})");
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[BeerCase Apply Error] {ex.Message}");
            }
            finally
            {
                isRemoteBeerDrink.Remove(caseHash);
            }
        }

        #endregion
        // ==============================================================================================
        // === END LOCKED: PERMANENT BEER CASE SYNC ===
        // ==============================================================================================


        #region Network Handlers

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.ItemPickUp:
                    HandleItemPickUp(reader, connectionId);
                    break;

                case PacketType.ItemDrop:
                    HandleItemDrop(reader, connectionId);
                    break;

                case PacketType.ItemSync:
                    HandleItemSync(reader, connectionId);
                    break;

                case PacketType.ContainerUnpack:
                    HandleContainerUnpack(reader, connectionId);
                    break;

                case PacketType.RequestItemSnapshot:
                    HandleRequestItemSnapshot(reader, connectionId);
                    break;

                case PacketType.ItemSnapshot:
                    HandleItemSnapshot(reader);
                    break;

                case PacketType.BeerCaseDrink:
                    HandleBeerCaseDrink(reader, connectionId);
                    Player.PlayerManager.Instance?.TriggerRemoteDrinkBeer(connectionId);
                    break;

                case PacketType.SpawnItem:
                    HandleSpawnItem(reader, connectionId);
                    break;

                case PacketType.ShoppingBagAction:
                    HandleShoppingBagAction(reader, connectionId);
                    break;
            }
        }

        private void HandleItemPickUp(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            string itemName = (reader.RemainingBytes > 0) ? reader.ReadString() : "";

            int myId = transport.IsServer ? 0 : 1;
            if (playerId == myId) return;

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.ItemPickUp))
                {
                    relay.WriteInt32(itemHash);
                    relay.WriteInt32(playerId);
                    relay.WriteString(itemName);
                    transport.Send(relay, connectionId);
                }
            }

            heldItemsByPlayer[playerId] = itemHash;
            localMovingItems.Remove(itemHash);
            remoteMovingItems.Remove(itemHash);

            GameObject item = SceneHasher.FindObjectByHash(itemHash);
            if (item == null && !string.IsNullOrEmpty(itemName) && itemName.StartsWith("JONNEZ"))
            {
                var jonnezVeh = VehiclePhysicsManager.Instance != null ? VehiclePhysicsManager.Instance.GetVehicle(6) : null;
                item = (jonnezVeh != null && jonnezVeh.GameObject != null) 
                    ? jonnezVeh.GameObject 
                    : (GameObject.Find("JONNEZ ES(Clone)") ?? GameObject.Find("JONNEZ ES"));
                if (item != null)
                {
                    SceneHasher.RegisterDynamicObject(itemHash, item);
                }
            }

            if (item == null)
            {
                // Резервный поиск предмета около позиции игрока (гарантирует подхват пакетов с покупками)
                RemotePuppet puppet = playerManager.GetPuppet(playerId);
                Vector3 searchPos = (puppet != null && puppet.RootObject != null) ? puppet.RootObject.transform.position : Vector3.zero;
                if (searchPos != Vector3.zero)
                {
                    var colliders = Physics.OverlapSphere(searchPos, 4.0f);
                    for (int i = 0; i < colliders.Length; i++)
                    {
                        if (colliders[i] == null || colliders[i].gameObject == null) continue;
                        GameObject candidate = colliders[i].gameObject;
                        bool match = false;
                        string rootName = (candidate.transform.root != null) ? candidate.transform.root.name : "";
                        if (!string.IsNullOrEmpty(itemName))
                        {
                            if (candidate.name.StartsWith(itemName) || itemName.StartsWith(candidate.name) ||
                                (!string.IsNullOrEmpty(rootName) && (rootName.StartsWith(itemName) || itemName.StartsWith(rootName))))
                            {
                                match = true;
                                if (!string.IsNullOrEmpty(rootName) && rootName.StartsWith("JONNEZ"))
                                {
                                    candidate = candidate.transform.root.gameObject;
                                }
                            }
                        }
                        else
                        {
                            if (candidate.name.StartsWith("shopping bag") || candidate.name.StartsWith("block"))
                            {
                                match = true;
                            }
                        }

                        if (match)
                        {
                            item = candidate;
                            SceneHasher.RegisterDynamicObject(itemHash, item);
                            break;
                        }
                    }
                }
            }

            if (item != null)
            {
                if (item.name.StartsWith("JONNEZ"))
                {
                    var jColl = item.transform.Find("Colliders/Coll");
                    if (jColl != null) jColl.gameObject.SetActive(false);
                    VehiclePhysicsManager.Instance?.SetVehicleCarriedRemotely(item, true, playerId);
                }

                RemotePuppet puppet = playerManager.GetPuppet(playerId);
                if (puppet != null)
                {
                    puppet.HoldItem(item);
                    string who = (playerId == 0) ? "Хост" : $"Игрок #{playerId}";
                    OnLog?.Invoke($"[Сеть] {who} взял в руки: {item.name}");
                }
            }
        }

        private void HandleItemDrop(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            float vx = reader.ReadSingle();
            float vy = reader.ReadSingle();
            float vz = reader.ReadSingle();
            float avx = reader.ReadSingle();
            float avy = reader.ReadSingle();
            float avz = reader.ReadSingle();

            int myId = transport.IsServer ? 0 : 1;
            if (playerId == myId) return;

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.ItemDrop))
                {
                    relay.WriteInt32(itemHash);
                    relay.WriteInt32(playerId);
                    relay.WriteSingle(px); relay.WriteSingle(py); relay.WriteSingle(pz);
                    relay.WriteSingle(rx); relay.WriteSingle(ry); relay.WriteSingle(rz); relay.WriteSingle(rw);
                    relay.WriteSingle(vx); relay.WriteSingle(vy); relay.WriteSingle(vz);
                    relay.WriteSingle(avx); relay.WriteSingle(avy); relay.WriteSingle(avz);
                    transport.Send(relay, connectionId);
                }
            }

            heldItemsByPlayer.Remove(playerId);
            localMovingItems.Remove(itemHash);

            RemotePuppet puppet = playerManager.GetPuppet(playerId);
            if (puppet != null)
            {
                puppet.DropItem();
            }

            GameObject item = SceneHasher.FindObjectByHash(itemHash);
            if (item == null && VehiclePhysicsManager.Instance != null)
            {
                var jonnezVeh = VehiclePhysicsManager.Instance.GetVehicle(6);
                if (jonnezVeh != null && jonnezVeh.GameObject != null && jonnezVeh.IsBeingCarriedRemotely)
                {
                    item = jonnezVeh.GameObject;
                    SceneHasher.RegisterDynamicObject(itemHash, item);
                }
            }

            if (item == null)
            {
                Vector3 dropPos = new Vector3(px, py, pz);
                var colliders = Physics.OverlapSphere(dropPos, 3.5f);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] == null || colliders[i].gameObject == null) continue;
                    GameObject candidate = colliders[i].gameObject;
                    string rootName = (candidate.transform.root != null) ? candidate.transform.root.name : "";
                    if (candidate.name.StartsWith("shopping bag") || candidate.name.StartsWith("block") ||
                        candidate.name.StartsWith("JONNEZ") || rootName.StartsWith("JONNEZ"))
                    {
                        item = rootName.StartsWith("JONNEZ") ? candidate.transform.root.gameObject : candidate;
                        SceneHasher.RegisterDynamicObject(itemHash, item);
                        break;
                    }
                }
            }

            if (item != null)
            {
                bool isJonnez = item.name.StartsWith("JONNEZ");
                if (isJonnez)
                {
                    var jColl = item.transform.Find("Colliders/Coll");
                    if (jColl != null) jColl.gameObject.SetActive(true);
                    VehiclePhysicsManager.Instance?.SetVehicleCarriedRemotely(item, false, playerId);
                }

                if (puppet != null)
                {
                    puppet.DropItem(item);
                }

                item.transform.position = new Vector3(px, py, pz);
                item.transform.rotation = new Quaternion(rx, ry, rz, rw);
                if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
                {
                    item.transform.localScale = Vector3.one;
                }

                var rb = item.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    if (isJonnez)
                    {
                        // Для мопеда не блокируем кинематику, чтобы физика колес и подвески работала сразу после броска
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.velocity = new Vector3(vx, vy, vz);
                        rb.angularVelocity = new Vector3(avx, avy, avz);
                    }
                    else
                    {
                        rb.isKinematic = true;
                        rb.useGravity = false;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                }

                if (!isJonnez)
                {
                    remoteMovingItems[itemHash] = new RemoteInterpolatedTracker
                    {
                        Hash = itemHash,
                        Object = item,
                        Rigidbody = rb,
                        TargetPosition = new Vector3(px, py, pz),
                        TargetRotation = new Quaternion(rx, ry, rz, rw),
                        LastPacketTime = Time.time
                    };
                }

                string who = (playerId == 0) ? "Хост" : $"Игрок #{playerId}";
                OnLog?.Invoke($"[Сеть] {who} бросил: {item.name}");
            }
        }

        private void HandleItemSync(PacketReader reader, int connectionId)
        {
            int itemHash = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            bool isSleeping = reader.ReadBoolean();

            int myId = transport.IsServer ? 0 : 1;
            if (playerId == myId) return;

            if (lastLocalHeldHash == itemHash) return;

            // Разрешение конфликта владения между хостом и клиентом
            if (localMovingItems.ContainsKey(itemHash))
            {
                if (!transport.IsServer)
                {
                    localMovingItems.Remove(itemHash);
                }
                else
                {
                    return;
                }
            }

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.ItemSync))
                {
                    relay.WriteInt32(itemHash);
                    relay.WriteInt32(playerId);
                    relay.WriteSingle(px); relay.WriteSingle(py); relay.WriteSingle(pz);
                    relay.WriteSingle(rx); relay.WriteSingle(ry); relay.WriteSingle(rz); relay.WriteSingle(rw);
                    relay.WriteBoolean(isSleeping);
                    transport.Send(relay, connectionId);
                }
            }

            GameObject item = SceneHasher.FindObjectByHash(itemHash);
            if (item == null || item.name.StartsWith("JONNEZ")) return;

            if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
            {
                item.transform.localScale = Vector3.one;
            }

            Vector3 newTargetPos = new Vector3(px, py, pz);
            Quaternion newTargetRot = new Quaternion(rx, ry, rz, rw);

            if (isSleeping)
            {
                remoteMovingItems.Remove(itemHash);
                localMovingItems.Remove(itemHash);

                item.transform.position = newTargetPos;
                item.transform.rotation = newTargetRot;

                var rb = item.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.Sleep();
                }

                RecordMovedWorldItem(itemHash, newTargetPos, newTargetRot);
            }
            else
            {
                if (!remoteMovingItems.TryGetValue(itemHash, out var tracker))
                {
                    var rb = item.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.useGravity = false;
                    }
                    tracker = new RemoteInterpolatedTracker
                    {
                        Hash = itemHash,
                        Object = item,
                        Rigidbody = rb
                    };
                    remoteMovingItems[itemHash] = tracker;
                }

                tracker.TargetPosition = newTargetPos;
                tracker.TargetRotation = newTargetRot;
                tracker.LastPacketTime = Time.time;
            }
        }

        private void HandleContainerUnpack(PacketReader reader, int connectionId)
        {
        }

        private void HandleRequestItemSnapshot(PacketReader reader, int connectionId)
        {
            if (!transport.IsServer) return;

            using (var packet = new PacketWriter(PacketType.ItemSnapshot))
            {
                packet.WriteInt32(heldItemsByPlayer.Count);
                foreach (var kvp in heldItemsByPlayer)
                {
                    packet.WriteInt32(kvp.Key);
                    packet.WriteInt32(kvp.Value);
                }

                packet.WriteInt32(unpackedBags.Count);
                foreach (var bagHash in unpackedBags)
                {
                    packet.WriteInt32(bagHash);
                }

                packet.WriteInt32(movedWorldItems.Count);
                foreach (var kvp in movedWorldItems)
                {
                    packet.WriteInt32(kvp.Key);
                    packet.WriteSingle(kvp.Value.Position.x);
                    packet.WriteSingle(kvp.Value.Position.y);
                    packet.WriteSingle(kvp.Value.Position.z);
                    packet.WriteSingle(kvp.Value.Rotation.x);
                    packet.WriteSingle(kvp.Value.Rotation.y);
                    packet.WriteSingle(kvp.Value.Rotation.z);
                    packet.WriteSingle(kvp.Value.Rotation.w);
                }

                transport.Send(packet);
            }

            OnLog?.Invoke($"[Снимок] Отправлен снимок предметов для клиента #{connectionId} (В руках: {heldItemsByPlayer.Count}, Пакетов: {unpackedBags.Count}, Мир: {movedWorldItems.Count})");
        }

        private void HandleItemSnapshot(PacketReader reader)
        {
            hasRequestedSnapshot = true;

            int heldCount = reader.ReadInt32();
            for (int i = 0; i < heldCount; i++)
            {
                int playerId = reader.ReadInt32();
                int itemHash = reader.ReadInt32();

                heldItemsByPlayer[playerId] = itemHash;
                GameObject item = SceneHasher.FindObjectByHash(itemHash);
                if (item != null)
                {
                    RemotePuppet puppet = playerManager.GetPuppet(playerId);
                    if (puppet != null)
                    {
                        puppet.HoldItem(item);
                    }
                }
            }

            int unpackedCount = reader.ReadInt32();
            for (int i = 0; i < unpackedCount; i++)
            {
                int bagHash = reader.ReadInt32();
                unpackedBags.Add(bagHash);
                GameObject bag = SceneHasher.FindObjectByHash(bagHash);
                if (bag != null)
                {
                    var fsm = bag.GetPlayMaker("Use");
                    if (fsm != null) fsm.SendEvent("GARBAGE");
                }
            }

            int movedCount = reader.ReadInt32();
            int appliedCount = 0;
            for (int i = 0; i < movedCount; i++)
            {
                int itemHash = reader.ReadInt32();
                float px = reader.ReadSingle();
                float py = reader.ReadSingle();
                float pz = reader.ReadSingle();
                float rx = reader.ReadSingle();
                float ry = reader.ReadSingle();
                float rz = reader.ReadSingle();
                float rw = reader.ReadSingle();

                Vector3 targetPos = new Vector3(px, py, pz);
                Quaternion targetRot = new Quaternion(rx, ry, rz, rw);

                RecordMovedWorldItem(itemHash, targetPos, targetRot);

                if (lastLocalHeldHash == itemHash) continue;

                GameObject item = SceneHasher.FindObjectByHash(itemHash);
                if (item != null)
                {
                    item.transform.position = targetPos;
                    item.transform.rotation = targetRot;
                    if (item.transform.localScale.sqrMagnitude > 3.5f || item.transform.localScale.sqrMagnitude < 0.2f)
                    {
                        item.transform.localScale = Vector3.one;
                    }

                    var rb = item.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.Sleep();
                    }
                    appliedCount++;
                }
            }

            OnLog?.Invoke($"[Снимок] Получен снимок предметов: {heldCount} в руках, {unpackedCount} пакетов, {appliedCount}/{movedCount} предметов мира перемещено.");
        }

        public void RequestSnapshot()
        {
            if (!transport.IsClient) return;

            using (var packet = new PacketWriter(PacketType.RequestItemSnapshot))
            {
                transport.Send(packet);
            }

            OnLog?.Invoke("[Снимок] Запрошен снимок предметов мира...");
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport.IsServer)
            {
                HandleRequestItemSnapshot(null, clientId);
            }
        }

        private void HandleClientDisconnected(int clientId)
        {
            if (heldItemsByPlayer.TryGetValue(clientId, out int itemHash))
            {
                heldItemsByPlayer.Remove(clientId);
                GameObject item = SceneHasher.FindObjectByHash(itemHash);
                if (item != null)
                {
                    RemotePuppet puppet = playerManager.GetPuppet(clientId);
                    if (puppet != null)
                    {
                        puppet.ReleaseItem(item);
                    }
                }
            }

            if (!transport.IsServer)
            {
                hasRequestedSnapshot = false;
            }
        }

        public void Reset()
        {
            ClearAllTrackers();
        }

        public void ClearAllTrackers()
        {
            foreach (var tracker in remoteMovingItems.Values)
            {
                if (tracker.Rigidbody != null)
                {
                    tracker.Rigidbody.isKinematic = false;
                    tracker.Rigidbody.useGravity = true;
                    tracker.Rigidbody.WakeUp();
                }
            }
            remoteMovingItems.Clear();
            localMovingItems.Clear();
            heldItemsByPlayer.Clear();
            trackedBags.Clear();
            trackedBeerCases.Clear();
            movedWorldItems.Clear();
            cachedTemplates.Clear();
            hasRequestedSnapshot = false;
        }

        #endregion

                #region Validation Helpers

        public static bool IsValidPortableItem(GameObject go)
        {
            if (go == null) return false;

            string name = go.name;
            string rootName = go.transform.root.name;
            string nameLower = name.ToLower();

            // Исключаем пустые бутылки и банки от выпитого пива/напитков (локальный мусор), но разрешаем полные бутылки
            if (nameLower.Contains("empty") && !nameLower.Contains("case"))
            {
                return false;
            }

            // Кузовы автомобилей и тяжелой техники исключены НАМЕРТВО
            if (name.StartsWith("SATSUMA") || rootName.StartsWith("SATSUMA") ||
                name.StartsWith("HAYOSIKO") || rootName.StartsWith("HAYOSIKO") ||
                name.StartsWith("FERNDALE") || rootName.StartsWith("FERNDALE") ||
                name.StartsWith("KEKMET") || rootName.StartsWith("KEKMET") ||
                name.StartsWith("RICOCHET") || rootName.StartsWith("RICOCHET") ||
                name.StartsWith("FLATBED") || rootName.StartsWith("FLATBED") ||
                name.StartsWith("GIFU") || rootName.StartsWith("GIFU") ||
                name.StartsWith("COMBINE") || rootName.StartsWith("COMBINE") ||
                name.StartsWith("BOAT") || rootName.StartsWith("BOAT"))
            {
                if (go == go.transform.root.gameObject) return false;
            }

            // Мопед Jonnez ES, блок цилиндров двигателя и пакеты с покупками всегда валидны для переноски
            if (name.StartsWith("JONNEZ") || rootName.StartsWith("JONNEZ") || name == "block(Clone)" || name.StartsWith("shopping bag") || go.CompareTag("PART"))
            {
                return true;
            }

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Повышен порог массы до 160 кг (позволяет переносить частично и полностью собранный двигатель Сацумы)
                if (rb.mass > 160f) return false;
            }

            if (name.StartsWith("Door") || name.StartsWith("pivot_door") || (name.StartsWith("Pivot") && go.transform.parent != null && go.transform.parent.name.StartsWith("Door")) || name == "coll")
            {
                return false;
            }

            return true;
        }

        #endregion
    }
}
