using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    public class TrackedSwitch
    {
        public int Hash;
        public GameObject GameObject;
        public PlayMakerFSM Fsm;
        public FsmBool SwitchVar;
        public FsmEvent MpEvent;
        public Light RoomLight;
        public GameObject LightGameObject;
        public bool SuppressEcho = false;
    }

    /// <summary>
    /// Сетевой менеджер дома и двора:
    /// 1. Настенные выключатели света во всех комнатах (кухня, зал, спальни, ванная, коридор, гараж).
    /// 2. Каменка сауны (ручки термостата и таймера Kiuas, пар от ковша воды).
    /// 3. Телевизор в гостиной (включение/выключение каналов).
    /// 4. Кухонная плита (4 конфорки, ручки мощности и нагрев).
    /// 5. Водопроводные краны и душ (кухня, ванная, колодец во дворе).
    /// 6. Колка дров (Logwall у сарая с трактором).
    /// </summary>
    public class HouseManager
    {
        public static HouseManager Instance { get; private set; }

        private readonly NetworkTransport transport;
        private readonly Dictionary<int, TrackedSwitch> switches = new Dictionary<int, TrackedSwitch>();

        // Элементы каменки сауны
        private PlayMakerFSM saunaPowerFsm;
        private FsmFloat saunaPowerRot;
        private Transform saunaPowerKnobMesh;

        private PlayMakerFSM saunaTimerFsm;
        private FsmFloat saunaTimerRot;
        private FsmFloat saunaTimerMath1;
        private Transform saunaTimerKnobMesh;

        private PlayMakerFSM saunaStoveTrigger;
        private bool isUpdatingSaunaKnob = false;
        private bool isSuppressingSteam = false;

        // Телевизор в гостиной и телепередачи
        private PlayMakerFSM tvSwitchFsm;
        private FsmBool tvIsOpen;
        private PlayMakerFSM videoProgramFsm;
        private PlayMakerFSM rallyProgramFsm;
        private PlayMakerFSM nightProgramFsm;
        private PlayMakerFSM smokeHomeFsm;
        private bool isSuppressingTv = false;
        private byte currentTvChannel = 0; // 0 = Off, 1 = Video, 2 = Night, 3 = Rally, 4 = Fire
        private byte currentTvSubProgram = 0; // 0 = Topless, 1 = Marjatta

        // Кухонная плита (4 конфорки)
        private readonly PlayMakerFSM[] stoveKnobFsms = new PlayMakerFSM[4];
        private readonly FsmFloat[] stoveKnobData = new FsmFloat[4];
        private readonly Transform[] stoveKnobMeshes = new Transform[4];
        private bool isUpdatingStoveKnob = false;

        // Водопроводные краны и душ
        private readonly Dictionary<int, PlayMakerFSM> waterTaps = new Dictionary<int, PlayMakerFSM>();
        private bool isSuppressingTap = false;

        private bool isInitialized = false;

        public event Action<string> OnLog;
        public int RegisteredSwitchesCount => switches.Count;
        public int RegisteredWaterTapsCount => waterTaps.Count;

        public void Reset()
        {
            isInitialized = false;
            switches.Clear();
            waterTaps.Clear();
            saunaPowerFsm = null;
            saunaPowerRot = null;
            saunaPowerKnobMesh = null;
            saunaTimerFsm = null;
            saunaTimerRot = null;
            saunaTimerMath1 = null;
            saunaTimerKnobMesh = null;
            saunaStoveTrigger = null;
            tvSwitchFsm = null;
            tvIsOpen = null;
            videoProgramFsm = null;
            rallyProgramFsm = null;
            nightProgramFsm = null;
            smokeHomeFsm = null;
            isSuppressingTv = false;
            currentTvChannel = 0;
            currentTvSubProgram = 0;
            for (int i = 0; i < stoveKnobFsms.Length; i++)
            {
                stoveKnobFsms[i] = null;
                stoveKnobData[i] = null;
                stoveKnobMeshes[i] = null;
            }
        }

        public HouseManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeHouse()
        {
            if (isInitialized) return;

            try
            {
                // 1. Поиск и инициализация настенных выключателей света
                InitLightSwitches();

                // 2. Поиск и подключение сауны
                InitSauna();

                // 3. Подключение телевизора в гостиной
                InitTV();

                // 4. Подключение кухонной плиты (4 конфорки)
                InitKitchenStove();

                // 5. Подключение водопроводных кранов и душа
                InitWaterSources();

                isInitialized = true;
                OnLog?.Invoke($"[Дом] Выключатели: {switches.Count}, сауна, плита, ТВ и краны ({waterTaps.Count}) синхронизированы.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[HouseManager] Ошибка инициализации: {ex.Message}");
            }
        }

        private void InitLightSwitches()
        {
            switches.Clear();

            // Поиск светильников в комнатах
            var lightMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            var electric = GameObject.Find("YARD/Building/ElectricAppliances") ?? GameObject.Find("ElectricAppliances");
            if (electric != null)
            {
                var lights = electric.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    string pName = lights[i].transform.parent != null ? lights[i].transform.parent.name : "";
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

            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();

            for (int i = 0; i < allGo.Length; i++)
            {
                GameObject go = allGo[i];
                if (go == null || !go.name.StartsWith("switch")) continue;

                var fsm = go.GetComponent<PlayMakerFSM>();
                if (fsm == null || fsm.FsmName != "Use") continue;

                fsm.Initialize();
                if (!fsm.HasState("Position")) continue;

                int hash = SceneHasher.GetHash(go);
                if (switches.ContainsKey(hash)) continue;

                FsmBool switchVar = fsm.FsmVariables.FindFsmBool("Switch") ?? fsm.FsmVariables.FindFsmBool("SwitchOn");
                FsmEvent mpEvent = fsm.AddEvent("MP_SWITCH");

                GameObject lightGo = null;
                string roomKey = go.name.Replace("switch_", "").Replace("switch", "");
                foreach (var kvp in lightMap)
                {
                    if (kvp.Key.IndexOf(roomKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        lightGo = kvp.Value;
                        break;
                    }
                }

                var tracked = new TrackedSwitch
                {
                    Hash = hash,
                    GameObject = go,
                    Fsm = fsm,
                    SwitchVar = switchVar,
                    MpEvent = mpEvent,
                    LightGameObject = lightGo,
                    RoomLight = lightGo != null ? lightGo.GetComponent<Light>() : null
                };

                fsm.AddGlobalTransition(mpEvent, "Position");
                fsm.InsertAction("Position", () => OnLocalSwitchToggled(tracked), 0, false);
                fsm.Initialize();

                switches[hash] = tracked;
            }
        }

        private void InitSauna()
        {
            Transform sauna = GameObject.Find("YARD")?.transform.Find("Building/SAUNA/Sauna");
            if (sauna == null)
            {
                var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    if (allGo[i].name == "Sauna" && allGo[i].transform.Find("Kiuas") != null)
                    {
                        sauna = allGo[i].transform;
                        break;
                    }
                }
            }

            if (sauna == null) return;

            // Ручка мощности Kiuas
            Transform powerKnob = sauna.Find("Kiuas/ButtonPower");
            if (powerKnob != null)
            {
                saunaPowerFsm = powerKnob.GetComponent<PlayMakerFSM>();
                if (saunaPowerFsm != null)
                {
                    saunaPowerFsm.Initialize();
                    saunaPowerRot = saunaPowerFsm.FsmVariables.FindFsmFloat("Rot");
                    var capMeshVar = saunaPowerFsm.FsmVariables.FindFsmGameObject("CapMesh");
                    if (capMeshVar != null && capMeshVar.Value != null)
                    {
                        saunaPowerKnobMesh = capMeshVar.Value.transform;
                    }

                    saunaPowerFsm.InsertAction("Wait", () => OnLocalSaunaKnobChanged(false), -1, false);
                }
            }

            // Ручка таймера Kiuas
            Transform timerKnob = sauna.Find("Kiuas/ButtonTime");
            if (timerKnob != null)
            {
                saunaTimerFsm = timerKnob.GetComponent<PlayMakerFSM>();
                if (saunaTimerFsm != null)
                {
                    saunaTimerFsm.Initialize();
                    saunaTimerRot = saunaTimerFsm.FsmVariables.FindFsmFloat("Timer");
                    saunaTimerMath1 = saunaTimerFsm.FsmVariables.FindFsmFloat("Math1");
                    var capMeshVar = saunaTimerFsm.FsmVariables.FindFsmGameObject("CapMesh");
                    if (capMeshVar != null && capMeshVar.Value != null)
                    {
                        saunaTimerKnobMesh = capMeshVar.Value.transform;
                    }

                    saunaTimerFsm.InsertAction("Wait", () => OnLocalSaunaKnobChanged(true), -1, false);
                }
            }

            // Триггер пара (ковш воды)
            Transform steamTrigger = sauna.Find("Kiuas/StoveTrigger");
            if (steamTrigger != null)
            {
                saunaStoveTrigger = steamTrigger.GetComponent<PlayMakerFSM>();
                if (saunaStoveTrigger != null)
                {
                    saunaStoveTrigger.InsertAction("Calc blur", () =>
                    {
                        if (isSuppressingSteam)
                        {
                            isSuppressingSteam = false;
                            return;
                        }
                        SendSteamEvent();
                    }, -1, false);
                }
            }
        }

        private void InitTV()
        {
            Transform tvSwitch = GameObject.Find("YARD")?.transform.Find("Building/LIVINGROOM/TV/Switch");
            Transform tvTransform = null;

            if (tvSwitch == null)
            {
                var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    if (allGo[i].name == "TV" && allGo[i].transform.Find("Switch") != null)
                    {
                        tvTransform = allGo[i].transform;
                        tvSwitch = tvTransform.Find("Switch");
                        break;
                    }
                }
            }
            else
            {
                tvTransform = tvSwitch.parent;
            }

            if (tvSwitch != null)
            {
                tvSwitchFsm = tvSwitch.GetComponent<PlayMakerFSM>();
                if (tvSwitchFsm != null)
                {
                    tvSwitchFsm.Initialize();
                    tvIsOpen = tvSwitchFsm.FsmVariables.FindFsmBool("Open");

                    PlayMakerUtilities.AddSafeGlobalTransition(tvSwitchFsm, "MP_SYNC_CLOSE", "Close TV");
                    PlayMakerUtilities.AddSafeGlobalTransition(tvSwitchFsm, "MP_SYNC_VIDEO", "Video");
                    PlayMakerUtilities.AddSafeGlobalTransition(tvSwitchFsm, "MP_SYNC_NIGHT", "Night");
                    PlayMakerUtilities.AddSafeGlobalTransition(tvSwitchFsm, "MP_SYNC_RALLY", "Rally");
                    PlayMakerUtilities.AddSafeGlobalTransition(tvSwitchFsm, "MP_OPEN", "State 5");

                    tvSwitchFsm.InsertAction("Close TV", () =>
                    {
                        if (isSuppressingTv) return;
                        currentTvChannel = 0;
                        currentTvSubProgram = 0;
                        SendTVProgram(0, 0);
                        OnLog?.Invoke("[ТВ] Телевизор выключен");
                    }, 0, false);

                    tvSwitchFsm.InsertAction("Video", () =>
                    {
                        if (isSuppressingTv) return;
                        currentTvChannel = 1;
                        SendTVProgram(1, currentTvSubProgram);
                        OnLog?.Invoke($"[ТВ] Включена дневная телепередача (подпрограмма: {(currentTvSubProgram == 0 ? "Topless" : "Marjatta")})");
                    }, 0, false);

                    tvSwitchFsm.InsertAction("Night", () =>
                    {
                        if (isSuppressingTv) return;
                        currentTvChannel = 2;
                        SendTVProgram(2, 0);
                        OnLog?.Invoke("[ТВ] Включен ночной эфир (настроечная таблица / шум)");
                    }, 0, false);

                    tvSwitchFsm.InsertAction("Rally", () =>
                    {
                        if (isSuppressingTv) return;
                        currentTvChannel = 3;
                        SendTVProgram(3, 0);
                        OnLog?.Invoke("[ТВ] Включена трансляция ралли");
                    }, 0, false);

                    tvSwitchFsm.InsertAction("Switch", () =>
                    {
                        if (isSuppressingTv)
                        {
                            isSuppressingTv = false;
                            return;
                        }
                        bool next = tvIsOpen != null && !tvIsOpen.Value;
                        SendTVToggle(next);
                    }, 0, false);
                }
            }

            if (tvTransform != null)
            {
                var programsTr = tvTransform.Find("TV_Programs");
                if (programsTr != null)
                {
                    var videoTr = programsTr.Find("VideoProgram");
                    if (videoTr != null)
                    {
                        videoProgramFsm = PlayMakerUtilities.FindFsmOnGameObject(videoTr.gameObject, "Program");
                        if (videoProgramFsm != null)
                        {
                            PlayMakerUtilities.AddSafeGlobalTransition(videoProgramFsm, "MP_SYNC_TOPLESS", "Topless");
                            PlayMakerUtilities.AddSafeGlobalTransition(videoProgramFsm, "MP_SYNC_MARJATTA", "Marjatta");

                            videoProgramFsm.InsertAction("Topless", () =>
                            {
                                if (isSuppressingTv) return;
                                currentTvSubProgram = 0;
                                if (currentTvChannel == 1)
                                {
                                    SendTVProgram(1, 0);
                                    OnLog?.Invoke("[ТВ] Передача: показ программы Topless");
                                }
                            }, 0, false);

                            videoProgramFsm.InsertAction("Marjatta", () =>
                            {
                                if (isSuppressingTv) return;
                                currentTvSubProgram = 1;
                                if (currentTvChannel == 1)
                                {
                                    SendTVProgram(1, 1);
                                    OnLog?.Invoke("[ТВ] Передача: показ программы Marjatta");
                                }
                            }, 0, false);
                        }
                    }

                    var rallyTr = programsTr.Find("RallyProgram");
                    if (rallyTr != null)
                    {
                        rallyProgramFsm = PlayMakerUtilities.FindFsmOnGameObject(rallyTr.gameObject, "Program");
                    }

                    var nightTr = programsTr.Find("NightProgram");
                    if (nightTr != null)
                    {
                        nightProgramFsm = PlayMakerUtilities.FindFsmOnGameObject(nightTr.gameObject, "Program");
                    }
                }

                var smokeTr = tvTransform.Find("SmokeHomeTV");
                if (smokeTr != null)
                {
                    smokeHomeFsm = PlayMakerUtilities.FindFsmOnGameObject(smokeTr.gameObject, "Logic");
                    if (smokeHomeFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(smokeHomeFsm, "MP_SYNC_FIRE", "Fire");
                        smokeHomeFsm.InsertAction("Fire", () =>
                        {
                            if (isSuppressingTv) return;
                            currentTvChannel = 4;
                            SendTVProgram(4, 0);
                            OnLog?.Invoke("[ТВ] Внимание: телевизор вспыхнул / задымился!");
                        }, 0, false);
                    }
                }
            }
        }

        private void InitKitchenStove()
        {
            Transform oven = GameObject.Find("YARD")?.transform.Find("Building/KITCHEN/OvenStove");
            if (oven == null)
            {
                var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGo.Length; i++)
                {
                    if (allGo[i].name == "OvenStove")
                    {
                        oven = allGo[i].transform;
                        break;
                    }
                }
            }

            if (oven != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    int idx = i;
                    Transform knob = oven.Find("KnobPower" + (i + 1));
                    if (knob == null) continue;

                    var fsm = knob.GetComponent<PlayMakerFSM>();
                    if (fsm == null) continue;

                    fsm.Initialize();
                    stoveKnobFsms[idx] = fsm;
                    stoveKnobData[idx] = fsm.FsmVariables.FindFsmFloat("Data");
                    var meshVar = fsm.FsmVariables.FindFsmGameObject("Mesh");
                    if (meshVar != null && meshVar.Value != null)
                    {
                        stoveKnobMeshes[idx] = meshVar.Value.transform;
                    }

                    string waitState = fsm.HasState("Wait 2") ? "Wait 2" : (fsm.HasState("Wait") ? "Wait" : null);
                    if (waitState != null)
                    {
                        fsm.InsertAction(waitState, () =>
                        {
                            if (isUpdatingStoveKnob) return;
                            float val = stoveKnobData[idx] != null ? stoveKnobData[idx].Value : 0f;
                            SendStoveKnob((byte)idx, val);
                        }, 0, false);
                    }
                }
            }
        }

        private void InitWaterSources()
        {
            var allFsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            for (int i = 0; i < allFsms.Length; i++)
            {
                var fsm = allFsms[i];
                if (fsm == null || fsm.FsmName != "Use") continue;

                string pName = fsm.transform.parent != null ? fsm.transform.parent.name : "";
                string oName = fsm.gameObject.name;

                if (pName == "KitchenWaterTap" || pName == "BathroomWaterTap" || pName == "Shower" || pName == "WaterWell" || oName.Contains("WaterTap"))
                {
                    fsm.Initialize();
                    int hash = SceneHasher.GetHash(fsm.gameObject);
                    if (!waterTaps.ContainsKey(hash))
                    {
                        waterTaps[hash] = fsm;
                        var tapOn = fsm.FsmVariables.FindFsmBool("SwitchOn");
                        FsmEvent mpOn = fsm.AddEvent("MP_ON");
                        fsm.AddGlobalTransition(mpOn, "ON");
                        FsmEvent mpOff = fsm.AddEvent("MP_OFF");
                        fsm.AddGlobalTransition(mpOff, "OFF");

                        string state = fsm.HasState("Position") ? "Position" : (fsm.HasState("Switch") ? "Switch" : null);
                        if (state != null)
                        {
                            fsm.InsertAction(state, () =>
                            {
                                if (isSuppressingTap)
                                {
                                    isSuppressingTap = false;
                                    return;
                                }
                                bool cur = tapOn != null && tapOn.Value;
                                SendWaterTap(hash, !cur);
                            }, 0, false);
                        }
                    }
                }
            }
        }

        private void OnLocalSwitchToggled(TrackedSwitch sw)
        {
            if (sw.SuppressEcho)
            {
                sw.SuppressEcho = false;
                return;
            }

            if (!transport.IsRunning) return;

            bool state = sw.SwitchVar != null && sw.SwitchVar.Value;
            if (sw.RoomLight != null) sw.RoomLight.enabled = state;
            if (sw.LightGameObject != null) sw.LightGameObject.SetActive(state);

            SendSwitchToggle(sw.Hash, state);
            OnLog?.Invoke($"[Дом] Выключатель {sw.GameObject.name} переключен локально -> {(state ? "ВКЛ" : "ВЫКЛ")}");
        }

        private void OnLocalSaunaKnobChanged(bool isTimer)
        {
            if (isUpdatingSaunaKnob)
            {
                isUpdatingSaunaKnob = false;
                return;
            }

            if (!transport.IsRunning) return;

            float val = isTimer
                ? (saunaTimerRot != null ? saunaTimerRot.Value : 0f)
                : (saunaPowerRot != null ? saunaPowerRot.Value : 0f);

            SendSaunaKnob(isTimer, val);
        }

        private void SendSwitchToggle(int switchHash, bool state)
        {
            using (var packet = new PacketWriter(PacketType.LightSwitchToggle))
            {
                packet.WriteInt32(switchHash);
                packet.WriteBoolean(state);
                transport.Send(packet);
            }
        }

        private void SendSaunaKnob(bool isTimer, float value)
        {
            using (var packet = new PacketWriter(PacketType.SaunaKnobSync))
            {
                packet.WriteBoolean(isTimer);
                packet.WriteSingle(value);
                transport.Send(packet);
            }
        }

        private void SendSteamEvent()
        {
            if (!transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.SaunaSteam))
            {
                transport.Send(packet);
            }
        }

        private void SendTVToggle(bool state)
        {
            using (var packet = new PacketWriter(PacketType.TVToggle))
            {
                packet.WriteBoolean(state);
                transport.Send(packet);
            }
        }

        public void SendTVProgram(byte channel, byte subProgram)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TVProgramSync))
            {
                packet.WriteByte(channel);
                packet.WriteByte(subProgram);
                transport.Send(packet);
            }
        }

        private void SendStoveKnob(byte knobIndex, float value)
        {
            using (var packet = new PacketWriter(PacketType.StoveKnobSync))
            {
                packet.WriteByte(knobIndex);
                packet.WriteSingle(value);
                transport.Send(packet);
            }
        }

        private void SendWaterTap(int hash, bool on)
        {
            using (var packet = new PacketWriter(PacketType.WaterTapToggle))
            {
                packet.WriteInt32(hash);
                packet.WriteBoolean(on);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.LightSwitchToggle:
                {
                    int hash = reader.ReadInt32();
                    bool state = reader.ReadBoolean();

                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.LightSwitchToggle))
                        {
                            relay.WriteInt32(hash);
                            relay.WriteBoolean(state);
                            transport.Send(relay, connectionId);
                        }
                    }

                    if (switches.TryGetValue(hash, out var sw))
                    {
                        sw.SuppressEcho = true;
                        if (sw.SwitchVar != null) sw.SwitchVar.Value = state;
                        if (sw.RoomLight != null) sw.RoomLight.enabled = state;
                        if (sw.LightGameObject != null) sw.LightGameObject.SetActive(state);
                        sw.Fsm.Fsm.Event(sw.MpEvent);
                        OnLog?.Invoke($"[Дом] Выключатель {sw.GameObject.name} переключен по сети -> {(state ? "ВКЛ" : "ВЫКЛ")}");
                    }
                    break;
                }

                case PacketType.SaunaKnobSync:
                {
                    bool isTimer = reader.ReadBoolean();
                    float val = reader.ReadSingle();

                    isUpdatingSaunaKnob = true;
                    if (isTimer)
                    {
                        if (saunaTimerRot != null) saunaTimerRot.Value = val;
                        if (saunaTimerMath1 != null) saunaTimerMath1.Value = val * 6f;
                        if (saunaTimerKnobMesh != null) saunaTimerKnobMesh.localEulerAngles = Vector3.up * val;
                    }
                    else
                    {
                        if (saunaPowerRot != null) saunaPowerRot.Value = val;
                        if (saunaPowerKnobMesh != null) saunaPowerKnobMesh.localEulerAngles = Vector3.up * val;
                    }
                    break;
                }

                case PacketType.SaunaSteam:
                {
                    if (saunaStoveTrigger != null)
                    {
                        isSuppressingSteam = true;
                        saunaStoveTrigger.SendEvent("GLOBALEVENT");
                    }
                    break;
                }

                case PacketType.LightSwitchSnapshot:
                {
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int hash = reader.ReadInt32();
                        bool state = reader.ReadBoolean();
                        if (switches.TryGetValue(hash, out var sw))
                        {
                            sw.SuppressEcho = true;
                            if (sw.SwitchVar != null) sw.SwitchVar.Value = state;
                            if (sw.RoomLight != null) sw.RoomLight.enabled = state;
                            if (sw.LightGameObject != null) sw.LightGameObject.SetActive(state);
                            sw.Fsm.Fsm.Event(sw.MpEvent);
                        }
                    }
                    OnLog?.Invoke($"[Дом] Снимок освещения ({count} выключателей) применен.");
                    break;
                }

                case PacketType.TVToggle:
                {
                    bool state = reader.ReadBoolean();
                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.TVToggle))
                        {
                            relay.WriteBoolean(state);
                            transport.Send(relay, connectionId);
                        }
                    }
                    if (tvSwitchFsm != null)
                    {
                        isSuppressingTv = true;
                        try
                        {
                            tvSwitchFsm.SendEvent(state ? "MP_OPEN" : "MP_SYNC_CLOSE");
                        }
                        finally
                        {
                            isSuppressingTv = false;
                        }
                    }
                    break;
                }

                case PacketType.TVProgramSync:
                {
                    byte channel = reader.ReadByte();
                    byte subProgram = reader.ReadByte();
                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.TVProgramSync))
                        {
                            relay.WriteByte(channel);
                            relay.WriteByte(subProgram);
                            transport.Send(relay, connectionId);
                        }
                    }
                    ApplyRemoteTVProgram(channel, subProgram);
                    break;
                }

                case PacketType.StoveKnobSync:
                {
                    byte knobIdx = reader.ReadByte();
                    float val = reader.ReadSingle();
                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.StoveKnobSync))
                        {
                            relay.WriteByte(knobIdx);
                            relay.WriteSingle(val);
                            transport.Send(relay, connectionId);
                        }
                    }
                    if (knobIdx < 4 && stoveKnobFsms[knobIdx] != null)
                    {
                        isUpdatingStoveKnob = true;
                        if (stoveKnobData[knobIdx] != null) stoveKnobData[knobIdx].Value = val;
                        if (stoveKnobMeshes[knobIdx] != null) stoveKnobMeshes[knobIdx].localEulerAngles = Vector3.up * val * 45f;
                    }
                    break;
                }

                case PacketType.WaterTapToggle:
                {
                    int tapHash = reader.ReadInt32();
                    bool on = reader.ReadBoolean();
                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.WaterTapToggle))
                        {
                            relay.WriteInt32(tapHash);
                            relay.WriteBoolean(on);
                            transport.Send(relay, connectionId);
                        }
                    }
                    if (waterTaps.TryGetValue(tapHash, out var tapFsm) && tapFsm != null)
                    {
                        isSuppressingTap = true;
                        tapFsm.SendEvent(on ? "MP_ON" : "MP_OFF");
                    }
                    break;
                }
            }
        }

        private void HandleClientConnected(int clientId)
        {
            // Отправка снимка состояния всех выключателей новому клиенту
            if (switches.Count > 0)
            {
                using (var packet = new PacketWriter(PacketType.LightSwitchSnapshot))
                {
                    packet.WriteInt32(switches.Count);
                    foreach (var pair in switches)
                    {
                        packet.WriteInt32(pair.Key);
                        packet.WriteBoolean(pair.Value.SwitchVar != null && pair.Value.SwitchVar.Value);
                    }
                    transport.Send(packet);
                }
            }

            // Отправка состояния ручек сауны
            if (saunaPowerRot != null) SendSaunaKnob(false, saunaPowerRot.Value);
            if (saunaTimerRot != null) SendSaunaKnob(true, saunaTimerRot.Value);

            // Отправка состояния ТВ и активной телепередачи
            if (tvIsOpen != null) SendTVToggle(tvIsOpen.Value);
            SendTVProgram(currentTvChannel, currentTvSubProgram);
        }

        private void ApplyRemoteTVProgram(byte channel, byte subProgram)
        {
            if (tvSwitchFsm == null) InitTV();
            if (tvSwitchFsm == null) return;

            isSuppressingTv = true;
            try
            {
                currentTvChannel = channel;
                currentTvSubProgram = subProgram;

                switch (channel)
                {
                    case 0: // Off / Close TV
                        tvSwitchFsm.SendEvent("MP_SYNC_CLOSE");
                        break;

                    case 1: // Video
                        tvSwitchFsm.SendEvent("MP_SYNC_VIDEO");
                        if (videoProgramFsm != null)
                        {
                            videoProgramFsm.SendEvent(subProgram == 1 ? "MP_SYNC_MARJATTA" : "MP_SYNC_TOPLESS");
                        }
                        break;

                    case 2: // Night static / test pattern
                        tvSwitchFsm.SendEvent("MP_SYNC_NIGHT");
                        break;

                    case 3: // Rally
                        tvSwitchFsm.SendEvent("MP_SYNC_RALLY");
                        break;

                    case 4: // Fire
                        if (smokeHomeFsm != null)
                        {
                            smokeHomeFsm.SendEvent("MP_SYNC_FIRE");
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingTv = false;
            }

            string chName = channel == 0 ? "ВЫКЛ" : (channel == 1 ? (subProgram == 1 ? "Видео (Marjatta)" : "Видео (Topless)") : (channel == 2 ? "Ночной эфир" : (channel == 3 ? "Ралли" : "Пожар")));
            OnLog?.Invoke($"[ТВ] Сетевая синхронизация телепередачи: {chName}");
        }
    }
}
