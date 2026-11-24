using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.TimeWeather
{
    public sealed class TimeWeatherModule : IOscModule
    {
        public string ModuleId => "TimeWeather";
        public string DisplayName => "Время и Погода";
        public string StatusSummary
        {
            get
            {
                if (!isInitialized) return "Поиск солнца и облаков";
                int h = timeVar != null ? timeVar.Value : 0;
                float m = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
                int displayH = (h + Mathf.FloorToInt(m / 60f)) % 24;
                int displayM = Mathf.FloorToInt(m % 60f);
                return $"День {(dayVar != null ? dayVar.Value : 0)}, {displayH:D2}:{displayM:D2}";
            }
        }

        private OscHostContext ctx;
        private bool isInitialized;
        private float nextInitTime;
        private float nextBroadcastTime;

        private PlayMakerFSM sunColorFsm;
        private Transform sunPivot;
        private FsmInt timeVar;
        private FsmFloat sunMinutesVar;
        private FsmFloat timescaleVar;
        private FsmInt dayVar;

        private PlayMakerFSM weatherFsm;
        private FsmInt cloudIdVar;
        private FsmInt weatherTypeVar;
        private FsmFloat cloudOffsetVar;
        private FsmFloat cloudPosXVar;
        private FsmFloat cloudPosZVar;
        private FsmFloat cloudRotVar;

        private bool sunHooksInstalled;
        private bool clockHookInstalled;
        private bool weatherHooksInstalled;
        private bool clientLoadDisabled;

        private bool isLocalTime = true;
        private bool updatingMinutes;
        private bool hasInitialSync;

        private int lastSentDay = -1;
        private int lastSentTime = -1;
        private float lastSentMinutes = -999f;
        private int lastSentWeather = -1;
        private int lastSentCloudId = -1;
        private float lastSentTimescale = 1f;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            ctx.Router.Register(PacketType.TimeWeatherSync, ModuleId, HandlePacket);
            ctx.Transport.OnClientConnected += HandleClientConnected;
        }

        public void OnMenuLoaded() => ResetState();

        public void OnLevelLoaded()
        {
            ResetState();
            TryInitReferences();
        }

        public void OnShutdown()
        {
            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
            }
            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetState();
        }

        private void ResetState()
        {
            isInitialized = false;
            sunHooksInstalled = false;
            clockHookInstalled = false;
            weatherHooksInstalled = false;
            clientLoadDisabled = false;
            isLocalTime = true;
            updatingMinutes = false;
            hasInitialSync = false;

            sunColorFsm = null;
            sunPivot = null;
            timeVar = null;
            sunMinutesVar = null;
            timescaleVar = null;
            dayVar = null;
            weatherFsm = null;
            cloudIdVar = null;
            weatherTypeVar = null;
            cloudOffsetVar = null;
            cloudPosXVar = null;
            cloudPosZVar = null;
            cloudRotVar = null;

            lastSentDay = -1;
            lastSentTime = -1;
            lastSentMinutes = -999f;
            lastSentWeather = -1;
            lastSentCloudId = -1;
            lastSentTimescale = 1f;
        }

        public void OnUpdate()
        {
            if (!isInitialized && Time.time >= nextInitTime)
            {
                nextInitTime = Time.time + 1.5f;
                TryInitReferences();
            }

            if (!isInitialized || ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            if (ctx.Transport.IsClient && !clientLoadDisabled)
            {
                DisableClientLoadStates();
            }

            if (ctx.Transport.IsServer)
            {
                int curDay = dayVar != null ? dayVar.Value : 0;
                int curTime = timeVar != null ? timeVar.Value : 0;
                float curMin = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
                int curWeather = GetWeatherCode();
                int curCloud = cloudIdVar != null ? cloudIdVar.Value : 0;
                float curScale = timescaleVar != null ? timescaleVar.Value : 1f;

                bool changed = curDay != lastSentDay
                    || curTime != lastSentTime
                    || Mathf.Abs(curMin - lastSentMinutes) >= 0.1f
                    || curWeather != lastSentWeather
                    || curCloud != lastSentCloudId
                    || Mathf.Abs(curScale - lastSentTimescale) > 0.05f;

                if (changed || Time.time >= nextBroadcastTime)
                {
                    BroadcastState(0, false);
                }
            }
        }

        public void OnFixedUpdate() { }
        public void OnLateUpdate() { }

        private void TryInitReferences()
        {
            try
            {
                if (PlayMakerGlobals.Instance?.Variables != null)
                {
                    dayVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("GlobalDay");
                }

                GameObject sunObj = GameObject.Find("MAP/SUN/Pivot/SUN") ?? GameObject.Find("SUN");
                if (sunObj != null)
                {
                    sunPivot = sunObj.transform.parent;
                    var fsms = sunObj.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        var fsm = fsms[i];
                        if (fsm == null) continue;
                        fsm.InitializeSafe();
                        if (timescaleVar == null && fsm.FsmVariables != null)
                        {
                            timescaleVar = fsm.FsmVariables.FindFsmFloat("Timescale");
                        }
                        if (fsm.FsmName == "Color") sunColorFsm = fsm;
                    }

                    if (sunColorFsm != null && ctx.Ownership.TryClaim(sunColorFsm, ModuleId, "SUN Color FSM"))
                    {
                        sunColorFsm.InitializeSafe();
                        timeVar = sunColorFsm.FsmVariables.FindFsmInt("Time");
                        sunMinutesVar = sunColorFsm.FsmVariables.FindFsmFloat("Minutes");
                        InstallSunHooks();
                    }
                }

                InstallSuomiClockHook();

                GameObject cloudsObj = GameObject.Find("MAP/CloudSystem/Clouds") ?? GameObject.Find("Clouds");
                if (cloudsObj != null)
                {
                    weatherFsm = PlayMakerBridge.FindFsm(cloudsObj, "Weather") ?? cloudsObj.GetComponent<PlayMakerFSM>();
                    if (weatherFsm != null && ctx.Ownership.TryClaim(weatherFsm, ModuleId, "Weather FSM"))
                    {
                        weatherFsm.InitializeSafe();
                        cloudIdVar = weatherFsm.FsmVariables.FindFsmInt("WeatherCloudID");
                        weatherTypeVar = weatherFsm.FsmVariables.FindFsmInt("WeatherType");
                        cloudOffsetVar = weatherFsm.FsmVariables.FindFsmFloat("Offset");
                        cloudPosXVar = weatherFsm.FsmVariables.FindFsmFloat("PosX");
                        cloudPosZVar = weatherFsm.FsmVariables.FindFsmFloat("PosZ");
                        cloudRotVar = weatherFsm.FsmVariables.FindFsmFloat("Rotation");
                        InstallWeatherHooks();
                    }
                }

                DisableClientLoadStates();

                if (sunColorFsm != null && weatherFsm != null)
                {
                    isInitialized = true;
                    ctx.Log?.Invoke("[Время и Погода] Контроллеры времени 1:1 и погоды подключены.");
                }
            }
            catch (Exception ex)
            {
                ctx.Log?.Invoke($"[TimeWeather Init] {ex.Message}");
            }
        }

        private void InstallSunHooks()
        {
            if (sunHooksInstalled || sunColorFsm == null || sunMinutesVar == null) return;

            sunColorFsm.InsertAction("State 3", () =>
            {
                if (isLocalTime && ctx.Transport != null && ctx.Transport.IsServer && isInitialized)
                {
                    BroadcastState(0, true);
                }
                isLocalTime = true;
            }, 0);

            if (sunColorFsm.FsmStates != null)
            {
                int count = Mathf.Min(12, sunColorFsm.FsmStates.Length);
                for (int i = 0; i < count; i++)
                {
                    FsmState state = sunColorFsm.FsmStates[i];
                    if (state?.Actions == null) continue;
                    FsmStateAction[] actions = state.Actions;
                    bool modified = false;

                    for (int j = 0; j < actions.Length; j++)
                    {
                        if (actions[j] is SetFloatValue setFloat && setFloat.floatVariable == sunMinutesVar)
                        {
                            actions[j] = new PM_Hook(() =>
                            {
                                if (!updatingMinutes && sunMinutesVar != null)
                                {
                                    sunMinutesVar.Value = 0f;
                                }
                                updatingMinutes = false;
                            });
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        state.Actions = actions;
                    }
                }
            }

            sunHooksInstalled = true;
        }

        private void InstallSuomiClockHook()
        {
            if (clockHookInstalled) return;
            GameObject clockObj = GameObject.Find("YARD/Building/Dynamics/SuomiClock/Clock")
                ?? GameObject.Find("YARD")?.transform.Find("Building/Dynamics/SuomiClock/Clock")?.gameObject;
            if (clockObj == null) return;

            PlayMakerFSM clockFsm = PlayMakerBridge.FindFsm(clockObj, "Time");
            if (clockFsm == null || !ctx.Ownership.TryClaim(clockFsm, ModuleId, "SuomiClock FSM")) return;

            FsmState setTime = clockFsm.GetState("Set time");
            if (setTime?.Actions != null && setTime.Actions.Length > 1 &&
                setTime.Actions[1] is SetRotation setRot && setRot.gameObject?.GameObject != null)
            {
                GameObject needle = setRot.gameObject.GameObject.Value;
                if (needle != null)
                {
                    FsmStateAction[] actions = setTime.Actions;
                    actions[1] = new PM_Hook(() =>
                    {
                        if (sunMinutesVar == null) return;
                        float angle = (60f - (sunMinutesVar.Value % 60f)) / 60f * 360f;
                        needle.transform.localEulerAngles = Vector3.up * angle;
                    });
                    setTime.Actions = actions;
                    clockHookInstalled = true;
                }
            }
        }

        private void InstallWeatherHooks()
        {
            if (weatherHooksInstalled || weatherFsm == null) return;
            weatherFsm.AddSafeGlobalTransition("MP_UpdateWeather", "Set cloud");
            if (ctx.Transport.IsServer && weatherFsm.HasState("Move clouds"))
            {
                weatherFsm.InsertAction("Move clouds", () => BroadcastState(0, false), 0);
            }
            weatherHooksInstalled = true;
        }

        private void DisableClientLoadStates()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsClient) return;

            if (sunColorFsm != null)
            {
                FsmState load = sunColorFsm.GetState("Load");
                if (load?.Actions != null)
                {
                    for (int i = 0; i < load.Actions.Length; i++) load.Actions[i].Enabled = false;
                }
            }
            if (weatherFsm != null)
            {
                FsmState loadGame = weatherFsm.GetState("Load game");
                if (loadGame?.Actions != null)
                {
                    for (int i = 0; i < loadGame.Actions.Length; i++) loadGame.Actions[i].Enabled = false;
                }
            }
            if (sunColorFsm != null && weatherFsm != null)
            {
                clientLoadDisabled = true;
            }
        }

        private int GetWeatherCode()
        {
            if (weatherFsm == null) return 0;
            if (weatherFsm.ActiveStateName == "Rain") return 1;
            if (weatherFsm.ActiveStateName == "Thunder") return 2;
            return 0;
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx.Transport.IsServer && isInitialized)
            {
                BroadcastState(clientId, true);
            }
        }

        private void BroadcastState(int targetClientId, bool forceWakeup)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning || !ctx.Transport.IsServer) return;

            int curDay = dayVar != null ? dayVar.Value : 0;
            int curTime = timeVar != null ? timeVar.Value : 0;
            float curMin = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
            float curScale = timescaleVar != null ? timescaleVar.Value : 1f;
            float curSunRot = sunPivot != null ? sunPivot.localEulerAngles.y : 0f;
            int curWeather = GetWeatherCode();
            int curCloudId = cloudIdVar != null ? cloudIdVar.Value : 0;
            int curWType = weatherTypeVar != null ? weatherTypeVar.Value : 0;

            lastSentDay = curDay;
            lastSentTime = curTime;
            lastSentMinutes = curMin;
            lastSentWeather = curWeather;
            lastSentCloudId = curCloudId;
            lastSentTimescale = curScale;
            nextBroadcastTime = Time.time + 1.0f;

            using (var p = new PacketWriter(PacketType.TimeWeatherSync))
            {
                p.WriteInt32(curDay);
                p.WriteInt32(curTime);
                p.WriteSingle(curMin);
                p.WriteSingle(curSunRot);
                p.WriteSingle(curScale);
                p.WriteByte((byte)curWeather);
                p.WriteInt32(curCloudId);
                p.WriteInt32(curWType);
                p.WriteSingle(cloudOffsetVar != null ? cloudOffsetVar.Value : 0f);
                p.WriteSingle(cloudPosXVar != null ? cloudPosXVar.Value : 0f);
                p.WriteSingle(cloudPosZVar != null ? cloudPosZVar.Value : 0f);
                p.WriteSingle(cloudRotVar != null ? cloudRotVar.Value : 0f);
                p.WriteBoolean(forceWakeup);

                if (targetClientId > 0) ctx.Transport.SendTo(p, targetClientId);
                else ctx.Transport.Send(p);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (ctx.Transport == null || !ctx.Transport.IsClient) return;

            int day = reader.ReadInt32();
            int time = reader.ReadInt32();
            float sunMinutes = reader.ReadSingle();
            float sunRot = reader.ReadSingle();
            float timeScale = reader.ReadSingle();
            byte weatherCode = reader.ReadByte();
            int cloudId = reader.ReadInt32();
            int wType = reader.ReadInt32();
            float cOffset = reader.ReadSingle();
            float cPosX = reader.ReadSingle();
            float cPosZ = reader.ReadSingle();
            float cRot = reader.ReadSingle();
            bool forceWakeup = reader.ReadBoolean();

            DisableClientLoadStates();

            int prevDay = dayVar != null ? dayVar.Value : day;
            int prevTime = timeVar != null ? timeVar.Value : time;
            float prevMin = sunMinutesVar != null ? sunMinutesVar.Value : sunMinutes;

            if (dayVar != null) dayVar.Value = day;
            if (timeVar != null) timeVar.Value = time;
            if (sunMinutesVar != null) sunMinutesVar.Value = sunMinutes;
            if (timescaleVar != null) timescaleVar.Value = timeScale;

            if (sunPivot != null)
            {
                Vector3 e = sunPivot.localEulerAngles;
                sunPivot.localEulerAngles = new Vector3(e.x, sunRot, e.z);
            }

            bool majorJump = forceWakeup || !hasInitialSync || prevDay != day || prevTime != time || Mathf.Abs(prevMin - sunMinutes) > 1.0f;
            if (sunColorFsm != null && majorJump)
            {
                hasInitialSync = true;
                updatingMinutes = true;
                isLocalTime = false;
                sunColorFsm.SendEvent("WAKEUP");
            }

            if (weatherFsm != null)
            {
                bool cloudChanged = cloudIdVar != null && cloudIdVar.Value != cloudId;
                if (cloudIdVar != null) cloudIdVar.Value = cloudId;
                if (weatherTypeVar != null) weatherTypeVar.Value = wType;
                if (cloudOffsetVar != null) cloudOffsetVar.Value = cOffset;
                if (cloudPosXVar != null) cloudPosXVar.Value = cPosX;
                if (cloudPosZVar != null) cloudPosZVar.Value = cPosZ;
                if (cloudRotVar != null)
                {
                    cloudRotVar.Value = cRot;
                    weatherFsm.transform.eulerAngles = Vector3.up * cRot;
                }

                if (cloudChanged)
                {
                    weatherFsm.SendEvent("MP_UpdateWeather");
                }

                string curState = weatherFsm.ActiveStateName;
                if (weatherCode == 1 && curState != "Rain") weatherFsm.SendEvent("RAIN");
                else if (weatherCode == 2 && curState != "Thunder") weatherFsm.SendEvent("THUNDER");
                else if (weatherCode == 0 && (curState == "Rain" || curState == "Thunder")) weatherFsm.SendEvent("NORAIN");
            }
        }
    }
}
