using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Jobs
{
    /// <summary>
    /// Главный сетевой менеджер квестов и работ в My Summer Car.
    /// Построен на хост-авторитарной модели (в отличие от WreckMP), что исключает рассинхрон:
    /// 1. Колка и доставка дров (Logwall, прицеп с дровами, сарай дровосека Livaloinen, оплата).
    /// 2. Откачка септиков (5 жилых колодцев Kaivo1-5, уровень стоков, шланги ассенизатора Gifu, оплата заказчиков, станция очистки).
    /// 3. Брага (Килью) и алкаш Йокке (брожение в ведре, продажа бутылок на столе, ночной подвоз из бара).
    /// 4. Бабушка Сиркка (доставка сосисок, молока, рыбы, разговор и конверт с марками).
    /// 5. Техосмотр у Линделла (INSPECTION / Order / чек-лист).
    /// 6. Рекламные буклеты Теймо и почтовые ящики.
    /// 7. Общий кошелек (PlayerMoney) — зарплата за работу начисляется обоим игрокам синхронно.
    /// </summary>
    public class JobManager
    {
        public static JobManager Instance { get; private set; }

        private readonly NetworkTransport transport;

        // Колка дров (Logwall)
        private PlayMakerFSM logwallFsm;
        private FsmGameObject logwallCurrentLog;
        private FsmEvent logwallSpawnEvent;
        private readonly Dictionary<int, FixedJoint> activeLogJoints = new Dictionary<int, FixedJoint>();
        private int deterministicLogCounter = 0;
        private bool isSuppressingLog = false;

        // Сарай покупателя дров (Livaloinen)
        private PlayMakerFSM woodShedFsm;
        private FsmInt woodDeliveredCount;

        // Септики (5 колодцев)
        public class SepticWell
        {
            public int Index;
            public GameObject WellObject;
            public PlayMakerFSM LevelFsm;
            public FsmFloat LevelVar;
            public PlayMakerFSM LidFsm;
            public bool IsOpen;
        }
        private readonly List<SepticWell> septicWells = new List<SepticWell>();

        // Очистные сооружения
        // (резерв)

        // Техосмотр
        private PlayMakerFSM inspectionOrderFsm;
        private FsmEvent inspectionTriggerEvent;
        private bool isSuppressingInspection = false;

        // Рекламные буклеты Теймо
        private PlayMakerFSM teimoAdvertPileFsm;
        private FsmEvent teimoSpawnSheetEvent;
        private readonly List<PlayMakerFSM> mailboxFsms = new List<PlayMakerFSM>();
        private bool isSuppressingAdvert = false;

        // Общий баланс денег (PlayerMoney) и сигареты (PlayerCigarettes)
        private FsmFloat playerMoneyVar;
        private FsmInt playerCigarettesVar;
        private float lastSyncedMoney = -1f;
        private int lastSavedCigarettes = -1;
        private float nextMoneyCheckTime = 0f;

        private bool isInitialized = false;
        public event Action<string> OnLog;

        public void Reset()
        {
            isInitialized = false;
            playerMoneyVar = null;
            playerCigarettesVar = null;
            logwallFsm = null;
            woodShedFsm = null;
            septicWells.Clear();
            inspectionOrderFsm = null;
            mailboxFsms.Clear();
            teimoAdvertPileFsm = null;
            lastSyncedMoney = -1f;
        }

        public JobManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeJobs()
        {
            if (isInitialized) return;

            try
            {
                // Инициализация переменной денег игрока
                InitMoney();

                // 1. Инициализация дровяника и колки дров
                InitWoodChopping();

                // 2. Инициализация покупателя дров
                InitWoodCustomer();

                // 3. Инициализация септиков и очистной станции
                InitSepticWells();

                // 4. Инициализация техосмотра
                InitInspection();

                // 5. Инициализация рекламы Теймо
                InitAdvertFlyers();

                // 6. Инициализация килью (Йокке) и бабушки Сиркки
                InitKilju();
                InitGrandma();

                isInitialized = true;
                OnLog?.Invoke($"[Квесты и Работа] Менеджер инициализирован. Колодцев: {septicWells.Count}, ящиков: {mailboxFsms.Count}.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[JobManager Init Error] {ex.Message}");
            }
        }

        public void Update()
        {
            if (!transport.IsRunning || !isInitialized) return;

            // Двусторонняя проверка и синхронизация общего кошелька (раз в 0.5 сек)
            if (playerMoneyVar != null && Time.time >= nextMoneyCheckTime)
            {
                nextMoneyCheckTime = Time.time + 0.5f;
                float current = playerMoneyVar.Value;
                float diff = current - lastSyncedMoney;

                if (Mathf.Abs(diff) >= 0.5f)
                {
                    if (transport.IsServer)
                    {
                        lastSyncedMoney = current;
                        SendMoneySync(current);
                    }
                    else if (transport.IsClient)
                    {
                        lastSyncedMoney = current;
                        SendMoneyDelta(diff);
                    }
                }
            }

            // Автосохранение и восстановление сигарет клиента
            if (playerCigarettesVar != null && transport.IsClient)
            {
                int currentCigs = playerCigarettesVar.Value;
                if (currentCigs != lastSavedCigarettes && currentCigs >= 0)
                {
                    lastSavedCigarettes = currentCigs;
                    PlayerPrefs.SetInt("OSC_SavedCigarettes", currentCigs);
                }
            }
        }

        private void InitMoney()
        {
            try
            {
                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    playerMoneyVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
                    if (playerMoneyVar != null)
                    {
                        lastSyncedMoney = playerMoneyVar.Value;
                    }

                    playerCigarettesVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("PlayerCigarettes");
                    if (playerCigarettesVar != null && transport.IsClient)
                    {
                        // Восстановление сигарет клиента при перезаходе на сервер
                        int savedCigs = PlayerPrefs.GetInt("OSC_SavedCigarettes", -1);
                        if (savedCigs > 0)
                        {
                            playerCigarettesVar.Value = savedCigs;
                            lastSavedCigarettes = savedCigs;
                            OnLog?.Invoke($"[Инвентарь] Восстановлено {savedCigs} сигарет из сохранения.");
                        }
                        else
                        {
                            lastSavedCigarettes = playerCigarettesVar.Value;
                        }
                    }
                }
            }
            catch { }
        }

        private void InitWoodChopping()
        {
            Transform logwallTr = GameObject.Find("YARD")?.transform.Find("MachineHall/Logging/Logwall");
            if (logwallTr == null)
            {
                var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    if (allGo[i].name == "Logwall")
                    {
                        logwallTr = allGo[i].transform;
                        break;
                    }
                }
            }

            if (logwallTr == null) return;

            logwallFsm = logwallTr.GetComponent<PlayMakerFSM>();
            if (logwallFsm != null)
            {
                logwallFsm.Initialize();
                logwallCurrentLog = logwallFsm.FsmVariables.FindFsmGameObject("Log");
                logwallSpawnEvent = logwallFsm.AddEvent("MP_SPAWN");
                logwallFsm.AddGlobalTransition(logwallSpawnEvent, "Create log");

                logwallFsm.InsertAction("Create log", () =>
                {
                    if (isSuppressingLog)
                    {
                        isSuppressingLog = false;
                        return;
                    }
                    OnLocalLogCreated();
                }, -1, false);
            }
        }

        private void OnLocalLogCreated()
        {
            if (logwallCurrentLog == null || logwallCurrentLog.Value == null) return;
            GameObject logObj = logwallCurrentLog.Value;

            deterministicLogCounter++;
            int logNetId = deterministicLogCounter * 100;
            int childNetId = logNetId + 1;

            HookLogChild(logObj, childNetId);

            // Отправка пакета создания полена с детерминированным ID
            SendJobAction(JobActionType.FirewoodDelivered, logNetId, childNetId, 0f);
        }

        private void HookLogChild(GameObject logObj, int childNetId)
        {
            if (logObj == null || logObj.transform.childCount == 0) return;
            Transform child = logObj.transform.GetChild(0);
            if (child == null) return;

            var joint = child.GetComponent<FixedJoint>();
            if (joint != null)
            {
                activeLogJoints[childNetId] = joint;
            }

            var childFsm = child.GetComponent<PlayMakerFSM>();
            if (childFsm != null)
            {
                childFsm.Initialize();
                string cutState = childFsm.HasState("State 2") ? "State 2" : (childFsm.HasState("Cut") ? "Cut" : null);
                if (cutState != null)
                {
                    childFsm.InsertAction(cutState, () =>
                    {
                        SendJobAction(JobActionType.FirewoodDelivered, childNetId, -1, 0f);
                    }, 0, false);
                }
            }
        }

        private void InitWoodCustomer()
        {
            // Поиск сарая дровосека
            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i].name == "WoodShed" || allGo[i].name == "WoodTrigger")
                {
                    var fsm = allGo[i].GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        fsm.Initialize();
                        woodShedFsm = fsm;
                        woodDeliveredCount = fsm.FsmVariables.FindFsmInt("WoodCount");
                        break;
                    }
                }
            }
        }

        private void InitSepticWells()
        {
            septicWells.Clear();
            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();

            for (int i = 0; i < allGo.Length; i++)
            {
                string name = allGo[i].name;
                if (name.StartsWith("Kaivo") || name.StartsWith("SepticTank") || name.Contains("WasteWell"))
                {
                    var fsms = allGo[i].GetComponentsInChildren<PlayMakerFSM>(true);
                    PlayMakerFSM levelFsm = null;
                    PlayMakerFSM lidFsm = null;
                    FsmFloat levelVar = null;

                    for (int j = 0; j < fsms.Length; j++)
                    {
                        fsms[j].Initialize();
                        var lVar = fsms[j].FsmVariables.FindFsmFloat("Level");
                        if (lVar != null)
                        {
                            levelFsm = fsms[j];
                            levelVar = lVar;
                        }
                        if (fsms[j].FsmName == "Use" && (fsms[j].gameObject.name.Contains("lid") || fsms[j].HasState("Open")))
                        {
                            lidFsm = fsms[j];
                        }
                    }

                    if (levelFsm != null || lidFsm != null)
                    {
                        int index = septicWells.Count;
                        septicWells.Add(new SepticWell
                        {
                            Index = index,
                            WellObject = allGo[i],
                            LevelFsm = levelFsm,
                            LevelVar = levelVar,
                            LidFsm = lidFsm,
                            IsOpen = false
                        });
                    }
                }
            }
        }

        private void InitInspection()
        {
            Transform orderTr = GameObject.Find("INSPECTION")?.transform.Find("Functions/Order") ?? GameObject.Find("INSPECTION")?.transform;
            if (orderTr != null)
            {
                inspectionOrderFsm = orderTr.GetComponent<PlayMakerFSM>();
                if (inspectionOrderFsm != null)
                {
                    inspectionOrderFsm.Initialize();
                    inspectionTriggerEvent = inspectionOrderFsm.AddEvent("MP_TRIGGER");
                    inspectionOrderFsm.AddGlobalTransition(inspectionTriggerEvent, "Pay");

                    inspectionOrderFsm.InsertAction("Pay", () =>
                    {
                        if (isSuppressingInspection)
                        {
                            isSuppressingInspection = false;
                            return;
                        }
                        SendJobAction(JobActionType.InspectionPaid, 0, 0, 0f);
                        OnLog?.Invoke("[Техосмотр] Оплачен техосмотр Satsuma.");
                    }, 0, false);
                }
            }
        }

        private void InitKilju()
        {
            var jokke = GameObject.Find("JOKKE") ?? GameObject.Find("KiljuBuyer") ?? GameObject.Find("Jokke");
            if (jokke != null)
            {
                var fsms = jokke.GetComponentsInChildren<PlayMakerFSM>();
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i].FsmName == "Use" || fsms[i].FsmName == "Order" || fsms[i].FsmName == "Buy")
                    {
                        fsms[i].Initialize();
                        string buyState = fsms[i].HasState("Buy") ? "Buy" : (fsms[i].HasState("Pay") ? "Pay" : null);
                        if (buyState != null)
                        {
                            fsms[i].InsertAction(buyState, () =>
                            {
                                SendJobAction(JobActionType.KiljuBottleSold, 0, 0, 0f);
                                OnLog?.Invoke("[Брага] Продана бутылка килью Йокке.");
                            }, 0, false);
                        }
                    }
                }
            }
        }

        private void InitGrandma()
        {
            var grandma = GameObject.Find("Grandma") ?? GameObject.Find("Sirkka") ?? GameObject.Find("GRANDMA");
            if (grandma != null)
            {
                var fsms = grandma.GetComponentsInChildren<PlayMakerFSM>();
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i].FsmName == "Order" || fsms[i].FsmName == "Use")
                    {
                        fsms[i].Initialize();
                        string state = fsms[i].HasState("Pay") ? "Pay" : (fsms[i].HasState("Give food") ? "Give food" : null);
                        if (state != null)
                        {
                            fsms[i].InsertAction(state, () =>
                            {
                                SendJobAction(JobActionType.GrandmaFoodDelivered, 0, 0, 0f);
                                OnLog?.Invoke("[Бабушка] Доставлена еда бабушке Сиркке.");
                            }, 0, false);
                        }
                    }
                }
            }
        }

        private void InitAdvertFlyers()
        {
            Transform adPile = GameObject.Find("STORE")?.transform.Find("AdvertSpawn/teimo advert pile(itemx)") ?? GameObject.Find("teimo advert pile(itemx)")?.transform;
            if (adPile != null)
            {
                teimoAdvertPileFsm = adPile.GetComponent<PlayMakerFSM>();
                if (teimoAdvertPileFsm != null)
                {
                    teimoAdvertPileFsm.Initialize();
                    teimoSpawnSheetEvent = teimoAdvertPileFsm.AddEvent("MP_SPAWNSHEET");
                    teimoAdvertPileFsm.AddGlobalTransition(teimoSpawnSheetEvent, "Open");

                    teimoAdvertPileFsm.InsertAction("Open", () =>
                    {
                        if (isSuppressingAdvert)
                        {
                            isSuppressingAdvert = false;
                            return;
                        }
                        SendJobAction(JobActionType.FlyerDelivered, 0, 0, 0f);
                    }, -1, false);
                }
            }

            // Почтовые ящики
            mailboxFsms.Clear();
            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i].name == "BoxHatch")
                {
                    var fsm = allGo[i].GetComponent<PlayMakerFSM>();
                    if (fsm != null && fsm.FsmName == "WaitAd")
                    {
                        fsm.Initialize();
                        mailboxFsms.Add(fsm);
                    }
                }
            }
        }

        public void SendMoneySync(float money)
        {
            using (var packet = new PacketWriter(PacketType.MoneySync))
            {
                packet.WriteSingle(money);
                transport.Send(packet);
            }
        }

        public void SendMoneyDelta(float delta)
        {
            if (transport == null || !transport.IsRunning) return;
            using (var packet = new PacketWriter(PacketType.MoneyDelta))
            {
                packet.WriteSingle(delta);
                transport.Send(packet);
            }
        }

        public void SendJobAction(JobActionType actionType, int intParam1, int intParam2, float floatParam)
        {
            using (var packet = new PacketWriter(PacketType.JobAction))
            {
                packet.WriteByte((byte)actionType);
                packet.WriteInt32(intParam1);
                packet.WriteInt32(intParam2);
                packet.WriteSingle(floatParam);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.JobAction)
            {
                JobActionType action = (JobActionType)reader.ReadByte();
                int p1 = reader.ReadInt32();
                int p2 = reader.ReadInt32();
                float fp = reader.ReadSingle();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.JobAction))
                    {
                        relay.WriteByte((byte)action);
                        relay.WriteInt32(p1);
                        relay.WriteInt32(p2);
                        relay.WriteSingle(fp);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteJobAction(action, p1, p2, fp);
            }
            else if (reader.Type == PacketType.MoneySync)
            {
                float money = reader.ReadSingle();
                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.MoneySync))
                    {
                        relay.WriteSingle(money);
                        transport.Send(relay, connectionId);
                    }
                }

                if (playerMoneyVar != null)
                {
                    playerMoneyVar.Value = money;
                    lastSyncedMoney = money;
                    OnLog?.Invoke($"[Финансы] Баланс кошелька: {money:F1} mk");
                }
            }
            else if (reader.Type == PacketType.MoneyDelta)
            {
                float delta = reader.ReadSingle();
                if (transport.IsServer)
                {
                    if (playerMoneyVar != null)
                    {
                        playerMoneyVar.Value += delta;
                        lastSyncedMoney = playerMoneyVar.Value;
                        SendMoneySync(playerMoneyVar.Value);
                        OnLog?.Invoke($"[Финансы] Баланс изменен клиентом на {delta:+0.0;-0.0;0.0} mk. Итог: {playerMoneyVar.Value:F1} mk");
                    }
                }
            }
        }

        private void ApplyRemoteJobAction(JobActionType action, int p1, int p2, float fp)
        {
            try
            {
                switch (action)
                {
                    case JobActionType.FirewoodDelivered:
                        if (p2 == -1) // Раскол полена
                        {
                            if (activeLogJoints.TryGetValue(p1, out var joint) && joint != null)
                            {
                                UnityEngine.Object.Destroy(joint);
                                activeLogJoints.Remove(p1);
                            }
                        }
                        else // Создание нового полена
                        {
                            if (logwallFsm != null)
                            {
                                isSuppressingLog = true;
                                logwallFsm.SendEvent("MP_SPAWN");
                            }
                        }
                        break;

                    case JobActionType.InspectionPaid:
                        if (inspectionOrderFsm != null)
                        {
                            isSuppressingInspection = true;
                            inspectionOrderFsm.SendEvent("MP_TRIGGER");
                        }
                        break;

                    case JobActionType.FlyerDelivered:
                        if (teimoAdvertPileFsm != null)
                        {
                            isSuppressingAdvert = true;
                            teimoAdvertPileFsm.SendEvent("MP_SPAWNSHEET");
                        }
                        break;

                    case JobActionType.KiljuBottleSold:
                        OnLog?.Invoke("[Брага] Другой игрок продал бутылку килью Йокке.");
                        break;

                    case JobActionType.GrandmaFoodDelivered:
                        OnLog?.Invoke("[Бабушка] Другой игрок доставил еду бабушке Сиркке.");
                        break;
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyJobAction Error] {ex.Message}");
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport.IsServer && playerMoneyVar != null)
            {
                SendMoneySync(playerMoneyVar.Value);
            }
        }
    }
}
