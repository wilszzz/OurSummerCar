using System;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Jobs
{
    /// <summary>
    /// Менеджер сетевой синхронизации ралли (Amis Rally), дрэг-рейсинга на аэродроме (Drag Racing),
    /// штрафов полиции и тюремного заключения (Police & Jail), а также диких лосей (Wildlife).
    /// </summary>
    public class RallyAndPoliceManager : MonoBehaviour
    {
        public static RallyAndPoliceManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Ралли
        private PlayMakerFSM rallyRegistrationFsm;
        private PlayMakerFSM rallyTimerFsm;

        // Дрэг-рейсинг
        private PlayMakerFSM dragTreeFsm;
        private PlayMakerFSM dragTimingFsm;

        // Полиция и Тюрьма
        private PlayMakerFSM policeFsm;
        private PlayMakerFSM jailTimeFsm;

        // Дикая природа (Лось)
        private PlayMakerFSM mooseFsm;

        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanObjects();
            OnLog?.Invoke("[Ралли/Полиция] Менеджер ралли, гонок, полиции и фауны инициализирован");
        }

        public void Reset()
        {
            rallyRegistrationFsm = null;
            rallyTimerFsm = null;
            dragTreeFsm = null;
            dragTimingFsm = null;
            policeFsm = null;
            jailTimeFsm = null;
            mooseFsm = null;
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
                // 1. Ралли
                var rallyGo = GameObject.Find("Rally") ?? GameObject.Find("RallyRegistration");
                if (rallyGo != null)
                {
                    rallyRegistrationFsm = PlayMakerUtilities.FindFsmOnGameObject(rallyGo, "Registration") ??
                                           PlayMakerUtilities.FindFsmOnGameObject(rallyGo, "Rally") ??
                                           rallyGo.GetComponent<PlayMakerFSM>();
                    rallyTimerFsm = PlayMakerUtilities.FindFsmOnGameObject(rallyGo, "Timer") ??
                                    PlayMakerUtilities.FindFsmOnGameObject(rallyGo, "Stage");
                }

                // 2. Дрэг-рейсинг на аэродроме
                var dragTreeGo = GameObject.Find("drag_tree") ?? GameObject.Find("DRAGRACE");
                if (dragTreeGo != null)
                {
                    dragTreeFsm = PlayMakerUtilities.FindFsmOnGameObject(dragTreeGo, "Tree") ??
                                  PlayMakerUtilities.FindFsmOnGameObject(dragTreeGo, "Lights") ??
                                  dragTreeGo.GetComponentInChildren<PlayMakerFSM>();
                }

                var dragTimingGo = GameObject.Find("DragTiming") ?? GameObject.Find("DragTimeSlip");
                if (dragTimingGo != null)
                {
                    dragTimingFsm = PlayMakerUtilities.FindFsmOnGameObject(dragTimingGo, "Timing") ??
                                    dragTimingGo.GetComponent<PlayMakerFSM>();
                }

                // 3. Полиция и Тюрьма
                var policeGo = GameObject.Find("Police") ?? GameObject.Find("PoliceCheckpoint");
                if (policeGo != null)
                {
                    policeFsm = PlayMakerUtilities.FindFsmOnGameObject(policeGo, "Police") ??
                                policeGo.GetComponent<PlayMakerFSM>();
                }

                var jailGo = GameObject.Find("Jail") ?? GameObject.Find("jail_door_hatch") ?? GameObject.Find("JAIL");
                if (jailGo != null)
                {
                    jailTimeFsm = PlayMakerUtilities.FindFsmOnGameObject(jailGo, "Sentence") ??
                                  PlayMakerUtilities.FindFsmOnGameObject(jailGo, "Time") ??
                                  jailGo.GetComponentInChildren<PlayMakerFSM>();
                }

                // 4. Лось
                var mooseGo = GameObject.Find("AnimalsMoose") ?? GameObject.Find("Moose");
                if (mooseGo != null)
                {
                    mooseFsm = PlayMakerUtilities.FindFsmOnGameObject(mooseGo, "Moose") ??
                               PlayMakerUtilities.FindFsmOnGameObject(mooseGo, "Behavior") ??
                               mooseGo.GetComponentInChildren<PlayMakerFSM>();
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Ралли/Полиция] Ошибка сканирования: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 8f;
                if (dragTreeFsm == null || policeFsm == null)
                {
                    ScanObjects();
                }
            }
        }

        public void SendRallyStageEvent(string stageName, float stageTime, bool isFinish)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.RallyRaceSync))
            {
                packet.WriteString(stageName);
                packet.WriteSingle(stageTime);
                packet.WriteBoolean(isFinish);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Ралли] Стейдж: {stageName}, Время: {stageTime:F2}с {(isFinish ? "(Финиш!)" : string.Empty)}");
        }

        public void SendDragTreeCountdown(int lightState)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.DragRaceSync))
            {
                packet.WriteInt32(lightState);
                transport.Send(packet);
            }
        }

        public void SendPoliceFine(int fineAmount, string reason)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.PoliceFineSync))
            {
                packet.WriteInt32(fineAmount);
                packet.WriteString(reason);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Полиция] Выписан штраф {fineAmount} mk ({reason})");
        }

        public void SendMooseHit(Vector3 hitPos)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.WildlifeSync))
            {
                packet.WriteSingle(hitPos.x);
                packet.WriteSingle(hitPos.y);
                packet.WriteSingle(hitPos.z);
                transport.Send(packet);
            }
            OnLog?.Invoke("[Фауна] Столкновение с лосем на трассе!");
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.RallyRaceSync:
                    HandleRallySync(reader);
                    break;

                case PacketType.DragRaceSync:
                    HandleDragSync(reader);
                    break;

                case PacketType.PoliceFineSync:
                    HandlePoliceFineSync(reader);
                    break;

                case PacketType.WildlifeSync:
                    HandleWildlifeSync(reader);
                    break;
            }
        }

        private void HandleRallySync(PacketReader reader)
        {
            string stage = reader.ReadString();
            float time = reader.ReadSingle();
            bool finish = reader.ReadBoolean();

            if (rallyTimerFsm != null)
            {
                var tVar = rallyTimerFsm.FsmVariables.FindFsmFloat("Time");
                if (tVar != null) tVar.Value = time;
                if (finish) rallyTimerFsm.SendEvent("FINISH");
            }

            OnLog?.Invoke($"[Ралли] Друг проехал {stage}: {time:F2} сек {(finish ? "и завершил заезд!" : string.Empty)}");
        }

        private void HandleDragSync(PacketReader reader)
        {
            int light = reader.ReadInt32();
            if (dragTreeFsm != null)
            {
                dragTreeFsm.SendEvent($"LIGHT_{light}");
            }
        }

        private void HandlePoliceFineSync(PacketReader reader)
        {
            int fine = reader.ReadInt32();
            string reason = reader.ReadString();

            OnLog?.Invoke($"[Полиция] Другу выписан полицейский штраф: {fine} mk ({reason})");
        }

        private void HandleWildlifeSync(PacketReader reader)
        {
            Vector3 pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (mooseFsm != null)
            {
                mooseFsm.SendEvent("DEATH");
            }
            OnLog?.Invoke("[Фауна] Лось сбит на дороге!");
        }
    }
}
