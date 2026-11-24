using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Doors;
using OurSummerCar.Environment;
using OurSummerCar.Equipment;
using OurSummerCar.Gambling;
using OurSummerCar.Items;
using OurSummerCar.Jobs;
using OurSummerCar.Network;
using OurSummerCar.Player;
using OurSummerCar.Savegame;
using OurSummerCar.Store;
using OurSummerCar.Vehicles;
using OurSummerCar.World;
using UnityEngine;

namespace OurSummerCar
{
    public class OurSummerCar : Mod
    {
        public override string ID => "OurSummerCar";
        public override string Name => "Our Summer Car";
        public override string Version => "0.0.1";
        public override string Author => "OurSummerCar Team";
        public override string Description => "Co-op multiplayer mod for My Summer Car (Full Symmetrical Synchronization: Jobs, Assembly, World, Items).";

        public static OurSummerCar Instance { get; private set; }

        private NetworkTransport transport;
        private PlayerManager playerManager;
        private DoorManager doorManager;
        private ItemManager itemManager;
        private JerryCanManager jerryCanManager;
        private ToolSetManager toolSetManager;
        private HouseManager houseManager;
        private VehicleDoorManager vehicleDoorManager;
        private ConsumableManager consumableManager;
        private VehicleAssemblyManager vehicleAssemblyManager;
        private TelephoneManager telephoneManager;
        private JobManager jobManager;
        private StoreManager storeManager;
        private EquipmentManager equipmentManager;
        private SleepSyncManager sleepSyncManager;
        private TimeAndWeatherManager timeAndWeatherManager;
        private GamblingManager gamblingManager;
        private HeistAndVandalismManager heistManager;
        private PlayerActionsManager playerActionsManager;
        private VehicleInteractionManager vehicleInteractionManager;
        private VehiclePhysicsManager vehiclePhysicsManager;
        private NpcTrafficManager npcTrafficManager;
        private SavegameSyncManager savegameSyncManager;
        private WorldInteractionManager worldInteractionManager;
        private ExpandedQuestsManager expandedQuestsManager;
        private VehicleDamageAndTowingManager vehicleDamageAndTowingManager;
        private FleetariAndCatalogManager fleetariAndCatalogManager;
        private PubAndIslandManager pubAndIslandManager;
        private RallyAndPoliceManager rallyAndPoliceManager;
        private CarRadioManager carRadioManager;
        private VehicleEngineSimulationManager vehicleEngineSimulationManager;
        private StorylinesAndQuestsManager storylinesAndQuestsManager;
        private WorldHazardsManager worldHazardsManager;
        private DomesticAndMinigamesManager domesticAndMinigamesManager;
        private VehiclePassengerManager vehiclePassengerManager;
        private PhysicsOptimizer physicsOptimizer;

        private bool showMenu = true;
        private Rect windowRect = new Rect(30, 30, 395, 680);
        private Vector2 windowScrollPos = Vector2.zero;
        private Vector2 logScrollPos = Vector2.zero;

        private string playerNickname = "Player";
        private string localRadminIP = null;
        private bool autoStartServer = false;

        private string ipAddress = "127.0.0.1";
        private string portString = "7777";
        private readonly List<string> recentLogs = new List<string>();
        private bool isInitialized = false;

        public static bool IsInGameWorld { get; private set; } = false;

        public override void ModSetup()
        {
            Instance = this;
            LoadSettings();
            SetupFunction(Setup.OnMenuLoad, Mod_OnMenuLoad);
            SetupFunction(Setup.OnLoad, Mod_OnLoad);
            SetupFunction(Setup.Update, Mod_Update);
            SetupFunction(Setup.OnGUI, Mod_OnGUI);
        }

        private void LoadSettings()
        {
            playerNickname = PlayerPrefs.GetString("OSC_Nickname", string.Empty);
            if (string.IsNullOrEmpty(playerNickname))
            {
                try { playerNickname = System.Environment.UserName; } catch { playerNickname = "Player"; }
                if (string.IsNullOrEmpty(playerNickname)) playerNickname = "Player";
            }

            ipAddress = PlayerPrefs.GetString("OSC_TargetIP", "127.0.0.1");
            portString = PlayerPrefs.GetString("OSC_Port", "7777");
            autoStartServer = PlayerPrefs.GetInt("OSC_AutoHost", 0) == 1;

            DetectLocalNetworkIPs();
        }

