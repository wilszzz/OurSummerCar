using System;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Boat
{
    internal sealed class BoatMotorSync
    {
        private readonly OscHostContext ctx;
        private readonly string moduleId;

        private GameObject boatGo;
        private Transform boatTransform;
        private Rigidbody boatRb;
        private Rigidbody gfxRb;

        private PlayMakerFSM motorFsm;
        private PlayMakerFSM ignitionFsm;
        private PlayMakerFSM gearFsm;
        private PlayMakerFSM shutOffFsm;
        private PlayMakerFSM throttleFsm;

        private FsmFloat throttleVar;
        private Transform tillerTransform;
        private bool tillerRotatesAroundY = true;

        private string yankStateName;
        private string runStateName;

        private bool suppressMotorFsm;
        private bool suppressIgnitionYank;
        private bool suppressIgnitionStart;
        private bool suppressGear;
        private bool suppressShutOff;

        private bool isEngineRunning;
        private byte currentGear;
        private float currentThrottle;
        private float currentTillerAngle;

        private bool hasRemoteContinuous;
        private float remoteTargetThrottle;
        private float remoteTargetTillerAngle;
        private float lastContinuousRecvTime;
        private float teleportGuardUntil;
        private float lastContinuousSendTime;
        private float lastSentThrottle = -999f;
        private float lastSentTiller = -999f;

        public GameObject BoatObject => boatGo;
        public Transform BoatTransform => boatTransform;
        public Rigidbody BoatRigidbody => boatRb;
        public Rigidbody GfxRigidbody => gfxRb;
        public bool IsEngineRunning => isEngineRunning;
        public byte CurrentGear => currentGear;
        public float CurrentThrottle => currentThrottle;
        public float CurrentTillerAngle => currentTillerAngle;

        public event Action OnEngineShutOff;

        public BoatMotorSync(OscHostContext context, string ownerModuleId)
        {
            ctx = context;
            moduleId = ownerModuleId;
        }

        public void Reset()
        {
            boatGo = null;
            boatTransform = null;
            boatRb = null;
            gfxRb = null;

            motorFsm = null;
            ignitionFsm = null;
            gearFsm = null;
            shutOffFsm = null;
            throttleFsm = null;
            throttleVar = null;
            tillerTransform = null;
            tillerRotatesAroundY = true;

            yankStateName = null;
            runStateName = null;

            suppressMotorFsm = false;
            suppressIgnitionYank = false;
            suppressIgnitionStart = false;
            suppressGear = false;
            suppressShutOff = false;

            isEngineRunning = false;
            currentGear = (byte)BoatGearState.Neutral;
            currentThrottle = 0f;
            currentTillerAngle = 0f;

            hasRemoteContinuous = false;
            remoteTargetThrottle = 0f;
            remoteTargetTillerAngle = 0f;
            lastContinuousRecvTime = 0f;
            teleportGuardUntil = 0f;
            lastContinuousSendTime = 0f;
            lastSentThrottle = -999f;
            lastSentTiller = -999f;
        }

        public bool IsBoatEvent(int vehicleId, Transform vehicleTransform)
        {
            if (vehicleId == BoatConstants.BoatVehicleId) return true;
            return boatTransform != null && vehicleTransform == boatTransform;
        }

        public bool DiscoverAndHook()
        {
            if (boatGo == null)
            {
                boatGo = BoatHelpers.FindBoatObject();
                if (boatGo == null) return false;

                boatTransform = boatGo.transform;
                boatRb = boatGo.GetComponent<Rigidbody>();
                Transform gfx = boatTransform.Find("GFX");
                if (gfx != null)
                {
                    gfxRb = gfx.GetComponent<Rigidbody>();
                }
            }

            ResolveTillerTransform();
            HookLegacyMotorFsm();
            HookIgnitionFsm();
            HookGearFsm();
            HookShutOffFsm();
            DiscoverThrottleVariable();
            return true;
        }

        public void HandleTeleport(VehicleTeleportEvent evt)
        {
            if (!IsBoatEvent(evt.VehicleId, evt.VehicleTransform)) return;

            teleportGuardUntil = Time.time + BoatConstants.TeleportGuardDuration;
            hasRemoteContinuous = false;

            if (tillerTransform != null)
            {
                Vector3 e = tillerTransform.localEulerAngles;
                if (tillerRotatesAroundY)
                {
                    tillerTransform.localEulerAngles = new Vector3(e.x, remoteTargetTillerAngle, e.z);
                }
                else
                {
                    tillerTransform.localEulerAngles = new Vector3(e.x, e.y, remoteTargetTillerAngle);
                }
            }

            if (boatRb != null && !boatRb.isKinematic)
            {
                boatRb.velocity = Vector3.zero;
                boatRb.angularVelocity = Vector3.zero;
            }
            if (gfxRb != null && !gfxRb.isKinematic)
            {
                gfxRb.velocity = Vector3.zero;
                gfxRb.angularVelocity = Vector3.zero;
            }
        }

        public void HandleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            if (!IsBoatEvent(evt.VehicleId, evt.VehicleTransform)) return;
            if (!evt.IsDrivenRemotely || Time.time < teleportGuardUntil) return;
            // ContinuousMotorAndTiller переносит реальный Throttle FSM и Y-румпель; VehicleControlSync шлёт 0f без CarController
            if (Time.time - lastContinuousRecvTime < 0.5f) return;

            remoteTargetTillerAngle = Mathf.Clamp(BoatHelpers.NormalizeAngle(evt.SteerAngle), -55f, 55f);
            hasRemoteContinuous = true;
        }

        public void TickUpdate(bool isLocalDriver, bool isRemoteDriver, bool isLocalPhysicsOwner)
        {
            if (boatGo == null) return;

            RefreshLocalRunningState();

            bool canSendControls = isLocalDriver || (isLocalPhysicsOwner && isEngineRunning && !isRemoteDriver);
            if (canSendControls)
            {
                currentThrottle = ReadLocalThrottle();
                currentTillerAngle = ReadLocalTillerAngle();

                if (Time.time - lastContinuousSendTime >= BoatConstants.ContinuousSendInterval)
                {
                    bool changed = Mathf.Abs(currentThrottle - lastSentThrottle) > 0.015f
                        || Mathf.Abs(Mathf.DeltaAngle(currentTillerAngle, lastSentTiller)) > 0.6f;

                    if (changed || Time.time - lastContinuousSendTime >= 0.25f)
                    {
                        lastContinuousSendTime = Time.time;
                        lastSentThrottle = currentThrottle;
                        lastSentTiller = currentTillerAngle;
                        SendContinuousMotorAndTiller();
                    }
                }
            }
            else if (hasRemoteContinuous && !isLocalDriver)
            {
                ApplyRemoteThrottleAndTiller(Time.deltaTime);
            }
        }

        public void NeutralizeUnmannedThrottleAndGear()
        {
            if (boatGo == null) return;

            if (throttleVar != null && Mathf.Abs(throttleVar.Value) > 0.01f)
            {
                throttleVar.Value = 0f;
            }
            currentThrottle = 0f;
            remoteTargetThrottle = 0f;

            if (currentGear != (byte)BoatGearState.Neutral && gearFsm != null && gearFsm.HasState("Neutral"))
            {
                currentGear = (byte)BoatGearState.Neutral;
                if (gearFsm.gameObject.activeInHierarchy && gearFsm.enabled && gearFsm.ActiveStateName != "Neutral")
                {
                    suppressGear = true;
                    try
                    {
                        gearFsm.SendEvent("MP_GEAR_NEUTRAL");
                    }
                    finally
                    {
                        suppressGear = false;
                    }
                }
                SendSubtype(BoatEngineSubPacket.GearShift, (byte)BoatGearState.Neutral, 0f);
            }

            SendContinuousMotorAndTiller();
            ctx?.Log?.Invoke("[Boat/R5] Беспилотный газ/передача лодки переведены в нейтраль для безопасности пассажира");
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            BoatEngineSubPacket sub = (BoatEngineSubPacket)reader.ReadByte();

            switch (sub)
            {
                case BoatEngineSubPacket.LegacyMotor:
                {
                    bool running = reader.ReadBoolean();
                    float throttle = reader.UnreadBytes >= 4 ? reader.ReadSingle() : 0f;
                    ApplyRemoteLegacyMotor(running, throttle);
                    break;
                }
                case BoatEngineSubPacket.StarterIgnition:
                {
                    byte val = reader.ReadByte();
                    if (reader.UnreadBytes >= 4) reader.ReadSingle();
                    ApplyRemoteStarterIgnition(val);
                    break;
                }
                case BoatEngineSubPacket.GearShift:
                {
                    byte gearVal = reader.ReadByte();
                    if (reader.UnreadBytes >= 4) reader.ReadSingle();
                    ApplyRemoteGear(gearVal);
                    break;
                }
                case BoatEngineSubPacket.ShutOff:
                {
                    if (reader.UnreadBytes >= 1) reader.ReadByte();
                    if (reader.UnreadBytes >= 4) reader.ReadSingle();
                    ApplyRemoteShutOff();
                    break;
                }
                case BoatEngineSubPacket.ContinuousMotorAndTiller:
                {
                    int senderId = reader.ReadInt32();
                    int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
                    if (senderId == localId) return;

                    bool running = reader.ReadBoolean();
                    byte gear = reader.ReadByte();
                    float throttle = reader.ReadSingle();
                    float tillerAngle = reader.ReadSingle();
                    ApplyRemoteContinuous(running, gear, throttle, tillerAngle);
                    break;
                }
                case BoatEngineSubPacket.FullEngineSnapshot:
                {
                    bool running = reader.ReadBoolean();
                    byte gear = reader.ReadByte();
                    float throttle = reader.ReadSingle();
                    float tillerAngle = reader.ReadSingle();
                    ApplyRemoteSnapshot(running, gear, throttle, tillerAngle);
                    break;
                }
            }
        }

        public void SendSnapshotTo(int targetClientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning || !ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.BoatEngineSync))
            {
                p.WriteByte((byte)BoatEngineSubPacket.FullEngineSnapshot);
                p.WriteBoolean(isEngineRunning);
                p.WriteByte(currentGear);
                p.WriteSingle(ReadLocalThrottle());
                p.WriteSingle(ReadLocalTillerAngle());
                ctx.Transport.SendTo(p, targetClientId);
            }
        }

        #region Discovery & FSM Hooking

        private void ResolveTillerTransform()
        {
            if (tillerTransform != null || boatTransform == null) return;

            tillerTransform = BoatHelpers.FindFirstPath(boatTransform, BoatConstants.TillerPaths);
            if (tillerTransform == null)
            {
                for (int i = 0; i < BoatConstants.TillerFallbackNames.Length; i++)
                {
                    tillerTransform = PlayMakerBridge.FindChildRecursive(boatTransform, BoatConstants.TillerFallbackNames[i]);
                    if (tillerTransform != null) break;
                }
            }

            if (tillerTransform != null)
            {
                // Для лодки MSC румпель обычно вращается вокруг Y (или Z, если ось смещена)
                Vector3 e = tillerTransform.localEulerAngles;
                float ay = Mathf.Abs(BoatHelpers.NormalizeAngle(e.y));
                float az = Mathf.Abs(BoatHelpers.NormalizeAngle(e.z));
                tillerRotatesAroundY = ay >= az || az < 1f;
            }
        }

        private void HookLegacyMotorFsm()
        {
            if (motorFsm != null || boatGo == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(boatGo, "Motor")
                ?? PlayMakerBridge.FindFsm(boatGo, "StartEngine");
            if (fsm == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, "Boat_LegacyMotor")) return;

            fsm.InitializeSafe();
            motorFsm = fsm;
            if (throttleVar == null)
            {
                throttleVar = fsm.FsmVariables.FindFsmFloat("Throttle");
            }

            if (fsm.HasState("Start"))
            {
                fsm.InsertAction("Start", () =>
                {
                    if (suppressMotorFsm) return;
                    isEngineRunning = true;
                    float thr = throttleVar != null ? throttleVar.Value : 0f;
                    SendLegacyMotor(true, thr);
                }, 0, true);
            }
        }

        private void HookIgnitionFsm()
        {
            if (ignitionFsm != null || boatTransform == null) return;

            Transform ignTr = boatTransform.Find("GFX/Motor/Pivot/Ignition");
            if (ignTr == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(ignTr.gameObject, "Jank")
                ?? ignTr.GetComponent<PlayMakerFSM>();
            if (fsm == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, "Boat_Ignition")) return;

            fsm.InitializeSafe();
            ignitionFsm = fsm;
            string startState = fsm.Fsm != null ? fsm.Fsm.StartState : string.Empty;

            string[] yankCandidates = { "Yank", "Jank", "Pull", "Pull rope", "State 1", "State 2" };
            for (int i = 0; i < yankCandidates.Length; i++)
            {
                string s = yankCandidates[i];
                if (s != startState && fsm.HasState(s))
                {
                    yankStateName = s;
                    break;
                }
            }

            string[] runCandidates = { "Running", "Start", "Motor running", "Engine running", "Started", "ON", "On" };
            for (int i = 0; i < runCandidates.Length; i++)
            {
                string s = runCandidates[i];
                if (s != startState && s != yankStateName && fsm.HasState(s))
                {
                    runStateName = s;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(yankStateName))
            {
                fsm.AddSafeGlobalTransition("MP_BOAT_YANK", yankStateName);
                fsm.InsertAction(yankStateName, () =>
                {
                    if (suppressIgnitionYank)
                    {
                        suppressIgnitionYank = false;
                        return;
                    }
                    SendSubtype(BoatEngineSubPacket.StarterIgnition, 0, 0f);
                }, 0, true);
            }

            if (!string.IsNullOrEmpty(runStateName))
            {
                fsm.AddSafeGlobalTransition("MP_BOAT_START", runStateName);
                fsm.InsertAction(runStateName, () =>
                {
                    isEngineRunning = true;
                    if (suppressIgnitionStart)
                    {
                        suppressIgnitionStart = false;
                        return;
                    }
                    SendSubtype(BoatEngineSubPacket.StarterIgnition, 1, ReadLocalThrottle());
                }, 0, true);
            }
        }

        private void HookGearFsm()
        {
            if (gearFsm != null || boatTransform == null) return;

            Transform gearTr = boatTransform.Find("GFX/Motor/Pivot/Gear");
            if (gearTr == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(gearTr.gameObject, "Use")
                ?? gearTr.GetComponent<PlayMakerFSM>();
            if (fsm == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, "Boat_Gear")) return;

            fsm.InitializeSafe();
            gearFsm = fsm;

            string startState = fsm.Fsm != null ? fsm.Fsm.StartState : string.Empty;
            if (startState == "First") currentGear = (byte)BoatGearState.First;
            else if (startState == "Reverse") currentGear = (byte)BoatGearState.Reverse;
            else currentGear = (byte)BoatGearState.Neutral;

            HookGearState(fsm, "Neutral", "MP_GEAR_NEUTRAL", (byte)BoatGearState.Neutral);
            HookGearState(fsm, "First", "MP_GEAR_FIRST", (byte)BoatGearState.First);
            HookGearState(fsm, "Reverse", "MP_GEAR_REVERSE", (byte)BoatGearState.Reverse);
        }

        private void HookGearState(PlayMakerFSM fsm, string stateName, string mpEvent, byte gearByte)
        {
            if (!fsm.HasState(stateName)) return;

            fsm.AddSafeGlobalTransition(mpEvent, stateName);
            fsm.InsertAction(stateName, () =>
            {
                if (suppressGear)
                {
                    suppressGear = false;
                    return;
                }
                if (currentGear != gearByte)
                {
                    currentGear = gearByte;
                    SendSubtype(BoatEngineSubPacket.GearShift, gearByte, ReadLocalThrottle());
                }
            }, 0, true);
        }

        private void HookShutOffFsm()
        {
            if (shutOffFsm != null || boatTransform == null) return;

            Transform shutTr = boatTransform.Find("GFX/Motor/Pivot/ShutOff");
            if (shutTr == null) return;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(shutTr.gameObject, "Use")
                ?? shutTr.GetComponent<PlayMakerFSM>();
            if (fsm == null) return;
            if (!ctx.Ownership.TryClaim(fsm, moduleId, "Boat_ShutOff")) return;

            fsm.InitializeSafe();
            shutOffFsm = fsm;

            if (fsm.HasState("Shut Off"))
            {
                fsm.AddSafeGlobalTransition("MP_BOAT_SHUTOFF", "Shut Off");
                fsm.InsertAction("Shut Off", () =>
                {
                    isEngineRunning = false;
                    SetIgnitionRunningBools(false);

                    if (suppressShutOff)
                    {
                        suppressShutOff = false;
                        return;
                    }

                    OnEngineShutOff?.Invoke();
                    SendSubtype(BoatEngineSubPacket.ShutOff, 1, 0f);
                }, 0, true);
            }
        }

        private void DiscoverThrottleVariable()
        {
            if (throttleVar != null || boatTransform == null) return;

            Transform thrTr = boatTransform.Find("GFX/Motor/Pivot/Throttle")
                ?? boatTransform.Find("GFX/Motor/Pivot");
            if (thrTr != null)
            {
                var fsms = thrTr.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < fsms.Length; i++)
                {
                    PlayMakerFSM f = fsms[i];
                    if (f == null) continue;
                    f.InitializeSafe();
                    FsmFloat found = f.FsmVariables.FindFsmFloat("Throttle")
                        ?? f.FsmVariables.FindFsmFloat("Gas")
                        ?? f.FsmVariables.FindFsmFloat("RPM");
                    if (found != null)
                    {
                        throttleFsm = f;
                        throttleVar = found;
                        return;
                    }
                }
            }
        }

        #endregion

        #region State Helpers & Remote Application

        private void RefreshLocalRunningState()
        {
            if (ignitionFsm == null) return;

            string[] boolNames = { "Running", "MotorOn", "EngineOn", "On" };
            for (int i = 0; i < boolNames.Length; i++)
            {
                FsmBool b = ignitionFsm.FsmVariables.FindFsmBool(boolNames[i]);
                if (b != null)
                {
                    isEngineRunning = b.Value;
                    return;
                }
            }
        }

        private void SetIgnitionRunningBools(bool running)
        {
            if (ignitionFsm == null) return;
            string[] boolNames = { "Running", "MotorOn", "EngineOn", "On" };
            for (int i = 0; i < boolNames.Length; i++)
            {
                FsmBool b = ignitionFsm.FsmVariables.FindFsmBool(boolNames[i]);
                if (b != null) b.Value = running;
            }
        }

        private float ReadLocalThrottle()
        {
            if (throttleVar != null) return throttleVar.Value;
            if (throttleFsm != null)
            {
                FsmFloat v = throttleFsm.FsmVariables.FindFsmFloat("Throttle");
                if (v != null) return v.Value;
            }
            return currentThrottle;
        }

        private float ReadLocalTillerAngle()
        {
            if (tillerTransform == null) return 0f;
            Vector3 e = tillerTransform.localEulerAngles;
            return BoatHelpers.NormalizeAngle(tillerRotatesAroundY ? e.y : e.z);
        }

        private void ApplyRemoteLegacyMotor(bool running, float throttle)
        {
            isEngineRunning = running;
            remoteTargetThrottle = throttle;
            if (motorFsm == null) return;

            suppressMotorFsm = true;
            try
            {
                if (throttleVar != null) throttleVar.Value = throttle;
                motorFsm.SendEvent(running ? "START" : "STOP");
            }
            finally
            {
                suppressMotorFsm = false;
            }
        }

        private void ApplyRemoteStarterIgnition(byte val)
        {
            if (ignitionFsm == null) return;

            if (val == 0 && !string.IsNullOrEmpty(yankStateName))
            {
                suppressIgnitionYank = true;
                ignitionFsm.SendEvent("MP_BOAT_YANK");
            }
            else if (val == 1)
            {
                isEngineRunning = true;
                SetIgnitionRunningBools(true);
                if (!string.IsNullOrEmpty(runStateName))
                {
                    suppressIgnitionStart = true;
                    ignitionFsm.SendEvent("MP_BOAT_START");
                }
            }
        }

        private void ApplyRemoteGear(byte gearVal)
        {
            currentGear = gearVal;
            if (gearFsm == null) return;

            string stateName = gearVal == (byte)BoatGearState.First
                ? "First"
                : (gearVal == (byte)BoatGearState.Reverse ? "Reverse" : "Neutral");
            string ev = gearVal == (byte)BoatGearState.First
                ? "MP_GEAR_FIRST"
                : (gearVal == (byte)BoatGearState.Reverse ? "MP_GEAR_REVERSE" : "MP_GEAR_NEUTRAL");

            if (!gearFsm.gameObject.activeInHierarchy || !gearFsm.enabled || !gearFsm.HasState(stateName))
            {
                return;
            }
            if (gearFsm.ActiveStateName == stateName)
            {
                return;
            }

            suppressGear = true;
            try
            {
                gearFsm.SendEvent(ev);
            }
            finally
            {
                suppressGear = false;
            }
        }

        private void ApplyRemoteShutOff()
        {
            isEngineRunning = false;
            remoteTargetThrottle = 0f;
            SetIgnitionRunningBools(false);

            if (shutOffFsm != null && shutOffFsm.HasState("Shut Off"))
            {
                suppressShutOff = true;
                shutOffFsm.SendEvent("MP_BOAT_SHUTOFF");
            }

            OnEngineShutOff?.Invoke();
        }

        private void ApplyRemoteContinuous(bool running, byte gear, float throttle, float tillerAngle)
        {
            if (running != isEngineRunning)
            {
                if (running) ApplyRemoteStarterIgnition(1);
                else ApplyRemoteShutOff();
            }

            if (gear != currentGear)
            {
                ApplyRemoteGear(gear);
            }

            remoteTargetThrottle = throttle;
            remoteTargetTillerAngle = Mathf.Clamp(BoatHelpers.NormalizeAngle(tillerAngle), -55f, 55f);
            hasRemoteContinuous = true;
            lastContinuousRecvTime = Time.time;
        }

        private void ApplyRemoteSnapshot(bool running, byte gear, float throttle, float tillerAngle)
        {
            ApplyRemoteContinuous(running, gear, throttle, tillerAngle);
            if (tillerTransform != null)
            {
                Vector3 e = tillerTransform.localEulerAngles;
                if (tillerRotatesAroundY)
                {
                    tillerTransform.localEulerAngles = new Vector3(e.x, remoteTargetTillerAngle, e.z);
                }
                else
                {
                    tillerTransform.localEulerAngles = new Vector3(e.x, e.y, remoteTargetTillerAngle);
                }
            }
            if (throttleVar != null)
            {
                throttleVar.Value = remoteTargetThrottle;
            }
        }

        private void ApplyRemoteThrottleAndTiller(float dt)
        {
            bool instantSnap = Time.time < teleportGuardUntil;
            float lerpT = instantSnap ? 1f : Mathf.Clamp01(dt * 18f);

            if (throttleVar != null)
            {
                throttleVar.Value = Mathf.Lerp(throttleVar.Value, remoteTargetThrottle, lerpT);
                currentThrottle = throttleVar.Value;
            }

            if (tillerTransform != null)
            {
                Vector3 e = tillerTransform.localEulerAngles;
                if (tillerRotatesAroundY)
                {
                    float nextY = instantSnap ? remoteTargetTillerAngle : Mathf.LerpAngle(e.y, remoteTargetTillerAngle, lerpT);
                    tillerTransform.localEulerAngles = new Vector3(e.x, nextY, e.z);
                }
                else
                {
                    float nextZ = instantSnap ? remoteTargetTillerAngle : Mathf.LerpAngle(e.z, remoteTargetTillerAngle, lerpT);
                    tillerTransform.localEulerAngles = new Vector3(e.x, e.y, nextZ);
                }
                currentTillerAngle = ReadLocalTillerAngle();
            }
        }

        #endregion

        #region Network Senders

        private void SendLegacyMotor(bool running, float throttle)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.BoatEngineSync))
            {
                p.WriteByte((byte)BoatEngineSubPacket.LegacyMotor);
                p.WriteBoolean(running);
                p.WriteSingle(throttle);
                ctx.Transport.Send(p);
            }
        }

        private void SendSubtype(BoatEngineSubPacket sub, byte value, float throttle)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.BoatEngineSync))
            {
                p.WriteByte((byte)sub);
                p.WriteByte(value);
                p.WriteSingle(throttle);
                ctx.Transport.Send(p);
            }
        }

        private void SendContinuousMotorAndTiller()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.BoatEngineSync))
            {
                p.WriteByte((byte)BoatEngineSubPacket.ContinuousMotorAndTiller);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(isEngineRunning);
                p.WriteByte(currentGear);
                p.WriteSingle(currentThrottle);
                p.WriteSingle(currentTillerAngle);
                ctx.Transport.Send(p);
            }
        }

        #endregion
    }
}
