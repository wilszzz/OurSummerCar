using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Satsuma
{
    /// <summary>
    /// Синхронизация тюнинга двигателя/шасси Satsuma (PacketType.SatsumaTuningSync = 97),
    /// жидкостей, механического износа и АКБ (PacketType.SatsumaFluidsAndWearSync = 98).
    /// </summary>
    internal sealed class SatsumaTuningAndFluids
    {
        private enum TuningCategory : byte
        {
            RockerValves = 1,
            Distributor = 2,
            Carburetor = 3,
            AlternatorBelt = 4,
            WheelToe = 5,
            HandbrakeCable = 6
        }

        private enum FluidChannel : byte
        {
            Oil = 1,
            Coolant = 2,
            BrakeFluidF = 3,
            BrakeFluidR = 4,
            ClutchFluid = 5
        }

        private enum WearPartKind : byte
        {
            Headgasket = 1,
            Piston1 = 2,
            Piston2 = 3,
            Piston3 = 4,
            Piston4 = 5,
            Block = 6,
            Starter = 7,
            RockerShaft = 8,
            FanBelt = 9
        }

        private sealed class TrackedTuningParam
        {
            public TuningCategory Category;
            public int KeyHash;
            public string VarName;
            public PlayMakerFSM Fsm;
            public FsmFloat FloatVar;
            public FsmBool BoolVar;
            public bool IsBool;
            public float LastSentFloat;
            public bool LastSentBool;
            public float Tolerance;
            public float SuppressUntilTime;
            public bool Initialized;
        }

        private sealed class TrackedFluid
        {
            public FluidChannel Channel;
            public string Label;
            public readonly List<FsmFloat> Targets = new List<FsmFloat>();
            public float LastSentValue = -999f;
            public float LastSyncTime;
            public float SuppressUntilTime;
        }

        private sealed class TrackedWearPart
        {
            public WearPartKind Kind;
            public string Label;
            public GameObject PartObject;
            public PlayMakerFSM StateFsm;
            public PlayMakerFSM WearFsm;
            public FsmFloat WearVar;
            public FsmBool BrokenVar;
            public float LastSentWear = -999f;
            public bool LastSentBroken;
            public float LastSyncTime;
            public float SuppressUntilTime;
            public bool Initialized;
        }

        private sealed class TrackedBattery
        {
            public int KeyHash;
            public GameObject TargetObject;
            public PlayMakerFSM Fsm;
            public FsmFloat ChargeVar;
            public FsmFloat LevelVar;
            public FsmBool DeadVar;
            public float LastSentCharge = -999f;
            public float LastSentLevel = -999f;
            public bool LastSentDead;
            public float LastSyncTime;
            public float SuppressUntilTime;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly Dictionary<int, TrackedTuningParam> tuningByKey = new Dictionary<int, TrackedTuningParam>();
        private readonly List<TrackedTuningParam> tuningList = new List<TrackedTuningParam>();

        private readonly Dictionary<FluidChannel, TrackedFluid> fluidsByChannel = new Dictionary<FluidChannel, TrackedFluid>();
        private GameObject steamRadiatorObj;
        private bool lastSentBoiling;

        private readonly Dictionary<WearPartKind, TrackedWearPart> wearByKind = new Dictionary<WearPartKind, TrackedWearPart>();
        private readonly List<TrackedWearPart> wearList = new List<TrackedWearPart>();

        private readonly Dictionary<int, TrackedBattery> batteriesByHash = new Dictionary<int, TrackedBattery>();
        private readonly List<TrackedBattery> batteryList = new List<TrackedBattery>();

        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        private GameObject satsumaRoot;
        private GameObject databaseRoot;

        private float nextScanTime;
        private float nextTuningPollTime;
        private float nextFluidsPollTime;
        private float nextWearPollTime;
        private float nextBatteryPollTime;

        private bool isSuppressingTuning;
        private bool isSuppressingFluids;
        private bool isSuppressingWear;
        private bool isSuppressingBattery;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Тюнинг: {tuningList.Count}, Жидкости: {CountBoundFluids()}/5, Износ: {wearList.Count}, АКБ: {batteryList.Count}";

        public SatsumaTuningAndFluids(OscHostContext context, string moduleId)
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
            tuningByKey.Clear();
            tuningList.Clear();
            fluidsByChannel.Clear();
            steamRadiatorObj = null;
            lastSentBoiling = false;
            wearByKind.Clear();
            wearList.Clear();
            batteriesByHash.Clear();
            batteryList.Clear();
            pendingSnapshots.Clear();

            satsumaRoot = null;
            databaseRoot = null;
            nextScanTime = 0f;
            nextTuningPollTime = 0f;
            nextFluidsPollTime = 0f;
            nextWearPollTime = 0f;
            nextBatteryPollTime = 0f;

            isSuppressingTuning = false;
            isSuppressingFluids = false;
            isSuppressingWear = false;
            isSuppressingBattery = false;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            if (now >= nextScanTime)
            {
                nextScanTime = now + 5.0f;
                ScanAll();
            }

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextTuningPollTime)
            {
                nextTuningPollTime = now + 0.25f;
                PollTuningDeltas(now);
            }

            if (now >= nextFluidsPollTime)
            {
                nextFluidsPollTime = now + 0.25f;
                PollFluids(now);
            }

            if (now >= nextWearPollTime)
            {
                nextWearPollTime = now + 0.35f;
                PollWearStates(now);
            }

            if (now >= nextBatteryPollTime)
            {
                nextBatteryPollTime = now + 0.35f;
                PollBatteries(now);
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanAll();
            SendFullSnapshotToClient(clientId);

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
                    int clientId = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    ScanAll();
                    SendFullSnapshotToClient(clientId);
                }
            }
        }

        private void SendFullSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            SendTuningSnapshot(clientId);
            SendFluidsSnapshot(clientId);
            SendWearSnapshot(clientId);
            SendBatterySnapshot(clientId);
        }

        #region Discovery / Scanning

        private void ScanAll()
        {
            try
            {
                if (satsumaRoot == null)
                {
                    satsumaRoot = FindSatsumaRoot();
                }
                if (databaseRoot == null)
                {
                    databaseRoot = GameObject.Find("Database");
                }

                ScanTuningTargets();
                ScanFluids();
                ScanWearParts();
                ScanBatteries();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma.Tuning] Ошибка сканирования: {ex.Message}");
            }
        }

        private static GameObject FindSatsumaRoot()
        {
            GameObject go = GameObject.Find("SATSUMA(557kg, 248)")
                         ?? GameObject.Find("SATSUMA(580kg, 87)")
                         ?? GameObject.Find("Satsuma");
            if (go != null) return go;

            GameObject[] allGos = UnityEngine.Object.FindObjectsOfType<GameObject>();
            for (int i = 0; i < allGos.Length; i++)
            {
                if (allGos[i] != null && allGos[i].name.StartsWith("SATSUMA(", StringComparison.Ordinal))
                {
                    return allGos[i];
                }
            }
            return null;
        }

        private static bool ContainsIgnoreCase(string source, string needle)
        {
            return !string.IsNullOrEmpty(source) &&
                   !string.IsNullOrEmpty(needle) &&
                   source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ScanTuningTargets()
        {
            // 1. Клапаны (Rocker shaft)
            ScanNamedObjectsForTuning(
                new[] { "rocker shaft(Clone)", "RockerShaft", "trigger_rocker shaft", "Database/DatabaseMotor/RockerShaft" },
                "rocker",
                TuningCategory.RockerValves,
                new[] { "Cyl1In", "Cyl1Ex", "Cyl2In", "Cyl2Ex", "Cyl3In", "Cyl3Ex", "Cyl4In", "Cyl4Ex", "Valve", "Intake", "Exhaust", "Clearance", "Alignment" },
                null);

            // 2. Трамблёр (Distributor)
            ScanNamedObjectsForTuning(
                new[] { "distributor(Clone)", "Distributor", "trigger_distributor", "Database/DatabaseMotor/Distributor" },
                "distributor",
                TuningCategory.Distributor,
                new[] { "Advance", "Timing", "IgnitionTiming", "SparkTiming", "Angle", "Alignment", "Rotation" },
                null);

            // 3. Карбюратор (Carburetor AFR)
            ScanNamedObjectsForTuning(
                new[]
                {
                    "carburator(Clone)", "carburetor(Clone)", "twin carburators(Clone)",
                    "racing carburators(Clone)", "Carburetor", "Carburator", "Database/DatabaseMotor/Carburator"
                },
                "carbur",
                TuningCategory.Carburetor,
                new[] { "Mixture", "AFR", "AirFuelRatio", "FuelMixture", "Mixture1", "Mixture2", "Mixture3", "Mixture4", "Jet1", "Jet2", "Jet3", "Jet4", "Alignment", "IdleRPM" },
                null);

            // 4. Натяжение и свист ремня генератора (Alternator belt)
            ScanNamedObjectsForTuning(
                new[] { "alternator(Clone)", "Alternator", "FanBelt", "fan belt(Clone)", "_Motor", "Database/DatabaseMotor/Alternator" },
                "alternator",
                TuningCategory.AlternatorBelt,
                new[] { "Tension", "BeltTension", "Squeal", "BeltSqueal", "Tightness", "Alignment", "Slip" },
                new[] { "Squeal", "BeltSqueal", "Slipping" });

            // 5. Сход-развал передних колёс (Wheel Toe / Alignment)
            ScanNamedObjectsForTuning(
                new[]
                {
                    "SteeringRack", "steering rod fl", "steering rod fr", "SteeringRodFL", "SteeringRodFR",
                    "TieRodL", "TieRodR", "WheelFL", "WheelFR", "Database/DatabaseMechanics/Alignment"
                },
                "steering rod",
                TuningCategory.WheelToe,
                new[] { "Toe", "ToeL", "ToeR", "Alignment", "WheelAlignment", "SteerOffset", "Angle", "Length" },
                null);

            // 6. Натяжение троса ручника (Handbrake cable)
            ScanNamedObjectsForTuning(
                new[] { "Handbrake", "handbrake(Clone)", "HandbrakeCable", "Database/DatabaseMechanics/Handbrake" },
                "handbrake",
                TuningCategory.HandbrakeCable,
                new[] { "Tension", "CableTension", "HandbrakeTension", "Alignment", "Tightness", "MaxBrake" },
                null);
        }

        private void ScanNamedObjectsForTuning(
            string[] explicitPaths,
            string subtreeKeyword,
            TuningCategory category,
            string[] floatNames,
            string[] boolNames)
        {
            for (int i = 0; i < explicitPaths.Length; i++)
            {
                GameObject go = GameObject.Find(explicitPaths[i]);
                if (go != null)
                {
                    RegisterTuningVariablesOnObject(go, category, floatNames, boolNames);
                }
            }

            if (!string.IsNullOrEmpty(subtreeKeyword))
            {
                ScanHierarchyForTuning(satsumaRoot, subtreeKeyword, category, floatNames, boolNames);
                ScanHierarchyForTuning(databaseRoot, subtreeKeyword, category, floatNames, boolNames);
            }
        }

        private void ScanHierarchyForTuning(
            GameObject root,
            string keyword,
            TuningCategory category,
            string[] floatNames,
            string[] boolNames)
        {
            if (root == null) return;
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform tr = children[i];
                if (tr != null && ContainsIgnoreCase(tr.name, keyword))
                {
                    RegisterTuningVariablesOnObject(tr.gameObject, category, floatNames, boolNames);
                }
            }
        }

        private void RegisterTuningVariablesOnObject(
            GameObject rootGo,
            TuningCategory category,
            string[] floatNames,
            string[] boolNames)
        {
            if (rootGo == null) return;

            PlayMakerFSM[] fsms = rootGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int f = 0; f < fsms.Length; f++)
            {
                PlayMakerFSM fsm = fsms[f];
                if (fsm == null) continue;

                // Для _Motor берём только FSM ремня/генератора, чтобы не захватить лишние переменные двигателя
                if (rootGo.name == "_Motor" &&
                    fsm.FsmName != "Fan Belt" &&
                    !ContainsIgnoreCase(fsm.FsmName, "Belt") &&
                    !ContainsIgnoreCase(fsm.FsmName, "Alternator"))
                {
                    continue;
                }

                // Не трогаем позицию рычага ручника (она принадлежит Controls/DashboardControlsSync)
                if (category == TuningCategory.HandbrakeCable &&
                    (fsm.gameObject.name == "ParkingBrake" || ContainsIgnoreCase(fsm.FsmName, "Lever")))
                {
                    continue;
                }

                fsm.InitializeSafe();
                string objId = SceneHasher.GetObjectIdentifier(fsm.gameObject);

                if (floatNames != null && fsm.FsmVariables != null && fsm.FsmVariables.FloatVariables != null)
                {
                    FsmFloat[] floats = fsm.FsmVariables.FloatVariables;
                    for (int v = 0; v < floats.Length; v++)
                    {
                        FsmFloat fv = floats[v];
                        if (fv == null || string.IsNullOrEmpty(fv.Name)) continue;
                        if (!MatchesVariableName(fv.Name, floatNames)) continue;

                        int keyHash = SceneHasher.GetStableHash(objId + "|" + fsm.FsmName + "|F|" + fv.Name);
                        if (tuningByKey.ContainsKey(keyHash)) continue;

                        var param = new TrackedTuningParam
                        {
                            Category = category,
                            KeyHash = keyHash,
                            VarName = fv.Name,
                            Fsm = fsm,
                            FloatVar = fv,
                            IsBool = false,
                            LastSentFloat = fv.Value,
                            Tolerance = 0.002f,
                            Initialized = true
                        };
                        tuningByKey[keyHash] = param;
                        tuningList.Add(param);
                    }
                }

                if (boolNames != null && fsm.FsmVariables != null && fsm.FsmVariables.BoolVariables != null)
                {
                    FsmBool[] bools = fsm.FsmVariables.BoolVariables;
                    for (int v = 0; v < bools.Length; v++)
                    {
                        FsmBool bv = bools[v];
                        if (bv == null || string.IsNullOrEmpty(bv.Name)) continue;
                        if (!MatchesVariableName(bv.Name, boolNames)) continue;

                        int keyHash = SceneHasher.GetStableHash(objId + "|" + fsm.FsmName + "|B|" + bv.Name);
                        if (tuningByKey.ContainsKey(keyHash)) continue;

                        var param = new TrackedTuningParam
                        {
                            Category = category,
                            KeyHash = keyHash,
                            VarName = bv.Name,
                            Fsm = fsm,
                            BoolVar = bv,
                            IsBool = true,
                            LastSentBool = bv.Value,
                            Initialized = true
                        };
                        tuningByKey[keyHash] = param;
                        tuningList.Add(param);
                    }
                }
            }
        }

        private static bool MatchesVariableName(string actualName, string[] patterns)
        {
            for (int i = 0; i < patterns.Length; i++)
            {
                string pat = patterns[i];
                if (string.Equals(actualName, pat, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if ((pat == "Valve" || pat == "Intake" || pat == "Exhaust") &&
                    actualName.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private TrackedFluid GetOrCreateFluid(FluidChannel channel, string label)
        {
            if (!fluidsByChannel.TryGetValue(channel, out TrackedFluid fluid))
            {
                fluid = new TrackedFluid
                {
                    Channel = channel,
                    Label = label
                };
                fluidsByChannel[channel] = fluid;
            }
            return fluid;
        }

        private static void AddUniqueFloatTarget(TrackedFluid fluid, FsmFloat fv)
        {
            if (fluid == null || fv == null) return;
            for (int i = 0; i < fluid.Targets.Count; i++)
            {
                if (ReferenceEquals(fluid.Targets[i], fv)) return;
            }
            fluid.Targets.Add(fv);
        }

        private void ScanFluids()
        {
            TrackedFluid oil = GetOrCreateFluid(FluidChannel.Oil, "Oil");
            FindFluidVarsOnNames(new[] { "Oil", "Combustion", "Oilpan", "Database/DatabaseMechanics/Oil" }, new[] { "Oil", "OilLevel" }, oil);

            TrackedFluid coolant = GetOrCreateFluid(FluidChannel.Coolant, "Coolant");
            FindFluidVarsOnNames(
                new[] { "Radiator", "racing radiator(Clone)", "radiator(Clone)", "Racing Radiator", "Cooling" },
                new[] { "Coolant", "Water", "Fluid" },
                coolant);

            if (steamRadiatorObj == null)
            {
                steamRadiatorObj = GameObject.Find("SteamRadiator");
            }

            TrackedFluid brakeF = GetOrCreateFluid(FluidChannel.BrakeFluidF, "BrakeFluidF");
            TrackedFluid brakeR = GetOrCreateFluid(FluidChannel.BrakeFluidR, "BrakeFluidR");

            GameObject brakeMaster = GameObject.Find("BrakeMasterCylinder");
            if (brakeMaster != null)
            {
                PlayMakerFSM[] fsms = brakeMaster.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    if (fsms[i] == null) continue;
                    fsms[i].InitializeSafe();
                    if (fsms[i].FsmVariables == null) continue;

                    FsmFloat bfF = fsms[i].FsmVariables.FindFsmFloat("BrakeFluidF") ?? fsms[i].FsmVariables.FindFsmFloat("FluidF");
                    FsmFloat bfR = fsms[i].FsmVariables.FindFsmFloat("BrakeFluidR") ?? fsms[i].FsmVariables.FindFsmFloat("FluidR");
                    FsmFloat bfCommon = fsms[i].FsmVariables.FindFsmFloat("BrakeFluid");

                    if (bfF != null) AddUniqueFloatTarget(brakeF, bfF);
                    if (bfR != null) AddUniqueFloatTarget(brakeR, bfR);
                    if (bfF == null && bfCommon != null) AddUniqueFloatTarget(brakeF, bfCommon);
                }
            }

            FindFluidVarsOnNames(new[] { "BrakeFluidF", "BrakeMasterCylinderF", "Database/DatabaseMechanics/BrakeFluidF" }, new[] { "BrakeFluidF", "BrakeFluid", "Fluid" }, brakeF);
            FindFluidVarsOnNames(new[] { "BrakeFluidR", "BrakeMasterCylinderR", "Database/DatabaseMechanics/BrakeFluidR" }, new[] { "BrakeFluidR", "BrakeFluid", "Fluid" }, brakeR);

            TrackedFluid clutch = GetOrCreateFluid(FluidChannel.ClutchFluid, "ClutchFluid");
            FindFluidVarsOnNames(new[] { "ClutchMasterCylinder", "ClutchFluid", "Database/DatabaseMechanics/ClutchFluid" }, new[] { "ClutchFluid", "Fluid" }, clutch);
        }

        private static void FindFluidVarsOnNames(string[] goNames, string[] varNames, TrackedFluid target)
        {
            for (int i = 0; i < goNames.Length; i++)
            {
                GameObject go = GameObject.Find(goNames[i]);
                if (go == null) continue;

                PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int f = 0; f < fsms.Length; f++)
                {
                    if (fsms[f] == null) continue;
                    fsms[f].InitializeSafe();
                    if (fsms[f].FsmVariables == null) continue;

                    for (int v = 0; v < varNames.Length; v++)
                    {
                        FsmFloat fv = fsms[f].FsmVariables.FindFsmFloat(varNames[v]);
                        if (fv != null)
                        {
                            AddUniqueFloatTarget(target, fv);
                        }
                    }
                }
            }
        }

        private void ScanWearParts()
        {
            RegisterWearPart(WearPartKind.Headgasket, "Headgasket", new[] { "head gasket(Clone)", "trigger_head gasket", "Database/DatabaseMotor/HeadGasket" });
            RegisterWearPart(WearPartKind.Piston1, "Piston1", new[] { "piston1(Clone)", "trigger_piston1" });
            RegisterWearPart(WearPartKind.Piston2, "Piston2", new[] { "piston2(Clone)", "trigger_piston2" });
            RegisterWearPart(WearPartKind.Piston3, "Piston3", new[] { "piston3(Clone)", "trigger_piston3" });
            RegisterWearPart(WearPartKind.Piston4, "Piston4", new[] { "piston4(Clone)", "trigger_piston4" });
            RegisterWearPart(WearPartKind.Block, "Block", new[] { "block(Clone)", "EngineBlock", "Database/DatabaseMotor/Block" });
            RegisterWearPart(WearPartKind.Starter, "Starter", new[] { "starter(Clone)", "trigger_starter", "Database/DatabaseMotor/Starter" });
            RegisterWearPart(WearPartKind.RockerShaft, "RockerShaft", new[] { "rocker shaft(Clone)", "trigger_rocker shaft", "Database/DatabaseMotor/RockerShaft" });
            RegisterWearPart(WearPartKind.FanBelt, "FanBelt", new[] { "_Motor", "fan belt(Clone)" });
        }

        private void RegisterWearPart(WearPartKind kind, string label, string[] goNames)
        {
            if (wearByKind.TryGetValue(kind, out TrackedWearPart existing) && existing.PartObject != null)
            {
                return;
            }

            GameObject go = null;
            for (int i = 0; i < goNames.Length; i++)
            {
                go = GameObject.Find(goNames[i]);
                if (go != null) break;
            }
            if (go == null) return;

            PlayMakerFSM stateFsm = null;
            PlayMakerFSM wearFsm = null;
            FsmFloat wearVar = null;
            FsmBool brokenVar = null;

            PlayMakerFSM[] fsms = go.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;
                fsm.InitializeSafe();

                if (kind == WearPartKind.FanBelt && go.name == "_Motor")
                {
                    if (fsm.FsmName == "Fan Belt")
                    {
                        stateFsm = fsm;
                        wearFsm = fsm;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    if (stateFsm == null && (fsm.FsmName == "Removal" || fsm.FsmName == "Collision" || fsm.HasState("Death") || fsm.HasState("Break")))
                    {
                        stateFsm = fsm;
                    }
                    if (wearFsm == null && (fsm.FsmName == "Data" || fsm.FsmName == "Wear" || fsm.FsmName == "Condition" || fsm.FsmName == "Use"))
                    {
                        wearFsm = fsm;
                    }
                }

                if (fsm.FsmVariables != null)
                {
                    if (wearVar == null)
                    {
                        wearVar = fsm.FsmVariables.FindFsmFloat("Wear")
                               ?? fsm.FsmVariables.FindFsmFloat("Condition")
                               ?? fsm.FsmVariables.FindFsmFloat("Health")
                               ?? fsm.FsmVariables.FindFsmFloat("Damage");
                        if (wearVar != null && wearFsm == null) wearFsm = fsm;
                    }
                    if (brokenVar == null)
                    {
                        brokenVar = fsm.FsmVariables.FindFsmBool("Broken")
                                 ?? fsm.FsmVariables.FindFsmBool("Dead")
                                 ?? fsm.FsmVariables.FindFsmBool("Damaged");
                    }
                }
            }

            if (stateFsm == null && wearVar == null && brokenVar == null) return;

            if (existing == null)
            {
                existing = new TrackedWearPart
                {
                    Kind = kind,
                    Label = label
                };
                wearByKind[kind] = existing;
                wearList.Add(existing);
            }

            existing.PartObject = go;
            existing.StateFsm = stateFsm;
            existing.WearFsm = wearFsm;
            existing.WearVar = wearVar;
            existing.BrokenVar = brokenVar;

            if (!existing.Initialized)
            {
                existing.LastSentWear = wearVar != null ? wearVar.Value : 0f;
                existing.LastSentBroken = ReadPartBrokenState(existing);
                existing.Initialized = true;
            }
        }

        private void ScanBatteries()
        {
            string[] batteryGoNames =
            {
                "battery(Clone)",
                "racing battery(Clone)",
                "trigger_battery",
                "Database/DatabaseMechanics/Battery",
                "TriggerCharger",
                "Battery"
            };

            for (int i = 0; i < batteryGoNames.Length; i++)
            {
                GameObject go = GameObject.Find(batteryGoNames[i]);
                if (go != null)
                {
                    RegisterBatteryOnObject(go);
                }
            }

            if (satsumaRoot != null)
            {
                Transform[] trs = satsumaRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < trs.Length; i++)
                {
                    if (trs[i] != null && ContainsIgnoreCase(trs[i].name, "battery"))
                    {
                        RegisterBatteryOnObject(trs[i].gameObject);
                    }
                }
            }
        }

        private void RegisterBatteryOnObject(GameObject go)
        {
            if (go == null) return;

            PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.FsmVariables == null) continue;
                fsm.InitializeSafe();

                FsmFloat chargeVar = fsm.FsmVariables.FindFsmFloat("Charge")
                                  ?? fsm.FsmVariables.FindFsmFloat("BatteryCharge")
                                  ?? fsm.FsmVariables.FindFsmFloat("Power")
                                  ?? fsm.FsmVariables.FindFsmFloat("Voltage");
                FsmFloat levelVar = fsm.FsmVariables.FindFsmFloat("Level");
                FsmBool deadVar = fsm.FsmVariables.FindFsmBool("Dead") ?? fsm.FsmVariables.FindFsmBool("Broken");

                if (chargeVar == null && levelVar == null && deadVar == null) continue;

                int hash = SceneHasher.GetStableHash(SceneHasher.GetObjectIdentifier(fsm.gameObject) + "|" + fsm.FsmName + "|Battery");
                if (batteriesByHash.ContainsKey(hash)) continue;

                var entry = new TrackedBattery
                {
                    KeyHash = hash,
                    TargetObject = fsm.gameObject,
                    Fsm = fsm,
                    ChargeVar = chargeVar,
                    LevelVar = levelVar,
                    DeadVar = deadVar,
                    LastSentCharge = chargeVar != null ? chargeVar.Value : -999f,
                    LastSentLevel = levelVar != null ? levelVar.Value : -999f,
                    LastSentDead = deadVar != null && deadVar.Value
                };
                batteriesByHash[hash] = entry;
                batteryList.Add(entry);
            }
        }

        #endregion

        #region Polling & Sending

        private void PollTuningDeltas(float now)
        {
            if (isSuppressingTuning || tuningList.Count == 0) return;

            List<TrackedTuningParam> changed = null;
            for (int i = 0; i < tuningList.Count; i++)
            {
                TrackedTuningParam p = tuningList[i];
                if (p == null || p.Fsm == null || now < p.SuppressUntilTime) continue;

                if (p.IsBool)
                {
                    if (p.BoolVar == null) continue;
                    bool cur = p.BoolVar.Value;
                    if (!p.Initialized)
                    {
                        p.LastSentBool = cur;
                        p.Initialized = true;
                        continue;
                    }
                    if (cur != p.LastSentBool)
                    {
                        p.LastSentBool = cur;
                        if (changed == null) changed = new List<TrackedTuningParam>();
                        changed.Add(p);
                    }
                }
                else
                {
                    if (p.FloatVar == null) continue;
                    float cur = p.FloatVar.Value;
                    if (!p.Initialized)
                    {
                        p.LastSentFloat = cur;
                        p.Initialized = true;
                        continue;
                    }
                    if (Mathf.Abs(cur - p.LastSentFloat) > p.Tolerance)
                    {
                        p.LastSentFloat = cur;
                        if (changed == null) changed = new List<TrackedTuningParam>();
                        changed.Add(p);
                    }
                }
            }

            if (changed != null && changed.Count > 0)
            {
                SendTuningList(SatsumaProtocol.TuningDelta, changed, -1);
            }
        }

        private void SendTuningSnapshot(int targetClientId)
        {
            if (!IsNetworkRunning || tuningList.Count == 0) return;
            SendTuningList(SatsumaProtocol.TuningSnapshot, tuningList, targetClientId);
        }

        private void SendTuningList(byte subType, List<TrackedTuningParam> items, int targetClientId)
        {
            using (var p = new PacketWriter(PacketType.SatsumaTuningSync))
            {
                p.WriteByte(subType);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(items.Count);

                for (int i = 0; i < items.Count; i++)
                {
                    TrackedTuningParam item = items[i];
                    p.WriteByte((byte)item.Category);
                    p.WriteInt32(item.KeyHash);
                    p.WriteString(item.VarName ?? string.Empty);
                    p.WriteBoolean(item.IsBool);
                    if (item.IsBool)
                    {
                        bool val = item.BoolVar != null ? item.BoolVar.Value : item.LastSentBool;
                        item.LastSentBool = val;
                        p.WriteBoolean(val);
                    }
                    else
                    {
                        float val = item.FloatVar != null ? item.FloatVar.Value : item.LastSentFloat;
                        item.LastSentFloat = val;
                        p.WriteSingle(val);
                    }
                }

                if (targetClientId >= 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        private void PollFluids(float now)
        {
            if (isSuppressingFluids) return;

            bool boiling = steamRadiatorObj != null && steamRadiatorObj.activeInHierarchy;
            bool boilingChanged = boiling != lastSentBoiling;

            List<FluidChannel> changedChannels = null;
            foreach (var kv in fluidsByChannel)
            {
                TrackedFluid fluid = kv.Value;
                if (fluid.Targets.Count == 0 || now < fluid.SuppressUntilTime) continue;

                float cur = fluid.Targets[0].Value;
                if (fluid.LastSentValue < -900f)
                {
                    fluid.LastSentValue = cur;
                    fluid.LastSyncTime = now;
                    continue;
                }

                float delta = Mathf.Abs(cur - fluid.LastSentValue);
                // Мгновенная двусторонняя отправка при скачке > 0.5 (заправка / рефилл / BetterCheatBox);
                // плавный расход/утечка синхронизируется хостом раз в 2.0с без пинг-понга.
                bool jumpRefill = delta > 0.5f;
                bool hostTrickle = ctx.Transport.IsServer && (now - fluid.LastSyncTime) >= 2.0f && delta > 0.015f;

                if (jumpRefill || hostTrickle)
                {
                    fluid.LastSentValue = cur;
                    fluid.LastSyncTime = now;
                    if (changedChannels == null) changedChannels = new List<FluidChannel>();
                    changedChannels.Add(fluid.Channel);
                }
            }

            if ((changedChannels != null && changedChannels.Count > 0) || (ctx.Transport.IsServer && boilingChanged))
            {
                lastSentBoiling = boiling;
                if (changedChannels == null) changedChannels = new List<FluidChannel>();
                SendFluidsPacket(SatsumaProtocol.FluidsDelta, changedChannels, boiling, -1);
            }
        }

        private void SendFluidsSnapshot(int targetClientId)
        {
            if (!IsNetworkRunning) return;

            var allChannels = new List<FluidChannel>();
            foreach (var kv in fluidsByChannel)
            {
                if (kv.Value.Targets.Count > 0)
                {
                    allChannels.Add(kv.Key);
                }
            }

            bool boiling = steamRadiatorObj != null && steamRadiatorObj.activeInHierarchy;
            lastSentBoiling = boiling;
            SendFluidsPacket(SatsumaProtocol.FluidsSnapshot, allChannels, boiling, targetClientId);
        }

        private void SendFluidsPacket(byte subType, List<FluidChannel> channels, bool boiling, int targetClientId)
        {
            using (var p = new PacketWriter(PacketType.SatsumaFluidsAndWearSync))
            {
                p.WriteByte(subType);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(boiling);
                p.WriteByte((byte)channels.Count);

                for (int i = 0; i < channels.Count; i++)
                {
                    FluidChannel ch = channels[i];
                    TrackedFluid fluid = fluidsByChannel[ch];
                    float val = fluid.Targets.Count > 0 ? fluid.Targets[0].Value : 0f;
                    fluid.LastSentValue = val;
                    fluid.LastSyncTime = Time.time;

                    p.WriteByte((byte)ch);
                    p.WriteSingle(val);
                }

                if (targetClientId >= 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        private void PollWearStates(float now)
        {
            if (isSuppressingWear || wearList.Count == 0) return;

            List<TrackedWearPart> changed = null;
            for (int i = 0; i < wearList.Count; i++)
            {
                TrackedWearPart part = wearList[i];
                if (part == null || now < part.SuppressUntilTime) continue;

                float curWear = part.WearVar != null ? part.WearVar.Value : 0f;
                bool curBroken = ReadPartBrokenState(part);

                if (!part.Initialized)
                {
                    part.LastSentWear = curWear;
                    part.LastSentBroken = curBroken;
                    part.LastSyncTime = now;
                    part.Initialized = true;
                    continue;
                }

                bool brokenChanged = curBroken != part.LastSentBroken;
                float wearDelta = Mathf.Abs(curWear - part.LastSentWear);
                bool wearJump = wearDelta > 2.0f;
                bool hostTrickle = ctx.Transport.IsServer && wearDelta > 0.1f && (now - part.LastSyncTime) >= 2.5f;

                if (brokenChanged || wearJump || hostTrickle)
                {
                    part.LastSentWear = curWear;
                    part.LastSentBroken = curBroken;
                    part.LastSyncTime = now;
                    if (changed == null) changed = new List<TrackedWearPart>();
                    changed.Add(part);
                }
            }

            if (changed != null && changed.Count > 0)
            {
                SendWearPacket(SatsumaProtocol.WearDelta, changed, -1);
            }
        }

        private void SendWearSnapshot(int targetClientId)
        {
            if (!IsNetworkRunning || wearList.Count == 0) return;
            SendWearPacket(SatsumaProtocol.WearSnapshot, wearList, targetClientId);
        }

        private void SendWearPacket(byte subType, List<TrackedWearPart> parts, int targetClientId)
        {
            using (var p = new PacketWriter(PacketType.SatsumaFluidsAndWearSync))
            {
                p.WriteByte(subType);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte((byte)parts.Count);

                for (int i = 0; i < parts.Count; i++)
                {
                    TrackedWearPart part = parts[i];
                    float wear = part.WearVar != null ? part.WearVar.Value : 0f;
                    bool broken = ReadPartBrokenState(part);
                    part.LastSentWear = wear;
                    part.LastSentBroken = broken;
                    part.LastSyncTime = Time.time;

                    p.WriteByte((byte)part.Kind);
                    p.WriteBoolean(part.WearVar != null);
                    p.WriteSingle(wear);
                    p.WriteBoolean(broken);
                }

                if (targetClientId >= 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        private void PollBatteries(float now)
        {
            if (isSuppressingBattery || batteryList.Count == 0) return;

            for (int i = 0; i < batteryList.Count; i++)
            {
                TrackedBattery bat = batteryList[i];
                if (bat == null || bat.Fsm == null || now < bat.SuppressUntilTime) continue;

                float curCharge = bat.ChargeVar != null ? bat.ChargeVar.Value : -999f;
                float curLevel = bat.LevelVar != null ? bat.LevelVar.Value : -999f;
                bool curDead = bat.DeadVar != null && bat.DeadVar.Value;

                if (bat.LastSentCharge < -900f && curCharge >= -900f)
                {
                    bat.LastSentCharge = curCharge;
                    bat.LastSentLevel = curLevel;
                    bat.LastSentDead = curDead;
                    bat.LastSyncTime = now;
                    continue;
                }

                float dCharge = bat.ChargeVar != null ? Mathf.Abs(curCharge - bat.LastSentCharge) : 0f;
                float dLevel = bat.LevelVar != null ? Mathf.Abs(curLevel - bat.LastSentLevel) : 0f;
                bool deadChanged = curDead != bat.LastSentDead;
                bool jump = dCharge > 2.0f || dLevel > 0.5f || deadChanged;
                bool hostTrickle = ctx.Transport.IsServer && (now - bat.LastSyncTime) >= 1.5f && (dCharge > 0.1f || dLevel > 0.05f);

                if (jump || hostTrickle)
                {
                    SendBatteryEntry(bat, -1);
                }
            }
        }

        private void SendBatterySnapshot(int targetClientId)
        {
            if (!IsNetworkRunning) return;
            for (int i = 0; i < batteryList.Count; i++)
            {
                SendBatteryEntry(batteryList[i], targetClientId);
            }
        }

        private void SendBatteryEntry(TrackedBattery bat, int targetClientId)
        {
            if (bat == null || !IsNetworkRunning) return;

            float charge = bat.ChargeVar != null ? bat.ChargeVar.Value : 0f;
            float level = bat.LevelVar != null ? bat.LevelVar.Value : 0f;
            bool dead = bat.DeadVar != null && bat.DeadVar.Value;

            bat.LastSentCharge = bat.ChargeVar != null ? charge : -999f;
            bat.LastSentLevel = bat.LevelVar != null ? level : -999f;
            bat.LastSentDead = dead;
            bat.LastSyncTime = Time.time;

            using (var p = new PacketWriter(PacketType.SatsumaFluidsAndWearSync))
            {
                p.WriteByte(SatsumaProtocol.BatteryState);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(bat.KeyHash);
                p.WriteBoolean(bat.ChargeVar != null);
                p.WriteSingle(charge);
                p.WriteBoolean(bat.LevelVar != null);
                p.WriteSingle(level);
                p.WriteBoolean(dead);

                if (targetClientId >= 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        #endregion

        #region Packet Handlers

        public void HandleTuningPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 9) return;
            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            if (subType != SatsumaProtocol.TuningDelta && subType != SatsumaProtocol.TuningSnapshot) return;

            int count = reader.ReadInt32();
            if (count <= 0 || count > 2048) return;

            if (tuningList.Count == 0)
            {
                ScanAll();
            }

            float now = Time.time;
            isSuppressingTuning = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (reader.UnreadBytes < 7) break;

                    TuningCategory cat = (TuningCategory)reader.ReadByte();
                    int keyHash = reader.ReadInt32();
                    string varName = reader.ReadString();
                    if (reader.UnreadBytes < 2) break;
                    bool isBool = reader.ReadBoolean();
                    bool boolVal = false;
                    float floatVal = 0f;
                    if (isBool)
                    {
                        if (reader.UnreadBytes < 1) break;
                        boolVal = reader.ReadBoolean();
                    }
                    else
                    {
                        if (reader.UnreadBytes < 4) break;
                        floatVal = reader.ReadSingle();
                    }

                    if (!tuningByKey.TryGetValue(keyHash, out TrackedTuningParam param))
                    {
                        param = FindTuningParamFallback(cat, varName);
                    }
                    if (param == null) continue;

                    param.SuppressUntilTime = now + 0.35f;
                    if (isBool && param.BoolVar != null)
                    {
                        param.BoolVar.Value = boolVal;
                        param.LastSentBool = boolVal;
                        param.Initialized = true;
                    }
                    else if (!isBool && param.FloatVar != null)
                    {
                        param.FloatVar.Value = floatVal;
                        param.LastSentFloat = floatVal;
                        param.Initialized = true;

                        // Если это настроенный болт со стейтом Setup и уже добавленным событием MP_SETUP — мягко обновляем позицию/параметр
                        if (param.Fsm != null &&
                            param.VarName == "Alignment" &&
                            param.Fsm.FsmName == "Screw" &&
                            HasFsmEvent(param.Fsm, "MP_SETUP"))
                        {
                            param.Fsm.SendEvent("MP_SETUP");
                        }
                    }
                }
            }
            finally
            {
                isSuppressingTuning = false;
            }
        }

        public void HandleFluidsWearPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 5) return;
            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case SatsumaProtocol.FluidsDelta:
                case SatsumaProtocol.FluidsSnapshot:
                    ApplyFluidsPacket(reader);
                    break;

                case SatsumaProtocol.WearDelta:
                case SatsumaProtocol.WearSnapshot:
                    ApplyWearPacket(reader);
                    break;

                case SatsumaProtocol.BatteryState:
                    ApplyBatteryPacket(reader);
                    break;
            }
        }

        private void ApplyFluidsPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 2) return;
            bool boiling = reader.ReadBoolean();
            byte count = reader.ReadByte();

            if (fluidsByChannel.Count == 0)
            {
                ScanFluids();
            }

            float now = Time.time;
            isSuppressingFluids = true;
            try
            {
                lastSentBoiling = boiling;
                if (steamRadiatorObj != null && steamRadiatorObj.activeInHierarchy != boiling)
                {
                    steamRadiatorObj.SetActive(boiling);
                }

                for (int i = 0; i < count; i++)
                {
                    if (reader.UnreadBytes < 5) break;
                    FluidChannel ch = (FluidChannel)reader.ReadByte();
                    float val = reader.ReadSingle();

                    if (fluidsByChannel.TryGetValue(ch, out TrackedFluid fluid))
                    {
                        fluid.SuppressUntilTime = now + 0.5f;
                        fluid.LastSentValue = val;
                        fluid.LastSyncTime = now;
                        for (int t = 0; t < fluid.Targets.Count; t++)
                        {
                            if (fluid.Targets[t] != null)
                            {
                                fluid.Targets[t].Value = val;
                            }
                        }
                    }
                }
            }
            finally
            {
                isSuppressingFluids = false;
            }
        }

        private void ApplyWearPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 1) return;
            byte count = reader.ReadByte();

            if (wearList.Count == 0)
            {
                ScanWearParts();
            }

            float now = Time.time;
            isSuppressingWear = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (reader.UnreadBytes < 7) break;
                    WearPartKind kind = (WearPartKind)reader.ReadByte();
                    bool hasWearFloat = reader.ReadBoolean();
                    float wearVal = reader.ReadSingle();
                    bool broken = reader.ReadBoolean();

                    if (!wearByKind.TryGetValue(kind, out TrackedWearPart part)) continue;

                    bool wasBroken = ReadPartBrokenState(part);
                    part.SuppressUntilTime = now + 0.5f;
                    part.LastSentWear = wearVal;
                    part.LastSentBroken = broken;
                    part.LastSyncTime = now;
                    part.Initialized = true;

                    if (hasWearFloat && part.WearVar != null)
                    {
                        part.WearVar.Value = wearVal;
                    }
                    if (part.BrokenVar != null)
                    {
                        part.BrokenVar.Value = broken;
                    }

                    if (broken && !wasBroken && part.StateFsm != null)
                    {
                        TriggerBrokenStateFsm(part);
                    }
                }
            }
            finally
            {
                isSuppressingWear = false;
            }
        }

        private void ApplyBatteryPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 15) return;
            int keyHash = reader.ReadInt32();
            bool hasCharge = reader.ReadBoolean();
            float charge = reader.ReadSingle();
            bool hasLevel = reader.ReadBoolean();
            float level = reader.ReadSingle();
            bool dead = reader.ReadBoolean();

            if (batteryList.Count == 0)
            {
                ScanBatteries();
            }

            float now = Time.time;
            isSuppressingBattery = true;
            try
            {
                if (!batteriesByHash.TryGetValue(keyHash, out TrackedBattery bat))
                {
                    bat = batteryList.Count > 0 ? batteryList[0] : null;
                }
                if (bat == null) return;

                bat.SuppressUntilTime = now + 0.5f;
                if (hasCharge && bat.ChargeVar != null)
                {
                    bat.ChargeVar.Value = charge;
                    bat.LastSentCharge = charge;
                }
                if (hasLevel && bat.LevelVar != null)
                {
                    bat.LevelVar.Value = level;
                    bat.LastSentLevel = level;
                }
                if (bat.DeadVar != null)
                {
                    bat.DeadVar.Value = dead;
                    bat.LastSentDead = dead;
                }
                bat.LastSyncTime = now;
            }
            finally
            {
                isSuppressingBattery = false;
            }
        }

        #endregion

        #region Helpers

        private TrackedTuningParam FindTuningParamFallback(TuningCategory category, string varName)
        {
            for (int i = 0; i < tuningList.Count; i++)
            {
                TrackedTuningParam p = tuningList[i];
                if (p.Category == category && string.Equals(p.VarName, varName, StringComparison.Ordinal))
                {
                    return p;
                }
            }
            return null;
        }

        private static bool HasFsmEvent(PlayMakerFSM fsm, string eventName)
        {
            if (fsm == null || fsm.FsmEvents == null || string.IsNullOrEmpty(eventName)) return false;
            for (int i = 0; i < fsm.FsmEvents.Length; i++)
            {
                if (fsm.FsmEvents[i] != null && fsm.FsmEvents[i].Name == eventName)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ReadPartBrokenState(TrackedWearPart part)
        {
            if (part == null) return false;
            if (part.BrokenVar != null && part.BrokenVar.Value) return true;
            if (part.StateFsm != null)
            {
                string st = part.StateFsm.ActiveStateName;
                if (st == "Death" || st == "Break" || st == "Broken" || st == "No belt")
                {
                    return true;
                }
            }
            return false;
        }

        private static void TriggerBrokenStateFsm(TrackedWearPart part)
        {
            PlayMakerFSM fsm = part.StateFsm;
            if (fsm == null) return;

            switch (part.Kind)
            {
                case WearPartKind.FanBelt:
                    if (fsm.ActiveStateName != "No belt") fsm.SendEvent("SNAP");
                    break;
                case WearPartKind.RockerShaft:
                    if (fsm.ActiveStateName != "Break") fsm.SendEvent("BREAK");
                    break;
                case WearPartKind.Starter:
                    if (fsm.HasState("Death")) fsm.SendEvent("DEATH");
                    else if (fsm.HasState("Break")) fsm.SendEvent("BREAK");
                    break;
                default:
                    if (fsm.HasState("Death") && fsm.ActiveStateName != "Death") fsm.SendEvent("DEATH");
                    break;
            }
        }

        private int CountBoundFluids()
        {
            int count = 0;
            foreach (var kv in fluidsByChannel)
            {
                if (kv.Value.Targets.Count > 0) count++;
            }
            return count;
        }

        #endregion
    }
}