        private void SaveSettings()
        {
            PlayerPrefs.SetString("OSC_Nickname", playerNickname);
            PlayerPrefs.SetString("OSC_TargetIP", ipAddress);
            PlayerPrefs.SetString("OSC_Port", portString);
            PlayerPrefs.SetInt("OSC_AutoHost", autoStartServer ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void DetectLocalNetworkIPs()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string s = ip.ToString();
                        if (s.StartsWith("26."))
                        {
                            localRadminIP = s;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private void Mod_OnMenuLoad()
        {
            if (IsInGameWorld)
            {
                // Игрок вышел из мира в главное меню — завершаем сетевую сессию
                if (transport != null && transport.IsRunning)
                {
                    transport.Stop();
                }
            }

            IsInGameWorld = false;
            SceneHasher.Clear();
            playerManager?.ClearAllPuppets();
            itemManager?.ClearAllTrackers();
            CleanupManagers();
            isInitialized = false;
            InitMod("в главном меню");
        }

        private void Mod_OnLoad()
        {
            IsInGameWorld = true;
            SceneHasher.Clear();
            playerManager?.ClearAllPuppets();
            itemManager?.ClearAllTrackers();

            // Очищаем и сбрасываем ссылки на объекты предыдущей сцены перед подключением к новому миру
            CleanupManagers();
            isInitialized = false;
            InitMod("в игре");

            doorManager?.InitializeDoors();
            itemManager?.InitializeItems();
            jerryCanManager?.InitializeJerryCans();
            toolSetManager?.InitializeToolSets();
            houseManager?.InitializeHouse();
            vehicleDoorManager?.InitializeVehicleDoors();
            consumableManager?.InitializeConsumables();
            vehicleAssemblyManager?.InitializeAssembly();
            telephoneManager?.InitializeTelephone();
            jobManager?.InitializeJobs();
            equipmentManager?.InitializeEquipment();
            storeManager?.InitializeStore();
            savegameSyncManager?.InitializeSave();

            vehicleDamageAndTowingManager?.ScanVehiclesAndRope();
            fleetariAndCatalogManager?.ScanObjects();
            pubAndIslandManager?.ScanObjects();
            rallyAndPoliceManager?.ScanObjects();
            carRadioManager?.ScanRadios();
            vehicleEngineSimulationManager?.ScanEngineObjects();
            storylinesAndQuestsManager?.ScanStoryObjects();
            worldHazardsManager?.ScanHazardObjects();
            domesticAndMinigamesManager?.ScanDomesticObjects();
            vehiclePassengerManager?.ScanAndCreatePassengerSeats();

            if (autoStartServer && transport != null && !transport.IsRunning)
            {
                int port = 7777;
                int.TryParse(portString, out port);
                transport.StartServer(port > 0 ? port : 7777);
            }

            if (transport != null && transport.IsRunning)
            {
                playerManager?.SendLocalPlayerNickname(playerNickname);
                if (transport.IsServer)
                {
                    AddLog($"[Хост] Мир загружен. Сервер активен на порту {portString}.");
                }
                else
                {
                    AddLog($"[Клиент] Мир загружен. Соединение с хостом активно.");
                }
            }
        }

        private void InitMod(string context)
        {
            if (isInitialized) return;
            isInitialized = true;

            if (transport == null)
            {
                transport = new NetworkTransport();
                transport.OnLog += AddLog;
            }
            else
            {
                // Очищаем старые подписки менеджеров предыдущей сцены, чтобы не было утечек и задвоений
                transport.ClearHandlers();
            }

            transport.OnClientConnected += id => AddLog($"[Сетка] Игрок #{id} подключился.");
            transport.OnClientDisconnected += id => AddLog($"[Сетка] Игрок #{id} отключился.");

            playerManager = new PlayerManager(transport);
            playerManager.LocalNickname = playerNickname;
            playerManager.OnLog += AddLog;

            // Загрузка 3D-моделей персонажей WreckMP
            CharacterAssetLoader.LoadAssets();

            doorManager = new DoorManager(transport);
            doorManager.OnLog += AddLog;

            itemManager = new ItemManager(transport, playerManager);
            itemManager.OnLog += AddLog;

            jerryCanManager = new JerryCanManager(transport);
            jerryCanManager.OnLog += AddLog;

            toolSetManager = new ToolSetManager(transport);
            toolSetManager.OnLog += AddLog;

            houseManager = new HouseManager(transport);
            houseManager.OnLog += AddLog;

            vehicleDoorManager = new VehicleDoorManager(transport);
            vehicleDoorManager.OnLog += AddLog;

            consumableManager = new ConsumableManager(transport);
            consumableManager.OnLog += AddLog;

            vehicleAssemblyManager = new VehicleAssemblyManager(transport);
            vehicleAssemblyManager.OnLog += AddLog;

            telephoneManager = new TelephoneManager(transport);
            telephoneManager.OnLog += AddLog;

            jobManager = new JobManager(transport);
            jobManager.OnLog += AddLog;

            equipmentManager = new EquipmentManager(transport);
            equipmentManager.OnLog += AddLog;

            var sleepObj = CreateSingletonManagerObject("OurSummerCar_SleepManager");
            sleepSyncManager = sleepObj.AddComponent<SleepSyncManager>();
            sleepSyncManager.OnLog += AddLog;
            sleepSyncManager.Initialize(transport);

            var timeObj = CreateSingletonManagerObject("OurSummerCar_TimeWeatherManager");
            timeAndWeatherManager = timeObj.AddComponent<TimeAndWeatherManager>();
            timeAndWeatherManager.OnLog += AddLog;
            timeAndWeatherManager.Initialize(transport);

            var gambObj = CreateSingletonManagerObject("OurSummerCar_GamblingManager");
            gamblingManager = gambObj.AddComponent<GamblingManager>();
            gamblingManager.OnLog += AddLog;
            gamblingManager.Initialize(transport);

            var heistObj = CreateSingletonManagerObject("OurSummerCar_HeistManager");
            heistManager = heistObj.AddComponent<HeistAndVandalismManager>();
            heistManager.OnLog += AddLog;
            heistManager.Initialize(transport);

            var actionObj = CreateSingletonManagerObject("OurSummerCar_PlayerActionsManager");
            playerActionsManager = actionObj.AddComponent<PlayerActionsManager>();
            playerActionsManager.OnLog += AddLog;
            playerActionsManager.Initialize(transport, playerManager);

            var vehIntObj = CreateSingletonManagerObject("OurSummerCar_VehicleInteractionManager");
            vehicleInteractionManager = vehIntObj.AddComponent<VehicleInteractionManager>();
            vehicleInteractionManager.OnLog += AddLog;
            vehicleInteractionManager.Initialize(transport);

            var vehPhysObj = CreateSingletonManagerObject("OurSummerCar_VehiclePhysicsManager");
            vehiclePhysicsManager = vehPhysObj.AddComponent<VehiclePhysicsManager>();
            vehiclePhysicsManager.OnLog += AddLog;
            vehiclePhysicsManager.Initialize(transport);

            var trafficObj = CreateSingletonManagerObject("OurSummerCar_NpcTrafficManager");
            npcTrafficManager = trafficObj.AddComponent<NpcTrafficManager>();
            npcTrafficManager.OnLog += AddLog;
            npcTrafficManager.Initialize(transport);

            var worldObj = CreateSingletonManagerObject("OurSummerCar_WorldInteractionManager");
            worldInteractionManager = worldObj.AddComponent<WorldInteractionManager>();
            worldInteractionManager.OnLog += AddLog;
            worldInteractionManager.Initialize(transport);

            var questObj = CreateSingletonManagerObject("OurSummerCar_ExpandedQuestsManager");
            expandedQuestsManager = questObj.AddComponent<ExpandedQuestsManager>();
            expandedQuestsManager.OnLog += AddLog;
            expandedQuestsManager.Initialize(transport);

            var storeObj = CreateSingletonManagerObject("OurSummerCar_StoreManager");
            storeManager = storeObj.AddComponent<StoreManager>();
            storeManager.OnLog += AddLog;
            storeManager.Initialize(transport);

            var saveObj = CreateSingletonManagerObject("OurSummerCar_SavegameSyncManager");
            savegameSyncManager = saveObj.AddComponent<SavegameSyncManager>();
            savegameSyncManager.OnLog += AddLog;
            savegameSyncManager.Initialize(transport);

            var dmgObj = CreateSingletonManagerObject("OurSummerCar_VehicleDamageAndTowingManager");
            vehicleDamageAndTowingManager = dmgObj.AddComponent<VehicleDamageAndTowingManager>();
            vehicleDamageAndTowingManager.OnLog += AddLog;
            vehicleDamageAndTowingManager.Initialize(transport);

            var fleetObj = CreateSingletonManagerObject("OurSummerCar_FleetariAndCatalogManager");
            fleetariAndCatalogManager = fleetObj.AddComponent<FleetariAndCatalogManager>();
            fleetariAndCatalogManager.OnLog += AddLog;
            fleetariAndCatalogManager.Initialize(transport);

            var pubObj = CreateSingletonManagerObject("OurSummerCar_PubAndIslandManager");
            pubAndIslandManager = pubObj.AddComponent<PubAndIslandManager>();
            pubAndIslandManager.OnLog += AddLog;
            pubAndIslandManager.Initialize(transport);

            var rallyObj = CreateSingletonManagerObject("OurSummerCar_RallyAndPoliceManager");
            rallyAndPoliceManager = rallyObj.AddComponent<RallyAndPoliceManager>();
            rallyAndPoliceManager.OnLog += AddLog;
            rallyAndPoliceManager.Initialize(transport);

            var radioObj = CreateSingletonManagerObject("OurSummerCar_CarRadioManager");
            carRadioManager = radioObj.AddComponent<CarRadioManager>();
            carRadioManager.OnLog += AddLog;
            carRadioManager.Initialize(transport);

            var engObj = CreateSingletonManagerObject("OurSummerCar_VehicleEngineSimulationManager");
            vehicleEngineSimulationManager = engObj.AddComponent<VehicleEngineSimulationManager>();
            vehicleEngineSimulationManager.OnLog += AddLog;
            vehicleEngineSimulationManager.Initialize(transport);

            var storyObj = CreateSingletonManagerObject("OurSummerCar_StorylinesAndQuestsManager");
            storylinesAndQuestsManager = storyObj.AddComponent<StorylinesAndQuestsManager>();
            storylinesAndQuestsManager.OnLog += AddLog;
            storylinesAndQuestsManager.Initialize(transport);

            var hazardObj = CreateSingletonManagerObject("OurSummerCar_WorldHazardsManager");
            worldHazardsManager = hazardObj.AddComponent<WorldHazardsManager>();
            worldHazardsManager.OnLog += AddLog;
            worldHazardsManager.Initialize(transport);

            var minigameObj = CreateSingletonManagerObject("OurSummerCar_DomesticAndMinigamesManager");
            domesticAndMinigamesManager = minigameObj.AddComponent<DomesticAndMinigamesManager>();
            domesticAndMinigamesManager.OnLog += AddLog;
            domesticAndMinigamesManager.Initialize(transport);

            var passObj = CreateSingletonManagerObject("OurSummerCar_VehiclePassengerManager");
            vehiclePassengerManager = passObj.AddComponent<VehiclePassengerManager>();
            vehiclePassengerManager.OnLog += AddLog;
            vehiclePassengerManager.Initialize(transport);

            var physOptObj = CreateSingletonManagerObject("OurSummerCar_PhysicsOptimizer");
            physicsOptimizer = physOptObj.AddComponent<PhysicsOptimizer>();

            AddLog($"OurSummerCar v{Version} инициализирован ({context}). F10 — меню.");
            ModConsole.Print($"<color=green>[OurSummerCar]</color> Загружен ({context})!");
        }

        private static GameObject CreateSingletonManagerObject(string name)
        {
            var existing = GameObject.Find(name);
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
            var go = new GameObject(name);
            GameObject.DontDestroyOnLoad(go);
            return go;
        }

        private void CleanupManagers()
        {
            try { playerManager?.Reset(); } catch { }
            try { doorManager?.Reset(); } catch { }
            try { itemManager?.Reset(); } catch { }
            try { jerryCanManager?.Reset(); } catch { }
            try { toolSetManager?.Reset(); } catch { }
            try { houseManager?.Reset(); } catch { }
            try { vehicleDoorManager?.Reset(); } catch { }
            try { consumableManager?.Reset(); } catch { }
            try { vehicleAssemblyManager?.Reset(); } catch { }
            try { telephoneManager?.Reset(); } catch { }
            try { jobManager?.Reset(); } catch { }
            try { equipmentManager?.Reset(); } catch { }

            try { sleepSyncManager?.Reset(); } catch { }
            try { timeAndWeatherManager?.Reset(); } catch { }
            try { gamblingManager?.Reset(); } catch { }
            try { heistManager?.Reset(); } catch { }
            try { playerActionsManager?.Reset(); } catch { }
            try { vehicleInteractionManager?.Reset(); } catch { }
            try { vehiclePhysicsManager?.Reset(); } catch { }
            try { npcTrafficManager?.Reset(); } catch { }
            try { worldInteractionManager?.Reset(); } catch { }
            try { expandedQuestsManager?.Reset(); } catch { }
            try { storeManager?.Reset(); } catch { }
            try { savegameSyncManager?.Reset(); } catch { }
            try { vehicleDamageAndTowingManager?.Reset(); } catch { }
            try { fleetariAndCatalogManager?.Reset(); } catch { }
            try { pubAndIslandManager?.Reset(); } catch { }
            try { rallyAndPoliceManager?.Reset(); } catch { }
            try { carRadioManager?.Reset(); } catch { }
            try { vehicleEngineSimulationManager?.Reset(); } catch { }
            try { storylinesAndQuestsManager?.Reset(); } catch { }
            try { worldHazardsManager?.Reset(); } catch { }
            try { domesticAndMinigamesManager?.Reset(); } catch { }
            try { vehiclePassengerManager?.Reset(); } catch { }
            try { physicsOptimizer?.Reset(); } catch { }
            try { SceneHasher.Clear(); } catch { }

            string[] managerNames = new string[]
            {
                "OSC_LocalPlayerAnimationTracker",
                "OurSummerCar_SleepManager",
                "OurSummerCar_TimeWeatherManager",
                "OurSummerCar_GamblingManager",
                "OurSummerCar_HeistManager",
                "OurSummerCar_PlayerActionsManager",
                "OurSummerCar_VehicleInteractionManager",
                "OurSummerCar_VehiclePhysicsManager",
                "OurSummerCar_NpcTrafficManager",
                "OurSummerCar_WorldInteractionManager",
                "OurSummerCar_ExpandedQuestsManager",
                "OurSummerCar_StoreManager",
                "OurSummerCar_SavegameSyncManager",
                "OurSummerCar_VehicleDamageAndTowingManager",
                "OurSummerCar_FleetariAndCatalogManager",
                "OurSummerCar_PubAndIslandManager",
                "OurSummerCar_RallyAndPoliceManager",
                "OurSummerCar_CarRadioManager",
                "OurSummerCar_VehicleEngineSimulationManager",
                "OurSummerCar_StorylinesAndQuestsManager",
                "OurSummerCar_WorldHazardsManager",
                "OurSummerCar_DomesticAndMinigamesManager",
                "OurSummerCar_VehiclePassengerManager",
                "OurSummerCar_PhysicsOptimizer"
            };

            for (int i = 0; i < managerNames.Length; i++)
            {
                var go = GameObject.Find(managerNames[i]);
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private void Mod_Update()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                showMenu = !showMenu;
            }

            transport?.PollEvents();

            if (IsInGameWorld)
            {
                playerManager?.Update();
                doorManager?.Update();
                itemManager?.Update();
                jerryCanManager?.Update();
                vehicleDoorManager?.Update();
                consumableManager?.Update();
                toolSetManager?.Update();
                vehicleAssemblyManager?.Update();
                jobManager?.Update();
                equipmentManager?.Update();
            }
        }

        private void Mod_OnGUI()
        {
            if (!showMenu) return;

            GUI.depth = -100;
            float maxWindowHeight = Mathf.Min(Screen.height - 40, 700);
            windowRect.height = maxWindowHeight;
            windowRect = GUI.Window(99211, windowRect, DrawWindow, "Our Summer Car (Кооператив)");
        }

        private void DrawWindow(int windowID)
        {
            windowScrollPos = GUILayout.BeginScrollView(windowScrollPos, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUILayout.Space(4);

            // Настройка вашего никнейма
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Ваш ник:</b>", GUILayout.Width(70));
            string newNick = GUILayout.TextField(playerNickname, 20);
            if (newNick != playerNickname)
            {
                playerNickname = newNick;
                playerManager?.SendLocalPlayerNickname(playerNickname);
                SaveSettings();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // Статус подключения
            string statusStr = "<color=grey>● Отключен</color>";
            if (transport != null && transport.IsRunning)
            {
                if (transport.IsServer)
                {
                    statusStr = $"<color=lime>● ХОСТ (Клиентов: {transport.ConnectedClientsCount})</color>";
                }
                else if (transport.IsClient)
                {
                    string pingStr = transport.LastPingRTT >= 0 ? $"{transport.LastPingRTT} мс" : "...";
                    statusStr = $"<color=cyan>● КЛИЕНТ (Пинг: {pingStr})</color>";
                }
            }

            GUILayout.Label("Сеть: " + statusStr);

            if (!Player.CharacterAssetLoader.IsLoaded && Event.current.type == EventType.Repaint)
            {
                Player.CharacterAssetLoader.LoadAssets();
            }

            string modelStr = Player.CharacterAssetLoader.IsLoaded 
                ? "<color=lime>● 3D-модели: ОК (WreckMP)</color>" 
                : $"<color=yellow>● 3D-модели: {Player.CharacterAssetLoader.StatusMessage}</color>";
            GUILayout.Label(modelStr);

            if (playerManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Игроков в мире: <color=cyan>{playerManager.ActivePuppetsCount + 1}</color>");
            }

            if (doorManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Двери и ворота: <color=lime>{doorManager.RegisteredDoorsCount}</color>");
            }

            if (itemManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Предметы и руки: <color=yellow>{itemManager.TrackedItemsCount}</color>");
            }

            if (houseManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Дом и выключатели: <color=lime>{houseManager.RegisteredSwitchesCount}</color>");
            }

            if (vehicleDoorManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Транспорт и двери: <color=cyan>{vehicleDoorManager.RegisteredDoorsCount}</color>");
            }

            if (consumableManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Расходники: <color=yellow>{consumableManager.TrackedCount}</color>");
            }

            if (vehicleAssemblyManager != null && transport != null && transport.IsRunning)
            {
                GUILayout.Label($"Сборка Satsuma: <color=lime>{vehicleAssemblyManager.TrackedPartsCount}</color> дет, <color=lime>{vehicleAssemblyManager.TrackedBoltsCount}</color> болт.");
            }

            GUILayout.Space(4);

            // Оптимизатор физики PhysX 3.3
            if (physicsOptimizer != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Оптимизация PhysX:", GUILayout.Width(140));
                string optStatus = physicsOptimizer.IsOptimized ? "<color=lime>ВКЛ (18 итераций, 3мм)</color>" : "<color=yellow>ВЫКЛ (Ваниль MSC)</color>";
                if (GUILayout.Button(optStatus, GUILayout.ExpandWidth(true), GUILayout.Height(22)))
                {
                    physicsOptimizer.SetEnabled(!physicsOptimizer.IsOptimized);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);

            // Поля ввода и управление подключением
            if (transport == null || !transport.IsRunning)
            {
                if (!string.IsNullOrEmpty(localRadminIP))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<color=lime>Radmin IP:</color> <b>{localRadminIP}</b>", GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Копировать", GUILayout.Width(85), GUILayout.Height(20)))
                    {
                        CopyToClipboard(localRadminIP);
                        AddLog($"Ваш IP {localRadminIP} скопирован в буфер обмена!");
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("IP хоста:", GUILayout.Width(65));
                string newIp = GUILayout.TextField(ipAddress);
                if (newIp != ipAddress)
                {
                    ipAddress = newIp;
                    SaveSettings();
                }

                GUILayout.Label("Порт:", GUILayout.Width(40));
                string newPort = GUILayout.TextField(portString, GUILayout.Width(55));
                if (newPort != portString)
                {
                    portString = newPort;
                    SaveSettings();
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6);

                if (GUILayout.Button("Запустить Хост (Сервер)", GUILayout.Height(28)))
                {
                    if (int.TryParse(portString, out int port))
                    {
                        transport.StartServer(port);
                        if (transport.IsServer)
                        {
                            playerManager?.SendLocalPlayerNickname(playerNickname);
                        }
                    }
                    else
                    {
                        AddLog("Ошибка: некорректный порт.");
                    }
                }

                GUILayout.Space(3);

                if (GUILayout.Button("Подключиться к другу (Клиент)", GUILayout.Height(28)))
                {
                    if (int.TryParse(portString, out int port))
                    {
                        transport.Connect(ipAddress.Trim(), port);
                        playerManager?.SendLocalPlayerNickname(playerNickname);
                    }
                    else
                    {
                        AddLog("Ошибка: некорректный порт.");
                    }
                }

                bool newAuto = GUILayout.Toggle(autoStartServer, " Авто-запуск Хоста при загрузке");
                if (newAuto != autoStartServer)
                {
                    autoStartServer = newAuto;
                    SaveSettings();
                }
            }
            else
            {
                if (GUILayout.Button("Остановить соединение", GUILayout.Height(28)))
                {
                    transport.Stop();
                    playerManager?.ClearAllPuppets();
                    itemManager?.ClearAllTrackers();
                }

                GUILayout.Space(3);

                if (GUILayout.Button("Отправить тестовый Ping", GUILayout.Height(24)))
                {
                    transport.SendPing();
                    AddLog("[Тест] Отправлен Ping...");
                }

                if (savegameSyncManager != null && transport != null && transport.IsRunning && !transport.IsServer)
                {
                    GUILayout.Space(6);
                    GUILayout.Label("<b>Синхронизация сохранения мира:</b>");

                    bool autoSync = GUILayout.Toggle(savegameSyncManager.AutoSyncOnConnect, " Авто-загрузка мира при подключении");
                    if (autoSync != savegameSyncManager.AutoSyncOnConnect)
                    {
                        savegameSyncManager.AutoSyncOnConnect = autoSync;
                    }

                    if (savegameSyncManager.IsSyncing)
                    {
                        GUILayout.Label($"<color=yellow>● {savegameSyncManager.StatusMessage}</color>");
                    }
                    else
                    {
                        if (GUILayout.Button("Загрузить мир хоста (Сохраняя потребности)", GUILayout.Height(26)))
                        {
                            savegameSyncManager.RequestHostWorldSave();
                        }
                    }

                    if (savegameSyncManager.NeedsWorldReload)
                    {
                        GUILayout.Space(3);
                        if (GUILayout.Button("<color=yellow><b>● Мир обновлен! Нажмите для перезагрузки</b></color>", GUILayout.Height(28)))
                        {
                            savegameSyncManager.ReloadGameWorld();
                        }
                    }
                }
            }

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>Журнал событий:</b>");
            if (GUILayout.Button("Очистить", GUILayout.Width(75), GUILayout.Height(18)))
            {
                lock (recentLogs) { recentLogs.Clear(); }
            }
            GUILayout.EndHorizontal();

            // Окно лога с возможностью скроллинга и просмотром всей истории
            logScrollPos = GUILayout.BeginScrollView(logScrollPos, GUI.skin.box, GUILayout.Height(140));
            lock (recentLogs)
            {
                for (int i = 0; i < recentLogs.Count; i++)
                {
                    GUILayout.Label("<size=11>" + recentLogs[i] + "</size>");
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4);
            GUILayout.Label("<size=10>F10 — скрыть меню | Нажмите для взаимодействия</size>");

            GUILayout.EndScrollView();

            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        private static void CopyToClipboard(string text)
        {
            try
            {
                var prop = typeof(GUIUtility).GetProperty("systemCopyBuffer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (prop != null)
                {
                    prop.SetValue(null, text, null);
                }
            }
            catch { }
        }

        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string entry = $"[{time}] {message}";
            lock (recentLogs)
            {
                recentLogs.Add(entry);
                if (recentLogs.Count > 100) recentLogs.RemoveAt(0);
            }
            logScrollPos.y = float.MaxValue;
            ModConsole.Print($"[OurSummerCar] {message}");
        }
    }
}
