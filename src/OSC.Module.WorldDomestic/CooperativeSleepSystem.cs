using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldDomestic
{
    /// <summary>
    /// Кооперативный сон (PacketType.PlayerSleepSync = 25):
    /// - перехват всех ванильных триггеров сна + 6 кастомных BoxCollider-кроватей/ковриков с PivotWake (0.9, 0, 0.5);
    /// - взаимное ожидание сна всех подключённых игроков, соло-фолбэк при 0 клиентов, курение в постели (пожар);
    /// - публикация LocalPlayerSleepStateEvent / RemotePlayerSleepStateEvent (горизонтальная поза куклы)
    ///   и WorldTimeForceSyncEvent при промотке ночи;
    /// - безопасная отмена/выход при разрыве соединения партнёра или хоста без софтлока.
    /// </summary>
    internal sealed class CooperativeSleepSystem
    {
        private struct CustomBedDef
        {
            public readonly string Name;
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly Vector3 TriggerSize;

            public CustomBedDef(string name, Vector3 position, float yaw, Vector3 triggerSize)
            {
                Name = name;
                Position = position;
                Yaw = yaw;
                TriggerSize = triggerSize;
            }
        }

        private sealed class BedEntry
        {
            public GameObject TriggerObj;
            public Collider Collider;
            public Transform Pivot;
            public Transform PivotWake;
            public PlayMakerFSM VanillaFsm;
            public bool IsCustom;
        }

        private struct PeerSleepState
        {
            public bool IsLayingDown;
            public bool IsSmoking;
            public Vector3 BedPosition;
            public float BedYaw;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private enum SleepPhase : byte
        {
            None = 0,
            ClosingEyes = 1,
            SleepingWait = 2,
            OpeningEyes = 3
        }

        private static readonly string[] KnownVanillaBedPaths = new string[]
        {
            "YARD/Building/BEDROOM1/LOD_bedroom1/Sleep/SleepTrigger",
            "ITEMS/sofa(itemx)/Sleep/SleepTrigger",
            "seat rear(Clone)/Sleep/SleepTrigger",
            "RCO_RUSCKO12(270)/LOD/Sleep/SleepTrigger",
            "CABIN/LOD/Sleep/SleepTrigger",
            "COTTAGE/LOD/Sleep/SleepTrigger",
            "JAIL/Sleep/SleepTrigger",
            "GIFU(750/450psi)/LOD/Sleep/SleepTrigger"
        };

        private static readonly CustomBedDef[] CustomBedDefs = new CustomBedDef[]
        {
            new CustomBedDef("Диван дома", new Vector3(-3.981f, 0.138f, 8.744f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Спальное место 1 (Гостевая)", new Vector3(-2.319f, 0.199f, 13.576f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Спальное место 2 (Гостевая)", new Vector3(-2.348f, 0.199f, 12.587f), 270.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Дом свина (Кровать)", new Vector3(-167.169f, -3.311f, 1021.886f), 87.0f, new Vector3(1.2f, 0.8f, 2.0f)),
            new CustomBedDef("Коврик в доме свина", new Vector3(-166.469f, -3.689f, 1020.904f), 101.5f, new Vector3(1.2f, 0.6f, 2.0f)),
            new CustomBedDef("Коврик в домике на острове", new Vector3(-847.334f, -2.532f, 505.995f), 333.6f, new Vector3(1.2f, 0.6f, 2.0f))
        };

        private const float LayTransitionDuration = 0.75f;
        private const float SoloSleepDelaySeconds = 3.5f;
        private const float EyesCloseDuration = 3.5f;
        private const float SleepWaitDuration = 1.5f;
        private const float EyesOpenDuration = 1.0f;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly List<BedEntry> beds = new List<BedEntry>();
        private readonly HashSet<int> hookedBedObjIds = new HashSet<int>();
        private readonly List<GameObject> createdCustomBeds = new List<GameObject>();
        private readonly Dictionary<int, PeerSleepState> peerSleepStates = new Dictionary<int, PeerSleepState>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();
        private readonly List<MouseLook> disabledMouseLooks = new List<MouseLook>();

        private int vanillaBedsCount;
        private int customBedsCount;
        private bool customBedsCreated;
        private float nextScanTime;

        private Transform playerTr;
        private Animation sleepEyes;
        private FsmFloat fatigueVar;
        private FsmInt timeVar;
        private FsmInt dayVar;
        private FsmBool playerStopVar;
        private FsmFloat drunkVar;
        private FsmFloat hungerVar;
        private FsmFloat thirstVar;
        private FsmFloat urineVar;
        private FsmString guiInteraction;
        private FsmBool guiUse;

        private PlayMakerFSM fireBedroomFsm;
        private PlayMakerFSM housefireFsm;

        private bool isLocalLayingDown;
        private bool isLocalSmoking;
        private bool isExecutingSleep;
        private bool showingPrompt;
        private BedEntry activeBed;
        private Vector3 playerOriginalPos;
        private Quaternion playerOriginalRot;
        private Quaternion lockedHeadRotation = Quaternion.identity;
        private Vector3 activeBedPos;
        private float activeBedYaw;
        private float soloSleepTimer;

        private bool isTransitioningToBed;
        private float transitionElapsed;
        private Vector3 transStartPos;
        private Quaternion transStartRot;
        private Vector3 transTargetPos;
        private Quaternion transTargetRot;
        private Vector3 transStartCamPos;

        private SleepPhase sleepPhase = SleepPhase.None;
        private float sleepPhaseTimer;
        private int pendingSleepHours = 8;
        private bool pendingSleepFire;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private bool IsServer => ctx != null && ctx.Transport != null && ctx.Transport.IsServer;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Кровати: {vanillaBedsCount}+{customBedsCount}, Сон: {(isExecutingSleep ? "Ночь" : (isLocalLayingDown ? "Ожидание" : "Готов"))}";

        public CooperativeSleepSystem(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            FindGameReferences();
            HookVanillaBeds();
            SetupCustomBeds();
            ctx?.Log?.Invoke($"[Сон] Подключено триггеров сна: ванильных {vanillaBedsCount}, кастомных {customBedsCount}.");
        }

        public void Reset()
        {
            if (isLocalLayingDown || isExecutingSleep)
            {
                UnlockLocalPlayerImmediate();
            }

            for (int i = 0; i < beds.Count; i++)
            {
                if (beds[i].VanillaFsm != null)
                {
                    beds[i].VanillaFsm.enabled = true;
                }
            }

            for (int i = 0; i < createdCustomBeds.Count; i++)
            {
                if (createdCustomBeds[i] != null)
                {
                    UnityEngine.Object.Destroy(createdCustomBeds[i]);
                }
            }

            beds.Clear();
            hookedBedObjIds.Clear();
            createdCustomBeds.Clear();
            peerSleepStates.Clear();
            pendingSnapshots.Clear();
            disabledMouseLooks.Clear();

            vanillaBedsCount = 0;
            customBedsCount = 0;
            customBedsCreated = false;
            nextScanTime = 0f;

            playerTr = null;
            sleepEyes = null;
            fatigueVar = null;
            timeVar = null;
            dayVar = null;
            playerStopVar = null;
            drunkVar = null;
            hungerVar = null;
            thirstVar = null;
            urineVar = null;
            guiInteraction = null;
            guiUse = null;
            fireBedroomFsm = null;
            housefireFsm = null;

            isLocalLayingDown = false;
            isLocalSmoking = false;
            isExecutingSleep = false;
            showingPrompt = false;
            activeBed = null;
            isTransitioningToBed = false;
            transitionElapsed = 0f;
            sleepPhase = SleepPhase.None;
            sleepPhaseTimer = 0f;
            pendingSleepHours = 8;
            pendingSleepFire = false;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;

            if (now >= nextScanTime)
            {
                nextScanTime = now + 2.0f;
                if (playerTr == null || timeVar == null || fatigueVar == null)
                {
                    FindGameReferences();
                }
                HookVanillaBeds();
                if (!customBedsCreated)
                {
                    SetupCustomBeds();
                }
            }

            ProcessPendingSnapshots(now);

            if (isExecutingSleep)
            {
                StepSleepExecution(Time.deltaTime);
                HoldSleepingCamera();
                return;
            }

            if (isLocalLayingDown)
            {
                StepLayingInBed(Time.deltaTime, now);
                return;
            }

            PollBedInteractionRaycast();
        }

        public void OnGUI()
        {
            if (!isLocalLayingDown || isExecutingSleep) return;

            Color prevColor = GUI.color;
            TextAnchor prevAlign = GUI.skin.label.alignment;
            int prevSize = GUI.skin.label.fontSize;

            try
            {
                GUI.color = Color.white;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.skin.label.fontSize = 20;

                string statusText;
                if (!IsNetworkRunning)
                {
                    statusText = "ОДИНОЧНЫЙ СОН... ЗАСЫПАЕМ\n(WASD или Пробел для отмены)";
                }
                else if (IsServer)
                {
                    int clients = ctx.Transport.ConnectedClientsCount;
                    if (clients == 0)
                    {
                        statusText = "ОДИНОЧНЫЙ СОН... ЗАСЫПАЕМ\n(WASD или Пробел для отмены)";
                    }
                    else if (AreAllRemotePeersLayingDown())
                    {
                        statusText = "ОБА ИГРОКА ГОТОВЫ... ЗАСЫПАЕМ";
                    }
                    else
                    {
                        statusText = "ВЫ В ПОСТЕЛИ. ОЖИДАНИЕ ВТОРОГО ИГРОКА...\n(WASD или Пробел для отмены)";
                    }
                }
                else
                {
                    statusText = IsAnyRemotePeerLayingDown()
                        ? "ОБА ИГРОКА ГОТОВЫ... ЗАСЫПАЕМ"
                        : "ВЫ В ПОСТЕЛИ. ОЖИДАНИЕ ВТОРОГО ИГРОКА...\n(WASD или Пробел для отмены)";
                }

                if (isLocalSmoking)
                {
                    statusText += "\n<color=red>ВНИМАНИЕ: СИГАРЕТА ВО РТУ! ОПАСНОСТЬ ПОЖАРА!</color>";
                }

                GUI.Label(new Rect(0f, Screen.height * 0.7f, Screen.width, 100f), statusText);
            }
            finally
            {
                GUI.color = prevColor;
                GUI.skin.label.alignment = prevAlign;
                GUI.skin.label.fontSize = prevSize;
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;

            SendSleepSnapshotToClient(clientId);

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

            int disconnectedId = (ctx?.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;

            if (peerSleepStates.ContainsKey(disconnectedId))
            {
                peerSleepStates.Remove(disconnectedId);
            }

            ctx?.Events?.Publish(new RemotePlayerSleepStateEvent
            {
                PlayerId = disconnectedId,
                IsSleeping = false,
                BedPosition = Vector3.zero,
                BedYaw = 0f
            });

            if (isExecutingSleep)
            {
                // Не прерываем уже запущенную перемотку ночи — игрок спокойно проснётся.
                return;
            }

            if (ctx?.Transport != null && ctx.Transport.IsClient && isLocalLayingDown)
            {
                // Клиент не может уснуть без хоста — немедленно поднимаем его, чтобы не было софтлока.
                ctx?.Log?.Invoke("[Сон] Хост отключился — подъём с постели без блокировки.");
                CancelLayingDown();
                return;
            }

            if (IsServer && isLocalLayingDown)
            {
                soloSleepTimer = Time.time;
                ctx?.Log?.Invoke("[Сон] Напарник отключился. Переход на одиночный режим сна (WASD/Пробел для отмены).");
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subtype = reader.ReadByte();
            switch (subtype)
            {
                case DomesticProtocol.SleepEnterState:
                {
                    if (reader.UnreadBytes < 4 + 1 + 12 + 4) return;
                    int playerId = reader.ReadInt32();
                    bool smoking = reader.ReadBoolean();
                    Vector3 bedPos = reader.ReadVector3();
                    float bedYaw = reader.ReadSingle();

                    if (playerId == LocalPlayerId) return;

                    peerSleepStates[playerId] = new PeerSleepState
                    {
                        IsLayingDown = true,
                        IsSmoking = smoking,
                        BedPosition = bedPos,
                        BedYaw = bedYaw
                    };

                    ctx?.Events?.Publish(new RemotePlayerSleepStateEvent
                    {
                        PlayerId = playerId,
                        IsSleeping = true,
                        BedPosition = bedPos,
                        BedYaw = bedYaw
                    });

                    if (IsServer)
                    {
                        CheckCoopSleepCondition();
                    }
                    break;
                }

                case DomesticProtocol.SleepExitState:
                {
                    if (reader.UnreadBytes < 4) return;
                    int playerId = reader.ReadInt32();
                    if (playerId == LocalPlayerId) return;

                    peerSleepStates[playerId] = new PeerSleepState
                    {
                        IsLayingDown = false,
                        IsSmoking = false,
                        BedPosition = Vector3.zero,
                        BedYaw = 0f
                    };

                    ctx?.Events?.Publish(new RemotePlayerSleepStateEvent
                    {
                        PlayerId = playerId,
                        IsSleeping = false,
                        BedPosition = Vector3.zero,
                        BedYaw = 0f
                    });
                    break;
                }

                case DomesticProtocol.SleepExecuteSkip:
                {
                    if (reader.UnreadBytes < 2) return;
                    int sleepHours = reader.ReadByte();
                    bool isFire = reader.ReadBoolean();

                    if (!IsServer && !isExecutingSleep)
                    {
                        BeginSleepExecution(sleepHours, isFire);
                    }
                    break;
                }

                case DomesticProtocol.SleepSnapshot:
                {
                    if (reader.UnreadBytes < 4) return;
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        if (reader.UnreadBytes < 4 + 1 + 12 + 4) break;
                        int playerId = reader.ReadInt32();
                        bool smoking = reader.ReadBoolean();
                        Vector3 bedPos = reader.ReadVector3();
                        float bedYaw = reader.ReadSingle();

                        if (playerId == LocalPlayerId) continue;

                        peerSleepStates[playerId] = new PeerSleepState
                        {
                            IsLayingDown = true,
                            IsSmoking = smoking,
                            BedPosition = bedPos,
                            BedYaw = bedYaw
                        };

                        ctx?.Events?.Publish(new RemotePlayerSleepStateEvent
                        {
                            PlayerId = playerId,
                            IsSleeping = true,
                            BedPosition = bedPos,
                            BedYaw = bedYaw
                        });
                    }
                    break;
                }
            }
        }

        private void FindGameReferences()
        {
            try
            {
                GameObject playerObj = GameObject.Find("PLAYER");
                if (playerObj != null)
                {
                    playerTr = playerObj.transform;
                    Transform eyesTr = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/SleepEyes");
                    if (eyesTr != null)
                    {
                        sleepEyes = eyesTr.GetComponent<Animation>();
                    }
                }

                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    var vars = PlayMakerGlobals.Instance.Variables;
                    fatigueVar = vars.FindFsmFloat("PlayerFatigue");
                    timeVar = vars.FindFsmInt("GlobalTime") ?? vars.FindFsmInt("Time");
                    dayVar = vars.FindFsmInt("GlobalDay");
                    playerStopVar = vars.FindFsmBool("PlayerStop");
                    drunkVar = vars.FindFsmFloat("PlayerDrunkAdjusted");
                    hungerVar = vars.FindFsmFloat("PlayerHunger");
                    thirstVar = vars.FindFsmFloat("PlayerThirst");
                    urineVar = vars.FindFsmFloat("PlayerUrine");
                    guiInteraction = vars.FindFsmString("GUIinteraction");
                    guiUse = vars.FindFsmBool("GUIuse");
                }

                if (timeVar == null)
                {
                    GameObject mapObj = GameObject.Find("MAP");
                    Transform sunObj = mapObj != null ? mapObj.transform.Find("SUN/Pivot/SUN") : null;
                    if (sunObj != null)
                    {
                        PlayMakerFSM sunFsm = sunObj.GetComponent<PlayMakerFSM>();
                        if (sunFsm != null)
                        {
                            sunFsm.InitializeSafe();
                            timeVar = sunFsm.FsmVariables.FindFsmInt("Time");
                        }
                    }
                }

                GameObject bedFireObj = GameObject.Find("FireBedroom1") ?? GameObject.Find("FireBedroom2");
                if (bedFireObj != null)
                {
                    fireBedroomFsm = bedFireObj.GetComponent<PlayMakerFSM>();
                }

                GameObject housefireObj = GameObject.Find("HOUSEFIRE");
                if (housefireObj != null)
                {
                    housefireFsm = housefireObj.GetComponent<PlayMakerFSM>();
                }
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Сон] Ошибка поиска игровых ссылок: {ex.Message}");
            }
        }

        private void HookVanillaBeds()
        {
            for (int i = 0; i < KnownVanillaBedPaths.Length; i++)
            {
                GameObject go = GameObject.Find(KnownVanillaBedPaths[i]);
                if (go != null)
                {
                    HookBedTrigger(go);
                }
            }

            GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                GameObject go = allGo[i];
                if (go == null || go.name != "SleepTrigger") continue;
                if (go.name.StartsWith("OSC_CustomBed_", StringComparison.Ordinal)) continue;
                HookBedTrigger(go);
            }
        }

        private void HookBedTrigger(GameObject bedGo)
        {
            if (bedGo == null) return;
            int id = bedGo.GetInstanceID();
            if (hookedBedObjIds.Contains(id)) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(bedGo, "Use") ?? bedGo.GetComponent<PlayMakerFSM>();
            if (fsm == null) return;

            fsm.InitializeSafe();
            if (ctx?.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, "SleepTrigger"))
            {
                return;
            }

            hookedBedObjIds.Add(id);
            fsm.enabled = false;

            Transform pivot = bedGo.transform.Find("Pivot")
                              ?? bedGo.transform.Find("PivotSleep")
                              ?? bedGo.transform;

            Transform wakeTr = (pivot.parent != null)
                ? (pivot.parent.Find("PivotWake") ?? pivot.parent.Find("SleepTrigger/PivotWake") ?? bedGo.transform.Find("PivotWake"))
                : bedGo.transform.Find("PivotWake");

            Collider col = bedGo.GetComponent<Collider>();
            if (col == null)
            {
                var box = bedGo.AddComponent<BoxCollider>();
                box.size = new Vector3(1.2f, 0.6f, 2.0f);
                col = box;
            }
            col.isTrigger = true;

            beds.Add(new BedEntry
            {
                TriggerObj = bedGo,
                Collider = col,
                Pivot = pivot,
                PivotWake = wakeTr,
                VanillaFsm = fsm,
                IsCustom = false
            });
            vanillaBedsCount++;
        }

        private void SetupCustomBeds()
        {
            if (customBedsCreated) return;

            for (int i = 0; i < CustomBedDefs.Length; i++)
            {
                CustomBedDef def = CustomBedDefs[i];
                try
                {
                    var bedObj = new GameObject($"OSC_CustomBed_{i}_{def.Name}");
                    bedObj.transform.position = def.Position;
                    bedObj.transform.rotation = Quaternion.Euler(0f, def.Yaw, 0f);
                    bedObj.layer = 0;

                    var boxCol = bedObj.AddComponent<BoxCollider>();
                    boxCol.size = def.TriggerSize;
                    boxCol.center = new Vector3(0f, 0.2f, 0.5f);
                    boxCol.isTrigger = true;

                    var pivotObj = new GameObject("Pivot");
                    pivotObj.transform.parent = bedObj.transform;
                    pivotObj.transform.localPosition = Vector3.zero;
                    pivotObj.transform.localRotation = Quaternion.identity;

                    var wakeObj = new GameObject("PivotWake");
                    wakeObj.transform.parent = bedObj.transform;
                    wakeObj.transform.localPosition = new Vector3(0.9f, 0f, 0.5f);
                    wakeObj.transform.localRotation = Quaternion.identity;

                    hookedBedObjIds.Add(bedObj.GetInstanceID());
                    createdCustomBeds.Add(bedObj);
                    beds.Add(new BedEntry
                    {
                        TriggerObj = bedObj,
                        Collider = boxCol,
                        Pivot = pivotObj.transform,
                        PivotWake = wakeObj.transform,
                        VanillaFsm = null,
                        IsCustom = true
                    });
                    customBedsCount++;
                }
                catch (Exception ex)
                {
                    ctx?.Log?.Invoke($"[Сон] Ошибка создания кастомной кровати '{def.Name}': {ex.Message}");
                }
            }

            customBedsCreated = true;
        }

        private void PollBedInteractionRaycast()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                ClearPromptIfShowing();
                return;
            }

            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 2.8f) && hit.collider != null)
            {
                BedEntry hitBed = FindBedForCollider(hit.collider);
                if (hitBed != null)
                {
                    bool canSleep = fatigueVar == null || fatigueVar.Value >= 10f;
                    if (guiInteraction != null) guiInteraction.Value = canSleep ? "SLEEP" : "NOT TIRED";
                    if (guiUse != null) guiUse.Value = canSleep;
                    showingPrompt = true;

                    if (canSleep && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.E)))
                    {
                        ClearPromptIfShowing();
                        StartLayingDown(hitBed);
                    }
                    return;
                }
            }

            ClearPromptIfShowing();
        }

        private BedEntry FindBedForCollider(Collider col)
        {
            for (int i = 0; i < beds.Count; i++)
            {
                BedEntry bed = beds[i];
                if (bed.TriggerObj == null || bed.Collider == null) continue;
                if (!bed.TriggerObj.activeInHierarchy) continue;

                if (col == bed.Collider || col.transform.IsChildOf(bed.TriggerObj.transform))
                {
                    return bed;
                }
            }
            return null;
        }

        private void ClearPromptIfShowing()
        {
            if (!showingPrompt) return;
            showingPrompt = false;
            if (guiInteraction != null) guiInteraction.Value = string.Empty;
            if (guiUse != null) guiUse.Value = false;
        }

        private Vector3 GetBedLayingPosition(Transform pivot)
        {
            if (pivot == null) return playerTr != null ? playerTr.position : Vector3.zero;
            return pivot.position;
        }

        private Quaternion GetBedLayingRotation(Transform pivot)
        {
            if (pivot == null)
            {
                return playerTr != null ? Quaternion.Euler(0f, playerTr.eulerAngles.y, 0f) : Quaternion.identity;
            }

            Vector3 bedForward = Vector3.forward;
            if (pivot.parent != null)
            {
                bedForward = pivot.parent.forward;
                bedForward.y = 0f;
                if (bedForward.sqrMagnitude < 0.01f)
                {
                    bedForward = pivot.parent.right;
                    bedForward.y = 0f;
                }
            }
            if (bedForward.sqrMagnitude < 0.01f)
            {
                bedForward = pivot.forward;
                bedForward.y = 0f;
            }
            if (bedForward.sqrMagnitude < 0.01f)
            {
                bedForward = Vector3.forward;
            }

            bedForward.Normalize();
            float yaw = Mathf.Atan2(bedForward.x, bedForward.z) * Mathf.Rad2Deg;
            return Quaternion.Euler(0f, yaw, 0f);
        }

        private void StartLayingDown(BedEntry bed)
        {
            if (playerTr == null) FindGameReferences();
            if (playerTr == null || bed == null) return;

            isLocalLayingDown = true;
            activeBed = bed;
            playerOriginalPos = playerTr.position;
            playerOriginalRot = playerTr.rotation;
            soloSleepTimer = Time.time;

            if (playerStopVar != null)
            {
                playerStopVar.Value = true;
            }

            isLocalSmoking = CheckIfSmoking();

            Vector3 targetPos = GetBedLayingPosition(bed.Pivot);
            Quaternion targetRot = GetBedLayingRotation(bed.Pivot);
            lockedHeadRotation = targetRot;
            activeBedPos = targetPos;
            activeBedYaw = targetRot.eulerAngles.y;

            Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera")
                               ?? playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");

            isTransitioningToBed = true;
            transitionElapsed = 0f;
            transStartPos = playerOriginalPos;
            transStartRot = playerOriginalRot;
            transTargetPos = targetPos;
            transTargetRot = targetRot;
            transStartCamPos = fpsCam != null ? fpsCam.localPosition : new Vector3(0f, 1.4f, 0f);

            SetCameraLookLocked(true);

            peerSleepStates[LocalPlayerId] = new PeerSleepState
            {
                IsLayingDown = true,
                IsSmoking = isLocalSmoking,
                BedPosition = activeBedPos,
                BedYaw = activeBedYaw
            };

            ctx?.Events?.Publish(new LocalPlayerSleepStateEvent
            {
                IsSleeping = true,
                BedPosition = activeBedPos,
                BedYaw = activeBedYaw
            });

            SendSleepEnter(LocalPlayerId, isLocalSmoking, activeBedPos, activeBedYaw);

            ctx?.Log?.Invoke(isLocalSmoking
                ? "[Сон] Вы легли в постель С ЗАКУРЕННОЙ СИГАРЕТОЙ! Ожидание напарника..."
                : "[Сон] Вы легли в постель. Ожидание напарника...");

            if (IsServer)
            {
                CheckCoopSleepCondition();
            }
        }

        private void CancelLayingDown()
        {
            if (!isLocalLayingDown || isExecutingSleep) return;

            isTransitioningToBed = false;
            SetCameraLookLocked(false);

            isLocalLayingDown = false;
            isLocalSmoking = false;

            if (playerStopVar != null)
            {
                playerStopVar.Value = false;
            }

            if (playerTr != null)
            {
                playerTr.position = playerOriginalPos;
                playerTr.rotation = Quaternion.Euler(0f, playerOriginalRot.eulerAngles.y, 0f);
                ResetPlayerCameraRoll(playerTr);
                TriggerPlayerStandCrouch();
            }

            peerSleepStates[LocalPlayerId] = new PeerSleepState
            {
                IsLayingDown = false,
                IsSmoking = false,
                BedPosition = Vector3.zero,
                BedYaw = 0f
            };

            ctx?.Events?.Publish(new LocalPlayerSleepStateEvent
            {
                IsSleeping = false,
                BedPosition = Vector3.zero,
                BedYaw = 0f
            });

            SendSleepExit(LocalPlayerId);
            ctx?.Log?.Invoke("[Сон] Вы встали с постели.");
        }

        private void StepLayingInBed(float deltaTime, float now)
        {
            if (isTransitioningToBed)
            {
                transitionElapsed += deltaTime;
                float t = Mathf.Clamp01(transitionElapsed / LayTransitionDuration);
                float smoothT = Mathf.SmoothStep(0f, 1f, t);

                if (playerTr != null)
                {
                    playerTr.position = Vector3.Lerp(transStartPos, transTargetPos, smoothT);
                    playerTr.rotation = Quaternion.Slerp(transStartRot, transTargetRot, smoothT);

                    Transform animPivot = playerTr.Find("Pivot/AnimPivot");
                    if (animPivot != null) animPivot.localRotation = Quaternion.identity;

                    Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
                    if (fpsCam != null)
                    {
                        fpsCam.localPosition = Vector3.Lerp(transStartCamPos, new Vector3(0f, 0.22f, 0f), smoothT);
                        fpsCam.localRotation = Quaternion.identity;
                    }
                }

                if (t >= 1f)
                {
                    isTransitioningToBed = false;
                }
            }
            else
            {
                HoldSleepingCamera();
            }

            // Если игрок закурил уже лёжа в постели, обновляем флаг пожароопасности
            if (!isLocalSmoking && CheckIfSmoking())
            {
                isLocalSmoking = true;
                peerSleepStates[LocalPlayerId] = new PeerSleepState
                {
                    IsLayingDown = true,
                    IsSmoking = true,
                    BedPosition = activeBedPos,
                    BedYaw = activeBedYaw
                };
                SendSleepEnter(LocalPlayerId, true, activeBedPos, activeBedYaw);
                ctx?.Log?.Invoke("[Сон] Внимание: вы с сигаретой в постели!");
            }

            if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) ||
                Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D) ||
                Input.GetKeyDown(KeyCode.Space))
            {
                CancelLayingDown();
                return;
            }

            if (IsServer)
            {
                if (ctx.Transport.ConnectedClientsCount == 0 && now - soloSleepTimer > SoloSleepDelaySeconds)
                {
                    CheckCoopSleepCondition();
                }
            }
            else if (!IsNetworkRunning && now - soloSleepTimer > SoloSleepDelaySeconds)
            {
                int hours = CalculateSleepHours();
                BeginSleepExecution(hours, isLocalSmoking);
            }
        }

        private void HoldSleepingCamera()
        {
            if (playerTr == null) return;

            playerTr.rotation = lockedHeadRotation;

            Transform pivot = playerTr.Find("Pivot");
            if (pivot != null) pivot.localRotation = Quaternion.identity;

            Transform animPivot = playerTr.Find("Pivot/AnimPivot");
            if (animPivot != null) animPivot.localRotation = Quaternion.identity;

            Transform camNode = playerTr.Find("Pivot/AnimPivot/Camera");
            if (camNode != null) camNode.localRotation = Quaternion.identity;

            Transform fpsCam = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera");
            if (fpsCam != null)
            {
                fpsCam.localPosition = new Vector3(0f, 0.22f, 0f);
                fpsCam.localRotation = Quaternion.identity;
            }

            Transform mainCamChild = playerTr.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
            if (mainCamChild != null) mainCamChild.localRotation = Quaternion.identity;

            if (Camera.main != null && Camera.main.transform != null)
            {
                Camera.main.transform.rotation = lockedHeadRotation;
            }
        }

        private int CalculateSleepHours()
        {
            if (fatigueVar != null)
            {
                return Mathf.Clamp(Mathf.FloorToInt(fatigueVar.Value / 12f) * 2, 2, 12);
            }
            return 8;
        }

        private bool IsAnyRemotePeerLayingDown()
        {
            foreach (var kv in peerSleepStates)
            {
                if (kv.Key != LocalPlayerId && kv.Value.IsLayingDown)
                {
                    return true;
                }
            }
            return false;
        }

        private bool AreAllRemotePeersLayingDown()
        {
            if (ctx?.Transport == null) return false;
            int clients = ctx.Transport.ConnectedClientsCount;
            if (clients <= 0) return true;

            int readyCount = 0;
            foreach (var kv in peerSleepStates)
            {
                if (kv.Key != LocalPlayerId && kv.Value.IsLayingDown)
                {
                    readyCount++;
                }
            }
            return readyCount >= clients;
        }

        private void CheckCoopSleepCondition()
        {
            if (!IsServer || isExecutingSleep || !isLocalLayingDown) return;

            int clients = ctx.Transport.ConnectedClientsCount;
            if (clients > 0 && !AreAllRemotePeersLayingDown()) return;

            int sleepHours = CalculateSleepHours();
            bool isFire = isLocalSmoking;
            foreach (var kv in peerSleepStates)
            {
                if (kv.Value.IsLayingDown && kv.Value.IsSmoking)
                {
                    isFire = true;
                    break;
                }
            }

            SendSleepExecute(sleepHours, isFire);
            BeginSleepExecution(sleepHours, isFire);
        }

        private void BeginSleepExecution(int sleepHours, bool isFire)
        {
            isExecutingSleep = true;
            isTransitioningToBed = false;
            pendingSleepHours = sleepHours;
            pendingSleepFire = isFire;
            sleepPhase = SleepPhase.ClosingEyes;
            sleepPhaseTimer = 0f;

            if (sleepEyes != null)
            {
                sleepEyes.gameObject.SetActive(true);
                sleepEyes.Play("sleep_on");
            }

            ctx?.Log?.Invoke($"[Сон] Наступила ночь. Синхронный сон на {sleepHours} ч...");
        }

        private void StepSleepExecution(float deltaTime)
        {
            sleepPhaseTimer += deltaTime;

            switch (sleepPhase)
            {
                case SleepPhase.ClosingEyes:
                    if (sleepPhaseTimer >= EyesCloseDuration)
                    {
                        ApplyNightSkipAndStats();
                        sleepPhase = SleepPhase.SleepingWait;
                        sleepPhaseTimer = 0f;
                    }
                    break;

                case SleepPhase.SleepingWait:
                    if (sleepPhaseTimer >= SleepWaitDuration)
                    {
                        if (sleepEyes != null)
                        {
                            sleepEyes.Play("sleep_off");
                        }
                        sleepPhase = SleepPhase.OpeningEyes;
                        sleepPhaseTimer = 0f;
                    }
                    break;

                case SleepPhase.OpeningEyes:
                    if (sleepPhaseTimer >= EyesOpenDuration)
                    {
                        if (sleepEyes != null)
                        {
                            sleepEyes.gameObject.SetActive(false);
                        }
                        FinishSleepExecution();
                    }
                    break;
            }
        }

        private void ApplyNightSkipAndStats()
        {
            if (timeVar != null)
            {
                int newTime = timeVar.Value + pendingSleepHours;
                if (newTime >= 24)
                {
                    newTime -= 24;
                    if (dayVar != null)
                    {
                        dayVar.Value = (dayVar.Value % 7) + 1;
                    }
                }
                timeVar.Value = Mathf.Clamp(newTime, 0, 23);
            }

            if (fatigueVar != null) fatigueVar.Value = 0f;
            if (drunkVar != null) drunkVar.Value = Mathf.Max(0f, drunkVar.Value - (pendingSleepHours * 0.2f));
            if (hungerVar != null) hungerVar.Value = Mathf.Min(100f, hungerVar.Value + (pendingSleepHours * 2.5f));
            if (thirstVar != null) thirstVar.Value = Mathf.Min(100f, thirstVar.Value + (pendingSleepHours * 3.5f));
            if (urineVar != null) urineVar.Value = Mathf.Min(100f, urineVar.Value + (pendingSleepHours * 2.0f));

            ctx?.Events?.Publish(new WorldTimeForceSyncEvent
            {
                ImmediateBroadcast = true
            });
        }

        private void FinishSleepExecution()
        {
            try
            {
                if (playerTr != null)
                {
                    Vector3 wakePos = playerOriginalPos;
                    if (activeBed != null)
                    {
                        if (activeBed.PivotWake != null)
                        {
                            wakePos = activeBed.PivotWake.position;
                        }
                        else if (wakePos.sqrMagnitude < 1f && activeBed.Pivot != null)
                        {
                            wakePos = activeBed.Pivot.position + activeBed.Pivot.right * 0.8f;
                        }
                    }

                    playerTr.position = wakePos;
                    playerTr.rotation = Quaternion.Euler(0f, playerOriginalRot.eulerAngles.y, 0f);
                    ResetPlayerCameraRoll(playerTr);
                    TriggerPlayerStandCrouch();
                }
            }
            finally
            {
                isExecutingSleep = false;
                isLocalLayingDown = false;
                isLocalSmoking = false;
                sleepPhase = SleepPhase.None;
                sleepPhaseTimer = 0f;

                SetCameraLookLocked(false);

                if (playerStopVar != null)
                {
                    playerStopVar.Value = false;
                }

                if (playerTr != null)
                {
                    ResetPlayerCameraRoll(playerTr);
                }

                // Сбрасываем состояния сна всех участников и уведомляем модуль игроков
                var remoteIds = new List<int>();
                foreach (var kv in peerSleepStates)
                {
                    if (kv.Key != LocalPlayerId && kv.Value.IsLayingDown)
                    {
                        remoteIds.Add(kv.Key);
                    }
                }
                peerSleepStates.Clear();

                ctx?.Events?.Publish(new LocalPlayerSleepStateEvent
                {
                    IsSleeping = false,
                    BedPosition = Vector3.zero,
                    BedYaw = 0f
                });

                for (int i = 0; i < remoteIds.Count; i++)
                {
                    ctx?.Events?.Publish(new RemotePlayerSleepStateEvent
                    {
                        PlayerId = remoteIds[i],
                        IsSleeping = false,
                        BedPosition = Vector3.zero,
                        BedYaw = 0f
                    });
                }
            }

            if (pendingSleepFire)
            {
                pendingSleepFire = false;
                ctx?.Log?.Invoke("[ПОЖАР] ОДИН ИЗ ИГРОКОВ ЗАСНУЛ С СИГАРЕТОЙ! ДОМ ЗАГОРЕЛСЯ!");
                TriggerHouseFireLocally();
            }
            else
            {
                ctx?.Log?.Invoke("[Сон] Доброе утро! Вы проснулись бодрыми.");
            }
        }

        private void UnlockLocalPlayerImmediate()
        {
            if (sleepEyes != null)
            {
                sleepEyes.gameObject.SetActive(false);
            }
            SetCameraLookLocked(false);
            if (playerStopVar != null)
            {
                playerStopVar.Value = false;
            }
            if (playerTr != null)
            {
                ResetPlayerCameraRoll(playerTr);
            }
            ctx?.Events?.Publish(new LocalPlayerSleepStateEvent
            {
                IsSleeping = false,
                BedPosition = Vector3.zero,
                BedYaw = 0f
            });
        }

        private void TriggerPlayerStandCrouch()
        {
            if (playerTr == null) return;
            PlayMakerFSM[] allFsms = playerTr.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                if (allFsms[i] != null && allFsms[i].FsmName == "Crouch")
                {
                    allFsms[i].SendEvent("JUMP");
                    break;
                }
            }
        }

        private void TriggerHouseFireLocally()
        {
            try
            {
                if (fireBedroomFsm != null)
                {
                    fireBedroomFsm.SendEvent("FIRE");
                }

                if (housefireFsm != null)
                {
                    housefireFsm.SendEvent("FIRE");
                    housefireFsm.Fsm.Event("State 2");
                }

                GameObject fireTrigger = GameObject.Find("FireBedroom1") ?? GameObject.Find("FireTrigger");
                if (fireTrigger != null)
                {
                    PlayMakerFSM fsm = fireTrigger.GetComponent<PlayMakerFSM>();
                    if (fsm != null) fsm.SendEvent("FIRE");
                }
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Сон] Ошибка запуска пожара от сигареты: {ex.Message}");
            }
        }

        private void SetCameraLookLocked(bool locked)
        {
            try
            {
                if (playerTr == null)
                {
                    GameObject p = GameObject.Find("PLAYER");
                    if (p != null) playerTr = p.transform;
                }
                if (playerTr == null) return;

                if (locked)
                {
                    disabledMouseLooks.Clear();
                    MouseLook[] looks = playerTr.GetComponentsInChildren<MouseLook>(true);
                    for (int i = 0; i < looks.Length; i++)
                    {
                        if (looks[i] != null && looks[i].enabled)
                        {
                            looks[i].enabled = false;
                            disabledMouseLooks.Add(looks[i]);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < disabledMouseLooks.Count; i++)
                    {
                        if (disabledMouseLooks[i] != null)
                        {
                            disabledMouseLooks[i].enabled = true;
                        }
                    }
                    disabledMouseLooks.Clear();

                    MouseLook[] looks = playerTr.GetComponentsInChildren<MouseLook>(true);
                    for (int i = 0; i < looks.Length; i++)
                    {
                        if (looks[i] != null)
                        {
                            looks[i].enabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Сон] Ошибка блокировки обзора: {ex.Message}");
            }
        }

        private static bool CheckIfSmoking()
        {
            try
            {
                if (Input.GetKey(KeyCode.I)) return true;
                GameObject smokingObj = GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Smoking");
                if (smokingObj != null && smokingObj.activeSelf) return true;
            }
            catch { }
            return false;
        }

        private static void ResetPlayerCameraRoll(Transform player)
        {
            if (player == null) return;

            Vector3 euler = player.eulerAngles;
            player.rotation = Quaternion.Euler(0f, euler.y, 0f);

            Transform pivot = player.Find("Pivot");
            if (pivot != null)
            {
                Vector3 pE = pivot.localEulerAngles;
                pivot.localEulerAngles = new Vector3(pE.x, pE.y, 0f);
            }

            Transform animPivot = player.Find("Pivot/AnimPivot");
            if (animPivot != null)
            {
                animPivot.localPosition = Vector3.zero;
                animPivot.localRotation = Quaternion.identity;
                Animation anim = animPivot.GetComponent<Animation>();
                if (anim != null && anim.isPlaying) anim.Stop();
            }

            Transform camNode = player.Find("Pivot/AnimPivot/Camera");
            if (camNode != null)
            {
                camNode.localRotation = Quaternion.identity;
            }

            Transform fpsCam = player.Find("Pivot/AnimPivot/Camera/FPSCamera");
            if (fpsCam != null)
            {
                Vector3 fE = fpsCam.localEulerAngles;
                fpsCam.localEulerAngles = new Vector3(fE.x, 0f, 0f);
                fpsCam.localPosition = new Vector3(0f, 1.4f, 0f);
            }

            Transform mainCamChild = player.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera");
            if (mainCamChild != null)
            {
                mainCamChild.localRotation = Quaternion.identity;
            }

            if (Camera.main != null && Camera.main.transform != null)
            {
                Vector3 cE = Camera.main.transform.localEulerAngles;
                Camera.main.transform.localEulerAngles = new Vector3(cE.x, cE.y, 0f);
            }
        }

        private void ProcessPendingSnapshots(float now)
        {
            if (!IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    int targetId = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    SendSleepSnapshotToClient(targetId);
                }
            }
        }

        private void SendSleepSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

            var sleepingPeers = new List<KeyValuePair<int, PeerSleepState>>();
            foreach (var kv in peerSleepStates)
            {
                if (kv.Value.IsLayingDown)
                {
                    sleepingPeers.Add(kv);
                }
            }

            if (sleepingPeers.Count == 0) return;

            using (var packet = new PacketWriter(PacketType.PlayerSleepSync))
            {
                packet.WriteByte(DomesticProtocol.SleepSnapshot);
                packet.WriteInt32(sleepingPeers.Count);
                for (int i = 0; i < sleepingPeers.Count; i++)
                {
                    packet.WriteInt32(sleepingPeers[i].Key);
                    packet.WriteBoolean(sleepingPeers[i].Value.IsSmoking);
                    packet.WriteVector3(sleepingPeers[i].Value.BedPosition);
                    packet.WriteSingle(sleepingPeers[i].Value.BedYaw);
                }
                ctx.Transport.SendTo(packet, clientId);
            }
        }

        private void SendSleepEnter(int playerId, bool smoking, Vector3 bedPos, float bedYaw)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PlayerSleepSync))
            {
                packet.WriteByte(DomesticProtocol.SleepEnterState);
                packet.WriteInt32(playerId);
                packet.WriteBoolean(smoking);
                packet.WriteVector3(bedPos);
                packet.WriteSingle(bedYaw);
                ctx.Transport.Send(packet);
            }
        }

        private void SendSleepExit(int playerId)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PlayerSleepSync))
            {
                packet.WriteByte(DomesticProtocol.SleepExitState);
                packet.WriteInt32(playerId);
                ctx.Transport.Send(packet);
            }
        }

        private void SendSleepExecute(int sleepHours, bool isFire)
        {
            if (!IsNetworkRunning) return;
            using (var packet = new PacketWriter(PacketType.PlayerSleepSync))
            {
                packet.WriteByte(DomesticProtocol.SleepExecuteSkip);
                packet.WriteByte((byte)sleepHours);
                packet.WriteBoolean(isFire);
                ctx.Transport.Send(packet);
            }
        }
    }
}
