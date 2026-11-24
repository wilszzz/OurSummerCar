using System;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.World
{
    /// <summary>
    /// Менеджер сетевой синхронизации бара Pub Nappo (разливное пиво, еда, музыкальный автомат Jukebox),
    /// острова Мёкки (лодка, дровяная печь сауны) и рыболовной верши (Katiska).
    /// </summary>
    public class PubAndIslandManager : MonoBehaviour
    {
        public static PubAndIslandManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Паб Наппо и Музыкальные автоматы
        private PlayMakerFSM pubBeerTapFsm;
        private PlayMakerFSM pubJukeboxFsm;
        private PlayMakerFSM storeJukeboxFsm;
        private bool isSuppressingTap = false;
        private bool isSuppressingJb = false;

        // Лодка и Остров
        private PlayMakerFSM boatMotorFsm;
        private PlayMakerFSM boatIgnitionFsm;
        private PlayMakerFSM boatGearFsm;
        private PlayMakerFSM boatShutOffFsm;
        private string boatYankState;
        private string boatRunState;
        private int lastLocalBoatGear = -1;

        private PlayMakerFSM cottageStoveFsm;
        private PlayMakerFSM fishTrapFsm;
        private bool isSuppressingBoat = false;
        private bool suppressBoatIgnitionYank = false;
        private bool suppressBoatIgnitionStart = false;
        private bool suppressBoatGear = false;
        private bool suppressBoatShutOff = false;
        private bool isSuppressingCottage = false;

        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanObjects();
            OnLog?.Invoke("[Паб/Остров] Менеджер паба, лодки и острова инициализирован");
        }

        public void Reset()
        {
            pubBeerTapFsm = null;
            pubJukeboxFsm = null;
            storeJukeboxFsm = null;
            boatMotorFsm = null;
            boatIgnitionFsm = null;
            boatGearFsm = null;
            boatShutOffFsm = null;
            boatYankState = null;
            boatRunState = null;
            lastLocalBoatGear = -1;
            cottageStoveFsm = null;
            fishTrapFsm = null;
            isSuppressingTap = false;
            isSuppressingJb = false;
            isSuppressingBoat = false;
            suppressBoatIgnitionYank = false;
            suppressBoatIgnitionStart = false;
            suppressBoatGear = false;
            suppressBoatShutOff = false;
            isSuppressingCottage = false;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void ScanObjects()
        {
            try
            {
                // 1. Бар Паб Наппо: разливной кран пива
                if (pubBeerTapFsm == null)
                {
                    var tapGo = GameObject.Find("pubi_kalia") ?? GameObject.Find("BeerTap") ?? GameObject.Find("BeerTapPub");
                    if (tapGo != null)
                    {
                        pubBeerTapFsm = PlayMakerUtilities.FindFsmOnGameObject(tapGo, "Use") ??
                                        tapGo.GetComponent<PlayMakerFSM>();
                    }
                }

                // 2. Музыкальные автоматы (Jukebox)
                if (storeJukeboxFsm == null)
                {
                    var jbStoreGo = GameObject.Find("JukeboxStore");
                    if (jbStoreGo != null)
                    {
                        storeJukeboxFsm = PlayMakerUtilities.FindFsmOnGameObject(jbStoreGo, "Jukebox") ??
                                          jbStoreGo.GetComponent<PlayMakerFSM>();
                        if (storeJukeboxFsm != null)
                        {
                            storeJukeboxFsm.InsertAction("Play", () =>
                            {
                                if (isSuppressingJb) return;
                                var sVar = storeJukeboxFsm.FsmVariables.FindFsmInt("Song");
                                SendJukeboxPlay(false, sVar != null ? sVar.Value : 1);
                            }, 0, false);
                        }
                    }
                }

                if (pubJukeboxFsm == null)
                {
                    var jbPubGo = GameObject.Find("JukeboxFleetari") ?? GameObject.Find("JukeboxPub");
                    if (jbPubGo != null)
                    {
                        pubJukeboxFsm = PlayMakerUtilities.FindFsmOnGameObject(jbPubGo, "Jukebox") ??
                                        jbPubGo.GetComponent<PlayMakerFSM>();
                        if (pubJukeboxFsm != null)
                        {
                            pubJukeboxFsm.InsertAction("Play", () =>
                            {
                                if (isSuppressingJb) return;
                                var sVar = pubJukeboxFsm.FsmVariables.FindFsmInt("Song");
                                SendJukeboxPlay(true, sVar != null ? sVar.Value : 1);
                            }, 0, false);
                        }
                    }
                }

                // 3. Лодка (BOAT): GFX/Motor/Pivot/Ignition ("Jank"), Gear ("Use"), ShutOff ("Use")
                var boatGo = GameObject.Find("BOAT") ?? GameObject.Find("Boat") ?? GameObject.Find("Vene");
                if (boatGo != null)
                {
                    if (boatMotorFsm == null)
                    {
                        boatMotorFsm = PlayMakerUtilities.FindFsmOnGameObject(boatGo, "Motor") ??
                                       PlayMakerUtilities.FindFsmOnGameObject(boatGo, "StartEngine");
                        if (boatMotorFsm != null && boatMotorFsm.HasState("Start"))
                        {
                            boatMotorFsm.InsertAction("Start", () =>
                            {
                                if (isSuppressingBoat) return;
                                var tVar = boatMotorFsm.FsmVariables.FindFsmFloat("Throttle");
                                SendBoatEngine(true, tVar != null ? tVar.Value : 0f);
                            }, 0, false);
                        }
                    }

                    HookBoatMotorFsms(boatGo);
                }

                // 4. Дровяная печь в сауне на острове (Cottage)
                if (cottageStoveFsm == null)
                {
                    var cottageGo = GameObject.Find("COTTAGE") ?? GameObject.Find("Cottage");
                    if (cottageGo != null)
                    {
                        cottageStoveFsm = PlayMakerUtilities.FindFsmOnGameObject(cottageGo, "Stove") ??
                                          PlayMakerUtilities.FindFsmOnGameObject(cottageGo, "Fireplace") ??
                                          cottageGo.GetComponentInChildren<PlayMakerFSM>();
                    }
                }

                // 5. Рыболовная верша (Katiska)
                if (fishTrapFsm == null)
                {
                    var katiskaGo = GameObject.Find("katiska_frame") ?? GameObject.Find("katiska(itemx)") ?? GameObject.Find("katiska");
                    if (katiskaGo != null)
                    {
                        fishTrapFsm = PlayMakerUtilities.FindFsmOnGameObject(katiskaGo, "Trap") ??
                                      katiskaGo.GetComponent<PlayMakerFSM>();
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Паб/Остров] Ошибка сканирования: {ex.Message}");
            }
        }

        private void HookBoatMotorFsms(GameObject boatGo)
        {
            if (boatGo == null) return;

            // 3.1. Стартер лодки: BOAT/GFX/Motor/Pivot/Ignition (FSM "Jank")
            if (boatIgnitionFsm == null)
            {
                Transform ignTr = boatGo.transform.Find("GFX/Motor/Pivot/Ignition");
                if (ignTr != null)
                {
                    var fsm = PlayMakerUtilities.FindFsmOnGameObject(ignTr.gameObject, "Jank") ??
                              ignTr.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        fsm.Initialize();
                        boatIgnitionFsm = fsm;
                        string startState = fsm.Fsm != null ? fsm.Fsm.StartState : string.Empty;

                        string[] yankCandidates = { "Yank", "Jank", "Pull", "Pull rope", "State 1", "State 2" };
                        for (int i = 0; i < yankCandidates.Length; i++)
                        {
                            string s = yankCandidates[i];
                            if (s != startState && fsm.HasState(s))
                            {
                                boatYankState = s;
                                break;
                            }
                        }

                        string[] runCandidates = { "Running", "Start", "Motor running", "Engine running", "Started", "ON", "On" };
                        for (int i = 0; i < runCandidates.Length; i++)
                        {
                            string s = runCandidates[i];
                            if (s != startState && s != boatYankState && fsm.HasState(s))
                            {
                                boatRunState = s;
                                break;
                            }
                        }

                        if (boatYankState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_BOAT_YANK", boatYankState);
                            fsm.InsertAction(boatYankState, () =>
                            {
                                if (suppressBoatIgnitionYank)
                                {
                                    suppressBoatIgnitionYank = false;
                                    return;
                                }
                                SendBoatEngineSubtype(1, 0, 0f);
                            }, 0, false);
                        }

                        if (boatRunState != null)
                        {
                            fsm.AddSafeGlobalTransition("MP_BOAT_START", boatRunState);
                            fsm.InsertAction(boatRunState, () =>
                            {
                                if (suppressBoatIgnitionStart)
                                {
                                    suppressBoatIgnitionStart = false;
                                    return;
                                }
                                SendBoatEngineSubtype(1, 1, 0f);
                            }, 0, false);
                        }

                        fsm.Initialize();
                    }
                }
            }

            // 3.2. Передача лодки: BOAT/GFX/Motor/Pivot/Gear (FSM "Use", состояния "First", "Neutral", "Reverse")
            if (boatGearFsm == null)
            {
                Transform gearTr = boatGo.transform.Find("GFX/Motor/Pivot/Gear");
                if (gearTr != null)
                {
                    var fsm = PlayMakerUtilities.FindFsmOnGameObject(gearTr.gameObject, "Use") ??
                              gearTr.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        fsm.Initialize();
                        boatGearFsm = fsm;

                        string startState = fsm.Fsm != null ? fsm.Fsm.StartState : string.Empty;
                        if (startState == "First") lastLocalBoatGear = 1;
                        else if (startState == "Reverse") lastLocalBoatGear = 2;
                        else if (startState == "Neutral") lastLocalBoatGear = 0;
                        else lastLocalBoatGear = -1;

                        if (fsm.HasState("Neutral"))
                        {
                            fsm.AddSafeGlobalTransition("MP_GEAR_NEUTRAL", "Neutral");
                            fsm.InsertAction("Neutral", () =>
                            {
                                if (suppressBoatGear)
                                {
                                    suppressBoatGear = false;
                                    return;
                                }
                                if (lastLocalBoatGear != 0)
                                {
                                    lastLocalBoatGear = 0;
                                    SendBoatEngineSubtype(2, 0, 0f);
                                }
                            }, 0, false);
                        }

                        if (fsm.HasState("First"))
                        {
                            fsm.AddSafeGlobalTransition("MP_GEAR_FIRST", "First");
                            fsm.InsertAction("First", () =>
                            {
                                if (suppressBoatGear)
                                {
                                    suppressBoatGear = false;
                                    return;
                                }
                                if (lastLocalBoatGear != 1)
                                {
                                    lastLocalBoatGear = 1;
                                    SendBoatEngineSubtype(2, 1, 0f);
                                }
                            }, 0, false);
                        }

                        if (fsm.HasState("Reverse"))
                        {
                            fsm.AddSafeGlobalTransition("MP_GEAR_REVERSE", "Reverse");
                            fsm.InsertAction("Reverse", () =>
                            {
                                if (suppressBoatGear)
                                {
                                    suppressBoatGear = false;
                                    return;
                                }
                                if (lastLocalBoatGear != 2)
                                {
                                    lastLocalBoatGear = 2;
                                    SendBoatEngineSubtype(2, 2, 0f);
                                }
                            }, 0, false);
                        }

                        fsm.Initialize();
                    }
                }
            }

            // 3.3. Кнопка глушения лодки: BOAT/GFX/Motor/Pivot/ShutOff (FSM "Use", состояние "Shut Off")
            if (boatShutOffFsm == null)
            {
                Transform shutTr = boatGo.transform.Find("GFX/Motor/Pivot/ShutOff");
                if (shutTr != null)
                {
                    var fsm = PlayMakerUtilities.FindFsmOnGameObject(shutTr.gameObject, "Use") ??
                              shutTr.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        fsm.Initialize();
                        boatShutOffFsm = fsm;

                        if (fsm.HasState("Shut Off"))
                        {
                            fsm.AddSafeGlobalTransition("MP_BOAT_SHUTOFF", "Shut Off");
                            fsm.InsertAction("Shut Off", () =>
                            {
                                if (suppressBoatShutOff)
                                {
                                    suppressBoatShutOff = false;
                                    return;
                                }
                                SendBoatEngineSubtype(3, 1, 0f);
                            }, 0, false);
                        }

                        fsm.Initialize();
                    }
                }
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 7f;
                if (pubJukeboxFsm == null || storeJukeboxFsm == null ||
                    boatIgnitionFsm == null || boatGearFsm == null || boatShutOffFsm == null)
                {
                    ScanObjects();
                }
            }
        }

        public void SendPubTapAction()
        {
            if (isSuppressingTap || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.PubNappoSync))
            {
                packet.WriteByte(1); // 1 = Beer tap
                transport.Send(packet);
            }
            OnLog?.Invoke("[Паб] Налито разливное пиво в пабе Наппо");
        }

        public void SendJukeboxPlay(bool isPub, int songIndex)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.JukeboxSync))
            {
                packet.WriteBoolean(isPub);
                packet.WriteInt32(songIndex);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Jukebox] Включен трек #{songIndex} ({(isPub ? "Паб" : "Магазин")})");
        }

        public void SendBoatEngine(bool running, float throttle)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.BoatEngineSync))
            {
                packet.WriteByte(0); // 0 = legacy/start
                packet.WriteBoolean(running);
                packet.WriteSingle(throttle);
                transport.Send(packet);
            }
        }

        private void SendBoatEngineSubtype(byte subtype, byte value, float throttle)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.BoatEngineSync))
            {
                packet.WriteByte(subtype); // 1=Ignition yank/start, 2=Gear, 3=ShutOff
                packet.WriteByte(value);
                packet.WriteSingle(throttle);
                transport.Send(packet);
            }
        }

        public void SendCottageStove(bool lit, float heat)
        {
            if (isSuppressingCottage || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CottageSync))
            {
                packet.WriteBoolean(lit);
                packet.WriteSingle(heat);
                transport.Send(packet);
            }
        }

        public void SendFishTrapState(bool inWater, int fishCount)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.FishTrapSync))
            {
                packet.WriteBoolean(inWater);
                packet.WriteInt32(fishCount);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.PubNappoSync:
                    HandlePubNappoSync(reader);
                    break;

                case PacketType.JukeboxSync:
                    HandleJukeboxSync(reader);
                    break;

                case PacketType.BoatEngineSync:
                    HandleBoatEngineSync(reader, connectionId);
                    break;

                case PacketType.CottageSync:
                    HandleCottageSync(reader);
                    break;

                case PacketType.FishTrapSync:
                    HandleFishTrapSync(reader);
                    break;
            }
        }

        private void HandlePubNappoSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            if (action == 1 && pubBeerTapFsm != null)
            {
                isSuppressingTap = true;
                try
                {
                    pubBeerTapFsm.SendEvent("USE");
                }
                finally
                {
                    isSuppressingTap = false;
                }
                OnLog?.Invoke("[Паб] Друг налил пиво в баре");
            }
        }

        private void HandleJukeboxSync(PacketReader reader)
        {
            bool isPub = reader.ReadBoolean();
            int song = reader.ReadInt32();

            isSuppressingJb = true;
            try
            {
                var jb = isPub ? pubJukeboxFsm : storeJukeboxFsm;
                if (jb != null)
                {
                    var sVar = jb.FsmVariables.FindFsmInt("Song");
                    if (sVar != null) sVar.Value = song;
                    jb.SendEvent("PLAY");
                }
            }
            finally
            {
                isSuppressingJb = false;
            }

            OnLog?.Invoke($"[Jukebox] Друг запустил песню #{song} в {(isPub ? "пабе" : "магазине Теймо")}");
        }

        private void HandleBoatEngineSync(PacketReader reader, int connectionId)
        {
            byte subtype = reader.ReadByte();

            if (subtype == 0)
            {
                bool running = reader.ReadBoolean();
                float throttle = reader.UnreadBytes >= 4 ? reader.ReadSingle() : 0f;

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.BoatEngineSync))
                    {
                        relay.WriteByte(0);
                        relay.WriteBoolean(running);
                        relay.WriteSingle(throttle);
                        transport.Send(relay, connectionId);
                    }
                }

                isSuppressingBoat = true;
                try
                {
                    if (boatMotorFsm != null)
                    {
                        var tVar = boatMotorFsm.FsmVariables.FindFsmFloat("Throttle");
                        if (tVar != null) tVar.Value = throttle;
                        boatMotorFsm.SendEvent(running ? "START" : "STOP");
                    }
                }
                finally
                {
                    isSuppressingBoat = false;
                }
                return;
            }

            byte value = reader.ReadByte();
            float extraThrottle = reader.UnreadBytes >= 4 ? reader.ReadSingle() : 0f;

            if (transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.BoatEngineSync))
                {
                    relay.WriteByte(subtype);
                    relay.WriteByte(value);
                    relay.WriteSingle(extraThrottle);
                    transport.Send(relay, connectionId);
                }
            }

            if (subtype == 1)
            {
                if (boatIgnitionFsm != null)
                {
                    if (value == 0)
                    {
                        if (boatYankState != null)
                        {
                            suppressBoatIgnitionYank = true;
                            boatIgnitionFsm.SendEvent("MP_BOAT_YANK");
                        }
                    }
                    else if (value == 1)
                    {
                        string[] boolNames = { "Running", "MotorOn", "EngineOn", "On" };
                        for (int i = 0; i < boolNames.Length; i++)
                        {
                            var bVar = boatIgnitionFsm.FsmVariables.FindFsmBool(boolNames[i]);
                            if (bVar != null) bVar.Value = true;
                        }
                        if (boatRunState != null)
                        {
                            suppressBoatIgnitionStart = true;
                            boatIgnitionFsm.SendEvent("MP_BOAT_START");
                        }
                    }
                }
            }
            else if (subtype == 2)
            {
                if (boatGearFsm != null)
                {
                    suppressBoatGear = true;
                    lastLocalBoatGear = value;
                    string evName = value == 1 ? "MP_GEAR_FIRST" : (value == 2 ? "MP_GEAR_REVERSE" : "MP_GEAR_NEUTRAL");
                    boatGearFsm.SendEvent(evName);
                }
            }
            else if (subtype == 3)
            {
                if (boatShutOffFsm != null)
                {
                    suppressBoatShutOff = true;
                    boatShutOffFsm.SendEvent("MP_BOAT_SHUTOFF");
                }
            }
        }

        private void HandleCottageSync(PacketReader reader)
        {
            bool lit = reader.ReadBoolean();
            float heat = reader.ReadSingle();

            isSuppressingCottage = true;
            try
            {
                if (cottageStoveFsm != null)
                {
                    var hVar = cottageStoveFsm.FsmVariables.FindFsmFloat("Heat");
                    if (hVar != null) hVar.Value = heat;
                    cottageStoveFsm.SendEvent(lit ? "IGNITE" : "EXTINGUISH");
                }
            }
            finally
            {
                isSuppressingCottage = false;
            }
            OnLog?.Invoke($"[Остров] Печь сауны на острове: {(lit ? "Растоплена" : "Потушена")}");
        }

        private void HandleFishTrapSync(PacketReader reader)
        {
            bool inWater = reader.ReadBoolean();
            int fishCount = reader.ReadInt32();

            if (fishTrapFsm != null)
            {
                var fVar = fishTrapFsm.FsmVariables.FindFsmInt("Fish");
                if (fVar != null) fVar.Value = fishCount;
            }
            OnLog?.Invoke($"[Рыбалка] Верша проверена. Поймано рыбы: {fishCount}");
        }
    }
}
