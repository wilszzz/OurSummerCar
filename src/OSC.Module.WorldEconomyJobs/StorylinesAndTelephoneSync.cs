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
    /// Синхронизация сюжетных линий, NPC-квестов и домашнего телефона (PacketType.StoryAndQuestsSync = 133):
    /// - Домашний телефон (входящий звонок, Caller ID / Topic, снятие и укладка трубки, маркеры заказов);
    /// - Дядя Тойво Кессели и ключи от транспорта (PlayerKeyHayosiko, PlayerKeyGifu, PlayerKeyRuscko, PlayerKeyFerndale и др.)
    ///   через безопасный опрос FsmBool/FsmInt без конфликта владения с SatsumaFleetariAndInspection;
    /// - Пьяница Йокке (ночной автостоп, переезд, чемодан с деньгами, атака с топором);
    /// - Бабушка Сиркка (доставка продуктов, поездка в церковь) и Суски (спасение, больница, свидание/письмо);
    /// - Стритрейсеры Jani / Petteri (хулиганы на жёлтой/серой машинах, драка у павильона Fighter/Fighter2 и кража наручных часов);
    /// - Late-Join снимок (немедленно + через +2.0f) и очистка в OnClientDisconnected.
    /// </summary>
    internal sealed class StorylinesAndTelephoneSync
    {
        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private sealed class KeySlot
        {
            public string Name;
            public FsmBool BoolVar;
            public FsmInt IntVar;
            public bool CurrentValue;
        }

        private sealed class PhoneRingWatcher : MonoBehaviour
        {
            public Action OnRingEnabled;

            private void OnEnable()
            {
                OnRingEnabled?.Invoke();
            }
        }

        private const byte PhoneOpRing = 1;
        private const byte PhoneOpPickup = 2;
        private const byte PhoneOpHangup = 3;

        private const byte JokkeOpHitchClimb = 1;
        private const byte JokkeOpPaidRide = 2;
        private const byte JokkeOpMovingDone = 3;
        private const byte JokkeOpSuitcaseFound = 4;
        private const byte JokkeOpSuitcaseStolen = 5;
        private const byte JokkeOpAxeAttack = 6;

        private const byte StoryOpGrandmaDelivery = 1;
        private const byte StoryOpGrandmaChurch = 2;
        private const byte StoryOpSuskiStage = 3;

        private const byte HooliganOpRacerState = 1;
        private const byte HooliganOpFighterHit = 2;
        private const byte HooliganOpWatchTheft = 3;

        private const float ScanIntervalSeconds = 6.0f;
        private const float KeyPollIntervalSeconds = 0.5f;

        private static readonly string[] CallerMarkerNames = new string[]
        {
            "call_woods",
            "call_lift",
            "shitwell01_call",
            "shitwell02_call",
            "shitwell03_call",
            "shitwell04_call",
            "shitwell05_call",
            "call_movinghelp",
            "call_mummo1",
            "call_mummo2",
            "call_mummo3",
            "call_uncle"
        };

        private static readonly string[] VehicleKeyNames = new string[]
        {
            "PlayerKeyHayosiko",
            "PlayerKeyGifu",
            "PlayerKeyRuscko",
            "PlayerKeyFerndale",
            "PlayerKeyHome",
            "PlayerKeySatsuma"
        };

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // 1. Домашний телефон
        private PlayMakerFSM phoneRingFsm;
        private PlayMakerFSM phoneUseFsm;
        private FsmString phoneTopicVar;
        private FsmInt phoneRandomTimesVar;
        private GameObject phoneLogicGo;
        private bool phoneUseHooked;
        private bool isPhoneRinging;
        private bool isPhoneCallActive;
        private bool waitingRemoteCallEnd;
        private bool remoteCallEnteredState2;
        private string activePhoneTopic = string.Empty;
        private string activePhoneQuestId = string.Empty;
        private int activePhoneRandomTimes = 5;

        // 2. Дядя Кессели и ключи от авто
        private PlayMakerFSM uncleStatesFsm;
        private PlayMakerFSM uncleSpeakFsm;
        private bool uncleHooked;
        private byte lastUncleStage;
        private bool lastUncleGifuKeys;
        private bool lastUncleHasVan;

        private readonly KeySlot[] keySlots = new KeySlot[VehicleKeyNames.Length];
        private bool keysInitialized;
        private float suppressKeysUntilTime;

        // 3. Йокке и чемодан с деньгами
        private PlayMakerFSM jokkeHikerFsm;
        private PlayMakerFSM jokkeAxeFsm;
        private PlayMakerFSM movingFsm;
        private GameObject jokkeSuitcaseObj;
        private PlayMakerFSM suitcaseFsm;
        private bool jokkeHooked;
        private byte lastJokkeStage;
        private bool lastSuitcaseVisible;
        private bool lastSuitcaseStolen;
        private bool lastMovingDone;

        // 4. Бабушка Сиркка и Суски
        private PlayMakerFSM grandmaOrderFsm;
        private PlayMakerFSM grandmaNavFsm;
        private FsmBool grandmaAtChurchVar;
        private bool grandmaHooked;
        private int lastGrandmaDeliveries;
        private bool lastGrandmaAtChurch;

        private PlayMakerFSM suskiStagesFsm;
        private FsmInt suskiStageVar;
        private bool suskiHooked;
        private byte lastSuskiStage;

        // 5. Стритрейсеры Jani / Petteri, драка у павильона и кража часов
        private PlayMakerFSM janiStateFsm;
        private PlayMakerFSM petteriStateFsm;
        private FsmBool janiDeadVar;
        private FsmBool petteriDeadVar;
        private bool lastJaniDown;
        private bool lastPetteriDown;

        private PlayMakerFSM fighter1HitFsm;
        private PlayMakerFSM fighter1DefendFsm;
        private PlayMakerFSM fighter2HitFsm;
        private PlayMakerFSM fighter2DefendFsm;
        private FsmBool fighter1DeadVar;
        private FsmBool fighter2DeadVar;
        private bool fighter1Hooked;
        private bool fighter2Hooked;
        private bool lastFighter1Ko;
        private bool lastFighter2Ko;

        private PlayMakerFSM watchStealFsm;
        private FsmBool wristwatchOwnedVar;
        private bool lastWatchStolen;

        // Анти-эхо флаги
        private bool isSuppressingPhone;
        private bool isSuppressingUncle;
        private bool isSuppressingKeys;
        private bool isSuppressingJokke;
        private bool isSuppressingGrandma;
        private bool isSuppressingSuski;
        private bool isSuppressingHooligans;

        private float nextScanTime;
        private float nextKeyPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Телефон: {(phoneRingFsm != null ? "OK" : "-")}, Ключи: {CountOwnedKeys()}, Йокке: {lastJokkeStage}, Бабушка/Суски: {lastGrandmaDeliveries}/{lastSuskiStage}";

        public StorylinesAndTelephoneSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
            for (int i = 0; i < VehicleKeyNames.Length; i++)
            {
                keySlots[i] = new KeySlot { Name = VehicleKeyNames[i] };
            }
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

            phoneRingFsm = null;
            phoneUseFsm = null;
            phoneTopicVar = null;
            phoneRandomTimesVar = null;
            phoneLogicGo = null;
            phoneUseHooked = false;
            isPhoneRinging = false;
            isPhoneCallActive = false;
            waitingRemoteCallEnd = false;
            remoteCallEnteredState2 = false;
            activePhoneTopic = string.Empty;
            activePhoneQuestId = string.Empty;
            activePhoneRandomTimes = 5;

            uncleStatesFsm = null;
            uncleSpeakFsm = null;
            uncleHooked = false;
            lastUncleStage = 0;
            lastUncleGifuKeys = false;
            lastUncleHasVan = false;

            for (int i = 0; i < keySlots.Length; i++)
            {
                keySlots[i].BoolVar = null;
                keySlots[i].IntVar = null;
                keySlots[i].CurrentValue = false;
            }
            keysInitialized = false;
            suppressKeysUntilTime = 0f;

            jokkeHikerFsm = null;
            jokkeAxeFsm = null;
            movingFsm = null;
            jokkeSuitcaseObj = null;
            suitcaseFsm = null;
            jokkeHooked = false;
            lastJokkeStage = 0;
            lastSuitcaseVisible = false;
            lastSuitcaseStolen = false;
            lastMovingDone = false;

            grandmaOrderFsm = null;
            grandmaNavFsm = null;
            grandmaAtChurchVar = null;
            grandmaHooked = false;
            lastGrandmaDeliveries = 0;
            lastGrandmaAtChurch = false;

            suskiStagesFsm = null;
            suskiStageVar = null;
            suskiHooked = false;
            lastSuskiStage = 0;

            janiStateFsm = null;
            petteriStateFsm = null;
            janiDeadVar = null;
            petteriDeadVar = null;
            lastJaniDown = false;
            lastPetteriDown = false;

            fighter1HitFsm = null;
            fighter1DefendFsm = null;
            fighter2HitFsm = null;
            fighter2DefendFsm = null;
            fighter1DeadVar = null;
            fighter2DeadVar = null;
            fighter1Hooked = false;
            fighter2Hooked = false;
            lastFighter1Ko = false;
            lastFighter2Ko = false;

            watchStealFsm = null;
            wristwatchOwnedVar = null;
            lastWatchStolen = false;

            isSuppressingPhone = false;
            isSuppressingUncle = false;
            isSuppressingKeys = false;
            isSuppressingJokke = false;
            isSuppressingGrandma = false;
            isSuppressingSuski = false;
            isSuppressingHooligans = false;

            nextScanTime = 0f;
            nextKeyPollTime = 0f;
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

            UpdateRemoteCallState();

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextKeyPollTime)
            {
                nextKeyPollTime = now + KeyPollIntervalSeconds;
                PollVehicleKeys(now);
                PollStoryProgress();
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
            return phoneRingFsm == null
                || phoneUseFsm == null
                || uncleStatesFsm == null
                || jokkeHikerFsm == null
                || suskiStagesFsm == null
                || grandmaOrderFsm == null
                || fighter1HitFsm == null;
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
                ScanTelephone(allObjects);
                ScanUncleAndKeys();
                ScanJokkeAndSuitcase(allObjects);
                ScanGrandmaAndSuski(allObjects);
                ScanHooligansAndFighters(allObjects);
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[StorylinesAndTelephoneSync] Ошибка сканирования: {ex.Message}");
            }
        }

        private void ScanTelephone(GameObject[] allObjects)
        {
            Transform logicTr = null;
            GameObject yard = GameObject.Find("YARD");
            if (yard != null)
            {
                logicTr = yard.transform.Find("Building/LIVINGROOM/Telephone/Logic");
            }
            if (logicTr == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go != null && go.name == "Telephone")
                    {
                        Transform t = go.transform.Find("Logic");
                        if (t != null)
                        {
                            logicTr = t;
                            break;
                        }
                    }
                }
            }
            if (logicTr == null) return;

            if (phoneRingFsm == null)
            {
                Transform ringTr = logicTr.Find("Ring") ?? logicTr;
                PlayMakerFSM ringFsm = PlayMakerBridge.FindFsm(ringTr.gameObject, "Ring")
                                    ?? PlayMakerBridge.FindFsm(ringTr.gameObject, "Logic")
                                    ?? ringTr.GetComponent<PlayMakerFSM>();
                if (ringFsm != null)
                {
                    phoneRingFsm = ringFsm;
                    ringFsm.InitializeSafe();
                    phoneTopicVar = ringFsm.FsmVariables.FindFsmString("Topic");
                    phoneRandomTimesVar = ringFsm.FsmVariables.FindFsmInt("RandomTimes");

                    PhoneRingWatcher watcher = ringFsm.gameObject.GetComponent<PhoneRingWatcher>()
                                            ?? ringFsm.gameObject.AddComponent<PhoneRingWatcher>();
                    watcher.OnRingEnabled = OnHostPhoneRingEnabled;

                    if (!hookedFsmIds.Contains(ringFsm.GetInstanceID()) && TryClaimSafe(ringFsm, "Story_PhoneRing"))
                    {
                        hookedFsmIds.Add(ringFsm.GetInstanceID());

                        if (ringFsm.HasState("State 2"))
                        {
                            ringFsm.InsertAction("State 2", () =>
                            {
                                if (isSuppressingPhone) return;
                                string topic = phoneTopicVar != null ? phoneTopicVar.Value : activePhoneTopic;
                                string questId = GetQuestIdFromTopic(topic);
                                isPhoneRinging = false;
                                isPhoneCallActive = true;
                                activePhoneTopic = topic ?? string.Empty;
                                activePhoneQuestId = questId ?? string.Empty;
                                ActivateQuestAndMarkers(activePhoneQuestId, activePhoneTopic);
                                SendPhonePacket(PhoneOpPickup, activePhoneTopic, activePhoneQuestId, activePhoneRandomTimes);
                            }, 0, true);
                        }
                    }
                }
            }

            if (phoneUseFsm == null)
            {
                Transform handleTr = logicTr.Find("UseHandle");
                if (handleTr != null)
                {
                    PlayMakerFSM useFsm = PlayMakerBridge.FindFsm(handleTr.gameObject, "Use")
                                       ?? handleTr.GetComponent<PlayMakerFSM>();
                    if (useFsm != null)
                    {
                        phoneUseFsm = useFsm;
                        useFsm.InitializeSafe();

                        if (!hookedFsmIds.Contains(useFsm.GetInstanceID()) && TryClaimSafe(useFsm, "Story_PhoneUse"))
                        {
                            hookedFsmIds.Add(useFsm.GetInstanceID());
                            phoneUseHooked = true;
                            useFsm.AddSafeGlobalTransition("MP_CLOSE", "Close phone");
                            if (useFsm.HasState("Close phone"))
                            {
                                useFsm.InsertAction("Close phone", () =>
                                {
                                    if (isSuppressingPhone) return;
                                    isPhoneRinging = false;
                                    isPhoneCallActive = false;
                                    waitingRemoteCallEnd = false;
                                    SendPhonePacket(PhoneOpHangup, string.Empty, string.Empty, 0);
                                }, 0, true);
                            }
                        }

                        if (ctx != null && ctx.Transport != null && ctx.Transport.IsClient)
                        {
                            FsmState closeState = useFsm.GetState("Close phone");
                            if (closeState != null && closeState.Actions != null && closeState.Actions.Length > 9 && closeState.Actions[9] != null)
                            {
                                closeState.Actions[9].Enabled = false;
                            }
                        }
                    }
                }
            }

            if (phoneLogicGo == null)
            {
                Transform logicChild = logicTr.Find("PhoneLogic");
                if (logicChild != null)
                {
                    phoneLogicGo = logicChild.gameObject;
                    if (ctx != null && ctx.Transport != null && ctx.Transport.IsClient)
                    {
                        phoneLogicGo.SetActive(false);
                    }
                }
            }
        }

        private void ScanUncleAndKeys()
        {
            EnsureKeyVariables();

            if (uncleStatesFsm == null)
            {
                GameObject uncleGo = GameObject.Find("UNCLE");
                if (uncleGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(uncleGo, "States") ?? uncleGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        uncleStatesFsm = fsm;
                        fsm.InitializeSafe();
                        FsmInt stVar = fsm.FsmVariables.FindFsmInt("UncleStage");
                        if (stVar != null) lastUncleStage = (byte)Mathf.Clamp(stVar.Value, 0, 255);
                        FsmBool gifuVar = fsm.FsmVariables.FindFsmBool("GifuKeys");
                        if (gifuVar != null) lastUncleGifuKeys = gifuVar.Value;

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_UncleStates"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            uncleHooked = true;
                            for (int st = 1; st <= 5; st++)
                            {
                                string stName = st.ToString();
                                byte capturedStage = (byte)st;
                                if (fsm.HasState(stName))
                                {
                                    fsm.AddSafeGlobalTransition("MP_STAGE_" + stName, stName);
                                    fsm.InsertAction(stName, () =>
                                    {
                                        if (isSuppressingUncle) return;
                                        FsmBool gv = fsm.FsmVariables.FindFsmBool("GifuKeys");
                                        bool hasGifu = gv != null && gv.Value;
                                        bool hasVan = capturedStage >= 2 || GetKeyState("PlayerKeyHayosiko");
                                        lastUncleStage = capturedStage;
                                        lastUncleGifuKeys = hasGifu;
                                        lastUncleHasVan = hasVan;
                                        SendKeysAndUnclePacket(includeUncle: true, capturedStage, hasGifu, hasVan);
                                    }, 0, true);
                                }
                            }
                        }
                    }
                }
            }

            if (uncleSpeakFsm == null)
            {
                GameObject uncleNpc = GameObject.Find("Uncle");
                if (uncleNpc != null)
                {
                    uncleSpeakFsm = PlayMakerBridge.FindFsm(uncleNpc, "Speak");
                }
            }
        }

        private void ScanJokkeAndSuitcase(GameObject[] allObjects)
        {
            if (jokkeHikerFsm == null)
            {
                GameObject jokkeGo = GameObject.Find("JokkeHiker1") ?? GameObject.Find("JokkeHiker2") ?? GameObject.Find("Jokke");
                if (jokkeGo != null)
                {
                    PlayMakerFSM hitch = PlayMakerBridge.FindFsm(jokkeGo, "Hitch") ?? jokkeGo.GetComponent<PlayMakerFSM>();
                    if (hitch != null)
                    {
                        jokkeHikerFsm = hitch;
                        hitch.InitializeSafe();

                        if (!hookedFsmIds.Contains(hitch.GetInstanceID()) && TryClaimSafe(hitch, "Story_JokkeHitch"))
                        {
                            hookedFsmIds.Add(hitch.GetInstanceID());
                            jokkeHooked = true;
                            if (hitch.HasState("CLIMB"))
                            {
                                hitch.AddSafeGlobalTransition("MP_CLIMB", "CLIMB");
                                hitch.InsertAction("CLIMB", () =>
                                {
                                    if (isSuppressingJokke) return;
                                    lastJokkeStage = 1;
                                    SendJokkePacket(JokkeOpHitchClimb, "CLIMB", true);
                                }, 0, true);
                            }
                            if (hitch.HasState("PROCEED"))
                            {
                                hitch.AddSafeGlobalTransition("MP_PROCEED", "PROCEED");
                                hitch.InsertAction("PROCEED", () =>
                                {
                                    if (isSuppressingJokke) return;
                                    lastJokkeStage = 2;
                                    SendJokkePacket(JokkeOpPaidRide, "PROCEED", true);
                                }, 0, true);
                            }
                        }
                    }
                }
            }

            if (movingFsm == null)
            {
                GameObject movingGo = GameObject.Find("Moving");
                if (movingGo != null)
                {
                    PlayMakerFSM fsm = movingGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        movingFsm = fsm;
                        fsm.InitializeSafe();
                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_JokkeMoving"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            if (fsm.HasState("Moving done"))
                            {
                                fsm.AddSafeGlobalTransition("MP_DONE", "Moving done");
                                fsm.InsertAction("Moving done", () =>
                                {
                                    if (isSuppressingJokke) return;
                                    lastMovingDone = true;
                                    SendJokkePacket(JokkeOpMovingDone, "Moving done", true);
                                }, 0, true);
                            }
                        }
                    }
                }
            }

            if (jokkeSuitcaseObj == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go != null && (go.name == "suitcase(itemx)" || go.name == "Suitcase"))
                    {
                        jokkeSuitcaseObj = go;
                        lastSuitcaseVisible = go.activeSelf;
                        PlayMakerFSM fsm = go.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            suitcaseFsm = fsm;
                            fsm.InitializeSafe();
                            if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_JokkeSuitcase"))
                            {
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                if (fsm.HasState("Enable murder"))
                                {
                                    fsm.AddSafeGlobalTransition("MP_STEAL", "Enable murder");
                                    fsm.InsertAction("Enable murder", () =>
                                    {
                                        if (isSuppressingJokke) return;
                                        lastSuitcaseStolen = true;
                                        SendJokkePacket(JokkeOpSuitcaseStolen, "Enable murder", true);
                                    }, 0, true);
                                }
                            }
                        }
                        break;
                    }
                }
            }

            if (jokkeAxeFsm == null)
            {
                GameObject axeGo = GameObject.Find("DrunkAxe") ?? GameObject.Find("Ax") ?? GameObject.Find("Axe");
                if (axeGo != null)
                {
                    PlayMakerFSM fsm = axeGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        jokkeAxeFsm = fsm;
                        fsm.InitializeSafe();
                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_JokkeAxe"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            string atkState = fsm.HasState("Attack") ? "Attack" : (fsm.HasState("ATTACK") ? "ATTACK" : null);
                            if (atkState != null)
                            {
                                fsm.AddSafeGlobalTransition("MP_ATTACK", atkState);
                                fsm.InsertAction(atkState, () =>
                                {
                                    if (isSuppressingJokke) return;
                                    SendJokkePacket(JokkeOpAxeAttack, atkState, true);
                                }, 0, true);
                            }
                        }
                    }
                }
            }
        }

        private void ScanGrandmaAndSuski(GameObject[] allObjects)
        {
            if (grandmaOrderFsm == null || grandmaNavFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    if (n != "Grandma" && n != "Sirkka" && n != "GRANDMA" && n != "ChurchGrandma" && n != "MUMMO")
                    {
                        continue;
                    }

                    PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int j = 0; j < fsms.Length; j++)
                    {
                        PlayMakerFSM fsm = fsms[j];
                        if (fsm == null) continue;
                        fsm.InitializeSafe();

                        if (grandmaOrderFsm == null && (fsm.FsmName == "Order" || fsm.FsmName == "Use" || fsm.FsmName == "Food" ||
                            fsm.HasState("Pay") || fsm.HasState("Give food") || fsm.HasState("Food")))
                        {
                            string delState = fsm.HasState("Pay") ? "Pay" : (fsm.HasState("Give food") ? "Give food" : (fsm.HasState("Food") ? "Food" : null));
                            if (delState != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_GrandmaOrder"))
                            {
                                grandmaOrderFsm = fsm;
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                grandmaHooked = true;
                                fsm.AddSafeGlobalTransition("MP_FOOD", delState);
                                fsm.InsertAction(delState, () =>
                                {
                                    if (isSuppressingGrandma) return;
                                    lastGrandmaDeliveries++;
                                    SendGrandmaSuskiPacket(StoryOpGrandmaDelivery, lastGrandmaDeliveries, lastGrandmaAtChurch, 0);
                                }, 0, true);
                            }
                        }

                        if (grandmaNavFsm == null && (fsm.FsmName == "Navigation" || fsm.HasState("Waypoint")))
                        {
                            if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_GrandmaNav"))
                            {
                                grandmaNavFsm = fsm;
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                grandmaAtChurchVar = fsm.FsmVariables.FindFsmBool("AtChurch");
                                if (grandmaAtChurchVar != null) lastGrandmaAtChurch = grandmaAtChurchVar.Value;

                                if (fsm.HasState("Waypoint"))
                                {
                                    fsm.AddSafeGlobalTransition("MP_WALK", "Waypoint");
                                    fsm.InsertAction("Waypoint", () =>
                                    {
                                        if (isSuppressingGrandma) return;
                                        bool atChurch = grandmaAtChurchVar != null && grandmaAtChurchVar.Value;
                                        lastGrandmaAtChurch = atChurch;
                                        SendGrandmaSuskiPacket(StoryOpGrandmaChurch, lastGrandmaDeliveries, atChurch, 0);
                                    }, 0, true);
                                }
                            }
                        }
                    }
                }
            }

            if (suskiStagesFsm == null)
            {
                GameObject suskiGo = GameObject.Find("SuskiStages") ?? GameObject.Find("Suski");
                if (suskiGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(suskiGo, "Logic")
                                    ?? PlayMakerBridge.FindFsm(suskiGo, "Stages")
                                    ?? suskiGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        suskiStagesFsm = fsm;
                        fsm.InitializeSafe();
                        suskiStageVar = fsm.FsmVariables.FindFsmInt("SuskiStage") ?? fsm.FsmVariables.FindFsmInt("Stage");
                        if (suskiStageVar != null) lastSuskiStage = (byte)Mathf.Clamp(suskiStageVar.Value, 0, 255);

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_SuskiStages"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            suskiHooked = true;
                            for (int s = 1; s <= 8; s++)
                            {
                                string stName = s.ToString();
                                byte stageByte = (byte)s;
                                if (fsm.HasState(stName))
                                {
                                    fsm.AddSafeGlobalTransition("MP_SUSKI_" + stName, stName);
                                    fsm.InsertAction(stName, () =>
                                    {
                                        if (isSuppressingSuski) return;
                                        lastSuskiStage = stageByte;
                                        SendGrandmaSuskiPacket(StoryOpSuskiStage, 0, false, stageByte);
                                    }, 0, true);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ScanHooligansAndFighters(GameObject[] allObjects)
        {
            // 1. Стритрейсеры Jani (жёлтая машина) и Petteri (серая машина) — БЕЗ захвата AI/Rigidbody вождения
            if (janiStateFsm == null || petteriStateFsm == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go == null) continue;
                    string n = go.name;
                    bool isJani = n == "KYLAJANI" || n == "Jani" || n == "Kylajani";
                    bool isPetteri = n == "AMIS2" || n == "PETTERI" || n == "Petteri" || n == "Amis";
                    if (!isJani && !isPetteri) continue;

                    PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                    for (int j = 0; j < fsms.Length; j++)
                    {
                        PlayMakerFSM fsm = fsms[j];
                        if (fsm == null) continue;
                        string fn = fsm.FsmName;
                        if (ContainsIgnoreCase(fn, "Drive") || ContainsIgnoreCase(fn, "Route") ||
                            ContainsIgnoreCase(fn, "AI") || ContainsIgnoreCase(fn, "Move") ||
                            ContainsIgnoreCase(fn, "Drivetrain"))
                        {
                            continue;
                        }

                        fsm.InitializeSafe();
                        FsmBool deadVar = fsm.FsmVariables.FindFsmBool("Dead") ?? fsm.FsmVariables.FindFsmBool("Crashed");
                        if (deadVar != null || ContainsIgnoreCase(fn, "Death") || ContainsIgnoreCase(fn, "Crash") || ContainsIgnoreCase(fn, "Talk"))
                        {
                            if (isJani && janiStateFsm == null)
                            {
                                janiStateFsm = fsm;
                                janiDeadVar = deadVar;
                                lastJaniDown = deadVar != null && deadVar.Value;
                            }
                            else if (isPetteri && petteriStateFsm == null)
                            {
                                petteriStateFsm = fsm;
                                petteriDeadVar = deadVar;
                                lastPetteriDown = deadVar != null && deadVar.Value;
                            }
                        }
                    }
                }
            }

            // 2. Бойцы Fighter и Fighter2 (кулачный бой у павильона)
            if (fighter1HitFsm == null)
            {
                GameObject f1 = GameObject.Find("Fighter");
                if (f1 != null)
                {
                    fighter1DefendFsm = PlayMakerBridge.FindFsm(f1, "Defend");
                    PlayMakerFSM hit = PlayMakerBridge.FindFsm(f1, "Hit");
                    if (hit != null)
                    {
                        fighter1HitFsm = hit;
                        hit.InitializeSafe();
                        fighter1DeadVar = hit.FsmVariables.FindFsmBool("Dead") ?? hit.FsmVariables.FindFsmBool("KO");
                        lastFighter1Ko = fighter1DeadVar != null && fighter1DeadVar.Value;
                        HookFighterFsm(hit, fighterIndex: 1, ref fighter1Hooked);
                    }
                }
            }

            if (fighter2HitFsm == null)
            {
                GameObject f2 = GameObject.Find("Fighter2");
                if (f2 != null)
                {
                    fighter2DefendFsm = PlayMakerBridge.FindFsm(f2, "Defend");
                    PlayMakerFSM hit = PlayMakerBridge.FindFsm(f2, "Hit");
                    if (hit != null)
                    {
                        fighter2HitFsm = hit;
                        hit.InitializeSafe();
                        fighter2DeadVar = hit.FsmVariables.FindFsmBool("Dead") ?? hit.FsmVariables.FindFsmBool("KO");
                        lastFighter2Ko = fighter2DeadVar != null && fighter2DeadVar.Value;
                        HookFighterFsm(hit, fighterIndex: 2, ref fighter2Hooked);
                    }
                }
            }

            // 3. Наручные часы (кража/выигрыш у бойца — НЕ захватываем Wristwatch/Data, чтобы не конфликтовать с WorldDomestic)
            if (wristwatchOwnedVar == null)
            {
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject go = allObjects[i];
                    if (go != null && go.name == "Wristwatch")
                    {
                        PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Data") ?? go.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            fsm.InitializeSafe();
                            wristwatchOwnedVar = fsm.FsmVariables.FindFsmBool("Owned");
                            if (wristwatchOwnedVar != null)
                            {
                                lastWatchStolen = wristwatchOwnedVar.Value;
                            }
                        }
                        break;
                    }
                }
            }

            if (watchStealFsm == null)
            {
                GameObject watchDrop = GameObject.Find("FighterWatch") ?? GameObject.Find("WatchDrop") ?? GameObject.Find("StealWatch");
                if (watchDrop != null)
                {
                    PlayMakerFSM fsm = watchDrop.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Story_FighterWatchSteal"))
                    {
                        watchStealFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        string takeState = fsm.HasState("Take") ? "Take" : (fsm.HasState("Pick") ? "Pick" : (fsm.HasState("Steal") ? "Steal" : null));
                        if (takeState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_STEALWATCH", takeState);
                            fsm.InsertAction(takeState, () =>
                            {
                                if (isSuppressingHooligans) return;
                                lastWatchStolen = true;
                                SendHooliganPacket(HooliganOpWatchTheft, 0, true);
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void HookFighterFsm(PlayMakerFSM hitFsm, int fighterIndex, ref bool hookedFlag)
        {
            if (hitFsm == null || hookedFsmIds.Contains(hitFsm.GetInstanceID())) return;
            if (!TryClaimSafe(hitFsm, $"Story_Fighter{fighterIndex}_Hit")) return;

            hookedFsmIds.Add(hitFsm.GetInstanceID());
            hookedFlag = true;

            if (hitFsm.HasState("HIT1"))
            {
                hitFsm.AddSafeGlobalTransition("MP_HIT", "HIT1");
                hitFsm.InsertAction("HIT1", () =>
                {
                    if (isSuppressingHooligans) return;
                    SendHooliganPacket(HooliganOpFighterHit, fighterIndex, false);
                }, 0, true);
            }

            string koState = hitFsm.HasState("DIE") ? "DIE" : (hitFsm.HasState("KO") ? "KO" : (hitFsm.HasState("Knockout") ? "Knockout" : null));
            if (koState != null)
            {
                hitFsm.AddSafeGlobalTransition("MP_KO", koState);
                hitFsm.InsertAction(koState, () =>
                {
                    if (isSuppressingHooligans) return;
                    if (fighterIndex == 1) lastFighter1Ko = true;
                    else lastFighter2Ko = true;
                    SendHooliganPacket(HooliganOpFighterHit, fighterIndex, true);
                }, 0, true);
            }
        }

        #endregion

        #region Polling & Update State Machine

        private void EnsureKeyVariables()
        {
            if (PlayMakerGlobals.Instance == null || PlayMakerGlobals.Instance.Variables == null) return;

            bool anyFound = false;
            for (int i = 0; i < keySlots.Length; i++)
            {
                KeySlot slot = keySlots[i];
                if (slot.BoolVar == null && slot.IntVar == null)
                {
                    slot.BoolVar = PlayMakerGlobals.Instance.Variables.FindFsmBool(slot.Name);
                    if (slot.BoolVar == null)
                    {
                        slot.IntVar = PlayMakerGlobals.Instance.Variables.FindFsmInt(slot.Name);
                    }
                }
                if (slot.BoolVar != null || slot.IntVar != null)
                {
                    anyFound = true;
                }
            }

            if (anyFound && !keysInitialized)
            {
                for (int i = 0; i < keySlots.Length; i++)
                {
                    keySlots[i].CurrentValue = ReadSlotValue(keySlots[i]);
                }
                keysInitialized = true;
            }
        }

        private void PollVehicleKeys(float now)
        {
            if (isSuppressingKeys || now < suppressKeysUntilTime) return;
            EnsureKeyVariables();
            if (!keysInitialized) return;

            bool changed = false;
            for (int i = 0; i < keySlots.Length; i++)
            {
                bool val = ReadSlotValue(keySlots[i]);
                if (val != keySlots[i].CurrentValue)
                {
                    keySlots[i].CurrentValue = val;
                    changed = true;
                }
            }

            if (changed)
            {
                SendKeysAndUnclePacket(includeUncle: false, lastUncleStage, lastUncleGifuKeys, lastUncleHasVan);
            }
        }

        private void PollStoryProgress()
        {
            if (!isSuppressingJokke && jokkeSuitcaseObj != null)
            {
                bool vis = jokkeSuitcaseObj.activeSelf;
                if (vis != lastSuitcaseVisible)
                {
                    lastSuitcaseVisible = vis;
                    SendJokkePacket(JokkeOpSuitcaseFound, "SuitcaseActive", vis);
                }
            }

            if (!isSuppressingSuski && !suskiHooked && suskiStagesFsm != null && suskiStageVar != null)
            {
                byte st = (byte)Mathf.Clamp(suskiStageVar.Value, 0, 255);
                if (st != lastSuskiStage && st > 0)
                {
                    lastSuskiStage = st;
                    SendGrandmaSuskiPacket(StoryOpSuskiStage, 0, false, st);
                }
            }

            if (!isSuppressingHooligans)
            {
                if (janiDeadVar != null && janiDeadVar.Value != lastJaniDown)
                {
                    lastJaniDown = janiDeadVar.Value;
                    SendHooliganPacket(HooliganOpRacerState, 1, lastJaniDown);
                }
                if (petteriDeadVar != null && petteriDeadVar.Value != lastPetteriDown)
                {
                    lastPetteriDown = petteriDeadVar.Value;
                    SendHooliganPacket(HooliganOpRacerState, 2, lastPetteriDown);
                }
                if (!fighter1Hooked && fighter1DeadVar != null && fighter1DeadVar.Value != lastFighter1Ko)
                {
                    lastFighter1Ko = fighter1DeadVar.Value;
                    SendHooliganPacket(HooliganOpFighterHit, 1, lastFighter1Ko);
                }
                if (!fighter2Hooked && fighter2DeadVar != null && fighter2DeadVar.Value != lastFighter2Ko)
                {
                    lastFighter2Ko = fighter2DeadVar.Value;
                    SendHooliganPacket(HooliganOpFighterHit, 2, lastFighter2Ko);
                }
                if (wristwatchOwnedVar != null && wristwatchOwnedVar.Value != lastWatchStolen)
                {
                    bool stolen = wristwatchOwnedVar.Value;
                    lastWatchStolen = stolen;
                    if (stolen && (lastFighter1Ko || lastFighter2Ko || lastJaniDown || lastPetteriDown))
                    {
                        SendHooliganPacket(HooliganOpWatchTheft, 0, true);
                    }
                }
            }
        }

        private void UpdateRemoteCallState()
        {
            if (!waitingRemoteCallEnd || phoneRingFsm == null) return;

            if (phoneRingFsm.ActiveStateName == "State 2")
            {
                remoteCallEnteredState2 = true;
                return;
            }

            if (remoteCallEnteredState2)
            {
                waitingRemoteCallEnd = false;
                remoteCallEnteredState2 = false;
                isPhoneCallActive = false;
                isPhoneRinging = false;

                isSuppressingPhone = true;
                try
                {
                    phoneRingFsm.SendEvent("ENDCALL");
                    if (phoneUseFsm != null && phoneUseHooked)
                    {
                        phoneUseFsm.SendEvent("MP_CLOSE");
                    }
                }
                finally
                {
                    isSuppressingPhone = false;
                }
            }
        }

        private void OnHostPhoneRingEnabled()
        {
            if (ctx == null || ctx.Transport == null || !ctx.Transport.IsServer || isSuppressingPhone) return;

            string topic = phoneTopicVar != null ? phoneTopicVar.Value : string.Empty;
            int randomTimes = phoneRandomTimesVar != null ? phoneRandomTimesVar.Value : 5;
            string questId = GetQuestIdFromTopic(topic);

            isPhoneRinging = true;
            isPhoneCallActive = false;
            activePhoneTopic = topic ?? string.Empty;
            activePhoneQuestId = questId ?? string.Empty;
            activePhoneRandomTimes = randomTimes;

            SendPhonePacket(PhoneOpRing, activePhoneTopic, activePhoneQuestId, randomTimes);
        }

        #endregion

        #region Sending

        private void SendPhonePacket(byte op, string topic, string questId, int randomTimes)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.TelephoneCallState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteString(topic ?? string.Empty);
                p.WriteString(questId ?? string.Empty);
                p.WriteInt32(randomTimes);
                ctx.Transport.Send(p);
            }
        }

        private void SendKeysAndUnclePacket(bool includeUncle, byte uncleStage, bool gifuKeys, bool hasVan)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.PlayerVehicleKeys);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(includeUncle);
                p.WriteByte(uncleStage);
                p.WriteBoolean(gifuKeys);
                p.WriteBoolean(hasVan);
                p.WriteByte((byte)keySlots.Length);
                for (int i = 0; i < keySlots.Length; i++)
                {
                    p.WriteBoolean(GetKeyState(keySlots[i].Name));
                }
                ctx.Transport.Send(p);
            }
        }

        private void SendJokkePacket(byte op, string stateName, bool flag)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.JokkeRideAndSuitcase);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteString(stateName ?? string.Empty);
                p.WriteBoolean(flag);
                ctx.Transport.Send(p);
            }
        }

        private void SendGrandmaSuskiPacket(byte op, int count, bool atChurch, byte suskiStage)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.GrandmaAndSuski);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteInt32(count);
                p.WriteBoolean(atChurch);
                p.WriteByte(suskiStage);
                ctx.Transport.Send(p);
            }
        }

        private void SendHooliganPacket(byte op, int targetIndex, bool stateFlag)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.HooligansAndFight);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(op);
                p.WriteInt32(targetIndex);
                p.WriteBoolean(stateFlag);
                ctx.Transport.Send(p);
            }
        }

        private void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            EnsureKeyVariables();

            using (var p = new PacketWriter(PacketType.StoryAndQuestsSync))
            {
                p.WriteByte(EconomyProtocol.StorySnapshot);
                p.WriteInt32(LocalPlayerId);

                // 1. Телефон и активные маркеры на карте
                p.WriteBoolean(isPhoneRinging);
                p.WriteBoolean(isPhoneCallActive);
                p.WriteString(activePhoneTopic ?? string.Empty);
                p.WriteString(activePhoneQuestId ?? string.Empty);
                p.WriteInt32(activePhoneRandomTimes);

                byte activeMarkersMaskCount = 0;
                List<string> activeMarkers = CollectActiveCallerMarkers();
                activeMarkersMaskCount = (byte)Math.Min(activeMarkers.Count, 255);
                p.WriteByte(activeMarkersMaskCount);
                for (int i = 0; i < activeMarkersMaskCount; i++)
                {
                    p.WriteString(activeMarkers[i]);
                }

                // 2. Дядя и ключи авто
                p.WriteByte(lastUncleStage);
                p.WriteBoolean(lastUncleGifuKeys);
                p.WriteBoolean(lastUncleHasVan);
                p.WriteByte((byte)keySlots.Length);
                for (int i = 0; i < keySlots.Length; i++)
                {
                    p.WriteBoolean(GetKeyState(keySlots[i].Name));
                }

                // 3. Йокке и чемодан
                p.WriteByte(lastJokkeStage);
                p.WriteBoolean(jokkeSuitcaseObj != null ? jokkeSuitcaseObj.activeSelf : lastSuitcaseVisible);
                p.WriteBoolean(lastSuitcaseStolen);
                p.WriteBoolean(lastMovingDone);

                // 4. Бабушка и Суски
                p.WriteInt32(lastGrandmaDeliveries);
                p.WriteBoolean(grandmaAtChurchVar != null ? grandmaAtChurchVar.Value : lastGrandmaAtChurch);
                p.WriteByte(suskiStageVar != null ? (byte)Mathf.Clamp(suskiStageVar.Value, 0, 255) : lastSuskiStage);

                // 5. Хулиганы, бойцы и часы
                p.WriteBoolean(lastJaniDown);
                p.WriteBoolean(lastPetteriDown);
                p.WriteBoolean(lastFighter1Ko);
                p.WriteBoolean(lastFighter2Ko);
                p.WriteBoolean(wristwatchOwnedVar != null ? wristwatchOwnedVar.Value : lastWatchStolen);

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
                case EconomyProtocol.TelephoneCallState:
                    HandlePhonePacket(reader);
                    break;

                case EconomyProtocol.PlayerVehicleKeys:
                    HandleKeysPacket(reader);
                    break;

                case EconomyProtocol.JokkeRideAndSuitcase:
                    HandleJokkePacket(reader);
                    break;

                case EconomyProtocol.GrandmaAndSuski:
                    HandleGrandmaSuskiPacket(reader);
                    break;

                case EconomyProtocol.HooligansAndFight:
                    HandleHooliganPacket(reader);
                    break;

                case EconomyProtocol.StorySnapshot:
                    HandleStorySnapshot(reader);
                    break;
            }
        }

        private void HandlePhonePacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 7) return;
            byte op = reader.ReadByte();
            string topic = reader.ReadString();
            string questId = reader.ReadString();
            int randomTimes = reader.ReadInt32();

            ApplyRemotePhoneState(op, topic, questId, randomTimes);
        }

        private void ApplyRemotePhoneState(byte op, string topic, string questId, int randomTimes)
        {
            isSuppressingPhone = true;
            try
            {
                if (op == PhoneOpRing)
                {
                    isPhoneRinging = true;
                    isPhoneCallActive = false;
                    activePhoneTopic = topic ?? string.Empty;
                    activePhoneQuestId = questId ?? string.Empty;
                    activePhoneRandomTimes = randomTimes;

                    if (phoneTopicVar != null) phoneTopicVar.Value = activePhoneTopic;
                    if (phoneRandomTimesVar != null) phoneRandomTimesVar.Value = randomTimes;
                    if (phoneRingFsm != null)
                    {
                        phoneRingFsm.gameObject.SetActive(true);
                    }
                    ctx?.Log?.Invoke($"[Телефон] Входящий звонок: {activePhoneTopic} ({activePhoneQuestId})");
                }
                else if (op == PhoneOpPickup)
                {
                    isPhoneRinging = false;
                    isPhoneCallActive = true;
                    activePhoneTopic = topic ?? string.Empty;
                    activePhoneQuestId = questId ?? string.Empty;

                    if (phoneRingFsm != null)
                    {
                        if (!phoneRingFsm.gameObject.activeSelf) phoneRingFsm.gameObject.SetActive(true);
                        if (phoneTopicVar != null && !string.IsNullOrEmpty(activePhoneTopic)) phoneTopicVar.Value = activePhoneTopic;
                        phoneRingFsm.SendEvent("ANSWER");
                        waitingRemoteCallEnd = true;
                        remoteCallEnteredState2 = false;
                    }
                    ActivateQuestAndMarkers(activePhoneQuestId, activePhoneTopic);
                }
                else if (op == PhoneOpHangup)
                {
                    isPhoneRinging = false;
                    isPhoneCallActive = false;
                    waitingRemoteCallEnd = false;
                    remoteCallEnteredState2 = false;

                    if (phoneUseFsm != null && phoneUseHooked)
                    {
                        phoneUseFsm.SendEvent("MP_CLOSE");
                    }
                    else if (phoneRingFsm != null)
                    {
                        phoneRingFsm.SendEvent("ENDCALL");
                    }
                }
            }
            finally
            {
                isSuppressingPhone = false;
            }
        }

        private void HandleKeysPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 5) return;
            bool includeUncle = reader.ReadBoolean();
            byte uncleStage = reader.ReadByte();
            bool gifuKeys = reader.ReadBoolean();
            bool hasVan = reader.ReadBoolean();
            byte keyCount = reader.ReadByte();

            bool[] vals = new bool[keyCount];
            for (int i = 0; i < keyCount && reader.UnreadBytes >= 1; i++)
            {
                vals[i] = reader.ReadBoolean();
            }

            if (includeUncle)
            {
                ApplyUncleState(uncleStage, gifuKeys, hasVan, triggerFsm: true);
            }
            ApplyKeyValues(vals);
        }

        private void ApplyUncleState(byte stage, bool gifuKeys, bool hasVan, bool triggerFsm)
        {
            isSuppressingUncle = true;
            try
            {
                lastUncleStage = stage;
                lastUncleGifuKeys = gifuKeys;
                lastUncleHasVan = hasVan;

                if (uncleStatesFsm != null && stage > 0)
                {
                    FsmInt stVar = uncleStatesFsm.FsmVariables.FindFsmInt("UncleStage");
                    if (stVar != null) stVar.Value = stage;
                    FsmBool gv = uncleStatesFsm.FsmVariables.FindFsmBool("GifuKeys");
                    if (gv != null) gv.Value = gifuKeys;

                    if (triggerFsm && uncleHooked)
                    {
                        uncleStatesFsm.SendEvent("MP_STAGE_" + stage);
                    }
                }

                if (triggerFsm && uncleSpeakFsm != null)
                {
                    uncleSpeakFsm.SendEvent("PROCEED");
                }
            }
            finally
            {
                isSuppressingUncle = false;
            }
        }

        private void ApplyKeyValues(bool[] vals)
        {
            if (vals == null || vals.Length == 0) return;

            EnsureKeyVariables();
            isSuppressingKeys = true;
            suppressKeysUntilTime = Time.time + 0.6f;
            try
            {
                int count = Math.Min(vals.Length, keySlots.Length);
                for (int i = 0; i < count; i++)
                {
                    SetSlotValue(keySlots[i], vals[i]);
                }
            }
            finally
            {
                isSuppressingKeys = false;
            }
        }

        private void HandleJokkePacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 3) return;
            byte op = reader.ReadByte();
            string stateName = reader.ReadString();
            bool flag = reader.ReadBoolean();

            ApplyJokkeAction(op, stateName, flag, triggerFsm: true);
        }

        private void ApplyJokkeAction(byte op, string stateName, bool flag, bool triggerFsm)
        {
            isSuppressingJokke = true;
            try
            {
                switch (op)
                {
                    case JokkeOpHitchClimb:
                        lastJokkeStage = 1;
                        if (triggerFsm && jokkeHikerFsm != null)
                        {
                            jokkeHikerFsm.SendEvent(jokkeHooked ? "MP_CLIMB" : "CLIMB");
                        }
                        break;

                    case JokkeOpPaidRide:
                        lastJokkeStage = 2;
                        if (triggerFsm && jokkeHikerFsm != null)
                        {
                            jokkeHikerFsm.SendEvent(jokkeHooked ? "MP_PROCEED" : "PROCEED");
                        }
                        break;

                    case JokkeOpMovingDone:
                        lastMovingDone = flag;
                        if (triggerFsm && movingFsm != null && flag)
                        {
                            movingFsm.SendEvent("MP_DONE");
                        }
                        break;

                    case JokkeOpSuitcaseFound:
                        lastSuitcaseVisible = flag;
                        if (jokkeSuitcaseObj != null)
                        {
                            jokkeSuitcaseObj.SetActive(flag);
                        }
                        break;

                    case JokkeOpSuitcaseStolen:
                        lastSuitcaseStolen = flag;
                        if (triggerFsm && suitcaseFsm != null && flag)
                        {
                            suitcaseFsm.SendEvent("MP_STEAL");
                        }
                        break;

                    case JokkeOpAxeAttack:
                        if (triggerFsm && jokkeAxeFsm != null && flag)
                        {
                            jokkeAxeFsm.SendEvent("MP_ATTACK");
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingJokke = false;
            }
        }

        private void HandleGrandmaSuskiPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 7) return;
            byte op = reader.ReadByte();
            int count = reader.ReadInt32();
            bool atChurch = reader.ReadBoolean();
            byte suskiStage = reader.ReadByte();

            switch (op)
            {
                case StoryOpGrandmaDelivery:
                    isSuppressingGrandma = true;
                    try
                    {
                        lastGrandmaDeliveries = count;
                        if (grandmaOrderFsm != null && grandmaHooked)
                        {
                            grandmaOrderFsm.SendEvent("MP_FOOD");
                        }
                        ctx?.Log?.Invoke($"[Бабушка] Доставка продуктов бабушке Сиркке (#{count}).");
                    }
                    finally
                    {
                        isSuppressingGrandma = false;
                    }
                    break;

                case StoryOpGrandmaChurch:
                    isSuppressingGrandma = true;
                    try
                    {
                        lastGrandmaAtChurch = atChurch;
                        if (grandmaAtChurchVar != null) grandmaAtChurchVar.Value = atChurch;
                        if (grandmaNavFsm != null)
                        {
                            grandmaNavFsm.SendEvent("MP_WALK");
                        }
                    }
                    finally
                    {
                        isSuppressingGrandma = false;
                    }
                    break;

                case StoryOpSuskiStage:
                    ApplySuskiStage(suskiStage, triggerFsm: true);
                    break;
            }
        }

        private void ApplySuskiStage(byte suskiStage, bool triggerFsm)
        {
            isSuppressingSuski = true;
            try
            {
                lastSuskiStage = suskiStage;
                if (suskiStagesFsm != null && suskiStage > 0)
                {
                    if (suskiStageVar != null) suskiStageVar.Value = suskiStage;
                    if (triggerFsm && suskiHooked)
                    {
                        suskiStagesFsm.SendEvent("MP_SUSKI_" + suskiStage);
                    }
                }
            }
            finally
            {
                isSuppressingSuski = false;
            }
        }

        private void HandleHooliganPacket(PacketReader reader)
        {
            if (reader.UnreadBytes < 6) return;
            byte op = reader.ReadByte();
            int targetIdx = reader.ReadInt32();
            bool stateFlag = reader.ReadBoolean();

            ApplyHooliganState(op, targetIdx, stateFlag, triggerFsm: true);
        }

        private void ApplyHooliganState(byte op, int targetIdx, bool stateFlag, bool triggerFsm)
        {
            isSuppressingHooligans = true;
            try
            {
                switch (op)
                {
                    case HooliganOpRacerState:
                        if (targetIdx == 1)
                        {
                            lastJaniDown = stateFlag;
                            if (janiDeadVar != null) janiDeadVar.Value = stateFlag;
                        }
                        else if (targetIdx == 2)
                        {
                            lastPetteriDown = stateFlag;
                            if (petteriDeadVar != null) petteriDeadVar.Value = stateFlag;
                        }
                        break;

                    case HooliganOpFighterHit:
                        if (targetIdx == 1)
                        {
                            if (stateFlag) lastFighter1Ko = true;
                            if (fighter1DeadVar != null && stateFlag) fighter1DeadVar.Value = true;
                            if (triggerFsm && fighter1HitFsm != null && fighter1Hooked)
                            {
                                fighter1HitFsm.SendEvent(stateFlag ? "MP_KO" : "MP_HIT");
                            }
                            if (triggerFsm && fighter1DefendFsm != null) fighter1DefendFsm.SendEvent("HIT");
                        }
                        else if (targetIdx == 2)
                        {
                            if (stateFlag) lastFighter2Ko = true;
                            if (fighter2DeadVar != null && stateFlag) fighter2DeadVar.Value = true;
                            if (triggerFsm && fighter2HitFsm != null && fighter2Hooked)
                            {
                                fighter2HitFsm.SendEvent(stateFlag ? "MP_KO" : "MP_HIT");
                            }
                            if (triggerFsm && fighter2DefendFsm != null) fighter2DefendFsm.SendEvent("HIT");
                        }
                        break;

                    case HooliganOpWatchTheft:
                        lastWatchStolen = stateFlag;
                        if (wristwatchOwnedVar != null) wristwatchOwnedVar.Value = stateFlag;
                        if (triggerFsm && watchStealFsm != null && stateFlag)
                        {
                            watchStealFsm.SendEvent("MP_STEALWATCH");
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingHooligans = false;
            }
        }

        private void HandleStorySnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 5) return;

            bool ringing = reader.ReadBoolean();
            bool callActive = reader.ReadBoolean();
            string topic = reader.ReadString();
            string questId = reader.ReadString();
            if (reader.UnreadBytes < 5) return;
            int randomTimes = reader.ReadInt32();

            byte markerCount = reader.ReadByte();
            for (int i = 0; i < markerCount; i++)
            {
                if (reader.UnreadBytes < 1) break;
                string marker = reader.ReadString();
                ActivateCallerObject(marker);
            }

            if (ringing)
            {
                ApplyRemotePhoneState(PhoneOpRing, topic, questId, randomTimes);
            }
            else if (callActive)
            {
                ApplyRemotePhoneState(PhoneOpPickup, topic, questId, randomTimes);
            }

            if (reader.UnreadBytes < 4) return;
            byte uncleStage = reader.ReadByte();
            bool gifuKeys = reader.ReadBoolean();
            bool hasVan = reader.ReadBoolean();
            byte keyCount = reader.ReadByte();
            bool[] keyVals = new bool[keyCount];
            for (int i = 0; i < keyCount && reader.UnreadBytes >= 1; i++)
            {
                keyVals[i] = reader.ReadBoolean();
            }
            ApplyUncleState(uncleStage, gifuKeys, hasVan, triggerFsm: false);
            ApplyKeyValues(keyVals);

            if (reader.UnreadBytes < 4) return;
            byte jokkeStage = reader.ReadByte();
            bool suitcaseVis = reader.ReadBoolean();
            bool suitcaseStolen = reader.ReadBoolean();
            bool movingDone = reader.ReadBoolean();
            lastJokkeStage = jokkeStage;
            ApplyJokkeAction(JokkeOpSuitcaseFound, "SuitcaseActive", suitcaseVis, triggerFsm: false);
            if (suitcaseStolen) ApplyJokkeAction(JokkeOpSuitcaseStolen, "Enable murder", true, triggerFsm: false);
            if (movingDone) ApplyJokkeAction(JokkeOpMovingDone, "Moving done", true, triggerFsm: false);

            if (reader.UnreadBytes < 6) return;
            int grandmaDel = reader.ReadInt32();
            bool grandmaChurch = reader.ReadBoolean();
            byte suskiStage = reader.ReadByte();
            lastGrandmaDeliveries = grandmaDel;
            lastGrandmaAtChurch = grandmaChurch;
            if (grandmaAtChurchVar != null) grandmaAtChurchVar.Value = grandmaChurch;
            ApplySuskiStage(suskiStage, triggerFsm: false);

            if (reader.UnreadBytes >= 5)
            {
                bool janiDown = reader.ReadBoolean();
                bool petteriDown = reader.ReadBoolean();
                bool f1Ko = reader.ReadBoolean();
                bool f2Ko = reader.ReadBoolean();
                bool watchOwned = reader.ReadBoolean();

                ApplyHooliganState(HooliganOpRacerState, 1, janiDown, triggerFsm: false);
                ApplyHooliganState(HooliganOpRacerState, 2, petteriDown, triggerFsm: false);
                if (f1Ko) ApplyHooliganState(HooliganOpFighterHit, 1, true, triggerFsm: false);
                if (f2Ko) ApplyHooliganState(HooliganOpFighterHit, 2, true, triggerFsm: false);
                if (watchOwned) ApplyHooliganState(HooliganOpWatchTheft, 0, true, triggerFsm: false);
            }
        }

        #endregion

        #region Helpers

        private int CountOwnedKeys()
        {
            int count = 0;
            for (int i = 0; i < keySlots.Length; i++)
            {
                if (GetKeyState(keySlots[i].Name)) count++;
            }
            return count;
        }

        private bool GetKeyState(string keyName)
        {
            for (int i = 0; i < keySlots.Length; i++)
            {
                if (keySlots[i].Name == keyName)
                {
                    return ReadSlotValue(keySlots[i]);
                }
            }
            return false;
        }

        private static bool ReadSlotValue(KeySlot slot)
        {
            if (slot == null) return false;
            if (slot.BoolVar != null) return slot.BoolVar.Value;
            if (slot.IntVar != null) return slot.IntVar.Value != 0;
            return slot.CurrentValue;
        }

        private static void SetSlotValue(KeySlot slot, bool val)
        {
            if (slot == null) return;
            slot.CurrentValue = val;
            if (slot.BoolVar != null) slot.BoolVar.Value = val;
            if (slot.IntVar != null) slot.IntVar.Value = val ? 1 : 0;
        }

        private void ActivateQuestAndMarkers(string questId, string topic)
        {
            if (phoneRingFsm != null && !string.IsNullOrEmpty(topic))
            {
                if (phoneRingFsm.HasState(topic))
                {
                    phoneRingFsm.SendEvent(topic);
                }
                else
                {
                    string ev = topic.ToUpperInvariant();
                    if (ev.Contains("WOOD")) phoneRingFsm.SendEvent("WOOD1");
                    else if (ev.Contains("DRUNK")) phoneRingFsm.SendEvent("DRUNK");
                    else if (ev.Contains("SHIT1")) phoneRingFsm.SendEvent("SHIT1");
                    else if (ev.Contains("SHIT2")) phoneRingFsm.SendEvent("SHIT2");
                    else if (ev.Contains("SHIT3")) phoneRingFsm.SendEvent("SHIT3");
                    else if (ev.Contains("SHIT4")) phoneRingFsm.SendEvent("SHIT4");
                    else if (ev.Contains("SHIT5")) phoneRingFsm.SendEvent("SHIT5");
                    else if (ev.Contains("MOVING")) phoneRingFsm.SendEvent("MOVING");
                    else if (ev.Contains("MUMMO1")) phoneRingFsm.SendEvent("MUMMO1");
                    else if (ev.Contains("MUMMO2")) phoneRingFsm.SendEvent("MUMMO2");
                    else if (ev.Contains("MUMMO3")) phoneRingFsm.SendEvent("MUMMO3");
                    else if (ev.Contains("UNCLE")) phoneRingFsm.SendEvent("UNCLE");
                }
            }

            string combined = ((questId ?? string.Empty) + " " + (topic ?? string.Empty)).ToUpperInvariant();
            string targetCaller = null;

            if (combined.Contains("WOOD")) targetCaller = "call_woods";
            else if (combined.Contains("DRUNK")) targetCaller = "call_lift";
            else if (combined.Contains("SHIT") || combined.Contains("SEWAGE"))
            {
                if (combined.Contains("SHIT2") || combined.Contains("SEWAGE2")) targetCaller = "shitwell02_call";
                else if (combined.Contains("SHIT3") || combined.Contains("SEWAGE3")) targetCaller = "shitwell03_call";
                else if (combined.Contains("SHIT4") || combined.Contains("SEWAGE4")) targetCaller = "shitwell04_call";
                else if (combined.Contains("SHIT5") || combined.Contains("SEWAGE5")) targetCaller = "shitwell05_call";
                else targetCaller = "shitwell01_call";
            }
            else if (combined.Contains("MOVING")) targetCaller = "call_movinghelp";
            else if (combined.Contains("MUMMO1")) targetCaller = "call_mummo1";
            else if (combined.Contains("MUMMO2")) targetCaller = "call_mummo2";
            else if (combined.Contains("MUMMO3")) targetCaller = "call_mummo3";
            else if (combined.Contains("UNCLE")) targetCaller = "call_uncle";

            if (!string.IsNullOrEmpty(targetCaller))
            {
                ActivateCallerObject(targetCaller);
            }
        }

        private static void ActivateCallerObject(string callerName)
        {
            if (string.IsNullOrEmpty(callerName)) return;

            GameObject go = GameObject.Find(callerName);
            if (go != null)
            {
                go.SetActive(true);
                return;
            }

            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == callerName)
                {
                    all[i].SetActive(true);
                    return;
                }
            }
        }

        private static List<string> CollectActiveCallerMarkers()
        {
            var result = new List<string>();
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < CallerMarkerNames.Length; i++)
            {
                string marker = CallerMarkerNames[i];
                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j] != null && all[j].name == marker && all[j].activeSelf)
                    {
                        result.Add(marker);
                        break;
                    }
                }
            }
            return result;
        }

        private static string GetQuestIdFromTopic(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return string.Empty;
            string upper = topic.ToUpperInvariant();
            if (upper.Contains("WOOD")) return "Jobs_WoodOrder";
            if (upper.Contains("DRUNK")) return "Jobs_DrunkGuy";
            if (upper.Contains("SHIT") || upper.Contains("SEWAGE")) return "Jobs_Sewage";
            if (upper.Contains("MOVING")) return "Jobs_MovingJokke";
            if (upper.Contains("MUMMO") || upper.Contains("GRANDMA")) return "Jobs_Grandma";
            if (upper.Contains("UNCLE")) return "Jobs_Uncle";
            if (upper.Contains("FLEETARI")) return "Jobs_Fleetari";
            if (upper.Contains("SUSKI")) return "Jobs_Suski";
            return topic;
        }

        private static bool ContainsIgnoreCase(string source, string needle)
        {
            return !string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(needle) &&
                   source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
