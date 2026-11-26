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
    /// Синхронизация автомагнитолы и CD-плеера (PacketType.VehicleRadioSync = 72).
    /// Исправляет баг легаси CarRadioManager.cs: каждая магнитола/CD-плеер идентифицируется строго
    /// по уникальному хэшу (SceneHasher) и пути в иерархии; читаются реальные Power, Volume, Channel,
    /// Track, крутилки (Knob FSM) и состояние AudioSource без глобальной рассылки на все машины.
    /// </summary>
    internal sealed class CarRadioSync
    {
        private sealed class RadioUnitEntry
        {
            public int UnitHash;
            public string HierarchyPath;
            public PlayMakerFSM MainFsm;
            public FsmBool PowerVar;
            public FsmFloat VolumeVar;
            public FsmFloat ChannelVar;
            public FsmInt TrackVar;
            public AudioSource[] AudioSources;
            public bool LastPower;
            public float LastVolume;
            public float LastChannel;
            public int LastTrack;
            public bool LastIsCdMode;
            public bool SuppressNext;
            public float SuppressUntilTime;
        }

        private sealed class RadioKnobEntry
        {
            public int KeyHash;
            public string HierarchyPath;
            public PlayMakerFSM Fsm;
            public FsmFloat TargetValue;
            public float AddStep;
            public FsmEvent UpdateEvent;
            public bool Updating;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private readonly Dictionary<int, RadioUnitEntry> unitsByHash = new Dictionary<int, RadioUnitEntry>();
        private readonly Dictionary<int, RadioKnobEntry> knobsByHash = new Dictionary<int, RadioKnobEntry>();
        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();

        private float nextScanTime;
        private float nextPollTime;

        public int TrackedRadioCount => unitsByHash.Count;
        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public CarRadioSync(OscHostContext context, string ownerModuleId)
        {
            ctx = context;
            moduleId = ownerModuleId;
        }

        public void Reset()
        {
            unitsByHash.Clear();
            knobsByHash.Clear();
            hookedFsmIds.Clear();
            nextScanTime = 0f;
            nextPollTime = 0f;
        }

        public void OnLevelLoaded()
        {
            ScanRadios();
        }

        public void OnUpdate()
        {
            if (!IsNetworkRunning) return;

            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + 8f;
                ScanRadios();
            }

            if (Time.time >= nextPollTime)
            {
                nextPollTime = Time.time + 0.35f;
                PollRadioVariableChanges();
            }
        }

        public void ScanRadios()
        {
            PlayMakerFSM[] allFsms = UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>();
            if (allFsms == null) return;

            for (int i = 0; i < allFsms.Length; i++)
            {
                PlayMakerFSM fsm = allFsms[i];
                if (fsm == null) continue;
                int instId = fsm.GetInstanceID();
                if (hookedFsmIds.Contains(instId)) continue;

                if (!IsRadioOrCdObject(fsm)) continue;
                fsm.InitializeSafe();

                if (fsm.FsmName == "Knob" && fsm.HasState("Increase"))
                {
                    HookRadioKnob(fsm);
                    continue;
                }

                if (fsm.FsmName == "Radio" || fsm.FsmName == "CD" ||
                    fsm.HasState("Radio OK") || fsm.HasState("CD OK") ||
                    fsm.FsmVariables.FindFsmBool("Power") != null)
                {
                    HookRadioMainFsm(fsm);
                }
            }
        }

        private static bool IsRadioOrCdObject(PlayMakerFSM fsm)
        {
            if (fsm.FsmName == "Radio" || fsm.FsmName == "CD") return true;
            Transform t = fsm.transform;
            for (int depth = 0; depth < 3 && t != null; depth++)
            {
                string n = t.name;
                if (ControlsHelpers.ContainsOrdinalIgnoreCase(n, "radio") ||
                    ControlsHelpers.ContainsOrdinalIgnoreCase(n, "cd player") ||
                    ControlsHelpers.ContainsOrdinalIgnoreCase(n, "cdplayer"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        private void HookRadioMainFsm(PlayMakerFSM fsm)
        {
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"RadioMain_{fsm.gameObject.name}")) return;
            hookedFsmIds.Add(fsm.GetInstanceID());

            string path = ControlsHelpers.GetHierarchyPath(fsm.transform);
            int hash = SceneHasher.GetStableHash(path + "::" + fsm.FsmName);

            var entry = new RadioUnitEntry
            {
                UnitHash = hash,
                HierarchyPath = path,
                MainFsm = fsm,
                PowerVar = fsm.FsmVariables.FindFsmBool("Power"),
                VolumeVar = fsm.FsmVariables.FindFsmFloat("Volume") ?? fsm.FsmVariables.FindFsmFloat("Vol"),
                ChannelVar = fsm.FsmVariables.FindFsmFloat("Channel") ?? fsm.FsmVariables.FindFsmFloat("Freq") ?? fsm.FsmVariables.FindFsmFloat("Station"),
                TrackVar = fsm.FsmVariables.FindFsmInt("Track") ?? fsm.FsmVariables.FindFsmInt("CurrentTrack") ?? fsm.FsmVariables.FindFsmInt("TrackNumber") ?? fsm.FsmVariables.FindFsmInt("Song"),
                AudioSources = fsm.gameObject.GetComponentsInChildren<AudioSource>(true)
            };

            ReadUnitValues(entry, out entry.LastPower, out entry.LastVolume, out entry.LastChannel, out entry.LastTrack);

            fsm.AddSafeGlobalTransition("RADIO ON", fsm.HasState("Radio OK") ? "Radio OK" : (fsm.HasState("CD OK") ? "CD OK" : fsm.Fsm.StartState));
            fsm.AddSafeGlobalTransition("RADIO OFF", fsm.HasState("Radio Out") ? "Radio Out" : (fsm.HasState("CD Out") ? "CD Out" : fsm.Fsm.StartState));
            if (fsm.HasState("CD OK")) fsm.AddSafeGlobalTransition("MP_CD_ON", "CD OK");
            if (fsm.HasState("CD Out")) fsm.AddSafeGlobalTransition("MP_CD_OFF", "CD Out");

            HookStateAction(fsm, "Radio OK", entry, forcedPower: true, isCd: false, cdAction: 0);
            HookStateAction(fsm, "Radio Out", entry, forcedPower: false, isCd: false, cdAction: 0);
            HookStateAction(fsm, "CD OK", entry, forcedPower: true, isCd: true, cdAction: 1);
            HookStateAction(fsm, "CD Out", entry, forcedPower: false, isCd: true, cdAction: 2);
            HookStateAction(fsm, "Play", entry, forcedPower: true, isCd: true, cdAction: 1);
            HookStateAction(fsm, "Stop", entry, forcedPower: false, isCd: true, cdAction: 2);
            HookStateAction(fsm, "Next", entry, forcedPower: null, isCd: true, cdAction: 3, insertIndex: -1);
            HookStateAction(fsm, "Previous", entry, forcedPower: null, isCd: true, cdAction: 4, insertIndex: -1);
            HookStateAction(fsm, "Eject", entry, forcedPower: false, isCd: true, cdAction: 5);
            HookStateAction(fsm, "Insert", entry, forcedPower: true, isCd: true, cdAction: 6);

            if (fsm.HasState("Next")) fsm.AddSafeGlobalTransition("MP_CD_NEXT", "Next");
            if (fsm.HasState("Previous")) fsm.AddSafeGlobalTransition("MP_CD_PREV", "Previous");
            if (fsm.HasState("Eject")) fsm.AddSafeGlobalTransition("MP_CD_EJECT", "Eject");
            if (fsm.HasState("Insert")) fsm.AddSafeGlobalTransition("MP_CD_INSERT", "Insert");

            unitsByHash[hash] = entry;
        }

        private void HookStateAction(PlayMakerFSM fsm, string stateName, RadioUnitEntry entry, bool? forcedPower, bool isCd, byte cdAction, int insertIndex = 0)
        {
            if (!fsm.HasState(stateName)) return;
            fsm.InsertAction(stateName, () =>
            {
                if (entry.SuppressNext || Time.time < entry.SuppressUntilTime)
                {
                    entry.SuppressNext = false;
                    return;
                }
                entry.LastIsCdMode = isCd;
                ReadUnitValues(entry, out bool power, out float vol, out float chan, out int track);
                if (forcedPower.HasValue) power = forcedPower.Value;
                if (entry.PowerVar != null && forcedPower.HasValue) entry.PowerVar.Value = forcedPower.Value;

                entry.LastPower = power;
                entry.LastVolume = vol;
                entry.LastChannel = chan;
                entry.LastTrack = track;

                if (cdAction >= 3)
                {
                    SendCdActionPacket(entry, cdAction, track);
                }
                SendRadioStatePacket(entry, power, vol, chan, track, isCd);
            }, insertIndex, true);
        }

        private void HookRadioKnob(PlayMakerFSM fsm)
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
            if (!ctx.Ownership.TryClaim(fsm, moduleId, $"RadioKnob_{fsm.gameObject.name}")) return;
            hookedFsmIds.Add(fsm.GetInstanceID());

            string path = ControlsHelpers.GetHierarchyPath(fsm.transform);
            int keyHash = SceneHasher.GetStableHash(path + "::Knob");
            var knob = new RadioKnobEntry
            {
                KeyHash = keyHash,
                HierarchyPath = path,
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
                if (!IsNetworkRunning) return;
                using (var p = new PacketWriter(PacketType.VehicleRadioSync))
                {
                    p.WriteByte((byte)RadioSubType.KnobSync);
                    p.WriteInt32(knob.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteString(knob.HierarchyPath);
                    p.WriteSingle(knob.TargetValue.Value);
                    ctx.Transport.Send(p);
                }
            }, 0, true);

            knobsByHash[keyHash] = knob;
        }

        private void PollRadioVariableChanges()
        {
            foreach (var kv in unitsByHash)
            {
                RadioUnitEntry entry = kv.Value;
                if (entry == null || entry.MainFsm == null) continue;
                if (Time.time < entry.SuppressUntilTime) continue;

                ReadUnitValues(entry, out bool power, out float vol, out float chan, out int track);
                bool changed = power != entry.LastPower ||
                               Mathf.Abs(vol - entry.LastVolume) > 0.02f ||
                               Mathf.Abs(chan - entry.LastChannel) > 0.02f ||
                               track != entry.LastTrack;
                if (!changed) continue;

                entry.LastPower = power;
                entry.LastVolume = vol;
                entry.LastChannel = chan;
                entry.LastTrack = track;
                SendRadioStatePacket(entry, power, vol, chan, track, entry.LastIsCdMode);
            }
        }

        private static void ReadUnitValues(RadioUnitEntry entry, out bool power, out float volume, out float channel, out int track)
        {
            power = entry.PowerVar != null ? entry.PowerVar.Value : entry.LastPower;
            volume = entry.VolumeVar != null ? entry.VolumeVar.Value : entry.LastVolume;
            channel = entry.ChannelVar != null ? entry.ChannelVar.Value : entry.LastChannel;
            track = entry.TrackVar != null ? entry.TrackVar.Value : entry.LastTrack;

            if (entry.AudioSources != null && entry.AudioSources.Length > 0)
            {
                AudioSource primary = entry.AudioSources[0];
                if (primary != null)
                {
                    if (entry.PowerVar == null) power = primary.isPlaying && !primary.mute;
                    if (entry.VolumeVar == null) volume = primary.volume;
                }
            }
        }

        public void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            foreach (var kv in unitsByHash)
            {
                RadioUnitEntry entry = kv.Value;
                if (entry == null || entry.MainFsm == null) continue;
                ReadUnitValues(entry, out bool power, out float vol, out float chan, out int track);
                using (var p = new PacketWriter(PacketType.VehicleRadioSync))
                {
                    p.WriteByte((byte)RadioSubType.StateSync);
                    p.WriteInt32(entry.UnitHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteString(entry.HierarchyPath);
                    p.WriteBoolean(power);
                    p.WriteSingle(vol);
                    p.WriteSingle(chan);
                    p.WriteInt32(track);
                    p.WriteBoolean(entry.LastIsCdMode);
                    ctx.Transport.SendTo(p, clientId);
                }
            }

            foreach (var kv in knobsByHash)
            {
                RadioKnobEntry knob = kv.Value;
                if (knob == null || knob.TargetValue == null) continue;
                using (var p = new PacketWriter(PacketType.VehicleRadioSync))
                {
                    p.WriteByte((byte)RadioSubType.KnobSync);
                    p.WriteInt32(knob.KeyHash);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteString(knob.HierarchyPath);
                    p.WriteSingle(knob.TargetValue.Value);
                    ctx.Transport.SendTo(p, clientId);
                }
            }
        }

        private void SendRadioStatePacket(RadioUnitEntry entry, bool power, float volume, float channel, int track, bool isCd)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleRadioSync))
            {
                p.WriteByte((byte)RadioSubType.StateSync);
                p.WriteInt32(entry.UnitHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteString(entry.HierarchyPath);
                p.WriteBoolean(power);
                p.WriteSingle(volume);
                p.WriteSingle(channel);
                p.WriteInt32(track);
                p.WriteBoolean(isCd);
                ctx.Transport.Send(p);
            }
        }

        private void SendCdActionPacket(RadioUnitEntry entry, byte cdAction, int track)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleRadioSync))
            {
                p.WriteByte((byte)RadioSubType.CdAction);
                p.WriteInt32(entry.UnitHash);
                p.WriteInt32(LocalPlayerId);
                p.WriteString(entry.HierarchyPath);
                p.WriteByte(cdAction);
                p.WriteInt32(track);
                ctx.Transport.Send(p);
            }
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 9) return;

            var subType = (RadioSubType)reader.ReadByte();
            int unitHash = reader.ReadInt32();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            string path = reader.ReadString();

            if (subType == RadioSubType.KnobSync)
            {
                float val = reader.ReadSingle();
                RadioKnobEntry knob = FindKnob(unitHash, path);
                if (knob != null && knob.Fsm != null && knob.TargetValue != null && knob.UpdateEvent != null)
                {
                    knob.Updating = true;
                    knob.TargetValue.Value = val - knob.AddStep;
                    knob.Fsm.Fsm.Event(knob.UpdateEvent);
                }
                return;
            }

            RadioUnitEntry entry = FindUnit(unitHash, path);
            if (entry == null || entry.MainFsm == null) return;

            if (subType == RadioSubType.CdAction)
            {
                byte cdAction = reader.ReadByte();
                int track = reader.ReadInt32();
                entry.SuppressNext = true;
                entry.SuppressUntilTime = Time.time + 0.25f;
                try
                {
                    if (entry.TrackVar != null) entry.TrackVar.Value = track;
                    entry.LastTrack = track;
                    if (cdAction == 3 && entry.MainFsm.HasState("Next")) entry.MainFsm.SendEvent("MP_CD_NEXT");
                    else if (cdAction == 4 && entry.MainFsm.HasState("Previous")) entry.MainFsm.SendEvent("MP_CD_PREV");
                    else if (cdAction == 5 && entry.MainFsm.HasState("Eject")) entry.MainFsm.SendEvent("MP_CD_EJECT");
                    else if (cdAction == 6 && entry.MainFsm.HasState("Insert")) entry.MainFsm.SendEvent("MP_CD_INSERT");
                }
                finally
                {
                    entry.SuppressNext = false;
                }
                return;
            }

            bool power = reader.ReadBoolean();
            float volume = reader.ReadSingle();
            float channel = reader.ReadSingle();
            int stateTrack = reader.ReadInt32();
            bool isCd = reader.UnreadBytes >= 1 && reader.ReadBoolean();

            entry.SuppressNext = true;
            entry.SuppressUntilTime = Time.time + 0.25f;
            try
            {
                if (entry.PowerVar != null) entry.PowerVar.Value = power;
                if (entry.VolumeVar != null) entry.VolumeVar.Value = volume;
                if (entry.ChannelVar != null) entry.ChannelVar.Value = channel;
                if (entry.TrackVar != null) entry.TrackVar.Value = stateTrack;

                entry.LastPower = power;
                entry.LastVolume = volume;
                entry.LastChannel = channel;
                entry.LastTrack = stateTrack;
                entry.LastIsCdMode = isCd;

                if (isCd && entry.MainFsm.HasState("CD OK"))
                {
                    entry.MainFsm.SendEvent(power ? "MP_CD_ON" : "MP_CD_OFF");
                }
                else
                {
                    entry.MainFsm.SendEvent(power ? "RADIO ON" : "RADIO OFF");
                }

                if (entry.AudioSources != null)
                {
                    for (int i = 0; i < entry.AudioSources.Length; i++)
                    {
                        AudioSource src = entry.AudioSources[i];
                        if (src == null) continue;
                        src.volume = Mathf.Clamp01(volume);
                        if (power && src.loop && !src.isPlaying && src.clip != null)
                        {
                            src.Play();
                        }
                        else if (!power && src.isPlaying)
                        {
                            src.Stop();
                        }
                    }
                }
            }
            finally
            {
                entry.SuppressNext = false;
            }
        }

        private RadioUnitEntry FindUnit(int hash, string path)
        {
            if (unitsByHash.TryGetValue(hash, out var exact)) return exact;
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var kv in unitsByHash)
                {
                    if (string.Equals(kv.Value.HierarchyPath, path, StringComparison.Ordinal))
                    {
                        return kv.Value;
                    }
                }
            }
            return null;
        }

        private RadioKnobEntry FindKnob(int hash, string path)
        {
            if (knobsByHash.TryGetValue(hash, out var exact)) return exact;
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var kv in knobsByHash)
                {
                    if (string.Equals(kv.Value.HierarchyPath, path, StringComparison.Ordinal))
                    {
                        return kv.Value;
                    }
                }
            }
            return null;
        }
    }
}
