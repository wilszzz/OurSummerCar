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
    /// Синхронизация NPC-трафика (автобус, Пена на зелёной Fittan, поезд на переезде, Amis, шоссе),
    /// полицейских засад (алкотестер, штрафы, срок в тюрьме) и субботнего ралли (этапы, таймеры, заявки).
    /// Использует PacketType.NpcTrafficAndPoliceSync (134).
    /// </summary>
    internal sealed class NpcTrafficAndPoliceSync
    {
        private enum NpcSlot : byte
        {
            Bus = 0,
            AmisKylajani = 1,
            PenaFittan = 2,
            Train = 3,
            AmisKuski = 4,
            HighwayBase = 10,
            HighwayMax = 253
        }

        private sealed class TrackedNpc
        {
            public byte Id;
            public string Name;
            public GameObject GameObject;
            public Transform Transform;
            public Rigidbody Rigidbody;
            public bool OriginallyKinematic;
            public bool KinematicForced;
            public bool ClientSuppressed;
            public readonly List<PlayMakerFSM> DisabledFsms = new List<PlayMakerFSM>();

            public bool HasReceivedPose;
            public bool WasActiveOnClient;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation = Quaternion.identity;
            public Vector3 TargetVelocity;
            public float LastPoseTime;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private const float ScanIntervalSeconds = 3.0f;
        private const float TrafficBroadcastIntervalSeconds = 0.08f; // 12.5 Гц
        private const float PollIntervalSeconds = 0.35f;
        private const float JumpSnapDistanceMeters = 12.0f;
        private const float RotateSnapDistanceMeters = 3.5f;
        private const float RotateSnapAngleDegrees = 95.0f;
        private const float ClientLerpSpeed = 16.0f;
        private const float ClientMoveTowardsSpeed = 45.0f;
        private const float ExtrapolationLimitSeconds = 0.14f;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly Dictionary<byte, TrackedNpc> trackedNpcs = new Dictionary<byte, TrackedNpc>();
        private readonly List<TrackedNpc> scratchActiveNpcs = new List<TrackedNpc>();
        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // Шоссейный трафик
        private GameObject vehiclesHighwayGo;
        private bool lastHighwayActive;

        // Автобус: маршрут и стоимость билета
        private PlayMakerFSM busRouteFsm;
        private PlayMakerFSM busTicketFsm;
        private PlayMakerFSM busDriverFsm;
        private FsmInt busStopIndexVar;
        private FsmFloat busFareVar;
        private FsmBool busDoorOpenVar;
        private int currentBusStopIndex = -1;
        private float currentBusFare = 25.0f;
        private bool currentBusDoorOpen;
        private int totalBusTicketsBought;

        // Поезд: гудок и переезд
        private PlayMakerFSM trainWhistleFsm;
        private PlayMakerFSM trainCrossingFsm;
        private bool lastCrossingClosed;
        private float lastTrainWhistleSentTime = -10f;

        // Полиция, алкотестер, штрафы, тюрьма
        private PlayMakerFSM policeSpawnFsm;
        private FsmEvent[] policeClientEvents;
        private int currentPoliceCheckpointIndex = -1;
        private bool currentPoliceActive;

        private PlayMakerFSM alcometerFsm;
        private PlayMakerFSM spikeStripFsm;
        private FsmFloat playerDrunkVar;
        private bool lastSpikeStripDeployed;
        private float lastAlcoBlowTime = -10f;

        private GameObject inJailFlagObj;
        private PlayMakerFSM jailSentenceFsm;
        private PlayMakerFSM guardSpeakFsm;
        private FsmInt globalFinesCountVar;
        private FsmFloat globalFineAmountVar;
        private FsmInt jailDaysVar;
        private int syncedJailDaysRemaining;
        private bool syncedInJail;
        private int syncedFineCount;
        private float syncedFineAmount;
        private string lastFineReason = string.Empty;

        // Субботнее ралли
        private PlayMakerFSM rallyRegistrationFsm;
        private PlayMakerFSM rallyTimerFsm;
        private FsmBool rallyRegisteredVar;
        private FsmFloat rallyStageTimeVar;
        private FsmFloat rallyTotalTimeVar;
        private FsmInt rallyStageIndexVar;
        private int currentRallyStageIndex;
        private string currentRallyStageName = string.Empty;
        private float currentRallyStageTime;
        private float currentRallyTotalTime;
        private bool currentRallyRegistered;
        private bool currentRallyRunning;
        private bool currentRallyFinished;

        // Подавление эха и таймеры
        private bool isSuppressingBus;
        private bool isSuppressingTrain;
        private bool isSuppressingPolice;
        private bool isSuppressingJail;
        private bool isSuppressingRally;

        private float nextScanTime;
        private float nextBroadcastTime;
        private float nextPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private bool IsServer => ctx != null && ctx.Transport != null && ctx.Transport.IsServer;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary
        {
            get
            {
                int activeCount = 0;
                foreach (var kv in trackedNpcs)
                {
                    if (kv.Value != null && kv.Value.GameObject != null && kv.Value.GameObject.activeInHierarchy)
                    {
                        activeCount++;
                    }
                }
                return $"NPC: {activeCount}/{trackedNpcs.Count}, Полиция: {(currentPoliceActive ? "#" + currentPoliceCheckpointIndex : "Нет")}, Тюрьма: {syncedJailDaysRemaining}д, Ралли: {(currentRallyRunning ? $"Ст.{currentRallyStageIndex}" : "Ожидание")}";
            }
        }

        public NpcTrafficAndPoliceSync(OscHostContext context, string moduleId)
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
            RestoreClientNpcStates();

            trackedNpcs.Clear();
            scratchActiveNpcs.Clear();
            hookedFsmIds.Clear();
            pendingSnapshots.Clear();

            vehiclesHighwayGo = null;
            lastHighwayActive = false;

            busRouteFsm = null;
            busTicketFsm = null;
            busDriverFsm = null;
            busStopIndexVar = null;
            busFareVar = null;
            busDoorOpenVar = null;
            currentBusStopIndex = -1;
            currentBusFare = 25.0f;
            currentBusDoorOpen = false;
            totalBusTicketsBought = 0;

            trainWhistleFsm = null;
            trainCrossingFsm = null;
            lastCrossingClosed = false;
            lastTrainWhistleSentTime = -10f;

            policeSpawnFsm = null;
            policeClientEvents = null;
            currentPoliceCheckpointIndex = -1;
            currentPoliceActive = false;

            alcometerFsm = null;
            spikeStripFsm = null;
            playerDrunkVar = null;
            lastSpikeStripDeployed = false;
            lastAlcoBlowTime = -10f;

            inJailFlagObj = null;
            jailSentenceFsm = null;
            guardSpeakFsm = null;
            globalFinesCountVar = null;
            globalFineAmountVar = null;
            jailDaysVar = null;
            syncedJailDaysRemaining = 0;
            syncedInJail = false;
            syncedFineCount = 0;
            syncedFineAmount = 0f;
            lastFineReason = string.Empty;

            rallyRegistrationFsm = null;
            rallyTimerFsm = null;
            rallyRegisteredVar = null;
            rallyStageTimeVar = null;
            rallyTotalTimeVar = null;
            rallyStageIndexVar = null;
            currentRallyStageIndex = 0;
            currentRallyStageName = string.Empty;
            currentRallyStageTime = 0f;
            currentRallyTotalTime = 0f;
            currentRallyRegistered = false;
            currentRallyRunning = false;
            currentRallyFinished = false;

            isSuppressingBus = false;
            isSuppressingTrain = false;
            isSuppressingPolice = false;
            isSuppressingJail = false;
            isSuppressingRally = false;

            nextScanTime = 0f;
            nextBroadcastTime = 0f;
            nextPollTime = 0f;
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

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

            if (!IsServer)
            {
                RestoreClientNpcStates();
            }
        }

        public void OnUpdate()
        {
            float now = Time.time;

            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanIntervalSeconds;
                ScanAll();
            }

            if (!IsNetworkRunning) return;

            if (IsServer)
            {
                ProcessPendingSnapshots(now);

                if (now >= nextBroadcastTime)
                {
                    nextBroadcastTime = now + TrafficBroadcastIntervalSeconds;
                    BroadcastHostTraffic();
                }

                if (now >= nextPollTime)
                {
                    nextPollTime = now + PollIntervalSeconds;
                    PollHostWorldStates();
                }
            }
            else
            {
                EnsureClientTrafficSuppressed();
                InterpolateClientNonKinematicNpcs(Time.deltaTime, now);

                if (now >= nextPollTime)
                {
                    nextPollTime = now + PollIntervalSeconds;
                    PollClientLocalEvents();
                }
            }
        }

        public void OnFixedUpdate()
        {
            if (!IsNetworkRunning || IsServer) return;

            float dt = Time.fixedDeltaTime;
            float now = Time.time;

            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc == null || npc.GameObject == null || !npc.GameObject.activeInHierarchy) continue;
                if (!npc.HasReceivedPose) continue;
                if (npc.Rigidbody == null || !npc.Rigidbody.isKinematic) continue;

                StepNpcInterpolation(npc, dt, now, useRigidbodyMove: true);
            }
        }

        #region Discovery & Hooks

        private void ScanAll()
        {
            try
            {
                DiscoverNpcs();
                DiscoverBusAndTrainSystems();
                DiscoverPoliceAndJailSystems();
                DiscoverRallySystems();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[NPC/Полиция] Ошибка сканирования: {ex.Message}");
            }
        }

        private void DiscoverNpcs()
        {
            // 1. Автобус
            if (!IsNpcValid((byte)NpcSlot.Bus))
            {
                GameObject bus = GameObject.Find("BUS") ?? GameObject.Find("Bus") ?? GameObject.Find("bus(270)");
                if (bus != null) RegisterNpc((byte)NpcSlot.Bus, "Автобус", bus);
            }

            // 2. Amis (Kylajani)
            if (!IsNpcValid((byte)NpcSlot.AmisKylajani))
            {
                GameObject amis = GameObject.Find("NPC_CARS/Amikset/KYLAJANI") ?? GameObject.Find("AMIS2") ?? GameObject.Find("Amis");
                if (amis != null) RegisterNpc((byte)NpcSlot.AmisKylajani, "Amis (Kylajani)", amis);
            }

            // 3. Кузен Пена (зелёная Fittan)
            if (!IsNpcValid((byte)NpcSlot.PenaFittan))
            {
                GameObject fittan = GameObject.Find("FITTAN") ?? GameObject.Find("Fittan");
                if (fittan != null) RegisterNpc((byte)NpcSlot.PenaFittan, "Пена (Fittan)", fittan);
            }

            // 4. Поезд на переезде
            if (!IsNpcValid((byte)NpcSlot.Train))
            {
                GameObject train = GameObject.Find("TRAIN") ?? GameObject.Find("Train");
                if (train != null) RegisterNpc((byte)NpcSlot.Train, "Поезд", train);
            }

            // 5. Второй Amis (Kuski)
            if (!IsNpcValid((byte)NpcSlot.AmisKuski))
            {
                GameObject kuski = GameObject.Find("NPC_CARS/Amikset/KUSKI") ?? GameObject.Find("AMIS1");
                if (kuski != null) RegisterNpc((byte)NpcSlot.AmisKuski, "Amis (Kuski)", kuski);
            }

            // 6. Шоссейный трафик и контроллер полиции
            if (vehiclesHighwayGo == null || policeSpawnFsm == null)
            {
                GameObject traffic = GameObject.Find("TRAFFIC");
                if (traffic != null)
                {
                    if (vehiclesHighwayGo == null)
                    {
                        Transform vh = traffic.transform.Find("VehiclesHighway");
                        if (vh != null) vehiclesHighwayGo = vh.gameObject;
                    }

                    if (policeSpawnFsm == null)
                    {
                        Transform pol = traffic.transform.Find("Police");
                        if (pol != null)
                        {
                            HookPoliceSpawnFsm(pol.GetComponent<PlayMakerFSM>());
                        }
                    }
                }
            }

            if (IsNetworkRunning && !IsServer)
            {
                foreach (var kv in trackedNpcs)
                {
                    TrackedNpc npc = kv.Value;
                    if (npc != null && !npc.ClientSuppressed)
                    {
                        ApplyClientNpcSuppression(npc);
                    }
                }

                if (policeSpawnFsm != null && policeClientEvents == null)
                {
                    ConfigurePoliceClientTransitions(policeSpawnFsm);
                }
            }

            if (vehiclesHighwayGo != null)
            {
                int count = vehiclesHighwayGo.transform.childCount;
                for (int i = 0; i < count; i++)
                {
                    int rawId = (int)NpcSlot.HighwayBase + i;
                    if (rawId > (int)NpcSlot.HighwayMax) break;

                    byte id = (byte)rawId;
                    if (!IsNpcValid(id))
                    {
                        GameObject child = vehiclesHighwayGo.transform.GetChild(i).gameObject;
                        RegisterNpc(id, $"Шоссе #{i}", child);
                    }
                }
            }
        }

        private bool IsNpcValid(byte id)
        {
            return trackedNpcs.TryGetValue(id, out TrackedNpc npc) && npc != null && npc.GameObject != null;
        }

        private void RegisterNpc(byte id, string name, GameObject go)
        {
            if (go == null) return;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            var tracked = new TrackedNpc
            {
                Id = id,
                Name = name,
                GameObject = go,
                Transform = go.transform,
                Rigidbody = rb,
                OriginallyKinematic = rb != null && rb.isKinematic
            };

            if (IsNetworkRunning && !IsServer)
            {
                ApplyClientNpcSuppression(tracked);
            }

            trackedNpcs[id] = tracked;
        }

        private void ApplyClientNpcSuppression(TrackedNpc npc)
        {
            if (npc == null || npc.GameObject == null) return;

            PlayMakerFSM[] fsms = npc.GameObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || string.IsNullOrEmpty(fsm.FsmName)) continue;

                // Не отключаем Talk/Smoking (их может использовать StoryAndQuestsSync)
                string fn = fsm.FsmName.ToLower();
                if (fn.Contains("drive") || fn.Contains("route") || fn.Contains("ai") ||
                    fn.Contains("move") || fn.Contains("logic") || fn.Contains("drivetrain"))
                {
                    string owner = ctx != null && ctx.Ownership != null ? ctx.Ownership.GetOwner(fsm) : null;
                    if (!string.IsNullOrEmpty(owner) && owner != moduleId) continue;

                    if (fsm.enabled)
                    {
                        fsm.enabled = false;
                        if (!npc.DisabledFsms.Contains(fsm))
                        {
                            npc.DisabledFsms.Add(fsm);
                        }
                    }
                }
            }

            // Никогда не трогаем MainRigidbody / DriveTrigger или чужой Rigidbody
            if (npc.Rigidbody != null && npc.Rigidbody.gameObject.name != "MainRigidbody")
            {
                string rbOwner = ctx != null && ctx.Ownership != null ? ctx.Ownership.GetOwner(npc.Rigidbody) : null;
                if (string.IsNullOrEmpty(rbOwner) || rbOwner == moduleId)
                {
                    npc.Rigidbody.isKinematic = true;
                    npc.KinematicForced = true;
                }
            }

            npc.ClientSuppressed = true;
        }

        private void RestoreClientNpcStates()
        {
            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc == null) continue;

                for (int i = 0; i < npc.DisabledFsms.Count; i++)
                {
                    PlayMakerFSM fsm = npc.DisabledFsms[i];
                    if (fsm != null)
                    {
                        fsm.enabled = true;
                    }
                }
                npc.DisabledFsms.Clear();

                if (npc.KinematicForced && npc.Rigidbody != null)
                {
                    npc.Rigidbody.isKinematic = npc.OriginallyKinematic;
                    npc.KinematicForced = false;
                }

                npc.ClientSuppressed = false;
            }
        }

        private void DiscoverBusAndTrainSystems()
        {
            if (trackedNpcs.TryGetValue((byte)NpcSlot.Bus, out TrackedNpc busNpc) && busNpc.GameObject != null)
            {
                GameObject busGo = busNpc.GameObject;
                if (busRouteFsm == null)
                {
                    busRouteFsm = FindFsmInChildren(busGo, "Route")
                        ?? FindFsmInChildren(busGo, "Throttle")
                        ?? PlayMakerBridge.FindFsm(busGo, "Route");
                    if (busRouteFsm != null)
                    {
                        busRouteFsm.InitializeSafe();
                        busStopIndexVar = busRouteFsm.FsmVariables.FindFsmInt("Waypoint")
                            ?? busRouteFsm.FsmVariables.FindFsmInt("Stop")
                            ?? busRouteFsm.FsmVariables.FindFsmInt("Route");
                        busDoorOpenVar = busRouteFsm.FsmVariables.FindFsmBool("DoorOpen")
                            ?? busRouteFsm.FsmVariables.FindFsmBool("Open");
                    }
                }

                if (busDriverFsm == null)
                {
                    busDriverFsm = FindFsmInChildren(busGo, "DriverSpeaking")
                        ?? PlayMakerBridge.FindFsm(busGo, "DriverSpeaking");
                }

                if (busTicketFsm == null)
                {
                    busTicketFsm = FindFsmInChildren(busGo, "Ticket")
                        ?? FindFsmInChildren(busGo, "Pay")
                        ?? FindFsmInChildren(busGo, "Buy");

                    if (busTicketFsm != null && TryClaimSafe(busTicketFsm, "BusTicket"))
                    {
                        busTicketFsm.InitializeSafe();
                        busFareVar = busTicketFsm.FsmVariables.FindFsmFloat("Price")
                            ?? busTicketFsm.FsmVariables.FindFsmFloat("Fare")
                            ?? busTicketFsm.FsmVariables.FindFsmFloat("Money");
                        if (busFareVar != null && busFareVar.Value > 0f)
                        {
                            currentBusFare = busFareVar.Value;
                        }

                        HookFsmStateOnce(busTicketFsm, "Buy", OnLocalBusTicketAction);
                        HookFsmStateOnce(busTicketFsm, "Pay", OnLocalBusTicketAction);
                        HookFsmStateOnce(busTicketFsm, "Ticket", OnLocalBusTicketAction);
                    }
                }
            }

            if (trackedNpcs.TryGetValue((byte)NpcSlot.Train, out TrackedNpc trainNpc) && trainNpc.GameObject != null)
            {
                GameObject trainGo = trainNpc.GameObject;
                if (trainWhistleFsm == null)
                {
                    trainWhistleFsm = PlayMakerBridge.FindFsm(trainGo, "Whistle") ?? FindFsmInChildren(trainGo, "Whistle");
                    if (trainWhistleFsm != null && TryClaimSafe(trainWhistleFsm, "TrainWhistle"))
                    {
                        trainWhistleFsm.InitializeSafe();
                        HookFsmStateOnce(trainWhistleFsm, "State 2", OnHostTrainWhistle);
                    }
                }
            }

            if (trainCrossingFsm == null)
            {
                GameObject crossingGo = GameObject.Find("RailwayCrossing")
                    ?? GameObject.Find("RailCrossing")
                    ?? GameObject.Find("Crossing");
                if (crossingGo != null)
                {
                    trainCrossingFsm = crossingGo.GetComponent<PlayMakerFSM>();
                    if (trainCrossingFsm != null && TryClaimSafe(trainCrossingFsm, "TrainCrossing"))
                    {
                        trainCrossingFsm.InitializeSafe();
                    }
                }
            }
        }

        private void DiscoverPoliceAndJailSystems()
        {
            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                if (playerDrunkVar == null)
                {
                    playerDrunkVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunk");
                }
                if (globalFinesCountVar == null)
                {
                    globalFinesCountVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("NumberOfFines")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmInt("PoliceFines");
                }
                if (globalFineAmountVar == null)
                {
                    globalFineAmountVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("FineAmount")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmFloat("DayFine");
                }
                if (jailDaysVar == null)
                {
                    jailDaysVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("JailDays")
                        ?? PlayMakerGlobals.Instance.Variables.FindFsmInt("JailTime");
                }
            }

            if (alcometerFsm == null)
            {
                GameObject alcoGo = GameObject.Find("AlcometerTrigger") ?? GameObject.Find("CopRadar1") ?? GameObject.Find("Alcometer");
                if (alcoGo != null)
                {
                    PlayMakerFSM candidate = PlayMakerBridge.FindFsm(alcoGo, "Use")
                        ?? PlayMakerBridge.FindFsm(alcoGo, "InitRotate")
                        ?? alcoGo.GetComponent<PlayMakerFSM>();
                    if (candidate != null && TryClaimSafe(candidate, "PoliceAlcometer"))
                    {
                        alcometerFsm = candidate;
                        alcometerFsm.InitializeSafe();
                        HookFsmStateOnce(alcometerFsm, "Blowing", OnLocalAlcometerBlow);
                        HookFsmStateOnce(alcometerFsm, "Blow", OnLocalAlcometerBlow);
                        HookFsmStateOnce(alcometerFsm, "Fine", OnLocalPoliceFineState);
                    }
                }
            }

            if (spikeStripFsm == null)
            {
                GameObject spikeGo = GameObject.Find("SpikeStrip") ?? GameObject.Find("Spikes");
                if (spikeGo != null)
                {
                    PlayMakerFSM candidate = spikeGo.GetComponent<PlayMakerFSM>();
                    if (candidate != null && TryClaimSafe(candidate, "PoliceSpikeStrip"))
                    {
                        spikeStripFsm = candidate;
                        spikeStripFsm.InitializeSafe();
                    }
                }
            }

            // Никогда не выключаем объект-локацию "JAIL", работаем только с точным флагом "InJail" если он есть
            if (inJailFlagObj == null)
            {
                GameObject candidateInJail = GameObject.Find("InJail");
                if (candidateInJail != null && candidateInJail.name == "InJail")
                {
                    inJailFlagObj = candidateInJail;
                }
            }

            if (jailSentenceFsm == null)
            {
                GameObject jailGo = GameObject.Find("Jail") ?? GameObject.Find("jail_door_hatch") ?? GameObject.Find("JAIL");
                if (jailGo != null)
                {
                    PlayMakerFSM candidate = PlayMakerBridge.FindFsm(jailGo, "Sentence")
                        ?? PlayMakerBridge.FindFsm(jailGo, "Time")
                        ?? FindFsmInChildren(jailGo, "Sentence")
                        ?? FindFsmInChildren(jailGo, "Time");
                    if (candidate != null && TryClaimSafe(candidate, "JailSentence"))
                    {
                        jailSentenceFsm = candidate;
                        jailSentenceFsm.InitializeSafe();
                        if (jailDaysVar == null)
                        {
                            jailDaysVar = jailSentenceFsm.FsmVariables.FindFsmInt("Days")
                                ?? jailSentenceFsm.FsmVariables.FindFsmInt("Time");
                        }
                    }
                }
            }

            if (guardSpeakFsm == null)
            {
                GameObject guardGo = GameObject.Find("Guard");
                if (guardGo != null)
                {
                    PlayMakerFSM candidate = PlayMakerBridge.FindFsm(guardGo, "Speak");
                    if (candidate != null && TryClaimSafe(candidate, "JailGuardSpeak"))
                    {
                        guardSpeakFsm = candidate;
                        guardSpeakFsm.InitializeSafe();
                    }
                }
            }
        }

        private void HookPoliceSpawnFsm(PlayMakerFSM fsm)
        {
            if (fsm == null) return;
            if (!TryClaimSafe(fsm, "PoliceSpawn")) return;

            policeSpawnFsm = fsm;
            policeSpawnFsm.InitializeSafe();

            if (!IsNetworkRunning || IsServer)
            {
                HookFsmStateOnce(policeSpawnFsm, "State 3", () => OnHostPoliceCheckpointSpawn(0));
                HookFsmStateOnce(policeSpawnFsm, "State 4", () => OnHostPoliceCheckpointSpawn(1));
                HookFsmStateOnce(policeSpawnFsm, "State 5", () => OnHostPoliceCheckpointSpawn(2));
                HookFsmStateOnce(policeSpawnFsm, "State 6", () => OnHostPoliceCheckpointSpawn(3));
            }
            else
            {
                ConfigurePoliceClientTransitions(policeSpawnFsm);
            }
        }

        private void ConfigurePoliceClientTransitions(PlayMakerFSM fsm)
        {
            if (fsm == null || policeClientEvents != null) return;

            policeClientEvents = new FsmEvent[]
            {
                fsm.AddSafeEvent("MP_SPAWN0"),
                fsm.AddSafeEvent("MP_SPAWN1"),
                fsm.AddSafeEvent("MP_SPAWN2"),
                fsm.AddSafeEvent("MP_SPAWN3")
            };

            fsm.AddSafeGlobalTransition(policeClientEvents[0], "State 3");
            fsm.AddSafeGlobalTransition(policeClientEvents[1], "State 4");
            fsm.AddSafeGlobalTransition(policeClientEvents[2], "State 5");
            fsm.AddSafeGlobalTransition(policeClientEvents[3], "State 6");

            FsmState cop1State = fsm.GetState("Cop1");
            if (cop1State != null && cop1State.Actions != null && cop1State.Actions.Length > 2 && cop1State.Actions[2] != null)
            {
                cop1State.Actions[2].Enabled = false;
            }

            FsmState state1 = fsm.GetState("State 1");
            if (state1 != null && state1.Actions != null && state1.Actions.Length > 0 && state1.Actions[0] != null)
            {
                state1.Actions[0].Enabled = false;
            }
        }

        private void DiscoverRallySystems()
        {
            if (rallyRegistrationFsm == null || rallyTimerFsm == null)
            {
                GameObject rallyGo = GameObject.Find("Rally") ?? GameObject.Find("RallyRegistration") ?? GameObject.Find("RALLY");
                if (rallyGo != null)
                {
                    if (rallyRegistrationFsm == null)
                    {
                        PlayMakerFSM reg = PlayMakerBridge.FindFsm(rallyGo, "Registration")
                            ?? PlayMakerBridge.FindFsm(rallyGo, "Rally")
                            ?? FindFsmInChildren(rallyGo, "Registration")
                            ?? rallyGo.GetComponent<PlayMakerFSM>();
                        if (reg != null && TryClaimSafe(reg, "RallyRegistration"))
                        {
                            rallyRegistrationFsm = reg;
                            rallyRegistrationFsm.InitializeSafe();
                            rallyRegisteredVar = rallyRegistrationFsm.FsmVariables.FindFsmBool("Registered")
                                ?? rallyRegistrationFsm.FsmVariables.FindFsmBool("SignedUp")
                                ?? rallyRegistrationFsm.FsmVariables.FindFsmBool("Active");

                            HookFsmStateOnce(rallyRegistrationFsm, "Register", OnLocalRallyStateChanged);
                            HookFsmStateOnce(rallyRegistrationFsm, "Registered", OnLocalRallyStateChanged);
                            HookFsmStateOnce(rallyRegistrationFsm, "Start", OnLocalRallyStateChanged);
                        }
                    }

                    if (rallyTimerFsm == null)
                    {
                        PlayMakerFSM timer = PlayMakerBridge.FindFsm(rallyGo, "Timer")
                            ?? PlayMakerBridge.FindFsm(rallyGo, "Stage")
                            ?? FindFsmInChildren(rallyGo, "Timer")
                            ?? FindFsmInChildren(rallyGo, "Stage");
                        if (timer != null && TryClaimSafe(timer, "RallyTimer"))
                        {
                            rallyTimerFsm = timer;
                            rallyTimerFsm.InitializeSafe();
                            rallyStageTimeVar = rallyTimerFsm.FsmVariables.FindFsmFloat("Time")
                                ?? rallyTimerFsm.FsmVariables.FindFsmFloat("StageTime");
                            rallyTotalTimeVar = rallyTimerFsm.FsmVariables.FindFsmFloat("TotalTime")
                                ?? rallyTimerFsm.FsmVariables.FindFsmFloat("Total");
                            rallyStageIndexVar = rallyTimerFsm.FsmVariables.FindFsmInt("Stage")
                                ?? rallyTimerFsm.FsmVariables.FindFsmInt("StageIndex");

                            HookFsmStateOnce(rallyTimerFsm, "Start", OnLocalRallyStateChanged);
                            HookFsmStateOnce(rallyTimerFsm, "Finish", OnLocalRallyFinished);
                            HookFsmStateOnce(rallyTimerFsm, "Finished", OnLocalRallyFinished);
                        }
                    }
                }
            }
        }

        #endregion

        #region Host Broadcast & Polling

        private void EnsureClientTrafficSuppressed()
        {
            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc != null && !npc.ClientSuppressed)
                {
                    ApplyClientNpcSuppression(npc);
                }
            }

            if (policeSpawnFsm != null && policeClientEvents == null)
            {
                ConfigurePoliceClientTransitions(policeSpawnFsm);
            }
        }

        private void BroadcastHostTraffic()
        {
            if (vehiclesHighwayGo != null)
            {
                bool hwActive = vehiclesHighwayGo.activeSelf;
                if (hwActive != lastHighwayActive)
                {
                    lastHighwayActive = hwActive;
                    SendHighwayToggle(hwActive, targetClientId: -1);
                }
            }

            scratchActiveNpcs.Clear();
            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc != null && npc.GameObject != null && npc.GameObject.activeInHierarchy)
                {
                    scratchActiveNpcs.Add(npc);
                }
            }

            if (scratchActiveNpcs.Count == 0) return;

            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.NpcVehiclePose);
                packet.WriteBoolean(vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf);
                packet.WriteByte((byte)scratchActiveNpcs.Count);

                for (int i = 0; i < scratchActiveNpcs.Count; i++)
                {
                    TrackedNpc npc = scratchActiveNpcs[i];
                    packet.WriteByte(npc.Id);
                    packet.WriteBoolean(npc.GameObject.activeSelf);
                    packet.WriteVector3(npc.Transform.position);
                    packet.WriteQuaternion(npc.Transform.rotation);

                    Vector3 vel = (npc.Rigidbody != null && !npc.Rigidbody.isKinematic)
                        ? npc.Rigidbody.velocity
                        : Vector3.zero;
                    packet.WriteVector3(vel);
                }

                // Дополнительные метаданные автобуса (маршрут и стоимость билета)
                packet.WriteInt32(currentBusStopIndex);
                packet.WriteSingle(currentBusFare);
                packet.WriteBoolean(currentBusDoorOpen);
                packet.WriteInt32(totalBusTicketsBought);

                ctx.Transport.Send(packet);
            }
        }

        private void PollHostWorldStates()
        {
            // Автобус: смена остановки/двери/тарифа
            if (busRouteFsm != null)
            {
                int stopIdx = busStopIndexVar != null ? busStopIndexVar.Value : currentBusStopIndex;
                float fare = busFareVar != null && busFareVar.Value > 0f ? busFareVar.Value : currentBusFare;
                bool doorOpen = busDoorOpenVar != null && busDoorOpenVar.Value;

                if (stopIdx != currentBusStopIndex || Math.Abs(fare - currentBusFare) > 0.05f || doorOpen != currentBusDoorOpen)
                {
                    currentBusStopIndex = stopIdx;
                    currentBusFare = fare;
                    currentBusDoorOpen = doorOpen;
                }
            }

            // Переезд поезда
            if (trainCrossingFsm != null && !isSuppressingTrain)
            {
                string st = trainCrossingFsm.ActiveStateName ?? string.Empty;
                bool closed = st.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              st.IndexOf("Down", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              st.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0;
                if (closed != lastCrossingClosed)
                {
                    lastCrossingClosed = closed;
                    SendTrainEventPacket(isWhistle: false, crossingClosed: closed);
                }
            }

            PollSharedPoliceJailAndRally();
        }

        private void PollClientLocalEvents()
        {
            PollSharedPoliceJailAndRally();
        }

        private void PollSharedPoliceJailAndRally()
        {
            // Полоса с шипами
            if (spikeStripFsm != null && !isSuppressingPolice)
            {
                string st = spikeStripFsm.ActiveStateName ?? string.Empty;
                bool deployed = st.IndexOf("Active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                st.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                st.IndexOf("On", StringComparison.OrdinalIgnoreCase) >= 0;
                if (deployed != lastSpikeStripDeployed)
                {
                    lastSpikeStripDeployed = deployed;
                    SendPoliceCheckpointState(
                        currentPoliceCheckpointIndex,
                        currentPoliceActive,
                        alcoBlowing: false,
                        bacLevel: GetLocalBac(),
                        spikeDeployed: deployed);
                }
            }

            // Тюрьма и штрафы
            if (!isSuppressingJail)
            {
                bool inJail = (inJailFlagObj != null && inJailFlagObj.activeSelf) ||
                              (jailSentenceFsm != null && !string.IsNullOrEmpty(jailSentenceFsm.ActiveStateName) &&
                               jailSentenceFsm.ActiveStateName.IndexOf("Jail", StringComparison.OrdinalIgnoreCase) >= 0);
                int days = jailDaysVar != null ? jailDaysVar.Value : syncedJailDaysRemaining;
                int fines = globalFinesCountVar != null ? globalFinesCountVar.Value : syncedFineCount;
                float fineAmt = globalFineAmountVar != null ? globalFineAmountVar.Value : syncedFineAmount;

                if (inJail != syncedInJail || days != syncedJailDaysRemaining || fines != syncedFineCount || Math.Abs(fineAmt - syncedFineAmount) > 0.5f)
                {
                    syncedInJail = inJail;
                    syncedJailDaysRemaining = days;
                    syncedFineCount = fines;
                    syncedFineAmount = fineAmt;
                    SendJailAndFinesPacket(inJail, days, fines, fineAmt, inJail ? "Заключение" : "Штраф/Освобождение");
                }
            }

            // Субботнее ралли
            if (!isSuppressingRally && (rallyRegistrationFsm != null || rallyTimerFsm != null))
            {
                bool reg = rallyRegisteredVar != null ? rallyRegisteredVar.Value : currentRallyRegistered;
                int stageIdx = rallyStageIndexVar != null ? rallyStageIndexVar.Value : currentRallyStageIndex;
                float stageTime = rallyStageTimeVar != null ? rallyStageTimeVar.Value : currentRallyStageTime;
                float totalTime = rallyTotalTimeVar != null ? rallyTotalTimeVar.Value : currentRallyTotalTime;
                string stageName = rallyTimerFsm != null ? (rallyTimerFsm.ActiveStateName ?? string.Empty) : currentRallyStageName;
                bool running = rallyTimerFsm != null && rallyTimerFsm.enabled &&
                               !string.IsNullOrEmpty(stageName) &&
                               stageName.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) < 0 &&
                               stageName.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) < 0 &&
                               stageName.IndexOf("Finish", StringComparison.OrdinalIgnoreCase) < 0;
                bool finished = !string.IsNullOrEmpty(stageName) && stageName.IndexOf("Finish", StringComparison.OrdinalIgnoreCase) >= 0;

                if (reg != currentRallyRegistered || stageIdx != currentRallyStageIndex ||
                    running != currentRallyRunning || finished != currentRallyFinished ||
                    (running && Math.Abs(stageTime - currentRallyStageTime) >= 1.0f))
                {
                    currentRallyRegistered = reg;
                    currentRallyStageIndex = stageIdx;
                    currentRallyStageName = stageName;
                    currentRallyStageTime = stageTime;
                    currentRallyTotalTime = totalTime;
                    currentRallyRunning = running;
                    currentRallyFinished = finished;

                    SendRallyStagePacket();
                }
            }
        }

        private void ProcessPendingSnapshots(float now)
        {
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    int clientId = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    SendSnapshotToClient(clientId);
                }
            }
        }

        #endregion

        #region Client Interpolation & Snap Guard

        private void InterpolateClientNonKinematicNpcs(float dt, float now)
        {
            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc == null || npc.GameObject == null || !npc.GameObject.activeInHierarchy) continue;
                if (!npc.HasReceivedPose) continue;
                if (npc.Rigidbody != null && npc.Rigidbody.isKinematic) continue;

                StepNpcInterpolation(npc, dt, now, useRigidbodyMove: false);
            }
        }

        private static void StepNpcInterpolation(TrackedNpc npc, float dt, float now, bool useRigidbodyMove)
        {
            float age = now - npc.LastPoseTime;
            Vector3 desiredPos = npc.TargetPosition;
            if (age > 0f && age <= ExtrapolationLimitSeconds && npc.TargetVelocity.sqrMagnitude > 0.01f)
            {
                desiredPos += npc.TargetVelocity * age;
            }

            Vector3 currentPos = npc.Transform.position;
            Quaternion currentRot = npc.Transform.rotation;

            float dist = Vector3.Distance(currentPos, desiredPos);
            float angle = Quaternion.Angle(currentRot, npc.TargetRotation);

            // Защита от рывка телепортации
            if (dist > JumpSnapDistanceMeters || (dist > RotateSnapDistanceMeters && angle > RotateSnapAngleDegrees))
            {
                SnapNpcTransform(npc, desiredPos, npc.TargetRotation);
                return;
            }

            float moveSpeed = Mathf.Max(ClientMoveTowardsSpeed, npc.TargetVelocity.magnitude * 1.35f);
            Vector3 nextPos = Vector3.MoveTowards(currentPos, desiredPos, moveSpeed * dt);
            Quaternion nextRot = Quaternion.Slerp(currentRot, npc.TargetRotation, Mathf.Clamp01(ClientLerpSpeed * dt));

            if (useRigidbodyMove && npc.Rigidbody != null)
            {
                npc.Rigidbody.MovePosition(nextPos);
                npc.Rigidbody.MoveRotation(nextRot);
            }
            else
            {
                npc.Transform.position = nextPos;
                npc.Transform.rotation = nextRot;
            }
        }

        private static void SnapNpcTransform(TrackedNpc npc, Vector3 pos, Quaternion rot)
        {
            npc.Transform.position = pos;
            npc.Transform.rotation = rot;
            if (npc.Rigidbody != null && npc.Rigidbody.isKinematic)
            {
                npc.Rigidbody.position = pos;
                npc.Rigidbody.rotation = rot;
            }
        }

        #endregion

        #region Local Hook Callbacks

        private void OnLocalBusTicketAction()
        {
            if (isSuppressingBus || !IsNetworkRunning) return;

            totalBusTicketsBought++;
            if (busFareVar != null && busFareVar.Value > 0f)
            {
                currentBusFare = busFareVar.Value;
            }

            SendBusEventPacket();
            ctx?.Log?.Invoke($"[Автобус] Пробит билет за {currentBusFare:F0} mk (остановка #{currentBusStopIndex}).");
        }

        private void OnHostTrainWhistle()
        {
            if (isSuppressingTrain || !IsNetworkRunning || !IsServer) return;
            if (Time.time - lastTrainWhistleSentTime < 1.5f) return;

            lastTrainWhistleSentTime = Time.time;
            SendTrainEventPacket(isWhistle: true, crossingClosed: lastCrossingClosed);
        }

        private void OnHostPoliceCheckpointSpawn(int index)
        {
            if (isSuppressingPolice || !IsNetworkRunning || !IsServer) return;

            currentPoliceCheckpointIndex = index;
            currentPoliceActive = true;
            SendPoliceCheckpointState(index, true, alcoBlowing: false, bacLevel: 0f, spikeDeployed: lastSpikeStripDeployed);
            ctx?.Log?.Invoke($"[Полиция] Засада активирована в точке #{index}.");
        }

        private void OnLocalAlcometerBlow()
        {
            if (isSuppressingPolice || !IsNetworkRunning) return;
            if (Time.time - lastAlcoBlowTime < 1.5f) return;

            lastAlcoBlowTime = Time.time;
            float bac = GetLocalBac();
            SendPoliceCheckpointState(
                currentPoliceCheckpointIndex,
                currentPoliceActive,
                alcoBlowing: true,
                bacLevel: bac,
                spikeDeployed: lastSpikeStripDeployed);
            ctx?.Log?.Invoke($"[Полиция] Пройден тест алкотестера (алкоголь: {bac:F2}).");
        }

        private void OnLocalPoliceFineState()
        {
            if (isSuppressingJail || !IsNetworkRunning) return;

            int fines = globalFinesCountVar != null ? globalFinesCountVar.Value + 1 : syncedFineCount + 1;
            float amt = globalFineAmountVar != null && globalFineAmountVar.Value > 0f ? globalFineAmountVar.Value : 500f;
            syncedFineCount = fines;
            syncedFineAmount = amt;
            lastFineReason = "Нарушение ПДД / алкотестер";
            SendJailAndFinesPacket(syncedInJail, syncedJailDaysRemaining, fines, amt, lastFineReason);
        }

        private void OnLocalRallyStateChanged()
        {
            if (isSuppressingRally || !IsNetworkRunning) return;

            if (rallyRegisteredVar != null) currentRallyRegistered = rallyRegisteredVar.Value;
            if (rallyStageIndexVar != null) currentRallyStageIndex = rallyStageIndexVar.Value;
            if (rallyStageTimeVar != null) currentRallyStageTime = rallyStageTimeVar.Value;
            if (rallyTotalTimeVar != null) currentRallyTotalTime = rallyTotalTimeVar.Value;
            if (rallyTimerFsm != null) currentRallyStageName = rallyTimerFsm.ActiveStateName ?? string.Empty;
            currentRallyRunning = true;
            currentRallyFinished = false;

            SendRallyStagePacket();
        }

        private void OnLocalRallyFinished()
        {
            if (isSuppressingRally || !IsNetworkRunning) return;

            if (rallyStageIndexVar != null) currentRallyStageIndex = rallyStageIndexVar.Value;
            if (rallyStageTimeVar != null) currentRallyStageTime = rallyStageTimeVar.Value;
            if (rallyTotalTimeVar != null) currentRallyTotalTime = rallyTotalTimeVar.Value;
            currentRallyStageName = "Finish";
            currentRallyRunning = false;
            currentRallyFinished = true;

            SendRallyStagePacket();
            ctx?.Log?.Invoke($"[Ралли] Финиш спецучастка #{currentRallyStageIndex}: {currentRallyStageTime:F2}с (всего: {currentRallyTotalTime:F2}с).");
        }

        #endregion

        #region Packet Senders

        private void SendHighwayToggle(bool hwActive, int targetClientId)
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.NpcVehiclePose);
                packet.WriteBoolean(hwActive);
                packet.WriteByte(0); // Только флаг шоссе без поз
                packet.WriteInt32(currentBusStopIndex);
                packet.WriteSingle(currentBusFare);
                packet.WriteBoolean(currentBusDoorOpen);
                packet.WriteInt32(totalBusTicketsBought);

                if (targetClientId >= 0) ctx.Transport.SendTo(packet, targetClientId);
                else ctx.Transport.Send(packet);
            }
        }

        private void SendBusEventPacket()
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.NpcVehiclePose);
                packet.WriteBoolean(vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf);
                packet.WriteByte(0);
                packet.WriteInt32(currentBusStopIndex);
                packet.WriteSingle(currentBusFare);
                packet.WriteBoolean(currentBusDoorOpen);
                packet.WriteInt32(totalBusTicketsBought);
                ctx.Transport.Send(packet);
            }
        }

        private void SendTrainEventPacket(bool isWhistle, bool crossingClosed)
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.PoliceCheckpointState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteBoolean(true); // trainMarker
                packet.WriteBoolean(isWhistle);
                packet.WriteBoolean(crossingClosed);
                ctx.Transport.Send(packet);
            }
        }

        private void SendPoliceCheckpointState(int checkpointIndex, bool active, bool alcoBlowing, float bacLevel, bool spikeDeployed)
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.PoliceCheckpointState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteBoolean(false); // не trainMarker
                packet.WriteInt32(checkpointIndex);
                packet.WriteBoolean(active);
                packet.WriteBoolean(alcoBlowing);
                packet.WriteSingle(bacLevel);
                packet.WriteBoolean(spikeDeployed);
                ctx.Transport.Send(packet);
            }
        }

        private void SendJailAndFinesPacket(bool inJail, int daysRemaining, int fineCount, float fineAmount, string reason)
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.JailAndFinesState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteBoolean(inJail);
                packet.WriteInt32(daysRemaining);
                packet.WriteInt32(fineCount);
                packet.WriteSingle(fineAmount);
                packet.WriteString(reason ?? string.Empty);
                ctx.Transport.Send(packet);
            }
        }

        private void SendRallyStagePacket()
        {
            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.RallyStageState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(currentRallyStageIndex);
                packet.WriteString(currentRallyStageName ?? string.Empty);
                packet.WriteSingle(currentRallyStageTime);
                packet.WriteSingle(currentRallyTotalTime);
                packet.WriteBoolean(currentRallyRegistered);
                packet.WriteBoolean(currentRallyRunning);
                packet.WriteBoolean(currentRallyFinished);
                ctx.Transport.Send(packet);
            }
        }

        private void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

            scratchActiveNpcs.Clear();
            foreach (var kv in trackedNpcs)
            {
                TrackedNpc npc = kv.Value;
                if (npc != null && npc.GameObject != null)
                {
                    scratchActiveNpcs.Add(npc);
                }
            }

            using (var packet = new PacketWriter(PacketType.NpcTrafficAndPoliceSync))
            {
                packet.WriteByte(EconomyProtocol.NpcPoliceSnapshot);
                packet.WriteBoolean(vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf);

                // NPC позы
                int count = Mathf.Min(scratchActiveNpcs.Count, 250);
                packet.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    TrackedNpc npc = scratchActiveNpcs[i];
                    packet.WriteByte(npc.Id);
                    packet.WriteBoolean(npc.GameObject.activeSelf);
                    packet.WriteVector3(npc.Transform.position);
                    packet.WriteQuaternion(npc.Transform.rotation);
                    Vector3 vel = (npc.Rigidbody != null && !npc.Rigidbody.isKinematic)
                        ? npc.Rigidbody.velocity
                        : Vector3.zero;
                    packet.WriteVector3(vel);
                }

                // Автобус и поезд
                packet.WriteInt32(currentBusStopIndex);
                packet.WriteSingle(currentBusFare);
                packet.WriteBoolean(currentBusDoorOpen);
                packet.WriteInt32(totalBusTicketsBought);
                packet.WriteBoolean(lastCrossingClosed);

                // Полиция и тюрьма
                packet.WriteInt32(currentPoliceCheckpointIndex);
                packet.WriteBoolean(currentPoliceActive);
                packet.WriteBoolean(lastSpikeStripDeployed);
                packet.WriteBoolean(syncedInJail);
                packet.WriteInt32(syncedJailDaysRemaining);
                packet.WriteInt32(syncedFineCount);
                packet.WriteSingle(syncedFineAmount);
                packet.WriteString(lastFineReason ?? string.Empty);

                // Ралли
                packet.WriteInt32(currentRallyStageIndex);
                packet.WriteString(currentRallyStageName ?? string.Empty);
                packet.WriteSingle(currentRallyStageTime);
                packet.WriteSingle(currentRallyTotalTime);
                packet.WriteBoolean(currentRallyRegistered);
                packet.WriteBoolean(currentRallyRunning);
                packet.WriteBoolean(currentRallyFinished);

                ctx.Transport.SendTo(packet, clientId);
            }
        }

        #endregion

        #region Packet Handlers

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subType = reader.ReadByte();
            switch (subType)
            {
                case EconomyProtocol.NpcVehiclePose:
                    HandleNpcVehiclePose(reader);
                    break;

                case EconomyProtocol.PoliceCheckpointState:
                    HandlePoliceCheckpointState(reader);
                    break;

                case EconomyProtocol.JailAndFinesState:
                    HandleJailAndFinesState(reader);
                    break;

                case EconomyProtocol.RallyStageState:
                    HandleRallyStageState(reader);
                    break;

                case EconomyProtocol.NpcPoliceSnapshot:
                    HandleNpcPoliceSnapshot(reader);
                    break;
            }
        }

        private void HandleNpcVehiclePose(PacketReader reader)
        {
            if (reader.UnreadBytes < 2) return;

            bool hwActive = reader.ReadBoolean();
            byte count = reader.ReadByte();

            if (!IsServer && vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf != hwActive)
            {
                vehiclesHighwayGo.SetActive(hwActive);
            }

            if (!IsServer)
            {
                ReadAndApplyNpcPoses(reader, count);
            }
            else
            {
                // На сервере пропускаем позы от клиента, если бы они пришли, и читаем метаданные автобуса
                for (int i = 0; i < count; i++)
                {
                    if (reader.UnreadBytes < 42) return;
                    reader.ReadByte();
                    reader.ReadBoolean();
                    reader.ReadVector3();
                    reader.ReadQuaternion();
                    reader.ReadVector3();
                }
            }

            if (reader.UnreadBytes >= 13)
            {
                int busStop = reader.ReadInt32();
                float busFare = reader.ReadSingle();
                bool busDoor = reader.ReadBoolean();
                int ticketsBought = reader.ReadInt32();

                ApplyBusMetadata(busStop, busFare, busDoor, ticketsBought);
            }
        }

        private void ReadAndApplyNpcPoses(PacketReader reader, int count)
        {
            float now = Time.time;
            for (int i = 0; i < count; i++)
            {
                if (reader.UnreadBytes < 42) break;

                byte id = reader.ReadByte();
                bool active = reader.ReadBoolean();
                Vector3 targetPos = reader.ReadVector3();
                Quaternion targetRot = reader.ReadQuaternion();
                Vector3 vel = reader.ReadVector3();

                if (!trackedNpcs.TryGetValue(id, out TrackedNpc npc) || npc == null || npc.GameObject == null)
                {
                    continue;
                }

                if (npc.GameObject.activeSelf != active)
                {
                    npc.GameObject.SetActive(active);
                }

                if (!active)
                {
                    npc.WasActiveOnClient = false;
                    continue;
                }

                bool shouldSnap = !npc.HasReceivedPose || !npc.WasActiveOnClient;
                npc.WasActiveOnClient = true;
                npc.HasReceivedPose = true;
                npc.TargetPosition = targetPos;
                npc.TargetRotation = targetRot;
                npc.TargetVelocity = vel;
                npc.LastPoseTime = now;

                float dist = Vector3.Distance(npc.Transform.position, targetPos);
                float angle = Quaternion.Angle(npc.Transform.rotation, targetRot);
                if (shouldSnap || dist > JumpSnapDistanceMeters || (dist > RotateSnapDistanceMeters && angle > RotateSnapAngleDegrees))
                {
                    SnapNpcTransform(npc, targetPos, targetRot);
                }
            }
        }

        private void ApplyBusMetadata(int busStop, float busFare, bool busDoor, int ticketsBought)
        {
            isSuppressingBus = true;
            try
            {
                if (ticketsBought > totalBusTicketsBought)
                {
                    totalBusTicketsBought = ticketsBought;
                    if (busDriverFsm != null)
                    {
                        busDriverFsm.SendEvent("SPEAK");
                    }
                    ctx?.Log?.Invoke($"[Автобус] Оплачен проезд в рейсовом автобусе ({busFare:F0} mk).");
                }

                currentBusStopIndex = busStop;
                if (busFare > 0f) currentBusFare = busFare;
                currentBusDoorOpen = busDoor;

                if (!IsServer)
                {
                    if (busStopIndexVar != null && busStop >= 0) busStopIndexVar.Value = busStop;
                    if (busFareVar != null && busFare > 0f) busFareVar.Value = busFare;
                    if (busDoorOpenVar != null) busDoorOpenVar.Value = busDoor;
                }
            }
            finally
            {
                isSuppressingBus = false;
            }
        }

        private void HandlePoliceCheckpointState(PacketReader reader)
        {
            if (reader.UnreadBytes < 5) return;

            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            bool isTrainMarker = reader.ReadBoolean();
            if (isTrainMarker)
            {
                if (reader.UnreadBytes < 2) return;
                bool isWhistle = reader.ReadBoolean();
                bool crossingClosed = reader.ReadBoolean();
                ApplyTrainSync(isWhistle, crossingClosed);
                return;
            }

            if (reader.UnreadBytes < 11) return;
            int checkpointIndex = reader.ReadInt32();
            bool active = reader.ReadBoolean();
            bool alcoBlowing = reader.ReadBoolean();
            float bacLevel = reader.ReadSingle();
            bool spikeDeployed = reader.ReadBoolean();

            ApplyPoliceState(checkpointIndex, active, alcoBlowing, bacLevel, spikeDeployed, logChanges: true);
        }

        private void ApplyTrainSync(bool isWhistle, bool crossingClosed)
        {
            isSuppressingTrain = true;
            try
            {
                if (isWhistle && trainWhistleFsm != null)
                {
                    trainWhistleFsm.SendEvent("WHISTLE");
                    ctx?.Log?.Invoke("[Поезд] Приближающийся поезд подал гудок на переезде!");
                }

                if (crossingClosed != lastCrossingClosed)
                {
                    lastCrossingClosed = crossingClosed;
                    if (trainCrossingFsm != null)
                    {
                        trainCrossingFsm.SendEvent(crossingClosed ? "CLOSE" : "OPEN");
                    }
                }
            }
            finally
            {
                isSuppressingTrain = false;
            }
        }

        private void ApplyPoliceState(int checkpointIndex, bool active, bool alcoBlowing, float bacLevel, bool spikeDeployed, bool logChanges)
        {
            isSuppressingPolice = true;
            try
            {
                if (!IsServer && checkpointIndex != currentPoliceCheckpointIndex &&
                    policeSpawnFsm != null && policeClientEvents != null &&
                    checkpointIndex >= 0 && checkpointIndex < policeClientEvents.Length)
                {
                    policeSpawnFsm.Fsm.Event(policeClientEvents[checkpointIndex]);
                    if (logChanges)
                    {
                        ctx?.Log?.Invoke($"[Полиция] Засада синхронизирована с хостом (позиция #{checkpointIndex}).");
                    }
                }

                currentPoliceCheckpointIndex = checkpointIndex;
                currentPoliceActive = active;

                if (alcoBlowing)
                {
                    if (alcometerFsm != null)
                    {
                        alcometerFsm.SendEvent("BLOW");
                    }
                    if (logChanges)
                    {
                        ctx?.Log?.Invoke($"[Полиция] Напарник дунул в алкотестер (показание: {bacLevel:F2}).");
                    }
                }

                if (spikeDeployed != lastSpikeStripDeployed)
                {
                    lastSpikeStripDeployed = spikeDeployed;
                    if (spikeStripFsm != null)
                    {
                        spikeStripFsm.SendEvent(spikeDeployed ? "DEPLOY" : "RESET");
                    }
                }
            }
            finally
            {
                isSuppressingPolice = false;
            }
        }

        private void HandleJailAndFinesState(PacketReader reader)
        {
            if (reader.UnreadBytes < 18) return;

            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            bool inJail = reader.ReadBoolean();
            int daysRemaining = reader.ReadInt32();
            int fineCount = reader.ReadInt32();
            float fineAmount = reader.ReadSingle();
            string reason = reader.ReadString();

            ApplyJailAndFines(inJail, daysRemaining, fineCount, fineAmount, reason, logChanges: true);
        }

        private void ApplyJailAndFines(bool inJail, int daysRemaining, int fineCount, float fineAmount, string reason, bool logChanges)
        {
            isSuppressingJail = true;
            try
            {
                syncedInJail = inJail;
                syncedJailDaysRemaining = daysRemaining;
                syncedFineCount = fineCount;
                syncedFineAmount = fineAmount;
                lastFineReason = reason ?? string.Empty;

                if (jailDaysVar != null) jailDaysVar.Value = daysRemaining;
                if (globalFinesCountVar != null) globalFinesCountVar.Value = fineCount;
                if (globalFineAmountVar != null) globalFineAmountVar.Value = fineAmount;

                // Осторожно: трогаем только точный флаг InJail, но никогда не объект локации JAIL
                if (inJailFlagObj != null && inJailFlagObj.name == "InJail" && inJailFlagObj.activeSelf != inJail)
                {
                    inJailFlagObj.SetActive(inJail);
                }

                if (inJail && guardSpeakFsm != null)
                {
                    guardSpeakFsm.SendEvent("SPEAK");
                }

                if (logChanges)
                {
                    if (inJail)
                    {
                        ctx?.Log?.Invoke($"[Тюрьма] Напарник отбывает срок: осталось {daysRemaining} дн.");
                    }
                    else if (fineAmount > 0f)
                    {
                        ctx?.Log?.Invoke($"[Полиция] Напарнику выписан штраф {fineAmount:F0} mk ({reason}).");
                    }
                }
            }
            finally
            {
                isSuppressingJail = false;
            }
        }

        private void HandleRallyStageState(PacketReader reader)
        {
            if (reader.UnreadBytes < 20) return;

            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            int stageIndex = reader.ReadInt32();
            string stageName = reader.ReadString();
            float stageTime = reader.ReadSingle();
            float totalTime = reader.ReadSingle();
            bool registered = reader.ReadBoolean();
            bool running = reader.ReadBoolean();
            bool finished = reader.ReadBoolean();

            ApplyRallyState(stageIndex, stageName, stageTime, totalTime, registered, running, finished, logChanges: true);
        }

        private void ApplyRallyState(int stageIndex, string stageName, float stageTime, float totalTime, bool registered, bool running, bool finished, bool logChanges)
        {
            isSuppressingRally = true;
            try
            {
                bool wasFinished = currentRallyFinished;
                currentRallyStageIndex = stageIndex;
                currentRallyStageName = stageName ?? string.Empty;
                currentRallyStageTime = stageTime;
                currentRallyTotalTime = totalTime;
                currentRallyRegistered = registered;
                currentRallyRunning = running;
                currentRallyFinished = finished;

                if (rallyRegisteredVar != null) rallyRegisteredVar.Value = registered;
                if (rallyStageIndexVar != null) rallyStageIndexVar.Value = stageIndex;
                if (rallyStageTimeVar != null) rallyStageTimeVar.Value = stageTime;
                if (rallyTotalTimeVar != null) rallyTotalTimeVar.Value = totalTime;

                if (rallyTimerFsm != null && finished && !wasFinished)
                {
                    rallyTimerFsm.SendEvent("FINISH");
                }

                if (logChanges)
                {
                    if (finished)
                    {
                        ctx?.Log?.Invoke($"[Ралли] Финиш спецучастка #{stageIndex} ({stageName}): {stageTime:F2}с (итого: {totalTime:F2}с).");
                    }
                    else if (running)
                    {
                        ctx?.Log?.Invoke($"[Ралли] Участок #{stageIndex} ({stageName}), время: {stageTime:F2}с.");
                    }
                }
            }
            finally
            {
                isSuppressingRally = false;
            }
        }

        private void HandleNpcPoliceSnapshot(PacketReader reader)
        {
            if (IsServer) return;
            if (reader.UnreadBytes < 2) return;

            bool hwActive = reader.ReadBoolean();
            if (vehiclesHighwayGo != null && vehiclesHighwayGo.activeSelf != hwActive)
            {
                vehiclesHighwayGo.SetActive(hwActive);
            }

            byte npcCount = reader.ReadByte();
            ReadAndApplyNpcPoses(reader, npcCount);

            if (reader.UnreadBytes < 34) return;

            int busStop = reader.ReadInt32();
            float busFare = reader.ReadSingle();
            bool busDoor = reader.ReadBoolean();
            int ticketsBought = reader.ReadInt32();
            bool crossingClosed = reader.ReadBoolean();

            ApplyBusMetadata(busStop, busFare, busDoor, ticketsBought);
            lastCrossingClosed = crossingClosed;

            int cpIndex = reader.ReadInt32();
            bool cpActive = reader.ReadBoolean();
            bool spikeDeployed = reader.ReadBoolean();
            bool inJail = reader.ReadBoolean();
            int jailDays = reader.ReadInt32();
            int fineCount = reader.ReadInt32();
            float fineAmt = reader.ReadSingle();
            string reason = reader.ReadString();

            ApplyPoliceState(cpIndex, cpActive, alcoBlowing: false, bacLevel: 0f, spikeDeployed: spikeDeployed, logChanges: false);
            ApplyJailAndFines(inJail, jailDays, fineCount, fineAmt, reason, logChanges: false);

            if (reader.UnreadBytes >= 16)
            {
                int stageIndex = reader.ReadInt32();
                string stageName = reader.ReadString();
                float stageTime = reader.ReadSingle();
                float totalTime = reader.ReadSingle();
                bool reg = reader.ReadBoolean();
                bool running = reader.ReadBoolean();
                bool finished = reader.ReadBoolean();
                ApplyRallyState(stageIndex, stageName, stageTime, totalTime, reg, running, finished, logChanges: false);
            }

            ctx?.Log?.Invoke("[NPC/Полиция] Принят полный снимок трафика, полиции и ралли от хоста.");
        }

        #endregion

        #region Helpers

        private float GetLocalBac()
        {
            return playerDrunkVar != null ? playerDrunkVar.Value : 0f;
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

        private static PlayMakerFSM FindFsmInChildren(GameObject root, string fsmName)
        {
            if (root == null || string.IsNullOrEmpty(fsmName)) return null;
            PlayMakerFSM[] fsms = root.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                if (fsms[i] != null && fsms[i].FsmName == fsmName)
                {
                    return fsms[i];
                }
            }
            return null;
        }

        #endregion
    }
}
