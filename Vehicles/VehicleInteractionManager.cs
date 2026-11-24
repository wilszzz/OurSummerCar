using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    public class VehicleInteractionManager : MonoBehaviour
    {
        private sealed class DashboardButtonState
        {
            public PlayMakerFSM Fsm;
            public string[] ActionEvents;
            public int UpdatingAction = -1;
        }

        private sealed class DashboardLeverState
        {
            public string Key;
            public PlayMakerFSM Fsm;
            public FsmFloat KnobPos;
            public FsmEvent IncreaseEvent;
            public FsmEvent DecreaseEvent;
            public FsmEvent StopFallbackEvent;
            public int Owner = -1; // -1 = никто, 0 = хост, 1 = клиент
            public bool StopMoving;
        }

        private sealed class InteriorLightState
        {
            public PlayMakerFSM Fsm;
            public FsmBool LightOn;
            public bool Updating;
        }

        private sealed class TurnSignalState
        {
            public PlayMakerFSM Fsm;
            public string[] EventNames;
            public int UpdatingIndex = -1;
            public int LastSentIndex = 0;
        }

        private sealed class HornButtonState
        {
            public PlayMakerFSM Fsm;
            public bool SuppressNext;
            public bool LocalActive;
            public bool RemoteActive;
        }

        private sealed class StarterState
        {
            public PlayMakerFSM Fsm;
            public FsmBool Acc;
            public FsmBool ShutOff;
            public FsmBool Starting;
            public FsmEvent TriggerStarting;
            public bool StartingMP;

            public void Start(bool val, bool accVal)
            {
                if (val && Fsm != null && TriggerStarting != null)
                {
                    StartingMP = true;
                    Fsm.Fsm.Event(TriggerStarting);
                }
                if (Starting != null) Starting.Value = val;
                if (Acc != null) Acc.Value = accVal;
                if (ShutOff != null) ShutOff.Value = !val;
            }
        }

        private sealed class IgnitionState
        {
            public PlayMakerFSM Fsm;
            public FsmBool Acc;
            public FsmBool MotorOn;
            public FsmEvent UpdateEvent;
            public StarterState Starter;
            public bool UpdatingMP;
            public bool Starting;

            public void ToggleKey(byte stage)
            {
                switch (stage)
                {
                    case 0: // Off
                        if (Acc != null) Acc.Value = true;
                        Starter?.Start(false, false);
                        break;
                    case 1: // ACC
                        if (Acc != null) Acc.Value = false;
                        Starter?.Start(false, true);
                        break;
                    case 2: // MotorRunning
                        if (Acc != null) Acc.Value = true;
                        Starter?.Start(true, true);
                        break;
                }

                UpdatingMP = true;
                Starting = (stage == 2);
                if (MotorOn != null) MotorOn.Value = false;
                if (Fsm != null && UpdateEvent != null)
                {
                    Fsm.Fsm.Event(UpdateEvent);
                }
            }
        }

        private NetworkTransport transport;
        public Action<string> OnLog;

        private readonly Dictionary<string, PlayMakerFSM> vehicleIgnitions = new Dictionary<string, PlayMakerFSM>();
        private readonly Dictionary<string, PlayMakerFSM> vehicleSwitches = new Dictionary<string, PlayMakerFSM>();
        private readonly List<PlayMakerFSM> hydraulicFsms = new List<PlayMakerFSM>();

        private readonly Dictionary<string, IgnitionState> hayosikoIgnitions = new Dictionary<string, IgnitionState>();
        private readonly Dictionary<string, DashboardButtonState> dashboardButtons = new Dictionary<string, DashboardButtonState>();
        private readonly Dictionary<string, DashboardLeverState> dashboardLevers = new Dictionary<string, DashboardLeverState>();
        private readonly Dictionary<string, InteriorLightState> interiorLights = new Dictionary<string, InteriorLightState>();
        private readonly Dictionary<string, TurnSignalState> turnSignals = new Dictionary<string, TurnSignalState>();
        private readonly Dictionary<string, HornButtonState> hornButtons = new Dictionary<string, HornButtonState>();

        private bool hayosikoHooked = false;
        private float nextRetryTime = 0f;
        private bool isSuppressingAction = false;

        private int MyPlayerId => (transport != null && transport.IsServer) ? 0 : 1;

        public void Reset()
        {
            vehicleIgnitions.Clear();
            vehicleSwitches.Clear();
            hydraulicFsms.Clear();
            hayosikoIgnitions.Clear();
            dashboardButtons.Clear();
            dashboardLevers.Clear();
            interiorLights.Clear();
            turnSignals.Clear();
            hornButtons.Clear();
            hayosikoHooked = false;
            isSuppressingAction = false;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            HookAllVehicles();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (!hayosikoHooked && Time.time >= nextRetryTime)
            {
                nextRetryTime = Time.time + 3.0f;
                var hayo = GameObject.Find("HAYOSIKO(1500kg, 250)");
                if (hayo != null)
                {
                    HookHayosikoControls(hayo);
                }
            }

            // Освобождение удержания рычагов (ручник Hayosiko) при отпускании ЛКМ/ПКМ по модели WreckMP FsmDashboardLever
            if (dashboardLevers.Count > 0)
            {
                int myId = MyPlayerId;
                if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1))
                {
                    foreach (var kv in dashboardLevers)
                    {
                        var lever = kv.Value;
                        if (lever != null && lever.Owner == myId)
                        {
                            lever.Owner = -1;
                            float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                            SendVehicleSwitchValue(lever.Key, 0, val);
                        }
                    }
                }
            }
        }

        public void HookAllVehicles()
        {
            Reset();

            string[] vehicleNames = new string[]
            {
                "SATSUMA(557kg, 248)",
                "HAYOSIKO(1500kg, 250)",
                "RCO_RUSCKO12(270)",
                "GIFU(750/450psi)",
                "KEKMET(350-400psi)",
                "FERNDALE(1630kg)",
                "JONNEZ ES(Clone)",
                "FLATBED"
            };

            for (int i = 0; i < vehicleNames.Length; i++)
            {
                var vehGo = GameObject.Find(vehicleNames[i]);
                if (vehGo != null)
                {
                    HookVehicleComponents(vehGo);
                }
            }

            // Лодка
            var boatGo = GameObject.Find("BOAT") ?? GameObject.Find("Boat");
            if (boatGo != null)
            {
                HookVehicleComponents(boatGo);
            }
        }

        private void HookVehicleComponents(GameObject vehGo)
        {
            string vehKey = vehGo.name;

            if (vehKey.StartsWith("HAYOSIKO"))
            {
                HookHayosikoControls(vehGo);
                return;
            }

            // Зажигание / Ключи
            var fsms = vehGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                var fsm = fsms[i];
                if (fsm == null) continue;
                string goName = fsm.gameObject.name;

                if (goName.Contains("Ignition") || goName.Contains("starter") || goName.Contains("Key"))
                {
                    if (fsm.FsmName == "Use" || fsm.FsmName == "Logic")
                    {
                        fsm.Initialize();
                        string key = $"{vehKey}_{goName}";
                        vehicleIgnitions[key] = fsm;

                        string startState = fsm.HasState("Start") ? "Start" : (fsm.HasState("ACC") ? "ACC" : (fsm.HasState("ON") ? "ON" : null));
                        if (startState != null)
                        {
                            fsm.InsertAction(startState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendVehicleIgnition(key, true);
                            }, 0, false);
                        }
                    }
                }

                // Переключатели (свет, дворники, аварийка, подсос)
                if (goName.Contains("Light") || goName.Contains("Wiper") || goName.Contains("Hazard") ||
                    goName.Contains("Choke") || goName.Contains("Blinker") || goName.Contains("Horn"))
                {
                    if (fsm.FsmName == "Use" || fsm.FsmName == "Switch")
                    {
                        fsm.Initialize();
                        string key = $"{vehKey}_{goName}";
                        vehicleSwitches[key] = fsm;

                        string toggleState = fsm.HasState("Switch") ? "Switch" : (fsm.HasState("Toggle") ? "Toggle" : (fsm.HasState("State 1") ? "State 1" : null));
                        if (toggleState != null)
                        {
                            fsm.InsertAction(toggleState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendVehicleSwitch(key, 1);
                            }, 0, false);
                        }
                    }
                }

                // Гидравлика (Кекмет стрела/ковш/кузов, Гифу насос/ось)
                if (goName.Contains("Hydraulic") || goName.Contains("Tip trailer") || goName.Contains("PTO") || goName.Contains("LiftAxle"))
                {
                    fsm.Initialize();
                    hydraulicFsms.Add(fsm);
                }
            }
        }

        public void SendVehicleIgnition(string key, bool on)
        {
            SendVehicleIgnitionStage(key, on, on ? (byte)2 : (byte)0);
        }

        private void SendVehicleIgnitionStage(string key, bool on, byte stage)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleIgnition))
            {
                packet.WriteString(key);
                packet.WriteBoolean(on);
                packet.WriteByte(stage);
                transport.Send(packet);
            }
        }

        public void SendVehicleSwitch(string key, byte state)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleSwitchToggle))
            {
                packet.WriteString(key);
                packet.WriteByte(state);
                transport.Send(packet);
            }
        }

        private void SendVehicleSwitchValue(string key, byte state, float value)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleSwitchToggle))
            {
                packet.WriteString(key);
                packet.WriteByte(state);
                packet.WriteSingle(value);
                transport.Send(packet);
            }
        }

        public void SendHydraulics(int hydraulicId, float value)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleHydraulics))
            {
                packet.WriteInt32(hydraulicId);
                packet.WriteSingle(value);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.VehicleIgnition)
            {
                string key = reader.ReadString();
                bool on = reader.ReadBoolean();
                byte stage = reader.UnreadBytes >= 1 ? reader.ReadByte() : (on ? (byte)2 : (byte)0);

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleIgnition))
                    {
                        relay.WriteString(key);
                        relay.WriteBoolean(on);
                        relay.WriteByte(stage);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteIgnition(key, on, stage);
            }
            else if (reader.Type == PacketType.VehicleSwitchToggle)
            {
                string key = reader.ReadString();
                byte state = reader.ReadByte();
                bool hasFloat = reader.UnreadBytes >= 4;
                float floatVal = hasFloat ? reader.ReadSingle() : 0f;

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleSwitchToggle))
                    {
                        relay.WriteString(key);
                        relay.WriteByte(state);
                        if (hasFloat) relay.WriteSingle(floatVal);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteSwitch(key, state, hasFloat, floatVal, connectionId);
            }
            else if (reader.Type == PacketType.VehicleHydraulics)
            {
                int id = reader.ReadInt32();
                float val = reader.ReadSingle();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.VehicleHydraulics))
                    {
                        relay.WriteInt32(id);
                        relay.WriteSingle(val);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteHydraulics(id, val);
            }
        }

        private void ApplyRemoteIgnition(string key, bool on, byte stage)
        {
            if (hayosikoIgnitions.TryGetValue(key, out var ign) && ign != null)
            {
                ign.ToggleKey(stage);
                OnLog?.Invoke($"[Транспорт] Зажигание {key} -> {(stage == 2 ? "СТАРТ" : (stage == 1 ? "ACC" : "ВЫКЛ"))}");
                return;
            }

            try
            {
                isSuppressingAction = true;
                if (vehicleIgnitions.TryGetValue(key, out var fsm) && fsm != null)
                {
                    if (on)
                    {
                        fsm.SendEvent("MP_START");
                        fsm.SendEvent("Motor starting");
                        fsm.SendEvent("START");
                    }
                    else
                    {
                        fsm.SendEvent("MP_OFF");
                        fsm.SendEvent("Motor OFF");
                        fsm.SendEvent("STOP");
                    }
                    OnLog?.Invoke($"[Транспорт] Зажигание {key} -> {(on ? "СТАРТ" : "СТОП")}");
                }
            }
            finally
            {
                isSuppressingAction = false;
            }
        }

        private void ApplyRemoteSwitch(string key, byte state, bool hasFloat, float floatVal, int connectionId)
        {
            if (dashboardLevers.TryGetValue(key, out var lever) && lever != null)
            {
                int remoteOwner = transport.IsServer ? (connectionId > 0 ? connectionId : 1) : 0;
                if (state == 1 || state == 2)
                {
                    lever.Owner = remoteOwner;
                    if (lever.Fsm != null)
                    {
                        FsmEvent ev = (state == 1) ? lever.IncreaseEvent : lever.DecreaseEvent;
                        if (ev != null) lever.Fsm.Fsm.Event(ev);
                    }
                }
                else
                {
                    lever.Owner = -1;
                    if (hasFloat && lever.KnobPos != null)
                    {
                        lever.KnobPos.Value = floatVal;
                    }
                    lever.StopMoving = true;
                    if (lever.Fsm != null && lever.IncreaseEvent != null)
                    {
                        lever.Fsm.Fsm.Event(lever.IncreaseEvent);
                    }
                }
                return;
            }

            if (interiorLights.TryGetValue(key, out var light) && light != null && light.Fsm != null)
            {
                light.Updating = true;
                light.Fsm.SendEvent(state == 1 ? "DOOROPEN" : "DOORCLOSE");
                OnLog?.Invoke($"[Транспорт] Свет в салоне {key} -> {(state == 1 ? "ВКЛ" : "ВЫКЛ")}");
                return;
            }

            if (dashboardButtons.TryGetValue(key, out var btn) && btn != null && btn.Fsm != null)
            {
                if (btn.ActionEvents != null && state < btn.ActionEvents.Length)
                {
                    btn.UpdatingAction = state;
                    btn.Fsm.SendEvent(btn.ActionEvents[state]);
                    OnLog?.Invoke($"[Транспорт] Переключатель {key} -> {state}");
                }
                return;
            }

            if (turnSignals.TryGetValue(key, out var ts) && ts != null && ts.Fsm != null)
            {
                if (ts.EventNames != null && state < ts.EventNames.Length)
                {
                    ts.UpdatingIndex = state;
                    ts.LastSentIndex = state;
                    ts.Fsm.SendEvent(ts.EventNames[state]);
                    OnLog?.Invoke($"[Транспорт] Поворотники {key} -> {state}");
                }
                return;
            }

            if (hornButtons.TryGetValue(key, out var horn) && horn != null && horn.Fsm != null)
            {
                bool active = (state == 1);
                horn.RemoteActive = active;
                horn.SuppressNext = true;
                horn.Fsm.SendEvent(active ? "MP_HORN_ON" : "MP_HORN_OFF");
                return;
            }

            try
            {
                isSuppressingAction = true;
                if (vehicleSwitches.TryGetValue(key, out var fsm) && fsm != null)
                {
                    fsm.SendEvent("MP_TOGGLE");
                    fsm.SendEvent("TOGGLE");
                    OnLog?.Invoke($"[Транспорт] Переключатель {key} -> {state}");
                }
            }
            finally
            {
                isSuppressingAction = false;
            }
        }

        private void HookHayosikoControls(GameObject vehGo)
        {
            if (vehGo == null || hayosikoHooked) return;
            hayosikoHooked = true;
            string vehKey = vehGo.name;

            // 1. Ручник (LOD/Dashboard/ParkingBrake) -> FSM "Use" (INCREASE / DECREASE) по модели FsmDashboardLever
            Transform parkingBrake = vehGo.transform.Find("LOD/Dashboard/ParkingBrake") ??
                                     PlayMakerUtilities.FindChildRecursive(vehGo.transform, "ParkingBrake");
            if (parkingBrake != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(parkingBrake.gameObject, "Use");
                if (fsm != null)
                {
                    HookDashboardLever($"{vehKey}_handbrake", fsm);
                }
            }

            // 2. Свет в салоне (LOD/InteriorLight/Use) -> FSM "Use", bool "LightON" по модели FsmInteriorLight
            Transform interiorLight = vehGo.transform.Find("LOD/InteriorLight/Use") ??
                                      vehGo.transform.Find("LOD/InteriorLight") ??
                                      PlayMakerUtilities.FindChildRecursive(vehGo.transform, "InteriorLight");
            if (interiorLight != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(interiorLight.gameObject, "Use") ??
                          interiorLight.GetComponentInChildren<PlayMakerFSM>();
                if (fsm != null)
                {
                    HookInteriorLight($"{vehKey}_interior_light", fsm);
                }
            }

            // 3. Фары (LOD/Dashboard/Knobs/Lights) -> FSM "Use" ("Longs", "Shorts", "Test", "Off") по модели FsmDashboardButton
            Transform lightsTr = vehGo.transform.Find("LOD/Dashboard/Knobs/Lights") ??
                                 PlayMakerUtilities.FindChildRecursive(vehGo.transform, "Lights");
            if (lightsTr != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(lightsTr.gameObject, "Use");
                if (fsm != null)
                {
                    HookDashboardButton($"{vehKey}_knob_lights", fsm);
                }
            }

            // 4. Дворники (LOD/Dashboard/Knobs/ButtonWipers) -> FSM "Use" ("Fast", "Slow", "Test 2", "Off") по модели FsmDashboardButton
            Transform wipersTr = vehGo.transform.Find("LOD/Dashboard/Knobs/ButtonWipers") ??
                                 PlayMakerUtilities.FindChildRecursive(vehGo.transform, "ButtonWipers");
            if (wipersTr != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(wipersTr.gameObject, "Use");
                if (fsm != null)
                {
                    HookDashboardButton($"{vehKey}_knob_wipers", fsm);
                }
            }

            // 5. Поворотники (LOD/Dashboard/TurnSignals) -> FSM "Usage" по модели FsmTurnSignals
            Transform turnSignalsTr = vehGo.transform.Find("LOD/Dashboard/TurnSignals") ??
                                      PlayMakerUtilities.FindChildRecursive(vehGo.transform, "TurnSignals");
            if (turnSignalsTr != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(turnSignalsTr.gameObject, "Usage") ??
                          turnSignalsTr.GetComponent<PlayMakerFSM>();
                if (fsm != null)
                {
                    HookTurnSignals($"{vehKey}_turn_signals", fsm);
                }
            }

            // 6. Сигнал (LOD/Dashboard/ButtonHorn) -> FSM "Use"
            Transform hornTr = vehGo.transform.Find("LOD/Dashboard/ButtonHorn") ??
                               PlayMakerUtilities.FindChildRecursive(vehGo.transform, "ButtonHorn");
            if (hornTr != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(hornTr.gameObject, "Use");
                if (fsm != null)
                {
                    HookHornButton($"{vehKey}_horn", fsm);
                }
            }

            // 7. Зажигание и Стартер (LOD/Dashboard/Knobs/Ignition + Starter) по модели FsmIgnition + FsmStarter
            Transform ignTr = vehGo.transform.Find("LOD/Dashboard/Knobs/Ignition") ??
                              PlayMakerUtilities.FindChildRecursive(vehGo.transform, "Ignition");
            PlayMakerFSM ignitionFsm = ignTr != null ? PlayMakerUtilities.FindFsmOnGameObject(ignTr.gameObject, "Use") : null;

            PlayMakerFSM starterFsm = null;
            var allFsms = vehGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < allFsms.Length; i++)
            {
                if (allFsms[i] != null && allFsms[i].FsmName == "Starter")
                {
                    starterFsm = allFsms[i];
                    break;
                }
            }

            if (ignitionFsm != null)
            {
                HookHayosikoIgnition($"{vehKey}_ignition", ignitionFsm, starterFsm);
            }
        }

        private void HookDashboardLever(string key, PlayMakerFSM fsm)
        {
            try
            {
                fsm.Initialize();
                string incName = fsm.HasState("INCREASE") ? "INCREASE" : (fsm.HasState("INCREASE 2") ? "INCREASE 2" : null);
                string decName = fsm.HasState("DECREASE") ? "DECREASE" : (fsm.HasState("DECREASE 2") ? "DECREASE 2" : null);
                if (incName == null || decName == null) return;

                var lever = new DashboardLeverState
                {
                    Key = key,
                    Fsm = fsm
                };

                lever.IncreaseEvent = fsm.AddSafeEvent("MP_INCREASE");
                lever.DecreaseEvent = fsm.AddSafeEvent("MP_DECREASE");
                fsm.AddSafeGlobalTransition(lever.IncreaseEvent, incName);
                fsm.AddSafeGlobalTransition(lever.DecreaseEvent, decName);

                fsm.InsertAction(incName, () =>
                {
                    if (!lever.StopMoving)
                    {
                        int myId = MyPlayerId;
                        if (lever.Owner == -1 || lever.Owner == myId)
                        {
                            lever.Owner = myId;
                            float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                            SendVehicleSwitchValue(key, 1, val);
                        }
                    }
                }, 0, false);

                fsm.InsertAction(decName, () =>
                {
                    if (!lever.StopMoving)
                    {
                        int myId = MyPlayerId;
                        if (lever.Owner == -1 || lever.Owner == myId)
                        {
                            lever.Owner = myId;
                            float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                            SendVehicleSwitchValue(key, 2, val);
                        }
                    }
                }, 0, false);

                for (int s = 0; s < 2; s++)
                {
                    string stateName = (s == 0) ? incName : decName;
                    FsmState state = fsm.GetState(stateName);
                    if (state == null || state.Actions == null) continue;

                    FsmStateAction[] actions = state.Actions;
                    var newActions = new List<FsmStateAction>(actions.Length);

                    if (s == 0)
                    {
                        for (int a = 0; a < actions.Length; a++)
                        {
                            if (actions[a] is FloatAdd floatAdd)
                            {
                                lever.KnobPos = floatAdd.floatVariable;
                                break;
                            }
                        }
                    }

                    bool doMousePick = false;
                    int layerMask = 0;
                    float rayDistance = 0f;
                    FsmEvent mouseOff = null;

                    bool doMbd = false;
                    MouseButton mbdBtn = MouseButton.Left;
                    FsmEvent mbdEvent = null;

                    bool doMbu = false;
                    MouseButton mbuBtn = MouseButton.Left;
                    FsmEvent mbuEvent = null;

                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is MousePickEvent mousePick)
                        {
                            doMousePick = true;
                            layerMask = ActionHelpers.LayerArrayToLayerMask(mousePick.layerMask, mousePick.invertMask.Value);
                            rayDistance = mousePick.rayDistance.Value;
                            mouseOff = mousePick.mouseOff;
                        }
                        else if (actions[a] is GetMouseButtonDown mbd)
                        {
                            doMbd = true;
                            mbdBtn = mbd.button;
                            mbdEvent = mbd.sendEvent;
                        }
                        else if (actions[a] is GetMouseButtonUp mbu)
                        {
                            doMbu = true;
                            mbuBtn = mbu.button;
                            mbuEvent = mbu.sendEvent;
                            if (lever.StopFallbackEvent == null) lever.StopFallbackEvent = mbuEvent;
                        }
                        else
                        {
                            newActions.Add(actions[a]);
                        }
                    }

                    if (doMousePick || doMbd || doMbu)
                    {
                        int myIdConst = 0; // вычисляется динамически в лямбде через MyPlayerId
                        newActions.Add(new PM_Hook(() =>
                        {
                            myIdConst = MyPlayerId;
                            if (lever.StopMoving)
                            {
                                lever.StopMoving = false;
                                if (mbuEvent != null) fsm.Fsm.Event(mbuEvent);
                                else if (lever.StopFallbackEvent != null) fsm.Fsm.Event(lever.StopFallbackEvent);
                                else fsm.SendEvent("FINISHED");
                            }
                            else if (lever.Owner == -1 || lever.Owner == myIdConst)
                            {
                                if (doMousePick)
                                {
                                    bool isOver = ActionHelpers.IsMouseOver(fsm.gameObject, rayDistance, layerMask);
                                    fsm.Fsm.RaycastHitInfo = ActionHelpers.mousePickInfo;
                                    if (!isOver)
                                    {
                                        if (mouseOff != null) fsm.Fsm.Event(mouseOff);
                                        return;
                                    }
                                }

                                if (doMbd && ((mbdBtn == MouseButton.Left && Input.GetMouseButtonDown(0)) || (mbdBtn == MouseButton.Right && Input.GetMouseButtonDown(1))))
                                {
                                    if (mbdEvent != null) fsm.Fsm.Event(mbdEvent);
                                }
                                else if (doMbu && ((mbuBtn == MouseButton.Left && Input.GetMouseButtonUp(0)) || (mbuBtn == MouseButton.Right && Input.GetMouseButtonUp(1))))
                                {
                                    if (mbuEvent != null) fsm.Fsm.Event(mbuEvent);
                                }
                            }
                        }, true));

                        state.Actions = newActions.ToArray();
                    }
                }

                fsm.Initialize();
                dashboardLevers[key] = lever;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки рычага {key}: {ex.Message}");
            }
        }

        private void HookInteriorLight(string key, PlayMakerFSM fsm)
        {
            try
            {
                fsm.Initialize();
                var light = new InteriorLightState
                {
                    Fsm = fsm,
                    LightOn = fsm.FsmVariables.FindFsmBool("LightON")
                };

                bool hookedOpen = false;
                bool hookedClose = false;

                if (fsm.FsmGlobalTransitions != null && fsm.FsmGlobalTransitions.Length > 0)
                {
                    for (int i = 0; i < fsm.FsmGlobalTransitions.Length; i++)
                    {
                        var gt = fsm.FsmGlobalTransitions[i];
                        if (gt == null || gt.FsmEvent == null) continue;

                        if (gt.FsmEvent.Name == "DOOROPEN" && !hookedOpen)
                        {
                            hookedOpen = true;
                            fsm.InsertAction(gt.ToState, () =>
                            {
                                if (light.Updating)
                                {
                                    light.Updating = false;
                                    return;
                                }
                                SendVehicleSwitch(key, 1);
                            }, -1, false);
                        }
                        else if (gt.FsmEvent.Name == "DOORCLOSE" && !hookedClose)
                        {
                            hookedClose = true;
                            fsm.InsertAction(gt.ToState, () =>
                            {
                                if (light.Updating)
                                {
                                    light.Updating = false;
                                    return;
                                }
                                SendVehicleSwitch(key, 0);
                            }, -1, false);
                        }
                    }
                }

                if (!hookedOpen || !hookedClose)
                {
                    string onState = fsm.HasState("State 2") ? "State 2"
                        : (fsm.HasState("Light") ? "Light"
                        : (fsm.HasState("State 1") ? "State 1" : null));
                    string offState = fsm.HasState("State 3") ? "State 3"
                        : (fsm.HasState("OFF") ? "OFF" : null);

                    if (!hookedOpen && onState != null)
                    {
                        fsm.AddSafeGlobalTransition("DOOROPEN", onState);
                        fsm.InsertAction(onState, () =>
                        {
                            if (light.Updating)
                            {
                                light.Updating = false;
                                return;
                            }
                            SendVehicleSwitch(key, 1);
                        }, -1, false);
                    }

                    if (!hookedClose && offState != null)
                    {
                        fsm.AddSafeGlobalTransition("DOORCLOSE", offState);
                        fsm.InsertAction(offState, () =>
                        {
                            if (light.Updating)
                            {
                                light.Updating = false;
                                return;
                            }
                            SendVehicleSwitch(key, 0);
                        }, -1, false);
                    }
                }

                fsm.Initialize();
                interiorLights[key] = light;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки света салона {key}: {ex.Message}");
            }
        }

        private void HookDashboardButton(string key, PlayMakerFSM fsm)
        {
            try
            {
                fsm.Initialize();
                FsmState testState = null;
                if (fsm.FsmStates != null)
                {
                    for (int i = 0; i < fsm.FsmStates.Length; i++)
                    {
                        if (fsm.FsmStates[i] != null && fsm.FsmStates[i].Name.Contains("Test"))
                        {
                            testState = fsm.FsmStates[i];
                            break;
                        }
                    }
                }

                if (testState == null || testState.Transitions == null || testState.Transitions.Length == 0)
                {
                    return;
                }

                var btn = new DashboardButtonState
                {
                    Fsm = fsm,
                    ActionEvents = new string[testState.Transitions.Length]
                };

                for (int i = 0; i < testState.Transitions.Length; i++)
                {
                    var trans = testState.Transitions[i];
                    string evName = "MP_" + trans.EventName;
                    btn.ActionEvents[i] = evName;
                    fsm.AddSafeGlobalTransition(evName, trans.ToState);

                    int index = i;
                    fsm.InsertAction(trans.ToState, () =>
                    {
                        if (btn.UpdatingAction == index)
                        {
                            btn.UpdatingAction = -1;
                            return;
                        }
                        SendVehicleSwitch(key, (byte)index);
                    }, 0, false);
                }

                fsm.Initialize();
                dashboardButtons[key] = btn;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки кнопки {key}: {ex.Message}");
            }
        }

        private void HookTurnSignals(string key, PlayMakerFSM fsm)
        {
            try
            {
                fsm.Initialize();
                FsmState state2 = fsm.GetState("State 2");
                if (state2 == null || state2.Transitions == null) return;

                string leftState = null;
                string rightState = null;
                string offState = fsm.HasState("State 3") ? "State 3" : null;

                for (int i = 0; i < state2.Transitions.Length; i++)
                {
                    var t = state2.Transitions[i];
                    if (t == null) continue;
                    if (t.EventName == "LEFT") leftState = t.ToState;
                    else if (t.EventName == "RIGHT") rightState = t.ToState;
                    else if (offState == null && t.EventName == "OFF") offState = t.ToState;
                }

                if (offState == null || leftState == null || rightState == null) return;

                var ts = new TurnSignalState
                {
                    Fsm = fsm,
                    EventNames = new string[] { "MP_OFF", "MP_LEFT", "MP_RIGHT" },
                    LastSentIndex = (fsm.Fsm != null && fsm.Fsm.StartState == offState) ? 0 : -1
                };

                fsm.AddSafeGlobalTransition(ts.EventNames[0], offState);
                fsm.InsertAction(offState, () =>
                {
                    if (ts.UpdatingIndex == 0)
                    {
                        ts.UpdatingIndex = -1;
                        return;
                    }
                    if (ts.LastSentIndex != 0)
                    {
                        ts.LastSentIndex = 0;
                        SendVehicleSwitch(key, 0);
                    }
                }, 0, false);

                fsm.AddSafeGlobalTransition(ts.EventNames[1], leftState);
                fsm.InsertAction(leftState, () =>
                {
                    if (ts.UpdatingIndex == 1)
                    {
                        ts.UpdatingIndex = -1;
                        return;
                    }
                    ts.LastSentIndex = 1;
                    SendVehicleSwitch(key, 1);
                }, 0, false);

                fsm.AddSafeGlobalTransition(ts.EventNames[2], rightState);
                fsm.InsertAction(rightState, () =>
                {
                    if (ts.UpdatingIndex == 2)
                    {
                        ts.UpdatingIndex = -1;
                        return;
                    }
                    ts.LastSentIndex = 2;
                    SendVehicleSwitch(key, 2);
                }, 0, false);

                fsm.Initialize();
                turnSignals[key] = ts;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки поворотников {key}: {ex.Message}");
            }
        }

        private void HookHornButton(string key, PlayMakerFSM fsm)
        {
            try
            {
                fsm.Initialize();

                // Если ButtonHorn построен по схеме Test-стейта (как FsmDashboardButton)
                if (fsm.FsmStates != null && fsm.FsmStates.Any(s => s != null && s.Name.Contains("Test")))
                {
                    HookDashboardButton(key, fsm);
                    return;
                }

                // Иначе ищем состояние нажатия (содержащее GetMouseButtonUp или стандартное имя)
                string onState = null;
                string offState = null;
                if (fsm.FsmStates != null)
                {
                    for (int i = 0; i < fsm.FsmStates.Length; i++)
                    {
                        var st = fsm.FsmStates[i];
                        if (st == null || st.Actions == null) continue;
                        for (int a = 0; a < st.Actions.Length; a++)
                        {
                            if (st.Actions[a] is GetMouseButtonUp mbu)
                            {
                                onState = st.Name;
                                if (mbu.sendEvent != null && st.Transitions != null)
                                {
                                    for (int t = 0; t < st.Transitions.Length; t++)
                                    {
                                        if (st.Transitions[t].EventName == mbu.sendEvent.Name)
                                        {
                                            offState = st.Transitions[t].ToState;
                                            break;
                                        }
                                    }
                                }
                                break;
                            }
                        }
                        if (onState != null) break;
                    }
                }

                if (onState == null)
                {
                    onState = fsm.HasState("Horn") ? "Horn"
                        : (fsm.HasState("Sound") ? "Sound"
                        : (fsm.HasState("ON") ? "ON"
                        : (fsm.HasState("State 1") ? "State 1" : null)));
                }
                if (offState == null)
                {
                    offState = fsm.HasState("OFF") ? "OFF"
                        : (fsm.HasState("State 2") ? "State 2"
                        : (fsm.HasState("Wait") ? "Wait" : (fsm.Fsm != null ? fsm.Fsm.StartState : null)));
                }

                if (onState == null || offState == null || onState == offState) return;

                var horn = new HornButtonState { Fsm = fsm };

                // Оборачиваем GetMouseButtonUp в onState, чтобы удаленный сигнал не сбрасывался сразу
                FsmState activeState = fsm.GetState(onState);
                if (activeState != null && activeState.Actions != null)
                {
                    var actions = activeState.Actions;
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is GetMouseButtonUp lmbup)
                        {
                            actions[a] = new PM_Hook(() =>
                            {
                                if (!horn.RemoteActive && Input.GetMouseButtonUp((int)lmbup.button))
                                {
                                    if (lmbup.sendEvent != null) fsm.Fsm.Event(lmbup.sendEvent);
                                }
                            }, true);
                            break;
                        }
                    }
                    activeState.Actions = actions;
                }

                fsm.AddSafeGlobalTransition("MP_HORN_ON", onState);
                fsm.AddSafeGlobalTransition("MP_HORN_OFF", offState);

                fsm.InsertAction(onState, () =>
                {
                    if (horn.SuppressNext)
                    {
                        horn.SuppressNext = false;
                        return;
                    }
                    horn.LocalActive = true;
                    SendVehicleSwitch(key, 1);
                }, 0, false);

                fsm.InsertAction(offState, () =>
                {
                    if (horn.SuppressNext)
                    {
                        horn.SuppressNext = false;
                        return;
                    }
                    if (horn.LocalActive)
                    {
                        horn.LocalActive = false;
                        SendVehicleSwitch(key, 0);
                    }
                }, 0, false);

                fsm.Initialize();
                hornButtons[key] = horn;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки сигнала {key}: {ex.Message}");
            }
        }

        private void HookHayosikoIgnition(string key, PlayMakerFSM ignitionFsm, PlayMakerFSM starterFsm)
        {
            try
            {
                ignitionFsm.Initialize();

                StarterState starterState = null;
                if (starterFsm != null)
                {
                    starterFsm.Initialize();
                    starterState = new StarterState
                    {
                        Fsm = starterFsm,
                        Acc = starterFsm.FsmVariables.FindFsmBool("ACC"),
                        ShutOff = starterFsm.FsmVariables.FindFsmBool("ShutOff"),
                        Starting = starterFsm.FsmVariables.FindFsmBool("Starting")
                    };
                }

                var ign = new IgnitionState
                {
                    Fsm = ignitionFsm,
                    Acc = ignitionFsm.FsmVariables.FindFsmBool("ACC"),
                    MotorOn = ignitionFsm.FsmVariables.FindFsmBool("MotorOn"),
                    Starter = starterState
                };

                if (starterState != null)
                {
                    string starterStartState = starterFsm.HasState("Turn key") ? "Turn key" : "Starting engine";
                    if (starterFsm.HasState(starterStartState))
                    {
                        starterState.TriggerStarting = starterFsm.AddSafeEvent("MP_START");
                        starterFsm.AddSafeGlobalTransition(starterState.TriggerStarting, starterStartState);

                        FsmState stStart = starterFsm.GetState(starterStartState);
                        if (stStart != null && stStart.Actions != null)
                        {
                            var actions = stStart.Actions;
                            for (int i = 0; i < actions.Length; i++)
                            {
                                if (actions[i] is GetMouseButtonUp lmbup)
                                {
                                    actions[i] = new PM_Hook(() =>
                                    {
                                        if (!starterState.StartingMP && Input.GetMouseButtonUp(0))
                                        {
                                            if (lmbup.sendEvent != null) starterFsm.Fsm.Event(lmbup.sendEvent);
                                        }
                                    }, true);
                                    break;
                                }
                            }
                            stStart.Actions = actions;
                        }
                    }

                    if (starterFsm.HasState("Wait for start"))
                    {
                        starterFsm.InsertAction("Wait for start", () =>
                        {
                            starterState.StartingMP = false;
                        }, -1, false);
                    }

                    if (starterFsm.HasState("Start engine"))
                    {
                        starterFsm.InsertAction("Start engine", () =>
                        {
                            ign.Starting = false;
                            ign.UpdatingMP = true;
                        }, -1, false);
                    }
                    else if (starterFsm.HasState("Motor running"))
                    {
                        starterFsm.InsertAction("Motor running", () =>
                        {
                            ign.Starting = false;
                            ign.UpdatingMP = true;
                        }, -1, false);
                    }

                    starterFsm.Initialize();
                }

                ign.UpdateEvent = ignitionFsm.AddSafeEvent("MP_UPDATE");
                string updateTarget = ignitionFsm.HasState("Sound") ? "Sound"
                    : (ignitionFsm.HasState("Test") ? "Test" : (ignitionFsm.Fsm != null ? ignitionFsm.Fsm.StartState : string.Empty));
                ignitionFsm.AddSafeGlobalTransition(ign.UpdateEvent, updateTarget);

                if (ignitionFsm.HasState("Motor starting"))
                {
                    ignitionFsm.InsertAction("Motor starting", () =>
                    {
                        if (!ign.Starting)
                        {
                            SendVehicleIgnitionStage(key, true, 2);
                        }
                    }, 0, false);

                    FsmState stMotorStarting = ignitionFsm.GetState("Motor starting");
                    if (stMotorStarting != null && stMotorStarting.Actions != null)
                    {
                        var actions = stMotorStarting.Actions;
                        for (int i = 0; i < actions.Length; i++)
                        {
                            if (actions[i] is GetMouseButtonUp lmbup)
                            {
                                actions[i] = new PM_Hook(() =>
                                {
                                    if ((!ign.Starting && Input.GetMouseButtonUp(0)) || (!ign.Starting && ign.UpdatingMP))
                                    {
                                        if (lmbup.sendEvent != null) ignitionFsm.Fsm.Event(lmbup.sendEvent);
                                        ign.UpdatingMP = false;
                                    }
                                }, true);
                                break;
                            }
                        }
                        stMotorStarting.Actions = actions;
                    }
                }

                if (ignitionFsm.HasState("Motor OFF"))
                {
                    ignitionFsm.InsertAction("Motor OFF", () =>
                    {
                        if (!ign.UpdatingMP)
                        {
                            SendVehicleIgnitionStage(key, false, 0);
                        }
                        ign.UpdatingMP = false;
                    }, 0, false);
                }

                if (ignitionFsm.HasState("ACC on"))
                {
                    FsmState stAccOn = ignitionFsm.GetState("ACC on");
                    if (stAccOn != null && stAccOn.Actions != null)
                    {
                        var actions = stAccOn.Actions;
                        for (int i = 0; i < actions.Length; i++)
                        {
                            if (actions[i] is GetMouseButtonUp lmbup2)
                            {
                                actions[i] = new PM_Hook(() =>
                                {
                                    if (Input.GetMouseButtonUp(0) || ign.UpdatingMP)
                                    {
                                        if (lmbup2.sendEvent != null) ignitionFsm.Fsm.Event(lmbup2.sendEvent);
                                        if (!ign.UpdatingMP)
                                        {
                                            SendVehicleIgnitionStage(key, false, 1);
                                        }
                                        ign.UpdatingMP = false;
                                    }
                                }, true);
                                break;
                            }
                        }
                        stAccOn.Actions = actions;
                    }
                }

                if (ignitionFsm.HasState("ACC on 2"))
                {
                    FsmEvent startEv = ignitionFsm.FsmEvents != null
                        ? ignitionFsm.FsmEvents.FirstOrDefault(e => e != null && e.Name == "START")
                        : null;
                    FsmState stAccOn2 = ignitionFsm.GetState("ACC on 2");
                    if (stAccOn2 != null && stAccOn2.Actions != null)
                    {
                        var actions = stAccOn2.Actions;
                        for (int i = 0; i < actions.Length; i++)
                        {
                            if (actions[i] is GetMouseButtonUp lmbup3)
                            {
                                actions[i] = new PM_Hook(() =>
                                {
                                    if (ign.Starting)
                                    {
                                        if (startEv != null) ignitionFsm.Fsm.Event(startEv);
                                        ign.UpdatingMP = false;
                                    }
                                    else if (Input.GetMouseButtonUp(0) || ign.UpdatingMP)
                                    {
                                        if (lmbup3.sendEvent != null) ignitionFsm.Fsm.Event(lmbup3.sendEvent);
                                    }
                                }, true);
                                break;
                            }
                        }
                        stAccOn2.Actions = actions;
                    }
                }

                ignitionFsm.Initialize();
                hayosikoIgnitions[key] = ign;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[VehicleInteractionManager] Ошибка настройки зажигания {key}: {ex.Message}");
            }
        }

        private void ApplyRemoteHydraulics(int id, float val)
        {
            if (id >= 0 && id < hydraulicFsms.Count)
            {
                var fsm = hydraulicFsms[id];
                if (fsm != null)
                {
                    var axisVar = fsm.FsmVariables.FindFsmFloat("Axis") ?? fsm.FsmVariables.FindFsmFloat("Value");
                    if (axisVar != null) axisVar.Value = val;
                }
            }
        }
    }
}
