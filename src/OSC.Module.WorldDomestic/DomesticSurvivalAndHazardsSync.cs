using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldDomestic
{
    /// <summary>
    /// Синхронизация бытовых приборов, жарки на гриле, физиологии, снаряжения (PacketType = 112)
    /// и опасностей/крышек контейнеров (PacketType = 113).
    /// </summary>
    internal sealed class DomesticSurvivalAndHazardsSync
    {
        private enum ApplianceKind : byte
        {
            CoffeeMaker = 1,
            Fireplace = 2,
            Grill = 3,
            FishTrap = 4,
            Computer386 = 5
        }

        private enum CookStage : byte
        {
            Raw = 0,
            Cooked = 1,
            Burned = 2
        }

        private enum StatusEffectKind : byte
        {
            Vomit = 1,
            Blindness = 2
        }

        private enum GearKind : byte
        {
            Wristwatch = 1,
            RadarDetector = 2
        }

        private enum ContainerKind : byte
        {
            SpannerSetLid = 1,
            RatchetSetLid = 2,
            GasolineCap = 3,
            DieselCap = 4,
            TwoStrokeCap = 5
        }

        private sealed class TrackedCookableFood
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM CookFsm;
            public bool IsPike;
            public FsmBool CookedVar;
            public FsmBool BurnedVar;
            public FsmFloat CookProgressVar;
            public FsmInt CookIntVar;
            public CookStage LastStage;
            public float LastProgress;
            public bool IsHooked;
        }

        private sealed class TrackedContainerLid
        {
            public ContainerKind Kind;
            public GameObject LidObject;
            public PlayMakerFSM Fsm;
            public FsmBool OpenVar;
            public FsmBool ClosedVar;
            public bool IsJerryCanCap;
            public bool LastOpen;
            public bool IsOwnedAndHooked;
        }

        private sealed class ActiveRemoteFx
        {
            public GameObject FxObject;
            public float DestroyAtTime;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private const float ScanIntervalSeconds = 6.0f;
        private const float AppliancePollIntervalSeconds = 0.5f;
        private const float FoodPollIntervalSeconds = 0.45f;
        private const float HazardPollIntervalSeconds = 0.4f;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // 1. Кофеварка (coffee pan(itemx) / coffee_pan_cap)
        private PlayMakerFSM coffeeDataFsm;
        private PlayMakerFSM coffeePotCapFsm;
        private FsmFloat coffeeWaterVar;
        private FsmFloat coffeeAmountVar;
        private FsmFloat coffeeBoilVolVar;
        private FsmFloat coffeeCaffeineVar;
        private float lastCoffeeWater = -1f;
        private float lastCoffeeAmount = -1f;
        private float lastCoffeeBoil = -1f;
        private float lastCoffeeCaffeine = -1f;

        // 2. Камин в гостиной
        private PlayMakerFSM fireplaceFsm;
        private FsmBool fireplaceBurnVar;
        private FsmFloat fireplaceHeatVar;
        private bool lastFireplaceLit;

        // 3. Гриль и жарка сосисок / щуки
        private PlayMakerFSM grillFsm;
        private FsmBool grillFireVar;
        private FsmFloat grillHeatVar;
        private bool lastGrillLit;
        private readonly Dictionary<int, TrackedCookableFood> cookablesByHash = new Dictionary<int, TrackedCookableFood>();
        private readonly List<TrackedCookableFood> cookableList = new List<TrackedCookableFood>();

        // 4. Физиология: рвота от алкоголя и слепота от палёного самогона
        private PlayMakerFSM vomitFsm;
        private PlayMakerFSM vomitProjectorFsm;
        private GameObject blindnessObj;
        private PlayMakerFSM blindnessFsm;
        private FsmFloat playerBlindFloatVar;
        private FsmBool playerBlindBoolVar;
        private FsmFloat playerDrunkVar;
        private bool lastLocalBlind;
        private float lastVomitSentTime = -10f;
        private readonly Dictionary<int, bool> remoteBlindPlayers = new Dictionary<int, bool>();
        private readonly List<ActiveRemoteFx> activeRemoteFx = new List<ActiveRemoteFx>();

        // 5. Наручные часы (T / U) и радар-детектор
        private PlayMakerFSM wristwatchDataFsm;
        private FsmBool wristwatchOwnedVar;
        private GameObject wristwatchHandObj;
        private bool lastWatchOwned;
        private bool lastWatchViewing;
        private readonly Dictionary<int, bool> remoteWatchViewing = new Dictionary<int, bool>();

        private PlayMakerFSM radarDataFsm;
        private PlayMakerFSM radarSwitchFsm;
        private FsmBool radarInstalledVar;
        private FsmBool radarBoltedVar;
        private FsmBool radarPowerVar;
        private bool lastRadarInstalled;
        private bool lastRadarBolted;
        private bool lastRadarPower;

        // 6. Рыболовная верша (Katiska)
        private PlayMakerFSM fishTrapFsm;
        private FsmInt fishCountVar;
        private FsmBool fishTrapInWaterVar;
        private int lastFishCount = -1;
        private bool lastFishTrapInWater;

        // 7. Компьютер BBS (MikroPekka 386)
        private GameObject computerGo;
        private GameObject computerSystemGo;
        private PlayMakerFSM pcPowerFsm;
        private FsmString pcDiskVar;
        private FsmString pcProgramVar;
        private bool lastPcPower;
        private string lastPcProgram = string.Empty;

        // 8. Осиные гнёзда (Ruscko + Cottage)
        private GameObject rusckoHiveGo;
        private PlayMakerFSM rusckoHiveFsm;
        private FsmInt rusckoWaspsVar;
        private bool lastRusckoDestroyed;

        private GameObject cottageHiveGo;
        private PlayMakerFSM cottageHiveFsm;
        private FsmInt cottageWaspsVar;
        private bool lastCottageDestroyed;

        // 9. Крышки кейсов инструментов и пробки канистр
        private readonly Dictionary<ContainerKind, TrackedContainerLid> containerLids = new Dictionary<ContainerKind, TrackedContainerLid>();

        // Флаги подавления эха
        private bool isSuppressingAppliances;
        private bool isSuppressingCookables;
        private bool isSuppressingGear;
        private bool isSuppressingWasp;
        private bool isSuppressingContainers;

        private float nextScanTime;
        private float nextAppliancePollTime;
        private float nextFoodPollTime;
        private float nextHazardPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary
        {
            get
            {
                int ownedLids = 0;
                foreach (var kv in containerLids)
                {
                    if (kv.Value != null && kv.Value.IsOwnedAndHooked) ownedLids++;
                }
                return $"Еда: {cookableList.Count}, ПК: {(lastPcPower ? "ВКЛ" : "ВЫКЛ")}, Осы Ruscko: {(lastRusckoDestroyed ? "Нет" : "Живы")}, Крышки: {ownedLids}/{containerLids.Count}";
            }
        }

        public DomesticSurvivalAndHazardsSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanAll();
        }

        public void Reset()
        {
            hookedFsmIds.Clear();
            pendingSnapshots.Clear();

            coffeeDataFsm = null;
            coffeePotCapFsm = null;
            coffeeWaterVar = null;
            coffeeAmountVar = null;
            coffeeBoilVolVar = null;
            coffeeCaffeineVar = null;
            lastCoffeeWater = -1f;
            lastCoffeeAmount = -1f;
            lastCoffeeBoil = -1f;
            lastCoffeeCaffeine = -1f;

            fireplaceFsm = null;
            fireplaceBurnVar = null;
            fireplaceHeatVar = null;
            lastFireplaceLit = false;

            grillFsm = null;
            grillFireVar = null;
            grillHeatVar = null;
            lastGrillLit = false;
            cookablesByHash.Clear();
            cookableList.Clear();

            vomitFsm = null;
            vomitProjectorFsm = null;
            blindnessObj = null;
            blindnessFsm = null;
            playerBlindFloatVar = null;
            playerBlindBoolVar = null;
            playerDrunkVar = null;
            lastLocalBlind = false;
            lastVomitSentTime = -10f;
            remoteBlindPlayers.Clear();

            for (int i = 0; i < activeRemoteFx.Count; i++)
            {
                if (activeRemoteFx[i] != null && activeRemoteFx[i].FxObject != null)
                {
                    UnityEngine.Object.Destroy(activeRemoteFx[i].FxObject);
                }
            }
            activeRemoteFx.Clear();

            wristwatchDataFsm = null;
            wristwatchOwnedVar = null;
            wristwatchHandObj = null;
            lastWatchOwned = false;
            lastWatchViewing = false;
            remoteWatchViewing.Clear();

            radarDataFsm = null;
            radarSwitchFsm = null;
            radarInstalledVar = null;
            radarBoltedVar = null;
            radarPowerVar = null;
            lastRadarInstalled = false;
            lastRadarBolted = false;
            lastRadarPower = false;

            fishTrapFsm = null;
            fishCountVar = null;
            fishTrapInWaterVar = null;
            lastFishCount = -1;
            lastFishTrapInWater = false;

            computerGo = null;
            computerSystemGo = null;
            pcPowerFsm = null;
            pcDiskVar = null;
            pcProgramVar = null;
            lastPcPower = false;
            lastPcProgram = string.Empty;

            rusckoHiveGo = null;
            rusckoHiveFsm = null;
            rusckoWaspsVar = null;
            lastRusckoDestroyed = false;

            cottageHiveGo = null;
            cottageHiveFsm = null;
            cottageWaspsVar = null;
            lastCottageDestroyed = false;

            containerLids.Clear();

            isSuppressingAppliances = false;
            isSuppressingCookables = false;
            isSuppressingGear = false;
            isSuppressingWasp = false;
            isSuppressingContainers = false;

            nextScanTime = 0f;
            nextAppliancePollTime = 0f;
            nextFoodPollTime = 0f;
            nextHazardPollTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            TickTemporaryFx(now);

            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanIntervalSeconds;
                ScanAll();
            }

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextAppliancePollTime)
            {
                nextAppliancePollTime = now + AppliancePollIntervalSeconds;
                PollAppliancesAndStats();
            }

            if (now >= nextFoodPollTime)
            {
                nextFoodPollTime = now + FoodPollIntervalSeconds;
                PollCookableFoods();
            }

            if (now >= nextHazardPollTime)
            {
                nextHazardPollTime = now + HazardPollIntervalSeconds;
                PollWaspNests();
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanAll();
            SendAppliancesSnapshotToClient(clientId);
            SendHazardsSnapshotToClient(clientId);

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
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == clientId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }

            int targetId = (ctx?.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            remoteBlindPlayers.Remove(targetId);
            remoteWatchViewing.Remove(targetId);
        }

        public void HandleAppliancesPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subType = reader.ReadByte();
            switch (subType)
            {
                case DomesticProtocol.ApplianceFsmState:
                    HandleApplianceFsmStatePacket(reader);
                    break;

                case DomesticProtocol.GrillFoodState:
                    HandleGrillFoodStatePacket(reader);
                    break;

                case DomesticProtocol.PlayerStatusEffect:
                    HandlePlayerStatusEffectPacket(reader);
                    break;

                case DomesticProtocol.PlayerGearToggle:
                    HandlePlayerGearTogglePacket(reader);
                    break;

                case DomesticProtocol.AppliancesSnapshot:
                    HandleAppliancesSnapshotPacket(reader);
                    break;
            }
        }

        public void HandleHazardsPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subType = reader.ReadByte();
            switch (subType)
            {
                case DomesticProtocol.WaspNestState:
                    HandleWaspNestStatePacket(reader);
                    break;

                case DomesticProtocol.ContainerLidState:
                    HandleContainerLidStatePacket(reader);
                    break;

                case DomesticProtocol.HazardsContainersSnapshot:
                    HandleHazardsSnapshotPacket(reader);
                    break;
            }
        }

        #region Scanning & Hooking

        private void ScanAll()
        {
            try
            {
                ScanCoffeeMaker();
                ScanFireplace();
                ScanGrillAndCookables();
                ScanPhysiologyEffects();
                ScanWristwatchAndRadar();
                ScanFishTrap();
                ScanComputerBbs();
                ScanWaspNests();
                ScanContainerLids();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[DomesticSurvival] Ошибка сканирования: {ex.Message}");
            }
        }

        private void ScanCoffeeMaker()
        {
            if (coffeeDataFsm == null)
            {
                GameObject coffeeGo = FindObjectIncludingInactive("coffee pan(itemx)") ?? GameObject.Find("DrinkCoffee");
                if (coffeeGo != null)
                {
                    coffeeDataFsm = PlayMakerBridge.FindFsm(coffeeGo, "Data");
                    if (coffeeDataFsm != null)
                    {
                        coffeeDataFsm.InitializeSafe();
                        coffeeWaterVar = coffeeDataFsm.FsmVariables.FindFsmFloat("Water");
                        coffeeAmountVar = coffeeDataFsm.FsmVariables.FindFsmFloat("Coffee");
                        coffeeBoilVolVar = coffeeDataFsm.FsmVariables.FindFsmFloat("BoilVolume");
                        coffeeCaffeineVar = coffeeDataFsm.FsmVariables.FindFsmFloat("Caffeine");

                        if (TryClaimSafe(coffeeDataFsm, "Domestic_CoffeeData"))
                        {
                            HookFsmStateOnce(coffeeDataFsm, "CAPOPEN", () =>
                            {
                                if (isSuppressingAppliances) return;
                                SendCoffeeState(force: true);
                            });
                        }
                    }
                }
            }

            if (coffeePotCapFsm == null)
            {
                GameObject potCap = FindObjectIncludingInactive("coffee_pan_cap") ?? GameObject.Find("HandCoffeeHome");
                if (potCap != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(potCap, "Use") ?? potCap.GetComponent<PlayMakerFSM>();
                    if (fsm != null && TryClaimSafe(fsm, "Domestic_CoffeePotCap"))
                    {
                        coffeePotCapFsm = fsm;
                        fsm.InitializeSafe();
                        string boilState = fsm.HasState("Boil") ? "Boil" : (fsm.HasState("State 2") ? "State 2" : null);
                        if (boilState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_COFFEE_BOIL", boilState);
                            HookFsmStateOnce(fsm, boilState, () =>
                            {
                                if (isSuppressingAppliances) return;
                                SendCoffeeState(force: true);
                            });
                        }
                    }
                }
            }
        }

        private void ScanFireplace()
        {
            if (fireplaceFsm != null) return;

            GameObject fpGo = GameObject.Find("Fireplace") ?? GameObject.Find("YARD/Building/LIVINGROOM/Fireplace");
            if (fpGo == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(fpGo, "Use")
                ?? PlayMakerBridge.FindFsm(fpGo, "Fire")
                ?? PlayMakerBridge.FindFsm(fpGo, "Logic")
                ?? fpGo.GetComponent<PlayMakerFSM>();

            if (fsm == null || !TryClaimSafe(fsm, "Domestic_LivingroomFireplace")) return;

            fireplaceFsm = fsm;
            fsm.InitializeSafe();
            fireplaceBurnVar = fsm.FsmVariables.FindFsmBool("Fire")
                ?? fsm.FsmVariables.FindFsmBool("Burn")
                ?? fsm.FsmVariables.FindFsmBool("Lit");
            fireplaceHeatVar = fsm.FsmVariables.FindFsmFloat("Heat");

            string burnState = fsm.HasState("Burn") ? "Burn"
                : (fsm.HasState("Fire") ? "Fire" : (fsm.HasState("State 2") ? "State 2" : null));
            if (burnState != null)
            {
                fsm.AddSafeGlobalTransition("MP_FP_IGNITE", burnState);
                HookFsmStateOnce(fsm, burnState, () =>
                {
                    if (isSuppressingAppliances) return;
                    lastFireplaceLit = true;
                    float heat = fireplaceHeatVar != null ? fireplaceHeatVar.Value : 100f;
                    SendAppliancePacket(ApplianceKind.Fireplace, true, heat, 0f, 0f, 0f, string.Empty);
                    ctx?.Log?.Invoke("[Гостиная] Камин растоплен!");
                });
            }

            string offState = fsm.HasState("Extinguish") ? "Extinguish"
                : (fsm.HasState("Off") ? "Off" : (fsm.HasState("Out") ? "Out" : null));
            if (offState != null)
            {
                fsm.AddSafeGlobalTransition("MP_FP_OUT", offState);
                HookFsmStateOnce(fsm, offState, () =>
                {
                    if (isSuppressingAppliances) return;
                    lastFireplaceLit = false;
                    SendAppliancePacket(ApplianceKind.Fireplace, false, 0f, 0f, 0f, 0f, string.Empty);
                });
            }
        }

        private void ScanGrillAndCookables()
        {
            if (grillFsm == null)
            {
                GameObject grillGo = FindObjectIncludingInactive("grill(itemx)") ?? GameObject.Find("Grill");
                if (grillGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(grillGo, "Grill")
                        ?? PlayMakerBridge.FindFsm(grillGo, "Fire")
                        ?? PlayMakerBridge.FindFsm(grillGo, "Heat")
                        ?? PlayMakerBridge.FindFsm(grillGo, "Charcoal");

                    if (fsm != null && TryClaimSafe(fsm, "Domestic_CharcoalGrill"))
                    {
                        grillFsm = fsm;
                        fsm.InitializeSafe();
                        grillFireVar = fsm.FsmVariables.FindFsmBool("Fire")
                            ?? fsm.FsmVariables.FindFsmBool("OnFire")
                            ?? fsm.FsmVariables.FindFsmBool("Lit");
                        grillHeatVar = fsm.FsmVariables.FindFsmFloat("Heat")
                            ?? fsm.FsmVariables.FindFsmFloat("Temperature");

                        string fireState = fsm.HasState("Fire") ? "Fire"
                            : (fsm.HasState("Burn") ? "Burn" : (fsm.HasState("Lit") ? "Lit" : null));
                        if (fireState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_GRILL_IGNITE", fireState);
                            HookFsmStateOnce(fsm, fireState, () =>
                            {
                                if (isSuppressingAppliances) return;
                                lastGrillLit = true;
                                float heat = grillHeatVar != null ? grillHeatVar.Value : 100f;
                                SendAppliancePacket(ApplianceKind.Grill, true, heat, 0f, 0f, 0f, string.Empty);
                            });
                        }

                        string outState = fsm.HasState("Out") ? "Out"
                            : (fsm.HasState("Off") ? "Off" : null);
                        if (outState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_GRILL_OUT", outState);
                            HookFsmStateOnce(fsm, outState, () =>
                            {
                                if (isSuppressingAppliances) return;
                                lastGrillLit = false;
                                SendAppliancePacket(ApplianceKind.Grill, false, 0f, 0f, 0f, 0f, string.Empty);
                            });
                        }
                    }
                }
            }

            // Сканируем сосиски и щуку (только Cooking/Grill FSM, никогда не трогаем Use/Rigidbody!)
            GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                GameObject go = allGo[i];
                if (go == null) continue;

                string nameLower = (go.name ?? string.Empty).ToLowerInvariant();
                bool isSausage = nameLower.Contains("sausage") || nameLower.Contains("makkara");
                bool isPike = nameLower.Contains("pike") || nameLower.Contains("hauki");
                if (!isSausage && !isPike) continue;

                // Игнорируем префабы в кукле игрока или пустые контейнеры
                Transform root = go.transform.root;
                if (root != null && (root.name.StartsWith("RemotePuppet", StringComparison.OrdinalIgnoreCase) ||
                                     root.name.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                PlayMakerFSM cookFsm = PlayMakerBridge.FindFsm(go, "Cooking")
                    ?? PlayMakerBridge.FindFsm(go, "Cook")
                    ?? PlayMakerBridge.FindFsm(go, "Grill")
                    ?? PlayMakerBridge.FindFsm(go, "Roast");
                if (cookFsm == null) continue;

                int hash = SceneHasher.GetHash(go);
                if (cookablesByHash.ContainsKey(hash)) continue;

                // Захватываем ТОЛЬКО Cooking/Grill FSM (никогда Use и никогда Rigidbody!)
                if (!TryClaimSafe(cookFsm, $"CookFood_{hash:X}")) continue;

                cookFsm.InitializeSafe();
                FsmBool cookedVar = cookFsm.FsmVariables.FindFsmBool("Cooked") ?? cookFsm.FsmVariables.FindFsmBool("Done");
                FsmBool burnedVar = cookFsm.FsmVariables.FindFsmBool("Burned") ?? cookFsm.FsmVariables.FindFsmBool("Burnt");
                FsmFloat progressVar = cookFsm.FsmVariables.FindFsmFloat("Cook")
                    ?? cookFsm.FsmVariables.FindFsmFloat("Cooking")
                    ?? cookFsm.FsmVariables.FindFsmFloat("Progress");
                FsmInt cookIntVar = cookFsm.FsmVariables.FindFsmInt("CookState") ?? cookFsm.FsmVariables.FindFsmInt("State");

                var tracked = new TrackedCookableFood
                {
                    Hash = hash,
                    GameObject = go,
                    CookFsm = cookFsm,
                    IsPike = isPike,
                    CookedVar = cookedVar,
                    BurnedVar = burnedVar,
                    CookProgressVar = progressVar,
                    CookIntVar = cookIntVar,
                    LastStage = EvaluateCookStage(cookFsm, cookedVar, burnedVar, cookIntVar),
                    LastProgress = progressVar != null ? progressVar.Value : 0f
                };

                HookCookableFoodFsm(tracked);
                cookablesByHash[hash] = tracked;
                cookableList.Add(tracked);
            }
        }

        private void HookCookableFoodFsm(TrackedCookableFood food)
        {
            if (food == null || food.CookFsm == null || food.IsHooked) return;
            PlayMakerFSM fsm = food.CookFsm;

            string[] cookedStates = { "Cooked", "Done", "Ready", "Grilled" };
            for (int i = 0; i < cookedStates.Length; i++)
            {
                if (fsm.HasState(cookedStates[i]))
                {
                    fsm.AddSafeGlobalTransition("MP_COOK_DONE", cookedStates[i]);
                    HookFsmStateOnce(fsm, cookedStates[i], () => OnLocalFoodStageChanged(food, CookStage.Cooked));
                    break;
                }
            }

            string[] burnedStates = { "Burned", "Burnt", "Overcooked" };
            for (int i = 0; i < burnedStates.Length; i++)
            {
                if (fsm.HasState(burnedStates[i]))
                {
                    fsm.AddSafeGlobalTransition("MP_COOK_BURNED", burnedStates[i]);
                    HookFsmStateOnce(fsm, burnedStates[i], () => OnLocalFoodStageChanged(food, CookStage.Burned));
                    break;
                }
            }

            if (fsm.HasState("Raw"))
            {
                fsm.AddSafeGlobalTransition("MP_COOK_RAW", "Raw");
            }

            food.IsHooked = true;
        }

        private void ScanPhysiologyEffects()
        {
            if (vomitFsm == null)
            {
                GameObject vomitGo = GameObject.Find("Vomit");
                if (vomitGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(vomitGo, "Anim") ?? vomitGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && TryClaimSafe(fsm, "Domestic_PlayerVomit"))
                    {
                        vomitFsm = fsm;
                        fsm.InitializeSafe();
                        if (fsm.HasState("1"))
                        {
                            HookFsmStateOnce(fsm, "1", OnLocalVomitTriggered);
                        }
                    }
                }
            }

            if (vomitProjectorFsm == null)
            {
                GameObject projGo = GameObject.Find("VomitProjector");
                if (projGo != null)
                {
                    vomitProjectorFsm = PlayMakerBridge.FindFsm(projGo, "Effect") ?? projGo.GetComponent<PlayMakerFSM>();
                }
            }

            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                if (playerBlindFloatVar == null)
                {
                    playerBlindFloatVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerBlind")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerBlindness");
                }
                if (playerBlindBoolVar == null)
                {
                    playerBlindBoolVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerBlind")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerBlindness");
                }
                if (playerDrunkVar == null)
                {
                    playerDrunkVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunkAdjusted")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunk");
                }
            }

            if (blindnessObj == null)
            {
                GameObject playerGo = GameObject.Find("PLAYER");
                if (playerGo != null)
                {
                    Transform blindTr = PlayMakerBridge.FindChildRecursive(playerGo.transform, "Blindness")
                        ?? PlayMakerBridge.FindChildRecursive(playerGo.transform, "Blind");
                    if (blindTr != null)
                    {
                        blindnessObj = blindTr.gameObject;
                        blindnessFsm = PlayMakerBridge.FindFsm(blindnessObj, "Blind")
                            ?? PlayMakerBridge.FindFsm(blindnessObj, "Logic")
                            ?? blindnessObj.GetComponent<PlayMakerFSM>();
                    }
                }

                if (blindnessObj == null)
                {
                    blindnessObj = FindObjectIncludingInactive("Blindness");
                    if (blindnessObj != null && blindnessFsm == null)
                    {
                        blindnessFsm = blindnessObj.GetComponent<PlayMakerFSM>();
                    }
                }
            }
        }

        private void ScanWristwatchAndRadar()
        {
            if (wristwatchDataFsm == null)
            {
                GameObject watchGo = FindObjectIncludingInactive("Wristwatch");
                if (watchGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(watchGo, "Data") ?? watchGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        fsm.InitializeSafe();
                        wristwatchDataFsm = fsm;
                        wristwatchOwnedVar = fsm.FsmVariables.FindFsmBool("Owned");
                        if (wristwatchOwnedVar != null) lastWatchOwned = wristwatchOwnedVar.Value;
                    }
                }
            }

            if (wristwatchHandObj == null)
            {
                wristwatchHandObj = FindObjectIncludingInactive("WristwatchHand");
                if (wristwatchHandObj != null)
                {
                    lastWatchViewing = wristwatchHandObj.activeSelf;
                }
            }

            if (radarDataFsm == null)
            {
                GameObject radarGo = FindObjectIncludingInactive("Radar Buster")
                    ?? FindObjectIncludingInactive("radar buster(Clone)")
                    ?? FindObjectIncludingInactive("Radar");
                if (radarGo != null)
                {
                    PlayMakerFSM dataFsm = PlayMakerBridge.FindFsm(radarGo, "Data") ?? radarGo.GetComponent<PlayMakerFSM>();
                    if (dataFsm != null)
                    {
                        dataFsm.InitializeSafe();
                        radarDataFsm = dataFsm;
                        radarInstalledVar = dataFsm.FsmVariables.FindFsmBool("Installed");
                        radarBoltedVar = dataFsm.FsmVariables.FindFsmBool("Bolted");
                        radarPowerVar = dataFsm.FsmVariables.FindFsmBool("Power")
                            ?? dataFsm.FsmVariables.FindFsmBool("On")
                            ?? dataFsm.FsmVariables.FindFsmBool("Switch");

                        lastRadarInstalled = radarInstalledVar != null && radarInstalledVar.Value;
                        lastRadarBolted = radarBoltedVar != null && radarBoltedVar.Value;
                        lastRadarPower = radarPowerVar != null && radarPowerVar.Value;
                    }

                    Transform swTr = radarGo.transform.Find("Switch") ?? radarGo.transform.Find("Button");
                    if (swTr != null)
                    {
                        PlayMakerFSM swFsm = PlayMakerBridge.FindFsm(swTr.gameObject, "Use") ?? swTr.GetComponent<PlayMakerFSM>();
                        if (swFsm != null && TryClaimSafe(swFsm, "Domestic_RadarSwitch"))
                        {
                            radarSwitchFsm = swFsm;
                            swFsm.InitializeSafe();
                            if (radarPowerVar == null)
                            {
                                radarPowerVar = swFsm.FsmVariables.FindFsmBool("Switch")
                                    ?? swFsm.FsmVariables.FindFsmBool("On")
                                    ?? swFsm.FsmVariables.FindFsmBool("Power");
                            }
                            string toggleState = swFsm.HasState("Position") ? "Position"
                                : (swFsm.HasState("Switch") ? "Switch" : (swFsm.HasState("State 1") ? "State 1" : null));
                            if (toggleState != null)
                            {
                                swFsm.AddSafeGlobalTransition("MP_RADAR_TOGGLE", toggleState);
                                HookFsmStateOnce(swFsm, toggleState, () =>
                                {
                                    if (isSuppressingGear) return;
                                    bool installed = radarInstalledVar != null && radarInstalledVar.Value;
                                    bool bolted = radarBoltedVar != null && radarBoltedVar.Value;
                                    bool power = !(radarPowerVar != null && radarPowerVar.Value);
                                    lastRadarPower = power;
                                    SendGearPacket(GearKind.RadarDetector, installed, bolted, power);
                                });
                            }
                        }
                    }
                }
            }
        }

        private void ScanFishTrap()
        {
            if (fishTrapFsm != null) return;

            GameObject katiskaGo = FindObjectIncludingInactive("katiska_frame")
                ?? FindObjectIncludingInactive("katiska(itemx)")
                ?? FindObjectIncludingInactive("katiska");
            if (katiskaGo == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(katiskaGo, "Trap")
                ?? PlayMakerBridge.FindFsm(katiskaGo, "Fish")
                ?? PlayMakerBridge.FindFsm(katiskaGo, "Data");
            if (fsm == null) return;

            // Захватываем только логическую FSM ловушки (никогда Rigidbody!)
            if (!TryClaimSafe(fsm, "Domestic_FishTrap")) return;

            fsm.InitializeSafe();
            fishTrapFsm = fsm;
            fishCountVar = fsm.FsmVariables.FindFsmInt("Fish") ?? fsm.FsmVariables.FindFsmInt("FishCount");
            fishTrapInWaterVar = fsm.FsmVariables.FindFsmBool("InWater") ?? fsm.FsmVariables.FindFsmBool("Water");
            lastFishCount = fishCountVar != null ? fishCountVar.Value : 0;
            lastFishTrapInWater = fishTrapInWaterVar != null && fishTrapInWaterVar.Value;
        }

        private void ScanComputerBbs()
        {
            if (computerGo == null)
            {
                computerGo = FindObjectIncludingInactive("COMPUTER")
                    ?? FindObjectIncludingInactive("Computer")
                    ?? FindObjectIncludingInactive("computer(Clone)");
            }

            if (computerGo == null) return;

            if (computerSystemGo == null)
            {
                Transform sysTr = PlayMakerBridge.FindChildRecursive(computerGo.transform, "SYSTEM");
                computerSystemGo = sysTr != null ? sysTr.gameObject : FindObjectIncludingInactive("SYSTEM");
                if (computerSystemGo != null)
                {
                    lastPcPower = computerSystemGo.activeSelf;
                }
            }

            if (pcPowerFsm == null)
            {
                GameObject btnGo = GameObject.Find("386_button") ?? computerGo;
                PlayMakerFSM fsm = PlayMakerBridge.FindFsm(btnGo, "Use")
                    ?? PlayMakerBridge.FindFsm(btnGo, "Power")
                    ?? btnGo.GetComponent<PlayMakerFSM>();
                if (fsm != null && TryClaimSafe(fsm, "Domestic_Computer386"))
                {
                    pcPowerFsm = fsm;
                    fsm.InitializeSafe();
                    pcDiskVar = fsm.FsmVariables.FindFsmString("Disk") ?? fsm.FsmVariables.FindFsmString("Floppy");
                    pcProgramVar = fsm.FsmVariables.FindFsmString("Program") ?? fsm.FsmVariables.FindFsmString("ActiveGame");
                }
            }
        }

        private void ScanWaspNests()
        {
            if (rusckoHiveGo != null && cottageHiveGo != null) return;

            PlayMakerFSM[] allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null) continue;

                string goName = fsm.gameObject.name;
                bool isRuscko = fsm.transform.root != null &&
                    (fsm.transform.root.name.IndexOf("ruscko", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     fsm.transform.root.name.IndexOf("rusto", StringComparison.OrdinalIgnoreCase) >= 0);

                if (goName == "TargetHive")
                {
                    if (isRuscko && rusckoHiveGo == null)
                    {
                        rusckoHiveGo = fsm.gameObject;
                        rusckoHiveFsm = fsm;
                        fsm.InitializeSafe();
                        rusckoWaspsVar = fsm.FsmVariables.FindFsmInt("WaspsInHive");
                        lastRusckoDestroyed = IsHiveDestroyed(rusckoHiveGo, rusckoWaspsVar);
                    }
                    else if (!isRuscko && cottageHiveGo == null)
                    {
                        cottageHiveGo = fsm.gameObject;
                        cottageHiveFsm = fsm;
                        fsm.InitializeSafe();
                        cottageWaspsVar = fsm.FsmVariables.FindFsmInt("WaspsInHive");
                        lastCottageDestroyed = IsHiveDestroyed(cottageHiveGo, cottageWaspsVar);
                    }
                }
                else if (goName == "WaspHiveTrigger")
                {
                    fsm.InitializeSafe();
                    if (!TryClaimSafe(fsm, isRuscko ? "Domestic_WaspTrigger_Ruscko" : "Domestic_WaspTrigger_Cottage"))
                    {
                        continue;
                    }

                    string[] destroyStates = { "Put out", "Drown", "Water", "Destroy", "Extinguish" };
                    for (int s = 0; s < destroyStates.Length; s++)
                    {
                        if (fsm.HasState(destroyStates[s]))
                        {
                            bool rusckoFlag = isRuscko;
                            HookFsmStateOnce(fsm, destroyStates[s], () => OnLocalWaspNestDestroyed(rusckoFlag));
                        }
                    }
                }
            }
        }

        private void ScanContainerLids()
        {
            Transform itemsTr = GameObject.Find("ITEMS")?.transform;

            // SpannerSet, RatchetSet, Gasoline и Diesel уже синхронизируются в OSC.Module.Items;
            // здесь регистрируем только пробку двухтактной канистры (two stroke fuel), исключая конфликт владения.
            TryRegisterJerryCanCap(ContainerKind.TwoStrokeCap, "two stroke fuel(itemx)", itemsTr, "Domestic_TwoStrokeCap");
        }

        private void TryRegisterJerryCanCap(ContainerKind kind, string objectName, Transform itemsTr, string claimLabel)
        {
            if (containerLids.ContainsKey(kind)) return;

            GameObject canGo = (itemsTr != null ? itemsTr.Find(objectName)?.gameObject : null)
                ?? FindObjectIncludingInactive(objectName);
            if (canGo == null) return;

            Transform lidTr = canGo.transform.Find("Open");
            if (lidTr == null) return;

            PlayMakerFSM fsm = lidTr.GetComponent<PlayMakerFSM>();
            if (fsm == null) return;

            fsm.InitializeSafe();
            FsmBool openVar = fsm.FsmVariables.FindFsmBool("Open");
            FsmBool closedVar = fsm.FsmVariables.FindFsmBool("Closed");
            bool owned = TryClaimSafe(fsm, claimLabel);

            var entry = new TrackedContainerLid
            {
                Kind = kind,
                LidObject = lidTr.gameObject,
                Fsm = fsm,
                OpenVar = openVar,
                ClosedVar = closedVar,
                IsJerryCanCap = true,
                LastOpen = openVar != null && openVar.Value,
                IsOwnedAndHooked = false
            };

            if (owned && fsm.HasState("State 2"))
            {
                fsm.AddSafeGlobalTransition("MP_TOGGLE", "State 2");
                HookFsmStateOnce(fsm, "State 2", () =>
                {
                    bool nextOpen = !(entry.OpenVar != null && entry.OpenVar.Value);
                    OnLocalContainerLidToggled(entry, nextOpen);
                });
                entry.IsOwnedAndHooked = true;
            }

            containerLids[kind] = entry;
        }

        #endregion

        #region Local Polling & Hooks

        private void PollAppliancesAndStats()
        {
            if (coffeeDataFsm != null)
            {
                float w = coffeeWaterVar != null ? coffeeWaterVar.Value : 0f;
                float c = coffeeAmountVar != null ? coffeeAmountVar.Value : 0f;
                float b = coffeeBoilVolVar != null ? coffeeBoilVolVar.Value : 0f;
                float caf = coffeeCaffeineVar != null ? coffeeCaffeineVar.Value : 0f;

                if (Mathf.Abs(w - lastCoffeeWater) >= 0.2f ||
                    Mathf.Abs(c - lastCoffeeAmount) >= 0.2f ||
                    Mathf.Abs(b - lastCoffeeBoil) >= 0.2f ||
                    Mathf.Abs(caf - lastCoffeeCaffeine) >= 0.2f)
                {
                    SendCoffeeState(force: false);
                }
            }

            if (fireplaceFsm != null && fireplaceBurnVar != null)
            {
                bool lit = fireplaceBurnVar.Value;
                if (lit != lastFireplaceLit)
                {
                    lastFireplaceLit = lit;
                    float heat = fireplaceHeatVar != null ? fireplaceHeatVar.Value : (lit ? 100f : 0f);
                    SendAppliancePacket(ApplianceKind.Fireplace, lit, heat, 0f, 0f, 0f, string.Empty);
                }
            }

            if (grillFsm != null && grillFireVar != null)
            {
                bool lit = grillFireVar.Value;
                if (lit != lastGrillLit)
                {
                    lastGrillLit = lit;
                    float heat = grillHeatVar != null ? grillHeatVar.Value : (lit ? 100f : 0f);
                    SendAppliancePacket(ApplianceKind.Grill, lit, heat, 0f, 0f, 0f, string.Empty);
                }
            }

            // Слепота от палёного самогона
            bool currentBlind = IsLocalBlindActive();
            if (currentBlind != lastLocalBlind)
            {
                lastLocalBlind = currentBlind;
                float intensity = playerBlindFloatVar != null ? playerBlindFloatVar.Value : (currentBlind ? 1f : 0f);
                SendStatusEffectPacket(StatusEffectKind.Blindness, currentBlind, intensity);
                if (currentBlind)
                {
                    ctx?.Log?.Invoke("[Физиология] Ослепление палёным спиртом!");
                }
            }

            // Наручные часы (T / U)
            bool watchOwned = wristwatchOwnedVar != null ? wristwatchOwnedVar.Value : lastWatchOwned;
            bool watchViewing = (wristwatchHandObj != null && wristwatchHandObj.activeSelf) ||
                                (!Input.GetKey(KeyCode.LeftAlt) && (Input.GetKey(KeyCode.T) || Input.GetKey(KeyCode.U)) && watchOwned);
            if (watchOwned != lastWatchOwned || watchViewing != lastWatchViewing)
            {
                lastWatchOwned = watchOwned;
                lastWatchViewing = watchViewing;
                SendGearPacket(GearKind.Wristwatch, watchOwned, watchViewing, false);
            }

            // Радар-детектор
            if (radarDataFsm != null)
            {
                bool installed = radarInstalledVar != null && radarInstalledVar.Value;
                bool bolted = radarBoltedVar != null && radarBoltedVar.Value;
                bool power = radarPowerVar != null && radarPowerVar.Value;
                if (installed != lastRadarInstalled || bolted != lastRadarBolted || power != lastRadarPower)
                {
                    lastRadarInstalled = installed;
                    lastRadarBolted = bolted;
                    lastRadarPower = power;
                    SendGearPacket(GearKind.RadarDetector, installed, bolted, power);
                }
            }

            // Верша (Katiska)
            if (fishTrapFsm != null)
            {
                int fish = fishCountVar != null ? fishCountVar.Value : 0;
                bool inWater = fishTrapInWaterVar != null && fishTrapInWaterVar.Value;
                if (fish != lastFishCount || inWater != lastFishTrapInWater)
                {
                    lastFishCount = fish;
                    lastFishTrapInWater = inWater;
                    SendAppliancePacket(ApplianceKind.FishTrap, inWater, fish, 0f, 0f, 0f, string.Empty);
                }
            }

            // Компьютер BBS
            if (computerSystemGo != null)
            {
                bool pcPower = computerSystemGo.activeSelf;
                string prog = pcProgramVar != null ? (pcProgramVar.Value ?? string.Empty) : string.Empty;
                if (pcPower != lastPcPower || !string.Equals(prog, lastPcProgram, StringComparison.Ordinal))
                {
                    lastPcPower = pcPower;
                    lastPcProgram = prog;
                    string disk = pcDiskVar != null ? (pcDiskVar.Value ?? string.Empty) : string.Empty;
                    string combined = string.IsNullOrEmpty(disk) ? prog : (disk + "|" + prog);
                    SendAppliancePacket(ApplianceKind.Computer386, pcPower, 0f, 0f, 0f, 0f, combined);
                }
            }
        }

        private void PollCookableFoods()
        {
            if (isSuppressingCookables) return;

            for (int i = 0; i < cookableList.Count; i++)
            {
                TrackedCookableFood food = cookableList[i];
                if (food == null || food.GameObject == null) continue;

                CookStage stage = EvaluateCookStage(food.CookFsm, food.CookedVar, food.BurnedVar, food.CookIntVar);
                float progress = food.CookProgressVar != null ? food.CookProgressVar.Value : (float)stage;

                if (stage != food.LastStage || Mathf.Abs(progress - food.LastProgress) >= 0.25f)
                {
                    food.LastStage = stage;
                    food.LastProgress = progress;
                    SendGrillFoodPacket(food.Hash, food.IsPike, stage, progress);
                }
            }
        }

        private void PollWaspNests()
        {
            if (isSuppressingWasp) return;

            if (rusckoHiveGo != null && !lastRusckoDestroyed)
            {
                if (IsHiveDestroyed(rusckoHiveGo, rusckoWaspsVar))
                {
                    OnLocalWaspNestDestroyed(isRuscko: true);
                }
            }

            if (cottageHiveGo != null && !lastCottageDestroyed)
            {
                if (IsHiveDestroyed(cottageHiveGo, cottageWaspsVar))
                {
                    OnLocalWaspNestDestroyed(isRuscko: false);
                }
            }
        }

        private void OnLocalFoodStageChanged(TrackedCookableFood food, CookStage stage)
        {
            if (isSuppressingCookables || food == null || food.LastStage == stage) return;
            food.LastStage = stage;
            float progress = food.CookProgressVar != null ? food.CookProgressVar.Value : (float)stage;
            food.LastProgress = progress;

            if (IsNetworkRunning)
            {
                SendGrillFoodPacket(food.Hash, food.IsPike, stage, progress);
            }
        }

        private void OnLocalVomitTriggered()
        {
            if (isSuppressingAppliances || !IsNetworkRunning) return;
            float now = Time.time;
            if ((now - lastVomitSentTime) < 1.5f) return;
            lastVomitSentTime = now;

            float drunk = playerDrunkVar != null ? playerDrunkVar.Value : 1f;
            SendStatusEffectPacket(StatusEffectKind.Vomit, true, drunk);
            ctx?.Log?.Invoke("[Физиология] Приступ рвоты от алкоголя!");
        }

        private void OnLocalWaspNestDestroyed(bool isRuscko)
        {
            if (isSuppressingWasp) return;
            if (isRuscko)
            {
                if (lastRusckoDestroyed) return;
                lastRusckoDestroyed = true;
            }
            else
            {
                if (lastCottageDestroyed) return;
                lastCottageDestroyed = true;
            }

            if (!IsNetworkRunning) return;
            SendWaspNestPacket(isRuscko ? (byte)0 : (byte)1, destroyed: true);
            ctx?.Log?.Invoke($"[Шершни] Осиное гнездо в {(isRuscko ? "Ruscko" : "коттедже")} уничтожено!");
        }

        private void OnLocalContainerLidToggled(TrackedContainerLid entry, bool isOpen)
        {
            if (isSuppressingContainers || entry == null || !entry.IsOwnedAndHooked) return;
            entry.LastOpen = isOpen;

            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticHazardsAndContainersSync))
            {
                p.WriteByte(DomesticProtocol.ContainerLidState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte((byte)entry.Kind);
                p.WriteBoolean(isOpen);
                ctx.Transport.Send(p);
            }
        }

        #endregion

        #region Outbound Helpers & Snapshots

        private void SendCoffeeState(bool force)
        {
            if (isSuppressingAppliances || !IsNetworkRunning) return;

            float w = coffeeWaterVar != null ? coffeeWaterVar.Value : 0f;
            float c = coffeeAmountVar != null ? coffeeAmountVar.Value : 0f;
            float b = coffeeBoilVolVar != null ? coffeeBoilVolVar.Value : 0f;
            float caf = coffeeCaffeineVar != null ? coffeeCaffeineVar.Value : 0f;

            if (!force &&
                Mathf.Abs(w - lastCoffeeWater) < 0.2f &&
                Mathf.Abs(c - lastCoffeeAmount) < 0.2f &&
                Mathf.Abs(b - lastCoffeeBoil) < 0.2f &&
                Mathf.Abs(caf - lastCoffeeCaffeine) < 0.2f)
            {
                return;
            }

            lastCoffeeWater = w;
            lastCoffeeAmount = c;
            lastCoffeeBoil = b;
            lastCoffeeCaffeine = caf;

            SendAppliancePacket(ApplianceKind.CoffeeMaker, b > 0.1f, w, c, b, caf, string.Empty);
        }

        private void SendAppliancePacket(ApplianceKind kind, bool active, float f1, float f2, float f3, float f4, string text)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticAppliancesAndStatsSync))
            {
                p.WriteByte(DomesticProtocol.ApplianceFsmState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte((byte)kind);
                p.WriteBoolean(active);
                p.WriteSingle(f1);
                p.WriteSingle(f2);
                p.WriteSingle(f3);
                p.WriteSingle(f4);
                p.WriteString(text ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendGrillFoodPacket(int itemHash, bool isPike, CookStage stage, float progress)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticAppliancesAndStatsSync))
            {
                p.WriteByte(DomesticProtocol.GrillFoodState);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(itemHash);
                p.WriteBoolean(isPike);
                p.WriteByte((byte)stage);
                p.WriteSingle(progress);
                ctx.Transport.Send(p);
            }
        }

        private void SendStatusEffectPacket(StatusEffectKind effect, bool active, float intensity)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticAppliancesAndStatsSync))
            {
                p.WriteByte(DomesticProtocol.PlayerStatusEffect);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte((byte)effect);
                p.WriteBoolean(active);
                p.WriteSingle(intensity);
                ctx.Transport.Send(p);
            }
        }

        private void SendGearPacket(GearKind kind, bool flag1, bool flag2, bool flag3)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticAppliancesAndStatsSync))
            {
                p.WriteByte(DomesticProtocol.PlayerGearToggle);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte((byte)kind);
                p.WriteBoolean(flag1);
                p.WriteBoolean(flag2);
                p.WriteBoolean(flag3);
                ctx.Transport.Send(p);
            }
        }

        private void SendWaspNestPacket(byte nestId, bool destroyed)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticHazardsAndContainersSync))
            {
                p.WriteByte(DomesticProtocol.WaspNestState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(nestId);
                p.WriteBoolean(destroyed);
                ctx.Transport.Send(p);
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
                    ScanAll();
                    SendAppliancesSnapshotToClient(clientId);
                    SendHazardsSnapshotToClient(clientId);
                }
            }
        }

        private void SendAppliancesSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.DomesticAppliancesAndStatsSync))
            {
                p.WriteByte(DomesticProtocol.AppliancesSnapshot);
                p.WriteInt32(LocalPlayerId);

                // Кофе
                p.WriteSingle(coffeeWaterVar != null ? coffeeWaterVar.Value : 0f);
                p.WriteSingle(coffeeAmountVar != null ? coffeeAmountVar.Value : 0f);
                p.WriteSingle(coffeeBoilVolVar != null ? coffeeBoilVolVar.Value : 0f);
                p.WriteSingle(coffeeCaffeineVar != null ? coffeeCaffeineVar.Value : 0f);

                // Камин и гриль
                p.WriteBoolean(fireplaceBurnVar != null && fireplaceBurnVar.Value);
                p.WriteSingle(fireplaceHeatVar != null ? fireplaceHeatVar.Value : 0f);
                p.WriteBoolean(grillFireVar != null && grillFireVar.Value);
                p.WriteSingle(grillHeatVar != null ? grillHeatVar.Value : 0f);

                // Верша и ПК
                p.WriteBoolean(fishTrapInWaterVar != null && fishTrapInWaterVar.Value);
                p.WriteInt32(fishCountVar != null ? fishCountVar.Value : 0);
                p.WriteBoolean(computerSystemGo != null && computerSystemGo.activeSelf);
                p.WriteString(pcProgramVar != null ? (pcProgramVar.Value ?? string.Empty) : string.Empty);

                // Часы и радар
                p.WriteBoolean(wristwatchOwnedVar != null && wristwatchOwnedVar.Value);
                p.WriteBoolean(radarInstalledVar != null && radarInstalledVar.Value);
                p.WriteBoolean(radarBoltedVar != null && radarBoltedVar.Value);
                p.WriteBoolean(radarPowerVar != null && radarPowerVar.Value);

                // Еда на гриле (Raw/Cooked/Burned)
                p.WriteInt32(cookableList.Count);
                for (int i = 0; i < cookableList.Count; i++)
                {
                    TrackedCookableFood food = cookableList[i];
                    CookStage st = EvaluateCookStage(food.CookFsm, food.CookedVar, food.BurnedVar, food.CookIntVar);
                    float prog = food.CookProgressVar != null ? food.CookProgressVar.Value : (float)st;
                    p.WriteInt32(food.Hash);
                    p.WriteBoolean(food.IsPike);
                    p.WriteByte((byte)st);
                    p.WriteSingle(prog);
                }

                ctx.Transport.SendTo(p, clientId);
            }
        }

        private void SendHazardsSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            bool rusckoDead = IsHiveDestroyed(rusckoHiveGo, rusckoWaspsVar);
            bool cottageDead = IsHiveDestroyed(cottageHiveGo, cottageWaspsVar);

            var ownedLids = new List<TrackedContainerLid>();
            foreach (var kv in containerLids)
            {
                if (kv.Value != null && kv.Value.IsOwnedAndHooked)
                {
                    ownedLids.Add(kv.Value);
                }
            }

            using (var p = new PacketWriter(PacketType.DomesticHazardsAndContainersSync))
            {
                p.WriteByte(DomesticProtocol.HazardsContainersSnapshot);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(rusckoDead);
                p.WriteBoolean(cottageDead);

                p.WriteInt32(ownedLids.Count);
                for (int i = 0; i < ownedLids.Count; i++)
                {
                    TrackedContainerLid lid = ownedLids[i];
                    bool isOpen = lid.OpenVar != null ? lid.OpenVar.Value : lid.LastOpen;
                    p.WriteByte((byte)lid.Kind);
                    p.WriteBoolean(isOpen);
                }

                ctx.Transport.SendTo(p, clientId);
            }
        }

        #endregion

        #region Inbound Handlers

        private void HandleApplianceFsmStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            var kind = (ApplianceKind)reader.ReadByte();
            bool active = reader.ReadBoolean();
            float f1 = reader.ReadSingle();
            float f2 = reader.ReadSingle();
            float f3 = reader.ReadSingle();
            float f4 = reader.ReadSingle();
            string text = reader.ReadString();

            if (senderId == LocalPlayerId) return;

            ApplyApplianceState(kind, active, f1, f2, f3, f4, text);
        }

        private void HandleGrillFoodStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int itemHash = reader.ReadInt32();
            bool isPike = reader.ReadBoolean();
            var stage = (CookStage)reader.ReadByte();
            float progress = reader.ReadSingle();

            if (senderId == LocalPlayerId) return;

            ApplyRemoteFoodCookStage(itemHash, isPike, stage, progress);
        }

        private void HandlePlayerStatusEffectPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            var effect = (StatusEffectKind)reader.ReadByte();
            bool active = reader.ReadBoolean();
            float intensity = reader.ReadSingle();

            if (senderId == LocalPlayerId) return;

            if (effect == StatusEffectKind.Vomit && active)
            {
                SpawnRemoteVomitFx(senderId);
                ctx?.Log?.Invoke("[Физиология] Напарника тошнит от перебора алкоголя!");
            }
            else if (effect == StatusEffectKind.Blindness)
            {
                remoteBlindPlayers[senderId] = active;
                if (active)
                {
                    SpawnRemoteBlindnessWarningFx(senderId, intensity);
                    ctx?.Log?.Invoke("[Физиология] Напарник ослеп от палёного спирта!");
                }
                else
                {
                    ctx?.Log?.Invoke("[Физиология] К напарнику вернулось зрение.");
                }
            }
        }

        private void HandlePlayerGearTogglePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            var kind = (GearKind)reader.ReadByte();
            bool flag1 = reader.ReadBoolean();
            bool flag2 = reader.ReadBoolean();
            bool flag3 = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;

            ApplyRemoteGear(senderId, kind, flag1, flag2, flag3);
        }

        private void HandleAppliancesSnapshotPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            float coffeeW = reader.ReadSingle();
            float coffeeC = reader.ReadSingle();
            float coffeeB = reader.ReadSingle();
            float coffeeCaf = reader.ReadSingle();

            bool fpLit = reader.ReadBoolean();
            float fpHeat = reader.ReadSingle();
            bool grillLit = reader.ReadBoolean();
            float grillHeat = reader.ReadSingle();

            bool trapInWater = reader.ReadBoolean();
            int fishCount = reader.ReadInt32();
            bool pcPower = reader.ReadBoolean();
            string pcProg = reader.ReadString();

            bool watchOwned = reader.ReadBoolean();
            bool radarInst = reader.ReadBoolean();
            bool radarBolt = reader.ReadBoolean();
            bool radarPow = reader.ReadBoolean();

            ApplyApplianceState(ApplianceKind.CoffeeMaker, coffeeB > 0.1f, coffeeW, coffeeC, coffeeB, coffeeCaf, string.Empty);
            ApplyApplianceState(ApplianceKind.Fireplace, fpLit, fpHeat, 0f, 0f, 0f, string.Empty);
            ApplyApplianceState(ApplianceKind.Grill, grillLit, grillHeat, 0f, 0f, 0f, string.Empty);
            ApplyApplianceState(ApplianceKind.FishTrap, trapInWater, fishCount, 0f, 0f, 0f, string.Empty);
            ApplyApplianceState(ApplianceKind.Computer386, pcPower, 0f, 0f, 0f, 0f, pcProg);
            ApplyRemoteGear(senderId, GearKind.Wristwatch, watchOwned, false, false);
            ApplyRemoteGear(senderId, GearKind.RadarDetector, radarInst, radarBolt, radarPow);

            int foodCount = reader.ReadInt32();
            for (int i = 0; i < foodCount; i++)
            {
                int hash = reader.ReadInt32();
                bool isPike = reader.ReadBoolean();
                var stage = (CookStage)reader.ReadByte();
                float prog = reader.ReadSingle();
                ApplyRemoteFoodCookStage(hash, isPike, stage, prog);
            }
        }

        private void HandleWaspNestStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            byte nestId = reader.ReadByte();
            bool destroyed = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;

            ApplyRemoteWaspNest(isRuscko: nestId == 0, destroyed: destroyed);
        }

        private void HandleContainerLidStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            var kind = (ContainerKind)reader.ReadByte();
            bool isOpen = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;

            ApplyRemoteContainerLid(kind, isOpen);
        }

        private void HandleHazardsSnapshotPacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            bool rusckoDead = reader.ReadBoolean();
            bool cottageDead = reader.ReadBoolean();

            if (rusckoDead) ApplyRemoteWaspNest(isRuscko: true, destroyed: true);
            if (cottageDead) ApplyRemoteWaspNest(isRuscko: false, destroyed: true);

            int lidCount = reader.ReadInt32();
            for (int i = 0; i < lidCount; i++)
            {
                var kind = (ContainerKind)reader.ReadByte();
                bool isOpen = reader.ReadBoolean();
                ApplyRemoteContainerLid(kind, isOpen);
            }
        }

        #endregion

        #region Apply Helpers

        private void ApplyApplianceState(ApplianceKind kind, bool active, float f1, float f2, float f3, float f4, string text)
        {
            isSuppressingAppliances = true;
            try
            {
                switch (kind)
                {
                    case ApplianceKind.CoffeeMaker:
                        lastCoffeeWater = f1;
                        lastCoffeeAmount = f2;
                        lastCoffeeBoil = f3;
                        lastCoffeeCaffeine = f4;
                        if (coffeeWaterVar != null) coffeeWaterVar.Value = f1;
                        if (coffeeAmountVar != null) coffeeAmountVar.Value = f2;
                        if (coffeeBoilVolVar != null) coffeeBoilVolVar.Value = f3;
                        if (coffeeCaffeineVar != null) coffeeCaffeineVar.Value = f4;
                        if (active && coffeePotCapFsm != null && HasFsmEvent(coffeePotCapFsm, "MP_COFFEE_BOIL"))
                        {
                            coffeePotCapFsm.SendEvent("MP_COFFEE_BOIL");
                        }
                        break;

                    case ApplianceKind.Fireplace:
                        lastFireplaceLit = active;
                        if (fireplaceBurnVar != null) fireplaceBurnVar.Value = active;
                        if (fireplaceHeatVar != null) fireplaceHeatVar.Value = f1;
                        if (fireplaceFsm != null)
                        {
                            fireplaceFsm.SendEvent(active ? "MP_FP_IGNITE" : "MP_FP_OUT");
                        }
                        break;

                    case ApplianceKind.Grill:
                        lastGrillLit = active;
                        if (grillFireVar != null) grillFireVar.Value = active;
                        if (grillHeatVar != null) grillHeatVar.Value = f1;
                        if (grillFsm != null)
                        {
                            grillFsm.SendEvent(active ? "MP_GRILL_IGNITE" : "MP_GRILL_OUT");
                        }
                        break;

                    case ApplianceKind.FishTrap:
                        lastFishTrapInWater = active;
                        lastFishCount = Mathf.RoundToInt(f1);
                        if (fishTrapInWaterVar != null) fishTrapInWaterVar.Value = active;
                        if (fishCountVar != null) fishCountVar.Value = lastFishCount;
                        break;

                    case ApplianceKind.Computer386:
                        lastPcPower = active;
                        if (computerSystemGo != null && computerSystemGo.activeSelf != active)
                        {
                            computerSystemGo.SetActive(active);
                        }
                        if (!string.IsNullOrEmpty(text))
                        {
                            int sep = text.IndexOf('|');
                            if (sep >= 0)
                            {
                                string disk = text.Substring(0, sep);
                                string prog = text.Substring(sep + 1);
                                if (pcDiskVar != null) pcDiskVar.Value = disk;
                                if (pcProgramVar != null) pcProgramVar.Value = prog;
                                lastPcProgram = prog;
                            }
                            else
                            {
                                if (pcProgramVar != null) pcProgramVar.Value = text;
                                lastPcProgram = text;
                            }
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingAppliances = false;
            }
        }

        private void ApplyRemoteFoodCookStage(int itemHash, bool isPike, CookStage stage, float progress)
        {
            isSuppressingCookables = true;
            try
            {
                if (!cookablesByHash.TryGetValue(itemHash, out TrackedCookableFood food) || food.GameObject == null)
                {
                    GameObject go = SceneHasher.FindObjectByHash(itemHash);
                    if (go != null)
                    {
                        ScanGrillAndCookables();
                        cookablesByHash.TryGetValue(itemHash, out food);
                    }
                }

                if (food == null || food.GameObject == null) return;

                food.LastStage = stage;
                food.LastProgress = progress;

                if (food.CookProgressVar != null) food.CookProgressVar.Value = progress;
                if (food.CookIntVar != null) food.CookIntVar.Value = (int)stage;
                if (food.CookedVar != null) food.CookedVar.Value = stage == CookStage.Cooked || stage == CookStage.Burned;
                if (food.BurnedVar != null) food.BurnedVar.Value = stage == CookStage.Burned;

                if (food.CookFsm != null)
                {
                    if (stage == CookStage.Burned && HasFsmEvent(food.CookFsm, "MP_COOK_BURNED"))
                    {
                        food.CookFsm.SendEvent("MP_COOK_BURNED");
                    }
                    else if (stage == CookStage.Cooked && HasFsmEvent(food.CookFsm, "MP_COOK_DONE"))
                    {
                        food.CookFsm.SendEvent("MP_COOK_DONE");
                    }
                    else if (stage == CookStage.Raw && HasFsmEvent(food.CookFsm, "MP_COOK_RAW"))
                    {
                        food.CookFsm.SendEvent("MP_COOK_RAW");
                    }
                }

                // Визуальное затемнение материала при подгорании, если шейдер/рендерер активен
                if (stage == CookStage.Burned)
                {
                    Renderer[] rends = food.GameObject.GetComponentsInChildren<Renderer>(true);
                    Renderer rend = (rends != null && rends.Length > 0) ? rends[0] : null;
                    if (rend != null && rend.material != null)
                    {
                        rend.material.color = new Color(0.18f, 0.14f, 0.12f, 1f);
                    }
                }
                else if (stage == CookStage.Cooked)
                {
                    Renderer[] rends = food.GameObject.GetComponentsInChildren<Renderer>(true);
                    Renderer rend = (rends != null && rends.Length > 0) ? rends[0] : null;
                    if (rend != null && rend.material != null)
                    {
                        rend.material.color = isPike ? new Color(0.75f, 0.62f, 0.45f, 1f) : new Color(0.68f, 0.42f, 0.28f, 1f);
                    }
                }
            }
            finally
            {
                isSuppressingCookables = false;
            }
        }

        private void ApplyRemoteGear(int senderId, GearKind kind, bool flag1, bool flag2, bool flag3)
        {
            isSuppressingGear = true;
            try
            {
                if (kind == GearKind.Wristwatch)
                {
                    lastWatchOwned = flag1;
                    if (wristwatchOwnedVar != null) wristwatchOwnedVar.Value = flag1;

                    bool prevViewing = remoteWatchViewing.TryGetValue(senderId, out bool pv) && pv;
                    remoteWatchViewing[senderId] = flag2;
                    if (flag2 && !prevViewing)
                    {
                        ctx?.Log?.Invoke("[Часы] Напарник смотрит время на наручных часах.");
                    }
                }
                else if (kind == GearKind.RadarDetector)
                {
                    lastRadarInstalled = flag1;
                    lastRadarBolted = flag2;
                    lastRadarPower = flag3;

                    if (radarInstalledVar != null) radarInstalledVar.Value = flag1;
                    if (radarBoltedVar != null) radarBoltedVar.Value = flag2;
                    if (radarPowerVar != null)
                    {
                        bool prevPower = radarPowerVar.Value;
                        if (prevPower != flag3 && radarSwitchFsm != null && HasFsmEvent(radarSwitchFsm, "MP_RADAR_TOGGLE"))
                        {
                            radarPowerVar.Value = !flag3;
                            radarSwitchFsm.SendEvent("MP_RADAR_TOGGLE");
                        }
                        else
                        {
                            radarPowerVar.Value = flag3;
                        }
                    }
                }
            }
            finally
            {
                isSuppressingGear = false;
            }
        }

        private void ApplyRemoteWaspNest(bool isRuscko, bool destroyed)
        {
            isSuppressingWasp = true;
            try
            {
                if (isRuscko) lastRusckoDestroyed = destroyed;
                else lastCottageDestroyed = destroyed;

                PlayMakerFSM fsm = isRuscko ? rusckoHiveFsm : cottageHiveFsm;
                FsmInt waspsVar = isRuscko ? rusckoWaspsVar : cottageWaspsVar;
                GameObject hiveGo = isRuscko ? rusckoHiveGo : cottageHiveGo;

                if (waspsVar != null && destroyed) waspsVar.Value = 0;
                if (fsm != null && destroyed && HasFsmEvent(fsm, "SLEEP"))
                {
                    fsm.SendEvent("SLEEP");
                }
                if (hiveGo != null && destroyed)
                {
                    hiveGo.SetActive(false);
                }

                ctx?.Log?.Invoke($"[Шершни] Осиное гнездо в {(isRuscko ? "Ruscko" : "коттедже")} обезврежено напарником!");
            }
            finally
            {
                isSuppressingWasp = false;
            }
        }

        private void ApplyRemoteContainerLid(ContainerKind kind, bool isOpen)
        {
            if (!containerLids.TryGetValue(kind, out TrackedContainerLid entry) || entry == null || !entry.IsOwnedAndHooked)
            {
                return;
            }

            isSuppressingContainers = true;
            try
            {
                entry.LastOpen = isOpen;
                if (entry.Fsm == null) return;

                if (entry.IsJerryCanCap)
                {
                    if (entry.OpenVar != null && entry.OpenVar.Value == isOpen) return;
                    if (entry.OpenVar != null) entry.OpenVar.Value = !isOpen;
                    if (entry.ClosedVar != null) entry.ClosedVar.Value = isOpen;
                    entry.Fsm.SendEvent("MP_TOGGLE");
                }
                else
                {
                    if (entry.OpenVar != null && entry.OpenVar.Value == isOpen) return;
                    entry.Fsm.SendEvent(isOpen ? "MP_OPEN" : "MP_CLOSE");
                }
            }
            finally
            {
                isSuppressingContainers = false;
            }
        }

        private void SpawnRemoteVomitFx(int playerId)
        {
            GameObject puppet = FindRemotePuppet(playerId);
            if (puppet == null) return;

            Vector3 spawnPos = puppet.transform.position + Vector3.up * 1.45f + puppet.transform.forward * 0.25f;

            if (vomitProjectorFsm != null && HasFsmEvent(vomitProjectorFsm, "DIRTY"))
            {
                float distSqr = (vomitProjectorFsm.transform.position - puppet.transform.position).sqrMagnitude;
                if (distSqr < 9.0f)
                {
                    isSuppressingAppliances = true;
                    try { vomitProjectorFsm.SendEvent("DIRTY"); }
                    finally { isSuppressingAppliances = false; }
                }
            }

            var fxGo = new GameObject($"OSC_RemoteVomitFx_{playerId}");
            fxGo.transform.position = spawnPos;
            fxGo.transform.rotation = Quaternion.LookRotation(puppet.transform.forward + Vector3.down * 0.45f);

            ParticleSystem ps = fxGo.AddComponent<ParticleSystem>();
            ps.simulationSpace = ParticleSystemSimulationSpace.World;
            ps.gravityModifier = 0.9f;
            ps.enableEmission = false;
            ps.playOnAwake = false;
            ps.maxParticles = 64;

            ParticleSystemRenderer psRend = fxGo.GetComponent<ParticleSystemRenderer>();
            if (psRend != null)
            {
                Shader sh = Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Diffuse");
                if (sh != null)
                {
                    psRend.material = new Material(sh) { color = new Color(0.45f, 0.58f, 0.14f, 0.9f) };
                }
            }

            Vector3 baseDir = (puppet.transform.forward + Vector3.down * 0.45f).normalized;
            var vomitColor = new Color32(115, 148, 36, 230);
            for (int i = 0; i < 24; i++)
            {
                Vector3 vel = baseDir * UnityEngine.Random.Range(1.6f, 2.8f) + UnityEngine.Random.insideUnitSphere * 0.35f;
                ps.Emit(spawnPos + UnityEngine.Random.insideUnitSphere * 0.04f, vel, UnityEngine.Random.Range(0.05f, 0.09f), 0.85f, vomitColor);
            }

            activeRemoteFx.Add(new ActiveRemoteFx
            {
                FxObject = fxGo,
                DestroyAtTime = Time.time + 2.0f
            });
        }

        private void SpawnRemoteBlindnessWarningFx(int playerId, float intensity)
        {
            GameObject puppet = FindRemotePuppet(playerId);
            if (puppet == null) return;

            var fxGo = new GameObject($"OSC_RemoteBlindFx_{playerId}");
            Vector3 headPos = puppet.transform.position + Vector3.up * 1.68f;
            fxGo.transform.position = headPos;
            fxGo.transform.SetParent(puppet.transform, true);

            ParticleSystem ps = fxGo.AddComponent<ParticleSystem>();
            ps.simulationSpace = ParticleSystemSimulationSpace.World;
            ps.gravityModifier = -0.05f;
            ps.enableEmission = false;
            ps.playOnAwake = false;
            ps.maxParticles = 32;

            ParticleSystemRenderer psRend = fxGo.GetComponent<ParticleSystemRenderer>();
            if (psRend != null)
            {
                Shader sh = Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Diffuse");
                if (sh != null)
                {
                    psRend.material = new Material(sh) { color = new Color(0.85f, 0.85f, 0.95f, Mathf.Clamp01(0.4f + intensity * 0.3f)) };
                }
            }

            byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt((0.4f + intensity * 0.3f) * 255f), 90, 230);
            var blindColor = new Color32(217, 217, 242, alpha);
            for (int i = 0; i < 12; i++)
            {
                Vector3 vel = Vector3.up * 0.35f + UnityEngine.Random.insideUnitSphere * 0.2f;
                ps.Emit(headPos + UnityEngine.Random.insideUnitSphere * 0.12f, vel, 0.06f, 1.2f, blindColor);
            }

            activeRemoteFx.Add(new ActiveRemoteFx
            {
                FxObject = fxGo,
                DestroyAtTime = Time.time + 3.0f
            });
        }

        private void TickTemporaryFx(float now)
        {
            for (int i = activeRemoteFx.Count - 1; i >= 0; i--)
            {
                ActiveRemoteFx fx = activeRemoteFx[i];
                if (fx == null || now >= fx.DestroyAtTime)
                {
                    if (fx != null && fx.FxObject != null)
                    {
                        UnityEngine.Object.Destroy(fx.FxObject);
                    }
                    activeRemoteFx.RemoveAt(i);
                }
            }
        }

        #endregion

        #region Utilities

        private bool IsLocalBlindActive()
        {
            if (playerBlindBoolVar != null && playerBlindBoolVar.Value) return true;
            if (playerBlindFloatVar != null && playerBlindFloatVar.Value > 0.25f) return true;
            if (blindnessObj != null && blindnessObj.activeSelf) return true;
            if (blindnessFsm != null && !string.IsNullOrEmpty(blindnessFsm.ActiveStateName))
            {
                string st = blindnessFsm.ActiveStateName;
                if (string.Equals(st, "Blind", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(st, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryClaimSafe(PlayMakerFSM fsm, string claimContext)
        {
            if (fsm == null) return false;
            if (ctx == null || ctx.Ownership == null) return true;

            string currentOwner = ctx.Ownership.GetOwner(fsm);
            if (!string.IsNullOrEmpty(currentOwner) && currentOwner != moduleId)
            {
                return false;
            }

            return ctx.Ownership.TryClaim(fsm, moduleId, claimContext);
        }

        private void HookFsmStateOnce(PlayMakerFSM fsm, string stateName, Action action)
        {
            if (fsm == null || string.IsNullOrEmpty(stateName) || action == null) return;
            if (!fsm.HasState(stateName)) return;

            int key = fsm.GetInstanceID() ^ stateName.GetHashCode();
            if (hookedFsmIds.Contains(key)) return;
            hookedFsmIds.Add(key);

            fsm.InsertAction(stateName, action, 0);
        }

        private static CookStage EvaluateCookStage(PlayMakerFSM fsm, FsmBool cookedVar, FsmBool burnedVar, FsmInt cookIntVar)
        {
            if (burnedVar != null && burnedVar.Value) return CookStage.Burned;
            if (cookIntVar != null)
            {
                if (cookIntVar.Value >= 2) return CookStage.Burned;
                if (cookIntVar.Value == 1) return CookStage.Cooked;
            }
            if (cookedVar != null && cookedVar.Value) return CookStage.Cooked;

            if (fsm != null && !string.IsNullOrEmpty(fsm.ActiveStateName))
            {
                string st = fsm.ActiveStateName;
                if (st.IndexOf("Burn", StringComparison.OrdinalIgnoreCase) >= 0) return CookStage.Burned;
                if (st.IndexOf("Cooked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(st, "Done", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(st, "Grilled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(st, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    return CookStage.Cooked;
                }
            }

            return CookStage.Raw;
        }

        private static bool IsHiveDestroyed(GameObject hiveGo, FsmInt waspsVar)
        {
            if (hiveGo == null) return false;
            if (!hiveGo.activeSelf) return true;
            if (waspsVar != null && waspsVar.Value <= 0) return true;
            return false;
        }

        private static bool HasFsmEvent(PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || string.IsNullOrEmpty(eventName)) return false;
            fsm.InitializeSafe();
            FsmEvent[] events = fsm.FsmEvents;
            if (events == null) return false;
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i] != null && events[i].Name == eventName) return true;
            }
            return false;
        }

        private static GameObject FindRemotePuppet(int playerId)
        {
            return GameObject.Find(playerId == 0 ? "RemotePuppet_Host" : $"RemotePuppet_Client_{playerId}");
        }

        private static GameObject FindObjectIncludingInactive(string name)
        {
            GameObject active = GameObject.Find(name);
            if (active != null) return active;

            GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i] != null && allGo[i].name == name)
                {
                    return allGo[i];
                }
            }
            return null;
        }

        #endregion
    }
}
