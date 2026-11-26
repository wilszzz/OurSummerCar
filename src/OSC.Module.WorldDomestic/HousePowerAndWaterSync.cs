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
    /// Синхронизация электричества и водопровода дома/двора (PacketType.DomesticPowerAndWaterSync = 110):
    /// - настенные выключатели света во всех комнатах (switch_*);
    /// - электрощиток (Fusebox / FuseTable / fuseholder01..07 / Fuse1..7) и выбивание пробок грозой;
    /// - водопроводные краны кухни и ванной, душ, уличный колодец-насос (WaterWell);
    /// - анти-эхо через try/finally + окно подавления, Late-Join снимок (сразу + через +2.0f), OnClientDisconnected.
    /// </summary>
    internal sealed class HousePowerAndWaterSync
    {
        private sealed class TrackedSwitch
        {
            public int Hash;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool SwitchVar;
            public FsmEvent MpEvent;
            public Light RoomLight;
            public GameObject LightGameObject;
            public bool IsOn;
            public bool SuppressEcho;
            public float SuppressUntilTime;
        }

        private sealed class TrackedFuseHolder
        {
            public byte Index;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool ScrewedVar;
            public FsmEvent MpScrewEvent;
            public FsmEvent MpUnscrewEvent;
            public FsmEvent MpBlowEvent;
            public bool ScrewedIn;
            public bool SuppressEcho;
            public float SuppressUntilTime;
        }

        private sealed class TrackedFuseItem
        {
            public byte Index;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool BlownVar;
            public bool LastBlown;
            public string LastStateName;
            public bool SuppressEcho;
            public float SuppressUntilTime;
        }

        private sealed class TrackedWaterSource
        {
            public int Hash;
            public bool IsWellPump;
            public bool IsToggleType;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool OnVar;
            public FsmEvent MpOnEvent;
            public FsmEvent MpOffEvent;
            public FsmEvent MpPumpEvent;
            public bool IsOn;
            public bool SuppressEcho;
            public float SuppressUntilTime;
        }

        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private const byte FuseActionHolderScrew = 1;
        private const byte FuseActionBlowout = 2;
        private const byte FuseActionMainSwitch = 3;
        private const byte FuseAllMaskIndex = 255;

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly Dictionary<int, TrackedSwitch> switchesByHash = new Dictionary<int, TrackedSwitch>();
        private readonly List<TrackedSwitch> switchList = new List<TrackedSwitch>();

        private readonly List<TrackedFuseHolder> fuseHolders = new List<TrackedFuseHolder>();
        private readonly TrackedFuseItem[] fuseItems = new TrackedFuseItem[7];
        private PlayMakerFSM fuseTableFsm;
        private FsmBool fuseTableMainSwitchVar;
        private FsmBool fuseTableBlownVar;
        private bool lastFuseTableBlown;
        private bool lastMainSwitchOn = true;
        private string lastFuseTableStateName;

        private readonly Dictionary<int, TrackedWaterSource> tapsByHash = new Dictionary<int, TrackedWaterSource>();
        private readonly List<TrackedWaterSource> tapList = new List<TrackedWaterSource>();

        private readonly Dictionary<int, TrackedWaterSource> wellsByHash = new Dictionary<int, TrackedWaterSource>();
        private readonly List<TrackedWaterSource> wellList = new List<TrackedWaterSource>();

        private readonly HashSet<int> hookedObjectIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        private bool isApplyingRemoteSwitch;
        private bool isApplyingRemoteFuse;
        private bool isApplyingRemoteWater;
        private float fuseSuppressUntil;

        private float nextRescanTime;
        private float nextFusePollTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private bool IsServer => ctx != null && ctx.Transport != null && ctx.Transport.IsServer;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Свет: {switchList.Count}, Пробки: {fuseHolders.Count}/{CountFuseItems()}, Краны: {tapList.Count}, Колодец: {wellList.Count}";

        public HousePowerAndWaterSync(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanAll();
            ctx?.Log?.Invoke($"[Дом/Коммуникации] {StatusSummary}");
        }

        public void Reset()
        {
            switchesByHash.Clear();
            switchList.Clear();
            fuseHolders.Clear();
            for (int i = 0; i < fuseItems.Length; i++)
            {
                fuseItems[i] = null;
            }
            fuseTableFsm = null;
            fuseTableMainSwitchVar = null;
            fuseTableBlownVar = null;
            lastFuseTableBlown = false;
            lastMainSwitchOn = true;
            lastFuseTableStateName = null;

            tapsByHash.Clear();
            tapList.Clear();
            wellsByHash.Clear();
            wellList.Clear();
            hookedObjectIds.Clear();
            pendingSnapshots.Clear();

            isApplyingRemoteSwitch = false;
            isApplyingRemoteFuse = false;
            isApplyingRemoteWater = false;
            fuseSuppressUntil = 0f;
            nextRescanTime = 0f;
            nextFusePollTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;

            if (now >= nextRescanTime)
            {
                nextRescanTime = now + 3.0f;
                if (switchList.Count == 0 || tapList.Count == 0 || fuseHolders.Count == 0 || fuseTableFsm == null)
                {
                    ScanAll();
                }
            }

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextFusePollTime)
            {
                nextFusePollTime = now + 0.4f;
                PollFuseAndStormChanges(now);
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsServer) return;

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

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subtype = reader.ReadByte();
            switch (subtype)
            {
                case DomesticProtocol.PowerSwitchState:
                {
                    if (reader.UnreadBytes < 9) return;
                    int senderId = reader.ReadInt32();
                    int hash = reader.ReadInt32();
                    bool state = reader.ReadBoolean();
                    if (senderId == LocalPlayerId) return;

                    if (switchesByHash.TryGetValue(hash, out TrackedSwitch sw))
                    {
                        ApplyRemoteSwitch(sw, state);
                    }
                    break;
                }

                case DomesticProtocol.FuseBoxState:
                {
                    if (reader.UnreadBytes < 6) return;
                    int senderId = reader.ReadInt32();
                    byte actionKind = reader.ReadByte();
                    byte index = reader.ReadByte();
                    bool flag = reader.ReadBoolean();
                    if (senderId == LocalPlayerId) return;

                    ApplyRemoteFuseAction(actionKind, index, flag);
                    break;
                }

                case DomesticProtocol.WaterTapState:
                {
                    if (reader.UnreadBytes < 9) return;
                    int senderId = reader.ReadInt32();
                    int hash = reader.ReadInt32();
                    bool on = reader.ReadBoolean();
                    if (senderId == LocalPlayerId) return;

                    if (tapsByHash.TryGetValue(hash, out TrackedWaterSource tap))
                    {
                        ApplyRemoteWaterSource(tap, on);
                    }
                    break;
                }

                case DomesticProtocol.WellPumpAction:
                {
                    if (reader.UnreadBytes < 10) return;
                    int senderId = reader.ReadInt32();
                    int hash = reader.ReadInt32();
                    bool isToggle = reader.ReadBoolean();
                    bool on = reader.ReadBoolean();
                    if (senderId == LocalPlayerId) return;

                    if (wellsByHash.TryGetValue(hash, out TrackedWaterSource well))
                    {
                        if (isToggle)
                        {
                            ApplyRemoteWaterSource(well, on);
                        }
                        else
                        {
                            ApplyRemoteWellStroke(well);
                        }
                    }
                    break;
                }

                case DomesticProtocol.PowerWaterSnapshot:
                {
                    ApplySnapshot(reader);
                    break;
                }
            }
        }

        private void ScanAll()
        {
            try
            {
                InitLightSwitches();
                InitFuseBox();
                InitWaterSources();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Дом/Коммуникации] Ошибка инициализации: {ex.Message}");
            }
        }

        private void InitLightSwitches()
        {
            var lightMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            GameObject electric = GameObject.Find("YARD/Building/ElectricAppliances") ?? GameObject.Find("ElectricAppliances");
            if (electric != null)
            {
                Light[] lights = electric.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i] == null) continue;
                    string pName = lights[i].transform.parent != null ? lights[i].transform.parent.name : string.Empty;
                    if (!string.IsNullOrEmpty(pName) && !lightMap.ContainsKey(pName))
                    {
                        lightMap[pName] = lights[i].gameObject;
                    }
                    if (!lightMap.ContainsKey(lights[i].gameObject.name))
                    {
                        lightMap[lights[i].gameObject.name] = lights[i].gameObject;
                    }
                }
            }

            GameObject[] allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                GameObject go = allGo[i];
                if (go == null || !go.name.StartsWith("switch", StringComparison.Ordinal)) continue;

                int objId = go.GetInstanceID();
                if (hookedObjectIds.Contains(objId)) continue;

                PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Use") ?? go.GetComponent<PlayMakerFSM>();
                if (fsm == null || fsm.FsmName != "Use") continue;

                fsm.InitializeSafe();
                if (!fsm.HasState("Position")) continue;

                int hash = SceneHasher.GetHash(go);
                if (switchesByHash.ContainsKey(hash)) continue;

                if (ctx?.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, "HouseLightSwitch")) continue;

                hookedObjectIds.Add(objId);

                FsmBool switchVar = fsm.FsmVariables.FindFsmBool("Switch") ?? fsm.FsmVariables.FindFsmBool("SwitchOn");
                FsmEvent mpEvent = fsm.AddSafeGlobalTransition("MP_SWITCH", "Position");

                GameObject lightGo = null;
                string roomKey = go.name.Replace("switch_", string.Empty).Replace("switch", string.Empty);
                if (!string.IsNullOrEmpty(roomKey))
                {
                    foreach (var kvp in lightMap)
                    {
                        if (kvp.Key.IndexOf(roomKey, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            lightGo = kvp.Value;
                            break;
                        }
                    }
                }

                bool currentOn = switchVar != null && switchVar.Value;
                var tracked = new TrackedSwitch
                {
                    Hash = hash,
                    GameObject = go,
                    Fsm = fsm,
                    SwitchVar = switchVar,
                    MpEvent = mpEvent,
                    LightGameObject = lightGo,
                    RoomLight = lightGo != null ? lightGo.GetComponent<Light>() : null,
                    IsOn = currentOn
                };

                fsm.InsertAction("Position", () => OnLocalSwitchToggled(tracked), 0);

                switchesByHash[hash] = tracked;
                switchList.Add(tracked);
            }
        }

        private void InitFuseBox()
        {
            for (int i = 1; i <= 7; i++)
            {
                string holderName = $"fuseholder0{i}";
                GameObject holder = GameObject.Find(holderName);
                if (holder == null) continue;

                int objId = holder.GetInstanceID();
                if (hookedObjectIds.Contains(objId)) continue;

                PlayMakerFSM fsm = holder.GetComponent<PlayMakerFSM>();
                if (fsm == null) continue;

                fsm.InitializeSafe();
                if (ctx?.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, "FuseHolder")) continue;

                hookedObjectIds.Add(objId);

                byte holderIdx = (byte)(i - 1);
                FsmBool screwedVar = fsm.FsmVariables.FindFsmBool("Fuse")
                                     ?? fsm.FsmVariables.FindFsmBool("Screwed")
                                     ?? fsm.FsmVariables.FindFsmBool("Installed")
                                     ?? fsm.FsmVariables.FindFsmBool("OK");

                var tracked = new TrackedFuseHolder
                {
                    Index = holderIdx,
                    GameObject = holder,
                    Fsm = fsm,
                    ScrewedVar = screwedVar,
                    ScrewedIn = screwedVar != null ? screwedVar.Value : true
                };

                string screwState = fsm.HasState("Screw") ? "Screw" : (fsm.HasState("State 1") ? "State 1" : null);
                if (screwState != null)
                {
                    tracked.MpScrewEvent = fsm.AddSafeGlobalTransition("MP_SCREW", screwState);
                    fsm.InsertAction(screwState, () => OnLocalFuseHolderChanged(tracked, true), 0);
                }

                string unscrewState = fsm.HasState("Unscrew") ? "Unscrew" : (fsm.HasState("Remove") ? "Remove" : null);
                if (unscrewState != null)
                {
                    tracked.MpUnscrewEvent = fsm.AddSafeGlobalTransition("MP_UNSCREW", unscrewState);
                    fsm.InsertAction(unscrewState, () => OnLocalFuseHolderChanged(tracked, false), 0);
                }

                string blowState = fsm.HasState("Blow") ? "Blow" : (fsm.HasState("Blown") ? "Blown" : null);
                if (blowState != null)
                {
                    tracked.MpBlowEvent = fsm.AddSafeGlobalTransition("MP_BLOW", blowState);
                    fsm.InsertAction(blowState, () => OnLocalFuseBlown(holderIdx), 0);
                }

                fuseHolders.Add(tracked);
            }

            if (fuseTableFsm == null)
            {
                GameObject fuseTableGo = GameObject.Find("FuseTable")
                                         ?? GameObject.Find("Fusetable")
                                         ?? GameObject.Find("Fusebox")
                                         ?? GameObject.Find("DashFusebox");
                if (fuseTableGo != null && !hookedObjectIds.Contains(fuseTableGo.GetInstanceID()))
                {
                    PlayMakerFSM tableFsm = PlayMakerBridge.FindFsm(fuseTableGo, "Fuses") ?? fuseTableGo.GetComponent<PlayMakerFSM>();
                    if (tableFsm != null)
                    {
                        tableFsm.InitializeSafe();
                        if (ctx?.Ownership == null || ctx.Ownership.TryClaim(tableFsm, moduleId, "FuseTable"))
                        {
                            hookedObjectIds.Add(fuseTableGo.GetInstanceID());
                            fuseTableFsm = tableFsm;
                            fuseTableMainSwitchVar = tableFsm.FsmVariables.FindFsmBool("MainSwitch")
                                                     ?? tableFsm.FsmVariables.FindFsmBool("Power")
                                                     ?? tableFsm.FsmVariables.FindFsmBool("On");
                            fuseTableBlownVar = tableFsm.FsmVariables.FindFsmBool("Blown")
                                                ?? tableFsm.FsmVariables.FindFsmBool("Lightning");

                            lastMainSwitchOn = fuseTableMainSwitchVar == null || fuseTableMainSwitchVar.Value;
                            lastFuseTableBlown = fuseTableBlownVar != null && fuseTableBlownVar.Value;
                            lastFuseTableStateName = tableFsm.ActiveStateName;

                            string[] blowStates = new string[] { "Blow", "Blown", "Lightning", "Thunder", "Trip" };
                            for (int s = 0; s < blowStates.Length; s++)
                            {
                                if (tableFsm.HasState(blowStates[s]))
                                {
                                    tableFsm.InsertAction(blowStates[s], () => OnLocalFuseBlown(FuseAllMaskIndex), 0);
                                }
                            }
                        }
                    }
                }
            }

            for (int f = 1; f <= 7; f++)
            {
                int idx = f - 1;
                if (fuseItems[idx] != null) continue;

                GameObject fGo = GameObject.Find($"Fuse{f}");
                if (fGo == null || hookedObjectIds.Contains(fGo.GetInstanceID())) continue;

                PlayMakerFSM fFsm = PlayMakerBridge.FindFsm(fGo, "Assembly") ?? fGo.GetComponent<PlayMakerFSM>();
                if (fFsm == null) continue;

                fFsm.InitializeSafe();
                if (ctx?.Ownership != null && !ctx.Ownership.TryClaim(fFsm, moduleId, "FuseItem")) continue;

                hookedObjectIds.Add(fGo.GetInstanceID());

                FsmBool blownVar = fFsm.FsmVariables.FindFsmBool("Broken")
                                   ?? fFsm.FsmVariables.FindFsmBool("Blown")
                                   ?? fFsm.FsmVariables.FindFsmBool("Burned");

                var item = new TrackedFuseItem
                {
                    Index = (byte)idx,
                    GameObject = fGo,
                    Fsm = fFsm,
                    BlownVar = blownVar,
                    LastBlown = blownVar != null && blownVar.Value,
                    LastStateName = fFsm.ActiveStateName
                };

                string[] itemBlowStates = new string[] { "Blow", "Blown", "Break", "Broken" };
                for (int s = 0; s < itemBlowStates.Length; s++)
                {
                    if (fFsm.HasState(itemBlowStates[s]))
                    {
                        fFsm.InsertAction(itemBlowStates[s], () => OnLocalFuseItemBlown(item), 0);
                    }
                }

                fuseItems[idx] = item;
            }
        }

        private void InitWaterSources()
        {
            PlayMakerFSM[] allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null || fsm.gameObject == null) continue;

                int objId = fsm.gameObject.GetInstanceID();
                if (hookedObjectIds.Contains(objId)) continue;

                string pName = fsm.transform.parent != null ? fsm.transform.parent.name : string.Empty;
                string oName = fsm.gameObject.name;

                bool isWell = pName == "WaterWell"
                              || oName == "WaterWell"
                              || pName.IndexOf("WellPump", StringComparison.OrdinalIgnoreCase) >= 0
                              || oName.IndexOf("WellPump", StringComparison.OrdinalIgnoreCase) >= 0
                              || (pName == "Pump" && SceneHasher.GetScenePath(fsm.transform).IndexOf("Well", StringComparison.OrdinalIgnoreCase) >= 0);

                bool isTap = !isWell && (pName == "KitchenWaterTap"
                                         || pName == "BathroomWaterTap"
                                         || pName == "Shower"
                                         || oName.IndexOf("WaterTap", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isWell && !isTap) continue;

                if (fsm.FsmName != "Use" && !(isWell && fsm.FsmName == "Pump")) continue;

                fsm.InitializeSafe();
                int hash = SceneHasher.GetHash(fsm.gameObject);
                if (isWell && wellsByHash.ContainsKey(hash)) continue;
                if (isTap && tapsByHash.ContainsKey(hash)) continue;

                if (ctx?.Ownership != null && !ctx.Ownership.TryClaim(fsm, moduleId, isWell ? "WellPump" : "WaterTap")) continue;

                hookedObjectIds.Add(objId);

                FsmBool onVar = fsm.FsmVariables.FindFsmBool("SwitchOn")
                                ?? fsm.FsmVariables.FindFsmBool("On")
                                ?? fsm.FsmVariables.FindFsmBool("Open");

                bool hasToggleStates = fsm.HasState("ON") && fsm.HasState("OFF");
                var tracked = new TrackedWaterSource
                {
                    Hash = hash,
                    IsWellPump = isWell,
                    IsToggleType = !isWell || hasToggleStates,
                    GameObject = fsm.gameObject,
                    Fsm = fsm,
                    OnVar = onVar,
                    IsOn = onVar != null && onVar.Value
                };

                if (tracked.IsToggleType)
                {
                    if (fsm.HasState("ON")) tracked.MpOnEvent = fsm.AddSafeGlobalTransition("MP_ON", "ON");
                    if (fsm.HasState("OFF")) tracked.MpOffEvent = fsm.AddSafeGlobalTransition("MP_OFF", "OFF");

                    string hookState = fsm.HasState("Position") ? "Position" : (fsm.HasState("Switch") ? "Switch" : null);
                    if (hookState != null)
                    {
                        fsm.InsertAction(hookState, () => OnLocalWaterToggle(tracked), 0);
                    }
                }
                else
                {
                    string pumpState = fsm.HasState("Pump") ? "Pump"
                                     : (fsm.HasState("Pull") ? "Pull"
                                     : (fsm.HasState("Water") ? "Water"
                                     : (fsm.HasState("State 1") ? "State 1" : null)));
                    if (pumpState != null)
                    {
                        tracked.MpPumpEvent = fsm.AddSafeGlobalTransition("MP_PUMP", pumpState);
                        fsm.InsertAction(pumpState, () => OnLocalWellStroke(tracked), 0);
                    }
                }

                if (isWell)
                {
                    wellsByHash[hash] = tracked;
                    wellList.Add(tracked);
                }
                else
                {
                    tapsByHash[hash] = tracked;
                    tapList.Add(tracked);
                }
            }
        }

        private void OnLocalSwitchToggled(TrackedSwitch sw)
        {
            if (isApplyingRemoteSwitch)
            {
                sw.SuppressEcho = false;
                return;
            }
            if (sw.SuppressEcho)
            {
                sw.SuppressEcho = false;
                if (Time.time <= sw.SuppressUntilTime) return;
            }

            bool state = sw.SwitchVar != null && sw.SwitchVar.Value;
            sw.IsOn = state;
            if (sw.RoomLight != null) sw.RoomLight.enabled = state;
            if (sw.LightGameObject != null) sw.LightGameObject.SetActive(state);

            if (!IsNetworkRunning) return;
            SendSwitchToggle(sw.Hash, state);
        }

        private void OnLocalFuseHolderChanged(TrackedFuseHolder holder, bool screwedIn)
        {
            if (isApplyingRemoteFuse || Time.time <= fuseSuppressUntil)
            {
                holder.SuppressEcho = false;
                return;
            }
            if (holder.SuppressEcho)
            {
                holder.SuppressEcho = false;
                if (Time.time <= holder.SuppressUntilTime) return;
            }

            holder.ScrewedIn = screwedIn;
            if (!IsNetworkRunning) return;

            SendFuseBoxState(FuseActionHolderScrew, holder.Index, screwedIn);
            ctx?.Log?.Invoke($"[Электричество] Пробка #{holder.Index + 1} {(screwedIn ? "вкручена" : "выкручена")}.");
        }

        private void OnLocalFuseBlown(byte fuseIndex)
        {
            if (isApplyingRemoteFuse || Time.time <= fuseSuppressUntil) return;
            if (fuseIndex == FuseAllMaskIndex) lastFuseTableBlown = true;
            if (!IsNetworkRunning) return;

            SendFuseBoxState(FuseActionBlowout, fuseIndex, true);
            ctx?.Log?.Invoke("[Гроза/Электричество] Выбило пробки в электрощитке дома!");
        }

        private void OnLocalFuseItemBlown(TrackedFuseItem item)
        {
            if (isApplyingRemoteFuse || Time.time <= fuseSuppressUntil)
            {
                item.SuppressEcho = false;
                return;
            }
            if (item.SuppressEcho)
            {
                item.SuppressEcho = false;
                if (Time.time <= item.SuppressUntilTime) return;
            }

            item.LastBlown = true;
            if (!IsNetworkRunning) return;

            SendFuseBoxState(FuseActionBlowout, item.Index, true);
            ctx?.Log?.Invoke($"[Гроза/Электричество] Перегорела пробка #{item.Index + 1}!");
        }

        private void PollFuseAndStormChanges(float now)
        {
            if (isApplyingRemoteFuse || now <= fuseSuppressUntil) return;

            if (fuseTableFsm != null)
            {
                if (fuseTableMainSwitchVar != null)
                {
                    bool mainOn = fuseTableMainSwitchVar.Value;
                    if (mainOn != lastMainSwitchOn)
                    {
                        lastMainSwitchOn = mainOn;
                        SendFuseBoxState(FuseActionMainSwitch, 0, mainOn);
                    }
                }

                if (fuseTableBlownVar != null)
                {
                    bool blown = fuseTableBlownVar.Value;
                    if (blown && !lastFuseTableBlown)
                    {
                        lastFuseTableBlown = true;
                        SendFuseBoxState(FuseActionBlowout, FuseAllMaskIndex, true);
                    }
                    else if (!blown && lastFuseTableBlown)
                    {
                        lastFuseTableBlown = false;
                    }
                }

                string curState = fuseTableFsm.ActiveStateName;
                if (!string.IsNullOrEmpty(curState) && curState != lastFuseTableStateName)
                {
                    lastFuseTableStateName = curState;
                    if (curState.IndexOf("Blow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        curState.IndexOf("Lightning", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        lastFuseTableBlown = true;
                        SendFuseBoxState(FuseActionBlowout, FuseAllMaskIndex, true);
                    }
                }
            }

            for (int i = 0; i < fuseItems.Length; i++)
            {
                TrackedFuseItem item = fuseItems[i];
                if (item == null || item.Fsm == null) continue;

                if (item.BlownVar != null)
                {
                    bool blown = item.BlownVar.Value;
                    if (blown != item.LastBlown)
                    {
                        item.LastBlown = blown;
                        SendFuseBoxState(FuseActionBlowout, item.Index, blown);
                    }
                }

                string stateName = item.Fsm.ActiveStateName;
                if (!string.IsNullOrEmpty(stateName) && stateName != item.LastStateName)
                {
                    item.LastStateName = stateName;
                    if (stateName.IndexOf("Blow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stateName.IndexOf("Broken", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        item.LastBlown = true;
                        SendFuseBoxState(FuseActionBlowout, item.Index, true);
                    }
                }
            }
        }

        private void OnLocalWaterToggle(TrackedWaterSource source)
        {
            if (isApplyingRemoteWater)
            {
                source.SuppressEcho = false;
                return;
            }
            if (source.SuppressEcho)
            {
                source.SuppressEcho = false;
                if (Time.time <= source.SuppressUntilTime) return;
            }

            bool cur = source.OnVar != null && source.OnVar.Value;
            bool nextState = !cur;
            source.IsOn = nextState;

            if (!IsNetworkRunning) return;

            if (source.IsWellPump)
            {
                SendWellPumpAction(source.Hash, true, nextState);
            }
            else
            {
                SendWaterTapToggle(source.Hash, nextState);
            }
        }

        private void OnLocalWellStroke(TrackedWaterSource well)
        {
            if (isApplyingRemoteWater)
            {
                well.SuppressEcho = false;
                return;
            }
            if (well.SuppressEcho)
            {
                well.SuppressEcho = false;
                if (Time.time <= well.SuppressUntilTime) return;
            }

            if (!IsNetworkRunning) return;
            SendWellPumpAction(well.Hash, false, true);
        }

        private void ApplyRemoteSwitch(TrackedSwitch sw, bool state)
        {
            bool prev = isApplyingRemoteSwitch;
            isApplyingRemoteSwitch = true;
            try
            {
                sw.SuppressEcho = true;
                sw.SuppressUntilTime = Time.time + 0.25f;
                sw.IsOn = state;

                if (sw.SwitchVar != null) sw.SwitchVar.Value = state;
                if (sw.RoomLight != null) sw.RoomLight.enabled = state;
                if (sw.LightGameObject != null) sw.LightGameObject.SetActive(state);
                if (sw.Fsm != null && sw.MpEvent != null)
                {
                    sw.Fsm.Fsm.Event(sw.MpEvent);
                }
            }
            finally
            {
                isApplyingRemoteSwitch = prev;
            }
        }

        private void ApplyRemoteFuseAction(byte actionKind, byte index, bool flag)
        {
            bool prev = isApplyingRemoteFuse;
            isApplyingRemoteFuse = true;
            fuseSuppressUntil = Time.time + 0.35f;
            try
            {
                if (actionKind == FuseActionHolderScrew)
                {
                    TrackedFuseHolder holder = null;
                    for (int i = 0; i < fuseHolders.Count; i++)
                    {
                        if (fuseHolders[i] != null && fuseHolders[i].Index == index)
                        {
                            holder = fuseHolders[i];
                            break;
                        }
                    }
                    if (holder != null)
                    {
                        holder.SuppressEcho = true;
                        holder.SuppressUntilTime = fuseSuppressUntil;
                        holder.ScrewedIn = flag;
                        if (holder.ScrewedVar != null) holder.ScrewedVar.Value = flag;

                        if (holder.Fsm != null)
                        {
                            if (flag)
                            {
                                if (holder.MpScrewEvent != null) holder.Fsm.Fsm.Event(holder.MpScrewEvent);
                                else holder.Fsm.SendEvent("SCREW");
                            }
                            else
                            {
                                if (holder.MpUnscrewEvent != null) holder.Fsm.Fsm.Event(holder.MpUnscrewEvent);
                                else holder.Fsm.SendEvent("UNSCREW");
                            }
                        }
                    }
                }
                else if (actionKind == FuseActionBlowout)
                {
                    if (index == FuseAllMaskIndex)
                    {
                        lastFuseTableBlown = flag;
                        if (fuseTableBlownVar != null) fuseTableBlownVar.Value = flag;
                        if (fuseTableFsm != null && flag)
                        {
                            fuseTableFsm.SendEvent("BLOW");
                            lastFuseTableStateName = fuseTableFsm.ActiveStateName;
                        }
                        for (int i = 0; i < fuseItems.Length; i++)
                        {
                            ApplySingleFuseItemBlow(fuseItems[i], flag);
                        }
                        if (flag)
                        {
                            for (int i = 0; i < fuseHolders.Count; i++)
                            {
                                TrackedFuseHolder h = fuseHolders[i];
                                if (h != null && h.Fsm != null && h.MpBlowEvent != null)
                                {
                                    h.SuppressEcho = true;
                                    h.SuppressUntilTime = fuseSuppressUntil;
                                    h.Fsm.Fsm.Event(h.MpBlowEvent);
                                }
                            }
                        }
                        ctx?.Log?.Invoke("[Гроза] Удар молнии выбил пробки в электрощитке дома!");
                    }
                    else if (index < fuseItems.Length)
                    {
                        ApplySingleFuseItemBlow(fuseItems[index], flag);
                        if (flag)
                        {
                            for (int i = 0; i < fuseHolders.Count; i++)
                            {
                                TrackedFuseHolder h = fuseHolders[i];
                                if (h != null && h.Index == index && h.Fsm != null && h.MpBlowEvent != null)
                                {
                                    h.SuppressEcho = true;
                                    h.SuppressUntilTime = fuseSuppressUntil;
                                    h.Fsm.Fsm.Event(h.MpBlowEvent);
                                    break;
                                }
                            }
                        }
                    }
                }
                else if (actionKind == FuseActionMainSwitch)
                {
                    lastMainSwitchOn = flag;
                    if (fuseTableMainSwitchVar != null) fuseTableMainSwitchVar.Value = flag;
                    if (fuseTableFsm != null)
                    {
                        fuseTableFsm.SendEvent(flag ? "ON" : "OFF");
                    }
                }
            }
            finally
            {
                isApplyingRemoteFuse = prev;
            }
        }

        private void ApplySingleFuseItemBlow(TrackedFuseItem item, bool blown)
        {
            if (item == null) return;
            item.SuppressEcho = true;
            item.SuppressUntilTime = fuseSuppressUntil;
            item.LastBlown = blown;
            if (item.BlownVar != null) item.BlownVar.Value = blown;
            if (item.Fsm != null && blown)
            {
                item.Fsm.SendEvent("BLOW");
                item.LastStateName = item.Fsm.ActiveStateName;
            }
        }

        private void ApplyRemoteWaterSource(TrackedWaterSource source, bool on)
        {
            if (source == null || source.Fsm == null) return;

            bool prev = isApplyingRemoteWater;
            isApplyingRemoteWater = true;
            try
            {
                source.SuppressEcho = true;
                source.SuppressUntilTime = Time.time + 0.25f;
                source.IsOn = on;

                if (source.OnVar != null) source.OnVar.Value = on;

                if (on)
                {
                    if (source.MpOnEvent != null) source.Fsm.Fsm.Event(source.MpOnEvent);
                    else source.Fsm.SendEvent("MP_ON");
                }
                else
                {
                    if (source.MpOffEvent != null) source.Fsm.Fsm.Event(source.MpOffEvent);
                    else source.Fsm.SendEvent("MP_OFF");
                }
            }
            finally
            {
                isApplyingRemoteWater = prev;
            }
        }

        private void ApplyRemoteWellStroke(TrackedWaterSource well)
        {
            if (well == null || well.Fsm == null) return;

            bool prev = isApplyingRemoteWater;
            isApplyingRemoteWater = true;
            try
            {
                well.SuppressEcho = true;
                well.SuppressUntilTime = Time.time + 0.25f;
                if (well.MpPumpEvent != null)
                {
                    well.Fsm.Fsm.Event(well.MpPumpEvent);
                }
                else
                {
                    well.Fsm.SendEvent("MP_PUMP");
                }
            }
            finally
            {
                isApplyingRemoteWater = prev;
            }
        }

        private void ApplySnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 4) return;
            int switchCount = reader.ReadInt32();
            for (int i = 0; i < switchCount; i++)
            {
                if (reader.UnreadBytes < 5) return;
                int hash = reader.ReadInt32();
                bool state = reader.ReadBoolean();
                if (switchesByHash.TryGetValue(hash, out TrackedSwitch sw))
                {
                    ApplyRemoteSwitch(sw, state);
                }
            }

            if (reader.UnreadBytes < 4) return;
            int holdersCount = reader.ReadInt32();
            for (int i = 0; i < holdersCount; i++)
            {
                if (reader.UnreadBytes < 2) return;
                byte idx = reader.ReadByte();
                bool screwed = reader.ReadBoolean();
                ApplyRemoteFuseAction(FuseActionHolderScrew, idx, screwed);
            }

            if (reader.UnreadBytes < 4) return;
            bool tableBlown = reader.ReadBoolean();
            byte blownMask = reader.ReadByte();
            bool hasMainSwitch = reader.ReadBoolean();
            bool mainSwitchOn = reader.ReadBoolean();

            if (tableBlown)
            {
                ApplyRemoteFuseAction(FuseActionBlowout, FuseAllMaskIndex, true);
            }
            else
            {
                for (int i = 0; i < 7; i++)
                {
                    bool blown = (blownMask & (1 << i)) != 0;
                    if (blown)
                    {
                        ApplyRemoteFuseAction(FuseActionBlowout, (byte)i, true);
                    }
                }
            }

            if (hasMainSwitch)
            {
                ApplyRemoteFuseAction(FuseActionMainSwitch, 0, mainSwitchOn);
            }

            if (reader.UnreadBytes < 4) return;
            int tapsCount = reader.ReadInt32();
            for (int i = 0; i < tapsCount; i++)
            {
                if (reader.UnreadBytes < 5) return;
                int hash = reader.ReadInt32();
                bool on = reader.ReadBoolean();
                if (tapsByHash.TryGetValue(hash, out TrackedWaterSource tap))
                {
                    ApplyRemoteWaterSource(tap, on);
                }
            }

            if (reader.UnreadBytes < 4) return;
            int wellsCount = reader.ReadInt32();
            for (int i = 0; i < wellsCount; i++)
            {
                if (reader.UnreadBytes < 6) return;
                int hash = reader.ReadInt32();
                bool isToggle = reader.ReadBoolean();
                bool on = reader.ReadBoolean();
                if (isToggle && wellsByHash.TryGetValue(hash, out TrackedWaterSource well))
                {
                    ApplyRemoteWaterSource(well, on);
                }
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
                    SendSnapshotToClient(targetId);
                }
            }
        }

        private void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !IsServer) return;

            using (var packet = new PacketWriter(PacketType.DomesticPowerAndWaterSync))
            {
                packet.WriteByte(DomesticProtocol.PowerWaterSnapshot);

                packet.WriteInt32(switchList.Count);
                for (int i = 0; i < switchList.Count; i++)
                {
                    TrackedSwitch sw = switchList[i];
                    bool state = sw.SwitchVar != null ? sw.SwitchVar.Value : sw.IsOn;
                    packet.WriteInt32(sw.Hash);
                    packet.WriteBoolean(state);
                }

                packet.WriteInt32(fuseHolders.Count);
                for (int i = 0; i < fuseHolders.Count; i++)
                {
                    TrackedFuseHolder h = fuseHolders[i];
                    bool screwed = h.ScrewedVar != null ? h.ScrewedVar.Value : h.ScrewedIn;
                    packet.WriteByte(h.Index);
                    packet.WriteBoolean(screwed);
                }

                byte blownMask = 0;
                for (int i = 0; i < fuseItems.Length; i++)
                {
                    TrackedFuseItem item = fuseItems[i];
                    if (item != null && ((item.BlownVar != null && item.BlownVar.Value) || item.LastBlown))
                    {
                        blownMask |= (byte)(1 << i);
                    }
                }
                bool tableBlown = fuseTableBlownVar != null ? fuseTableBlownVar.Value : lastFuseTableBlown;
                bool hasMainSwitch = fuseTableMainSwitchVar != null;
                bool mainOn = !hasMainSwitch || fuseTableMainSwitchVar.Value;

                packet.WriteBoolean(tableBlown);
                packet.WriteByte(blownMask);
                packet.WriteBoolean(hasMainSwitch);
                packet.WriteBoolean(mainOn);

                packet.WriteInt32(tapList.Count);
                for (int i = 0; i < tapList.Count; i++)
                {
                    TrackedWaterSource tap = tapList[i];
                    bool on = tap.OnVar != null ? tap.OnVar.Value : tap.IsOn;
                    packet.WriteInt32(tap.Hash);
                    packet.WriteBoolean(on);
                }

                packet.WriteInt32(wellList.Count);
                for (int i = 0; i < wellList.Count; i++)
                {
                    TrackedWaterSource well = wellList[i];
                    bool on = well.OnVar != null ? well.OnVar.Value : well.IsOn;
                    packet.WriteInt32(well.Hash);
                    packet.WriteBoolean(well.IsToggleType);
                    packet.WriteBoolean(on);
                }

                ctx.Transport.SendTo(packet, clientId);
            }
        }

        private void SendSwitchToggle(int switchHash, bool state)
        {
            using (var packet = new PacketWriter(PacketType.DomesticPowerAndWaterSync))
            {
                packet.WriteByte(DomesticProtocol.PowerSwitchState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(switchHash);
                packet.WriteBoolean(state);
                ctx.Transport.Send(packet);
            }
        }

        private void SendFuseBoxState(byte actionKind, byte index, bool flag)
        {
            using (var packet = new PacketWriter(PacketType.DomesticPowerAndWaterSync))
            {
                packet.WriteByte(DomesticProtocol.FuseBoxState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteByte(actionKind);
                packet.WriteByte(index);
                packet.WriteBoolean(flag);
                ctx.Transport.Send(packet);
            }
        }

        private void SendWaterTapToggle(int tapHash, bool on)
        {
            using (var packet = new PacketWriter(PacketType.DomesticPowerAndWaterSync))
            {
                packet.WriteByte(DomesticProtocol.WaterTapState);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(tapHash);
                packet.WriteBoolean(on);
                ctx.Transport.Send(packet);
            }
        }

        private void SendWellPumpAction(int wellHash, bool isToggle, bool on)
        {
            using (var packet = new PacketWriter(PacketType.DomesticPowerAndWaterSync))
            {
                packet.WriteByte(DomesticProtocol.WellPumpAction);
                packet.WriteInt32(LocalPlayerId);
                packet.WriteInt32(wellHash);
                packet.WriteBoolean(isToggle);
                packet.WriteBoolean(on);
                ctx.Transport.Send(packet);
            }
        }

        private int CountFuseItems()
        {
            int count = 0;
            for (int i = 0; i < fuseItems.Length; i++)
            {
                if (fuseItems[i] != null) count++;
            }
            return count;
        }
    }
}
