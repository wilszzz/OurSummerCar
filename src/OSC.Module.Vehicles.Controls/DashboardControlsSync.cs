using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Синхронизация приборной панели и органов управления для всего автопарка:
    /// зажигание (Ignition/IgnitionSatsuma), стартер, фары, поворотники, аварийка, дворники,
    /// гудок, ручник (ParkingBrake Lever), свет салона, подсос (Choke), крутилки и спец-КПП/гидравлика
    /// (Gifu Transmission, Ferndale GearShifter, Kekmet Range & FrontHyd*).
    ///
    /// Архитектурная граница со Stage 2 (OSC.Module.Vehicles):
    /// Stage 2 владеет CarController.*Input (педали газа/тормоза/сцепления/руль) и SteeringWheel.enabled
    /// через PacketType.VehicleControlSync (68). Данный класс НИКОГДА не перезаписывает оси CarController
    /// и не захватывает MainRigidbody / DriveTrigger в OwnershipRegistry, а работает исключительно с FSM
    /// приборной панели и органов управления по PacketType.VehicleSystemToggle (69).
    /// </summary>
    internal sealed class DashboardControlsSync
    {
        private sealed class IgnitionEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM IgnitionFsm;
            public FsmBool Acc;
            public FsmBool MotorOn;
            public FsmEvent UpdateEvent;
            public PlayMakerFSM StarterFsm;
            public FsmBool StarterAcc;
            public FsmBool StarterShutOff;
            public FsmBool StarterStarting;
            public FsmFloat PlugHeat;
            public FsmFloat EngineTemp;
            public FsmEvent StarterTrigger;
            public bool StarterStartingMp;
            public bool UpdatingMp;
            public bool Starting;
            public byte Stage; // 0=Off, 1=ACC, 2=MotorRunning
        }

        private sealed class DashboardButtonEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public string[] ActionEvents;
            public int UpdatingAction = -1;
            public int CurrentAction = 255;
        }

        private sealed class DashboardLeverEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmFloat KnobPos;
            public FsmEvent IncreaseEvent;
            public FsmEvent DecreaseEvent;
            public FsmEvent StopFallbackEvent;
            public int Owner = -1;
            public bool StopMoving;
        }

        private sealed class DashboardKnobEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmFloat TargetValue;
            public FsmEvent UpdateEvent;
            public float AddStep;
            public bool Updating;
        }

        private sealed class TurnSignalEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public string[] EventNames;
            public int UpdatingIndex = -1;
            public int LastSentIndex = 0;
        }

        private sealed class InteriorLightEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmBool LightOn;
            public bool Updating;
        }

        private sealed class HornEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public bool SuppressNext;
            public bool LocalActive;
            public bool RemoteActive;
        }

        private sealed class GenericSwitchEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public bool IsTwoState;
            public bool SuppressNext;
            public byte CurrentState;
        }

        private sealed class GifuRangeEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmBool LowGears;
            public FsmEvent ToggleEvent;
            public bool Updating;
        }

        private sealed class FerndaleShifterEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmString Letter;
            public FsmEvent ShiftUpEvent;
            public FsmEvent ShiftDownEvent;
            public bool Updating;
            public bool LastShiftUp;
            public string LastLetter = "P";
        }

        private sealed class KekmetRangeEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmBool Range;
            public FsmEvent ToggleEvent;
            public bool Updating;
        }

        private sealed class KekmetFrontLoaderEntry
        {
            public int KeyHash;
            public VehicleCatalogEntry Vehicle;
            public PlayMakerFSM Fsm;
            public FsmFloat ArmRot;
            public string[] ActionEvents;
            public int UpdatingAction = -1;
            public int CurrentAction = 2;
            public int Owner = -1;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly VehicleCatalogEntry[] catalog;

        private readonly HashSet<int> hookedFsmInstanceIds = new HashSet<int>();
        private readonly Dictionary<int, IgnitionEntry> ignitions = new Dictionary<int, IgnitionEntry>();
        private readonly Dictionary<int, DashboardButtonEntry> buttons = new Dictionary<int, DashboardButtonEntry>();
        private readonly Dictionary<int, DashboardLeverEntry> levers = new Dictionary<int, DashboardLeverEntry>();
        private readonly Dictionary<int, DashboardKnobEntry> knobs = new Dictionary<int, DashboardKnobEntry>();
        private readonly Dictionary<int, TurnSignalEntry> turnSignals = new Dictionary<int, TurnSignalEntry>();
        private readonly Dictionary<int, InteriorLightEntry> interiorLights = new Dictionary<int, InteriorLightEntry>();
        private readonly Dictionary<int, HornEntry> horns = new Dictionary<int, HornEntry>();
        private readonly Dictionary<int, GenericSwitchEntry> genericSwitches = new Dictionary<int, GenericSwitchEntry>();
        private readonly Dictionary<int, GifuRangeEntry> gifuRanges = new Dictionary<int, GifuRangeEntry>();
        private readonly Dictionary<int, FerndaleShifterEntry> ferndaleShifters = new Dictionary<int, FerndaleShifterEntry>();
        private readonly Dictionary<int, KekmetRangeEntry> kekmetRanges = new Dictionary<int, KekmetRangeEntry>();
        private readonly Dictionary<int, KekmetFrontLoaderEntry> kekmetFrontLoaders = new Dictionary<int, KekmetFrontLoaderEntry>();

        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;
        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;

        public int HookedControlsCount =>
            ignitions.Count + buttons.Count + levers.Count + knobs.Count +
            turnSignals.Count + interiorLights.Count + horns.Count + genericSwitches.Count +
            gifuRanges.Count + ferndaleShifters.Count + kekmetRanges.Count + kekmetFrontLoaders.Count;

        public DashboardControlsSync(OscHostContext context, string ownerModuleId, VehicleCatalogEntry[] vehicleCatalog)
        {
            ctx = context;
            moduleId = ownerModuleId;
            catalog = vehicleCatalog;
        }

        public void Reset()
        {
            hookedFsmInstanceIds.Clear();
            ignitions.Clear();
            buttons.Clear();
            levers.Clear();
            knobs.Clear();
            turnSignals.Clear();
            interiorLights.Clear();
            horns.Clear();
            genericSwitches.Clear();
            gifuRanges.Clear();
            ferndaleShifters.Clear();
            kekmetRanges.Clear();
            kekmetFrontLoaders.Clear();
        }

        public void ScanVehicle(VehicleCatalogEntry veh)
        {
            if (veh == null || veh.RootObject == null) return;
            if (veh.Slot == VehicleSlot.Boat)
            {
                // Мотор лодки синхронизируется отдельным модулем OSC.Module.Vehicles.Boat
                return;
            }

            PlayMakerFSM[] fsms = veh.RootObject.GetComponentsInChildren<PlayMakerFSM>(true);
            if (fsms == null || fsms.Length == 0) return;

            HookVehicleIgnitionAndStarter(veh, fsms);
            HookVehicleHorns(veh);

            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;
                int instId = fsm.GetInstanceID();
                if (hookedFsmInstanceIds.Contains(instId)) continue;

                // Не перехватываем триггеры посадки/дверей/радио/кузова
                string goName = fsm.gameObject.name;
                if (goName == "DriveTrigger" || goName == "PassengerTrigger" || goName.StartsWith("OSC_PassengerSeat"))
                {
                    continue;
                }
                if (IsRadioRelatedFsm(fsm))
                {
                    continue;
                }

                fsm.InitializeSafe();

                if (fsm.FsmName == "Change" && goName == "Transmission")
                {
                    HookGifuRange(veh, fsm);
                }
                else if (fsm.FsmName == "Pos" && goName == "Pivot" && fsm.transform.parent != null && fsm.transform.parent.name == "GearShifter")
                {
                    HookFerndaleShifter(veh, fsm);
                }
                else if (fsm.FsmName == "Knob" && fsm.HasState("Increase"))
                {
                    HookDashboardKnob(veh, fsm);
                }
                else if (goName == "TurnSignals" && fsm.FsmName == "Usage")
                {
                    HookTurnSignals(veh, fsm);
                }
                else if (fsm.FsmName == "Use")
                {
                    if (goName == "Range" && veh.Slot == VehicleSlot.Kekmet)
                    {
                        HookKekmetRange(veh, fsm);
                    }
                    else if (goName == "InteriorLight" || (fsm.transform.parent != null && fsm.transform.parent.name == "InteriorLight"))
                    {
                        HookInteriorLight(veh, fsm);
                    }
                    else if (goName.StartsWith("FrontHyd") && fsm.transform.parent != null && fsm.transform.parent.name == "NewHydraulics")
                    {
                        HookKekmetFrontLoader(veh, fsm);
                    }
                    else if ((fsm.HasState("INCREASE") && fsm.HasState("DECREASE")) ||
                             (fsm.HasState("INCREASE 2") && fsm.HasState("DECREASE 2")))
                    {
                        HookDashboardLever(veh, fsm);
                    }
                    else if ((fsm.HasState("Test") || fsm.HasState("Test 2")) && goName != "Ignition")
                    {
                        HookDashboardButton(veh, fsm);
                    }
                    else if (IsFallbackControlName(goName))
                    {
                        HookGenericSwitch(veh, fsm);
                    }
                }
                else if (fsm.FsmName == "Switch" && IsFallbackControlName(goName))
                {
                    HookGenericSwitch(veh, fsm);
                }
            }
        }

        public void OnUpdate()
        {
            if (!IsNetworkRunning) return;

            if (levers.Count > 0 && !Input.GetMouseButton(0) && !Input.GetMouseButton(1))
            {
                int myId = LocalPlayerId;
                foreach (var kv in levers)
                {
                    DashboardLeverEntry lever = kv.Value;
                    if (lever != null && lever.Owner == myId)
                    {
                        lever.Owner = -1;
                        float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                        SendLeverPacket(lever.KeyHash, 0, val);
                    }
                }
            }
        }

        public void OnClientDisconnected(int connectionId)
        {
            foreach (var kv in levers)
            {
                DashboardLeverEntry lever = kv.Value;
                if (lever != null && lever.Owner >= 0 && (lever.Owner == connectionId || !ctx.Transport.IsServer))
                {
                    lever.Owner = -1;
                }
            }
            foreach (var kv in kekmetFrontLoaders)
            {
                if (kv.Value != null && kv.Value.Owner >= 0)
                {
                    kv.Value.Owner = -1;
                }
            }
        }

        public void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            foreach (var kv in ignitions)
            {
                var ign = kv.Value;
                if (ign == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.Ignition);
                    p.WriteInt32(ign.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(ign.Stage);
                    p.WriteBoolean(true);
                    p.WriteSingle(ign.PlugHeat != null ? ign.PlugHeat.Value : 0f);
                    p.WriteSingle(ign.EngineTemp != null ? ign.EngineTemp.Value : 0f);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in buttons)
            {
                var btn = kv.Value;
                if (btn == null || btn.CurrentAction == 255) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.DashboardButton);
                    p.WriteInt32(btn.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte((byte)btn.CurrentAction);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in levers)
            {
                var lever = kv.Value;
                if (lever == null || lever.KnobPos == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.DashboardLever);
                    p.WriteInt32(lever.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(0);
                    p.WriteSingle(lever.KnobPos.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in knobs)
            {
                var knob = kv.Value;
                if (knob == null || knob.TargetValue == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.DashboardKnob);
                    p.WriteInt32(knob.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteSingle(knob.TargetValue.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in turnSignals)
            {
                var ts = kv.Value;
                if (ts == null || ts.LastSentIndex < 0) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.TurnSignals);
                    p.WriteInt32(ts.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte((byte)ts.LastSentIndex);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in interiorLights)
            {
                var light = kv.Value;
                if (light == null || light.LightOn == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.InteriorLight);
                    p.WriteInt32(light.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(light.LightOn.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in gifuRanges)
            {
                var gr = kv.Value;
                if (gr == null || gr.LowGears == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.GifuRange);
                    p.WriteInt32(gr.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(gr.LowGears.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in kekmetRanges)
            {
                var kr = kv.Value;
                if (kr == null || kr.Range == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.KekmetRange);
                    p.WriteInt32(kr.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(kr.Range.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in genericSwitches)
            {
                var sw = kv.Value;
                if (sw == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.GenericSwitch);
                    p.WriteInt32(sw.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(sw.CurrentState);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in ferndaleShifters)
            {
                var fs = kv.Value;
                if (fs == null) continue;
                string ltr = (fs.Letter != null && !string.IsNullOrEmpty(fs.Letter.Value))
                    ? fs.Letter.Value.Substring(0, 1)
                    : (string.IsNullOrEmpty(fs.LastLetter) ? "P" : fs.LastLetter);
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.FerndaleShifter);
                    p.WriteInt32(fs.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(fs.LastShiftUp);
                    p.WriteString(ltr);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in kekmetFrontLoaders)
            {
                var fl = kv.Value;
                if (fl == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.KekmetFrontLoader);
                    p.WriteInt32(fl.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte((byte)fl.CurrentAction);
                    p.WriteSingle(fl.ArmRot != null ? fl.ArmRot.Value : 0f);
                    ctx.Transport.SendTo(p, clientId);
                }
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 9) return;

            var subType = (SystemToggleSubType)reader.ReadByte();
            int keyHash = reader.ReadInt32();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case SystemToggleSubType.Ignition:
                {
                    byte stage = reader.ReadByte();
                    bool hasTemps = reader.UnreadBytes >= 9 && reader.ReadBoolean();
                    float plugHeat = hasTemps ? reader.ReadSingle() : 0f;
                    float engineTemp = hasTemps ? reader.ReadSingle() : 0f;
                    if (ignitions.TryGetValue(keyHash, out var ign) && CanAcceptFromSender(ign.Vehicle, senderId))
                    {
                        if (hasTemps)
                        {
                            if (ign.PlugHeat != null) ign.PlugHeat.Value = plugHeat;
                            if (ign.EngineTemp != null) ign.EngineTemp.Value = engineTemp;
                        }
                        ApplyIgnitionStage(ign, stage);
                    }
                    break;
                }
                case SystemToggleSubType.DashboardButton:
                {
                    byte index = reader.ReadByte();
                    if (buttons.TryGetValue(keyHash, out var btn) && CanAcceptFromSender(btn.Vehicle, senderId))
                    {
                        if (btn.Fsm != null && btn.ActionEvents != null && index < btn.ActionEvents.Length)
                        {
                            btn.UpdatingAction = index;
                            btn.CurrentAction = index;
                            btn.Fsm.SendEvent(btn.ActionEvents[index]);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.DashboardLever:
                {
                    byte mode = reader.ReadByte();
                    float knobVal = reader.ReadSingle();
                    if (levers.TryGetValue(keyHash, out var lever) && CanAcceptFromSender(lever.Vehicle, senderId))
                    {
                        ApplyRemoteLever(lever, mode, knobVal, senderId);
                    }
                    break;
                }
                case SystemToggleSubType.DashboardKnob:
                {
                    float val = reader.ReadSingle();
                    if (knobs.TryGetValue(keyHash, out var knob) && CanAcceptFromSender(knob.Vehicle, senderId))
                    {
                        if (knob.Fsm != null && knob.TargetValue != null && knob.UpdateEvent != null)
                        {
                            knob.Updating = true;
                            knob.TargetValue.Value = val - knob.AddStep;
                            knob.Fsm.Fsm.Event(knob.UpdateEvent);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.TurnSignals:
                {
                    byte idx = reader.ReadByte();
                    if (turnSignals.TryGetValue(keyHash, out var ts) && CanAcceptFromSender(ts.Vehicle, senderId))
                    {
                        if (ts.Fsm != null && ts.EventNames != null && idx < ts.EventNames.Length)
                        {
                            ts.UpdatingIndex = idx;
                            ts.LastSentIndex = idx;
                            ts.Fsm.SendEvent(ts.EventNames[idx]);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.InteriorLight:
                {
                    bool on = reader.ReadBoolean();
                    if (interiorLights.TryGetValue(keyHash, out var light) && light.Fsm != null)
                    {
                        light.Updating = true;
                        light.Fsm.SendEvent(on ? "DOOROPEN" : "DOORCLOSE");
                    }
                    break;
                }
                case SystemToggleSubType.Horn:
                {
                    bool active = reader.ReadBoolean();
                    if (horns.TryGetValue(keyHash, out var horn) && CanAcceptFromSender(horn.Vehicle, senderId))
                    {
                        if (horn.Fsm != null)
                        {
                            horn.RemoteActive = active;
                            horn.SuppressNext = true;
                            horn.Fsm.SendEvent(active ? "MP_HORN_ON" : "MP_HORN_OFF");
                        }
                    }
                    break;
                }
                case SystemToggleSubType.GenericSwitch:
                {
                    byte state = reader.ReadByte();
                    if (genericSwitches.TryGetValue(keyHash, out var sw) && CanAcceptFromSender(sw.Vehicle, senderId))
                    {
                        if (sw.Fsm != null && sw.CurrentState != state)
                        {
                            sw.SuppressNext = true;
                            sw.CurrentState = state;
                            if (sw.IsTwoState)
                            {
                                sw.Fsm.SendEvent(state != 0 ? "MP_ON" : "MP_OFF");
                            }
                            else
                            {
                                sw.Fsm.SendEvent("MP_TOGGLE");
                            }
                        }
                    }
                    break;
                }
                case SystemToggleSubType.GifuRange:
                {
                    bool lowGears = reader.ReadBoolean();
                    if (gifuRanges.TryGetValue(keyHash, out var gr) && CanAcceptFromSender(gr.Vehicle, senderId))
                    {
                        if (gr.Fsm != null && gr.LowGears != null && gr.ToggleEvent != null)
                        {
                            gr.Updating = true;
                            gr.LowGears.Value = lowGears;
                            gr.Fsm.Fsm.Event(gr.ToggleEvent);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.FerndaleShifter:
                {
                    bool shiftUp = reader.ReadBoolean();
                    string letter = reader.ReadString();
                    if (ferndaleShifters.TryGetValue(keyHash, out var fs) && CanAcceptFromSender(fs.Vehicle, senderId))
                    {
                        if (fs.Fsm != null)
                        {
                            fs.Updating = true;
                            if (fs.Letter != null) fs.Letter.Value = letter;
                            fs.LastLetter = letter;
                            fs.LastShiftUp = shiftUp;
                            FsmEvent ev = shiftUp ? fs.ShiftUpEvent : fs.ShiftDownEvent;
                            if (ev != null) fs.Fsm.Fsm.Event(ev);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.KekmetRange:
                {
                    bool rangeVal = reader.ReadBoolean();
                    if (kekmetRanges.TryGetValue(keyHash, out var kr) && CanAcceptFromSender(kr.Vehicle, senderId))
                    {
                        if (kr.Fsm != null && kr.Range != null && kr.ToggleEvent != null)
                        {
                            kr.Updating = true;
                            kr.Range.Value = !rangeVal; // BoolFlip inside "Flip" flips to rangeVal
                            kr.Fsm.Fsm.Event(kr.ToggleEvent);
                        }
                    }
                    break;
                }
                case SystemToggleSubType.KekmetFrontLoader:
                {
                    byte mode = reader.ReadByte();
                    float armRot = reader.ReadSingle();
                    if (kekmetFrontLoaders.TryGetValue(keyHash, out var fl) && CanAcceptFromSender(fl.Vehicle, senderId))
                    {
                        if (fl.Fsm != null && fl.ActionEvents != null && mode < fl.ActionEvents.Length)
                        {
                            fl.UpdatingAction = mode;
                            fl.CurrentAction = mode;
                            fl.Owner = (mode != 2) ? senderId : -1;
                            if (mode == 2 && fl.ArmRot != null)
                            {
                                fl.ArmRot.Value = armRot;
                            }
                            fl.Fsm.SendEvent(fl.ActionEvents[mode]);
                        }
                    }
                    break;
                }
            }
        }

        private bool CanAcceptFromSender(VehicleCatalogEntry veh, int senderId)
        {
            if (veh == null) return true;
            return veh.DriverPlayerId < 0 || veh.DriverPlayerId == senderId;
        }

        private bool CanSendLocal(VehicleCatalogEntry veh)
        {
            if (veh == null) return IsNetworkRunning;
            if (!IsNetworkRunning) return false;
            return veh.DriverPlayerId < 0 || veh.DriverPlayerId == LocalPlayerId;
        }

        private static bool IsRadioRelatedFsm(PlayMakerFSM fsm)
        {
            if (fsm == null) return false;
            if (fsm.FsmName == "Radio" || fsm.FsmName == "CD") return true;

            Transform t = fsm.transform;
            while (t != null)
            {
                string n = t.name;
                if (ControlsHelpers.ContainsOrdinalIgnoreCase(n, "radio") ||
                    ControlsHelpers.ContainsOrdinalIgnoreCase(n, "cd player") ||
                    ControlsHelpers.ContainsOrdinalIgnoreCase(n, "cdplayer"))
                {
                    return true;
                }
                if (t.parent == null || t.name.IndexOf("SATSUMA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("HAYOSIKO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("FERNDALE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("GIFU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("KEKMET", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.name.IndexOf("RUSCKO", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    break;
                }
                t = t.parent;
            }
            return false;
        }

        private static bool IsFallbackControlName(string goName)
        {
            if (string.IsNullOrEmpty(goName)) return false;
            return ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Choke") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Hazard") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Wiper") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Blinker") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Light") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Hydraulic") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "Tip trailer") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "PTO") ||
                   ControlsHelpers.ContainsOrdinalIgnoreCase(goName, "LiftAxle");
        }

        private void HookVehicleIgnitionAndStarter(VehicleCatalogEntry veh, PlayMakerFSM[] fsms)
        {
            if (veh.Slot == VehicleSlot.Jonnez) return;

            PlayMakerFSM ignitionFsm = null;
            PlayMakerFSM starterFsm = null;
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM f = fsms[i];
                if (f == null) continue;
                if (f.FsmName == "Starter" && starterFsm == null)
                {
                    starterFsm = f;
                }
                else if (ignitionFsm == null &&
                    ((f.FsmName == "Use" && f.transform.name == "Ignition") ||
                     (f.FsmName == "UseNew" && f.transform.name == "IgnitionSatsuma")))
                {
                    ignitionFsm = f;
                }
            }

            if (ignitionFsm == null) return;
            int instId = ignitionFsm.GetInstanceID();
            if (hookedFsmInstanceIds.Contains(instId)) return;

            if (!ctx.Ownership.TryClaim(ignitionFsm, moduleId, $"Ignition_{veh.DisplayName}")) return;
            if (starterFsm != null && !ctx.Ownership.TryClaim(starterFsm, moduleId, $"Starter_{veh.DisplayName}"))
            {
                starterFsm = null;
            }

            hookedFsmInstanceIds.Add(instId);
            if (starterFsm != null) hookedFsmInstanceIds.Add(starterFsm.GetInstanceID());

            ignitionFsm.InitializeSafe();
            int keyHash = ControlsHelpers.GetControlKeyHash(ignitionFsm);

            var ign = new IgnitionEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                IgnitionFsm = ignitionFsm,
                Acc = ignitionFsm.FsmVariables.FindFsmBool("ACC"),
                MotorOn = ignitionFsm.FsmVariables.FindFsmBool("MotorOn"),
                StarterFsm = starterFsm
            };

            if (starterFsm != null)
            {
                starterFsm.InitializeSafe();
                ign.StarterAcc = starterFsm.FsmVariables.FindFsmBool("ACC");
                ign.StarterShutOff = starterFsm.FsmVariables.FindFsmBool("ShutOff");
                ign.StarterStarting = starterFsm.FsmVariables.FindFsmBool("Starting");
                ign.PlugHeat = starterFsm.FsmVariables.FindFsmFloat("PlugHeat");
                ign.EngineTemp = starterFsm.FsmVariables.FindFsmFloat("EngineTemp");

                if (ign.EngineTemp == null)
                {
                    Transform coolTr = veh.RootObject.transform.Find("LOD/Simulation/Cooling") ??
                                       veh.RootObject.transform.Find("Simulation/Cooling");
                    if (coolTr != null)
                    {
                        PlayMakerFSM coolFsm = ControlsHelpers.FindFsmSafe(coolTr.gameObject, "Cooling");
                        if (coolFsm != null)
                        {
                            coolFsm.InitializeSafe();
                            ign.EngineTemp = coolFsm.FsmVariables.FindFsmFloat("EngineTemp");
                        }
                    }
                }

                string starterState = starterFsm.HasState("Turn key")
                    ? "Turn key"
                    : (starterFsm.HasState("Starting engine") ? "Starting engine" : null);

                if (starterState != null)
                {
                    ign.StarterTrigger = starterFsm.AddSafeEvent("MP_START");
                    starterFsm.AddSafeGlobalTransition(ign.StarterTrigger, starterState);

                    FsmState stStart = starterFsm.GetState(starterState);
                    if (stStart != null && stStart.Actions != null)
                    {
                        var actions = stStart.Actions;
                        for (int a = 0; a < actions.Length; a++)
                        {
                            if (actions[a] is GetMouseButtonUp lmbup)
                            {
                                actions[a] = new EveryFrameFsmAction(() =>
                                {
                                    if (!ign.StarterStartingMp && Input.GetMouseButtonUp((int)lmbup.button))
                                    {
                                        if (lmbup.sendEvent != null) starterFsm.Fsm.Event(lmbup.sendEvent);
                                    }
                                });
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
                        ign.StarterStartingMp = false;
                    }, 0, true);
                }

                if (starterFsm.HasState("Start engine"))
                {
                    starterFsm.InsertAction("Start engine", () =>
                    {
                        ign.Starting = false;
                        ign.UpdatingMp = true;
                    }, 0, true);
                }
                else if (starterFsm.HasState("Motor running"))
                {
                    starterFsm.InsertAction("Motor running", () =>
                    {
                        ign.Starting = false;
                        ign.UpdatingMp = true;
                    }, 0, true);
                }
            }

            ign.UpdateEvent = ignitionFsm.AddSafeEvent("MP_UPDATE");
            string updateTarget = ignitionFsm.HasState("Sound")
                ? "Sound"
                : (ignitionFsm.HasState("Test")
                    ? "Test"
                    : (ignitionFsm.Fsm != null ? ignitionFsm.Fsm.StartState : string.Empty));
            if (!string.IsNullOrEmpty(updateTarget))
            {
                ignitionFsm.AddSafeGlobalTransition(ign.UpdateEvent, updateTarget);
            }

            if (ignitionFsm.HasState("Motor starting"))
            {
                ignitionFsm.InsertAction("Motor starting", () =>
                {
                    if (!ign.Starting)
                    {
                        ign.Stage = 2;
                        SendIgnitionPacket(ign);
                    }
                }, 0, true);

                FsmState stMotorStarting = ignitionFsm.GetState("Motor starting");
                if (stMotorStarting != null && stMotorStarting.Actions != null)
                {
                    var actions = stMotorStarting.Actions;
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is GetMouseButtonUp lmbup)
                        {
                            actions[a] = new EveryFrameFsmAction(() =>
                            {
                                if ((!ign.Starting && Input.GetMouseButtonUp((int)lmbup.button)) || (!ign.Starting && ign.UpdatingMp))
                                {
                                    if (lmbup.sendEvent != null) ignitionFsm.Fsm.Event(lmbup.sendEvent);
                                    ign.UpdatingMp = false;
                                }
                            });
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
                    if (!ign.UpdatingMp)
                    {
                        ign.Stage = 0;
                        SendIgnitionPacket(ign);
                    }
                    ign.UpdatingMp = false;
                }, 0, true);
            }

            if (ignitionFsm.HasState("ACC on"))
            {
                FsmState stAccOn = ignitionFsm.GetState("ACC on");
                if (stAccOn != null && stAccOn.Actions != null)
                {
                    var actions = stAccOn.Actions;
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is GetMouseButtonUp lmbup)
                        {
                            actions[a] = new EveryFrameFsmAction(() =>
                            {
                                if (Input.GetMouseButtonUp((int)lmbup.button) || ign.UpdatingMp)
                                {
                                    if (lmbup.sendEvent != null) ignitionFsm.Fsm.Event(lmbup.sendEvent);
                                    if (!ign.UpdatingMp)
                                    {
                                        ign.Stage = 1;
                                        SendIgnitionPacket(ign);
                                    }
                                    ign.UpdatingMp = false;
                                }
                            });
                            break;
                        }
                    }
                    stAccOn.Actions = actions;
                }
            }

            if (ignitionFsm.HasState("ACC on 2"))
            {
                FsmEvent startEv = null;
                if (ignitionFsm.FsmEvents != null)
                {
                    for (int e = 0; e < ignitionFsm.FsmEvents.Length; e++)
                    {
                        if (ignitionFsm.FsmEvents[e] != null && ignitionFsm.FsmEvents[e].Name == "START")
                        {
                            startEv = ignitionFsm.FsmEvents[e];
                            break;
                        }
                    }
                }

                FsmState stAccOn2 = ignitionFsm.GetState("ACC on 2");
                if (stAccOn2 != null && stAccOn2.Actions != null)
                {
                    var actions = stAccOn2.Actions;
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is GetMouseButtonUp lmbup)
                        {
                            actions[a] = new EveryFrameFsmAction(() =>
                            {
                                if (ign.Starting)
                                {
                                    if (startEv != null) ignitionFsm.Fsm.Event(startEv);
                                    ign.UpdatingMp = false;
                                }
                                else if (Input.GetMouseButtonUp((int)lmbup.button) || ign.UpdatingMp)
                                {
                                    if (lmbup.sendEvent != null) ignitionFsm.Fsm.Event(lmbup.sendEvent);
                                }
                            });
                            break;
                        }
                    }
                    stAccOn2.Actions = actions;
                }
            }

            ignitions[keyHash] = ign;
        }

        private void ApplyIgnitionStage(IgnitionEntry ign, byte stage)
        {
            if (ign == null) return;
            ign.Stage = stage;

            switch (stage)
            {
                case 0:
                    if (ign.Acc != null) ign.Acc.Value = true;
                    SetStarterState(ign, false, false);
                    break;
                case 1:
                    if (ign.Acc != null) ign.Acc.Value = false;
                    SetStarterState(ign, false, true);
                    break;
                case 2:
                    if (ign.Acc != null) ign.Acc.Value = true;
                    SetStarterState(ign, true, true);
                    break;
            }

            ign.UpdatingMp = true;
            ign.Starting = stage == 2;
            if (ign.MotorOn != null) ign.MotorOn.Value = false;
            if (ign.IgnitionFsm != null && ign.UpdateEvent != null)
            {
                ign.IgnitionFsm.Fsm.Event(ign.UpdateEvent);
            }
        }

        private static void SetStarterState(IgnitionEntry ign, bool startVal, bool accVal)
        {
            if (ign.StarterFsm == null) return;
            if (startVal && ign.StarterTrigger != null)
            {
                ign.StarterStartingMp = true;
                ign.StarterFsm.Fsm.Event(ign.StarterTrigger);
            }
            if (ign.StarterStarting != null) ign.StarterStarting.Value = startVal;
            if (ign.StarterAcc != null) ign.StarterAcc.Value = accVal;
            if (ign.StarterShutOff != null) ign.StarterShutOff.Value = !startVal;
        }

        private void SendIgnitionPacket(IgnitionEntry ign)
        {
            if (ign == null || !CanSendLocal(ign.Vehicle)) return;

            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.Ignition);
                p.WriteInt32(ign.KeyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(ign.Stage);
                p.WriteBoolean(false);
                ctx.Transport.Send(p);
            }
        }

        private void HookDashboardButton(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
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

            if (testState == null || testState.Transitions == null || testState.Transitions.Length == 0) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"Button_{veh.DisplayName}_{fsm.gameObject.name}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var btn = new DashboardButtonEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                ActionEvents = new string[testState.Transitions.Length]
            };

            for (int i = 0; i < testState.Transitions.Length; i++)
            {
                FsmTransition trans = testState.Transitions[i];
                if (trans == null) continue;
                string evName = "MP_" + trans.EventName;
                btn.ActionEvents[i] = evName;
                fsm.AddSafeGlobalTransition(evName, trans.ToState);

                int idx = i;
                fsm.InsertAction(trans.ToState, () =>
                {
                    if (btn.UpdatingAction == idx)
                    {
                        btn.UpdatingAction = -1;
                        return;
                    }
                    if (!CanSendLocal(btn.Vehicle)) return;
                    btn.CurrentAction = idx;
                    using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                    {
                        p.WriteByte((byte)SystemToggleSubType.DashboardButton);
                        p.WriteInt32(btn.KeyHash);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteByte((byte)idx);
                        ctx.Transport.Send(p);
                    }
                }, 0, true);
            }

            buttons[keyHash] = btn;
        }

        private void HookDashboardLever(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            string incName = fsm.HasState("INCREASE") ? "INCREASE" : (fsm.HasState("INCREASE 2") ? "INCREASE 2" : null);
            string decName = fsm.HasState("DECREASE") ? "DECREASE" : (fsm.HasState("DECREASE 2") ? "DECREASE 2" : null);
            if (incName == null || decName == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"Lever_{veh.DisplayName}_{fsm.gameObject.name}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var lever = new DashboardLeverEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm
            };

            lever.IncreaseEvent = fsm.AddSafeEvent("MP_INCREASE");
            lever.DecreaseEvent = fsm.AddSafeEvent("MP_DECREASE");
            fsm.AddSafeGlobalTransition(lever.IncreaseEvent, incName);
            fsm.AddSafeGlobalTransition(lever.DecreaseEvent, decName);

            fsm.InsertAction(incName, () =>
            {
                if (!lever.StopMoving && CanSendLocal(lever.Vehicle))
                {
                    int myId = LocalPlayerId;
                    if (lever.Owner == -1 || lever.Owner == myId)
                    {
                        lever.Owner = myId;
                        float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                        SendLeverPacket(lever.KeyHash, 1, val);
                    }
                }
            }, 0, true);

            fsm.InsertAction(decName, () =>
            {
                if (!lever.StopMoving && CanSendLocal(lever.Vehicle))
                {
                    int myId = LocalPlayerId;
                    if (lever.Owner == -1 || lever.Owner == myId)
                    {
                        lever.Owner = myId;
                        float val = lever.KnobPos != null ? lever.KnobPos.Value : 0f;
                        SendLeverPacket(lever.KeyHash, 2, val);
                    }
                }
            }, 0, true);

            for (int s = 0; s < 2; s++)
            {
                string stateName = s == 0 ? incName : decName;
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
                    newActions.Add(new EveryFrameFsmAction(() =>
                    {
                        int myId = LocalPlayerId;
                        if (lever.StopMoving)
                        {
                            lever.StopMoving = false;
                            if (mbuEvent != null) fsm.Fsm.Event(mbuEvent);
                            else if (lever.StopFallbackEvent != null) fsm.Fsm.Event(lever.StopFallbackEvent);
                            else fsm.SendEvent("FINISHED");
                        }
                        else if (lever.Owner == -1 || lever.Owner == myId)
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
                    }));

                    state.Actions = newActions.ToArray();
                }
            }

            levers[keyHash] = lever;
        }

        private void ApplyRemoteLever(DashboardLeverEntry lever, byte mode, float knobVal, int senderId)
        {
            if (lever == null || lever.Fsm == null) return;
            if (mode == 1 || mode == 2)
            {
                lever.Owner = senderId;
                FsmEvent ev = mode == 1 ? lever.IncreaseEvent : lever.DecreaseEvent;
                if (ev != null) lever.Fsm.Fsm.Event(ev);
            }
            else
            {
                lever.Owner = -1;
                if (lever.KnobPos != null) lever.KnobPos.Value = knobVal;
                lever.StopMoving = true;
                if (lever.IncreaseEvent != null) lever.Fsm.Fsm.Event(lever.IncreaseEvent);
            }
        }

        private void SendLeverPacket(int keyHash, byte mode, float knobPos)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.DashboardLever);
                p.WriteInt32(keyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(mode);
                p.WriteSingle(knobPos);
                ctx.Transport.Send(p);
            }
        }

        private void HookDashboardKnob(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            FsmState incState = fsm.GetState("Increase");
            if (incState == null || incState.Transitions == null || incState.Transitions.Length == 0) return;

            FsmFloat targetVal = null;
            float addStep = 0f;
            if (incState.Actions != null)
            {
                for (int i = 0; i < incState.Actions.Length; i++)
                {
                    if (incState.Actions[i] is FloatAdd fa)
                    {
                        targetVal = fa.floatVariable;
                        addStep = fa.add.Value;
                        break;
                    }
                }
            }

            if (targetVal == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"Knob_{veh.DisplayName}_{fsm.gameObject.name}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var knob = new DashboardKnobEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                TargetValue = targetVal,
                AddStep = addStep,
                UpdateEvent = fsm.AddSafeEvent("MP_UPDATE")
            };

            fsm.AddSafeGlobalTransition(knob.UpdateEvent, "Increase");
            string toState = incState.Transitions[0].ToState;
            fsm.InsertAction(toState, () =>
            {
                if (knob.Updating)
                {
                    knob.Updating = false;
                    return;
                }
                if (!CanSendLocal(knob.Vehicle)) return;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.DashboardKnob);
                    p.WriteInt32(knob.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteSingle(knob.TargetValue.Value);
                    ctx.Transport.Send(p);
                }
            }, 0, true);

            knobs[keyHash] = knob;
        }

        private void HookTurnSignals(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            FsmState state2 = fsm.GetState("State 2");
            if (state2 == null || state2.Transitions == null) return;

            string leftState = null;
            string rightState = null;
            string offState = fsm.HasState("State 3") ? "State 3" : null;

            for (int i = 0; i < state2.Transitions.Length; i++)
            {
                FsmTransition tr = state2.Transitions[i];
                if (tr == null) continue;
                if (tr.EventName == "LEFT") leftState = tr.ToState;
                else if (tr.EventName == "RIGHT") rightState = tr.ToState;
                else if (offState == null && tr.EventName == "OFF") offState = tr.ToState;
            }

            if (offState == null || leftState == null || rightState == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"TurnSignals_{veh.DisplayName}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var ts = new TurnSignalEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                EventNames = new[] { "MP_OFF", "MP_LEFT", "MP_RIGHT" },
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
                if (ts.LastSentIndex != 0 && CanSendLocal(ts.Vehicle))
                {
                    ts.LastSentIndex = 0;
                    SendTurnSignalPacket(ts.KeyHash, 0);
                }
            }, 0, true);

            fsm.AddSafeGlobalTransition(ts.EventNames[1], leftState);
            fsm.InsertAction(leftState, () =>
            {
                if (ts.UpdatingIndex == 1)
                {
                    ts.UpdatingIndex = -1;
                    return;
                }
                if (CanSendLocal(ts.Vehicle))
                {
                    ts.LastSentIndex = 1;
                    SendTurnSignalPacket(ts.KeyHash, 1);
                }
            }, 0, true);

            fsm.AddSafeGlobalTransition(ts.EventNames[2], rightState);
            fsm.InsertAction(rightState, () =>
            {
                if (ts.UpdatingIndex == 2)
                {
                    ts.UpdatingIndex = -1;
                    return;
                }
                if (CanSendLocal(ts.Vehicle))
                {
                    ts.LastSentIndex = 2;
                    SendTurnSignalPacket(ts.KeyHash, 2);
                }
            }, 0, true);

            turnSignals[keyHash] = ts;
        }

        private void SendTurnSignalPacket(int keyHash, byte idx)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.TurnSignals);
                p.WriteInt32(keyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(idx);
                ctx.Transport.Send(p);
            }
        }

        private void HookInteriorLight(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"InteriorLight_{veh.DisplayName}_{fsm.gameObject.name}")) return;
            hookedFsmInstanceIds.Add(fsm.GetInstanceID());

            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var light = new InteriorLightEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                LightOn = fsm.FsmVariables.FindFsmBool("LightON")
            };

            bool hookedOpen = false;
            bool hookedClose = false;
            if (fsm.FsmGlobalTransitions != null)
            {
                for (int i = 0; i < fsm.FsmGlobalTransitions.Length; i++)
                {
                    FsmTransition gt = fsm.FsmGlobalTransitions[i];
                    if (gt == null || gt.FsmEvent == null) continue;
                    if (gt.FsmEvent.Name == "DOOROPEN" && !hookedOpen)
                    {
                        hookedOpen = true;
                        fsm.InsertAction(gt.ToState, () => OnInteriorLightState(light, true), 0, true);
                    }
                    else if (gt.FsmEvent.Name == "DOORCLOSE" && !hookedClose)
                    {
                        hookedClose = true;
                        fsm.InsertAction(gt.ToState, () => OnInteriorLightState(light, false), 0, true);
                    }
                }
            }

            if (!hookedOpen)
            {
                string onState = fsm.HasState("State 2") ? "State 2" : (fsm.HasState("Light") ? "Light" : (fsm.HasState("State 1") ? "State 1" : null));
                if (onState != null)
                {
                    fsm.AddSafeGlobalTransition("DOOROPEN", onState);
                    fsm.InsertAction(onState, () => OnInteriorLightState(light, true), 0, true);
                }
            }

            if (!hookedClose)
            {
                string offState = fsm.HasState("State 3") ? "State 3" : (fsm.HasState("OFF") ? "OFF" : null);
                if (offState != null)
                {
                    fsm.AddSafeGlobalTransition("DOORCLOSE", offState);
                    fsm.InsertAction(offState, () => OnInteriorLightState(light, false), 0, true);
                }
            }

            interiorLights[keyHash] = light;
        }

        private void OnInteriorLightState(InteriorLightEntry light, bool on)
        {
            if (light.Updating)
            {
                light.Updating = false;
                return;
            }
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.InteriorLight);
                p.WriteInt32(light.KeyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(on);
                ctx.Transport.Send(p);
            }
        }

        private void HookVehicleHorns(VehicleCatalogEntry veh)
        {
            PlayMakerFSM[] fsms = veh.RootObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.FsmName != "Use") continue;
                string name = fsm.gameObject.name;
                if (name != "ButtonHorn" && name != "CarHorn" && name != "Horn" &&
                    !ControlsHelpers.ContainsOrdinalIgnoreCase(name, "Horn"))
                {
                    continue;
                }
                if (hookedFsmInstanceIds.Contains(fsm.GetInstanceID())) continue;

                fsm.InitializeSafe();
                bool hasTest = false;
                if (fsm.FsmStates != null)
                {
                    for (int s = 0; s < fsm.FsmStates.Length; s++)
                    {
                        if (fsm.FsmStates[s] != null && fsm.FsmStates[s].Name.Contains("Test"))
                        {
                            hasTest = true;
                            break;
                        }
                    }
                }

                if (hasTest)
                {
                    HookDashboardButton(veh, fsm);
                    continue;
                }

                string onState = null;
                string offState = null;
                if (fsm.FsmStates != null)
                {
                    for (int s = 0; s < fsm.FsmStates.Length; s++)
                    {
                        FsmState st = fsm.FsmStates[s];
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
                                        if (st.Transitions[t] != null && st.Transitions[t].EventName == mbu.sendEvent.Name)
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
                    onState = fsm.HasState("Horn") ? "Horn" : (fsm.HasState("Sound") ? "Sound" : (fsm.HasState("ON") ? "ON" : (fsm.HasState("State 1") ? "State 1" : null)));
                }
                if (offState == null)
                {
                    offState = fsm.HasState("OFF") ? "OFF" : (fsm.HasState("State 2") ? "State 2" : (fsm.HasState("Wait") ? "Wait" : (fsm.Fsm != null ? fsm.Fsm.StartState : null)));
                }
                if (onState == null || offState == null || onState == offState) continue;
                if (!ctx.Ownership.TryClaim(fsm, moduleId, $"Horn_{veh.DisplayName}_{name}")) continue;

                hookedFsmInstanceIds.Add(fsm.GetInstanceID());
                int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
                var horn = new HornEntry
                {
                    KeyHash = keyHash,
                    Vehicle = veh,
                    Fsm = fsm
                };

                FsmState activeState = fsm.GetState(onState);
                if (activeState != null && activeState.Actions != null)
                {
                    var actions = activeState.Actions;
                    for (int a = 0; a < actions.Length; a++)
                    {
                        if (actions[a] is GetMouseButtonUp lmbup)
                        {
                            actions[a] = new EveryFrameFsmAction(() =>
                            {
                                if (!horn.RemoteActive && Input.GetMouseButtonUp((int)lmbup.button))
                                {
                                    if (lmbup.sendEvent != null) fsm.Fsm.Event(lmbup.sendEvent);
                                }
                            });
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
                    if (!CanSendLocal(horn.Vehicle)) return;
                    horn.LocalActive = true;
                    SendHornPacket(horn.KeyHash, true);
                }, 0, true);

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
                        if (IsNetworkRunning) SendHornPacket(horn.KeyHash, false);
                    }
                }, 0, true);

                horns[keyHash] = horn;
            }
        }

        private void SendHornPacket(int keyHash, bool active)
        {
            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.Horn);
                p.WriteInt32(keyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(active);
                ctx.Transport.Send(p);
            }
        }

        private void HookGenericSwitch(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            string onState = null;
            string offState = null;

            if (fsm.HasState("ON") && (fsm.HasState("OFF") || fsm.HasState("Off")))
            {
                onState = "ON";
                offState = fsm.HasState("OFF") ? "OFF" : "Off";
            }
            else if (fsm.HasState("On") && (fsm.HasState("Off") || fsm.HasState("OFF")))
            {
                onState = "On";
                offState = fsm.HasState("Off") ? "Off" : "OFF";
            }
            else if (fsm.HasState("Switch") || fsm.HasState("Toggle"))
            {
                onState = fsm.HasState("Switch") ? "Switch" : "Toggle";
                offState = fsm.HasState("OFF") ? "OFF" : (fsm.HasState("Off") ? "Off" : null);
            }
            else if (fsm.HasState("State 1"))
            {
                if (fsm.HasState("State 2"))
                {
                    if (fsm.Fsm != null && fsm.Fsm.StartState == "State 1")
                    {
                        offState = "State 1";
                        onState = "State 2";
                    }
                    else
                    {
                        onState = "State 1";
                        offState = fsm.HasState("OFF") ? "OFF" : (fsm.HasState("Off") ? "Off" : "State 2");
                    }
                }
                else if (fsm.HasState("OFF") || fsm.HasState("Off"))
                {
                    onState = "State 1";
                    offState = fsm.HasState("OFF") ? "OFF" : "Off";
                }
                else
                {
                    onState = "State 1";
                }
            }

            if (onState == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"Switch_{veh.DisplayName}_{fsm.gameObject.name}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            bool isTwoState = offState != null && offState != onState;
            var sw = new GenericSwitchEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                IsTwoState = isTwoState,
                CurrentState = (isTwoState && fsm.Fsm != null && fsm.Fsm.StartState == onState) ? (byte)1 : (byte)0
            };

            if (isTwoState)
            {
                fsm.AddSafeGlobalTransition("MP_ON", onState);
                fsm.AddSafeGlobalTransition("MP_OFF", offState);

                fsm.InsertAction(onState, () =>
                {
                    if (sw.SuppressNext)
                    {
                        sw.SuppressNext = false;
                        return;
                    }
                    if (sw.CurrentState == 1) return;
                    sw.CurrentState = 1;
                    if (!CanSendLocal(sw.Vehicle)) return;
                    SendGenericSwitchPacket(sw.KeyHash, 1);
                }, 0, true);

                fsm.InsertAction(offState, () =>
                {
                    if (sw.SuppressNext)
                    {
                        sw.SuppressNext = false;
                        return;
                    }
                    if (sw.CurrentState == 0) return;
                    sw.CurrentState = 0;
                    if (!CanSendLocal(sw.Vehicle)) return;
                    SendGenericSwitchPacket(sw.KeyHash, 0);
                }, 0, true);
            }
            else
            {
                fsm.AddSafeGlobalTransition("MP_TOGGLE", onState);
                fsm.InsertAction(onState, () =>
                {
                    if (sw.SuppressNext)
                    {
                        sw.SuppressNext = false;
                        return;
                    }
                    if (!CanSendLocal(sw.Vehicle)) return;
                    sw.CurrentState = (byte)(sw.CurrentState == 0 ? 1 : 0);
                    SendGenericSwitchPacket(sw.KeyHash, sw.CurrentState);
                }, 0, true);
            }

            genericSwitches[keyHash] = sw;
        }

        private void SendGenericSwitchPacket(int keyHash, byte state)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
            {
                p.WriteByte((byte)SystemToggleSubType.GenericSwitch);
                p.WriteInt32(keyHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(state);
                ctx.Transport.Send(p);
            }
        }

        private void HookGifuRange(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            if (!fsm.HasState("State 2")) return;
            FsmBool lowGears = fsm.FsmVariables.FindFsmBool("LowGears");
            if (lowGears == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"GifuRange_{veh.DisplayName}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var entry = new GifuRangeEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                LowGears = lowGears,
                ToggleEvent = fsm.AddSafeEvent("MP_TOGGLE")
            };

            fsm.AddSafeGlobalTransition(entry.ToggleEvent, "State 2");
            fsm.InsertAction("State 2", () =>
            {
                if (entry.Updating)
                {
                    entry.Updating = false;
                    return;
                }
                if (!CanSendLocal(entry.Vehicle)) return;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.GifuRange);
                    p.WriteInt32(entry.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(entry.LowGears.Value);
                    ctx.Transport.Send(p);
                }
            }, 0, true);

            gifuRanges[keyHash] = entry;
        }

        private void HookFerndaleShifter(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            if (!fsm.HasState("State 4") || !fsm.HasState("State 3")) return;
            FsmString letter = fsm.FsmVariables.FindFsmString("Letter");
            if (letter == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"FerndaleShifter_{veh.DisplayName}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var entry = new FerndaleShifterEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                Letter = letter,
                ShiftUpEvent = fsm.AddSafeEvent("MP_SHIFTUP"),
                ShiftDownEvent = fsm.AddSafeEvent("MP_SHIFTDOWN"),
                LastLetter = string.IsNullOrEmpty(letter.Value) ? "P" : letter.Value
            };

            fsm.AddSafeGlobalTransition(entry.ShiftUpEvent, "State 4");
            fsm.AddSafeGlobalTransition(entry.ShiftDownEvent, "State 3");

            Action<bool> onShift = shiftUp =>
            {
                if (entry.Updating)
                {
                    entry.Updating = false;
                    return;
                }
                if (!CanSendLocal(entry.Vehicle)) return;
                string ltr = string.IsNullOrEmpty(entry.Letter.Value) ? "P" : entry.Letter.Value.Substring(0, 1);
                entry.LastShiftUp = shiftUp;
                entry.LastLetter = ltr;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.FerndaleShifter);
                    p.WriteInt32(entry.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(shiftUp);
                    p.WriteString(ltr);
                    ctx.Transport.Send(p);
                }
            };

            fsm.InsertAction("State 4", () => onShift(true), 0, true);
            fsm.InsertAction("State 3", () => onShift(false), 0, true);

            ferndaleShifters[keyHash] = entry;
        }

        private void HookKekmetRange(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            if (!fsm.HasState("Flip")) return;
            FsmBool rangeVar = fsm.FsmVariables.FindFsmBool("Range");
            if (rangeVar == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"KekmetRange_{veh.DisplayName}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var entry = new KekmetRangeEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                Range = rangeVar,
                ToggleEvent = fsm.AddSafeEvent("MP_TOGGLE")
            };

            fsm.AddSafeGlobalTransition(entry.ToggleEvent, "Flip");
            fsm.InsertAction("Flip", () =>
            {
                if (entry.Updating)
                {
                    entry.Updating = false;
                    return;
                }
                if (!CanSendLocal(entry.Vehicle)) return;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.KekmetRange);
                    p.WriteInt32(entry.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteBoolean(!entry.Range.Value);
                    ctx.Transport.Send(p);
                }
            }, 0, true);

            kekmetRanges[keyHash] = entry;
        }

        private void HookKekmetFrontLoader(VehicleCatalogEntry veh, PlayMakerFSM fsm)
        {
            string incName = fsm.HasState("INCREASE") ? "INCREASE" : (fsm.HasState("INCREASE 2") ? "INCREASE 2" : null);
            string decName = fsm.HasState("DECREASE") ? "DECREASE" : (fsm.HasState("DECREASE 2") ? "DECREASE 2" : null);
            if (incName == null || decName == null || !fsm.HasState("State 1")) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"FrontHyd_{veh.DisplayName}_{fsm.gameObject.name}")) return;

            hookedFsmInstanceIds.Add(fsm.GetInstanceID());
            int keyHash = ControlsHelpers.GetControlKeyHash(fsm);
            var entry = new KekmetFrontLoaderEntry
            {
                KeyHash = keyHash,
                Vehicle = veh,
                Fsm = fsm,
                ArmRot = fsm.FsmVariables.FindFsmFloat("ArmRot"),
                ActionEvents = new[] { "MP_INC", "MP_DEC", "MP_STOP" }
            };

            fsm.AddSafeGlobalTransition(entry.ActionEvents[0], incName);
            fsm.AddSafeGlobalTransition(entry.ActionEvents[1], decName);
            fsm.AddSafeGlobalTransition(entry.ActionEvents[2], "State 1");

            WrapFrontLoaderMouseUp(fsm.GetState(incName), entry);
            WrapFrontLoaderMouseUp(fsm.GetState(decName), entry);

            Action<int> sendMode = idx =>
            {
                if (entry.UpdatingAction == idx)
                {
                    entry.UpdatingAction = -1;
                    return;
                }
                if (!CanSendLocal(entry.Vehicle)) return;
                entry.CurrentAction = idx;
                entry.Owner = idx != 2 ? LocalPlayerId : -1;
                using (var p = new PacketWriter(PacketType.VehicleSystemToggle))
                {
                    p.WriteByte((byte)SystemToggleSubType.KekmetFrontLoader);
                    p.WriteInt32(entry.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte((byte)idx);
                    p.WriteSingle(entry.ArmRot != null ? entry.ArmRot.Value : 0f);
                    ctx.Transport.Send(p);
                }
            };

            fsm.InsertAction(incName, () => sendMode(0), 0, true);
            fsm.InsertAction(decName, () => sendMode(1), 0, true);
            fsm.InsertAction("State 1", () => sendMode(2), 0, true);

            kekmetFrontLoaders[keyHash] = entry;
        }

        private void WrapFrontLoaderMouseUp(FsmState state, KekmetFrontLoaderEntry entry)
        {
            if (state == null || state.Actions == null || entry == null || entry.Fsm == null) return;
            FsmStateAction[] actions = state.Actions;
            var newActions = new List<FsmStateAction>(actions.Length);

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

            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] is MousePickEvent mousePick)
                {
                    doMousePick = true;
                    layerMask = ActionHelpers.LayerArrayToLayerMask(mousePick.layerMask, mousePick.invertMask.Value);
                    rayDistance = mousePick.rayDistance.Value;
                    mouseOff = mousePick.mouseOff;
                }
                else if (actions[i] is GetMouseButtonDown mbd)
                {
                    doMbd = true;
                    mbdBtn = mbd.button;
                    mbdEvent = mbd.sendEvent;
                }
                else if (actions[i] is GetMouseButtonUp mbu)
                {
                    doMbu = true;
                    mbuBtn = mbu.button;
                    mbuEvent = mbu.sendEvent;
                }
                else
                {
                    newActions.Add(actions[i]);
                }
            }

            if (doMousePick || doMbd || doMbu)
            {
                newActions.Add(new EveryFrameFsmAction(() =>
                {
                    if (entry.Owner != -1 && entry.Owner != LocalPlayerId) return;

                    if (doMousePick)
                    {
                        bool isOver = ActionHelpers.IsMouseOver(entry.Fsm.gameObject, rayDistance, layerMask);
                        entry.Fsm.Fsm.RaycastHitInfo = ActionHelpers.mousePickInfo;
                        if (!isOver)
                        {
                            if (mouseOff != null) entry.Fsm.Fsm.Event(mouseOff);
                            return;
                        }
                    }

                    if (doMbd && ((mbdBtn == MouseButton.Left && Input.GetMouseButtonDown(0)) || (mbdBtn == MouseButton.Right && Input.GetMouseButtonDown(1))))
                    {
                        if (mbdEvent != null) entry.Fsm.Fsm.Event(mbdEvent);
                    }
                    else if (doMbu && ((mbuBtn == MouseButton.Left && Input.GetMouseButtonUp(0)) || (mbuBtn == MouseButton.Right && Input.GetMouseButtonUp(1))))
                    {
                        if (mbuEvent != null) entry.Fsm.Fsm.Event(mbuEvent);
                    }
                }));

                state.Actions = newActions.ToArray();
            }
        }
    }
}
