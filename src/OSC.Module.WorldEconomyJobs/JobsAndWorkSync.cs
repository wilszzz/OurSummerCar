using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldEconomyJobs
{
    /// <summary>
    /// Синхронизация работ и подработок этапа 6 (PacketType.JobsSync = 132):
    /// - Gifu ассенизатор: 5 септиков (уровень, крышка, оплата), уровень цистерны Gifu, слив на очистной станции и штраф за нелегальный слив в лесу;
    ///   (при этом FSM шланга/лебёдки принадлежат Vehicles.Controls — используем TryClaimSafe и read-only опрос без конфликтов);
    /// - Kekmet дрова: колка топором (Logwall), гидравлический дровокол, загрузка прицепа Flatbed (без захвата Hitch/Tip!) и сдача в сарай Livaloinen;
    /// - Брага (Kilju): брожение в ведре (вода, сахар, дрожжи, прогресс, уксус), розлив в бутылки и дегустация/продажа Йокке;
    /// - Клубника (сбор ягод и ящики) и зерноуборочный комбайн (жатка, обмолот, бункер, выгрузка);
    /// - Late-Join снимок (немедленно + через +2.0f) и очистка в OnClientDisconnected.
    /// </summary>
    internal sealed class JobsAndWorkSync
    {
        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private sealed class SepticWellEntry
        {
            public byte Index;
            public int Hash;
            public GameObject WellObject;
            public PlayMakerFSM LevelFsm;
            public FsmFloat LevelVar;
            public FsmBool PaidVar;
            public FsmBool EmptyVar;
            public PlayMakerFSM LidFsm;
            public FsmBool LidOpenVar;
            public bool LidHooked;
            public float LastLevel = -1f;
            public bool LastLidOpen;
            public bool LastPaid;
            public float SuppressUntilTime;
        }

        private sealed class StrawberryBoxEntry
        {
            public int Hash;
            public GameObject BoxObject;
            public PlayMakerFSM BoxFsm;
            public FsmInt CountIntVar;
            public FsmFloat FillFloatVar;
            public FsmBool FullBoolVar;
            public int LastCount = -1;
            public float LastFill = -1f;
            public bool LastFull;
        }

        private const byte SewageOpLevel = 1;
        private const byte SewageOpLid = 2;
        private const byte SewageOpPaid = 3;

        private const byte TruckOpLevel = 1;
        private const byte TruckOpSpillValve = 2;
        private const byte TruckOpTreatmentDump = 3;
        private const byte TruckOpIllegalForestFine = 4;

        private const byte FirewoodOpCreateLog = 1;
        private const byte FirewoodOpChopLog = 2;
        private const byte FirewoodOpSplitter = 3;
        private const byte FirewoodOpFlatbedLoad = 4;
        private const byte FirewoodOpShedDelivery = 5;

        private const byte KiljuOpBucketState = 1;
        private const byte KiljuOpBottleFill = 2;
        private const byte KiljuOpJokkeTasteOrSell = 3;

        private const byte FarmOpStrawberryPick = 1;
        private const byte FarmOpStrawberryBox = 2;
        private const byte FarmOpCombineState = 3;

        private const float ScanIntervalSeconds = 6.0f;
        private const float PollIntervalSeconds = 0.45f;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // 1. Gifu: септики и цистерна
        private readonly List<SepticWellEntry> septicWells = new List<SepticWellEntry>();
        private readonly Dictionary<int, SepticWellEntry> wellsByHash = new Dictionary<int, SepticWellEntry>();
        private GameObject gifuRoot;
        private PlayMakerFSM gifuTankFsm;
        private FsmFloat gifuTankLevelVar;
        private PlayMakerFSM gifuSpillValveFsm;
        private FsmBool gifuSpillOpenVar;
        private bool gifuSpillHooked;
        private float lastGifuTankLevel = -1f;
        private bool lastGifuSpillOpen;
        private float lastGifuTrickleTime;
        private float suppressGifuUntilTime;

        private PlayMakerFSM treatmentPlantFsm;
        private bool treatmentHooked;
        private Transform treatmentPlantTr;
        private float lastDumpEventTime = -10f;

        private PlayMakerFSM sewageFineFsm;
        private FsmFloat playerFinesVar;
        private float lastKnownFines = -1f;

        // 2. Дрова (Logwall, дровокол, прицеп Flatbed, сарай Livaloinen)
        private PlayMakerFSM logwallFsm;
        private FsmGameObject logwallCurrentLogVar;
        private bool logwallHooked;
        private readonly Dictionary<int, FixedJoint> activeLogJoints = new Dictionary<int, FixedJoint>();
        private readonly Dictionary<int, PlayMakerFSM> activeLogChildFsms = new Dictionary<int, PlayMakerFSM>();
        private int deterministicLogCounter;

        private PlayMakerFSM splitterFsm;
        private FsmBool splitterRunningVar;
        private bool splitterHooked;
        private bool lastSplitterRunning;
        private string lastSplitterState = string.Empty;

        private GameObject flatbedRoot;
        private PlayMakerFSM flatbedWoodFsm;
        private FsmInt flatbedWoodCountInt;
        private FsmFloat flatbedWoodLevelFloat;
        private int lastFlatbedWoodCount = -1;

        private PlayMakerFSM woodShedFsm;
        private FsmInt woodShedCountVar;
        private bool woodShedHooked;
        private int lastWoodShedCount = -1;

        // 3. Брага (Kilju) и Йокке
        private PlayMakerFSM kiljuBucketFsm;
        private FsmFloat kiljuWaterVar;
        private FsmFloat kiljuSugarVar;
        private FsmFloat kiljuYeastVar;
        private FsmFloat kiljuFermentVar;
        private FsmFloat kiljuAlcoholVar;
        private FsmBool kiljuLidOpenVar;
        private FsmBool kiljuReadyVar;
        private FsmBool kiljuVinegarVar;
        private FsmInt kiljuBottlesVar;
        private bool kiljuBucketHooked;

        private float lastKiljuWater = -1f;
        private float lastKiljuSugar = -1f;
        private float lastKiljuYeast = -1f;
        private float lastKiljuFerment = -1f;
        private float lastKiljuAlcohol = -1f;
        private bool lastKiljuLidOpen;
        private bool lastKiljuReady;
        private bool lastKiljuVinegar;
        private int lastKiljuBottles = -1;
        private float suppressKiljuUntilTime;

        private PlayMakerFSM jokkeBuyerFsm;
        private bool jokkeBuyerHooked;
        private int lastKiljuBottlesSold;

        // 4. Клубника и комбайн
        private PlayMakerFSM strawberryFieldFsm;
        private PlayMakerFSM strawberryAchFsm;
        private FsmInt strawberryPickedVar;
        private FsmInt strawberryBoxesVar;
        private bool strawberryHooked;
        private int lastStrawberryPicked = -1;
        private int lastStrawberryBoxes = -1;
        private readonly List<StrawberryBoxEntry> strawberryBoxes = new List<StrawberryBoxEntry>();
        private readonly Dictionary<int, StrawberryBoxEntry> strawberryBoxesByHash = new Dictionary<int, StrawberryBoxEntry>();

        private GameObject combineRoot;
        private PlayMakerFSM combineNormalizeFsm;
        private PlayMakerFSM combineGearFsm;
        private PlayMakerFSM combineGrainFsm;
        private FsmFloat combineHeaderVar;
        private FsmFloat combineGrainLevelVar;
        private FsmBool combineThreshingVar;
        private FsmBool combineUnloadOpenVar;
        private bool combineHooked;
        private bool lastCombineThreshing;
        private float lastCombineHeader = -1f;
        private float lastCombineGrain = -1f;
        private bool lastCombineUnloadOpen;
        private float suppressCombineUntilTime;

        // Анти-эхо флаги
        private bool isSuppressingSewage;
        private bool isSuppressingLog;
        private bool isSuppressingSplitter;
        private bool isSuppressingWoodShed;
        private bool isSuppressingKilju;
        private bool isSuppressingStrawberry;
        private bool isSuppressingCombine;

        private float nextScanTime;
        private float nextPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Септики: {septicWells.Count}, Дрова: {(logwallFsm != null ? "OK" : "-")}/{(woodShedFsm != null ? "Сарай" : "-")}, Брага: {(kiljuBucketFsm != null ? "OK" : "-")}, Ферма: {(strawberryFieldFsm != null || combineRoot != null ? "OK" : "-")}";

        public JobsAndWorkSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanAllObjects();
        }

        public void Reset()
        {
            hookedFsmIds.Clear();
            pendingSnapshots.Clear();

            septicWells.Clear();
            wellsByHash.Clear();
            gifuRoot = null;
            gifuTankFsm = null;
            gifuTankLevelVar = null;
            gifuSpillValveFsm = null;
            gifuSpillOpenVar = null;
            gifuSpillHooked = false;
            lastGifuTankLevel = -1f;
            lastGifuSpillOpen = false;
            lastGifuTrickleTime = 0f;
            suppressGifuUntilTime = 0f;

            treatmentPlantFsm = null;
            treatmentHooked = false;
            treatmentPlantTr = null;
            lastDumpEventTime = -10f;
            sewageFineFsm = null;
            playerFinesVar = null;
            lastKnownFines = -1f;

            logwallFsm = null;
            logwallCurrentLogVar = null;
            logwallHooked = false;
            activeLogJoints.Clear();
            activeLogChildFsms.Clear();
            deterministicLogCounter = 0;

            splitterFsm = null;
            splitterRunningVar = null;
            splitterHooked = false;
            lastSplitterRunning = false;
            lastSplitterState = string.Empty;

            flatbedRoot = null;
            flatbedWoodFsm = null;
            flatbedWoodCountInt = null;
            flatbedWoodLevelFloat = null;
            lastFlatbedWoodCount = -1;

            woodShedFsm = null;
            woodShedCountVar = null;
            woodShedHooked = false;
            lastWoodShedCount = -1;

            kiljuBucketFsm = null;
            kiljuWaterVar = null;
            kiljuSugarVar = null;
            kiljuYeastVar = null;
            kiljuFermentVar = null;
            kiljuAlcoholVar = null;
            kiljuLidOpenVar = null;
            kiljuReadyVar = null;
            kiljuVinegarVar = null;
            kiljuBottlesVar = null;
            kiljuBucketHooked = false;
            lastKiljuWater = -1f;
            lastKiljuSugar = -1f;
            lastKiljuYeast = -1f;
            lastKiljuFerment = -1f;
            lastKiljuAlcohol = -1f;
            lastKiljuLidOpen = false;
            lastKiljuReady = false;
            lastKiljuVinegar = false;
            lastKiljuBottles = -1;
            suppressKiljuUntilTime = 0f;
            jokkeBuyerFsm = null;
            jokkeBuyerHooked = false;
            lastKiljuBottlesSold = 0;

            strawberryFieldFsm = null;
            strawberryAchFsm = null;
            strawberryPickedVar = null;
            strawberryBoxesVar = null;
            strawberryHooked = false;
            lastStrawberryPicked = -1;
            lastStrawberryBoxes = -1;
            strawberryBoxes.Clear();
            strawberryBoxesByHash.Clear();

            combineRoot = null;
            combineNormalizeFsm = null;
            combineGearFsm = null;
            combineGrainFsm = null;
            combineHeaderVar = null;
            combineGrainLevelVar = null;
            combineThreshingVar = null;
            combineUnloadOpenVar = null;
            combineHooked = false;
            lastCombineThreshing = false;
            lastCombineHeader = -1f;
            lastCombineGrain = -1f;
            lastCombineUnloadOpen = false;
            suppressCombineUntilTime = 0f;

            isSuppressingSewage = false;
            isSuppressingLog = false;
            isSuppressingSplitter = false;
            isSuppressingWoodShed = false;
            isSuppressingKilju = false;
            isSuppressingStrawberry = false;
            isSuppressingCombine = false;

            nextScanTime = 0f;
            nextPollTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanIntervalSeconds;
                if (NeedsRescan())
                {
                    ScanAllObjects();
                }
            }

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextPollTime)
            {
                nextPollTime = now + PollIntervalSeconds;
                PollSewageAndGifu(now);
                PollFirewoodAndSplitter();
                PollKiljuBucket(now);
                PollStrawberryAndCombine(now);
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanAllObjects();
            SendSnapshotToClient(clientId);

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
        }

        private void ProcessPendingSnapshots(float now)
        {
            if (!ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    int cid = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    ScanAllObjects();
                    SendSnapshotToClient(cid);
                }
            }
        }

        private bool NeedsRescan()
        {
            return septicWells.Count < 5
                || gifuRoot == null
                || logwallFsm == null
                || woodShedFsm == null
                || kiljuBucketFsm == null
                || strawberryFieldFsm == null
                || combineRoot == null;
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

        #region Discovery & Hooks

        private void ScanAllObjects()
        {
            try
            {
                GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                ScanSewageAndGifu(allObjects);
                ScanFirewoodAndSplitter(allObjects);
                ScanKilju(allObjects);
                ScanStrawberryAndCombine(allObjects);
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[JobsAndWorkSync] Ошибка сканирования: {ex.Message}");
            }
        }

        private void ScanSewageAndGifu(GameObject[] allObjects)
        {
            // 1. Пять жилых септиков (Kaivo1..5 / SepticTank / WasteWell / shitwell01..05)
            if (septicWells.Count < 5)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (!n.StartsWith("Kaivo", StringComparison.OrdinalIgnoreCase) &&
                        !n.StartsWith("SepticTank", StringComparison.OrdinalIgnoreCase) &&
                        !ContainsIgnoreCase(n, "WasteWell") &&
                        !n.StartsWith("shitwell", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Игнорируем телефонные маркеры shitwell01_call
                    if (ContainsIgnoreCase(n, "_call")) continue;

                    int hash = SceneHasher.GetHash(go);
                    if (wellsByHash.ContainsKey(hash)) continue;

                    PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                    PlayMakerFSM levelFsm = null;
                    FsmFloat levelVar = null;
                    FsmBool paidVar = null;
                    FsmBool emptyVar = null;
                    PlayMakerFSM lidFsm = null;
                    FsmBool lidOpenVar = null;

                    for (int j = 0; j < fsms.Length; j++)
                    {
                        PlayMakerFSM fsm = fsms[j];
                        if (fsm == null) continue;
                        fsm.InitializeSafe();

                        if (levelFsm == null)
                        {
                            FsmFloat lv = FindFirstFloat(fsm, "Level", "Shit", "Waste", "Amount", "SewageLevel");
                            if (lv != null)
                            {
                                levelFsm = fsm;
                                levelVar = lv;
                                paidVar = FindFirstBool(fsm, "Paid", "Pay", "Done", "PaidOut");
                                emptyVar = FindFirstBool(fsm, "Empty", "IsEmpty", "Pumped");
                            }
                        }

                        if (lidFsm == null && (fsm.FsmName == "Use" || ContainsIgnoreCase(fsm.gameObject.name, "lid")) &&
                            (fsm.HasState("Open") || fsm.HasState("Close")))
                        {
                            lidFsm = fsm;
                            lidOpenVar = FindFirstBool(fsm, "Open", "IsOpen", "Opened");
                        }
                    }

                    if (levelFsm == null && lidFsm == null) continue;

                    byte wellIdx = ResolveWellIndex(n, (byte)septicWells.Count);
                    var entry = new SepticWellEntry
                    {
                        Index = wellIdx,
                        Hash = hash,
                        WellObject = go,
                        LevelFsm = levelFsm,
                        LevelVar = levelVar,
                        PaidVar = paidVar,
                        EmptyVar = emptyVar,
                        LidFsm = lidFsm,
                        LidOpenVar = lidOpenVar,
                        LastLevel = levelVar != null ? levelVar.Value : -1f,
                        LastLidOpen = ReadLidOpen(lidFsm, lidOpenVar),
                        LastPaid = paidVar != null && paidVar.Value
                    };

                    if (lidFsm != null && !hookedFsmIds.Contains(lidFsm.GetInstanceID()) &&
                        TryClaimSafe(lidFsm, $"Jobs_SepticLid_{wellIdx}"))
                    {
                        hookedFsmIds.Add(lidFsm.GetInstanceID());
                        entry.LidHooked = true;
                        lidFsm.AddSafeGlobalTransition("MP_OPEN", "Open");
                        lidFsm.AddSafeGlobalTransition("MP_CLOSE", "Close");

                        SepticWellEntry captured = entry;
                        if (lidFsm.HasState("Open"))
                        {
                            lidFsm.InsertAction("Open", () =>
                            {
                                if (isSuppressingSewage) return;
                                captured.LastLidOpen = true;
                                SendSewageWellPacket(captured.Hash, captured.Index, SewageOpLid, captured.LevelVar != null ? captured.LevelVar.Value : 0f, true);
                            }, 0, true);
                        }
                        if (lidFsm.HasState("Close"))
                        {
                            lidFsm.InsertAction("Close", () =>
                            {
                                if (isSuppressingSewage) return;
                                captured.LastLidOpen = false;
                                SendSewageWellPacket(captured.Hash, captured.Index, SewageOpLid, captured.LevelVar != null ? captured.LevelVar.Value : 0f, false);
                            }, 0, true);
                        }
                    }

                    septicWells.Add(entry);
                    wellsByHash[hash] = entry;
                }
            }

            // 2. Gifu цистерна и клапан слива (БЕЗ захвата шланга/лебёдки из Vehicles.Controls!)
            if (gifuRoot == null)
            {
                gifuRoot = GameObject.Find("GIFU(750/450psi)") ?? GameObject.Find("Gifu");
            }

            if (gifuRoot != null)
            {
                if (gifuTankFsm == null)
                {
                    PlayMakerFSM[] fsms = gifuRoot.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        PlayMakerFSM fsm = fsms[i];
                        if (fsm == null) continue;
                        // Игнорируем ActivateHose и hose coupler, принадлежащие Vehicles.Controls
                        if (ContainsIgnoreCase(fsm.gameObject.name, "Hose") ||
                            ContainsIgnoreCase(fsm.gameObject.name, "Winch"))
                        {
                            continue;
                        }

                        fsm.InitializeSafe();
                        FsmFloat lv = FindFirstFloat(fsm, "SewageLevel", "ShitLevel", "WasteLevel", "Level", "Shit", "Waste", "Amount");
                        if (lv != null && (ContainsIgnoreCase(fsm.gameObject.name, "Shit") ||
                                           ContainsIgnoreCase(fsm.gameObject.name, "Waste") ||
                                           ContainsIgnoreCase(fsm.gameObject.name, "Tank") ||
                                           ContainsIgnoreCase(fsm.gameObject.name, "Sewage") ||
                                           ContainsIgnoreCase(fsm.FsmName, "Shit") ||
                                           ContainsIgnoreCase(fsm.FsmName, "Level") ||
                                           ContainsIgnoreCase(fsm.FsmName, "Tank")))
                        {
                            gifuTankFsm = fsm;
                            gifuTankLevelVar = lv;
                            lastGifuTankLevel = lv.Value;
                            break;
                        }
                    }
                }

                if (gifuSpillValveFsm == null)
                {
                    Transform spillTr = PlayMakerBridge.FindChildRecursive(gifuRoot.transform, "OpenSpill");
                    GameObject spillGo = spillTr != null ? spillTr.gameObject : GameObject.Find("OpenSpill");
                    if (spillGo != null)
                    {
                        PlayMakerFSM fsm = PlayMakerBridge.FindFsm(spillGo, "Use")
                                        ?? PlayMakerBridge.FindFsm(spillGo, "Spill")
                                        ?? spillGo.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            gifuSpillValveFsm = fsm;
                            fsm.InitializeSafe();
                            gifuSpillOpenVar = FindFirstBool(fsm, "Open", "Spill", "Valve", "Pump");
                            lastGifuSpillOpen = ReadSpillOpen(fsm, gifuSpillOpenVar);

                            if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_GifuOpenSpill"))
                            {
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                gifuSpillHooked = true;
                                string openState = fsm.HasState("Open") ? "Open" : (fsm.HasState("Pump") ? "Pump" : null);
                                string closeState = fsm.HasState("Close") ? "Close" : (fsm.HasState("Stop") ? "Stop" : null);
                                if (openState != null)
                                {
                                    fsm.AddSafeGlobalTransition("MP_OPEN", openState);
                                    fsm.InsertAction(openState, () =>
                                    {
                                        if (isSuppressingSewage) return;
                                        OnLocalSpillChanged(true);
                                    }, 0, true);
                                }
                                if (closeState != null)
                                {
                                    fsm.AddSafeGlobalTransition("MP_CLOSE", closeState);
                                    fsm.InsertAction(closeState, () =>
                                    {
                                        if (isSuppressingSewage) return;
                                        OnLocalSpillChanged(false);
                                    }, 0, true);
                                }
                            }
                        }
                    }
                }
            }

            // 3. Очистная станция и штрафы за слив в лесу
            if (treatmentPlantTr == null || treatmentPlantFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (n == "WASTEWATER" || n == "WasteWaterPlant" || n == "TreatmentPlant" ||
                        n == "SewagePlant" || n == "WasteTrigger" || n == "ShitDump")
                    {
                        treatmentPlantTr = go.transform;
                        PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int j = 0; j < fsms.Length; j++)
                        {
                            PlayMakerFSM fsm = fsms[j];
                            if (fsm == null) continue;
                            if (fsm.HasState("Pay") || fsm.HasState("Dump") || fsm.HasState("Empty") || fsm.HasState("Sell"))
                            {
                                treatmentPlantFsm = fsm;
                                if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_TreatmentPlant"))
                                {
                                    hookedFsmIds.Add(fsm.GetInstanceID());
                                    treatmentHooked = true;
                                    string st = fsm.HasState("Dump") ? "Dump" : (fsm.HasState("Pay") ? "Pay" : (fsm.HasState("Empty") ? "Empty" : "Sell"));
                                    fsm.AddSafeGlobalTransition("MP_DUMP", st);
                                    fsm.InsertAction(st, () =>
                                    {
                                        if (isSuppressingSewage) return;
                                        lastDumpEventTime = Time.time;
                                        float lvl = gifuTankLevelVar != null ? gifuTankLevelVar.Value : 0f;
                                        SendSewageTruckPacket(TruckOpTreatmentDump, lvl, false, 0f);
                                    }, 0, true);
                                }
                                break;
                            }
                        }
                        if (treatmentPlantFsm != null) break;
                    }
                }
            }

            if (playerFinesVar == null && PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                playerFinesVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerFines")
                              ?? PlayMakerGlobals.Instance.Variables.FindFsmFloat("Fines");
                if (playerFinesVar != null)
                {
                    lastKnownFines = playerFinesVar.Value;
                }
            }

            if (sewageFineFsm == null)
            {
                GameObject fineGo = GameObject.Find("ShitFine") ?? GameObject.Find("WasteFine");
                if (fineGo != null)
                {
                    PlayMakerFSM fsm = fineGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        sewageFineFsm = fsm;
                        fsm.InitializeSafe();
                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_SewageFine"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            if (fsm.HasState("Fine"))
                            {
                                fsm.AddSafeGlobalTransition("MP_FINE", "Fine");
                                fsm.InsertAction("Fine", () =>
                                {
                                    if (isSuppressingSewage) return;
                                    float fineAmt = playerFinesVar != null ? playerFinesVar.Value : 0f;
                                    float lvl = gifuTankLevelVar != null ? gifuTankLevelVar.Value : 0f;
                                    SendSewageTruckPacket(TruckOpIllegalForestFine, lvl, true, fineAmt);
                                }, 0, true);
                            }
                        }
                    }
                }
            }
        }

        private void ScanFirewoodAndSplitter(GameObject[] allObjects)
        {
            // 1. Logwall (колка топором)
            if (logwallFsm == null)
            {
                GameObject yard = GameObject.Find("YARD");
                Transform logwallTr = yard != null ? yard.transform.Find("MachineHall/Logging/Logwall") : null;
                if (logwallTr == null)
                {
                    for (int i = 0; i < allObjects.Length; i++)
                    {
                        if (allObjects[i] != null && allObjects[i].name == "Logwall")
                        {
                            logwallTr = allObjects[i].transform;
                            break;
                        }
                    }
                }

                if (logwallTr != null)
                {
                    PlayMakerFSM fsm = logwallTr.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        logwallFsm = fsm;
                        fsm.InitializeSafe();
                        logwallCurrentLogVar = fsm.FsmVariables.FindFsmGameObject("Log");

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_Logwall"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            logwallHooked = true;
                            fsm.AddSafeGlobalTransition("MP_SPAWN", "Create log");
                            if (fsm.HasState("Create log"))
                            {
                                fsm.InsertAction("Create log", () =>
                                {
                                    if (isSuppressingLog) return;
                                    OnLocalLogCreated();
                                }, -1, true);
                            }
                        }
                    }
                }
            }

            // 2. Гидравлический дровокол (LogSplitter / Klapi)
            if (splitterFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (ContainsIgnoreCase(n, "LogSplitter") ||
                        ContainsIgnoreCase(n, "WoodSplitter") ||
                        ContainsIgnoreCase(n, "Klapi") ||
                        n == "Splitter")
                    {
                        PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int j = 0; j < fsms.Length; j++)
                        {
                            PlayMakerFSM fsm = fsms[j];
                            if (fsm == null) continue;
                            if (fsm.HasState("Split") || fsm.HasState("On") || fsm.HasState("Running") ||
                                fsm.FsmName == "Use" || fsm.FsmName == "Split" || fsm.FsmName == "Hydraulics")
                            {
                                splitterFsm = fsm;
                                fsm.InitializeSafe();
                                splitterRunningVar = FindFirstBool(fsm, "On", "Running", "Split", "Active", "PTO");
                                lastSplitterRunning = ReadSplitterRunning(fsm, splitterRunningVar);
                                lastSplitterState = fsm.ActiveStateName ?? string.Empty;

                                if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_LogSplitter"))
                                {
                                    hookedFsmIds.Add(fsm.GetInstanceID());
                                    splitterHooked = true;
                                    if (fsm.HasState("Split"))
                                    {
                                        fsm.AddSafeGlobalTransition("MP_SPLIT", "Split");
                                        fsm.InsertAction("Split", () =>
                                        {
                                            if (isSuppressingSplitter) return;
                                            SendFirewoodPacket(FirewoodOpSplitter, 1, 0, true, "Split");
                                        }, 0, true);
                                    }
                                    if (fsm.HasState("On"))
                                    {
                                        fsm.AddSafeGlobalTransition("MP_ON", "On");
                                        fsm.InsertAction("On", () =>
                                        {
                                            if (isSuppressingSplitter) return;
                                            SendFirewoodPacket(FirewoodOpSplitter, 1, 0, true, "On");
                                        }, 0, true);
                                    }
                                    if (fsm.HasState("Off"))
                                    {
                                        fsm.AddSafeGlobalTransition("MP_OFF", "Off");
                                        fsm.InsertAction("Off", () =>
                                        {
                                            if (isSuppressingSplitter) return;
                                            SendFirewoodPacket(FirewoodOpSplitter, 0, 0, false, "Off");
                                        }, 0, true);
                                    }
                                }
                                break;
                            }
                        }
                        if (splitterFsm != null) break;
                    }
                }
            }

            // 3. Прицеп Flatbed — ТОЛЬКО опрос загрузки дров (не трогаем Hitch/Tip FSMs из Vehicles.Controls!)
            if (flatbedRoot == null)
            {
                flatbedRoot = GameObject.Find("FLATBED") ?? GameObject.Find("Flatbed");
            }
            if (flatbedRoot != null && flatbedWoodFsm == null)
            {
                PlayMakerFSM[] fsms = flatbedRoot.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM fsm = fsms[i];
                    if (fsm == null) continue;
                    string fn = fsm.FsmName;
                    if (fn == "Hitch" || fn == "Attach" || fn == "Tip" || fn == "Hydraulics") continue;

                    fsm.InitializeSafe();
                    FsmInt wc = FindFirstInt(fsm, "Wood", "WoodCount", "Logs", "Firewood", "Count");
                    FsmFloat wl = FindFirstFloat(fsm, "Wood", "WoodLevel", "Load", "Fill");
                    if (wc != null || wl != null || ContainsIgnoreCase(fsm.gameObject.name, "Wood") || ContainsIgnoreCase(fn, "Wood"))
                    {
                        flatbedWoodFsm = fsm;
                        flatbedWoodCountInt = wc;
                        flatbedWoodLevelFloat = wl;
                        lastFlatbedWoodCount = ReadFlatbedWoodCount();
                        break;
                    }
                }
            }

            // 4. Сарай покупателя дров (Livaloinen / WoodShed)
            if (woodShedFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    if (go.name == "WoodShed" || go.name == "WoodTrigger" || go.name == "Livaloinen" || go.name == "FirewoodTrigger")
                    {
                        PlayMakerFSM fsm = go.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            woodShedFsm = fsm;
                            fsm.InitializeSafe();
                            woodShedCountVar = FindFirstInt(fsm, "WoodCount", "Count", "Logs", "Delivered");
                            lastWoodShedCount = woodShedCountVar != null ? woodShedCountVar.Value : 0;

                            if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_WoodShed"))
                            {
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                woodShedHooked = true;
                                string payState = fsm.HasState("Pay") ? "Pay" : (fsm.HasState("Done") ? "Done" : (fsm.HasState("Unload") ? "Unload" : null));
                                if (payState != null)
                                {
                                    fsm.AddSafeGlobalTransition("MP_PAY", payState);
                                    fsm.InsertAction(payState, () =>
                                    {
                                        if (isSuppressingWoodShed) return;
                                        int count = woodShedCountVar != null ? woodShedCountVar.Value : lastWoodShedCount;
                                        lastWoodShedCount = count;
                                        SendFirewoodPacket(FirewoodOpShedDelivery, count, 1, true, payState);
                                    }, 0, true);
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        private void ScanKilju(GameObject[] allObjects)
        {
            if (kiljuBucketFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (ContainsIgnoreCase(n, "kilju bucket") ||
                        n == "bucket(itemx)" ||
                        n == "KiljuBucket" ||
                        ContainsIgnoreCase(n, "kilju_bucket"))
                    {
                        PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int j = 0; j < fsms.Length; j++)
                        {
                            PlayMakerFSM fsm = fsms[j];
                            if (fsm == null) continue;
                            fsm.InitializeSafe();

                            FsmFloat w = FindFirstFloat(fsm, "Water", "WaterLevel");
                            FsmFloat s = FindFirstFloat(fsm, "Sugar");
                            FsmFloat y = FindFirstFloat(fsm, "Yeast");
                            FsmFloat f = FindFirstFloat(fsm, "Ferment", "Brew", "Time", "Progress");
                            if (w != null || s != null || y != null || f != null ||
                                ContainsIgnoreCase(fsm.FsmName, "Brew") || ContainsIgnoreCase(fsm.FsmName, "Kilju"))
                            {
                                kiljuBucketFsm = fsm;
                                kiljuWaterVar = w;
                                kiljuSugarVar = s;
                                kiljuYeastVar = y;
                                kiljuFermentVar = f;
                                kiljuAlcoholVar = FindFirstFloat(fsm, "Alcohol", "Quality", "Kilju");
                                kiljuLidOpenVar = FindFirstBool(fsm, "Lid", "Open", "LidOpen");
                                kiljuReadyVar = FindFirstBool(fsm, "Ready", "Done", "Brewed");
                                kiljuVinegarVar = FindFirstBool(fsm, "Vinegar", "Spoiled");
                                kiljuBottlesVar = FindFirstInt(fsm, "Bottles", "BottleCount", "Count");
                                SnapshotKiljuLocals();

                                if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_KiljuBucket"))
                                {
                                    hookedFsmIds.Add(fsm.GetInstanceID());
                                    kiljuBucketHooked = true;
                                    string bottleState = fsm.HasState("Bottle") ? "Bottle" : (fsm.HasState("Fill") ? "Fill" : null);
                                    if (bottleState != null)
                                    {
                                        fsm.AddSafeGlobalTransition("MP_BOTTLE", bottleState);
                                        fsm.InsertAction(bottleState, () =>
                                        {
                                            if (isSuppressingKilju) return;
                                            SnapshotKiljuLocals();
                                            SendKiljuBucketPacket(KiljuOpBottleFill);
                                        }, 0, true);
                                    }
                                }
                                break;
                            }
                        }
                        if (kiljuBucketFsm != null) break;
                    }
                }
            }

            if (jokkeBuyerFsm == null)
            {
                GameObject jokke = GameObject.Find("JOKKE") ?? GameObject.Find("KiljuBuyer") ?? GameObject.Find("Jokke");
                if (jokke != null)
                {
                    PlayMakerFSM[] fsms = jokke.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        PlayMakerFSM fsm = fsms[i];
                        if (fsm == null) continue;
                        if (fsm.FsmName == "Use" || fsm.FsmName == "Order" || fsm.FsmName == "Buy" || fsm.FsmName == "Taste")
                        {
                            fsm.InitializeSafe();
                            string buyState = fsm.HasState("Buy") ? "Buy" : (fsm.HasState("Pay") ? "Pay" : (fsm.HasState("Taste") ? "Taste" : null));
                            if (buyState != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_JokkeKiljuBuyer"))
                            {
                                jokkeBuyerFsm = fsm;
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                jokkeBuyerHooked = true;
                                fsm.AddSafeGlobalTransition("MP_TASTE", buyState);
                                fsm.InsertAction(buyState, () =>
                                {
                                    if (isSuppressingKilju) return;
                                    lastKiljuBottlesSold++;
                                    SendKiljuTastePacket(lastKiljuBottlesSold, true);
                                }, 0, true);
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void ScanStrawberryAndCombine(GameObject[] allObjects)
        {
            // 1. Клубничное поле и ящики
            if (strawberryFieldFsm == null || strawberryAchFsm == null)
            {
                GameObject field = GameObject.Find("StrawberryField") ?? GameObject.Find("strawberry_pick") ?? GameObject.Find("BerryTrigger");
                if (field != null)
                {
                    PlayMakerFSM ach = PlayMakerBridge.FindFsm(field, "Achievement");
                    PlayMakerFSM main = ach ?? field.GetComponent<PlayMakerFSM>();
                    if (main != null)
                    {
                        strawberryFieldFsm = main;
                        strawberryAchFsm = ach ?? main;
                        main.InitializeSafe();
                        strawberryPickedVar = FindFirstInt(main, "StrawberriesPicked", "Berries", "Picked", "Count");
                        strawberryBoxesVar = FindFirstInt(main, "Boxes", "BoxCount", "FullBoxes");
                        if (strawberryPickedVar != null) lastStrawberryPicked = strawberryPickedVar.Value;
                        if (strawberryBoxesVar != null) lastStrawberryBoxes = strawberryBoxesVar.Value;

                        if (!hookedFsmIds.Contains(main.GetInstanceID()) && TryClaimSafe(main, "Jobs_StrawberryField"))
                        {
                            hookedFsmIds.Add(main.GetInstanceID());
                            strawberryHooked = true;
                            string pickState = main.HasState("BERRY") ? "BERRY" : (main.HasState("Pick") ? "Pick" : (main.HasState("Box full") ? "Box full" : null));
                            if (pickState != null)
                            {
                                main.AddSafeGlobalTransition("MP_PICK", pickState);
                                main.InsertAction(pickState, () =>
                                {
                                    if (isSuppressingStrawberry) return;
                                    int picked = strawberryPickedVar != null ? strawberryPickedVar.Value : (lastStrawberryPicked + 1);
                                    int boxes = strawberryBoxesVar != null ? strawberryBoxesVar.Value : Math.Max(0, lastStrawberryBoxes);
                                    lastStrawberryPicked = picked;
                                    lastStrawberryBoxes = boxes;
                                    SendStrawberryPacket(FarmOpStrawberryPick, 0, picked, boxes, 0f, false);
                                }, 0, true);
                            }
                        }
                    }
                }
            }

            if (strawberryBoxes.Count == 0)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (ContainsIgnoreCase(n, "strawberry box") || n == "StrawberryBox" || ContainsIgnoreCase(n, "mansikka"))
                    {
                        int hash = SceneHasher.GetHash(go);
                        if (strawberryBoxesByHash.ContainsKey(hash)) continue;
                        PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Data") ?? PlayMakerBridge.FindFsm(go, "Use") ?? go.GetComponent<PlayMakerFSM>();
                        if (fsm == null) continue;
                        fsm.InitializeSafe();
                        FsmInt cnt = FindFirstInt(fsm, "Berries", "Count", "Amount", "Picked");
                        FsmFloat fill = FindFirstFloat(fsm, "Fill", "Level", "Amount");
                        FsmBool full = FindFirstBool(fsm, "Full", "IsFull");
                        if (cnt == null && fill == null && full == null) continue;

                        var box = new StrawberryBoxEntry
                        {
                            Hash = hash,
                            BoxObject = go,
                            BoxFsm = fsm,
                            CountIntVar = cnt,
                            FillFloatVar = fill,
                            FullBoolVar = full,
                            LastCount = cnt != null ? cnt.Value : 0,
                            LastFill = fill != null ? fill.Value : 0f,
                            LastFull = full != null && full.Value
                        };
                        strawberryBoxes.Add(box);
                        strawberryBoxesByHash[hash] = box;
                    }
                }
            }

            // 2. Зерноуборочный комбайн (никогда не захватываем MainRigidbody / DriveTrigger!)
            if (combineRoot == null)
            {
                combineRoot = GameObject.Find("COMBINE(350-400psi)") ?? GameObject.Find("Combine");
            }
            if (combineRoot != null && !combineHooked)
            {
                if (combineNormalizeFsm == null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(combineRoot, "Normalize");
                    if (fsm != null)
                    {
                        combineNormalizeFsm = fsm;
                        fsm.InitializeSafe();
                        combineThreshingVar = FindFirstBool(fsm, "Threshing", "Thresh", "Running", "Active");
                        combineHeaderVar = FindFirstFloat(fsm, "Header", "HeaderHeight", "Height");
                        if (combineThreshingVar != null) lastCombineThreshing = combineThreshingVar.Value;
                        if (combineHeaderVar != null) lastCombineHeader = combineHeaderVar.Value;

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Jobs_CombineNormalize"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            combineHooked = true;
                            if (fsm.HasState("Thresh"))
                            {
                                fsm.AddSafeGlobalTransition("MP_THRESH", "Thresh");
                                fsm.InsertAction("Thresh", () =>
                                {
                                    if (isSuppressingCombine) return;
                                    lastCombineThreshing = true;
                                    SendCombinePacket();
                                }, 0, true);
                            }
                            if (fsm.HasState("Stop"))
                            {
                                fsm.AddSafeGlobalTransition("MP_STOP", "Stop");
                                fsm.InsertAction("Stop", () =>
                                {
                                    if (isSuppressingCombine) return;
                                    lastCombineThreshing = false;
                                    SendCombinePacket();
                                }, 0, true);
                            }
                        }
                    }
                }

                if (combineGearFsm == null)
                {
                    combineGearFsm = PlayMakerBridge.FindFsm(combineRoot, "GearIndicator");
                }

                if (combineGrainFsm == null)
                {
                    PlayMakerFSM[] fsms = combineRoot.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        PlayMakerFSM fsm = fsms[i];
                        if (fsm == null) continue;
                        if (fsm.gameObject.name == "DriveTrigger" || fsm.FsmName == "PlayerTrigger") continue;

                        fsm.InitializeSafe();
                        FsmFloat grain = FindFirstFloat(fsm, "Grain", "Tank", "Level", "Harvest", "Fill");
                        FsmBool unload = FindFirstBool(fsm, "Unload", "Pipe", "Auger", "Open");
                        if (grain != null || unload != null)
                        {
                            combineGrainFsm = fsm;
                            combineGrainLevelVar = grain;
                            combineUnloadOpenVar = unload;
                            if (grain != null) lastCombineGrain = grain.Value;
                            if (unload != null) lastCombineUnloadOpen = unload.Value;
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region Polling

        private void PollSewageAndGifu(float now)
        {
            if (isSuppressingSewage) return;

            for (int i = 0; i < septicWells.Count; i++)
            {
                SepticWellEntry well = septicWells[i];
                if (well == null || now < well.SuppressUntilTime) continue;

                if (well.LevelVar != null)
                {
                    float curLevel = well.LevelVar.Value;
                    if (well.LastLevel < 0f)
                    {
                        well.LastLevel = curLevel;
                    }
                    else if (Mathf.Abs(curLevel - well.LastLevel) >= 1.5f || (curLevel <= 0.05f && well.LastLevel > 0.05f))
                    {
                        well.LastLevel = curLevel;
                        SendSewageWellPacket(well.Hash, well.Index, SewageOpLevel, curLevel, well.LastLidOpen);
                    }
                }

                if (!well.LidHooked && well.LidFsm != null)
                {
                    bool lidOpen = ReadLidOpen(well.LidFsm, well.LidOpenVar);
                    if (lidOpen != well.LastLidOpen)
                    {
                        well.LastLidOpen = lidOpen;
                        SendSewageWellPacket(well.Hash, well.Index, SewageOpLid, well.LevelVar != null ? well.LevelVar.Value : 0f, lidOpen);
                    }
                }

                if (well.PaidVar != null)
                {
                    bool paid = well.PaidVar.Value;
                    if (paid != well.LastPaid)
                    {
                        well.LastPaid = paid;
                        SendSewageWellPacket(well.Hash, well.Index, SewageOpPaid, well.LevelVar != null ? well.LevelVar.Value : 0f, paid);
                    }
                }
            }

            if (now >= suppressGifuUntilTime)
            {
                if (gifuTankLevelVar != null)
                {
                    float curTank = gifuTankLevelVar.Value;
                    if (lastGifuTankLevel < 0f)
                    {
                        lastGifuTankLevel = curTank;
                    }
                    else
                    {
                        float delta = curTank - lastGifuTankLevel;
                        bool suddenDrop = delta <= -5.0f;
                        bool significantChange = Mathf.Abs(delta) >= 2.0f && (now - lastGifuTrickleTime) >= 0.75f;
                        if (suddenDrop || significantChange)
                        {
                            lastGifuTankLevel = curTank;
                            lastGifuTrickleTime = now;
                            SendSewageTruckPacket(TruckOpLevel, curTank, lastGifuSpillOpen, 0f);

                            // Определяем слив на очистной станции vs нелегальный слив в лесу по состоянию клапана и расстоянию
                            if (suddenDrop && lastGifuSpillOpen && (now - lastDumpEventTime) > 3.0f)
                            {
                                lastDumpEventTime = now;
                                if (IsNearTreatmentPlant())
                                {
                                    SendSewageTruckPacket(TruckOpTreatmentDump, curTank, true, 0f);
                                }
                                else
                                {
                                    float fines = playerFinesVar != null ? playerFinesVar.Value : 0f;
                                    SendSewageTruckPacket(TruckOpIllegalForestFine, curTank, true, fines);
                                }
                            }
                        }
                    }
                }

                if (!gifuSpillHooked && gifuSpillValveFsm != null)
                {
                    bool spillOpen = ReadSpillOpen(gifuSpillValveFsm, gifuSpillOpenVar);
                    if (spillOpen != lastGifuSpillOpen)
                    {
                        OnLocalSpillChanged(spillOpen);
                    }
                }

                if (playerFinesVar != null)
                {
                    float curFines = playerFinesVar.Value;
                    if (lastKnownFines >= 0f && curFines - lastKnownFines >= 100f && lastGifuSpillOpen && !IsNearTreatmentPlant())
                    {
                        lastKnownFines = curFines;
                        SendSewageTruckPacket(TruckOpIllegalForestFine, lastGifuTankLevel, true, curFines);
                    }
                    else
                    {
                        lastKnownFines = curFines;
                    }
                }
            }
        }

        private void PollFirewoodAndSplitter()
        {
            if (!isSuppressingSplitter && splitterFsm != null && !splitterHooked)
            {
                bool running = ReadSplitterRunning(splitterFsm, splitterRunningVar);
                string st = splitterFsm.ActiveStateName ?? string.Empty;
                if (running != lastSplitterRunning || !string.Equals(st, lastSplitterState, StringComparison.Ordinal))
                {
                    lastSplitterRunning = running;
                    lastSplitterState = st;
                    SendFirewoodPacket(FirewoodOpSplitter, running ? 1 : 0, 0, running, st);
                }
            }

            if (flatbedRoot != null && flatbedWoodFsm != null)
            {
                int woodCnt = ReadFlatbedWoodCount();
                if (lastFlatbedWoodCount < 0)
                {
                    lastFlatbedWoodCount = woodCnt;
                }
                else if (woodCnt != lastFlatbedWoodCount)
                {
                    lastFlatbedWoodCount = woodCnt;
                    SendFirewoodPacket(FirewoodOpFlatbedLoad, woodCnt, 0, woodCnt > 0, string.Empty);
                }
            }

            if (!isSuppressingWoodShed && woodShedFsm != null && woodShedCountVar != null)
            {
                int shedCount = woodShedCountVar.Value;
                if (lastWoodShedCount < 0)
                {
                    lastWoodShedCount = shedCount;
                }
                else if (shedCount != lastWoodShedCount)
                {
                    lastWoodShedCount = shedCount;
                    SendFirewoodPacket(FirewoodOpShedDelivery, shedCount, 0, false, string.Empty);
                }
            }
        }

        private void PollKiljuBucket(float now)
        {
            if (isSuppressingKilju || kiljuBucketFsm == null || now < suppressKiljuUntilTime) return;

            float w = kiljuWaterVar != null ? kiljuWaterVar.Value : 0f;
            float s = kiljuSugarVar != null ? kiljuSugarVar.Value : 0f;
            float y = kiljuYeastVar != null ? kiljuYeastVar.Value : 0f;
            float f = kiljuFermentVar != null ? kiljuFermentVar.Value : 0f;
            float a = kiljuAlcoholVar != null ? kiljuAlcoholVar.Value : 0f;
            bool lid = kiljuLidOpenVar != null ? kiljuLidOpenVar.Value : lastKiljuLidOpen;
            bool ready = kiljuReadyVar != null && kiljuReadyVar.Value;
            bool vin = kiljuVinegarVar != null && kiljuVinegarVar.Value;
            int bot = kiljuBottlesVar != null ? kiljuBottlesVar.Value : 0;

            bool changed = Mathf.Abs(w - lastKiljuWater) >= 0.5f ||
                           Mathf.Abs(s - lastKiljuSugar) >= 0.5f ||
                           Mathf.Abs(y - lastKiljuYeast) >= 0.5f ||
                           Mathf.Abs(f - lastKiljuFerment) >= 2.0f ||
                           Mathf.Abs(a - lastKiljuAlcohol) >= 0.5f ||
                           lid != lastKiljuLidOpen ||
                           ready != lastKiljuReady ||
                           vin != lastKiljuVinegar ||
                           bot != lastKiljuBottles;

            if (changed)
            {
                lastKiljuWater = w;
                lastKiljuSugar = s;
                lastKiljuYeast = y;
                lastKiljuFerment = f;
                lastKiljuAlcohol = a;
                lastKiljuLidOpen = lid;
                lastKiljuReady = ready;
                lastKiljuVinegar = vin;
                lastKiljuBottles = bot;
                SendKiljuBucketPacket(KiljuOpBucketState);
            }
        }

        private void PollStrawberryAndCombine(float now)
        {
            if (!isSuppressingStrawberry)
            {
                if (!strawberryHooked && strawberryPickedVar != null)
                {
                    int picked = strawberryPickedVar.Value;
                    int boxes = strawberryBoxesVar != null ? strawberryBoxesVar.Value : Math.Max(0, lastStrawberryBoxes);
                    if (picked != lastStrawberryPicked || boxes != lastStrawberryBoxes)
                    {
                        lastStrawberryPicked = picked;
                        lastStrawberryBoxes = boxes;
                        SendStrawberryPacket(FarmOpStrawberryPick, 0, picked, boxes, 0f, false);
                    }
                }

                for (int i = 0; i < strawberryBoxes.Count; i++)
                {
                    StrawberryBoxEntry box = strawberryBoxes[i];
                    if (box == null) continue;
                    int cnt = box.CountIntVar != null ? box.CountIntVar.Value : 0;
                    float fill = box.FillFloatVar != null ? box.FillFloatVar.Value : 0f;
                    bool full = box.FullBoolVar != null && box.FullBoolVar.Value;

                    if (cnt != box.LastCount || Mathf.Abs(fill - box.LastFill) >= 0.1f || full != box.LastFull)
                    {
                        box.LastCount = cnt;
                        box.LastFill = fill;
                        box.LastFull = full;
                        SendStrawberryPacket(FarmOpStrawberryBox, box.Hash, cnt, 0, fill, full);
                    }
                }
            }

            if (!isSuppressingCombine && combineRoot != null && now >= suppressCombineUntilTime)
            {
                bool threshing = combineThreshingVar != null
                    ? combineThreshingVar.Value
                    : (combineNormalizeFsm != null && combineNormalizeFsm.ActiveStateName == "Thresh");
                float header = combineHeaderVar != null ? combineHeaderVar.Value : 0f;
                float grain = combineGrainLevelVar != null ? combineGrainLevelVar.Value : 0f;
                bool unload = combineUnloadOpenVar != null && combineUnloadOpenVar.Value;

                if (threshing != lastCombineThreshing ||
                    Mathf.Abs(header - lastCombineHeader) >= 0.08f ||
                    Mathf.Abs(grain - lastCombineGrain) >= 1.5f ||
                    unload != lastCombineUnloadOpen)
                {
                    lastCombineThreshing = threshing;
                    lastCombineHeader = header;
                    lastCombineGrain = grain;
                    lastCombineUnloadOpen = unload;
                    SendCombinePacket();
                }
            }
        }

        #endregion

        #region Local Callbacks & Sending

        private void OnLocalSpillChanged(bool open)
        {
            lastGifuSpillOpen = open;
            SendSewageTruckPacket(TruckOpSpillValve, lastGifuTankLevel, open, 0f);
        }

        private void OnLocalLogCreated()
        {
            if (logwallCurrentLogVar == null || logwallCurrentLogVar.Value == null) return;
            GameObject logObj = logwallCurrentLogVar.Value;

            deterministicLogCounter++;
            int logNetId = deterministicLogCounter * 100;
            int childNetId = logNetId + 1;
            HookLogChild(logObj, childNetId);

            SendFirewoodPacket(FirewoodOpCreateLog, logNetId, childNetId, false, string.Empty);
        }

        private void HookLogChild(GameObject logObj, int childNetId)
        {
            if (logObj == null || logObj.transform.childCount == 0) return;
            Transform child = logObj.transform.GetChild(0);
            if (child == null) return;

            FixedJoint joint = child.GetComponent<FixedJoint>();
            if (joint != null)
            {
                activeLogJoints[childNetId] = joint;
            }

            PlayMakerFSM childFsm = child.GetComponent<PlayMakerFSM>();
            if (childFsm != null)
            {
                childFsm.InitializeSafe();
                activeLogChildFsms[childNetId] = childFsm;
                string cutState = childFsm.HasState("State 2") ? "State 2" : (childFsm.HasState("Cut") ? "Cut" : (childFsm.HasState("Split") ? "Split" : null));
                if (cutState != null && !hookedFsmIds.Contains(childFsm.GetInstanceID()))
                {
                    hookedFsmIds.Add(childFsm.GetInstanceID());
                    int capturedId = childNetId;
                    childFsm.InsertAction(cutState, () =>
                    {
                        if (isSuppressingLog) return;
                        SendFirewoodPacket(FirewoodOpChopLog, capturedId, -1, true, cutState);
                    }, 0, true);
                }
            }
        }

        private void SendSewageWellPacket(int wellHash, byte wellIdx, byte op, float level, bool flag)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.SewageWellState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteByte(wellIdx);
                p.WriteInt32(wellHash);
                p.WriteSingle(level);
                p.WriteBoolean(flag);
                ctx.Transport.Send(p);
            }
        }

        private void SendSewageTruckPacket(byte op, float level, bool valveOpen, float fineAmount)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.SewageTruckLevel);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteSingle(level);
                p.WriteBoolean(valveOpen);
                p.WriteSingle(fineAmount);
                ctx.Transport.Send(p);
            }
        }

        private void SendFirewoodPacket(byte op, int param1, int param2, bool flag, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.FirewoodAndSplitter);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteInt32(param1);
                p.WriteInt32(param2);
                p.WriteBoolean(flag);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendKiljuBucketPacket(byte op)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.KiljuBrewState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteSingle(lastKiljuWater);
                p.WriteSingle(lastKiljuSugar);
                p.WriteSingle(lastKiljuYeast);
                p.WriteSingle(lastKiljuFerment);
                p.WriteSingle(lastKiljuAlcohol);
                p.WriteBoolean(lastKiljuLidOpen);
                p.WriteBoolean(lastKiljuReady);
                p.WriteBoolean(lastKiljuVinegar);
                p.WriteInt32(lastKiljuBottles);
                ctx.Transport.Send(p);
            }
        }

        private void SendKiljuTastePacket(int bottlesSold, bool tastePassed)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.KiljuBrewState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(KiljuOpJokkeTasteOrSell);
                p.WriteInt32(bottlesSold);
                p.WriteBoolean(tastePassed);
                ctx.Transport.Send(p);
            }
        }

        private void SendStrawberryPacket(byte op, int targetHash, int int1, int int2, float float1, bool flag)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.StrawberryOrCombine);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteInt32(targetHash);
                p.WriteInt32(int1);
                p.WriteInt32(int2);
                p.WriteSingle(float1);
                p.WriteBoolean(flag);
                ctx.Transport.Send(p);
            }
        }

        private void SendCombinePacket()
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.StrawberryOrCombine);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(FarmOpCombineState);
                p.WriteBoolean(lastCombineThreshing);
                p.WriteSingle(lastCombineHeader);
                p.WriteSingle(lastCombineGrain);
                p.WriteBoolean(lastCombineUnloadOpen);
                ctx.Transport.Send(p);
            }
        }

        private void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.JobsSync))
            {
                p.WriteByte(EconomyProtocol.JobsSnapshot);
                p.WriteInt32(LocalPlayerId);

                // 1. Септики
                p.WriteByte((byte)Math.Min(septicWells.Count, 255));
                for (int i = 0; i < septicWells.Count && i < 255; i++)
                {
                    SepticWellEntry w = septicWells[i];
                    float lvl = w.LevelVar != null ? w.LevelVar.Value : Math.Max(0f, w.LastLevel);
                    bool lidOpen = w.LidFsm != null ? ReadLidOpen(w.LidFsm, w.LidOpenVar) : w.LastLidOpen;
                    bool paid = w.PaidVar != null ? w.PaidVar.Value : w.LastPaid;
                    p.WriteInt32(w.Hash);
                    p.WriteByte(w.Index);
                    p.WriteSingle(lvl);
                    p.WriteBoolean(lidOpen);
                    p.WriteBoolean(paid);
                }

                // 2. Цистерна Gifu
                float tankLvl = gifuTankLevelVar != null ? gifuTankLevelVar.Value : Math.Max(0f, lastGifuTankLevel);
                bool spillOpen = gifuSpillValveFsm != null ? ReadSpillOpen(gifuSpillValveFsm, gifuSpillOpenVar) : lastGifuSpillOpen;
                p.WriteSingle(tankLvl);
                p.WriteBoolean(spillOpen);

                // 3. Дрова
                p.WriteInt32(deterministicLogCounter);
                p.WriteBoolean(lastSplitterRunning);
                p.WriteString(lastSplitterState ?? string.Empty);
                p.WriteInt32(flatbedWoodFsm != null ? ReadFlatbedWoodCount() : Math.Max(0, lastFlatbedWoodCount));
                p.WriteInt32(woodShedCountVar != null ? woodShedCountVar.Value : Math.Max(0, lastWoodShedCount));

                // 4. Брага (Kilju)
                p.WriteSingle(kiljuWaterVar != null ? kiljuWaterVar.Value : Math.Max(0f, lastKiljuWater));
                p.WriteSingle(kiljuSugarVar != null ? kiljuSugarVar.Value : Math.Max(0f, lastKiljuSugar));
                p.WriteSingle(kiljuYeastVar != null ? kiljuYeastVar.Value : Math.Max(0f, lastKiljuYeast));
                p.WriteSingle(kiljuFermentVar != null ? kiljuFermentVar.Value : Math.Max(0f, lastKiljuFerment));
                p.WriteSingle(kiljuAlcoholVar != null ? kiljuAlcoholVar.Value : Math.Max(0f, lastKiljuAlcohol));
                p.WriteBoolean(kiljuLidOpenVar != null ? kiljuLidOpenVar.Value : lastKiljuLidOpen);
                p.WriteBoolean(kiljuReadyVar != null ? kiljuReadyVar.Value : lastKiljuReady);
                p.WriteBoolean(kiljuVinegarVar != null ? kiljuVinegarVar.Value : lastKiljuVinegar);
                p.WriteInt32(kiljuBottlesVar != null ? kiljuBottlesVar.Value : Math.Max(0, lastKiljuBottles));
                p.WriteInt32(lastKiljuBottlesSold);

                // 5. Клубника и комбайн
                p.WriteInt32(strawberryPickedVar != null ? strawberryPickedVar.Value : Math.Max(0, lastStrawberryPicked));
                p.WriteInt32(strawberryBoxesVar != null ? strawberryBoxesVar.Value : Math.Max(0, lastStrawberryBoxes));
                p.WriteByte((byte)Math.Min(strawberryBoxes.Count, 255));
                for (int i = 0; i < strawberryBoxes.Count && i < 255; i++)
                {
                    StrawberryBoxEntry b = strawberryBoxes[i];
                    p.WriteInt32(b.Hash);
                    p.WriteInt32(b.CountIntVar != null ? b.CountIntVar.Value : b.LastCount);
                    p.WriteSingle(b.FillFloatVar != null ? b.FillFloatVar.Value : b.LastFill);
                    p.WriteBoolean(b.FullBoolVar != null ? b.FullBoolVar.Value : b.LastFull);
                }

                p.WriteBoolean(lastCombineThreshing);
                p.WriteSingle(combineHeaderVar != null ? combineHeaderVar.Value : Math.Max(0f, lastCombineHeader));
                p.WriteSingle(combineGrainLevelVar != null ? combineGrainLevelVar.Value : Math.Max(0f, lastCombineGrain));
                p.WriteBoolean(combineUnloadOpenVar != null ? combineUnloadOpenVar.Value : lastCombineUnloadOpen);

                ctx.Transport.SendTo(p, clientId);
            }
        }

        #endregion

        #region Packet Handling

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 5) return;

            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case EconomyProtocol.SewageWellState:
                    HandleSewageWellPacket(reader);
                    break;

                case EconomyProtocol.SewageTruckLevel:
                    HandleSewageTruckPacket(reader);
                    break;

                case EconomyProtocol.FirewoodAndSplitter:
                    HandleFirewoodPacket(reader);
                    break;

                case EconomyProtocol.KiljuBrewState:
                    HandleKiljuPacket(reader);
                    break;

                case EconomyProtocol.StrawberryOrCombine:
                    HandleStrawberryOrCombinePacket(reader);
                    break;

                case EconomyProtocol.JobsSnapshot:
                    HandleJobsSnapshot(reader);
                    break;
            }
        }

        private void HandleSewageWellPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 11) return;
            byte op = reader.ReadByte();
            byte wellIdx = reader.ReadByte();
            int wellHash = reader.ReadInt32();
            float level = reader.ReadSingle();
            bool flag = reader.ReadBoolean();

            ApplySepticWell(wellHash, wellIdx, op, level, flag);
        }

        private void ApplySepticWell(int wellHash, byte wellIdx, byte op, float level, bool flag)
        {
            SepticWellEntry well = FindWell(wellHash, wellIdx);
            if (well == null) return;

            isSuppressingSewage = true;
            well.SuppressUntilTime = Time.time + 0.6f;
            try
            {
                if (op == SewageOpLevel || op == SewageOpPaid)
                {
                    if (well.LevelVar != null)
                    {
                        well.LevelVar.Value = level;
                    }
                    well.LastLevel = level;
                    if (well.EmptyVar != null && level <= 0.05f)
                    {
                        well.EmptyVar.Value = true;
                    }
                }

                if (op == SewageOpLid)
                {
                    well.LastLidOpen = flag;
                    if (well.LidOpenVar != null)
                    {
                        well.LidOpenVar.Value = flag;
                    }
                    if (well.LidFsm != null && well.LidHooked)
                    {
                        well.LidFsm.SendEvent(flag ? "MP_OPEN" : "MP_CLOSE");
                    }
                }
                else if (op == SewageOpPaid)
                {
                    well.LastPaid = flag;
                    if (well.PaidVar != null)
                    {
                        well.PaidVar.Value = flag;
                    }
                }
            }
            finally
            {
                isSuppressingSewage = false;
            }
        }

        private void HandleSewageTruckPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 10) return;
            byte op = reader.ReadByte();
            float level = reader.ReadSingle();
            bool valveOpen = reader.ReadBoolean();
            float fineAmount = reader.ReadSingle();

            ApplyGifuTruckState(op, level, valveOpen, fineAmount, triggerActions: true);
        }

        private void ApplyGifuTruckState(byte op, float level, bool valveOpen, float fineAmount, bool triggerActions)
        {
            isSuppressingSewage = true;
            suppressGifuUntilTime = Time.time + 0.6f;
            try
            {
                if (gifuTankLevelVar != null)
                {
                    gifuTankLevelVar.Value = level;
                }
                lastGifuTankLevel = level;

                if (op == TruckOpSpillValve || op == TruckOpIllegalForestFine || !triggerActions)
                {
                    lastGifuSpillOpen = valveOpen;
                    if (gifuSpillOpenVar != null)
                    {
                        gifuSpillOpenVar.Value = valveOpen;
                    }
                    if (triggerActions && gifuSpillValveFsm != null && gifuSpillHooked)
                    {
                        gifuSpillValveFsm.SendEvent(valveOpen ? "MP_OPEN" : "MP_CLOSE");
                    }
                }

                if (triggerActions && op == TruckOpTreatmentDump)
                {
                    lastDumpEventTime = Time.time;
                    if (treatmentPlantFsm != null && treatmentHooked)
                    {
                        treatmentPlantFsm.SendEvent("MP_DUMP");
                    }
                    ctx?.Log?.Invoke("[Ассенизатор] Напарник слил отходы на очистной станции.");
                }
                else if (triggerActions && op == TruckOpIllegalForestFine)
                {
                    if (playerFinesVar != null && fineAmount > playerFinesVar.Value)
                    {
                        playerFinesVar.Value = fineAmount;
                        lastKnownFines = fineAmount;
                    }
                    ctx?.Log?.Invoke("[Ассенизатор] Штраф за нелегальный слив отходов в лесу!");
                }
            }
            finally
            {
                isSuppressingSewage = false;
            }
        }

        private void HandleFirewoodPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 11) return;
            byte op = reader.ReadByte();
            int p1 = reader.ReadInt32();
            int p2 = reader.ReadInt32();
            bool flag = reader.ReadBoolean();
            string stateName = reader.ReadString();

            switch (op)
            {
                case FirewoodOpCreateLog:
                    if (logwallFsm != null && logwallHooked)
                    {
                        isSuppressingLog = true;
                        try
                        {
                            logwallFsm.SendEvent("MP_SPAWN");
                            if (logwallCurrentLogVar != null && logwallCurrentLogVar.Value != null)
                            {
                                HookLogChild(logwallCurrentLogVar.Value, p2);
                            }
                        }
                        finally
                        {
                            isSuppressingLog = false;
                        }
                    }
                    deterministicLogCounter = Math.Max(deterministicLogCounter, p1 / 100);
                    break;

                case FirewoodOpChopLog:
                    isSuppressingLog = true;
                    try
                    {
                        if (activeLogJoints.TryGetValue(p1, out FixedJoint joint) && joint != null)
                        {
                            UnityEngine.Object.Destroy(joint);
                            activeLogJoints.Remove(p1);
                        }
                        if (activeLogChildFsms.TryGetValue(p1, out PlayMakerFSM childFsm) && childFsm != null)
                        {
                            if (!string.IsNullOrEmpty(stateName) && childFsm.HasState(stateName))
                            {
                                string evName = "MP_CHOP_" + stateName;
                                childFsm.AddSafeGlobalTransition(evName, stateName);
                                childFsm.SendEvent(evName);
                            }
                            activeLogChildFsms.Remove(p1);
                        }
                    }
                    finally
                    {
                        isSuppressingLog = false;
                    }
                    break;

                case FirewoodOpSplitter:
                    ApplySplitterState(flag, stateName);
                    break;

                case FirewoodOpFlatbedLoad:
                    ApplyFlatbedWoodLoad(p1);
                    break;

                case FirewoodOpShedDelivery:
                    ApplyWoodShedDelivery(p1, flag);
                    break;
            }
        }

        private void ApplySplitterState(bool running, string stateName)
        {
            isSuppressingSplitter = true;
            try
            {
                lastSplitterRunning = running;
                lastSplitterState = stateName ?? string.Empty;
                if (splitterRunningVar != null) splitterRunningVar.Value = running;

                if (splitterFsm != null && splitterHooked)
                {
                    if (stateName == "Split" && splitterFsm.HasState("Split"))
                    {
                        splitterFsm.SendEvent("MP_SPLIT");
                    }
                    else
                    {
                        splitterFsm.SendEvent(running ? "MP_ON" : "MP_OFF");
                    }
                }
            }
            finally
            {
                isSuppressingSplitter = false;
            }
        }

        private void ApplyFlatbedWoodLoad(int woodCount)
        {
            lastFlatbedWoodCount = woodCount;
            if (flatbedWoodCountInt != null) flatbedWoodCountInt.Value = woodCount;
            if (flatbedWoodLevelFloat != null) flatbedWoodLevelFloat.Value = woodCount;
        }

        private void ApplyWoodShedDelivery(int count, bool triggerPay)
        {
            isSuppressingWoodShed = true;
            try
            {
                lastWoodShedCount = count;
                if (woodShedCountVar != null) woodShedCountVar.Value = count;
                if (triggerPay && woodShedFsm != null && woodShedHooked)
                {
                    woodShedFsm.SendEvent("MP_PAY");
                    ctx?.Log?.Invoke($"[Дрова] Напарник сдал дрова покупателю (всего: {count}).");
                }
            }
            finally
            {
                isSuppressingWoodShed = false;
            }
        }

        private void HandleKiljuPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;
            byte op = reader.ReadByte();

            if (op == KiljuOpJokkeTasteOrSell)
            {
                if (reader.UnreadBytes < 5) return;
                int sold = reader.ReadInt32();
                bool passed = reader.ReadBoolean();
                lastKiljuBottlesSold = sold;

                isSuppressingKilju = true;
                try
                {
                    if (jokkeBuyerFsm != null && jokkeBuyerHooked)
                    {
                        jokkeBuyerFsm.SendEvent("MP_TASTE");
                    }
                    if (passed)
                    {
                        ctx?.Log?.Invoke($"[Брага] Йокке продегустировал/купил килью (бутылок: {sold}).");
                    }
                }
                finally
                {
                    isSuppressingKilju = false;
                }
                return;
            }

            if (reader.UnreadBytes < 27) return;
            float w = reader.ReadSingle();
            float s = reader.ReadSingle();
            float y = reader.ReadSingle();
            float f = reader.ReadSingle();
            float a = reader.ReadSingle();
            bool lid = reader.ReadBoolean();
            bool ready = reader.ReadBoolean();
            bool vin = reader.ReadBoolean();
            int bot = reader.ReadInt32();

            ApplyKiljuBucketState(op == KiljuOpBottleFill, w, s, y, f, a, lid, ready, vin, bot);
        }

        private void ApplyKiljuBucketState(bool triggerBottle, float w, float s, float y, float f, float a, bool lid, bool ready, bool vin, int bot)
        {
            isSuppressingKilju = true;
            suppressKiljuUntilTime = Time.time + 0.6f;
            try
            {
                lastKiljuWater = w;
                lastKiljuSugar = s;
                lastKiljuYeast = y;
                lastKiljuFerment = f;
                lastKiljuAlcohol = a;
                lastKiljuLidOpen = lid;
                lastKiljuReady = ready;
                lastKiljuVinegar = vin;
                lastKiljuBottles = bot;

                if (kiljuWaterVar != null) kiljuWaterVar.Value = w;
                if (kiljuSugarVar != null) kiljuSugarVar.Value = s;
                if (kiljuYeastVar != null) kiljuYeastVar.Value = y;
                if (kiljuFermentVar != null) kiljuFermentVar.Value = f;
                if (kiljuAlcoholVar != null) kiljuAlcoholVar.Value = a;
                if (kiljuLidOpenVar != null) kiljuLidOpenVar.Value = lid;
                if (kiljuReadyVar != null) kiljuReadyVar.Value = ready;
                if (kiljuVinegarVar != null) kiljuVinegarVar.Value = vin;
                if (kiljuBottlesVar != null) kiljuBottlesVar.Value = bot;

                if (triggerBottle && kiljuBucketFsm != null && kiljuBucketHooked)
                {
                    kiljuBucketFsm.SendEvent("MP_BOTTLE");
                }
            }
            finally
            {
                isSuppressingKilju = false;
            }
        }

        private void HandleStrawberryOrCombinePacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;
            byte op = reader.ReadByte();

            if (op == FarmOpCombineState)
            {
                if (reader.UnreadBytes < 10) return;
                bool threshing = reader.ReadBoolean();
                float header = reader.ReadSingle();
                float grain = reader.ReadSingle();
                bool unload = reader.ReadBoolean();
                ApplyCombineState(threshing, header, grain, unload);
                return;
            }

            if (reader.UnreadBytes < 17) return;
            int hash = reader.ReadInt32();
            int int1 = reader.ReadInt32();
            int int2 = reader.ReadInt32();
            float float1 = reader.ReadSingle();
            bool flag = reader.ReadBoolean();

            isSuppressingStrawberry = true;
            try
            {
                if (op == FarmOpStrawberryPick)
                {
                    lastStrawberryPicked = int1;
                    lastStrawberryBoxes = int2;
                    if (strawberryPickedVar != null) strawberryPickedVar.Value = int1;
                    if (strawberryBoxesVar != null) strawberryBoxesVar.Value = int2;
                }
                else if (op == FarmOpStrawberryBox && strawberryBoxesByHash.TryGetValue(hash, out StrawberryBoxEntry box) && box != null)
                {
                    box.LastCount = int1;
                    box.LastFill = float1;
                    box.LastFull = flag;
                    if (box.CountIntVar != null) box.CountIntVar.Value = int1;
                    if (box.FillFloatVar != null) box.FillFloatVar.Value = float1;
                    if (box.FullBoolVar != null) box.FullBoolVar.Value = flag;
                }
            }
            finally
            {
                isSuppressingStrawberry = false;
            }
        }

        private void ApplyCombineState(bool threshing, float header, float grain, bool unload)
        {
            isSuppressingCombine = true;
            suppressCombineUntilTime = Time.time + 0.6f;
            try
            {
                bool wasThreshing = lastCombineThreshing;
                lastCombineThreshing = threshing;
                lastCombineHeader = header;
                lastCombineGrain = grain;
                lastCombineUnloadOpen = unload;

                if (combineThreshingVar != null) combineThreshingVar.Value = threshing;
                if (combineHeaderVar != null) combineHeaderVar.Value = header;
                if (combineGrainLevelVar != null) combineGrainLevelVar.Value = grain;
                if (combineUnloadOpenVar != null) combineUnloadOpenVar.Value = unload;

                if (combineNormalizeFsm != null && combineHooked && threshing != wasThreshing)
                {
                    combineNormalizeFsm.SendEvent(threshing ? "MP_THRESH" : "MP_STOP");
                }
            }
            finally
            {
                isSuppressingCombine = false;
            }
        }

        private void HandleJobsSnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;

            // 1. Септики
            byte wellCount = reader.ReadByte();
            for (int i = 0; i < wellCount; i++)
            {
                if (reader.UnreadBytes < 11) return;
                int wHash = reader.ReadInt32();
                byte wIdx = reader.ReadByte();
                float wLevel = reader.ReadSingle();
                bool wLid = reader.ReadBoolean();
                bool wPaid = reader.ReadBoolean();
                ApplySepticWell(wHash, wIdx, SewageOpLevel, wLevel, wLid);
                ApplySepticWell(wHash, wIdx, SewageOpLid, wLevel, wLid);
                if (wPaid) ApplySepticWell(wHash, wIdx, SewageOpPaid, wLevel, true);
            }

            if (reader.UnreadBytes < 19) return;

            // 2. Цистерна Gifu
            float tankLvl = reader.ReadSingle();
            bool spillOpen = reader.ReadBoolean();
            ApplyGifuTruckState(TruckOpLevel, tankLvl, spillOpen, 0f, triggerActions: false);

            // 3. Дрова
            int logCounter = reader.ReadInt32();
            bool splitRun = reader.ReadBoolean();
            string splitState = reader.ReadString();
            if (reader.UnreadBytes < 8) return;
            int flatbedLoad = reader.ReadInt32();
            int shedCount = reader.ReadInt32();
            deterministicLogCounter = Math.Max(deterministicLogCounter, logCounter);
            ApplySplitterState(splitRun, splitState);
            ApplyFlatbedWoodLoad(flatbedLoad);
            ApplyWoodShedDelivery(shedCount, triggerPay: false);

            if (reader.UnreadBytes < 31) return;

            // 4. Брага
            float kw = reader.ReadSingle();
            float ks = reader.ReadSingle();
            float ky = reader.ReadSingle();
            float kf = reader.ReadSingle();
            float ka = reader.ReadSingle();
            bool klid = reader.ReadBoolean();
            bool kready = reader.ReadBoolean();
            bool kvin = reader.ReadBoolean();
            int kbot = reader.ReadInt32();
            int ksold = reader.ReadInt32();
            lastKiljuBottlesSold = ksold;
            ApplyKiljuBucketState(false, kw, ks, ky, kf, ka, klid, kready, kvin, kbot);

            if (reader.UnreadBytes < 9) return;

            // 5. Клубника и комбайн
            int picked = reader.ReadInt32();
            int boxes = reader.ReadInt32();
            byte boxListCount = reader.ReadByte();
            isSuppressingStrawberry = true;
            try
            {
                lastStrawberryPicked = picked;
                lastStrawberryBoxes = boxes;
                if (strawberryPickedVar != null) strawberryPickedVar.Value = picked;
                if (strawberryBoxesVar != null) strawberryBoxesVar.Value = boxes;

                for (int i = 0; i < boxListCount; i++)
                {
                    if (reader.UnreadBytes < 13) break;
                    int bHash = reader.ReadInt32();
                    int bCnt = reader.ReadInt32();
                    float bFill = reader.ReadSingle();
                    bool bFull = reader.ReadBoolean();
                    if (strawberryBoxesByHash.TryGetValue(bHash, out StrawberryBoxEntry box) && box != null)
                    {
                        box.LastCount = bCnt;
                        box.LastFill = bFill;
                        box.LastFull = bFull;
                        if (box.CountIntVar != null) box.CountIntVar.Value = bCnt;
                        if (box.FillFloatVar != null) box.FillFloatVar.Value = bFill;
                        if (box.FullBoolVar != null) box.FullBoolVar.Value = bFull;
                    }
                }
            }
            finally
            {
                isSuppressingStrawberry = false;
            }

            if (reader.UnreadBytes >= 10)
            {
                bool threshing = reader.ReadBoolean();
                float header = reader.ReadSingle();
                float grain = reader.ReadSingle();
                bool unload = reader.ReadBoolean();
                ApplyCombineState(threshing, header, grain, unload);
            }
        }

        #endregion

        #region Helpers

        private SepticWellEntry FindWell(int wellHash, byte wellIdx)
        {
            if (wellHash != 0 && wellsByHash.TryGetValue(wellHash, out SepticWellEntry byHash))
            {
                return byHash;
            }
            for (int i = 0; i < septicWells.Count; i++)
            {
                if (septicWells[i].Index == wellIdx) return septicWells[i];
            }
            return null;
        }

        private bool IsNearTreatmentPlant()
        {
            if (treatmentPlantTr == null || gifuRoot == null) return false;
            return (gifuRoot.transform.position - treatmentPlantTr.position).sqrMagnitude <= 2500f;
        }

        private int ReadFlatbedWoodCount()
        {
            if (flatbedWoodCountInt != null) return flatbedWoodCountInt.Value;
            if (flatbedWoodLevelFloat != null) return Mathf.RoundToInt(flatbedWoodLevelFloat.Value);
            return 0;
        }

        private void SnapshotKiljuLocals()
        {
            if (kiljuWaterVar != null) lastKiljuWater = kiljuWaterVar.Value;
            if (kiljuSugarVar != null) lastKiljuSugar = kiljuSugarVar.Value;
            if (kiljuYeastVar != null) lastKiljuYeast = kiljuYeastVar.Value;
            if (kiljuFermentVar != null) lastKiljuFerment = kiljuFermentVar.Value;
            if (kiljuAlcoholVar != null) lastKiljuAlcohol = kiljuAlcoholVar.Value;
            if (kiljuLidOpenVar != null) lastKiljuLidOpen = kiljuLidOpenVar.Value;
            if (kiljuReadyVar != null) lastKiljuReady = kiljuReadyVar.Value;
            if (kiljuVinegarVar != null) lastKiljuVinegar = kiljuVinegarVar.Value;
            if (kiljuBottlesVar != null) lastKiljuBottles = kiljuBottlesVar.Value;
        }

        private static byte ResolveWellIndex(string name, byte fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= '1' && c <= '5')
                {
                    return (byte)(c - '1');
                }
            }
            return fallback;
        }

        private static bool ReadLidOpen(PlayMakerFSM lidFsm, FsmBool openVar)
        {
            if (openVar != null) return openVar.Value;
            if (lidFsm != null && !string.IsNullOrEmpty(lidFsm.ActiveStateName))
            {
                return ContainsIgnoreCase(lidFsm.ActiveStateName, "Open");
            }
            return false;
        }

        private static bool ReadSpillOpen(PlayMakerFSM spillFsm, FsmBool openVar)
        {
            if (openVar != null) return openVar.Value;
            if (spillFsm != null && !string.IsNullOrEmpty(spillFsm.ActiveStateName))
            {
                string st = spillFsm.ActiveStateName;
                return ContainsIgnoreCase(st, "Open") || ContainsIgnoreCase(st, "Pump");
            }
            return false;
        }

        private static bool ReadSplitterRunning(PlayMakerFSM fsm, FsmBool runningVar)
        {
            if (runningVar != null) return runningVar.Value;
            if (fsm != null && !string.IsNullOrEmpty(fsm.ActiveStateName))
            {
                string st = fsm.ActiveStateName;
                return ContainsIgnoreCase(st, "Split") || ContainsIgnoreCase(st, "On") || ContainsIgnoreCase(st, "Run");
            }
            return false;
        }

        private static FsmFloat FindFirstFloat(PlayMakerFSM fsm, params string[] names)
        {
            if (fsm == null || fsm.FsmVariables == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                FsmFloat v = fsm.FsmVariables.FindFsmFloat(names[i]);
                if (v != null) return v;
            }
            return null;
        }

        private static FsmInt FindFirstInt(PlayMakerFSM fsm, params string[] names)
        {
            if (fsm == null || fsm.FsmVariables == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                FsmInt v = fsm.FsmVariables.FindFsmInt(names[i]);
                if (v != null) return v;
            }
            return null;
        }

        private static FsmBool FindFirstBool(PlayMakerFSM fsm, params string[] names)
        {
            if (fsm == null || fsm.FsmVariables == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                FsmBool v = fsm.FsmVariables.FindFsmBool(names[i]);
                if (v != null) return v;
            }
            return null;
        }

        private static bool ContainsIgnoreCase(string source, string needle)
        {
            return !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(needle) &&
                   source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
