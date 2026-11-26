using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Синхронизация:
    /// 1) Удалённых оборотов двигателя и звука (Drivetrain.rpm + SoundController через VehicleRemoteTelemetryEvent);
    /// 2) Уровня топлива во всех 8 топливных баках (мгновенная двусторонняя отправка при |delta| > 0.5L от АЗС/CheatBox + фоновая от хоста раз в 2с);
    /// 3) Жидкостей, износа, скрежета КПП и номерных знаков Satsuma (PacketType.VehicleFluidWearSync = 75).
    /// </summary>
    internal sealed class EngineAudioAndFuelSync
    {
        private sealed class FuelTankEntry
        {
            public VehicleSlot Slot;
            public string Name;
            public string Path;
            public GameObject TankObject;
            public FsmFloat FuelLevelVar;
            public float LastSentFuel = -999f;
            public float LastSyncTime;
        }

        private sealed class RemoteTelemetryState
        {
            public float Rpm;
            public int Gear;
            public float ThrottleInput;
            public bool IsDrivenRemotely;
            public float LastUpdateTime;
            public bool WasDrivenRemotely;
            public float LastSentIdleThrottle = -1f;
        }

        private static readonly FieldInfo fiEngineThrottleSource = typeof(SoundController).GetField("engineThrottleSource", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fiEngineNoThrottleSource = typeof(SoundController).GetField("engineNoThrottleSource", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fiTransmissionSource = typeof(SoundController).GetField("transmissionSource", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo fiVolumeFactor = typeof(SoundController).GetField("volumeFactor", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly VehicleCatalogEntry[] catalog;

        private readonly List<FuelTankEntry> fuelTanks = new List<FuelTankEntry>
        {
            new FuelTankEntry { Slot = VehicleSlot.Satsuma, Name = "Satsuma", Path = "Database/DatabaseMechanics/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Hayosiko, Name = "Hayosiko", Path = "HAYOSIKO(1500kg, 250)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Ruscko, Name = "Ruscko", Path = "RCO_RUSCKO12(270)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Ferndale, Name = "Ferndale", Path = "FERNDALE(1630kg)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Kekmet, Name = "Kekmet", Path = "KEKMET(350-400psi)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Gifu, Name = "Gifu", Path = "GIFU(750/450psi)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Jonnez, Name = "Jonnez", Path = "JONNEZ ES(Clone)/FuelTank" },
            new FuelTankEntry { Slot = VehicleSlot.Boat, Name = "Boat", Path = "BOAT/GFX/Motor/Pivot/FuelTank" }
        };

        private readonly Dictionary<byte, RemoteTelemetryState> telemetryByVehId = new Dictionary<byte, RemoteTelemetryState>();

        private float nextFuelScanTime;
        private float nextFuelCheckTime;
        private float nextEngineScanTime;
        private float nextFluidSyncTime;
        private float nextIdleCheckTime;

        // Satsuma жидкости / износ / номера
        private GameObject satsumaGo;
        private FuelTank satsumaFuelTank;
        private PlayMakerFSM fanBeltFsm;
        private PlayMakerFSM oilFsm;
        private PlayMakerFSM oilpanFsm;
        private PlayMakerFSM radiatorFsm;
        private GameObject steamRadiatorObj;
        private PlayMakerFSM brakeMasterFsm;
        private PlayMakerFSM clutchMasterFsm;
        private PlayMakerFSM rockerShaftFsm;
        private PlayMakerFSM headGasketFsm;
        private PlayMakerFSM piston1Fsm;
        private PlayMakerFSM piston2Fsm;
        private PlayMakerFSM piston3Fsm;
        private PlayMakerFSM piston4Fsm;
        private PlayMakerFSM gearGrindingFsm;
        private PlayMakerFSM regPlateFrontFsm;
        private PlayMakerFSM regPlateRearFsm;

        private bool isSuppressingGrind;
        private bool wasGrinding;
        private bool isSuppressingPlates;
        private bool isSuppressingFluids;
        private bool isSuppressingWear;

        private float lastSentOil = -1f;
        private float lastSentCoolant = -1f;
        private float lastSentBrake = -1f;
        private float lastSentClutch = -1f;
        private bool lastSentBoiling;
        private float lastFluidsHeartbeatTime;

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public EngineAudioAndFuelSync(OscHostContext context, string ownerModuleId, VehicleCatalogEntry[] vehicleCatalog)
        {
            ctx = context;
            moduleId = ownerModuleId;
            catalog = vehicleCatalog;
        }

        public void Reset()
        {
            for (int i = 0; i < fuelTanks.Count; i++)
            {
                fuelTanks[i].TankObject = null;
                fuelTanks[i].FuelLevelVar = null;
                fuelTanks[i].LastSentFuel = -999f;
                fuelTanks[i].LastSyncTime = 0f;
            }

            telemetryByVehId.Clear();
            nextFuelScanTime = 0f;
            nextFuelCheckTime = 0f;
            nextEngineScanTime = 0f;
            nextFluidSyncTime = 0f;
            nextIdleCheckTime = 0f;

            satsumaGo = null;
            satsumaFuelTank = null;
            fanBeltFsm = null;
            oilFsm = null;
            oilpanFsm = null;
            radiatorFsm = null;
            steamRadiatorObj = null;
            brakeMasterFsm = null;
            clutchMasterFsm = null;
            rockerShaftFsm = null;
            headGasketFsm = null;
            piston1Fsm = null;
            piston2Fsm = null;
            piston3Fsm = null;
            piston4Fsm = null;
            gearGrindingFsm = null;
            regPlateFrontFsm = null;
            regPlateRearFsm = null;

            isSuppressingGrind = false;
            wasGrinding = false;
            isSuppressingPlates = false;
            isSuppressingFluids = false;
            isSuppressingWear = false;
            lastSentOil = -1f;
            lastSentCoolant = -1f;
            lastSentBrake = -1f;
            lastSentClutch = -1f;
            lastSentBoiling = false;
            lastFluidsHeartbeatTime = 0f;
        }

        public void OnLevelLoaded()
        {
            ScanFuelTanks();
            ScanSatsumaEngineAndFluids();
        }

        public void HandleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            byte vid = (byte)evt.VehicleId;
            if (!telemetryByVehId.TryGetValue(vid, out var state))
            {
                state = new RemoteTelemetryState();
                telemetryByVehId[vid] = state;
            }

            state.Rpm = evt.Rpm;
            state.Gear = evt.Gear;
            state.ThrottleInput = evt.ThrottleInput;
            state.IsDrivenRemotely = evt.IsDrivenRemotely;
            state.LastUpdateTime = Time.time;

            VehicleCatalogEntry veh = ControlsHelpers.FindVehicleById(catalog, vid);
            if (veh != null)
            {
                veh.IsDrivenRemotely = evt.IsDrivenRemotely;
                if (evt.VehicleTransform != null && veh.RootObject == null)
                {
                    veh.RootObject = evt.VehicleTransform.gameObject;
                }
            }
        }

        public void OnUpdate()
        {
            UpdateRemoteEngineAudio();

            if (!IsNetworkRunning) return;

            UpdateFuelSync();
            UpdateIdleThrottleSync();

            if (Time.time >= nextEngineScanTime)
            {
                nextEngineScanTime = Time.time + 8f;
                if (satsumaGo == null || satsumaFuelTank == null || gearGrindingFsm == null)
                {
                    ScanSatsumaEngineAndFluids();
                }
            }

            if (ctx.Transport.IsServer && Time.time >= nextFluidSyncTime)
            {
                nextFluidSyncTime = Time.time + 0.5f;
                SyncSatsumaFluidsIfNeeded();
            }
        }

        public void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanFuelTanks();
            for (int i = 0; i < fuelTanks.Count; i++)
            {
                FuelTankEntry entry = fuelTanks[i];
                float fuel = GetTankFuel(entry);
                if (fuel >= 0f)
                {
                    using (var p = new PacketWriter(PacketType.VehicleFuelSync))
                    {
                        p.WriteByte((byte)entry.Slot);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteSingle(fuel);
                        ctx.Transport.SendTo(p, clientId);
                    }
                }
            }

            SendSatsumaFluidsPacket(clientId);
        }

        public void HandleFuelPacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 9) return;
            byte vehId = reader.ReadByte();
            int senderId = reader.ReadInt32();
            float fuelLevel = reader.ReadSingle();
            if (senderId == LocalPlayerId) return;

            FuelTankEntry entry = null;
            for (int i = 0; i < fuelTanks.Count; i++)
            {
                if ((byte)fuelTanks[i].Slot == vehId)
                {
                    entry = fuelTanks[i];
                    break;
                }
            }

            if (entry == null) return;
            if (entry.FuelLevelVar == null) ScanFuelTanks();

            SetTankFuel(entry, fuelLevel);
            entry.LastSentFuel = fuelLevel;
            entry.LastSyncTime = Time.time;
        }

        public void HandleFluidWearPacket(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 5) return;
            var subType = (FluidWearSubType)reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case FluidWearSubType.SatsumaFluids:
                    ApplySatsumaFluidsPacket(reader);
                    break;
                case FluidWearSubType.PartBreakage:
                {
                    string partName = reader.ReadString();
                    byte breakType = reader.ReadByte();
                    ApplyPartBreakage(partName, breakType);
                    break;
                }
                case FluidWearSubType.GearGrind:
                {
                    bool grinding = reader.ReadBoolean();
                    ApplyGearGrind(grinding);
                    break;
                }
                case FluidWearSubType.LicensePlates:
                {
                    bool installed = reader.ReadBoolean();
                    string plate = reader.ReadString();
                    ApplyLicensePlates(installed, plate);
                    break;
                }
                case FluidWearSubType.IdleThrottle:
                {
                    byte vehId = reader.ReadByte();
                    float idle = reader.ReadSingle();
                    VehicleCatalogEntry veh = ControlsHelpers.FindVehicleById(catalog, vehId);
                    if (veh != null && veh.Drivetrain != null && !veh.IsDrivenLocally)
                    {
                        veh.Drivetrain.idlethrottle = idle;
                    }
                    break;
                }
            }
        }

        private void UpdateRemoteEngineAudio()
        {
            float dt = Time.deltaTime;
            float now = Time.time;

            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (!veh.IsBound) continue;

                if (!telemetryByVehId.TryGetValue(veh.VehicleId, out var tel))
                {
                    continue;
                }

                // Таймаут телеметрии, если удалённый водитель вышел
                if (tel.IsDrivenRemotely && (now - tel.LastUpdateTime) > 1.5f)
                {
                    tel.IsDrivenRemotely = false;
                }

                bool activeRemote = tel.IsDrivenRemotely && !veh.IsDrivenLocally;
                if (activeRemote)
                {
                    tel.WasDrivenRemotely = true;

                    if (veh.Drivetrain != null)
                    {
                        float targetRpm = Mathf.Max(0f, tel.Rpm);
                        veh.Drivetrain.rpm = Mathf.Lerp(veh.Drivetrain.rpm, targetRpm, Mathf.Clamp01(dt * 15f));
                        veh.Drivetrain.gear = tel.Gear;
                        veh.Drivetrain.throttle = Mathf.Clamp01(tel.ThrottleInput);
                    }

                    if (veh.SoundController != null)
                    {
                        ApplyRemoteSoundController(veh.SoundController, veh.Drivetrain, tel.Rpm, tel.ThrottleInput, tel.Gear);
                    }
                }
                else if (tel.WasDrivenRemotely)
                {
                    tel.WasDrivenRemotely = false;
                    if (!veh.IsDrivenLocally && veh.Drivetrain != null && tel.Rpm < 120f)
                    {
                        veh.Drivetrain.rpm = tel.Rpm;
                    }
                    // Когда удалённый водитель вышел, глушим остаточный удалённый звук, если обороты низкие
                    if (!veh.IsDrivenLocally && veh.SoundController != null && (veh.Drivetrain == null || veh.Drivetrain.rpm < 200f))
                    {
                        StopRemoteSoundSources(veh.SoundController);
                    }
                }
            }
        }

        private void ApplyRemoteSoundController(SoundController sc, Drivetrain dt, float rpm, float throttleInput, int gear)
        {
            if (sc == null) return;

            AudioSource thrSrc = fiEngineThrottleSource?.GetValue(sc) as AudioSource;
            AudioSource noThrSrc = fiEngineNoThrottleSource?.GetValue(sc) as AudioSource;
            AudioSource transSrc = fiTransmissionSource?.GetValue(sc) as AudioSource;

            float volFactor = 1f;
            if (fiVolumeFactor != null)
            {
                object vfObj = fiVolumeFactor.GetValue(sc);
                if (vfObj is float vf && vf > 0.01f) volFactor = vf;
            }

            float maxRpm = (dt != null && dt.maxRPM > 500f) ? dt.maxRPM : 7000f;
            float normRpm = Mathf.Clamp01(rpm / maxRpm);
            bool engineRunning = rpm > 120f;
            float throttle = Mathf.Clamp01(throttleInput);

            if (thrSrc != null)
            {
                if (engineRunning)
                {
                    if (!thrSrc.isPlaying && thrSrc.clip != null) thrSrc.Play();
                    float pitchBase = sc.engineThrottlePitchFactor > 0.01f ? sc.engineThrottlePitchFactor : 1.0f;
                    thrSrc.pitch = Mathf.Clamp(0.45f + normRpm * 1.45f * pitchBase, 0.35f, 2.8f);
                    float baseVol = sc.engineThrottleVolume > 0.001f ? sc.engineThrottleVolume : 0.65f;
                    thrSrc.volume = Mathf.Clamp01(baseVol * volFactor * (0.25f + 0.75f * throttle) * (0.4f + 0.6f * normRpm));
                }
                else if (thrSrc.isPlaying)
                {
                    thrSrc.volume = 0f;
                }
            }

            if (noThrSrc != null)
            {
                if (engineRunning)
                {
                    if (!noThrSrc.isPlaying && noThrSrc.clip != null) noThrSrc.Play();
                    float pitchBase = sc.engineNoThrottlePitchFactor > 0.01f ? sc.engineNoThrottlePitchFactor : 1.0f;
                    noThrSrc.pitch = Mathf.Clamp(0.45f + normRpm * 1.35f * pitchBase, 0.35f, 2.6f);
                    float baseVol = sc.engineNoThrottleVolume > 0.001f ? sc.engineNoThrottleVolume : 0.55f;
                    noThrSrc.volume = Mathf.Clamp01(baseVol * volFactor * (1.0f - 0.55f * throttle) * (0.45f + 0.55f * normRpm));
                }
                else if (noThrSrc.isPlaying)
                {
                    noThrSrc.volume = 0f;
                }
            }

            if (transSrc != null)
            {
                bool inGear = gear != (dt != null ? dt.neutral : 1);
                if (engineRunning && inGear && normRpm > 0.15f)
                {
                    if (!transSrc.isPlaying && transSrc.clip != null) transSrc.Play();
                    float pitchBase = sc.transmissionSourcePitch > 0.01f ? sc.transmissionSourcePitch : 1.0f;
                    transSrc.pitch = Mathf.Clamp(0.5f + normRpm * 1.3f * pitchBase, 0.4f, 2.4f);
                    float baseVol = gear == 0
                        ? (sc.transmissionVolumeReverse > 0.001f ? sc.transmissionVolumeReverse : 0.2f)
                        : (sc.transmissionVolume > 0.001f ? sc.transmissionVolume : 0.15f);
                    transSrc.volume = Mathf.Clamp01(baseVol * volFactor * normRpm);
                }
                else if (transSrc.isPlaying)
                {
                    transSrc.volume = 0f;
                }
            }
        }

        private static void StopRemoteSoundSources(SoundController sc)
        {
            if (sc == null) return;
            AudioSource thrSrc = fiEngineThrottleSource?.GetValue(sc) as AudioSource;
            AudioSource noThrSrc = fiEngineNoThrottleSource?.GetValue(sc) as AudioSource;
            AudioSource transSrc = fiTransmissionSource?.GetValue(sc) as AudioSource;
            if (thrSrc != null) thrSrc.volume = 0f;
            if (noThrSrc != null) noThrSrc.volume = 0f;
            if (transSrc != null) transSrc.volume = 0f;
        }

        private void UpdateIdleThrottleSync()
        {
            if (Time.time < nextIdleCheckTime) return;
            nextIdleCheckTime = Time.time + 0.25f;

            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (!veh.IsBound || veh.Drivetrain == null || !veh.IsDrivenLocally) continue;

                if (!telemetryByVehId.TryGetValue(veh.VehicleId, out var tel))
                {
                    tel = new RemoteTelemetryState();
                    telemetryByVehId[veh.VehicleId] = tel;
                }

                float curIdle = veh.Drivetrain.idlethrottle;
                if (Mathf.Abs(curIdle - tel.LastSentIdleThrottle) > 0.01f)
                {
                    tel.LastSentIdleThrottle = curIdle;
                    using (var p = new PacketWriter(PacketType.VehicleFluidWearSync))
                    {
                        p.WriteByte((byte)FluidWearSubType.IdleThrottle);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteByte(veh.VehicleId);
                        p.WriteSingle(curIdle);
                        ctx.Transport.Send(p);
                    }
                }
            }
        }

        #region Fuel Sync (8 Tanks)

        public void ScanFuelTanks()
        {
            for (int i = 0; i < fuelTanks.Count; i++)
            {
                FuelTankEntry entry = fuelTanks[i];
                if (entry.TankObject != null && entry.FuelLevelVar != null) continue;

                GameObject go = FindFuelTankObject(entry);
                if (go == null) continue;

                entry.TankObject = go;
                PlayMakerFSM[] fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int f = 0; f < fsms.Length; f++)
                {
                    if (fsms[f] == null) continue;
                    fsms[f].InitializeSafe();
                    FsmFloat fv = fsms[f].FsmVariables.FindFsmFloat("FuelLevel");
                    if (fv != null)
                    {
                        entry.FuelLevelVar = fv;
                        break;
                    }
                }
            }
        }

        private GameObject FindFuelTankObject(FuelTankEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Path))
            {
                GameObject direct = GameObject.Find(entry.Path);
                if (direct != null) return direct;
            }

            if (entry.Slot == VehicleSlot.Satsuma)
            {
                GameObject db = GameObject.Find("Database");
                if (db != null)
                {
                    Transform ft = PlayMakerBridge.FindChildRecursive(db.transform, "FuelTank");
                    if (ft != null) return ft.gameObject;
                }
            }

            VehicleCatalogEntry veh = ControlsHelpers.FindVehicleById(catalog, (int)entry.Slot);
            if (veh != null && veh.RootObject != null)
            {
                Transform ft = PlayMakerBridge.FindChildRecursive(veh.RootObject.transform, "FuelTank");
                if (ft != null) return ft.gameObject;
            }

            return null;
        }

        private float GetTankFuel(FuelTankEntry entry)
        {
            if (entry.FuelLevelVar != null) return entry.FuelLevelVar.Value;
            if (entry.Slot == VehicleSlot.Satsuma && satsumaFuelTank != null) return satsumaFuelTank._currentFuel;
            return -1f;
        }

        private void SetTankFuel(FuelTankEntry entry, float fuelLevel)
        {
            if (entry.FuelLevelVar != null) entry.FuelLevelVar.Value = fuelLevel;
            if (entry.Slot == VehicleSlot.Satsuma && satsumaFuelTank != null) satsumaFuelTank._currentFuel = fuelLevel;
        }

        private void UpdateFuelSync()
        {
            if (Time.time >= nextFuelScanTime)
            {
                nextFuelScanTime = Time.time + 5.0f;
                ScanFuelTanks();
            }

            if (Time.time < nextFuelCheckTime) return;
            nextFuelCheckTime = Time.time + 0.25f;

            float now = Time.time;
            for (int i = 0; i < fuelTanks.Count; i++)
            {
                FuelTankEntry entry = fuelTanks[i];
                float currentFuel = GetTankFuel(entry);
                if (currentFuel < 0f) continue;

                if (entry.LastSentFuel < -900f)
                {
                    entry.LastSentFuel = currentFuel;
                    entry.LastSyncTime = now;
                    continue;
                }

                float delta = Mathf.Abs(currentFuel - entry.LastSentFuel);
                // > 0.5L (АЗС / BetterCheatBox): мгновенная двусторонняя отправка;
                // <= 0.5L: фоновый расход от хоста раз в 2.0с при дельте > 0.05L
                bool shouldSync = delta > 0.5f || (ctx.Transport.IsServer && (now - entry.LastSyncTime) >= 2.0f && delta > 0.05f);
                if (shouldSync)
                {
                    entry.LastSentFuel = currentFuel;
                    entry.LastSyncTime = now;
                    using (var p = new PacketWriter(PacketType.VehicleFuelSync))
                    {
                        p.WriteByte((byte)entry.Slot);
                        p.WriteInt32(LocalPlayerId);
                        p.WriteSingle(currentFuel);
                        ctx.Transport.Send(p);
                    }
                }
            }
        }

        #endregion

        #region Satsuma Fluids, Wear, Gear Grind & Plates

        private void ScanSatsumaEngineAndFluids()
        {
            VehicleCatalogEntry satsumaEntry = ControlsHelpers.FindVehicleById(catalog, (int)VehicleSlot.Satsuma);
            satsumaGo = satsumaEntry?.RootObject ?? GameObject.Find("SATSUMA(557kg, 248)") ?? GameObject.Find("SATSUMA(580kg, 87)");

            if (satsumaGo != null && satsumaFuelTank == null)
            {
                satsumaFuelTank = satsumaGo.GetComponentInChildren<FuelTank>();
            }
            if (satsumaFuelTank == null)
            {
                satsumaFuelTank = UnityEngine.Object.FindObjectOfType<FuelTank>();
            }

            if (fanBeltFsm == null)
            {
                GameObject motorGo = (satsumaGo != null ? PlayMakerBridge.FindChildRecursive(satsumaGo.transform, "_Motor")?.gameObject : null)
                    ?? GameObject.Find("_Motor");
                if (motorGo != null)
                {
                    fanBeltFsm = PlayMakerBridge.FindFsm(motorGo, "Fan Belt");
                    if (fanBeltFsm != null && fanBeltFsm.HasState("No belt") && ctx.Ownership.TryClaim(fanBeltFsm, moduleId, "Satsuma_FanBelt"))
                    {
                        fanBeltFsm.InsertAction("No belt", () =>
                        {
                            if (!isSuppressingWear) SendPartBreakage("FanBelt", 2);
                        }, 0, true);
                    }
                }
            }

            if (oilFsm == null)
            {
                GameObject oilGo = GameObject.Find("Oil") ?? GameObject.Find("Combustion");
                if (oilGo != null)
                {
                    oilFsm = PlayMakerBridge.FindFsm(oilGo, "Oil") ??
                             PlayMakerBridge.FindFsm(oilGo, "Pressure") ??
                             PlayMakerBridge.FindFsm(oilGo, "Cylinders");
                }
            }
            if (oilpanFsm == null)
            {
                GameObject oilpanGo = GameObject.Find("Oilpan");
                if (oilpanGo != null) oilpanFsm = PlayMakerBridge.FindFsm(oilpanGo, "Data");
            }
            if (radiatorFsm == null)
            {
                GameObject radGo = GameObject.Find("Radiator") ?? GameObject.Find("racing radiator(Clone)") ??
                                   GameObject.Find("radiator(Clone)") ?? GameObject.Find("Racing Radiator");
                if (radGo != null) radiatorFsm = PlayMakerBridge.FindFsm(radGo, "Radiator") ?? radGo.GetComponent<PlayMakerFSM>();
            }
            if (steamRadiatorObj == null) steamRadiatorObj = GameObject.Find("SteamRadiator");
            if (brakeMasterFsm == null)
            {
                GameObject go = GameObject.Find("BrakeMasterCylinder");
                if (go != null) brakeMasterFsm = PlayMakerBridge.FindFsm(go, "Data");
            }
            if (clutchMasterFsm == null)
            {
                GameObject go = GameObject.Find("ClutchMasterCylinder");
                if (go != null) clutchMasterFsm = PlayMakerBridge.FindFsm(go, "Data");
            }

            if (rockerShaftFsm == null)
            {
                GameObject rockerGo = GameObject.Find("rocker shaft(Clone)") ?? GameObject.Find("trigger_rocker shaft");
                if (rockerGo != null)
                {
                    rockerShaftFsm = PlayMakerBridge.FindFsm(rockerGo, "Removal") ?? rockerGo.GetComponent<PlayMakerFSM>();
                    if (rockerShaftFsm != null && rockerShaftFsm.HasState("Break") && ctx.Ownership.TryClaim(rockerShaftFsm, moduleId, "Satsuma_RockerShaft"))
                    {
                        rockerShaftFsm.InsertAction("Break", () =>
                        {
                            if (!isSuppressingWear) SendPartBreakage("RockerShaft", 4);
                        }, 0, true);
                    }
                }
            }

            if (headGasketFsm == null)
            {
                GameObject gasketGo = GameObject.Find("head gasket(Clone)") ?? GameObject.Find("trigger_head gasket");
                if (gasketGo != null)
                {
                    headGasketFsm = PlayMakerBridge.FindFsm(gasketGo, "Removal") ?? gasketGo.GetComponent<PlayMakerFSM>();
                    if (headGasketFsm != null && headGasketFsm.HasState("Death") && ctx.Ownership.TryClaim(headGasketFsm, moduleId, "Satsuma_HeadGasket"))
                    {
                        headGasketFsm.InsertAction("Death", () =>
                        {
                            if (!isSuppressingWear) SendPartBreakage("HeadGasket", 1);
                        }, 0, true);
                    }
                }
            }

            if (piston1Fsm == null) piston1Fsm = HookPiston("piston1(Clone)", 1);
            if (piston2Fsm == null) piston2Fsm = HookPiston("piston2(Clone)", 2);
            if (piston3Fsm == null) piston3Fsm = HookPiston("piston3(Clone)", 3);
            if (piston4Fsm == null) piston4Fsm = HookPiston("piston4(Clone)", 4);

            if (gearGrindingFsm == null)
            {
                GameObject grindGo = (satsumaGo != null ? PlayMakerBridge.FindChildRecursive(satsumaGo.transform, "GearGrinding")?.gameObject : null)
                    ?? GameObject.Find("GearGrinding");
                if (grindGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(grindGo, "Grinding");
                    if (fsm != null && ctx.Ownership.TryClaim(fsm, moduleId, "Satsuma_GearGrinding"))
                    {
                        gearGrindingFsm = fsm;
                        fsm.AddSafeGlobalTransition("MP_SYNC_GRIND", "State 1");
                        fsm.AddSafeGlobalTransition("MP_SYNC_STOP_GRIND", "State 2");
                        if (fsm.HasState("State 1"))
                        {
                            fsm.InsertAction("State 1", () =>
                            {
                                if (isSuppressingGrind) return;
                                wasGrinding = true;
                                SendGearGrindPacket(true);
                            }, 0, true);
                        }
                        if (fsm.HasState("State 2"))
                        {
                            fsm.InsertAction("State 2", () =>
                            {
                                if (isSuppressingGrind || !wasGrinding) return;
                                wasGrinding = false;
                                SendGearGrindPacket(false);
                            }, 0, true);
                        }
                    }
                }
            }

            if (regPlateFrontFsm == null) regPlateFrontFsm = HookPlateFsm("RegPlateFront");
            if (regPlateRearFsm == null) regPlateRearFsm = HookPlateFsm("RegPlateRear");
        }

        private PlayMakerFSM HookPiston(string goName, int idx)
        {
            GameObject pGo = GameObject.Find(goName) ?? GameObject.Find($"trigger_piston{idx}");
            if (pGo == null) return null;
            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(pGo, "Removal") ??
                               PlayMakerBridge.FindFsm(pGo, "Collision") ??
                               pGo.GetComponent<PlayMakerFSM>();
            if (fsm != null && fsm.HasState("Death") && ctx.Ownership.TryClaim(fsm, moduleId, $"Satsuma_Piston{idx}"))
            {
                fsm.InsertAction("Death", () =>
                {
                    if (!isSuppressingWear) SendPartBreakage($"Piston{idx}", 3);
                }, 0, true);
            }
            return fsm;
        }

        private PlayMakerFSM HookPlateFsm(string objectName)
        {
            GameObject plateGo = (satsumaGo != null ? PlayMakerBridge.FindChildRecursive(satsumaGo.transform, objectName)?.gameObject : null)
                ?? GameObject.Find(objectName);
            if (plateGo == null) return null;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(plateGo, "Data");
            if (fsm == null || !ctx.Ownership.TryClaim(fsm, moduleId, $"Satsuma_{objectName}")) return null;

            fsm.AddSafeGlobalTransition("MP_SYNC_INSTALL", "Installed");
            fsm.AddSafeGlobalTransition("MP_SYNC_DISABLE", "Disable plate");

            if (fsm.HasState("Installed"))
            {
                fsm.InsertAction("Installed", () =>
                {
                    if (!isSuppressingPlates) SendLicensePlatesPacket(true, GetCurrentPlateString());
                }, 0, true);
            }
            if (fsm.HasState("Disable plate"))
            {
                fsm.InsertAction("Disable plate", () =>
                {
                    if (!isSuppressingPlates) SendLicensePlatesPacket(false, GetCurrentPlateString());
                }, 0, true);
            }
            return fsm;
        }

        private void SyncSatsumaFluidsIfNeeded()
        {
            if (isSuppressingFluids) return;
            ReadCurrentFluids(out float oil, out float coolant, out float brake, out float clutch, out bool boiling);

            bool changed = Mathf.Abs(oil - lastSentOil) > 0.02f ||
                           Mathf.Abs(coolant - lastSentCoolant) > 0.02f ||
                           Mathf.Abs(brake - lastSentBrake) > 0.02f ||
                           Mathf.Abs(clutch - lastSentClutch) > 0.02f ||
                           boiling != lastSentBoiling;

            if (!changed && (Time.time - lastFluidsHeartbeatTime) < 2.5f) return;

            lastSentOil = oil;
            lastSentCoolant = coolant;
            lastSentBrake = brake;
            lastSentClutch = clutch;
            lastSentBoiling = boiling;
            lastFluidsHeartbeatTime = Time.time;

            SendSatsumaFluidsPacket(-1);
        }

        private void ReadCurrentFluids(out float oil, out float coolant, out float brake, out float clutch, out bool boiling)
        {
            oil = 1f;
            coolant = 1f;
            brake = 1f;
            clutch = 1f;
            boiling = steamRadiatorObj != null && steamRadiatorObj.activeInHierarchy;

            if (oilFsm != null)
            {
                FsmFloat oVar = oilFsm.FsmVariables.FindFsmFloat("Oil");
                if (oVar != null) oil = oVar.Value;
            }
            else if (oilpanFsm != null)
            {
                FsmFloat oVar = oilpanFsm.FsmVariables.FindFsmFloat("Oil");
                if (oVar != null) oil = oVar.Value;
            }

            if (radiatorFsm != null)
            {
                FsmFloat cVar = radiatorFsm.FsmVariables.FindFsmFloat("Coolant");
                if (cVar != null) coolant = cVar.Value;
            }

            if (brakeMasterFsm != null)
            {
                FsmFloat bVar = brakeMasterFsm.FsmVariables.FindFsmFloat("BrakeFluid");
                if (bVar != null) brake = bVar.Value;
            }

            if (clutchMasterFsm != null)
            {
                FsmFloat clVar = clutchMasterFsm.FsmVariables.FindFsmFloat("ClutchFluid");
                if (clVar != null) clutch = clVar.Value;
            }
        }

        private void SendSatsumaFluidsPacket(int targetClientId)
        {
            if (!IsNetworkRunning) return;
            ReadCurrentFluids(out float oil, out float coolant, out float brake, out float clutch, out bool boiling);

            using (var p = new PacketWriter(PacketType.VehicleFluidWearSync))
            {
                p.WriteByte((byte)FluidWearSubType.SatsumaFluids);
                p.WriteInt32(LocalPlayerId);
                p.WriteSingle(oil);
                p.WriteSingle(coolant);
                p.WriteSingle(brake);
                p.WriteSingle(clutch);
                p.WriteBoolean(boiling);
                if (targetClientId >= 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        private void ApplySatsumaFluidsPacket(PacketReader reader)
        {
            float oil = reader.ReadSingle();
            float coolant = reader.ReadSingle();
            float brake = reader.ReadSingle();
            float clutch = reader.ReadSingle();
            bool boiling = reader.ReadBoolean();

            isSuppressingFluids = true;
            try
            {
                if (oilFsm != null)
                {
                    FsmFloat oVar = oilFsm.FsmVariables.FindFsmFloat("Oil");
                    if (oVar != null) oVar.Value = oil;
                }
                if (oilpanFsm != null)
                {
                    FsmFloat oVar = oilpanFsm.FsmVariables.FindFsmFloat("Oil");
                    if (oVar != null) oVar.Value = oil;
                }
                if (radiatorFsm != null)
                {
                    FsmFloat cVar = radiatorFsm.FsmVariables.FindFsmFloat("Coolant");
                    if (cVar != null) cVar.Value = coolant;
                }
                if (brakeMasterFsm != null)
                {
                    FsmFloat bVar = brakeMasterFsm.FsmVariables.FindFsmFloat("BrakeFluid");
                    if (bVar != null) bVar.Value = brake;
                }
                if (clutchMasterFsm != null)
                {
                    FsmFloat clVar = clutchMasterFsm.FsmVariables.FindFsmFloat("ClutchFluid");
                    if (clVar != null) clVar.Value = clutch;
                }
                if (steamRadiatorObj != null)
                {
                    steamRadiatorObj.SetActive(boiling);
                }
            }
            finally
            {
                isSuppressingFluids = false;
            }
        }

        private void SendPartBreakage(string partName, byte breakType)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleFluidWearSync))
            {
                p.WriteByte((byte)FluidWearSubType.PartBreakage);
                p.WriteInt32(LocalPlayerId);
                p.WriteString(partName);
                p.WriteByte(breakType);
                ctx.Transport.Send(p);
            }
        }

        private void ApplyPartBreakage(string partName, byte breakType)
        {
            isSuppressingWear = true;
            try
            {
                if (breakType == 1 && headGasketFsm != null) headGasketFsm.SendEvent("DEATH");
                else if (breakType == 2 && fanBeltFsm != null) fanBeltFsm.SendEvent("SNAP");
                else if (breakType == 3)
                {
                    if (partName.Contains("1") && piston1Fsm != null) piston1Fsm.SendEvent("DEATH");
                    else if (partName.Contains("2") && piston2Fsm != null) piston2Fsm.SendEvent("DEATH");
                    else if (partName.Contains("3") && piston3Fsm != null) piston3Fsm.SendEvent("DEATH");
                    else if (partName.Contains("4") && piston4Fsm != null) piston4Fsm.SendEvent("DEATH");
                }
                else if (breakType == 4 && rockerShaftFsm != null) rockerShaftFsm.SendEvent("BREAK");
            }
            finally
            {
                isSuppressingWear = false;
            }
        }

        private void SendGearGrindPacket(bool isGrinding)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleFluidWearSync))
            {
                p.WriteByte((byte)FluidWearSubType.GearGrind);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(isGrinding);
                ctx.Transport.Send(p);
            }
        }

        private void ApplyGearGrind(bool isGrinding)
        {
            if (gearGrindingFsm == null) return;
            isSuppressingGrind = true;
            try
            {
                gearGrindingFsm.SendEvent(isGrinding ? "MP_SYNC_GRIND" : "MP_SYNC_STOP_GRIND");
            }
            finally
            {
                isSuppressingGrind = false;
            }
        }

        private void SendLicensePlatesPacket(bool installed, string plateStr)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleFluidWearSync))
            {
                p.WriteByte((byte)FluidWearSubType.LicensePlates);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(installed);
                p.WriteString(plateStr ?? "VBX-403");
                ctx.Transport.Send(p);
            }
        }

        private void ApplyLicensePlates(bool installed, string plateStr)
        {
            isSuppressingPlates = true;
            try
            {
                if (!string.IsNullOrEmpty(plateStr))
                {
                    RegPlateGen[] gens = satsumaGo != null
                        ? satsumaGo.GetComponentsInChildren<RegPlateGen>(true)
                        : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();
                    if (gens != null)
                    {
                        for (int i = 0; i < gens.Length; i++)
                        {
                            if (gens[i] == null) continue;
                            gens[i].PlateString = plateStr;
                            try
                            {
                                MethodInfo mi = typeof(RegPlateGen).GetMethod(
                                    "GenerateRegPlate",
                                    BindingFlags.NonPublic | BindingFlags.Instance,
                                    null,
                                    new[] { typeof(string) },
                                    null);
                                if (mi != null) mi.Invoke(gens[i], new object[] { plateStr });
                                else gens[i].SendMessage("GenerateRegPlate", plateStr, SendMessageOptions.DontRequireReceiver);
                            }
                            catch { }
                        }
                    }
                }

                if (regPlateFrontFsm != null) regPlateFrontFsm.SendEvent(installed ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
                if (regPlateRearFsm != null) regPlateRearFsm.SendEvent(installed ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
            }
            finally
            {
                isSuppressingPlates = false;
            }
        }

        private string GetCurrentPlateString()
        {
            RegPlateGen[] gens = satsumaGo != null
                ? satsumaGo.GetComponentsInChildren<RegPlateGen>(true)
                : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();
            if (gens != null && gens.Length > 0 && gens[0] != null && !string.IsNullOrEmpty(gens[0].PlateString))
            {
                return gens[0].PlateString;
            }
            return "VBX-403";
        }

        #endregion
    }
}
