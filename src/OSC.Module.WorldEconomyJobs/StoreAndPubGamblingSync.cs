using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldEconomyJobs
{
    /// <summary>
    /// Сетевая синхронизация общего кошелька (PlayerMoney), магазина Теймо (товары, касса, АЗС, взлом витрины и кража автомата)
    /// (PacketType.EconomyWalletAndStoreSync = 130), а также паба Наппо, музыкальных автоматов, игровых автоматов,
    /// видеопокера, дартса, ТВ-лотереи и карточной игры Вентти (21) (PacketType.PubAndGamblingSync = 131).
    /// </summary>
    internal sealed class StoreAndPubGamblingSync
    {
        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private sealed class StoreProductSync
        {
            public string Key;
            public string Name;
            public PlayMakerFSM Fsm;
            public FsmInt Quantity;
            public FsmInt Bought;
            public FsmEvent PurchaseEvent;
            public int LastQuantity;
            public int LastBought;
            public bool DoSync = true;
        }

        private sealed class FuelPumpSync
        {
            public byte Index;
            public string Name;
            public GameObject NozzleObject;
            public PlayMakerFSM Fsm;
            public FsmFloat LitresVar;
            public FsmFloat CostVar;
            public string PumpState;
            public string StopState;
            public bool LastPumping;
            public float LastLitres;
            public float LastCost;
        }

        private sealed class StoreWindowWatcher : MonoBehaviour
        {
            public Action OnDisabledBroken;

            private void OnDisable()
            {
                OnDisabledBroken?.Invoke();
            }
        }

        // Поддействия внутри EconomyProtocol.TeimoRegisterAction
        private const byte StoreSubProductAction = 1;
        private const byte StoreSubRegisterPay = 2;

        // Цели внутри EconomyProtocol.StoreWindowBreak
        private const byte BreakTargetStoreWindow = 1;
        private const byte BreakTargetSlotMachineHeist = 2;

        // Подвиды внутри EconomyProtocol.PubBeerTap
        private const byte PubSubBeerTap = 1;
        private const byte PubSubJukebox = 2;

        // Действия внутри EconomyProtocol.GamblingSlotOrPoker
        private const byte GamblingPokerButton = 1;
        private const byte GamblingPokerBreak = 2;
        private const byte GamblingSlotInsertCoin = 3;
        private const byte GamblingSlotSpin = 4;
        private const byte GamblingSlotCashOut = 5;

        // Подвиды внутри EconomyProtocol.DartsAndLottery
        private const byte ActivityDarts = 1;
        private const byte ActivityLottery = 2;

        // Действия внутри EconomyProtocol.VenttiMatchState
        private const byte VenttiPoll = 0;
        private const byte VenttiDeal = 1;
        private const byte VenttiHit = 2;
        private const byte VenttiStand = 3;
        private const byte VenttiWin = 4;
        private const byte VenttiLose = 5;
        private const byte VenttiBust = 6;
        private const byte VenttiReset = 7;

        private static readonly string[] RegisterResetVars = new string[]
        {
            "QBeer", "QCarBattery", "QCharcoal", "QCoolant", "QExtinguisher", "QMotorOil", "QTwoStroke"
        };

        private static readonly string[] PokerButtonNames = new string[]
        {
            "Pokeri-Buttons-InsertCoin",
            "Pokeri-Buttons-Bet",
            "Pokeri-Buttons-Deal",
            "Pokeri-Buttons-TakeWin",
            "Pokeri-Buttons-Double",
            "Pokeri-Buttons-Double-High",
            "Pokeri-Buttons-Double-Low",
            "Pokeri-Buttons-Hold1",
            "Pokeri-Buttons-Hold2",
            "Pokeri-Buttons-Hold3",
            "Pokeri-Buttons-Hold4",
            "Pokeri-Buttons-Hold5"
        };

        private static readonly string[] FuelPumpObjectNames98 = new string[]
        {
            "gasoline_pistol_98", "FuelPump98", "GasolinePump98"
        };

        private static readonly string[] FuelPumpObjectNamesDiesel = new string[]
        {
            "gasoline_pistol_Diesel", "FuelPumpDiesel", "DieselPump"
        };

        private static readonly string[] FuelPumpObjectNamesFuelOil = new string[]
        {
            "gasoline_pistol_PÖ", "gasoline_pistol_FuelOil", "gasoline_pistol_PO", "FuelPumpFuelOil"
        };

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // 1. Общий кошелёк (PlayerMoney)
        private FsmFloat playerMoneyVar;
        private float lastSyncedMoney = -1f;
        private float nextWalletPollTime;
        private float suppressWalletDetectUntilTime;
        private float protectMoneyUntilTime;
        private float protectedMoneyValue = -1f;
        private bool isApplyingWalletEvent;

        // 2. Магазин Теймо (полки, касса, инвентарь, витрины, угон автомата)
        private GameObject storeGo;
        private readonly List<StoreProductSync> products = new List<StoreProductSync>();
        private readonly Dictionary<string, StoreProductSync> productsByKey = new Dictionary<string, StoreProductSync>();
        private readonly Dictionary<string, StoreProductSync> productsByName = new Dictionary<string, StoreProductSync>();

        private PlayMakerFSM registerFsm;
        private GameObject bagCreator;
        private FsmInt[] registerIntVars;
        private PlayMakerHashTableProxy boughtInventory;
        private bool isSuppressingRegister;
        private float suppressRegisterUntilTime;
        private bool isApplyingRemoteProduct;
        private float suppressProductUntilTime;

        private GameObject storeWindowsIntact;
        private GameObject storeWindowsBroken;
        private GameObject storeWindows1;
        private readonly List<GameObject> storeWindowsBrokenShards = new List<GameObject>();
        private StoreWindowWatcher windowWatcher;
        private PlayMakerFSM breakableWindowFsm;
        private bool isWindowBroken;
        private bool isSuppressingWindow;
        private float suppressWindowUntilTime;

        private PlayMakerFSM hookSlotFsm;
        private string hookSlotBreakState;
        private bool isSlotMachineStolen;
        private bool isSuppressingSlotHeist;
        private float suppressSlotHeistUntilTime;

        // 3. Топливные колонки АЗС (98, Diesel, FuelOil) — никогда не трогаем баки транспорта
        private readonly List<FuelPumpSync> fuelPumps = new List<FuelPumpSync>();
        private bool isSuppressingFuel;
        private float suppressFuelUntilTime;

        // 4. Паб Наппо и музыкальные автоматы
        private PlayMakerFSM pubBeerTapFsm;
        private string pubBeerTapState;
        private bool isSuppressingTap;
        private float suppressTapUntilTime;

        private PlayMakerFSM storeJukeboxFsm;
        private PlayMakerFSM pubJukeboxFsm;
        private bool isSuppressingJukebox;
        private float suppressJukeboxUntilTime;

        // 5. Игровой автомат и видеопокер
        private GameObject videoPokerObj;
        private PlayMakerFSM videoPokerBreakFsm;
        private PlayMakerFSM videoPokerDataFsm;
        private bool isVideoPokerBroken;
        private readonly Dictionary<int, PlayMakerFSM> pokerButtonsByIndex = new Dictionary<int, PlayMakerFSM>();
        private readonly Dictionary<int, string> pokerButtonTargetStates = new Dictionary<int, string>();

        private GameObject slotMachineObj;
        private PlayMakerFSM slotInsertCoinFsm;
        private string slotInsertCoinState;
        private PlayMakerFSM slotButtonFsm;
        private string slotButtonState;
        private PlayMakerFSM slotCashOutFsm;
        private string slotCashOutState;
        private PlayMakerFSM slotDataFsm;
        private bool isSuppressingGambling;
        private float suppressGamblingUntilTime;

        // 6. Дартс
        private GameObject dartsObj;
        private PlayMakerFSM dartsFsm;
        private int lastDartsScore = int.MinValue;
        private int lastDartsThrows = int.MinValue;
        private string lastDartsState = string.Empty;
        private bool isSuppressingDarts;
        private float suppressDartsUntilTime;

        // 7. ТВ-лотерея
        private PlayMakerFSM lotteryNumbersFsm;
        private int lastLottoRound = -1;
        private float lastLottoPot = -1f;
        private bool isSuppressingLottery;
        private float suppressLotteryUntilTime;

        // 8. Карточная игра Вентти (21)
        private GameObject venttiObj;
        private PlayMakerFSM venttiDealerFsm;
        private PlayMakerFSM venttiDataFsm;
        private int lastVenttiBet = -1;
        private int lastVenttiPlayerScore = -1;
        private int lastVenttiDealerScore = -1;
        private string lastVenttiState = string.Empty;
        private readonly HashSet<byte> hookedVenttiActions = new HashSet<byte>();
        private bool isSuppressingVentti;
        private float suppressVenttiUntilTime;

        // Таймеры сканирования и опроса
        private float nextScanTime;
        private float nextStorePollTime;
        private float nextFuelPollTime;
        private float nextMinigamesPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary
        {
            get
            {
                string moneyStr = playerMoneyVar != null ? (playerMoneyVar.Value.ToString("F0") + "mk") : "-";
                string storeStr = storeGo != null ? products.Count.ToString() : "-";
                string pubStr = (pubBeerTapFsm != null || videoPokerObj != null || slotMachineObj != null || lotteryNumbersFsm != null || venttiDealerFsm != null) ? "OK" : "-";
                return $"Кошелёк: {moneyStr}, Магазин: {storeStr}, Колонки: {fuelPumps.Count}/3, Паб/Игры: {pubStr}";
            }
        }

        public StoreAndPubGamblingSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanObjects();
        }

        public void Reset()
        {
            if (windowWatcher != null)
            {
                windowWatcher.OnDisabledBroken = null;
                windowWatcher = null;
            }

            hookedFsmIds.Clear();
            pendingSnapshots.Clear();

            playerMoneyVar = null;
            lastSyncedMoney = -1f;
            nextWalletPollTime = 0f;
            suppressWalletDetectUntilTime = 0f;
            protectMoneyUntilTime = 0f;
            protectedMoneyValue = -1f;
            isApplyingWalletEvent = false;

            storeGo = null;
            products.Clear();
            productsByKey.Clear();
            productsByName.Clear();

            registerFsm = null;
            bagCreator = null;
            registerIntVars = null;
            boughtInventory = null;
            isSuppressingRegister = false;
            suppressRegisterUntilTime = 0f;
            isApplyingRemoteProduct = false;
            suppressProductUntilTime = 0f;

            storeWindowsIntact = null;
            storeWindowsBroken = null;
            storeWindows1 = null;
            storeWindowsBrokenShards.Clear();
            breakableWindowFsm = null;
            isWindowBroken = false;
            isSuppressingWindow = false;
            suppressWindowUntilTime = 0f;

            hookSlotFsm = null;
            hookSlotBreakState = null;
            isSlotMachineStolen = false;
            isSuppressingSlotHeist = false;
            suppressSlotHeistUntilTime = 0f;

            fuelPumps.Clear();
            isSuppressingFuel = false;
            suppressFuelUntilTime = 0f;

            pubBeerTapFsm = null;
            pubBeerTapState = null;
            isSuppressingTap = false;
            suppressTapUntilTime = 0f;

            storeJukeboxFsm = null;
            pubJukeboxFsm = null;
            isSuppressingJukebox = false;
            suppressJukeboxUntilTime = 0f;

            videoPokerObj = null;
            videoPokerBreakFsm = null;
            videoPokerDataFsm = null;
            isVideoPokerBroken = false;
            pokerButtonsByIndex.Clear();
            pokerButtonTargetStates.Clear();

            slotMachineObj = null;
            slotInsertCoinFsm = null;
            slotInsertCoinState = null;
            slotButtonFsm = null;
            slotButtonState = null;
            slotCashOutFsm = null;
            slotCashOutState = null;
            slotDataFsm = null;
            isSuppressingGambling = false;
            suppressGamblingUntilTime = 0f;

            dartsObj = null;
            dartsFsm = null;
            lastDartsScore = int.MinValue;
            lastDartsThrows = int.MinValue;
            lastDartsState = string.Empty;
            isSuppressingDarts = false;
            suppressDartsUntilTime = 0f;

            lotteryNumbersFsm = null;
            lastLottoRound = -1;
            lastLottoPot = -1f;
            isSuppressingLottery = false;
            suppressLotteryUntilTime = 0f;

            venttiObj = null;
            venttiDealerFsm = null;
            venttiDataFsm = null;
            lastVenttiBet = -1;
            lastVenttiPlayerScore = -1;
            lastVenttiDealerScore = -1;
            lastVenttiState = string.Empty;
            hookedVenttiActions.Clear();
            isSuppressingVentti = false;
            suppressVenttiUntilTime = 0f;

            nextScanTime = 0f;
            nextStorePollTime = 0f;
            nextFuelPollTime = 0f;
            nextMinigamesPollTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            if (now >= nextScanTime)
            {
                nextScanTime = now + 5.0f;
                if (NeedsRescan())
                {
                    ScanObjects();
                }
            }

            PollWallet(now);

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextStorePollTime)
            {
                nextStorePollTime = now + 0.5f;
                PollStoreProducts(now);
            }

            if (now >= nextFuelPollTime)
            {
                nextFuelPollTime = now + 0.25f;
                PollFuelPumps(now);
            }

            if (now >= nextMinigamesPollTime)
            {
                nextMinigamesPollTime = now + 0.35f;
                PollDarts(now);
                PollVentti(now);
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanObjects();
            SendStoreSnapshotToClient(clientId);
            SendPubSnapshotToClient(clientId);

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == clientId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }

            pendingSnapshots.Add(new PendingSnapshot
            {
                ClientId = clientId,
                SendAtTime = Time.time + 2.0f
            });
        }

        public void OnClientDisconnected(int clientId)
        {
            int targetId = (ctx != null && ctx.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == targetId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        public void HandleSharedWalletEvent(SharedWalletChangedEvent evt)
        {
            if (evt.SourceModuleId == moduleId) return;

            EnsureMoneyVar();
            float prevMoney = lastSyncedMoney >= 0f
                ? lastSyncedMoney
                : (playerMoneyVar != null ? playerMoneyVar.Value : evt.Money);
            float diff = evt.Money - prevMoney;

            isApplyingWalletEvent = true;
            suppressWalletDetectUntilTime = Time.time + 0.6f;
            protectMoneyUntilTime = 0f;
            try
            {
                if (playerMoneyVar != null)
                {
                    playerMoneyVar.Value = evt.Money;
                }
                lastSyncedMoney = evt.Money;
            }
            finally
            {
                isApplyingWalletEvent = false;
            }

            if (!IsNetworkRunning) return;

            if (ctx.Transport.IsServer)
            {
                SendWalletSync(isDelta: false, val: evt.Money);
            }
            else if (Mathf.Abs(diff) >= 0.01f)
            {
                SendWalletSync(isDelta: true, val: diff);
            }
        }

        private void ProcessPendingSnapshots(float now)
        {
            if (!ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    int clientId = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    ScanObjects();
                    SendStoreSnapshotToClient(clientId);
                    SendPubSnapshotToClient(clientId);
                }
            }
        }

        private bool NeedsRescan()
        {
            return playerMoneyVar == null
                || storeGo == null
                || products.Count == 0
                || registerFsm == null
                || fuelPumps.Count < 3
                || pubBeerTapFsm == null
                || videoPokerObj == null
                || slotMachineObj == null
                || lotteryNumbersFsm == null
                || venttiDealerFsm == null;
        }

        #region Discovery & Hooks

        private void ScanObjects()
        {
            try
            {
                EnsureMoneyVar();
                ScanStore();
                ScanFuelPumps();
                ScanPubAndJukeboxes();
                ScanGambling();
                ScanDartsAndLottery();
                ScanVentti();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Store/Pub] Ошибка сканирования: {ex.Message}");
            }
        }

        private void EnsureMoneyVar()
        {
            if (playerMoneyVar != null) return;
            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                playerMoneyVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
                if (playerMoneyVar != null && lastSyncedMoney < 0f)
                {
                    lastSyncedMoney = playerMoneyVar.Value;
                }
            }
        }

        private bool TryClaimSafe(PlayMakerFSM fsm, string claimContext)
        {
            if (fsm == null || ctx == null || ctx.Ownership == null) return false;
            string currentOwner = ctx.Ownership.GetOwner(fsm);
            if (currentOwner != null && currentOwner != moduleId)
            {
                return false;
            }
            return ctx.Ownership.TryClaim(fsm, moduleId, claimContext);
        }

        private void ScanStore()
        {
            if (storeGo == null)
            {
                storeGo = GameObject.Find("STORE");
            }
            if (storeGo == null) return;

            Transform lodTr = storeGo.transform.Find("LOD");
            if (lodTr != null)
            {
                lodTr.gameObject.SetActive(true);

                PlayMakerFSM storeLodFsm = PlayMakerBridge.FindFsm(storeGo, "LOD");
                if (storeLodFsm != null)
                {
                    storeLodFsm.enabled = false;
                }

                // 1. Товары на полках (Buy FSMs)
                if (products.Count == 0)
                {
                    Transform actTr = lodTr.Find("ActivateStore");
                    if (actTr != null)
                    {
                        PlayMakerFSM[] buyFsms = actTr.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int i = 0; i < buyFsms.Length; i++)
                        {
                            PlayMakerFSM fsm = buyFsms[i];
                            if (fsm == null || fsm.FsmName != "Buy") continue;
                            if (hookedFsmIds.Contains(fsm.GetInstanceID())) continue;
                            if (!TryClaimSafe(fsm, "Store_Buy_" + fsm.gameObject.name)) continue;

                            hookedFsmIds.Add(fsm.GetInstanceID());
                            fsm.InitializeSafe();

                            string key = SceneHasher.GetScenePath(fsm.transform);
                            if (string.IsNullOrEmpty(key)) key = fsm.gameObject.name;

                            var prod = new StoreProductSync
                            {
                                Key = key,
                                Name = fsm.gameObject.name,
                                Fsm = fsm,
                                Quantity = fsm.FsmVariables.FindFsmInt("Quantity"),
                                Bought = fsm.FsmVariables.FindFsmInt("Bought")
                            };
                            prod.LastQuantity = prod.Quantity != null ? prod.Quantity.Value : 0;
                            prod.LastBought = prod.Bought != null ? prod.Bought.Value : 0;

                            prod.PurchaseEvent = fsm.AddSafeEvent("MP_PURCHASE");
                            string purchaseState = fsm.HasState("Check inventory")
                                ? "Check inventory"
                                : (fsm.HasState("Play anim") ? "Play anim" : null);

                            if (purchaseState != null)
                            {
                                fsm.AddSafeGlobalTransition(prod.PurchaseEvent, purchaseState);
                                StoreProductSync captured = prod;
                                fsm.InsertAction(purchaseState, () =>
                                {
                                    if (!captured.DoSync || isApplyingRemoteProduct || Time.time < suppressProductUntilTime)
                                    {
                                        return;
                                    }

                                    int q = captured.Quantity != null ? captured.Quantity.Value : 0;
                                    int b = captured.Bought != null ? captured.Bought.Value : 0;
                                    captured.LastQuantity = q;
                                    captured.LastBought = b;
                                    SendStoreProductAction(captured.Key, captured.Name, true, q, b);
                                }, -1, true);
                            }

                            products.Add(prod);
                            if (!productsByKey.ContainsKey(prod.Key)) productsByKey.Add(prod.Key, prod);
                            if (!productsByName.ContainsKey(prod.Name)) productsByName.Add(prod.Name, prod);
                        }
                    }
                }

                // 2. Пакет покупок
                if (bagCreator == null)
                {
                    Transform bagTr = lodTr.Find("ShopFunctions/BagCreator");
                    if (bagTr != null) bagCreator = bagTr.gameObject;
                }
            }

            // 3. Касса магазина (StoreCashRegister/Register)
            if (registerFsm == null)
            {
                Transform regTr = storeGo.transform.Find("StoreCashRegister/Register");
                if (regTr != null)
                {
                    PlayMakerFSM reg = PlayMakerBridge.FindFsm(regTr.gameObject, "Use")
                                    ?? PlayMakerBridge.FindFsm(regTr.gameObject, "Register")
                                    ?? regTr.GetComponent<PlayMakerFSM>();
                    if (reg != null && !hookedFsmIds.Contains(reg.GetInstanceID()) && TryClaimSafe(reg, "Store_Register"))
                    {
                        registerFsm = reg;
                        hookedFsmIds.Add(reg.GetInstanceID());
                        reg.InitializeSafe();
                        registerIntVars = reg.FsmVariables.IntVariables;

                        if (reg.HasState("Check money"))
                        {
                            reg.AddSafeGlobalTransition("MP_INTERACT", "Check money");
                            reg.InsertAction("Check money", () =>
                            {
                                if (isSuppressingRegister || Time.time < suppressRegisterUntilTime)
                                {
                                    return;
                                }
                                SendStoreRegisterAction();
                            }, 0, true);
                        }
                    }
                }
            }

            // 4. Таблица купленных товаров (Inventory/Bought)
            if (boughtInventory == null)
            {
                Transform invTr = storeGo.transform.Find("Inventory");
                if (invTr != null)
                {
                    PlayMakerHashTableProxy[] proxies = invTr.GetComponents<PlayMakerHashTableProxy>();
                    for (int i = 0; i < proxies.Length; i++)
                    {
                        if (proxies[i] != null && proxies[i].referenceName == "Bought")
                        {
                            boughtInventory = proxies[i];
                            break;
                        }
                    }
                }
            }

            // 5. Витрина магазина Теймо (ночной взлом)
            ScanStoreWindows();

            // 6. Угон игрового автомата буксировочным тросом (HookSlot)
            if (hookSlotFsm == null)
            {
                GameObject hookObj = GameObject.Find("HookSlot");
                if (hookObj != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(hookObj, "Use") ?? hookObj.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Store_HookSlot"))
                    {
                        hookSlotFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();

                        string breakState = fsm.HasState("Remove rope")
                            ? "Remove rope"
                            : (fsm.HasState("Destroy") ? "Destroy" : (fsm.HasState("Break") ? "Break" : null));

                        if (breakState != null)
                        {
                            hookSlotBreakState = breakState;
                            fsm.AddSafeGlobalTransition("MP_SLOT_BREAK", breakState);
                            fsm.InsertAction(breakState, () =>
                            {
                                if (isSuppressingSlotHeist || Time.time < suppressSlotHeistUntilTime || isSlotMachineStolen)
                                {
                                    return;
                                }
                                isSlotMachineStolen = true;
                                SendWindowOrHeistBreak(BreakTargetSlotMachineHeist, true);
                                ctx?.Log?.Invoke("[Ограбление] Игровой автомат сорван буксировочным тросом!");
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void ScanStoreWindows()
        {
            if (storeWindowsIntact == null && storeGo != null)
            {
                Transform gfxTr = storeGo.transform.Find("GFX_Store");
                if (gfxTr != null)
                {
                    Transform winTr = gfxTr.Find("store_windows");
                    if (winTr != null) storeWindowsIntact = winTr.gameObject;

                    Transform breakableTr = gfxTr.Find("BreakableWindows");
                    if (breakableTr != null)
                    {
                        Transform bw = breakableTr.Find("BreakableWindow");
                        if (bw != null && breakableWindowFsm == null)
                        {
                            PlayMakerFSM fsm = bw.GetComponent<PlayMakerFSM>();
                            if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Store_BreakableWindow"))
                            {
                                breakableWindowFsm = fsm;
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                fsm.InitializeSafe();
                                if (fsm.HasState("Shatter"))
                                {
                                    fsm.AddSafeGlobalTransition("MP_SHATTER", "Shatter");
                                    fsm.InsertAction("Shatter", () =>
                                    {
                                        if (isSuppressingWindow || Time.time < suppressWindowUntilTime || isWindowBroken)
                                        {
                                            return;
                                        }
                                        isWindowBroken = true;
                                        SendWindowOrHeistBreak(BreakTargetStoreWindow, true);
                                        ctx?.Log?.Invoke("[Магазин Теймо] Витрина разбита! Сработала сигнализация!");
                                    }, 0, true);
                                }
                            }
                        }

                        Transform brokenTr = breakableTr.Find("store_windows_broken");
                        if (brokenTr != null) storeWindowsBroken = brokenTr.gameObject;

                        Transform win1Tr = breakableTr.Find("store_windows_1");
                        if (win1Tr != null) storeWindows1 = win1Tr.gameObject;
                    }
                }
            }

            if (storeWindowsIntact == null)
            {
                storeWindowsIntact = GameObject.Find("store_windows");
            }

            if (storeWindowsBrokenShards.Count == 0)
            {
                for (int i = 1; i <= 11; i++)
                {
                    GameObject shard = GameObject.Find($"store_windows_broken_{i:D3}");
                    if (shard != null) storeWindowsBrokenShards.Add(shard);
                }
            }

            if (storeWindowsIntact != null && windowWatcher == null)
            {
                windowWatcher = storeWindowsIntact.GetComponent<StoreWindowWatcher>()
                             ?? storeWindowsIntact.AddComponent<StoreWindowWatcher>();
                windowWatcher.OnDisabledBroken = () =>
                {
                    if (ctx == null || !ctx.IsInGameWorld || !IsNetworkRunning) return;
                    if (Time.timeSinceLevelLoad < 1.5f) return;
                    if (isSuppressingWindow || Time.time < suppressWindowUntilTime || isWindowBroken) return;

                    isWindowBroken = true;
                    SendWindowOrHeistBreak(BreakTargetStoreWindow, true);
                    ctx?.Log?.Invoke("[Ограбление] Витрина магазина Теймо разбита!");
                };
            }
        }

        private void ScanFuelPumps()
        {
            TryAddFuelPump(0, "98", FuelPumpObjectNames98);
            TryAddFuelPump(1, "Diesel", FuelPumpObjectNamesDiesel);
            TryAddFuelPump(2, "FuelOil", FuelPumpObjectNamesFuelOil);
        }

        private void TryAddFuelPump(byte index, string pumpLabel, string[] candidateNames)
        {
            for (int i = 0; i < fuelPumps.Count; i++)
            {
                if (fuelPumps[i].Index == index) return;
            }

            GameObject go = FindFirstGameObject(candidateNames);
            if (go == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Use")
                            ?? PlayMakerBridge.FindFsm(go, "Pump")
                            ?? go.GetComponent<PlayMakerFSM>();
            if (fsm == null || hookedFsmIds.Contains(fsm.GetInstanceID())) return;
            if (!TryClaimSafe(fsm, "Store_FuelPump_" + pumpLabel)) return;

            hookedFsmIds.Add(fsm.GetInstanceID());
            fsm.InitializeSafe();

            FsmFloat litresVar = fsm.FsmVariables.FindFsmFloat("FuelLitres")
                              ?? fsm.FsmVariables.FindFsmFloat("Amount")
                              ?? fsm.FsmVariables.FindFsmFloat("Litres")
                              ?? fsm.FsmVariables.FindFsmFloat("Fuel");

            FsmFloat costVar = fsm.FsmVariables.FindFsmFloat("Price")
                            ?? fsm.FsmVariables.FindFsmFloat("Money")
                            ?? fsm.FsmVariables.FindFsmFloat("Cost")
                            ?? fsm.FsmVariables.FindFsmFloat("Total");

            string pumpState = FindFirstExistingState(fsm, "Pump", "Pumping", "Fuel", "State 1");
            string stopState = FindFirstExistingState(fsm, "Stop", "Reset", "Pay", "Idle", "Wait");

            var entry = new FuelPumpSync
            {
                Index = index,
                Name = pumpLabel,
                NozzleObject = go,
                Fsm = fsm,
                LitresVar = litresVar,
                CostVar = costVar,
                PumpState = pumpState,
                StopState = stopState,
                LastLitres = litresVar != null ? litresVar.Value : 0f,
                LastCost = costVar != null ? costVar.Value : 0f,
                LastPumping = false
            };

            if (pumpState != null)
            {
                fsm.InsertAction(pumpState, () =>
                {
                    if (isSuppressingFuel || Time.time < suppressFuelUntilTime) return;
                    entry.LastPumping = true;
                    float l = entry.LitresVar != null ? entry.LitresVar.Value : 0f;
                    float c = entry.CostVar != null ? entry.CostVar.Value : 0f;
                    entry.LastLitres = l;
                    entry.LastCost = c;
                    SendFuelPumpState(entry.Index, true, l, c);
                }, 0, true);
            }

            if (stopState != null && stopState != pumpState)
            {
                fsm.InsertAction(stopState, () =>
                {
                    if (isSuppressingFuel || Time.time < suppressFuelUntilTime) return;
                    if (!entry.LastPumping) return;
                    entry.LastPumping = false;
                    float l = entry.LitresVar != null ? entry.LitresVar.Value : 0f;
                    float c = entry.CostVar != null ? entry.CostVar.Value : 0f;
                    entry.LastLitres = l;
                    entry.LastCost = c;
                    SendFuelPumpState(entry.Index, false, l, c);
                }, 0, true);
            }

            fuelPumps.Add(entry);
        }

        private void ScanPubAndJukeboxes()
        {
            // 1. Пивной кран в пабе Наппо (без каких-либо FSM лодки)
            if (pubBeerTapFsm == null)
            {
                GameObject tapGo = GameObject.Find("pubi_kalia")
                                ?? GameObject.Find("BeerTap")
                                ?? GameObject.Find("BeerTapPub");
                if (tapGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(tapGo, "Use") ?? tapGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Pub_BeerTap"))
                    {
                        pubBeerTapFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();

                        pubBeerTapState = FindFirstExistingState(fsm, "Pour", "Fill", "Serve", "Tap", "Use", "Buy", "State 1");
                        if (pubBeerTapState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_PUB_TAP", pubBeerTapState);
                            fsm.InsertAction(pubBeerTapState, () =>
                            {
                                if (isSuppressingTap || Time.time < suppressTapUntilTime) return;
                                SendPubTapAction();
                            }, 0, true);
                        }
                    }
                }
            }

            // 2. Музыкальный автомат в магазине Теймо
            if (storeJukeboxFsm == null)
            {
                GameObject jbStoreGo = GameObject.Find("JukeboxStore");
                if (jbStoreGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(jbStoreGo, "Jukebox") ?? jbStoreGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Store_Jukebox"))
                    {
                        storeJukeboxFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        if (fsm.HasState("Play"))
                        {
                            fsm.AddSafeGlobalTransition("MP_JUKEBOX_PLAY", "Play");
                            fsm.InsertAction("Play", () =>
                            {
                                if (isSuppressingJukebox || Time.time < suppressJukeboxUntilTime) return;
                                FsmInt sVar = fsm.FsmVariables.FindFsmInt("Song");
                                SendJukeboxPlay(false, sVar != null ? sVar.Value : 1);
                            }, 0, true);
                        }
                    }
                }
            }

            // 3. Музыкальный автомат в пабе Наппо
            if (pubJukeboxFsm == null)
            {
                GameObject jbPubGo = GameObject.Find("JukeboxFleetari") ?? GameObject.Find("JukeboxPub");
                if (jbPubGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(jbPubGo, "Jukebox") ?? jbPubGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Pub_Jukebox"))
                    {
                        pubJukeboxFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        if (fsm.HasState("Play"))
                        {
                            fsm.AddSafeGlobalTransition("MP_JUKEBOX_PLAY", "Play");
                            fsm.InsertAction("Play", () =>
                            {
                                if (isSuppressingJukebox || Time.time < suppressJukeboxUntilTime) return;
                                FsmInt sVar = fsm.FsmVariables.FindFsmInt("Song");
                                SendJukeboxPlay(true, sVar != null ? sVar.Value : 1);
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void ScanGambling()
        {
            // 1. Видеопокер
            if (videoPokerObj == null)
            {
                videoPokerObj = GameObject.Find("VideoPoker") ?? GameObject.Find("videopoker");
                if (videoPokerObj != null)
                {
                    PlayMakerFSM[] fsms = videoPokerObj.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        PlayMakerFSM fsm = fsms[i];
                        if (fsm == null) continue;
                        if (fsm.FsmName == "Break" && videoPokerBreakFsm == null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Pub_VideoPokerBreak"))
                        {
                            videoPokerBreakFsm = fsm;
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            fsm.InitializeSafe();
                            if (fsm.HasState("Destroy"))
                            {
                                fsm.AddSafeGlobalTransition("MP_POKER_BREAK", "Destroy");
                                fsm.InsertAction("Destroy", () =>
                                {
                                    if (isSuppressingGambling || Time.time < suppressGamblingUntilTime || isVideoPokerBroken) return;
                                    isVideoPokerBroken = true;
                                    SendGamblingAction(GamblingPokerBreak, 0, 0);
                                    ctx?.Log?.Invoke("[Ограбление] Видеопокер сорван с креплений!");
                                }, 0, true);
                            }
                        }
                        else if (fsm.FsmName == "Data" && videoPokerDataFsm == null)
                        {
                            videoPokerDataFsm = fsm;
                            fsm.InitializeSafe();
                        }
                    }
                }
            }

            // Кнопки видеопокера
            for (int i = 0; i < PokerButtonNames.Length; i++)
            {
                if (pokerButtonsByIndex.ContainsKey(i)) continue;
                GameObject btnObj = GameObject.Find(PokerButtonNames[i]);
                if (btnObj == null) continue;

                PlayMakerFSM fsm = btnObj.GetComponent<PlayMakerFSM>();
                if (fsm == null || hookedFsmIds.Contains(fsm.GetInstanceID())) continue;
                if (!TryClaimSafe(fsm, "Pub_PokerBtn_" + i)) continue;

                hookedFsmIds.Add(fsm.GetInstanceID());
                fsm.InitializeSafe();

                string targetState = FindFirstExistingState(fsm, "ButtonPressed", "Insert Coin", "Insert coin");
                pokerButtonsByIndex[i] = fsm;
                if (targetState != null)
                {
                    pokerButtonTargetStates[i] = targetState;
                    byte btnIdx = (byte)i;
                    fsm.AddSafeGlobalTransition("MP_POKER_PRESS", targetState);
                    fsm.InsertAction(targetState, () =>
                    {
                        if (isSuppressingGambling || Time.time < suppressGamblingUntilTime) return;
                        SendGamblingAction(GamblingPokerButton, btnIdx, 0);
                    }, 0, true);
                }
            }

            // 2. Игровой автомат (Markka-lama / Slotmachine)
            if (slotMachineObj == null)
            {
                slotMachineObj = GameObject.Find("Slotmachine") ?? GameObject.Find("SlotMachine");
                if (slotMachineObj != null)
                {
                    slotDataFsm = PlayMakerBridge.FindFsm(slotMachineObj, "Data")
                               ?? PlayMakerBridge.FindFsm(slotMachineObj, "Game")
                               ?? PlayMakerBridge.FindFsm(slotMachineObj, "Logic");
                    if (slotDataFsm != null) slotDataFsm.InitializeSafe();
                }
            }

            if (slotInsertCoinFsm == null)
            {
                GameObject coinObj = GameObject.Find("slot_insert_coin");
                if (coinObj != null)
                {
                    PlayMakerFSM fsm = coinObj.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Slot_InsertCoin"))
                    {
                        slotInsertCoinFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        slotInsertCoinState = FindFirstExistingState(fsm, "Insert Coin", "Insert coin", "Insert", "Coin", "State 1");
                        if (slotInsertCoinState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_SLOT_COIN", slotInsertCoinState);
                            fsm.InsertAction(slotInsertCoinState, () =>
                            {
                                if (isSuppressingGambling || Time.time < suppressGamblingUntilTime) return;
                                SendGamblingAction(GamblingSlotInsertCoin, 0, 0);
                            }, 0, true);
                        }
                    }
                }
            }

            if (slotButtonFsm == null)
            {
                GameObject btnObj = GameObject.Find("slot_button");
                if (btnObj != null)
                {
                    PlayMakerFSM fsm = btnObj.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Slot_Button"))
                    {
                        slotButtonFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        slotButtonState = FindFirstExistingState(fsm, "State 1", "Spin", "Button");
                        if (slotButtonState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_SLOT_SPIN", slotButtonState);
                            fsm.InsertAction(slotButtonState, () =>
                            {
                                if (isSuppressingGambling || Time.time < suppressGamblingUntilTime) return;
                                SendGamblingAction(GamblingSlotSpin, 0, 0);
                            }, 0, true);
                        }
                    }
                }
            }

            if (slotCashOutFsm == null)
            {
                GameObject cashObj = GameObject.Find("slot_cash_out");
                if (cashObj != null)
                {
                    PlayMakerFSM fsm = cashObj.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Slot_CashOut"))
                    {
                        slotCashOutFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        slotCashOutState = FindFirstExistingState(fsm, "Cash out", "CashOut", "Pay", "State 1");
                        if (slotCashOutState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_SLOT_CASHOUT", slotCashOutState);
                            fsm.InsertAction(slotCashOutState, () =>
                            {
                                if (isSuppressingGambling || Time.time < suppressGamblingUntilTime) return;
                                SendGamblingAction(GamblingSlotCashOut, 0, 0);
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void ScanDartsAndLottery()
        {
            // 1. Дартс (Darts / Dartboard / Tikkataulu)
            if (dartsFsm == null)
            {
                if (dartsObj == null)
                {
                    dartsObj = GameObject.Find("Darts")
                            ?? GameObject.Find("Dartboard")
                            ?? GameObject.Find("DartBoard")
                            ?? GameObject.Find("Tikkataulu")
                            ?? GameObject.Find("Tikka");
                }

                if (dartsObj != null)
                {
                    PlayMakerFSM candidate = PlayMakerBridge.FindFsm(dartsObj, "Scoring")
                                          ?? PlayMakerBridge.FindFsm(dartsObj, "Points")
                                          ?? PlayMakerBridge.FindFsm(dartsObj, "Score")
                                          ?? PlayMakerBridge.FindFsm(dartsObj, "Game")
                                          ?? PlayMakerBridge.FindFsm(dartsObj, "Data")
                                          ?? dartsObj.GetComponent<PlayMakerFSM>();

                    if (candidate == null)
                    {
                        PlayMakerFSM[] childFsms = dartsObj.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int i = 0; i < childFsms.Length; i++)
                        {
                            if (childFsms[i] != null)
                            {
                                candidate = childFsms[i];
                                break;
                            }
                        }
                    }

                    if (candidate != null && !hookedFsmIds.Contains(candidate.GetInstanceID()) && TryClaimSafe(candidate, "Pub_Darts"))
                    {
                        dartsFsm = candidate;
                        hookedFsmIds.Add(candidate.GetInstanceID());
                        candidate.InitializeSafe();
                        ReadDartsState(out lastDartsScore, out lastDartsThrows, out lastDartsState);

                        string[] dartStates = new string[] { "Score", "Hit", "Add", "Reset" };
                        for (int i = 0; i < dartStates.Length; i++)
                        {
                            string st = dartStates[i];
                            if (candidate.HasState(st))
                            {
                                candidate.AddSafeGlobalTransition("MP_DARTS_" + st.ToUpperInvariant(), st);
                                candidate.InsertAction(st, () =>
                                {
                                    if (isSuppressingDarts || Time.time < suppressDartsUntilTime) return;
                                    ReadDartsState(out int sc, out int th, out string stName);
                                    lastDartsScore = sc;
                                    lastDartsThrows = th;
                                    lastDartsState = stName;
                                    SendDartsState(sc, th, stName);
                                }, -1, true);
                            }
                        }
                    }
                }
            }

            // 2. Еженедельная ТВ-лотерея (Lottery / Numbers)
            if (lotteryNumbersFsm == null)
            {
                GameObject lottoGo = GameObject.Find("Lottery");
                if (lottoGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(lottoGo, "Numbers") ?? lottoGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Pub_Lottery"))
                    {
                        lotteryNumbersFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        ReadLotteryState(out lastLottoRound, out lastLottoPot, out _);

                        if (fsm.HasState("STOP"))
                        {
                            fsm.InsertAction("STOP", () =>
                            {
                                if (isSuppressingLottery || Time.time < suppressLotteryUntilTime) return;
                                ReadLotteryState(out int round, out float pot, out int[] numbers);
                                lastLottoRound = round;
                                lastLottoPot = pot;
                                SendLotteryDraw(round, pot, numbers);
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void ScanVentti()
        {
            if (venttiDealerFsm != null) return;

            if (venttiObj == null)
            {
                venttiObj = GameObject.Find("VenttiPig")
                         ?? GameObject.Find("VenttiPigHouse")
                         ?? GameObject.Find("VenttiTable")
                         ?? GameObject.Find("Ventti");
            }
            if (venttiObj == null) return;

            PlayMakerFSM dealer = PlayMakerBridge.FindFsm(venttiObj, "Use")
                               ?? PlayMakerBridge.FindFsm(venttiObj, "Game")
                               ?? PlayMakerBridge.FindFsm(venttiObj, "Deal")
                               ?? PlayMakerBridge.FindFsm(venttiObj, "Logic")
                               ?? venttiObj.GetComponent<PlayMakerFSM>();

            PlayMakerFSM data = PlayMakerBridge.FindFsm(venttiObj, "Data")
                             ?? PlayMakerBridge.FindFsm(venttiObj, "Cards")
                             ?? PlayMakerBridge.FindFsm(venttiObj, "Score");

            if (dealer == null)
            {
                PlayMakerFSM[] childFsms = venttiObj.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < childFsms.Length; i++)
                {
                    if (childFsms[i] != null)
                    {
                        dealer = childFsms[i];
                        break;
                    }
                }
            }

            if (dealer == null || hookedFsmIds.Contains(dealer.GetInstanceID())) return;
            if (!TryClaimSafe(dealer, "Pub_Ventti")) return;

            venttiDealerFsm = dealer;
            venttiDataFsm = data ?? dealer;
            hookedFsmIds.Add(dealer.GetInstanceID());
            dealer.InitializeSafe();
            if (venttiDataFsm != dealer) venttiDataFsm.InitializeSafe();

            ReadVenttiState(out lastVenttiBet, out lastVenttiPlayerScore, out lastVenttiDealerScore, out lastVenttiState);

            HookVenttiState(dealer, "Deal", VenttiDeal);
            HookVenttiState(dealer, "Card", VenttiDeal);
            HookVenttiState(dealer, "Hit", VenttiHit);
            HookVenttiState(dealer, "Stand", VenttiStand);
            HookVenttiState(dealer, "Win", VenttiWin);
            HookVenttiState(dealer, "Lose", VenttiLose);
            HookVenttiState(dealer, "Bust", VenttiBust);
            HookVenttiState(dealer, "Reset", VenttiReset);
        }

        private void HookVenttiState(PlayMakerFSM fsm, string stateName, byte actionCode)
        {
            if (fsm == null || !fsm.HasState(stateName)) return;

            hookedVenttiActions.Add(actionCode);
            fsm.AddSafeGlobalTransition("MP_VENTTI_" + actionCode, stateName);
            fsm.InsertAction(stateName, () =>
            {
                if (isSuppressingVentti || Time.time < suppressVenttiUntilTime) return;
                ReadVenttiState(out int bet, out int pScore, out int dScore, out string stName);
                lastVenttiBet = bet;
                lastVenttiPlayerScore = pScore;
                lastVenttiDealerScore = dScore;
                lastVenttiState = stName;
                SendVenttiState(actionCode, bet, pScore, dScore, stName);
            }, -1, true);
        }

        #endregion

        #region Polling

        private void PollWallet(float now)
        {
            EnsureMoneyVar();
            if (playerMoneyVar == null || isApplyingWalletEvent) return;

            if (now < protectMoneyUntilTime && protectedMoneyValue >= 0f)
            {
                if (Mathf.Abs(playerMoneyVar.Value - protectedMoneyValue) >= 0.01f)
                {
                    playerMoneyVar.Value = protectedMoneyValue;
                    lastSyncedMoney = protectedMoneyValue;
                }
                return;
            }

            if (now < suppressWalletDetectUntilTime)
            {
                lastSyncedMoney = playerMoneyVar.Value;
                return;
            }

            if (now < nextWalletPollTime) return;
            nextWalletPollTime = now + 0.25f;

            float current = playerMoneyVar.Value;
            if (lastSyncedMoney < 0f)
            {
                lastSyncedMoney = current;
                return;
            }

            float diff = current - lastSyncedMoney;
            if (Mathf.Abs(diff) >= 0.5f)
            {
                lastSyncedMoney = current;
                bool isServer = IsNetworkRunning && ctx.Transport.IsServer;
                PublishWalletBridge(current, isServer);

                if (IsNetworkRunning)
                {
                    if (ctx.Transport.IsServer)
                    {
                        SendWalletSync(isDelta: false, val: current);
                    }
                    else
                    {
                        SendWalletSync(isDelta: true, val: diff);
                    }
                }
            }
        }

        private void PollStoreProducts(float now)
        {
            if (isApplyingRemoteProduct || now < suppressProductUntilTime || products.Count == 0) return;

            for (int i = 0; i < products.Count; i++)
            {
                StoreProductSync prod = products[i];
                if (prod == null || !prod.DoSync) continue;

                int q = prod.Quantity != null ? prod.Quantity.Value : prod.LastQuantity;
                int b = prod.Bought != null ? prod.Bought.Value : prod.LastBought;

                if (q != prod.LastQuantity || b != prod.LastBought)
                {
                    prod.LastQuantity = q;
                    prod.LastBought = b;
                    SendStoreProductAction(prod.Key, prod.Name, false, q, b);
                }
            }
        }

        private void PollFuelPumps(float now)
        {
            if (isSuppressingFuel || now < suppressFuelUntilTime || fuelPumps.Count == 0) return;

            for (int i = 0; i < fuelPumps.Count; i++)
            {
                FuelPumpSync pump = fuelPumps[i];
                if (pump == null || pump.Fsm == null) continue;

                float litres = pump.LitresVar != null ? pump.LitresVar.Value : pump.LastLitres;
                float cost = pump.CostVar != null ? pump.CostVar.Value : pump.LastCost;
                string activeState = pump.Fsm.ActiveStateName ?? string.Empty;
                bool pumping = pump.PumpState != null && string.Equals(activeState, pump.PumpState, StringComparison.Ordinal);

                if (pumping != pump.LastPumping || Mathf.Abs(litres - pump.LastLitres) >= 0.25f || Mathf.Abs(cost - pump.LastCost) >= 0.5f)
                {
                    pump.LastPumping = pumping;
                    pump.LastLitres = litres;
                    pump.LastCost = cost;
                    SendFuelPumpState(pump.Index, pumping, litres, cost);
                }
            }
        }

        private void PollDarts(float now)
        {
            if (isSuppressingDarts || now < suppressDartsUntilTime || dartsFsm == null) return;

            ReadDartsState(out int score, out int throwsCount, out string stateName);
            if (score != lastDartsScore || throwsCount != lastDartsThrows || !string.Equals(stateName, lastDartsState, StringComparison.Ordinal))
            {
                lastDartsScore = score;
                lastDartsThrows = throwsCount;
                lastDartsState = stateName;
                SendDartsState(score, throwsCount, stateName);
            }
        }

        private void PollVentti(float now)
        {
            if (isSuppressingVentti || now < suppressVenttiUntilTime || venttiDealerFsm == null) return;

            ReadVenttiState(out int bet, out int pScore, out int dScore, out string stateName);
            if (bet != lastVenttiBet || pScore != lastVenttiPlayerScore || dScore != lastVenttiDealerScore || !string.Equals(stateName, lastVenttiState, StringComparison.Ordinal))
            {
                lastVenttiBet = bet;
                lastVenttiPlayerScore = pScore;
                lastVenttiDealerScore = dScore;
                lastVenttiState = stateName;
                SendVenttiState(VenttiPoll, bet, pScore, dScore, stateName);
            }
        }

        #endregion

        #region Sending

        private void PublishWalletBridge(float money, bool isAuthoritative)
        {
            ctx?.Events?.Publish(new SharedWalletChangedEvent
            {
                Money = money,
                SourceModuleId = moduleId,
                IsAuthoritative = isAuthoritative
            });
        }

        private void SendWalletSync(bool isDelta, float val)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.WalletSync);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(isDelta);
                p.WriteSingle(val);
                ctx.Transport.Send(p);
            }
        }

        private void SendStoreProductAction(string productKey, string productName, bool isPurchase, int quantity, int bought)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.TeimoRegisterAction);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(StoreSubProductAction);
                p.WriteString(productKey ?? string.Empty);
                p.WriteString(productName ?? string.Empty);
                p.WriteBoolean(isPurchase);
                p.WriteInt32(quantity);
                p.WriteInt32(bought);
                ctx.Transport.Send(p);
            }
        }

        private void SendStoreRegisterAction()
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.TeimoRegisterAction);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(StoreSubRegisterPay);
                ctx.Transport.Send(p);
            }
            ctx?.Log?.Invoke("[Магазин Теймо] Оплата покупок на кассе!");
        }

        private void SendFuelPumpState(byte pumpIndex, bool pumping, float litres, float cost)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.StoreFuelPump);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(pumpIndex);
                p.WriteBoolean(pumping);
                p.WriteSingle(litres);
                p.WriteSingle(cost);
                ctx.Transport.Send(p);
            }
        }

        private void SendWindowOrHeistBreak(byte target, bool broken)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.StoreWindowBreak);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(target);
                p.WriteBoolean(broken);
                ctx.Transport.Send(p);
            }
        }

        private void SendPubTapAction()
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.PubBeerTap);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(PubSubBeerTap);
                p.WriteBoolean(true);
                p.WriteInt32(1);
                ctx.Transport.Send(p);
            }
            ctx?.Log?.Invoke("[Паб] Налито разливное пиво в пабе Наппо");
        }

        private void SendJukeboxPlay(bool isPub, int songIndex)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.PubBeerTap);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(PubSubJukebox);
                p.WriteBoolean(isPub);
                p.WriteInt32(songIndex);
                ctx.Transport.Send(p);
            }
            ctx?.Log?.Invoke($"[Jukebox] Включен трек #{songIndex} ({(isPub ? "Паб" : "Магазин")})");
        }

        private void SendGamblingAction(byte action, byte index, int value)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.GamblingSlotOrPoker);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(action);
                p.WriteByte(index);
                p.WriteInt32(value);
                ctx.Transport.Send(p);
            }
        }

        private void SendDartsState(int score, int throwsCount, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.DartsAndLottery);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(ActivityDarts);
                p.WriteInt32(score);
                p.WriteInt32(throwsCount);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendLotteryDraw(int round, float pot, int[] numbers)
        {
            if (!IsNetworkRunning) return;
            int count = numbers != null ? Mathf.Min(numbers.Length, 10) : 0;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.DartsAndLottery);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(ActivityLottery);
                p.WriteInt32(round);
                p.WriteSingle(pot);
                p.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    p.WriteInt32(numbers[i]);
                }
                ctx.Transport.Send(p);
            }
            ctx?.Log?.Invoke($"[Лотерея] Розыгрыш тиража #{round} (джекпот: {pot:F0} mk)");
        }

        private void SendVenttiState(byte actionCode, int bet, int playerScore, int dealerScore, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.VenttiMatchState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(actionCode);
                p.WriteInt32(bet);
                p.WriteInt32(playerScore);
                p.WriteInt32(dealerScore);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendStoreSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            EnsureMoneyVar();
            float currentMoney = playerMoneyVar != null ? playerMoneyVar.Value : lastSyncedMoney;

            using (var p = new PacketWriter(PacketType.EconomyWalletAndStoreSync))
            {
                p.WriteByte(EconomyProtocol.StoreSnapshot);
                p.WriteInt32(LocalPlayerId);
                p.WriteSingle(currentMoney);

                // 1. Товары на полках
                p.WriteInt32(products.Count);
                for (int i = 0; i < products.Count; i++)
                {
                    StoreProductSync prod = products[i];
                    p.WriteString(prod.Key ?? string.Empty);
                    p.WriteString(prod.Name ?? string.Empty);
                    p.WriteInt32(prod.Quantity != null ? prod.Quantity.Value : 0);
                    p.WriteInt32(prod.Bought != null ? prod.Bought.Value : 0);
                }

                // 2. Витрина и кража автомата
                p.WriteBoolean(isWindowBroken);
                p.WriteBoolean(isSlotMachineStolen);

                // 3. Переменные кассы
                if (registerIntVars != null)
                {
                    p.WriteInt32(registerIntVars.Length);
                    for (int i = 0; i < registerIntVars.Length; i++)
                    {
                        FsmInt v = registerIntVars[i];
                        p.WriteString(v != null ? v.Name : string.Empty);
                        p.WriteInt32(v != null ? v.Value : 0);
                    }
                }
                else
                {
                    p.WriteInt32(0);
                }

                // 4. Bought хэштаблица
                if (boughtInventory != null && boughtInventory._hashTable != null)
                {
                    Hashtable ht = boughtInventory._hashTable;
                    p.WriteInt32(ht.Count);
                    foreach (DictionaryEntry entry in ht)
                    {
                        p.WriteString(entry.Key != null ? entry.Key.ToString() : string.Empty);
                        int val = 0;
                        if (entry.Value is int iv)
                        {
                            val = iv;
                        }
                        else if (entry.Value != null && int.TryParse(entry.Value.ToString(), out int parsed))
                        {
                            val = parsed;
                        }
                        p.WriteInt32(val);
                    }
                }
                else
                {
                    p.WriteInt32(0);
                }

                // 5. Топливные колонки
                p.WriteInt32(fuelPumps.Count);
                for (int i = 0; i < fuelPumps.Count; i++)
                {
                    FuelPumpSync pump = fuelPumps[i];
                    float l = pump.LitresVar != null ? pump.LitresVar.Value : pump.LastLitres;
                    float c = pump.CostVar != null ? pump.CostVar.Value : pump.LastCost;
                    p.WriteByte(pump.Index);
                    p.WriteBoolean(pump.LastPumping);
                    p.WriteSingle(l);
                    p.WriteSingle(c);
                }

                ctx.Transport.SendTo(p, clientId);
            }
        }

        private void SendPubSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            int storeSong = ReadIntVar(storeJukeboxFsm, "Song", 0);
            int pubSong = ReadIntVar(pubJukeboxFsm, "Song", 0);

            int pokerCredits = ReadFirstInt(videoPokerDataFsm, 0, "Credit", "Credits", "Money");
            int pokerBet = ReadFirstInt(videoPokerDataFsm, 0, "Bet", "Panos");

            int slotCredits = ReadFirstInt(slotDataFsm, 0, "Credit", "Credits", "Money");
            int slotBet = ReadFirstInt(slotDataFsm, 0, "Bet", "Panos");

            ReadDartsState(out int dartsScore, out int dartsThrows, out string dartsState);
            ReadLotteryState(out int lottoRound, out float lottoPot, out int[] lottoNums);
            ReadVenttiState(out int venttiBet, out int venttiPlayer, out int venttiDealer, out string venttiState);

            int numCount = lottoNums != null ? Mathf.Min(lottoNums.Length, 10) : 0;

            using (var p = new PacketWriter(PacketType.PubAndGamblingSync))
            {
                p.WriteByte(EconomyProtocol.PubSnapshot);
                p.WriteInt32(LocalPlayerId);

                p.WriteInt32(storeSong);
                p.WriteInt32(pubSong);

                p.WriteBoolean(isVideoPokerBroken);
                p.WriteInt32(pokerCredits);
                p.WriteInt32(pokerBet);

                p.WriteInt32(slotCredits);
                p.WriteInt32(slotBet);

                p.WriteBoolean(dartsFsm != null);
                p.WriteInt32(dartsScore);
                p.WriteInt32(dartsThrows);
                p.WriteString(dartsState ?? string.Empty);

                p.WriteBoolean(lotteryNumbersFsm != null);
                p.WriteInt32(lottoRound);
                p.WriteSingle(lottoPot);
                p.WriteByte((byte)numCount);
                for (int i = 0; i < numCount; i++)
                {
                    p.WriteInt32(lottoNums[i]);
                }

                p.WriteBoolean(venttiDealerFsm != null);
                p.WriteInt32(venttiBet);
                p.WriteInt32(venttiPlayer);
                p.WriteInt32(venttiDealer);
                p.WriteString(venttiState ?? string.Empty);

                ctx.Transport.SendTo(p, clientId);
            }
        }

        #endregion

        #region Packet Handling

        public void HandleStorePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 5) return;

            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case EconomyProtocol.WalletSync:
                    HandleWalletSyncPacket(reader);
                    break;

                case EconomyProtocol.TeimoRegisterAction:
                    HandleTeimoRegisterPacket(reader);
                    break;

                case EconomyProtocol.StoreFuelPump:
                    HandleStoreFuelPumpPacket(reader);
                    break;

                case EconomyProtocol.StoreWindowBreak:
                    HandleStoreWindowBreakPacket(reader);
                    break;

                case EconomyProtocol.StoreSnapshot:
                    ApplyStoreSnapshot(reader);
                    break;
            }
        }

        public void HandlePubPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 5) return;

            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case EconomyProtocol.PubBeerTap:
                    HandlePubBeerTapPacket(reader);
                    break;

                case EconomyProtocol.GamblingSlotOrPoker:
                    HandleGamblingPacket(reader);
                    break;

                case EconomyProtocol.DartsAndLottery:
                    HandleDartsAndLotteryPacket(reader);
                    break;

                case EconomyProtocol.VenttiMatchState:
                    HandleVenttiPacket(reader);
                    break;

                case EconomyProtocol.PubSnapshot:
                    ApplyPubSnapshot(reader);
                    break;
            }
        }

        private void HandleWalletSyncPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 5) return;
            bool isDelta = reader.ReadBoolean();
            float value = reader.ReadSingle();

            EnsureMoneyVar();

            if (isDelta)
            {
                if (ctx?.Transport == null || !ctx.Transport.IsServer) return;

                isApplyingWalletEvent = true;
                suppressWalletDetectUntilTime = Time.time + 0.6f;
                protectMoneyUntilTime = 0f;
                try
                {
                    if (playerMoneyVar != null)
                    {
                        playerMoneyVar.Value += value;
                        lastSyncedMoney = playerMoneyVar.Value;
                    }
                    else if (lastSyncedMoney >= 0f)
                    {
                        lastSyncedMoney += value;
                    }
                }
                finally
                {
                    isApplyingWalletEvent = false;
                }

                if (lastSyncedMoney >= 0f)
                {
                    PublishWalletBridge(lastSyncedMoney, true);
                    SendWalletSync(isDelta: false, val: lastSyncedMoney);
                }
            }
            else
            {
                if (ctx?.Transport != null && ctx.Transport.IsServer) return;

                isApplyingWalletEvent = true;
                suppressWalletDetectUntilTime = Time.time + 0.6f;
                if (Time.time < protectMoneyUntilTime)
                {
                    protectedMoneyValue = value;
                }
                try
                {
                    if (playerMoneyVar != null)
                    {
                        playerMoneyVar.Value = value;
                    }
                    lastSyncedMoney = value;
                }
                finally
                {
                    isApplyingWalletEvent = false;
                }

                PublishWalletBridge(value, true);
            }
        }

        private void HandleTeimoRegisterPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;
            byte subAction = reader.ReadByte();

            if (subAction == StoreSubProductAction)
            {
                if (reader.UnreadBytes < 9) return;
                string key = reader.ReadString();
                string name = reader.ReadString();
                bool isPurchase = reader.ReadBoolean();
                int qty = reader.ReadInt32();
                int bgt = reader.ReadInt32();
                ApplyRemoteProductAction(key, name, isPurchase, qty, bgt);
            }
            else if (subAction == StoreSubRegisterPay)
            {
                ApplyRemoteRegister();
            }
        }

        private void HandleStoreFuelPumpPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 10) return;
            byte pumpIdx = reader.ReadByte();
            bool pumping = reader.ReadBoolean();
            float litres = reader.ReadSingle();
            float cost = reader.ReadSingle();

            ApplyRemoteFuelPump(pumpIdx, pumping, litres, cost);
        }

        private void HandleStoreWindowBreakPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 2) return;
            byte target = reader.ReadByte();
            bool broken = reader.ReadBoolean();

            if (target == BreakTargetStoreWindow)
            {
                ApplyRemoteWindowBroken(broken);
            }
            else if (target == BreakTargetSlotMachineHeist)
            {
                ApplyRemoteSlotHeist(broken);
            }
        }

        private void HandlePubBeerTapPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 6) return;
            byte subKind = reader.ReadByte();
            bool isPub = reader.ReadBoolean();
            int param = reader.ReadInt32();

            if (subKind == PubSubBeerTap)
            {
                ApplyRemotePubTap();
            }
            else if (subKind == PubSubJukebox)
            {
                ApplyRemoteJukebox(isPub, param);
            }
        }

        private void HandleGamblingPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 6) return;
            byte action = reader.ReadByte();
            byte index = reader.ReadByte();
            int value = reader.ReadInt32();

            ApplyRemoteGambling(action, index, value);
        }

        private void HandleDartsAndLotteryPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;
            byte subKind = reader.ReadByte();

            if (subKind == ActivityDarts)
            {
                if (reader.UnreadBytes < 8) return;
                int score = reader.ReadInt32();
                int throwsCount = reader.ReadInt32();
                string stateName = reader.UnreadBytes > 0 ? reader.ReadString() : string.Empty;
                ApplyRemoteDarts(score, throwsCount, stateName);
            }
            else if (subKind == ActivityLottery)
            {
                if (reader.UnreadBytes < 9) return;
                int round = reader.ReadInt32();
                float pot = reader.ReadSingle();
                byte count = reader.ReadByte();
                int[] nums = new int[count];
                for (int i = 0; i < count && reader.UnreadBytes >= 4; i++)
                {
                    nums[i] = reader.ReadInt32();
                }
                ApplyRemoteLottery(round, pot, nums, triggerResults: true);
            }
        }

        private void HandleVenttiPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 13) return;
            byte actionCode = reader.ReadByte();
            int bet = reader.ReadInt32();
            int pScore = reader.ReadInt32();
            int dScore = reader.ReadInt32();
            string stateName = reader.UnreadBytes > 0 ? reader.ReadString() : string.Empty;

            ApplyRemoteVentti(actionCode, bet, pScore, dScore, stateName, triggerFsm: actionCode != VenttiPoll);
        }

        #endregion

        #region Remote Application

        private void ProtectMoneyFromRemoteFsm(float durationSeconds)
        {
            EnsureMoneyVar();
            float target = lastSyncedMoney >= 0f ? lastSyncedMoney : (playerMoneyVar != null ? playerMoneyVar.Value : -1f);
            if (target < 0f) return;

            protectedMoneyValue = target;
            protectMoneyUntilTime = Mathf.Max(protectMoneyUntilTime, Time.time + durationSeconds);
            suppressWalletDetectUntilTime = Mathf.Max(suppressWalletDetectUntilTime, Time.time + durationSeconds + 0.35f);
        }

        private StoreProductSync FindProduct(string key, string name)
        {
            if (!string.IsNullOrEmpty(key) && productsByKey.TryGetValue(key, out StoreProductSync byKey))
            {
                return byKey;
            }
            if (!string.IsNullOrEmpty(name) && productsByName.TryGetValue(name, out StoreProductSync byName))
            {
                return byName;
            }
            return null;
        }

        private void ApplyRemoteProductAction(string key, string name, bool isPurchase, int qty, int bgt)
        {
            StoreProductSync prod = FindProduct(key, name);
            if (prod == null) return;

            isApplyingRemoteProduct = true;
            suppressProductUntilTime = Time.time + 0.5f;
            prod.DoSync = false;
            try
            {
                if (prod.Quantity != null) prod.Quantity.Value = qty;
                if (prod.Bought != null) prod.Bought.Value = bgt;
                prod.LastQuantity = qty;
                prod.LastBought = bgt;

                if (isPurchase && prod.PurchaseEvent != null && prod.Fsm != null)
                {
                    prod.Fsm.SendEvent(prod.PurchaseEvent.Name);
                }
            }
            finally
            {
                prod.DoSync = true;
                isApplyingRemoteProduct = false;
            }
        }

        private void ApplyRemoteRegister()
        {
            isSuppressingRegister = true;
            suppressRegisterUntilTime = Time.time + 0.6f;
            ProtectMoneyFromRemoteFsm(0.35f);
            try
            {
                if (registerIntVars != null)
                {
                    for (int i = 0; i < registerIntVars.Length; i++)
                    {
                        FsmInt v = registerIntVars[i];
                        if (v != null && ContainsString(RegisterResetVars, v.Name))
                        {
                            v.Value = 0;
                        }
                    }
                }

                if (bagCreator != null)
                {
                    bagCreator.SetActive(true);
                }

                if (registerFsm != null)
                {
                    registerFsm.SendEvent("MP_INTERACT");
                }

                ctx?.Log?.Invoke("[Магазин Теймо] Напарник оплатил покупки на кассе!");
            }
            finally
            {
                isSuppressingRegister = false;
            }
        }

        private void ApplyRemoteFuelPump(byte pumpIdx, bool pumping, float litres, float cost)
        {
            FuelPumpSync pump = null;
            for (int i = 0; i < fuelPumps.Count; i++)
            {
                if (fuelPumps[i].Index == pumpIdx)
                {
                    pump = fuelPumps[i];
                    break;
                }
            }
            if (pump == null) return;

            isSuppressingFuel = true;
            suppressFuelUntilTime = Time.time + 0.45f;
            try
            {
                if (pump.LitresVar != null) pump.LitresVar.Value = litres;
                if (pump.CostVar != null) pump.CostVar.Value = cost;
                pump.LastPumping = pumping;
                pump.LastLitres = litres;
                pump.LastCost = cost;
            }
            finally
            {
                isSuppressingFuel = false;
            }
        }

        private void ApplyRemoteWindowBroken(bool broken)
        {
            isSuppressingWindow = true;
            suppressWindowUntilTime = Time.time + 0.6f;
            isWindowBroken = broken;
            try
            {
                if (storeWindowsIntact != null) storeWindowsIntact.SetActive(!broken);
                if (storeWindows1 != null) storeWindows1.SetActive(!broken);
                if (storeWindowsBroken != null) storeWindowsBroken.SetActive(broken);

                for (int i = 0; i < storeWindowsBrokenShards.Count; i++)
                {
                    if (storeWindowsBrokenShards[i] != null)
                    {
                        storeWindowsBrokenShards[i].SetActive(broken);
                    }
                }

                if (broken && breakableWindowFsm != null && breakableWindowFsm.ActiveStateName != "Shatter")
                {
                    breakableWindowFsm.SendEvent("MP_SHATTER");
                }

                if (broken)
                {
                    ctx?.Log?.Invoke("[Магазин Теймо] Витрина разбита напарником!");
                }
            }
            finally
            {
                isSuppressingWindow = false;
            }
        }

        private void ApplyRemoteSlotHeist(bool broken)
        {
            isSuppressingSlotHeist = true;
            suppressSlotHeistUntilTime = Time.time + 0.6f;
            isSlotMachineStolen = broken;
            try
            {
                if (broken && hookSlotFsm != null)
                {
                    if (hookSlotBreakState != null && hookSlotFsm.ActiveStateName != hookSlotBreakState)
                    {
                        hookSlotFsm.SendEvent("MP_SLOT_BREAK");
                    }
                    else
                    {
                        hookSlotFsm.SendEvent("ROPE_BREAK");
                    }
                    ctx?.Log?.Invoke("[Ограбление] Игровой автомат сорван напарником!");
                }
            }
            finally
            {
                isSuppressingSlotHeist = false;
            }
        }

        private void ApplyRemotePubTap()
        {
            if (pubBeerTapFsm == null) return;

            isSuppressingTap = true;
            suppressTapUntilTime = Time.time + 0.5f;
            ProtectMoneyFromRemoteFsm(0.35f);
            try
            {
                if (pubBeerTapState != null)
                {
                    pubBeerTapFsm.SendEvent("MP_PUB_TAP");
                }
                else
                {
                    pubBeerTapFsm.SendEvent("USE");
                }
                ctx?.Log?.Invoke("[Паб] Друг налил пиво в баре");
            }
            finally
            {
                isSuppressingTap = false;
            }
        }

        private void ApplyRemoteJukebox(bool isPub, int songIndex)
        {
            isSuppressingJukebox = true;
            suppressJukeboxUntilTime = Time.time + 0.5f;
            ProtectMoneyFromRemoteFsm(0.35f);
            try
            {
                PlayMakerFSM jb = isPub ? pubJukeboxFsm : storeJukeboxFsm;
                if (jb != null)
                {
                    FsmInt sVar = jb.FsmVariables.FindFsmInt("Song");
                    if (sVar != null) sVar.Value = songIndex;

                    if (jb.HasState("Play"))
                    {
                        jb.SendEvent("MP_JUKEBOX_PLAY");
                    }
                    else
                    {
                        jb.SendEvent("PLAY");
                    }
                }
                ctx?.Log?.Invoke($"[Jukebox] Друг запустил песню #{songIndex} в {(isPub ? "пабе" : "магазине Теймо")}");
            }
            finally
            {
                isSuppressingJukebox = false;
            }
        }

        private void ApplyRemoteGambling(byte action, byte index, int value)
        {
            isSuppressingGambling = true;
            suppressGamblingUntilTime = Time.time + 0.5f;
            ProtectMoneyFromRemoteFsm(0.35f);
            try
            {
                switch (action)
                {
                    case GamblingPokerButton:
                        if (pokerButtonsByIndex.TryGetValue(index, out PlayMakerFSM btnFsm) && btnFsm != null)
                        {
                            if (pokerButtonTargetStates.ContainsKey(index))
                            {
                                btnFsm.SendEvent("MP_POKER_PRESS");
                            }
                        }
                        break;

                    case GamblingPokerBreak:
                        isVideoPokerBroken = true;
                        if (videoPokerBreakFsm != null && videoPokerBreakFsm.ActiveStateName != "Destroy")
                        {
                            videoPokerBreakFsm.SendEvent("MP_POKER_BREAK");
                            ctx?.Log?.Invoke("[Ограбление] Видеопокер сорван напарником!");
                        }
                        break;

                    case GamblingSlotInsertCoin:
                        if (slotInsertCoinFsm != null && slotInsertCoinState != null)
                        {
                            slotInsertCoinFsm.SendEvent("MP_SLOT_COIN");
                        }
                        break;

                    case GamblingSlotSpin:
                        if (slotButtonFsm != null && slotButtonState != null)
                        {
                            slotButtonFsm.SendEvent("MP_SLOT_SPIN");
                        }
                        break;

                    case GamblingSlotCashOut:
                        if (slotCashOutFsm != null && slotCashOutState != null)
                        {
                            slotCashOutFsm.SendEvent("MP_SLOT_CASHOUT");
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingGambling = false;
            }
        }

        private void ApplyRemoteDarts(int score, int throwsCount, string stateName)
        {
            lastDartsScore = score;
            lastDartsThrows = throwsCount;
            lastDartsState = stateName ?? string.Empty;

            if (dartsFsm == null) return;

            isSuppressingDarts = true;
            suppressDartsUntilTime = Time.time + 0.5f;
            try
            {
                WriteFirstInt(dartsFsm, score, "Score", "Points", "Total", "LastScore");
                WriteFirstInt(dartsFsm, throwsCount, "Throws", "Throw", "Round", "DartsLeft");

                if (string.Equals(stateName, "Reset", StringComparison.OrdinalIgnoreCase) && dartsFsm.HasState("Reset"))
                {
                    dartsFsm.SendEvent("MP_DARTS_RESET");
                }
            }
            finally
            {
                isSuppressingDarts = false;
            }
        }

        private void ApplyRemoteLottery(int round, float pot, int[] numbers, bool triggerResults)
        {
            lastLottoRound = round;
            lastLottoPot = pot;

            if (lotteryNumbersFsm == null) return;

            isSuppressingLottery = true;
            suppressLotteryUntilTime = Time.time + 0.8f;
            try
            {
                FsmInt roundVar = lotteryNumbersFsm.FsmVariables.FindFsmInt("CurrentRound");
                if (roundVar != null) roundVar.Value = round;

                FsmFloat potVar = lotteryNumbersFsm.FsmVariables.FindFsmFloat("NationalPotF");
                if (potVar != null) potVar.Value = pot;

                if (numbers != null && numbers.Length > 0)
                {
                    WriteLotteryNumbers(numbers);
                }

                if (triggerResults)
                {
                    lotteryNumbersFsm.SendEvent("RESULTS");
                    ctx?.Log?.Invoke($"[Лотерея] Тираж #{round} разыгран (джекпот: {pot:F0} mk)");
                }
            }
            finally
            {
                isSuppressingLottery = false;
            }
        }

        private void ApplyRemoteVentti(byte actionCode, int bet, int playerScore, int dealerScore, string stateName, bool triggerFsm)
        {
            lastVenttiBet = bet;
            lastVenttiPlayerScore = playerScore;
            lastVenttiDealerScore = dealerScore;
            lastVenttiState = stateName ?? string.Empty;

            isSuppressingVentti = true;
            suppressVenttiUntilTime = Time.time + 0.6f;
            if (triggerFsm)
            {
                ProtectMoneyFromRemoteFsm(0.35f);
            }

            try
            {
                PlayMakerFSM targetFsm = venttiDataFsm ?? venttiDealerFsm;
                if (targetFsm != null && targetFsm.FsmVariables != null)
                {
                    WriteFirstInt(targetFsm, bet, "Bet", "Panos");
                    WriteFirstInt(targetFsm, playerScore, "PlayerScore", "Player", "ScorePlayer", "CardsPlayer");
                    WriteFirstInt(targetFsm, dealerScore, "DealerScore", "Dealer", "PigScore", "ScoreDealer");
                }

                if (triggerFsm && venttiDealerFsm != null && actionCode >= VenttiDeal && actionCode <= VenttiReset)
                {
                    if (!string.Equals(venttiDealerFsm.ActiveStateName, stateName, StringComparison.Ordinal))
                    {
                        if (hookedVenttiActions.Contains(actionCode))
                        {
                            venttiDealerFsm.SendEvent("MP_VENTTI_" + actionCode);
                        }
                        else if (actionCode == VenttiDeal)
                        {
                            venttiDealerFsm.SendEvent("DEAL");
                        }
                    }
                }
            }
            finally
            {
                isSuppressingVentti = false;
            }
        }

        private void ApplyStoreSnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 8) return;

            float money = reader.ReadSingle();
            if (money >= 0f)
            {
                EnsureMoneyVar();
                isApplyingWalletEvent = true;
                suppressWalletDetectUntilTime = Time.time + 0.6f;
                try
                {
                    if (playerMoneyVar != null) playerMoneyVar.Value = money;
                    lastSyncedMoney = money;
                }
                finally
                {
                    isApplyingWalletEvent = false;
                }
                PublishWalletBridge(money, true);
            }

            // 1. Товары
            int prodCount = reader.ReadInt32();
            isApplyingRemoteProduct = true;
            suppressProductUntilTime = Time.time + 0.5f;
            try
            {
                for (int i = 0; i < prodCount; i++)
                {
                    string key = reader.ReadString();
                    string name = reader.ReadString();
                    int qty = reader.ReadInt32();
                    int bgt = reader.ReadInt32();

                    StoreProductSync prod = FindProduct(key, name);
                    if (prod != null)
                    {
                        prod.DoSync = false;
                        try
                        {
                            if (prod.Quantity != null) prod.Quantity.Value = qty;
                            if (prod.Bought != null) prod.Bought.Value = bgt;
                            prod.LastQuantity = qty;
                            prod.LastBought = bgt;
                        }
                        finally
                        {
                            prod.DoSync = true;
                        }
                    }
                }
            }
            finally
            {
                isApplyingRemoteProduct = false;
            }

            // 2. Витрина и кража автомата
            if (reader.UnreadBytes < 6) return;
            bool winBroken = reader.ReadBoolean();
            bool slotStolen = reader.ReadBoolean();
            if (winBroken || isWindowBroken)
            {
                ApplyRemoteWindowBroken(winBroken);
            }
            if (slotStolen)
            {
                ApplyRemoteSlotHeist(true);
            }

            // 3. Переменные кассы
            int regCount = reader.ReadInt32();
            for (int i = 0; i < regCount; i++)
            {
                string varName = reader.ReadString();
                int varVal = reader.ReadInt32();
                if (registerIntVars != null)
                {
                    for (int j = 0; j < registerIntVars.Length; j++)
                    {
                        if (registerIntVars[j] != null && registerIntVars[j].Name == varName)
                        {
                            registerIntVars[j].Value = varVal;
                            break;
                        }
                    }
                }
            }

            // 4. Bought хэштаблица
            if (reader.UnreadBytes < 4) return;
            int htCount = reader.ReadInt32();
            for (int i = 0; i < htCount; i++)
            {
                string k = reader.ReadString();
                int v = reader.ReadInt32();
                if (boughtInventory != null && boughtInventory._hashTable != null && !string.IsNullOrEmpty(k))
                {
                    boughtInventory._hashTable[k] = v;
                }
            }

            // 5. Топливные колонки
            if (reader.UnreadBytes < 4) return;
            int pumpCount = reader.ReadInt32();
            for (int i = 0; i < pumpCount && reader.UnreadBytes >= 10; i++)
            {
                byte pIdx = reader.ReadByte();
                bool pumping = reader.ReadBoolean();
                float litres = reader.ReadSingle();
                float cost = reader.ReadSingle();
                ApplyRemoteFuelPump(pIdx, pumping, litres, cost);
            }
        }

        private void ApplyPubSnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 17) return;

            int storeSong = reader.ReadInt32();
            int pubSong = reader.ReadInt32();
            if (storeJukeboxFsm != null && storeSong > 0)
            {
                FsmInt sv = storeJukeboxFsm.FsmVariables.FindFsmInt("Song");
                if (sv != null) sv.Value = storeSong;
            }
            if (pubJukeboxFsm != null && pubSong > 0)
            {
                FsmInt sv = pubJukeboxFsm.FsmVariables.FindFsmInt("Song");
                if (sv != null) sv.Value = pubSong;
            }

            bool pokerBroken = reader.ReadBoolean();
            int pokerCredits = reader.ReadInt32();
            int pokerBet = reader.ReadInt32();
            if (pokerBroken)
            {
                ApplyRemoteGambling(GamblingPokerBreak, 0, 0);
            }
            if (videoPokerDataFsm != null)
            {
                WriteFirstInt(videoPokerDataFsm, pokerCredits, "Credit", "Credits", "Money");
                WriteFirstInt(videoPokerDataFsm, pokerBet, "Bet", "Panos");
            }

            int slotCredits = reader.ReadInt32();
            int slotBet = reader.ReadInt32();
            if (slotDataFsm != null)
            {
                WriteFirstInt(slotDataFsm, slotCredits, "Credit", "Credits", "Money");
                WriteFirstInt(slotDataFsm, slotBet, "Bet", "Panos");
            }

            if (reader.UnreadBytes < 10) return;
            bool hasDarts = reader.ReadBoolean();
            int dartsScore = reader.ReadInt32();
            int dartsThrows = reader.ReadInt32();
            string dartsState = reader.ReadString();
            if (hasDarts)
            {
                ApplyRemoteDarts(dartsScore, dartsThrows, dartsState);
            }

            if (reader.UnreadBytes < 10) return;
            bool hasLottery = reader.ReadBoolean();
            int lottoRound = reader.ReadInt32();
            float lottoPot = reader.ReadSingle();
            byte numCount = reader.ReadByte();
            int[] lottoNums = new int[numCount];
            for (int i = 0; i < numCount && reader.UnreadBytes >= 4; i++)
            {
                lottoNums[i] = reader.ReadInt32();
            }
            if (hasLottery && lottoRound >= 0)
            {
                ApplyRemoteLottery(lottoRound, lottoPot, lottoNums, triggerResults: false);
            }

            if (reader.UnreadBytes < 13) return;
            bool hasVentti = reader.ReadBoolean();
            int venttiBet = reader.ReadInt32();
            int venttiPlayer = reader.ReadInt32();
            int venttiDealer = reader.ReadInt32();
            string venttiState = reader.ReadString();
            if (hasVentti)
            {
                ApplyRemoteVentti(VenttiPoll, venttiBet, venttiPlayer, venttiDealer, venttiState, triggerFsm: false);
            }
        }

        #endregion

        #region Variable & Lookup Helpers

        private static GameObject FindFirstGameObject(string[] names)
        {
            if (names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                GameObject go = GameObject.Find(names[i]);
                if (go != null) return go;
            }
            return null;
        }

        private static string FindFirstExistingState(PlayMakerFSM fsm, params string[] candidates)
        {
            if (fsm == null || candidates == null) return null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (fsm.HasState(candidates[i])) return candidates[i];
            }
            return null;
        }

        private static bool ContainsString(string[] array, string value)
        {
            if (array == null || value == null) return false;
            for (int i = 0; i < array.Length; i++)
            {
                if (string.Equals(array[i], value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static int ReadIntVar(PlayMakerFSM fsm, string varName, int fallback)
        {
            if (fsm == null || fsm.FsmVariables == null) return fallback;
            FsmInt iv = fsm.FsmVariables.FindFsmInt(varName);
            return iv != null ? iv.Value : fallback;
        }

        private static int ReadFirstInt(PlayMakerFSM fsm, int fallback, params string[] names)
        {
            if (fsm == null || fsm.FsmVariables == null || names == null) return fallback;
            for (int i = 0; i < names.Length; i++)
            {
                FsmInt iv = fsm.FsmVariables.FindFsmInt(names[i]);
                if (iv != null) return iv.Value;

                FsmFloat fv = fsm.FsmVariables.FindFsmFloat(names[i]);
                if (fv != null) return Mathf.RoundToInt(fv.Value);
            }
            return fallback;
        }

        private static void WriteFirstInt(PlayMakerFSM fsm, int value, params string[] names)
        {
            if (fsm == null || fsm.FsmVariables == null || names == null) return;
            for (int i = 0; i < names.Length; i++)
            {
                FsmInt iv = fsm.FsmVariables.FindFsmInt(names[i]);
                if (iv != null)
                {
                    iv.Value = value;
                    return;
                }

                FsmFloat fv = fsm.FsmVariables.FindFsmFloat(names[i]);
                if (fv != null)
                {
                    fv.Value = value;
                    return;
                }
            }
        }

        private void ReadDartsState(out int score, out int throwsCount, out string stateName)
        {
            score = 0;
            throwsCount = 0;
            stateName = string.Empty;
            if (dartsFsm == null) return;

            stateName = dartsFsm.ActiveStateName ?? string.Empty;
            score = ReadFirstInt(dartsFsm, 0, "Score", "Points", "Total", "LastScore");
            throwsCount = ReadFirstInt(dartsFsm, 0, "Throws", "Throw", "Round", "DartsLeft");
        }

        private void ReadLotteryState(out int round, out float pot, out int[] numbers)
        {
            round = 0;
            pot = 0f;
            numbers = null;
            if (lotteryNumbersFsm == null || lotteryNumbersFsm.FsmVariables == null) return;

            FsmInt roundVar = lotteryNumbersFsm.FsmVariables.FindFsmInt("CurrentRound");
            if (roundVar != null) round = roundVar.Value;

            FsmFloat potVar = lotteryNumbersFsm.FsmVariables.FindFsmFloat("NationalPotF");
            if (potVar != null) pot = potVar.Value;

            var list = new List<int>(7);
            for (int i = 1; i <= 7; i++)
            {
                FsmInt nv = lotteryNumbersFsm.FsmVariables.FindFsmInt("Num" + i)
                         ?? lotteryNumbersFsm.FsmVariables.FindFsmInt("Number" + i)
                         ?? lotteryNumbersFsm.FsmVariables.FindFsmInt("Winning" + i);
                if (nv != null) list.Add(nv.Value);
            }
            numbers = list.Count > 0 ? list.ToArray() : new int[0];
        }

        private void WriteLotteryNumbers(int[] numbers)
        {
            if (lotteryNumbersFsm == null || lotteryNumbersFsm.FsmVariables == null || numbers == null) return;

            for (int i = 0; i < numbers.Length && i < 7; i++)
            {
                int idx = i + 1;
                FsmInt nv = lotteryNumbersFsm.FsmVariables.FindFsmInt("Num" + idx)
                         ?? lotteryNumbersFsm.FsmVariables.FindFsmInt("Number" + idx)
                         ?? lotteryNumbersFsm.FsmVariables.FindFsmInt("Winning" + idx);
                if (nv != null) nv.Value = numbers[i];
            }
        }

        private void ReadVenttiState(out int bet, out int playerScore, out int dealerScore, out string stateName)
        {
            bet = 0;
            playerScore = 0;
            dealerScore = 0;
            stateName = venttiDealerFsm != null ? (venttiDealerFsm.ActiveStateName ?? string.Empty) : string.Empty;

            PlayMakerFSM src = venttiDataFsm ?? venttiDealerFsm;
            if (src == null) return;

            bet = ReadFirstInt(src, 0, "Bet", "Panos");
            playerScore = ReadFirstInt(src, 0, "PlayerScore", "Player", "ScorePlayer", "CardsPlayer");
            dealerScore = ReadFirstInt(src, 0, "DealerScore", "Dealer", "PigScore", "ScoreDealer");
        }

        #endregion
    }
}
