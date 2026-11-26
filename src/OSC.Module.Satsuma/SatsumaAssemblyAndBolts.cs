using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Satsuma
{
    /// <summary>
    /// Синхронизация сборки, снятия и болтовой затяжки Satsuma (Stage 4):
    /// - Установка деталей через FSM "Assemble" / "Assembly" ("Insert", "TriggerCharger", проводка "RESETWIRING").
    /// - Снятие деталей через FSM "Removal" ("Remove part").
    /// - Затяжка болтов, штифтов, регулировочных болтов и резьбовых крышек (FSM "Screw").
    /// - Публикация PartAssembledEvent в OscEventBus при локальных и удалённых изменениях.
    /// - Детектор массовой автосборки (BetterCheatBox) через периодический дайджест SceneHasher и болтов.
    /// - Late-Join снимок (немедленный + отложенный на +2.0с) и очистка при отключении.
    /// </summary>
    internal sealed class SatsumaAssemblyAndBolts
    {
        private sealed class TrackedBolt
        {
            public int Hash;
            public PlayMakerFSM Fsm;
            public FsmInt StageVar;
            public FsmFloat TightnessVar;
            public FsmFloat AlignmentVar;
            public FsmFloat RotVar;
            public float MaxAlignment;
            public float RotAmount;
            public bool IsTuneBolt;
            public bool IsScrewableLid;
            public bool IsPin;
            public byte LastSyncedStage;
            public byte LastDigestStage;
            public bool IsHooked;
            public bool SuppressEcho;
            public float SuppressEchoUntil = -10f;

            public byte CurrentStage
            {
                get
                {
                    if (IsTuneBolt && AlignmentVar != null && MaxAlignment > 0f)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(AlignmentVar.Value / MaxAlignment * 255f), 0, 255);
                    }
                    if (IsScrewableLid && RotVar != null)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(RotVar.Value / 360f * 255f), 0, 255);
                    }
                    if (StageVar != null)
                    {
                        return (byte)Mathf.Clamp(StageVar.Value, 0, 255);
                    }
                    if (TightnessVar != null)
                    {
                        return (byte)Mathf.Clamp(Mathf.RoundToInt(TightnessVar.Value), 0, 255);
                    }
                    return 0;
                }
            }
        }

        private sealed class TrackedRemovalPart
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public bool IsHooked;
            public bool SuppressEcho;
            public float SuppressEchoUntil = -10f;
            public bool LastKnownAssembled;
            public bool LastDigestAssembled;
        }

        private sealed class TrackedAssembleTrigger
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmGameObject PartVar;
            public FsmBool InstalledVar;
            public string AssembleState;
            public bool IsHooked;
            public bool IsWiring;
            public bool IsTriggerCharger;
            public bool SuppressEcho;
            public float SuppressEchoUntil = -10f;
            public bool LastKnownAssembled;
            public bool LastDigestAssembled;
            public int LastAssembledPartHash;
        }

        private struct PendingAssemblePart
        {
            public int TriggerHash;
            public int PartHash;
            public float RetryTime;
            public int RetryCount;
        }

        private struct PendingBoltApply
        {
            public int BoltHash;
            public byte Stage;
            public float RetryTime;
            public int RetryCount;
        }

        private struct PendingSnapshotTarget
        {
            public int ClientId;
            public float SendAtTime;
        }

        private struct PendingLocalAssemble
        {
            public int TriggerHash;
            public int PartHash;
        }

        private struct PendingLocalBolt
        {
            public int BoltHash;
            public byte Stage;
        }

        private const float DigestCheckIntervalSeconds = 0.5f;
        private const float PostLoadWarmupSeconds = 1.5f;
        private const int CheatPartJumpThreshold = 3;
        private const int CheatBoltJumpThreshold = 6;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly Dictionary<int, TrackedBolt> bolts = new Dictionary<int, TrackedBolt>();
        private readonly List<TrackedBolt> boltList = new List<TrackedBolt>();

        private readonly Dictionary<int, TrackedRemovalPart> removalParts = new Dictionary<int, TrackedRemovalPart>();
        private readonly Dictionary<GameObject, TrackedRemovalPart> removalPartsByGo = new Dictionary<GameObject, TrackedRemovalPart>();
        private readonly List<TrackedRemovalPart> removalList = new List<TrackedRemovalPart>();

        private readonly Dictionary<int, TrackedAssembleTrigger> assembleTriggers = new Dictionary<int, TrackedAssembleTrigger>();
        private readonly List<TrackedAssembleTrigger> triggerList = new List<TrackedAssembleTrigger>();

        private readonly List<UnityEngine.Object> claimedObjects = new List<UnityEngine.Object>();
        private readonly List<PendingAssemblePart> pendingParts = new List<PendingAssemblePart>();
        private readonly List<PendingBoltApply> pendingBolts = new List<PendingBoltApply>();
        private readonly List<PendingSnapshotTarget> pendingSnapshots = new List<PendingSnapshotTarget>();

        private readonly List<PendingLocalAssemble> pendingLocalAssembles = new List<PendingLocalAssemble>();
        private readonly List<int> pendingLocalRemovals = new List<int>();
        private readonly Dictionary<int, PendingLocalBolt> pendingLocalBolts = new Dictionary<int, PendingLocalBolt>();

        private readonly HashSet<int> suppressedBolts = new HashSet<int>();
        private readonly HashSet<int> suppressedParts = new HashSet<int>();

        private bool isInitialized;
        private float nextInitRetryTime;
        private float nextDigestCheckTime;
        private float digestWarmupUntilTime;
        private int lastStateDigest;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary
        {
            get
            {
                int assembledCount = 0;
                for (int i = 0; i < triggerList.Count; i++)
                {
                    if (IsTriggerCurrentlyAssembled(triggerList[i]))
                    {
                        assembledCount++;
                    }
                }
                return $"Слотов: {assembledCount}/{triggerList.Count}, Деталей: {removalList.Count}, Болтов: {boltList.Count}";
            }
        }

        public SatsumaAssemblyAndBolts(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void Reset()
        {
            if (ctx != null && ctx.Ownership != null)
            {
                for (int i = 0; i < claimedObjects.Count; i++)
                {
                    if (claimedObjects[i] != null)
                    {
                        ctx.Ownership.Release(claimedObjects[i], moduleId);
                    }
                }
            }
            claimedObjects.Clear();

            isInitialized = false;
            nextInitRetryTime = 0f;
            nextDigestCheckTime = 0f;
            digestWarmupUntilTime = 0f;
            lastStateDigest = 0;

            bolts.Clear();
            boltList.Clear();
            removalParts.Clear();
            removalPartsByGo.Clear();
            removalList.Clear();
            assembleTriggers.Clear();
            triggerList.Clear();

            pendingParts.Clear();
            pendingBolts.Clear();
            pendingSnapshots.Clear();
            pendingLocalAssembles.Clear();
            pendingLocalRemovals.Clear();
            pendingLocalBolts.Clear();

            suppressedBolts.Clear();
            suppressedParts.Clear();
        }

        public void OnLevelLoaded()
        {
            Reset();
            InitializeAssembly();
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            if ((!isInitialized || (triggerList.Count == 0 && boltList.Count == 0)) && Time.time >= nextInitRetryTime)
            {
                nextInitRetryTime = Time.time + 2.0f;
                InitializeAssembly();
            }

            ProcessPendingParts();
            ProcessPendingBolts();
            ProcessPendingSnapshots();
            FlushPendingLocalChanges();

            if (Time.time >= nextDigestCheckTime)
            {
                nextDigestCheckTime = Time.time + DigestCheckIntervalSeconds;
                CheckStateDigestAndCheatAutoAssembly();
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (ctx == null || ctx.Transport == null || !ctx.Transport.IsServer) return;

            SendSnapshotToClient(clientId);

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == clientId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }

            pendingSnapshots.Add(new PendingSnapshotTarget
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

            if (ctx != null && ctx.Transport != null && !ctx.Transport.IsServer)
            {
                pendingParts.Clear();
                pendingBolts.Clear();
                pendingLocalAssembles.Clear();
                pendingLocalRemovals.Clear();
                pendingLocalBolts.Clear();
            }
        }

        private void InitializeAssembly()
        {
            try
            {
                IndexPartGameObjects();
                InitBolts();
                InitRemovalParts();
                InitAssembleTriggers();

                SyncDigestBaselines();
                digestWarmupUntilTime = Time.time + PostLoadWarmupSeconds;
                nextDigestCheckTime = Time.time + DigestCheckIntervalSeconds;
                isInitialized = true;

                ctx?.Log?.Invoke($"[Satsuma Сборка] Подключено деталей: {removalList.Count}, триггеров: {triggerList.Count}, болтов: {boltList.Count}.");
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma Сборка] Ошибка инициализации: {ex.Message}");
            }
        }

        private void IndexPartGameObjects()
        {
            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i];
                if (go == null || go.hideFlags != HideFlags.None) continue;
                if (!go.activeInHierarchy && go.activeSelf && go.transform.parent == null) continue;

                if (go.CompareTag("PART"))
                {
                    SceneHasher.GetHash(go);
                }
            }
        }

        private bool TryClaimFsm(PlayMakerFSM fsm, string purpose)
        {
            if (fsm == null) return false;
            if (ctx == null || ctx.Ownership == null) return true;

            if (!ctx.Ownership.TryClaim(fsm, moduleId, purpose))
            {
                return false;
            }

            if (!claimedObjects.Contains(fsm))
            {
                claimedObjects.Add(fsm);
            }
            return true;
        }

        private void InitBolts()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null || fsm.gameObject.hideFlags != HideFlags.None) continue;
                if (fsm.FsmName != "Screw") continue;

                fsm.InitializeSafe();
                FsmInt stageVar = fsm.FsmVariables.FindFsmInt("Stage");
                FsmFloat tightVar = stageVar == null ? fsm.FsmVariables.FindFsmFloat("Tightness") : null;
                FsmFloat alignmentVar = fsm.FsmVariables.FindFsmFloat("Alignment");
                FsmFloat rotVar = fsm.FsmVariables.FindFsmFloat("Rot");

                if (stageVar == null && tightVar == null && alignmentVar == null && rotVar == null) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (bolts.ContainsKey(hash)) continue;
                if (!TryClaimFsm(fsm, "SatsumaScrew")) continue;

                bool isTune = fsm.HasState("Setup") && alignmentVar != null;
                bool isLid = fsm.HasState("Wait") && !fsm.HasState("Wait 2") && !fsm.HasState("Wait 3") && !fsm.HasState("Wait 4") && rotVar != null;
                bool isPin = fsm.gameObject.name == "Pin";

                float maxAlign = 0f;
                if (isTune)
                {
                    FsmFloat maxVar = fsm.FsmVariables.FindFsmFloat("Max");
                    maxAlign = (maxVar != null && maxVar.Value > 0f) ? maxVar.Value : 1f;
                }

                float rotAmt = 0f;
                if (isLid)
                {
                    FsmFloat rotAmtVar = fsm.FsmVariables.FindFsmFloat("ScrewAmount");
                    rotAmt = rotAmtVar != null ? rotAmtVar.Value : 10f;
                }

                var bolt = new TrackedBolt
                {
                    Hash = hash,
                    Fsm = fsm,
                    StageVar = stageVar,
                    TightnessVar = tightVar,
                    AlignmentVar = alignmentVar,
                    RotVar = rotVar,
                    MaxAlignment = maxAlign,
                    RotAmount = rotAmt,
                    IsTuneBolt = isTune,
                    IsScrewableLid = isLid,
                    IsPin = isPin,
                    IsHooked = false
                };

                byte initialStage = bolt.CurrentStage;
                bolt.LastSyncedStage = initialStage;
                bolt.LastDigestStage = initialStage;

                bolts[hash] = bolt;
                boltList.Add(bolt);
                HookBoltFsm(bolt);
            }
        }

        private void HookBoltFsm(TrackedBolt bolt)
        {
            if (bolt.IsHooked || bolt.Fsm == null) return;
            bolt.Fsm.InitializeSafe();
            int hash = bolt.Hash;

            Action onTightnessAction = () =>
            {
                if (bolt.IsTuneBolt) return;
                if (bolt.SuppressEcho || suppressedBolts.Contains(hash) || Time.time < bolt.SuppressEchoUntil)
                {
                    byte synced = bolt.CurrentStage;
                    bolt.LastSyncedStage = synced;
                    bolt.LastDigestStage = synced;
                    return;
                }

                byte currentStage = bolt.CurrentStage;
                if (currentStage != bolt.LastSyncedStage)
                {
                    bolt.LastSyncedStage = currentStage;
                    bolt.LastDigestStage = currentStage;

                    if (IsNetworkRunning)
                    {
                        pendingLocalBolts[hash] = new PendingLocalBolt
                        {
                            BoltHash = hash,
                            Stage = currentStage
                        };
                    }
                }
            };

            string[] statesToHook = { "Wait 3", "Wait", "Wait 2", "Wait 4", "Stage up", "Stage down", "Setup", "Rotate" };
            for (int s = 0; s < statesToHook.Length; s++)
            {
                string st = statesToHook[s];
                if (bolt.Fsm.HasState(st))
                {
                    bolt.Fsm.InsertAction(st, onTightnessAction, 0, true);
                }
            }

            if (bolt.IsScrewableLid)
            {
                FsmEvent mpUnscrew = bolt.Fsm.AddSafeEvent("MP_UNSCREW");
                FsmEvent mpScrew = bolt.Fsm.AddSafeEvent("MP_SCREW");
                if (bolt.Fsm.HasState("Unscrew")) bolt.Fsm.AddSafeGlobalTransition(mpUnscrew, "Unscrew");
                if (bolt.Fsm.HasState("Screw")) bolt.Fsm.AddSafeGlobalTransition(mpScrew, "Screw");
            }
            else if (bolt.IsTuneBolt)
            {
                FsmEvent mpSetup = bolt.Fsm.AddSafeEvent("MP_SETUP");
                bolt.Fsm.AddSafeGlobalTransition(mpSetup, "Setup");
            }
            else if (bolt.IsPin)
            {
                FsmEvent mpTighten = bolt.Fsm.AddSafeEvent("MP_TIGHTEN");
                FsmEvent mpUntighten = bolt.Fsm.AddSafeEvent("MP_UNTIGHTEN");
                if (bolt.Fsm.HasState("1")) bolt.Fsm.AddSafeGlobalTransition(mpTighten, "1");
                if (bolt.Fsm.HasState("0")) bolt.Fsm.AddSafeGlobalTransition(mpUntighten, "0");
            }

            bolt.IsHooked = true;
        }

        private void InitRemovalParts()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null || fsm.gameObject.hideFlags != HideFlags.None) continue;
                if (fsm.FsmName != "Removal") continue;

                fsm.InitializeSafe();
                if (!fsm.HasState("Remove part")) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (removalParts.ContainsKey(hash)) continue;
                if (!TryClaimFsm(fsm, "SatsumaRemoval")) continue;

                FsmEvent mpRemove = fsm.AddSafeEvent("MP_REMOVE");
                fsm.AddSafeGlobalTransition(mpRemove, "Remove part");

                var part = new TrackedRemovalPart
                {
                    Hash = hash,
                    GameObject = fsm.gameObject,
                    Fsm = fsm,
                    IsHooked = false
                };

                bool assembledNow = IsRemovalPartAssembled(part);
                part.LastKnownAssembled = assembledNow;
                part.LastDigestAssembled = assembledNow;

                removalParts[hash] = part;
                removalPartsByGo[fsm.gameObject] = part;
                removalList.Add(part);
                HookRemovalFsm(part);
            }
        }

        private void HookRemovalFsm(TrackedRemovalPart part)
        {
            if (part.IsHooked || part.Fsm == null) return;
            part.Fsm.InitializeSafe();
            int hash = part.Hash;

            part.Fsm.InsertAction("Remove part", () =>
            {
                RestoreRemovedPartPhysics(part.GameObject);
                part.LastKnownAssembled = false;
                part.LastDigestAssembled = false;
                ClearTriggerStateForPart(part.GameObject);

                if (part.SuppressEcho || suppressedParts.Contains(hash) || Time.time < part.SuppressEchoUntil)
                {
                    return;
                }

                PublishPartAssembledEvent(hash, part.GameObject != null ? part.GameObject.name : string.Empty, part.GameObject, false, true);

                if (IsNetworkRunning && !pendingLocalRemovals.Contains(hash))
                {
                    pendingLocalRemovals.Add(hash);
                }
            }, 0, true);

            part.IsHooked = true;
        }

        private void InitAssembleTriggers()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null || fsm.gameObject.hideFlags != HideFlags.None) continue;

                bool isAssemblyFsm = fsm.FsmName == "Assembly";
                bool isAssembleFsm = fsm.FsmName == "Assemble";
                if (!isAssemblyFsm && !isAssembleFsm) continue;

                bool isInsert = isAssemblyFsm && fsm.gameObject.name == "Insert";
                bool isTriggerCharger = isAssemblyFsm && fsm.gameObject.name == "TriggerCharger";
                if (isAssemblyFsm && !isInsert && !isTriggerCharger) continue;

                fsm.InitializeSafe();
                bool isWiring = false;
                string assembleState = null;
                FsmGameObject partVar = null;

                if (isInsert)
                {
                    if (!fsm.HasState("Add battery")) continue;
                    assembleState = "Add battery";
                    partVar = fsm.FsmVariables.FindFsmGameObject("Part");
                }
                else if (isTriggerCharger)
                {
                    if (!fsm.HasState("Init")) continue;
                    assembleState = "Init";
                    partVar = fsm.FsmVariables.FindFsmGameObject("Battery");
                }
                else
                {
                    isWiring = fsm.FsmGlobalTransitions != null &&
                               fsm.FsmGlobalTransitions.Any(t => t != null && t.EventName == "RESETWIRING");
                    if (isWiring)
                    {
                        assembleState = fsm.HasState("Sound")
                            ? "Sound"
                            : (fsm.HasState("Assemble") ? "Assemble" : null);
                    }
                    else
                    {
                        FsmTransition assembleTransition = null;
                        if (fsm.FsmStates != null && fsm.FsmStates.Length > 0 && fsm.FsmStates[0].Transitions != null)
                        {
                            assembleTransition = fsm.FsmStates[0].Transitions.FirstOrDefault(
                                t => t != null && t.EventName != null && t.EventName.Contains("ASSEMBL"));
                        }

                        if (assembleTransition != null && !string.IsNullOrEmpty(assembleTransition.ToState))
                        {
                            assembleState = assembleTransition.ToState;
                        }
                        else if (fsm.FsmStates != null)
                        {
                            FsmState state = fsm.FsmStates.FirstOrDefault(
                                s => s != null && !string.IsNullOrEmpty(s.Name) &&
                                     s.Name.IndexOf("assemble", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (state != null)
                            {
                                assembleState = state.Name;
                            }
                        }
                    }

                    partVar = fsm.FsmVariables.FindFsmGameObject("Part");
                }

                if (string.IsNullOrEmpty(assembleState)) continue;

                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (assembleTriggers.ContainsKey(hash)) continue;
                if (!TryClaimFsm(fsm, "SatsumaAssemble")) continue;

                FsmFloat attachForce = fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                if (!isTriggerCharger)
                {
                    FsmEvent mpAssemble = fsm.AddSafeEvent("MP_ASSEMBLE");
                    fsm.AddSafeGlobalTransition(mpAssemble, assembleState);
                }

                var trigger = new TrackedAssembleTrigger
                {
                    Hash = hash,
                    GameObject = fsm.gameObject,
                    Fsm = fsm,
                    PartVar = partVar,
                    InstalledVar = fsm.FsmVariables.FindFsmBool("Installed"),
                    AssembleState = assembleState,
                    IsHooked = false,
                    IsWiring = isWiring,
                    IsTriggerCharger = isTriggerCharger
                };

                if (partVar != null && partVar.Value != null)
                {
                    SceneHasher.GetHash(partVar.Value);
                }

                bool assembledNow = IsTriggerCurrentlyAssembled(trigger);
                trigger.LastKnownAssembled = assembledNow;
                trigger.LastDigestAssembled = assembledNow;
                trigger.LastAssembledPartHash = (assembledNow && partVar != null && partVar.Value != null)
                    ? SceneHasher.GetHash(partVar.Value)
                    : 0;

                assembleTriggers[hash] = trigger;
                triggerList.Add(trigger);
                HookAssembleFsm(trigger, assembleState);
            }
        }

        private void HookAssembleFsm(TrackedAssembleTrigger trigger, string assembleState)
        {
            if (trigger.IsHooked || trigger.Fsm == null) return;
            trigger.Fsm.InitializeSafe();
            int triggerHash = trigger.Hash;

            Action onAssembledAction = () =>
            {
                FsmFloat attachForce = trigger.Fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                GameObject assembledPart = null;
                int partHash = 0;
                if (trigger.PartVar != null && trigger.PartVar.Value != null)
                {
                    assembledPart = trigger.PartVar.Value;
                    partHash = SceneHasher.GetHash(assembledPart);
                }

                trigger.LastKnownAssembled = true;
                trigger.LastDigestAssembled = true;
                trigger.LastAssembledPartHash = partHash;

                if (assembledPart != null && removalPartsByGo.TryGetValue(assembledPart, out TrackedRemovalPart rem))
                {
                    rem.LastKnownAssembled = true;
                    rem.LastDigestAssembled = true;
                }

                if (trigger.SuppressEcho || suppressedParts.Contains(triggerHash) || Time.time < trigger.SuppressEchoUntil)
                {
                    return;
                }

                string partKey = assembledPart != null
                    ? assembledPart.name
                    : (trigger.GameObject != null ? trigger.GameObject.name : string.Empty);

                PublishPartAssembledEvent(partHash, partKey, assembledPart, true, true);

                if (IsNetworkRunning)
                {
                    pendingLocalAssembles.Add(new PendingLocalAssemble
                    {
                        TriggerHash = triggerHash,
                        PartHash = partHash
                    });
                }
            };

            FsmState[] assembleStates = trigger.Fsm.FsmStates != null
                ? trigger.Fsm.FsmStates.Where(
                    s => s != null && !string.IsNullOrEmpty(s.Name) &&
                         s.Name.IndexOf("assemble", StringComparison.OrdinalIgnoreCase) >= 0).ToArray()
                : new FsmState[0];

            // Вставляем хук строго в конец стейта (index = -1), после всех ванильных действий PlayMaker
            if (trigger.IsWiring || trigger.IsTriggerCharger || trigger.Fsm.FsmName == "Assembly" || assembleStates.Length == 0)
            {
                trigger.Fsm.InsertAction(assembleState, onAssembledAction, -1, true);
            }
            else
            {
                for (int i = 0; i < assembleStates.Length; i++)
                {
                    trigger.Fsm.InsertAction(assembleStates[i].Name, onAssembledAction, -1, true);
                }
            }

            trigger.IsHooked = true;
        }

        private void FlushPendingLocalChanges()
        {
            int partChanges = pendingLocalAssembles.Count + pendingLocalRemovals.Count;
            int boltChanges = pendingLocalBolts.Count;

            if (partChanges == 0 && boltChanges == 0) return;

            if (!IsNetworkRunning)
            {
                pendingLocalAssembles.Clear();
                pendingLocalRemovals.Clear();
                pendingLocalBolts.Clear();
                return;
            }

            if (partChanges >= CheatPartJumpThreshold || boltChanges >= CheatBoltJumpThreshold)
            {
                pendingLocalAssembles.Clear();
                pendingLocalRemovals.Clear();
                pendingLocalBolts.Clear();

                SyncDigestBaselines();
                BroadcastFullSnapshot();
                ctx?.Log?.Invoke($"[Satsuma Сборка] Обнаружена пакетная сборка (деталей: {partChanges}, болтов: {boltChanges}) — разослан PartAssembleSnapshot.");
                return;
            }

            for (int i = 0; i < pendingLocalAssembles.Count; i++)
            {
                PendingLocalAssemble item = pendingLocalAssembles[i];
                SendPartAssemble(item.TriggerHash, item.PartHash);
            }
            pendingLocalAssembles.Clear();

            for (int i = 0; i < pendingLocalRemovals.Count; i++)
            {
                SendPartRemove(pendingLocalRemovals[i]);
            }
            pendingLocalRemovals.Clear();

            foreach (var kv in pendingLocalBolts)
            {
                SendBoltScrew(kv.Value.BoltHash, kv.Value.Stage);
            }
            pendingLocalBolts.Clear();

            lastStateDigest = ComputeStateDigest();
        }

        private void CheckStateDigestAndCheatAutoAssembly()
        {
            int currentDigest = ComputeStateDigest();
            if (currentDigest == lastStateDigest) return;

            if (Time.time < digestWarmupUntilTime)
            {
                SyncDigestBaselines();
                return;
            }

            int changedParts = 0;
            int changedBolts = 0;
            var changedTriggers = new List<TrackedAssembleTrigger>();
            var changedRemovals = new List<TrackedRemovalPart>();
            var changedBoltList = new List<TrackedBolt>();

            for (int i = 0; i < triggerList.Count; i++)
            {
                TrackedAssembleTrigger t = triggerList[i];
                if (t == null) continue;
                bool nowAssembled = IsTriggerCurrentlyAssembled(t);
                if (nowAssembled != t.LastDigestAssembled)
                {
                    changedParts++;
                    changedTriggers.Add(t);
                }
            }

            for (int i = 0; i < removalList.Count; i++)
            {
                TrackedRemovalPart r = removalList[i];
                if (r == null) continue;
                bool nowAssembled = IsRemovalPartAssembled(r);
                if (nowAssembled != r.LastDigestAssembled)
                {
                    // Не дублируем счётчик, если деталь уже учтена через свой триггер
                    if (!WasPartCoveredByTriggerList(r.GameObject, changedTriggers))
                    {
                        changedParts++;
                    }
                    changedRemovals.Add(r);
                }
            }

            for (int i = 0; i < boltList.Count; i++)
            {
                TrackedBolt b = boltList[i];
                if (b == null) continue;
                if (b.SuppressEcho || Time.time < b.SuppressEchoUntil)
                {
                    b.LastDigestStage = b.CurrentStage;
                    continue;
                }

                byte stage = b.CurrentStage;
                if (stage != b.LastDigestStage)
                {
                    changedBolts++;
                    changedBoltList.Add(b);
                }
            }

            if (changedParts == 0 && changedBolts == 0)
            {
                lastStateDigest = currentDigest;
                return;
            }

            // Публикуем события изменения деталей для смежных модулей (Doors / Controls)
            for (int i = 0; i < changedTriggers.Count; i++)
            {
                TrackedAssembleTrigger t = changedTriggers[i];
                bool nowAssembled = IsTriggerCurrentlyAssembled(t);
                t.LastDigestAssembled = nowAssembled;
                t.LastKnownAssembled = nowAssembled;

                GameObject partGo = t.PartVar != null ? t.PartVar.Value : null;
                int prevPartHash = t.LastAssembledPartHash;
                int partHash = partGo != null ? SceneHasher.GetHash(partGo) : prevPartHash;
                t.LastAssembledPartHash = nowAssembled ? partHash : 0;

                string partKey = partGo != null ? partGo.name : (t.GameObject != null ? t.GameObject.name : string.Empty);
                PublishPartAssembledEvent(partHash, partKey, partGo, nowAssembled, true);
            }

            for (int i = 0; i < changedRemovals.Count; i++)
            {
                TrackedRemovalPart r = changedRemovals[i];
                bool nowAssembled = IsRemovalPartAssembled(r);
                r.LastDigestAssembled = nowAssembled;
                r.LastKnownAssembled = nowAssembled;

                if (!WasPartCoveredByTriggerList(r.GameObject, changedTriggers))
                {
                    string partKey = r.GameObject != null ? r.GameObject.name : string.Empty;
                    PublishPartAssembledEvent(r.Hash, partKey, r.GameObject, nowAssembled, true);
                }
            }

            for (int i = 0; i < changedBoltList.Count; i++)
            {
                TrackedBolt b = changedBoltList[i];
                byte stage = b.CurrentStage;
                b.LastDigestStage = stage;
                b.LastSyncedStage = stage;
            }

            lastStateDigest = currentDigest;

            if (!IsNetworkRunning) return;

            if (changedParts >= CheatPartJumpThreshold || changedBolts >= CheatBoltJumpThreshold)
            {
                BroadcastFullSnapshot();
                ctx?.Log?.Invoke($"[Satsuma Сборка] Обнаружен скачок состояния (деталей: {changedParts}, болтов: {changedBolts}) — отправлен полный PartAssembleSnapshot.");
            }
            else
            {
                for (int i = 0; i < changedTriggers.Count; i++)
                {
                    TrackedAssembleTrigger t = changedTriggers[i];
                    if (t.LastKnownAssembled)
                    {
                        SendPartAssemble(t.Hash, t.LastAssembledPartHash);
                    }
                    else if (t.PartVar != null && t.PartVar.Value != null)
                    {
                        SendPartRemove(SceneHasher.GetHash(t.PartVar.Value));
                    }
                }

                for (int i = 0; i < changedRemovals.Count; i++)
                {
                    TrackedRemovalPart r = changedRemovals[i];
                    if (!r.LastKnownAssembled && !WasPartCoveredByTriggerList(r.GameObject, changedTriggers))
                    {
                        SendPartRemove(r.Hash);
                    }
                }

                for (int i = 0; i < changedBoltList.Count; i++)
                {
                    TrackedBolt b = changedBoltList[i];
                    if (b.IsTuneBolt) continue;
                    SendBoltScrew(b.Hash, b.LastSyncedStage);
                }
            }
        }

        private static bool WasPartCoveredByTriggerList(GameObject partGo, List<TrackedAssembleTrigger> triggers)
        {
            if (partGo == null || triggers == null) return false;
            for (int i = 0; i < triggers.Count; i++)
            {
                TrackedAssembleTrigger t = triggers[i];
                if (t != null && t.PartVar != null && t.PartVar.Value == partGo)
                {
                    return true;
                }
            }
            return false;
        }

        private void SyncDigestBaselines()
        {
            for (int i = 0; i < triggerList.Count; i++)
            {
                TrackedAssembleTrigger t = triggerList[i];
                if (t == null) continue;
                bool assembled = IsTriggerCurrentlyAssembled(t);
                t.LastKnownAssembled = assembled;
                t.LastDigestAssembled = assembled;
                t.LastAssembledPartHash = (assembled && t.PartVar != null && t.PartVar.Value != null)
                    ? SceneHasher.GetHash(t.PartVar.Value)
                    : 0;
            }

            for (int i = 0; i < removalList.Count; i++)
            {
                TrackedRemovalPart r = removalList[i];
                if (r == null) continue;
                bool assembled = IsRemovalPartAssembled(r);
                r.LastKnownAssembled = assembled;
                r.LastDigestAssembled = assembled;
            }

            for (int i = 0; i < boltList.Count; i++)
            {
                TrackedBolt b = boltList[i];
                if (b == null) continue;
                byte stage = b.CurrentStage;
                b.LastSyncedStage = stage;
                b.LastDigestStage = stage;
            }

            lastStateDigest = ComputeStateDigest();
        }

        private int ComputeStateDigest()
        {
            unchecked
            {
                uint hash = 2166136261;

                for (int i = 0; i < triggerList.Count; i++)
                {
                    TrackedAssembleTrigger t = triggerList[i];
                    if (t == null) continue;
                    bool assembled = IsTriggerCurrentlyAssembled(t);
                    hash ^= (uint)t.Hash;
                    hash *= 16777619;
                    hash ^= assembled ? 1u : 0u;
                    hash *= 16777619;
                    if (assembled && t.PartVar != null && t.PartVar.Value != null)
                    {
                        hash ^= (uint)SceneHasher.GetHash(t.PartVar.Value);
                        hash *= 16777619;
                    }
                }

                for (int i = 0; i < removalList.Count; i++)
                {
                    TrackedRemovalPart r = removalList[i];
                    if (r == null) continue;
                    hash ^= (uint)r.Hash;
                    hash *= 16777619;
                    hash ^= IsRemovalPartAssembled(r) ? 1u : 0u;
                    hash *= 16777619;
                }

                for (int i = 0; i < boltList.Count; i++)
                {
                    TrackedBolt b = boltList[i];
                    if (b == null) continue;
                    hash ^= (uint)b.Hash;
                    hash *= 16777619;
                    hash ^= b.CurrentStage;
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }

        public void HandlePartAssemble(PacketReader r, int connId)
        {
            if (r == null) return;
            int senderId = r.ReadInt32();
            int triggerHash = r.ReadInt32();
            int partHash = r.ReadInt32();

            if (senderId == LocalPlayerId) return;

            ApplyRemotePartAssemble(triggerHash, partHash);
        }

        public void HandlePartRemove(PacketReader r, int connId)
        {
            if (r == null) return;
            int senderId = r.ReadInt32();
            int partHash = r.ReadInt32();

            if (senderId == LocalPlayerId) return;

            ApplyRemotePartRemove(partHash);
        }

        public void HandlePartBoltScrew(PacketReader r, int connId)
        {
            if (r == null) return;
            int senderId = r.ReadInt32();
            int boltHash = r.ReadInt32();
            byte stage = r.ReadByte();

            if (senderId == LocalPlayerId) return;

            ApplyRemoteBoltScrew(boltHash, stage);
        }

        public void HandlePartAssembleSnapshot(PacketReader r, int connId)
        {
            if (r == null) return;
            int senderId = r.ReadInt32();
            if (senderId == LocalPlayerId) return;

            int unmountedCount = r.ReadInt32();
            for (int i = 0; i < unmountedCount; i++)
            {
                int partHash = r.ReadInt32();
                if (removalParts.TryGetValue(partHash, out TrackedRemovalPart part) && IsRemovalPartAssembled(part))
                {
                    ApplyRemotePartRemove(partHash);
                }
            }

            int assembledCount = r.ReadInt32();
            for (int i = 0; i < assembledCount; i++)
            {
                int triggerHash = r.ReadInt32();
                int partHash = r.ReadInt32();

                if (assembleTriggers.TryGetValue(triggerHash, out TrackedAssembleTrigger trigger) &&
                    IsTriggerCurrentlyAssembled(trigger) &&
                    (partHash == 0 || trigger.LastAssembledPartHash == partHash ||
                     (trigger.PartVar != null && trigger.PartVar.Value != null && SceneHasher.GetHash(trigger.PartVar.Value) == partHash)))
                {
                    trigger.LastKnownAssembled = true;
                    trigger.LastDigestAssembled = true;
                    continue;
                }

                ApplyRemotePartAssemble(triggerHash, partHash);
            }

            int boltCount = r.ReadInt32();
            for (int i = 0; i < boltCount; i++)
            {
                int boltHash = r.ReadInt32();
                byte stage = r.ReadByte();

                ApplyRemoteBoltScrew(boltHash, stage);

                if (stage > 0 && bolts.TryGetValue(boltHash, out TrackedBolt bolt) &&
                    bolt.Fsm != null && bolt.Fsm.gameObject != null && !bolt.Fsm.gameObject.activeInHierarchy)
                {
                    pendingBolts.Add(new PendingBoltApply
                    {
                        BoltHash = boltHash,
                        Stage = stage,
                        RetryTime = Time.time + 0.1f,
                        RetryCount = 0
                    });
                }
            }

            SyncDigestBaselines();
            ctx?.Log?.Invoke($"[Satsuma Сборка] Принят снимок: снято={unmountedCount}, слотов={assembledCount}, болтов={boltCount}.");
        }

        private void SendBoltScrew(int boltHash, byte stage)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PartBoltScrew))
            {
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(boltHash);
                packet.WriteByte(stage);
                ctx.Transport.Send(packet);
            }
        }

        private void SendPartRemove(int partHash)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PartRemove))
            {
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(partHash);
                ctx.Transport.Send(packet);
            }
        }

        private void SendPartAssemble(int triggerHash, int partHash)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PartAssemble))
            {
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(triggerHash);
                packet.WriteInt32(partHash);
                ctx.Transport.Send(packet);
            }
        }

        private PacketWriter BuildFullSnapshotPacket()
        {
            var unmountedParts = new List<int>();
            for (int i = 0; i < removalList.Count; i++)
            {
                TrackedRemovalPart r = removalList[i];
                if (r != null && !IsRemovalPartAssembled(r))
                {
                    unmountedParts.Add(r.Hash);
                }
            }

            var assembledSlots = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < triggerList.Count; i++)
            {
                TrackedAssembleTrigger t = triggerList[i];
                if (t == null || !IsTriggerCurrentlyAssembled(t)) continue;

                int partHash = 0;
                if (t.PartVar != null && t.PartVar.Value != null)
                {
                    partHash = SceneHasher.GetHash(t.PartVar.Value);
                }
                else if (t.LastAssembledPartHash != 0)
                {
                    partHash = t.LastAssembledPartHash;
                }
                assembledSlots.Add(new KeyValuePair<int, int>(t.Hash, partHash));
            }

            var packet = new PacketWriter(PacketType.PartAssembleSnapshot);
            packet.WriteInt32(LocalPlayerId);

            packet.WriteInt32(unmountedParts.Count);
            for (int i = 0; i < unmountedParts.Count; i++)
            {
                packet.WriteInt32(unmountedParts[i]);
            }

            packet.WriteInt32(assembledSlots.Count);
            for (int i = 0; i < assembledSlots.Count; i++)
            {
                packet.WriteInt32(assembledSlots[i].Key);
                packet.WriteInt32(assembledSlots[i].Value);
            }

            var nonTuneBolts = new List<TrackedBolt>();
            for (int i = 0; i < boltList.Count; i++)
            {
                TrackedBolt b = boltList[i];
                if (b == null || b.IsTuneBolt) continue;
                nonTuneBolts.Add(b);
            }

            packet.WriteInt32(nonTuneBolts.Count);
            for (int i = 0; i < nonTuneBolts.Count; i++)
            {
                TrackedBolt b = nonTuneBolts[i];
                byte stage = b.CurrentStage;
                b.LastSyncedStage = stage;
                b.LastDigestStage = stage;
                packet.WriteInt32(b.Hash);
                packet.WriteByte(stage);
            }

            return packet;
        }

        private void BroadcastFullSnapshot()
        {
            if (!IsNetworkRunning) return;
            using (PacketWriter packet = BuildFullSnapshotPacket())
            {
                ctx.Transport.Send(packet);
            }
        }

        private void SendSnapshotToClient(int targetClientId)
        {
            if (ctx == null || ctx.Transport == null || !ctx.Transport.IsServer) return;
            using (PacketWriter packet = BuildFullSnapshotPacket())
            {
                ctx.Transport.SendTo(packet, targetClientId);
            }
        }

        private void ProcessPendingSnapshots()
        {
            if (ctx == null || ctx.Transport == null || !ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            float now = Time.time;
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    SendSnapshotToClient(pendingSnapshots[i].ClientId);
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        private void ProcessPendingParts()
        {
            if (pendingParts.Count == 0) return;

            float now = Time.time;
            for (int i = pendingParts.Count - 1; i >= 0; i--)
            {
                PendingAssemblePart pending = pendingParts[i];
                if (now < pending.RetryTime) continue;

                bool success = TryApplyPartAssemble(pending.TriggerHash, pending.PartHash, true);
                if (success || pending.RetryCount >= 30)
                {
                    pendingParts.RemoveAt(i);
                }
                else
                {
                    pending.RetryCount++;
                    pending.RetryTime = now + 0.1f;
                    pendingParts[i] = pending;
                }
            }
        }

        private void ProcessPendingBolts()
        {
            if (pendingBolts.Count == 0) return;

            float now = Time.time;
            for (int i = pendingBolts.Count - 1; i >= 0; i--)
            {
                PendingBoltApply pending = pendingBolts[i];
                if (now < pending.RetryTime) continue;

                bool ready = bolts.TryGetValue(pending.BoltHash, out TrackedBolt bolt) &&
                             bolt.Fsm != null &&
                             bolt.Fsm.gameObject != null &&
                             bolt.Fsm.gameObject.activeInHierarchy;

                if (ready)
                {
                    ApplyRemoteBoltScrew(pending.BoltHash, pending.Stage);
                    pendingBolts.RemoveAt(i);
                }
                else if (pending.RetryCount >= 30)
                {
                    pendingBolts.RemoveAt(i);
                }
                else
                {
                    pending.RetryCount++;
                    pending.RetryTime = now + 0.1f;
                    pendingBolts[i] = pending;
                }
            }
        }

        private void ApplyRemoteBoltScrew(int boltHash, byte stage)
        {
            if (!bolts.TryGetValue(boltHash, out TrackedBolt bolt) || bolt.Fsm == null) return;

            bolt.SuppressEcho = true;
            bolt.SuppressEchoUntil = Time.time + 0.15f;
            suppressedBolts.Add(boltHash);

            try
            {
                bool fsmActive = bolt.Fsm.gameObject != null && bolt.Fsm.gameObject.activeInHierarchy && bolt.Fsm.enabled;

                if (bolt.IsTuneBolt)
                {
                    // Непрерывный тюнинг (Alignment) синхронизирует SatsumaTuningAndFluids без 8-битного квантования
                    return;
                }
                else if (bolt.IsScrewableLid)
                {
                    if (bolt.RotVar != null)
                    {
                        float targetAngle = (float)stage / 255f * 360f;
                        if (fsmActive)
                        {
                            bool isScrew = targetAngle > bolt.RotVar.Value;
                            bolt.RotVar.Value = isScrew ? (targetAngle - bolt.RotAmount) : (targetAngle + bolt.RotAmount);
                            if (!isScrew && targetAngle < 5f)
                            {
                                bolt.RotVar.Value = bolt.RotAmount;
                            }
                            bolt.Fsm.SendEvent(isScrew ? "MP_SCREW" : "MP_UNSCREW");
                        }
                        else
                        {
                            bolt.RotVar.Value = targetAngle;
                        }
                    }
                }
                else if (bolt.IsPin)
                {
                    if (bolt.StageVar != null)
                    {
                        bolt.StageVar.Value = stage;
                    }
                    else if (bolt.TightnessVar != null)
                    {
                        bolt.TightnessVar.Value = stage;
                    }

                    if (fsmActive)
                    {
                        bolt.Fsm.SendEvent(stage > 0 ? "MP_TIGHTEN" : "MP_UNTIGHTEN");
                    }
                }
                else
                {
                    if (bolt.StageVar != null)
                    {
                        bolt.StageVar.Value = stage;
                    }
                    else if (bolt.TightnessVar != null)
                    {
                        bolt.TightnessVar.Value = stage;
                    }

                    if (fsmActive && bolt.StageVar != null && bolt.Fsm.HasState("Wait 3"))
                    {
                        bolt.Fsm.SendEvent("WAIT");
                    }
                }

                byte appliedStage = bolt.CurrentStage;
                bolt.LastSyncedStage = appliedStage;
                bolt.LastDigestStage = appliedStage;
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma Болт] Ошибка применения: {ex.Message}");
            }
            finally
            {
                bolt.SuppressEcho = false;
                suppressedBolts.Remove(boltHash);
            }
        }

        private void ApplyRemotePartRemove(int partHash)
        {
            if (!removalParts.TryGetValue(partHash, out TrackedRemovalPart part) || part.Fsm == null) return;

            part.SuppressEcho = true;
            part.SuppressEchoUntil = Time.time + 0.25f;
            suppressedParts.Add(partHash);

            try
            {
                if (part.Fsm.gameObject != null && part.Fsm.gameObject.activeInHierarchy && part.Fsm.enabled)
                {
                    part.Fsm.SendEvent("MP_REMOVE");
                }

                RestoreRemovedPartPhysics(part.GameObject);
                part.LastKnownAssembled = false;
                part.LastDigestAssembled = false;
                ClearTriggerStateForPart(part.GameObject);

                PublishPartAssembledEvent(
                    partHash,
                    part.GameObject != null ? part.GameObject.name : string.Empty,
                    part.GameObject,
                    false,
                    false);
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma Сборка] Ошибка снятия детали: {ex.Message}");
            }
            finally
            {
                part.SuppressEcho = false;
                suppressedParts.Remove(partHash);
            }
        }

        private void ApplyRemotePartAssemble(int triggerHash, int partHash)
        {
            if (!TryApplyPartAssemble(triggerHash, partHash, false))
            {
                pendingParts.Add(new PendingAssemblePart
                {
                    TriggerHash = triggerHash,
                    PartHash = partHash,
                    RetryTime = Time.time + 0.05f,
                    RetryCount = 0
                });
            }
        }

        private bool TryApplyPartAssemble(int triggerHash, int partHash, bool isRetry)
        {
            if (!assembleTriggers.TryGetValue(triggerHash, out TrackedAssembleTrigger trigger) || trigger.Fsm == null)
            {
                return false;
            }

            if (trigger.GameObject != null &&
                trigger.GameObject.transform.parent != null &&
                !trigger.GameObject.transform.parent.gameObject.activeInHierarchy)
            {
                return false;
            }

            trigger.SuppressEcho = true;
            trigger.SuppressEchoUntil = Time.time + 0.25f;
            suppressedParts.Add(triggerHash);

            try
            {
                FsmFloat attachForce = trigger.Fsm.FsmVariables.FindFsmFloat("InitialAttachForce");
                if (attachForce != null) attachForce.Value = float.MaxValue;

                GameObject partObj = null;
                if (partHash != 0)
                {
                    partObj = SceneHasher.FindObjectByHash(partHash);
                    if (partObj == null)
                    {
                        return false;
                    }

                    if (partObj != null)
                    {
                        PreparePartObjectForRemoteAssemble(partObj);

                        if (trigger.PartVar != null)
                        {
                            trigger.PartVar.Value = partObj;
                        }
                    }
                }

                if (trigger.PartVar != null && trigger.PartVar.Value != null)
                {
                    PreparePartObjectForRemoteAssemble(trigger.PartVar.Value);
                    if (partObj == null)
                    {
                        partObj = trigger.PartVar.Value;
                    }
                }

                if (trigger.GameObject != null && !trigger.GameObject.activeSelf &&
                    (trigger.GameObject.transform.parent == null || trigger.GameObject.transform.parent.gameObject.activeInHierarchy))
                {
                    trigger.GameObject.SetActive(true);
                }

                if (!trigger.Fsm.enabled)
                {
                    trigger.Fsm.enabled = true;
                }

                if (trigger.Fsm.gameObject != null && !trigger.Fsm.gameObject.activeInHierarchy)
                {
                    return false;
                }

                trigger.Fsm.SendEvent(trigger.IsTriggerCharger ? "PLACEBATT" : "MP_ASSEMBLE");

                int resolvedPartHash = partObj != null ? SceneHasher.GetHash(partObj) : partHash;
                trigger.LastKnownAssembled = true;
                trigger.LastDigestAssembled = true;
                trigger.LastAssembledPartHash = resolvedPartHash;

                if (partObj != null && removalPartsByGo.TryGetValue(partObj, out TrackedRemovalPart rem))
                {
                    rem.LastKnownAssembled = true;
                    rem.LastDigestAssembled = true;
                }

                string partKey = partObj != null
                    ? partObj.name
                    : (trigger.GameObject != null ? trigger.GameObject.name : string.Empty);

                PublishPartAssembledEvent(resolvedPartHash, partKey, partObj, true, false);
                return true;
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma Сборка] Ошибка установки детали: {ex.Message}");
                return false;
            }
            finally
            {
                trigger.SuppressEcho = false;
                suppressedParts.Remove(triggerHash);
            }
        }

        private static void PreparePartObjectForRemoteAssemble(GameObject partObj)
        {
            if (partObj == null) return;

            if (!partObj.activeSelf)
            {
                partObj.SetActive(true);
            }

            if (partObj.transform.parent != null && IsHandParentName(partObj.transform.parent.name))
            {
                partObj.transform.SetParent(null, true);
            }

            Rigidbody rb = partObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            Collider[] colls = partObj.GetComponentsInChildren<Collider>(true);
            for (int c = 0; c < colls.Length; c++)
            {
                if (colls[c] != null && !colls[c].isTrigger)
                {
                    colls[c].enabled = false;
                }
            }
        }

        private static void RestoreRemovedPartPhysics(GameObject partObj)
        {
            if (partObj == null) return;

            Collider[] colls = partObj.GetComponentsInChildren<Collider>(true);
            for (int c = 0; c < colls.Length; c++)
            {
                if (colls[c] != null)
                {
                    colls[c].enabled = true;
                }
            }

            Rigidbody rb = partObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                rb.WakeUp();
            }
        }

        private void ClearTriggerStateForPart(GameObject partObj)
        {
            if (partObj == null) return;
            for (int i = 0; i < triggerList.Count; i++)
            {
                TrackedAssembleTrigger t = triggerList[i];
                if (t != null && t.PartVar != null && t.PartVar.Value == partObj)
                {
                    t.LastKnownAssembled = false;
                    t.LastDigestAssembled = false;
                    t.LastAssembledPartHash = 0;
                }
            }
        }

        private void PublishPartAssembledEvent(int partHash, string partKey, GameObject partObject, bool isAssembled, bool isLocalInitiated)
        {
            ctx?.Events?.Publish(new PartAssembledEvent
            {
                PartHash = partHash,
                PartKey = partKey ?? string.Empty,
                PartObject = partObject,
                IsAssembled = isAssembled,
                IsLocalInitiated = isLocalInitiated
            });
        }

        private bool IsRemovalPartAssembled(TrackedRemovalPart part)
        {
            if (part == null || part.GameObject == null || part.Fsm == null) return false;
            if (!part.GameObject.activeInHierarchy || !part.Fsm.enabled) return false;

            Transform parent = part.GameObject.transform.parent;
            if (parent == null || IsHandParentName(parent.name)) return false;

            return true;
        }

        private bool IsTriggerCurrentlyAssembled(TrackedAssembleTrigger trigger)
        {
            if (trigger == null || trigger.Fsm == null) return false;

            if (trigger.InstalledVar != null && trigger.InstalledVar.Value)
            {
                return true;
            }

            if (trigger.PartVar != null)
            {
                GameObject partGo = trigger.PartVar.Value;
                if (partGo == null || !partGo.activeInHierarchy) return false;

                Transform partParent = partGo.transform.parent;
                if (partParent == null || IsHandParentName(partParent.name)) return false;

                if (removalPartsByGo.TryGetValue(partGo, out TrackedRemovalPart rem))
                {
                    return IsRemovalPartAssembled(rem);
                }
                return true;
            }

            if (trigger.IsWiring)
            {
                if (trigger.LastKnownAssembled) return true;
                string activeState = trigger.Fsm.ActiveStateName;
                return !string.IsNullOrEmpty(activeState) &&
                       (activeState == trigger.AssembleState ||
                        activeState == "Sound" ||
                        activeState == "Done" ||
                        activeState == "Assembled");
            }

            return trigger.LastKnownAssembled;
        }

        private static bool IsHandParentName(string name)
        {
            return name == "ItemPivot" ||
                   name == "ItemHolder" ||
                   name == "ToolHolder" ||
                   name == "HandHolder" ||
                   name == "ItemHold";
        }
    }
}
