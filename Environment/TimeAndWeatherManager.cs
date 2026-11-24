using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    public class TimeAndWeatherManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        private PlayMakerFSM sunColorFsm;
        private Transform sunPivot;
        private FsmInt timeVar;
        private FsmFloat sunMinutesVar;
        private FsmFloat timescaleVar;
        private FsmInt dayVar;

        private PlayMakerFSM weatherFsm;
        private FsmInt cloudIdVar;

        private bool isInitialized = false;
        private bool sunHooksInstalled = false;
        private bool clockHookInstalled = false;
        private bool sunLoadDisabled = false;
        private bool weatherLoadDisabled = false;
        private bool hasAppliedInitialSync = false;
        private bool isLocalTime = true;
        private bool updatingMinutes = false;

        private float nextSearchTime = 0f;
        private float nextBroadcastTime = 0f;

        private int lastSentTime = -1;
        private float lastSentSunMinutes = -1f;
        private int lastSentDay = -1;
        private int lastSentWeather = -1;
        private float lastSentSunRot = -999f;
        private float lastSentTimeScale = 1.0f;

        public void Reset()
        {
            isInitialized = false;
            sunHooksInstalled = false;
            clockHookInstalled = false;
            sunLoadDisabled = false;
            weatherLoadDisabled = false;
            hasAppliedInitialSync = false;
            isLocalTime = true;
            updatingMinutes = false;
            sunColorFsm = null;
            sunPivot = null;
            timeVar = null;
            sunMinutesVar = null;
            timescaleVar = null;
            dayVar = null;
            weatherFsm = null;
            cloudIdVar = null;
            lastSentTime = -1;
            lastSentSunMinutes = -1f;
            lastSentDay = -1;
            lastSentWeather = -1;
            lastSentSunRot = -999f;
            lastSentTimeScale = 1.0f;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            TryInitReferences();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        private void Update()
        {
            if (!isInitialized && Time.time >= nextSearchTime)
            {
                nextSearchTime = Time.time + 2.0f;
                TryInitReferences();
            }

            if (!isInitialized || transport == null || !transport.IsRunning) return;

            if (transport.IsClient && (!sunLoadDisabled || !weatherLoadDisabled))
            {
                DisableClientLoadActions();
            }

            // Хост является единственным и абсолютным мастером времени и погоды в мире
            if (transport.IsServer)
            {
                int curTime = timeVar != null ? timeVar.Value : -1;
                float curSunMinutes = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
                int curDay = dayVar != null ? dayVar.Value : -1;
                int curWeather = GetCurrentWeatherCode();
                float curSunRot = sunPivot != null ? sunPivot.localEulerAngles.y : 0f;
                float curScale = timescaleVar != null ? timescaleVar.Value : 1.0f;

                bool timeChanged = curTime != lastSentTime;
                bool minChanged = Math.Abs(curSunMinutes - lastSentSunMinutes) > 1.0f;
                bool dayChanged = curDay != lastSentDay;
                bool weatherChanged = curWeather != lastSentWeather;
                bool scaleChanged = Math.Abs(curScale - lastSentTimeScale) > 0.05f;
                bool sunAngleJumped = (lastSentSunRot > -500f) && Math.Abs(Mathf.DeltaAngle(curSunRot, lastSentSunRot)) > 4.0f;

                if (timeChanged || minChanged || dayChanged || weatherChanged || scaleChanged || sunAngleJumped || Time.time >= nextBroadcastTime)
                {
                    nextBroadcastTime = Time.time + 4.0f;
                    BroadcastTimeWeather(curDay, curTime, curSunMinutes, curWeather, curSunRot, curScale);
                }
            }
        }

        private void TryInitReferences()
        {
            try
            {
                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    dayVar = PlayMakerGlobals.Instance.Variables.FindFsmInt("GlobalDay");
                }

                // 1. Солнце и игровое время (Color FSM: Time = 0,2..22, Minutes = 0..120)
                var sunObj = GameObject.Find("MAP/SUN/Pivot/SUN") ?? GameObject.Find("SUN");
                if (sunObj != null)
                {
                    sunPivot = sunObj.transform.parent;
                    var fsms = sunObj.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        var fsm = fsms[i];
                        fsm.Initialize();
                        if (fsm.FsmVariables != null && timescaleVar == null)
                        {
                            timescaleVar = fsm.FsmVariables.FindFsmFloat("Timescale");
                        }
                        if (fsm.FsmName == "Color") sunColorFsm = fsm;
                    }

                    if (sunColorFsm != null)
                    {
                        sunColorFsm.Initialize();
                        if (sunColorFsm.FsmVariables != null)
                        {
                            timeVar = sunColorFsm.FsmVariables.FindFsmInt("Time");
                            sunMinutesVar = sunColorFsm.FsmVariables.FindFsmFloat("Minutes");
                        }

                        InstallSunColorHooks();
                    }
                }

                // 2. Минутная стрелка домашних часов SuomiClock (0..60 из sunMinutesVar % 60f)
                var clockObj = GameObject.Find("YARD/Building/Dynamics/SuomiClock/Clock")
                    ?? GameObject.Find("YARD")?.transform.Find("Building/Dynamics/SuomiClock/Clock")?.gameObject;
                if (clockObj != null && !clockHookInstalled)
                {
                    var clockFsm = PlayMakerUtilities.FindFsmOnGameObject(clockObj, "Time");
                    if (clockFsm != null)
                    {
                        clockFsm.Initialize();
                        FsmState setTimeState = clockFsm.GetState("Set time");
                        if (setTimeState != null && setTimeState.Actions != null && setTimeState.Actions.Length > 1)
                        {
                            FsmStateAction[] actions = setTimeState.Actions;
                            if (actions[1] is SetRotation setRotation && setRotation.gameObject != null && setRotation.gameObject.GameObject != null)
                            {
                                GameObject needle = setRotation.gameObject.GameObject.Value;
                                if (needle != null)
                                {
                                    actions[1] = new PM_Hook(() =>
                                    {
                                        if (sunMinutesVar == null) return;
                                        float angle = (60f - (sunMinutesVar.Value % 60f)) / 60f * 360f;
                                        needle.transform.localEulerAngles = Vector3.up * angle;
                                    });
                                    setTimeState.Actions = actions;
                                    clockHookInstalled = true;
                                }
                            }
                        }
                    }
                }

                // 3. Система облаков и погоды
                var cloudsObj = GameObject.Find("MAP/CloudSystem/Clouds") ?? GameObject.Find("Clouds");
                if (cloudsObj != null)
                {
                    weatherFsm = PlayMakerUtilities.FindFsmOnGameObject(cloudsObj, "Weather") ?? cloudsObj.GetComponent<PlayMakerFSM>();
                    if (weatherFsm != null)
                    {
                        weatherFsm.Initialize();
                        cloudIdVar = weatherFsm.FsmVariables.FindFsmInt("WeatherCloudID");
                    }
                }

                DisableClientLoadActions();

                if (sunObj != null && cloudsObj != null)
                {
                    isInitialized = true;
                    OnLog?.Invoke("[Погода и Время] Контроллеры солнца, времени и погоды успешно подключены.");
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[TimeAndWeatherManager Init Error] {ex.Message}");
            }
        }

        private void InstallSunColorHooks()
        {
            if (sunHooksInstalled || sunColorFsm == null || sunMinutesVar == null) return;

            sunColorFsm.InsertAction("State 3", () =>
            {
                if (isLocalTime && transport != null && transport.IsServer && isInitialized)
                {
                    BroadcastCurrentTimeWeather();
                }
                isLocalTime = true;
            }, 0);

            if (sunColorFsm.FsmStates != null)
            {
                int stateCount = Mathf.Min(12, sunColorFsm.FsmStates.Length);
                for (int i = 0; i < stateCount; i++)
                {
                    FsmState fsmState = sunColorFsm.FsmStates[i];
                    if (fsmState == null || fsmState.Actions == null) continue;

                    FsmStateAction[] actions = fsmState.Actions;
                    bool modified = false;
                    for (int j = 0; j < actions.Length; j++)
                    {
                        if (actions[j] is SetFloatValue setFloatValue && setFloatValue.floatVariable == sunMinutesVar)
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
                        fsmState.Actions = actions;
                    }
                }
            }

            sunHooksInstalled = true;
        }

        private void DisableClientLoadActions()
        {
            if (transport == null || !transport.IsClient) return;

            if (!sunLoadDisabled && sunColorFsm != null)
            {
                FsmState loadState = sunColorFsm.GetState("Load");
                if (loadState != null && loadState.Actions != null)
                {
                    for (int i = 0; i < loadState.Actions.Length; i++)
                    {
                        loadState.Actions[i].Enabled = false;
                    }
                    sunLoadDisabled = true;
                }
            }

            if (!weatherLoadDisabled && weatherFsm != null)
            {
                FsmState loadGameState = weatherFsm.GetState("Load game");
                if (loadGameState != null && loadGameState.Actions != null)
                {
                    for (int i = 0; i < loadGameState.Actions.Length; i++)
                    {
                        loadGameState.Actions[i].Enabled = false;
                    }
                    weatherLoadDisabled = true;
                }
            }
        }

        private int GetCurrentWeatherCode()
        {
            if (weatherFsm == null) return 0;
            string state = weatherFsm.ActiveStateName;
            if (state == "Rain") return 1;
            if (state == "Thunder") return 2;
            return 0; // No weather / Clear
        }

        private void BroadcastCurrentTimeWeather()
        {
            int curTime = timeVar != null ? timeVar.Value : -1;
            float curSunMinutes = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
            int curDay = dayVar != null ? dayVar.Value : -1;
            int curWeather = GetCurrentWeatherCode();
            float curSunRot = sunPivot != null ? sunPivot.localEulerAngles.y : 0f;
            float curScale = timescaleVar != null ? timescaleVar.Value : 1.0f;

            nextBroadcastTime = Time.time + 4.0f;
            BroadcastTimeWeather(curDay, curTime, curSunMinutes, curWeather, curSunRot, curScale);
        }

        private void BroadcastTimeWeather(int day, int time, float sunMinutes, int weatherCode, float sunRot, float timeScale)
        {
            lastSentDay = day;
            lastSentTime = time;
            lastSentSunMinutes = sunMinutes;
            lastSentWeather = weatherCode;
            lastSentSunRot = sunRot;
            lastSentTimeScale = timeScale;

            using (var packet = new PacketWriter(PacketType.TimeWeatherSync))
            {
                packet.WriteInt32(day);
                packet.WriteInt32(time);
                packet.WriteSingle(sunMinutes);
                packet.WriteSingle(sunRot);
                packet.WriteSingle(timeScale);
                packet.WriteByte((byte)weatherCode);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.TimeWeatherSync)
            {
                int day = reader.ReadInt32();
                int time = reader.ReadInt32();
                float sunMinutes = (reader.RemainingBytes >= 4) ? reader.ReadSingle() : 0f;
                float sunRot = (reader.RemainingBytes >= 4) ? reader.ReadSingle() : 0f;
                float timeScale = (reader.RemainingBytes >= 4) ? reader.ReadSingle() : 1.0f;
                byte weatherCode = (reader.RemainingBytes >= 1) ? reader.ReadByte() : (byte)0;

                // Сервер является источником правды — пакеты времени от клиентов сервер игнорирует
                if (transport.IsClient)
                {
                    lastSentDay = day;
                    lastSentTime = time;
                    lastSentSunMinutes = sunMinutes;
                    lastSentSunRot = sunRot;
                    lastSentTimeScale = timeScale;
                    ApplyRemoteTimeWeather(day, time, sunMinutes, sunRot, timeScale, weatherCode);
                }
            }
        }

        private void ApplyRemoteTimeWeather(int day, int time, float sunMinutes, float sunRot, float timeScale, int weatherCode)
        {
            try
            {
                DisableClientLoadActions();

                int prevDay = dayVar != null ? dayVar.Value : day;
                int prevTime = timeVar != null ? timeVar.Value : time;
                float prevMinutes = sunMinutesVar != null ? sunMinutesVar.Value : sunMinutes;

                if (dayVar != null)
                {
                    dayVar.Value = day;
                }

                if (timeVar != null)
                {
                    timeVar.Value = time;
                }

                if (sunMinutesVar != null)
                {
                    sunMinutesVar.Value = sunMinutes;
                }

                if (timescaleVar != null && Math.Abs(timescaleVar.Value - timeScale) > 0.05f)
                {
                    timescaleVar.Value = timeScale;
                }

                if (sunPivot != null)
                {
                    Vector3 euler = sunPivot.localEulerAngles;
                    float finalRot = (sunRot != 0f) ? sunRot : ((time + sunMinutes / 60f) * 15f);
                    sunPivot.localEulerAngles = new Vector3(euler.x, finalRot, euler.z);
                }

                bool needsWakeup = !hasAppliedInitialSync
                    || prevDay != day
                    || prevTime != time
                    || Math.Abs(prevMinutes - sunMinutes) > 1.5f;

                if (sunColorFsm != null && needsWakeup)
                {
                    hasAppliedInitialSync = true;
                    updatingMinutes = true;
                    isLocalTime = false;
                    sunColorFsm.SendEvent("WAKEUP");
                }

                if (weatherFsm != null)
                {
                    string curState = weatherFsm.ActiveStateName;
                    if (weatherCode == 1 && curState != "Rain")
                    {
                        weatherFsm.SendEvent("RAIN");
                        OnLog?.Invoke("[Погода] Пошел дождь.");
                    }
                    else if (weatherCode == 2 && curState != "Thunder")
                    {
                        weatherFsm.SendEvent("THUNDER");
                        OnLog?.Invoke("[Погода] Началась гроза!");
                    }
                    else if (weatherCode == 0 && (curState == "Rain" || curState == "Thunder"))
                    {
                        weatherFsm.SendEvent("NORAIN");
                        OnLog?.Invoke("[Погода] Небо прояснилось.");
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ApplyRemoteTimeWeather Error] {ex.Message}");
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport.IsServer && isInitialized)
            {
                BroadcastCurrentTimeWeather();
                int curTime = timeVar != null ? timeVar.Value : 0;
                float curSunMinutes = sunMinutesVar != null ? sunMinutesVar.Value : 0f;
                int curDay = dayVar != null ? dayVar.Value : -1;
                int displayHour = (curTime + Mathf.FloorToInt(curSunMinutes / 60f)) % 24;
                int displayMin = Mathf.FloorToInt(curSunMinutes % 60f);
                OnLog?.Invoke($"[Время] Отправлен снимок времени хоста ({displayHour:D2}:{displayMin:D2}, День {curDay}) подключившемуся клиенту #{clientId}.");
            }
        }
    }
}
