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
    /// Синхронизация сауны (каменка Kiuas, подача пара ковшом, мочеиспускание на камни),
    /// дровяной печи на острове и пожаров дома (FireBedroom1/2, HOUSEFIRE, тушение огнетушителем).
    /// Использует PacketType.DomesticSaunaAndFireSync = 111.
    /// </summary>
    internal sealed class SaunaAndFireSync
    {
        private sealed class TrackedFireSource
        {
            public int KeyHash;
            public string Name;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool FireBoolVar;
            public FsmBool BurningBoolVar;
            public FsmFloat FireFloatVar;
            public bool LastOnFire;
            public bool IsHooked;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private const float PeeRadiusSqr = 4.5f;
        private const float PeeCooldownSeconds = 1.5f;
        private const float ScanIntervalSeconds = 5.0f;
        private const float FirePollIntervalSeconds = 0.35f;
        private const float HeatPollIntervalSeconds = 1.0f;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly List<TrackedFireSource> fireSources = new List<TrackedFireSource>();
        private readonly Dictionary<int, TrackedFireSource> fireByHash = new Dictionary<int, TrackedFireSource>();
        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly Dictionary<int, float> lastPeeOnSaunaByPlayer = new Dictionary<int, float>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        // Домашняя сауна (Kiuas)
        private Transform kiuasTr;
        private PlayMakerFSM kiuasFsm;
        private FsmFloat kiuasTempVar;
        private FsmFloat kiuasHeatVar;

        private PlayMakerFSM saunaPowerFsm;
        private FsmFloat saunaPowerRotVar;
        private Transform saunaPowerKnobMesh;

        private PlayMakerFSM saunaTimerFsm;
        private FsmFloat saunaTimerRotVar;
        private FsmFloat saunaTimerMath1Var;
        private Transform saunaTimerKnobMesh;

        private PlayMakerFSM saunaStoveTriggerFsm;

        // Островная сауна (Cottage)
        private PlayMakerFSM cottageStoveFsm;
        private FsmFloat cottageHeatVar;
        private FsmBool cottageLitVar;

        // Сгоревший дом
        private GameObject burntHouseBase;
        private GameObject burntHouseStack;
        private GameObject normalHouse;

        private float lastSentPowerRot = -999f;
        private float lastSentTimerRot = -999f;
        private float lastSentKiuasTemp = -999f;
        private float lastSentCottageHeat = -999f;
        private bool lastSentCottageLit;
        private bool lastHouseBurnt;

        private bool isSuppressingSauna;
        private bool isSuppressingSteam;
        private bool isSuppressingFire;

        private float nextScanTime;
        private float nextFirePollTime;
        private float nextHeatPollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary
        {
            get
            {
                int activeFires = 0;
                for (int i = 0; i < fireSources.Count; i++)
                {
                    if (fireSources[i].LastOnFire) activeFires++;
                }
                string saunaState = (kiuasTr != null) ? "Kiuas OK" : "Поиск";
                return $"Сауна: {saunaState}, Пожары: {activeFires}/{fireSources.Count}{(lastHouseBurnt ? " [СГОРЕЛ]" : "")}";
            }
        }

        public SaunaAndFireSync(OscHostContext context, string moduleId)
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
            fireSources.Clear();
            fireByHash.Clear();
            hookedFsmIds.Clear();
            lastPeeOnSaunaByPlayer.Clear();
            pendingSnapshots.Clear();

            kiuasTr = null;
            kiuasFsm = null;
            kiuasTempVar = null;
            kiuasHeatVar = null;

            saunaPowerFsm = null;
            saunaPowerRotVar = null;
            saunaPowerKnobMesh = null;

            saunaTimerFsm = null;
            saunaTimerRotVar = null;
            saunaTimerMath1Var = null;
            saunaTimerKnobMesh = null;

            saunaStoveTriggerFsm = null;

            cottageStoveFsm = null;
            cottageHeatVar = null;
            cottageLitVar = null;

            burntHouseBase = null;
            burntHouseStack = null;
            normalHouse = null;

            lastSentPowerRot = -999f;
            lastSentTimerRot = -999f;
            lastSentKiuasTemp = -999f;
            lastSentCottageHeat = -999f;
            lastSentCottageLit = false;
            lastHouseBurnt = false;

            isSuppressingSauna = false;
            isSuppressingSteam = false;
            isSuppressingFire = false;

            nextScanTime = 0f;
            nextFirePollTime = 0f;
            nextHeatPollTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            if (now >= nextScanTime)
            {
                nextScanTime = now + ScanIntervalSeconds;
                if (kiuasTr == null || saunaPowerFsm == null || saunaTimerFsm == null ||
                    saunaStoveTriggerFsm == null || fireSources.Count == 0)
                {
                    ScanAll();
                }
            }

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextHeatPollTime)
            {
                nextHeatPollTime = now + HeatPollIntervalSeconds;
                PollSaunaTemperature();
            }

            if (now >= nextFirePollTime)
            {
                nextFirePollTime = now + FirePollIntervalSeconds;
                PollFireStates();
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

            int targetId = (ctx?.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            lastPeeOnSaunaByPlayer.Remove(targetId);
        }

        public void HandleUrinateBridge(PlayerUrinateStateEvent evt)
        {
            if (ctx == null || !ctx.IsInGameWorld || !evt.IsPeeing) return;

            if (kiuasTr == null)
            {
                ScanSauna();
            }
            if (kiuasTr == null) return;

            CheckPeeOnSauna(evt);
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subType = reader.ReadByte();
            switch (subType)
            {
                case DomesticProtocol.SaunaStoveState:
                    HandleSaunaStoveState(reader);
                    break;

                case DomesticProtocol.SaunaWaterSteam:
                    HandleSaunaWaterSteam(reader);
                    break;

                case DomesticProtocol.SaunaPeeSteamBurn:
                    HandleSaunaPeeSteamBurn(reader);
                    break;

                case DomesticProtocol.HouseFireState:
                    HandleHouseFireState(reader);
                    break;

                case DomesticProtocol.SaunaFireSnapshot:
                    HandleSaunaFireSnapshot(reader);
                    break;
            }
        }

        #region Scanning & Hooking

        private void ScanAll()
        {
            try
            {
                ScanSauna();
                ScanCottageStove();
                ScanFireSources();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[SaunaAndFire] Ошибка сканирования: {ex.Message}");
            }
        }

        private void ScanSauna()
        {
            Transform sauna = null;
            GameObject yardGo = GameObject.Find("YARD");
            if (yardGo != null)
            {
                sauna = yardGo.transform.Find("Building/SAUNA/Sauna");
            }

            if (sauna == null)
            {
                GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    GameObject go = allGo[i];
                    if (go != null && go.name == "Sauna" && go.transform.Find("Kiuas") != null)
                    {
                        sauna = go.transform;
                        break;
                    }
                }
            }

            if (kiuasTr == null)
            {
                Transform foundKiuas = sauna != null ? sauna.Find("Kiuas") : null;
                if (foundKiuas == null)
                {
                    GameObject kiuasGo = GameObject.Find("Kiuas") ?? GameObject.Find("SmokeHomeKiuas");
                    if (kiuasGo != null) foundKiuas = kiuasGo.transform;
                }

                if (foundKiuas != null)
                {
                    kiuasTr = foundKiuas;
                    kiuasFsm = PlayMakerBridge.FindFsm(foundKiuas.gameObject, "Heat")
                        ?? PlayMakerBridge.FindFsm(foundKiuas.gameObject, "Logic")
                        ?? PlayMakerBridge.FindFsm(foundKiuas.gameObject, "Kiuas")
                        ?? foundKiuas.GetComponent<PlayMakerFSM>();

                    if (kiuasFsm != null)
                    {
                        kiuasFsm.InitializeSafe();
                        kiuasTempVar = kiuasFsm.FsmVariables.FindFsmFloat("Temperature")
                            ?? kiuasFsm.FsmVariables.FindFsmFloat("Temp");
                        kiuasHeatVar = kiuasFsm.FsmVariables.FindFsmFloat("Heat")
                            ?? kiuasTempVar;
                    }
                }
            }

            if (sauna == null) return;

            if (saunaPowerFsm == null)
            {
                Transform powerKnob = sauna.Find("Kiuas/ButtonPower");
                if (powerKnob != null)
                {
                    PlayMakerFSM fsm = powerKnob.GetComponent<PlayMakerFSM>();
                    if (fsm != null && TryClaimSafe(fsm, "Sauna_ButtonPower"))
                    {
                        saunaPowerFsm = fsm;
                        fsm.InitializeSafe();
                        saunaPowerRotVar = fsm.FsmVariables.FindFsmFloat("Rot")
                            ?? fsm.FsmVariables.FindFsmFloat("Data");
                        FsmGameObject capMeshVar = fsm.FsmVariables.FindFsmGameObject("CapMesh");
                        if (capMeshVar != null && capMeshVar.Value != null)
                        {
                            saunaPowerKnobMesh = capMeshVar.Value.transform;
                        }

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            string stateName = fsm.HasState("Wait") ? "Wait" : (fsm.HasState("State 1") ? "State 1" : null);
                            if (stateName != null)
                            {
                                fsm.InsertAction(stateName, () => OnLocalSaunaKnobChanged(isTimer: false));
                            }
                        }
                    }
                }
            }

            if (saunaTimerFsm == null)
            {
                Transform timerKnob = sauna.Find("Kiuas/ButtonTime");
                if (timerKnob != null)
                {
                    PlayMakerFSM fsm = timerKnob.GetComponent<PlayMakerFSM>();
                    if (fsm != null && TryClaimSafe(fsm, "Sauna_ButtonTime"))
                    {
                        saunaTimerFsm = fsm;
                        fsm.InitializeSafe();
                        saunaTimerRotVar = fsm.FsmVariables.FindFsmFloat("Timer")
                            ?? fsm.FsmVariables.FindFsmFloat("Rot");
                        saunaTimerMath1Var = fsm.FsmVariables.FindFsmFloat("Math1");
                        FsmGameObject capMeshVar = fsm.FsmVariables.FindFsmGameObject("CapMesh");
                        if (capMeshVar != null && capMeshVar.Value != null)
                        {
                            saunaTimerKnobMesh = capMeshVar.Value.transform;
                        }

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            string stateName = fsm.HasState("Wait") ? "Wait" : (fsm.HasState("State 1") ? "State 1" : null);
                            if (stateName != null)
                            {
                                fsm.InsertAction(stateName, () => OnLocalSaunaKnobChanged(isTimer: true));
                            }
                        }
                    }
                }
            }

            if (saunaStoveTriggerFsm == null)
            {
                Transform steamTrigger = sauna.Find("Kiuas/StoveTrigger");
                if (steamTrigger != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(steamTrigger.gameObject, "Steam")
                        ?? steamTrigger.GetComponent<PlayMakerFSM>();
                    if (fsm != null && TryClaimSafe(fsm, "Sauna_StoveTrigger"))
                    {
                        saunaStoveTriggerFsm = fsm;
                        fsm.InitializeSafe();
                        string steamState = fsm.HasState("Calc blur") ? "Calc blur"
                            : (fsm.HasState("Steam") ? "Steam" : null);
                        if (steamState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_STEAM", steamState);
                            if (!hookedFsmIds.Contains(fsm.GetInstanceID()))
                            {
                                hookedFsmIds.Add(fsm.GetInstanceID());
                                fsm.InsertAction(steamState, OnLocalSaunaWaterSteam);
                            }
                        }
                    }
                }
            }
        }

        private void ScanCottageStove()
        {
            if (cottageStoveFsm != null) return;

            GameObject cottageGo = GameObject.Find("COTTAGE") ?? GameObject.Find("Cottage");
            if (cottageGo == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(cottageGo, "Stove")
                ?? PlayMakerBridge.FindFsm(cottageGo, "Fireplace");
            if (fsm == null)
            {
                PlayMakerFSM[] childFsms = cottageGo.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < childFsms.Length; i++)
                {
                    PlayMakerFSM candidate = childFsms[i];
                    if (candidate == null) continue;
                    if (candidate.FsmName == "Stove" || candidate.FsmName == "Heat" ||
                        (candidate.gameObject != null && candidate.gameObject.name.IndexOf("Stove", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        fsm = candidate;
                        break;
                    }
                }
            }

            if (fsm != null && TryClaimSafe(fsm, "Cottage_SaunaStove"))
            {
                cottageStoveFsm = fsm;
                fsm.InitializeSafe();
                cottageHeatVar = fsm.FsmVariables.FindFsmFloat("Heat") ?? fsm.FsmVariables.FindFsmFloat("Temperature");
                cottageLitVar = fsm.FsmVariables.FindFsmBool("Fire") ?? fsm.FsmVariables.FindFsmBool("Lit");

                if (!hookedFsmIds.Contains(fsm.GetInstanceID()))
                {
                    hookedFsmIds.Add(fsm.GetInstanceID());
                    string burnState = fsm.HasState("Burn") ? "Burn" : (fsm.HasState("Fire") ? "Fire" : null);
                    if (burnState != null)
                    {
                        fsm.AddSafeGlobalTransition("MP_COTTAGE_IGNITE", burnState);
                        fsm.InsertAction(burnState, () =>
                        {
                            if (isSuppressingSauna) return;
                            float heat = cottageHeatVar != null ? cottageHeatVar.Value : 100f;
                            lastSentCottageLit = true;
                            lastSentCottageHeat = heat;
                            SendCottageStoveState(true, heat);
                        });
                    }

                    string offState = fsm.HasState("Off") ? "Off" : (fsm.HasState("Extinguish") ? "Extinguish" : null);
                    if (offState != null)
                    {
                        fsm.AddSafeGlobalTransition("MP_COTTAGE_OFF", offState);
                        fsm.InsertAction(offState, () =>
                        {
                            if (isSuppressingSauna) return;
                            float heat = cottageHeatVar != null ? cottageHeatVar.Value : 0f;
                            lastSentCottageLit = false;
                            lastSentCottageHeat = heat;
                            SendCottageStoveState(false, heat);
                        });
                    }
                }
            }
        }

        private void ScanFireSources()
        {
            if (burntHouseBase == null) burntHouseBase = FindObjectIncludingInactive("burnt_house_base");
            if (burntHouseStack == null) burntHouseStack = FindObjectIncludingInactive("burnt_house_stack");
            if (normalHouse == null) normalHouse = GameObject.Find("HOUSE") ?? GameObject.Find("House");

            if (burntHouseBase != null)
            {
                lastHouseBurnt = burntHouseBase.activeSelf;
            }

            string[] fireObjectNames =
            {
                "FireBedroom1",
                "FireBedroom2",
                "HOUSEFIRE",
                "FireTrigger",
                "SmokeHomeKiuas",
                "SmokeHomeOven",
                "SmokeHomeKitchen",
                "SmokeHomeFireplace"
            };

            for (int i = 0; i < fireObjectNames.Length; i++)
            {
                RegisterFireObjectByName(fireObjectNames[i]);
            }
        }

        private void RegisterFireObjectByName(string objName)
        {
            GameObject go = FindObjectIncludingInactive(objName);
            if (go == null) return;

            int keyHash = SceneHasher.GetHash(go);
            if (fireByHash.ContainsKey(keyHash)) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Logic")
                ?? PlayMakerBridge.FindFsm(go, "Fire")
                ?? PlayMakerBridge.FindFsm(go, "Use")
                ?? go.GetComponent<PlayMakerFSM>();

            if (fsm != null && !TryClaimSafe(fsm, "HouseFire_" + objName))
            {
                fsm = null;
            }

            FsmBool fireBool = null;
            FsmBool burningBool = null;
            FsmFloat fireFloat = null;

            if (fsm != null)
            {
                fsm.InitializeSafe();
                fireBool = fsm.FsmVariables.FindFsmBool("Fire")
                    ?? fsm.FsmVariables.FindFsmBool("OnFire")
                    ?? fsm.FsmVariables.FindFsmBool("Active");
                burningBool = fsm.FsmVariables.FindFsmBool("Burning");
                fireFloat = fsm.FsmVariables.FindFsmFloat("Fire")
                    ?? fsm.FsmVariables.FindFsmFloat("Heat");
            }

            bool initialFire = EvaluateFireActive(go, fsm, fireBool, burningBool, fireFloat);

            var tracked = new TrackedFireSource
            {
                KeyHash = keyHash,
                Name = objName,
                GameObject = go,
                Fsm = fsm,
                FireBoolVar = fireBool,
                BurningBoolVar = burningBool,
                FireFloatVar = fireFloat,
                LastOnFire = initialFire
            };

            if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()))
            {
                hookedFsmIds.Add(fsm.GetInstanceID());

                string[] fireStates = { "Fire", "FIRE", "Burning", "Burn", "State 2" };
                for (int s = 0; s < fireStates.Length; s++)
                {
                    if (fsm.HasState(fireStates[s]))
                    {
                        fsm.AddSafeGlobalTransition("MP_FIRE", fireStates[s]);
                        fsm.InsertAction(fireStates[s], () => OnLocalFireStateTriggered(tracked, onFire: true));
                        break;
                    }
                }

                string[] extinguishStates = { "Extinguish", "Extinguished", "Put out", "PutOut", "Off", "OFF", "State 1", "Wait" };
                for (int s = 0; s < extinguishStates.Length; s++)
                {
                    if (fsm.HasState(extinguishStates[s]))
                    {
                        fsm.AddSafeGlobalTransition("MP_EXTINGUISH", extinguishStates[s]);
                        if (extinguishStates[s] != "State 1" && extinguishStates[s] != "Wait")
                        {
                            fsm.InsertAction(extinguishStates[s], () => OnLocalFireStateTriggered(tracked, onFire: false));
                        }
                        break;
                    }
                }

                tracked.IsHooked = true;
            }

            fireByHash[keyHash] = tracked;
            fireSources.Add(tracked);
        }

        #endregion

        #region Local Triggers & Polling

        private void OnLocalSaunaKnobChanged(bool isTimer)
        {
            if (isSuppressingSauna || !IsNetworkRunning) return;

            if (isTimer)
            {
                float timerVal = saunaTimerRotVar != null ? saunaTimerRotVar.Value : 0f;
                if (Mathf.Abs(timerVal - lastSentTimerRot) < 0.01f) return;
                lastSentTimerRot = timerVal;
                SendSaunaStovePacket(stoveKind: 0, fieldKind: 2, boolVal: timerVal > 0.1f, floatVal: timerVal);
            }
            else
            {
                float powerVal = saunaPowerRotVar != null ? saunaPowerRotVar.Value : 0f;
                if (Mathf.Abs(powerVal - lastSentPowerRot) < 0.01f) return;
                lastSentPowerRot = powerVal;
                SendSaunaStovePacket(stoveKind: 0, fieldKind: 1, boolVal: powerVal > 0.1f, floatVal: powerVal);
            }
        }

        private void OnLocalSaunaWaterSteam()
        {
            if (isSuppressingSteam || !IsNetworkRunning) return;

            float temp = GetCurrentKiuasTemp();
            using (var p = new PacketWriter(PacketType.DomesticSaunaAndFireSync))
            {
                p.WriteByte(DomesticProtocol.SaunaWaterSteam);
                p.WriteInt32(LocalPlayerId);
                p.WriteSingle(temp);
                ctx.Transport.Send(p);
            }
        }

        private void CheckPeeOnSauna(PlayerUrinateStateEvent evt)
        {
            if (kiuasTr == null) return;

            Vector3 diff = evt.Position - kiuasTr.position;
            float distSqr = diff.sqrMagnitude;
            if (distSqr > PeeRadiusSqr || Mathf.Abs(diff.y) > 2.5f) return;

            float now = Time.time;
            if (lastPeeOnSaunaByPlayer.TryGetValue(evt.PlayerId, out float lastTime) &&
                (now - lastTime) < PeeCooldownSeconds)
            {
                return;
            }
            lastPeeOnSaunaByPlayer[evt.PlayerId] = now;

            bool isHotBurn = IsSaunaStoveHot();
            TriggerLocalSaunaSteamEffects();

            if (evt.IsLocal)
            {
                if (isHotBurn)
                {
                    ApplyLocalSaunaPeeBurnHazard();
                }

                ctx?.Log?.Invoke(isHotBurn
                    ? "[Сауна] Вы помочились на раскалённую каменку! Пошёл едкий пар и ожог!"
                    : "[Сауна] Вы помочились на камни сауны! Пошёл пар.");

                if (IsNetworkRunning)
                {
                    using (var p = new PacketWriter(PacketType.DomesticSaunaAndFireSync))
                    {
                        p.WriteByte(DomesticProtocol.SaunaPeeSteamBurn);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteInt32(evt.PlayerId);
                        p.WriteBoolean(isHotBurn);
                        ctx.Transport.Send(p);
                    }
                }
            }
            else
            {
                ctx?.Log?.Invoke("[Сауна] Напарник помочился на каменку сауны! Пошёл едкий пар!");
                if (isHotBurn && IsLocalPlayerNearKiuas(3.0f))
                {
                    ApplyLocalSaunaPeeBurnHazard();
                }
            }
        }

        private void OnLocalFireStateTriggered(TrackedFireSource source, bool onFire)
        {
            if (isSuppressingFire || source == null) return;
            if (source.LastOnFire == onFire) return;

            source.LastOnFire = onFire;
            if (!IsNetworkRunning) return;

            SendHouseFirePacket(source.KeyHash, onFire, isExtinguished: !onFire, isHouseBurnt: false);
            ctx?.Log?.Invoke(onFire
                ? $"[Пожар] Загорелся объект '{source.Name}'!"
                : $"[Пожар] Огонь на '{source.Name}' потушен!");
        }

        private void PollSaunaTemperature()
        {
            float temp = GetCurrentKiuasTemp();
            if (kiuasHeatVar != null && Mathf.Abs(temp - lastSentKiuasTemp) >= 2.0f)
            {
                lastSentKiuasTemp = temp;
                SendSaunaStovePacket(stoveKind: 0, fieldKind: 3, boolVal: temp > 35f, floatVal: temp);
            }

            if (cottageStoveFsm != null)
            {
                bool lit = cottageLitVar != null && cottageLitVar.Value;
                float heat = cottageHeatVar != null ? cottageHeatVar.Value : 0f;
                if (lit != lastSentCottageLit || Mathf.Abs(heat - lastSentCottageHeat) >= 2.5f)
                {
                    lastSentCottageLit = lit;
                    lastSentCottageHeat = heat;
                    SendCottageStoveState(lit, heat);
                }
            }
        }

        private void PollFireStates()
        {
            if (isSuppressingFire) return;

            for (int i = 0; i < fireSources.Count; i++)
            {
                TrackedFireSource src = fireSources[i];
                if (src == null || src.GameObject == null) continue;

                bool currentFire = EvaluateFireActive(src.GameObject, src.Fsm, src.FireBoolVar, src.BurningBoolVar, src.FireFloatVar);
                if (currentFire != src.LastOnFire)
                {
                    src.LastOnFire = currentFire;
                    SendHouseFirePacket(src.KeyHash, currentFire, isExtinguished: !currentFire, isHouseBurnt: false);
                    ctx?.Log?.Invoke(currentFire
                        ? $"[Пожар] Возгорание: {src.Name}"
                        : $"[Пожар] Очаг '{src.Name}' потушен огнетушителем!");
                }
            }

            if (burntHouseBase != null)
            {
                bool burntNow = burntHouseBase.activeSelf;
                if (burntNow && !lastHouseBurnt)
                {
                    lastHouseBurnt = true;
                    SendHouseFirePacket(0, isOnFire: false, isExtinguished: false, isHouseBurnt: true);
                    ctx?.Log?.Invoke("[Пожар] Дом сгорел дотла!");
                }
            }
        }

        #endregion

        #region Outbound Helpers

        private void SendSaunaStovePacket(byte stoveKind, byte fieldKind, bool boolVal, float floatVal)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticSaunaAndFireSync))
            {
                p.WriteByte(DomesticProtocol.SaunaStoveState);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(stoveKind);
                p.WriteByte(fieldKind);
                p.WriteBoolean(boolVal);
                p.WriteSingle(floatVal);
                ctx.Transport.Send(p);
            }
        }

        private void SendCottageStoveState(bool lit, float heat)
        {
            SendSaunaStovePacket(stoveKind: 1, fieldKind: 1, boolVal: lit, floatVal: heat);
        }

        private void SendHouseFirePacket(int sourceHash, bool isOnFire, bool isExtinguished, bool isHouseBurnt)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.DomesticSaunaAndFireSync))
            {
                p.WriteByte(DomesticProtocol.HouseFireState);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(sourceHash);
                p.WriteBoolean(isOnFire);
                p.WriteBoolean(isExtinguished);
                p.WriteBoolean(isHouseBurnt);
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
                    SendFullSnapshotToClient(clientId);
                }
            }
        }

        private void SendFullSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            float powerRot = saunaPowerRotVar != null ? saunaPowerRotVar.Value : 0f;
            float timerRot = saunaTimerRotVar != null ? saunaTimerRotVar.Value : 0f;
            float kiuasTemp = GetCurrentKiuasTemp();
            bool cottageLit = cottageLitVar != null && cottageLitVar.Value;
            float cottageHeat = cottageHeatVar != null ? cottageHeatVar.Value : 0f;
            bool houseBurnt = burntHouseBase != null && burntHouseBase.activeSelf;

            using (var p = new PacketWriter(PacketType.DomesticSaunaAndFireSync))
            {
                p.WriteByte(DomesticProtocol.SaunaFireSnapshot);
                p.WriteInt32(LocalPlayerId);

                p.WriteBoolean(saunaPowerRotVar != null);
                p.WriteSingle(powerRot);
                p.WriteBoolean(saunaTimerRotVar != null);
                p.WriteSingle(timerRot);
                p.WriteSingle(kiuasTemp);

                p.WriteBoolean(cottageStoveFsm != null);
                p.WriteBoolean(cottageLit);
                p.WriteSingle(cottageHeat);

                p.WriteBoolean(houseBurnt);
                p.WriteInt32(fireSources.Count);
                for (int i = 0; i < fireSources.Count; i++)
                {
                    TrackedFireSource src = fireSources[i];
                    bool active = src != null && src.GameObject != null &&
                                  EvaluateFireActive(src.GameObject, src.Fsm, src.FireBoolVar, src.BurningBoolVar, src.FireFloatVar);
                    p.WriteInt32(src != null ? src.KeyHash : 0);
                    p.WriteBoolean(active);
                }

                ctx.Transport.SendTo(p, clientId);
            }
        }

        #endregion

        #region Inbound Packet Handlers

        private void HandleSaunaStoveState(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            byte stoveKind = reader.ReadByte();
            byte fieldKind = reader.ReadByte();
            bool boolVal = reader.ReadBoolean();
            float floatVal = reader.ReadSingle();

            if (senderId == LocalPlayerId) return;

            ApplySaunaStoveState(stoveKind, fieldKind, boolVal, floatVal);
        }

        private void HandleSaunaWaterSteam(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            float temp = reader.ReadSingle();
            if (senderId == LocalPlayerId) return;

            if (kiuasHeatVar != null && temp > 0f)
            {
                kiuasHeatVar.Value = temp;
                lastSentKiuasTemp = temp;
            }
            TriggerLocalSaunaSteamEffects();
        }

        private void HandleSaunaPeeSteamBurn(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int peePlayerId = reader.ReadInt32();
            bool isHotBurn = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;

            float now = Time.time;
            if (lastPeeOnSaunaByPlayer.TryGetValue(peePlayerId, out float lastTime) &&
                (now - lastTime) < PeeCooldownSeconds)
            {
                return;
            }
            lastPeeOnSaunaByPlayer[peePlayerId] = now;

            TriggerLocalSaunaSteamEffects();
            if (isHotBurn && IsLocalPlayerNearKiuas(3.0f))
            {
                ApplyLocalSaunaPeeBurnHazard();
            }

            ctx?.Log?.Invoke(isHotBurn
                ? "[Сауна] Напарник помочился на горячую каменку! Едкий пар и ожог!"
                : "[Сауна] Напарник помочился на каменку сауны!");
        }

        private void HandleHouseFireState(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            int sourceHash = reader.ReadInt32();
            bool isOnFire = reader.ReadBoolean();
            bool isExtinguished = reader.ReadBoolean();
            bool isHouseBurnt = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;

            if (isHouseBurnt)
            {
                ApplyHouseBurntState(burnt: true);
                ctx?.Log?.Invoke("[Пожар] Дом сгорел дотла!");
                return;
            }

            if (fireByHash.TryGetValue(sourceHash, out TrackedFireSource src))
            {
                ApplyRemoteFireSource(src, isOnFire, isExtinguished);
            }
        }

        private void HandleSaunaFireSnapshot(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            bool hasPower = reader.ReadBoolean();
            float powerRot = reader.ReadSingle();
            bool hasTimer = reader.ReadBoolean();
            float timerRot = reader.ReadSingle();
            float kiuasTemp = reader.ReadSingle();

            bool hasCottage = reader.ReadBoolean();
            bool cottageLit = reader.ReadBoolean();
            float cottageHeat = reader.ReadSingle();

            bool houseBurnt = reader.ReadBoolean();
            int fireCount = reader.ReadInt32();

            if (hasPower) ApplySaunaStoveState(0, 1, powerRot > 0.1f, powerRot);
            if (hasTimer) ApplySaunaStoveState(0, 2, timerRot > 0.1f, timerRot);
            ApplySaunaStoveState(0, 3, kiuasTemp > 35f, kiuasTemp);

            if (hasCottage)
            {
                ApplySaunaStoveState(1, 1, cottageLit, cottageHeat);
            }

            if (houseBurnt)
            {
                ApplyHouseBurntState(burnt: true);
            }

            for (int i = 0; i < fireCount; i++)
            {
                int hash = reader.ReadInt32();
                bool active = reader.ReadBoolean();
                if (fireByHash.TryGetValue(hash, out TrackedFireSource src))
                {
                    if (active || src.LastOnFire)
                    {
                        ApplyRemoteFireSource(src, active, !active);
                    }
                }
            }
        }

        #endregion

        #region Apply Helpers

        private void ApplySaunaStoveState(byte stoveKind, byte fieldKind, bool boolVal, float floatVal)
        {
            isSuppressingSauna = true;
            try
            {
                if (stoveKind == 0)
                {
                    if (fieldKind == 1)
                    {
                        lastSentPowerRot = floatVal;
                        if (saunaPowerRotVar != null) saunaPowerRotVar.Value = floatVal;
                        if (saunaPowerKnobMesh != null) saunaPowerKnobMesh.localEulerAngles = Vector3.up * floatVal;
                    }
                    else if (fieldKind == 2)
                    {
                        lastSentTimerRot = floatVal;
                        if (saunaTimerRotVar != null) saunaTimerRotVar.Value = floatVal;
                        if (saunaTimerMath1Var != null) saunaTimerMath1Var.Value = floatVal * 6f;
                        if (saunaTimerKnobMesh != null) saunaTimerKnobMesh.localEulerAngles = Vector3.up * floatVal;
                    }
                    else if (fieldKind == 3)
                    {
                        lastSentKiuasTemp = floatVal;
                        if (kiuasHeatVar != null) kiuasHeatVar.Value = floatVal;
                        if (kiuasTempVar != null) kiuasTempVar.Value = floatVal;
                    }
                }
                else if (stoveKind == 1 && cottageStoveFsm != null)
                {
                    lastSentCottageLit = boolVal;
                    lastSentCottageHeat = floatVal;
                    if (cottageHeatVar != null) cottageHeatVar.Value = floatVal;
                    if (cottageLitVar != null) cottageLitVar.Value = boolVal;
                    cottageStoveFsm.SendEvent(boolVal ? "MP_COTTAGE_IGNITE" : "MP_COTTAGE_OFF");
                }
            }
            finally
            {
                isSuppressingSauna = false;
            }
        }

        private void TriggerLocalSaunaSteamEffects()
        {
            isSuppressingSteam = true;
            try
            {
                if (saunaStoveTriggerFsm != null)
                {
                    if (HasFsmEvent(saunaStoveTriggerFsm, "GLOBALEVENT"))
                    {
                        saunaStoveTriggerFsm.SendEvent("GLOBALEVENT");
                    }
                    else
                    {
                        saunaStoveTriggerFsm.SendEvent("MP_STEAM");
                    }
                }

                if (kiuasFsm != null && HasFsmEvent(kiuasFsm, "STEAM"))
                {
                    kiuasFsm.SendEvent("STEAM");
                }
            }
            finally
            {
                isSuppressingSteam = false;
            }
        }

        private void ApplyLocalSaunaPeeBurnHazard()
        {
            if (PlayMakerGlobals.Instance == null || PlayMakerGlobals.Instance.Variables == null) return;

            FsmFloat stressVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerStress");
            if (stressVar != null)
            {
                stressVar.Value = Mathf.Min(100f, stressVar.Value + 12f);
            }

            FsmFloat dirtVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDirtiness");
            if (dirtVar != null)
            {
                dirtVar.Value = Mathf.Min(100f, dirtVar.Value + 8f);
            }
        }

        private void ApplyRemoteFireSource(TrackedFireSource src, bool isOnFire, bool isExtinguished)
        {
            if (src == null || src.GameObject == null) return;

            isSuppressingFire = true;
            try
            {
                src.LastOnFire = isOnFire;

                if (src.FireBoolVar != null) src.FireBoolVar.Value = isOnFire;
                if (src.BurningBoolVar != null) src.BurningBoolVar.Value = isOnFire;
                if (src.FireFloatVar != null && isExtinguished) src.FireFloatVar.Value = 0f;

                if (isOnFire)
                {
                    if (!src.GameObject.activeSelf) src.GameObject.SetActive(true);

                    if (src.Fsm != null)
                    {
                        if (HasFsmEvent(src.Fsm, "FIRE")) src.Fsm.SendEvent("FIRE");
                        else src.Fsm.SendEvent("MP_FIRE");
                    }
                }
                else if (isExtinguished)
                {
                    if (src.Fsm != null)
                    {
                        if (HasFsmEvent(src.Fsm, "EXTINGUISH")) src.Fsm.SendEvent("EXTINGUISH");
                        else if (HasFsmEvent(src.Fsm, "PUTOUT")) src.Fsm.SendEvent("PUTOUT");
                        else if (HasFsmEvent(src.Fsm, "OFF")) src.Fsm.SendEvent("OFF");
                        else src.Fsm.SendEvent("MP_EXTINGUISH");
                    }

                    ParticleSystem[] particles = src.GameObject.GetComponentsInChildren<ParticleSystem>(true);
                    for (int i = 0; i < particles.Length; i++)
                    {
                        if (particles[i] != null) particles[i].Stop();
                    }

                    if (src.Name.StartsWith("FireBedroom", StringComparison.OrdinalIgnoreCase) ||
                        src.Name.StartsWith("SmokeHome", StringComparison.OrdinalIgnoreCase))
                    {
                        Transform fireChild = src.GameObject.transform.Find("Fire") ?? src.GameObject.transform.Find("Flames");
                        if (fireChild != null) fireChild.gameObject.SetActive(false);
                    }
                }
            }
            finally
            {
                isSuppressingFire = false;
            }
        }

        private void ApplyHouseBurntState(bool burnt)
        {
            if (!burnt) return;
            isSuppressingFire = true;
            try
            {
                lastHouseBurnt = true;
                if (burntHouseBase != null) burntHouseBase.SetActive(true);
                if (burntHouseStack != null) burntHouseStack.SetActive(true);
                if (normalHouse != null) normalHouse.SetActive(false);
            }
            finally
            {
                isSuppressingFire = false;
            }
        }

        #endregion

        #region Utilities

        private float GetCurrentKiuasTemp()
        {
            if (kiuasTempVar != null) return kiuasTempVar.Value;
            if (kiuasHeatVar != null) return kiuasHeatVar.Value;
            return 0f;
        }

        private bool IsSaunaStoveHot()
        {
            if (kiuasHeatVar != null && kiuasHeatVar.Value > 40f) return true;
            if (kiuasTempVar != null && kiuasTempVar.Value > 40f) return true;
            if (saunaPowerRotVar != null && saunaPowerRotVar.Value > 1f) return true;
            if (saunaTimerRotVar != null && saunaTimerRotVar.Value > 1f) return true;
            return false;
        }

        private bool IsLocalPlayerNearKiuas(float maxDist)
        {
            if (kiuasTr == null) return false;
            GameObject player = GameObject.Find("PLAYER");
            if (player == null) return false;
            return (player.transform.position - kiuasTr.position).sqrMagnitude <= (maxDist * maxDist);
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

        private static bool EvaluateFireActive(GameObject go, PlayMakerFSM fsm, FsmBool fireBool, FsmBool burningBool, FsmFloat fireFloat)
        {
            if (fireBool != null && fireBool.Value) return true;
            if (burningBool != null && burningBool.Value) return true;
            if (fireFloat != null && fireFloat.Value > 0.5f) return true;

            if (fsm != null && !string.IsNullOrEmpty(fsm.ActiveStateName))
            {
                string state = fsm.ActiveStateName;
                if (string.Equals(state, "Fire", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(state, "Burning", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(state, "Burn", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (go != null && (go.name == "FireBedroom1" || go.name == "FireBedroom2"))
            {
                Transform fireChild = go.transform.Find("Fire") ?? go.transform.Find("Flames");
                if (fireChild != null && fireChild.gameObject.activeSelf) return true;
            }

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
