using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Jobs
{
    public class HeistAndVandalismManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        // Ограбление магазина Теймо и витрины
        private GameObject storeWindowIntact;
        private readonly List<GameObject> storeWindowsBroken = new List<GameObject>();
        private bool isWindowBroken = false;
        private bool isSuppressingWindow = false;

        // Квест переезда Йокке (Helping a drunk move)
        private PlayMakerFSM movingFsm;
        private GameObject movingDropOffObj;
        private GameObject moneySuitcaseObj;
        private PlayMakerFSM suitcaseFsm;
        private bool isSuppressingMoving = false;

        // Месть Флитари: обливание смотровой станции отходами ассенизатора
        private PlayMakerFSM gifuSpillValveFsm;
        private Transform inspectionAreaTr;
        private bool isSuppressingSpill = false;

        public void Reset()
        {
            storeWindowIntact = null;
            storeWindowsBroken.Clear();
            movingFsm = null;
            movingDropOffObj = null;
            moneySuitcaseObj = null;
            suitcaseFsm = null;
            gifuSpillValveFsm = null;
            inspectionAreaTr = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            InitStoreWindows();
            InitMovingQuest();
            InitFleetariVandalism();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void InitStoreWindows()
        {
            try
            {
                storeWindowIntact = GameObject.Find("store_windows");
                storeWindowsBroken.Clear();

                for (int i = 1; i <= 11; i++)
                {
                    string name = $"store_windows_broken_{i:D3}";
                    var broken = GameObject.Find(name);
                    if (broken != null)
                    {
                        storeWindowsBroken.Add(broken);
                    }
                }

                // Отслеживание целостности стекла
                if (storeWindowIntact != null)
                {
                    var watcher = storeWindowIntact.AddComponent<StoreWindowWatcher>();
                    watcher.OnBroken += () =>
                    {
                        if (isSuppressingWindow || isWindowBroken) return;
                        isWindowBroken = true;
                        SendStoreBreakIn(true);
                        OnLog?.Invoke("[Ограбление] Витрина магазина Теймо разбита! Сработала сигнализация!");
                    };
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitStoreWindows Error] {ex.Message}");
            }
        }

        private void InitMovingQuest()
        {
            try
            {
                var movingObj = GameObject.Find("Moving");
                if (movingObj != null)
                {
                    movingFsm = movingObj.GetComponent<PlayMakerFSM>();
                    if (movingFsm != null)
                    {
                        movingFsm.Initialize();
                        if (movingFsm.HasState("Moving done"))
                        {
                            movingFsm.InsertAction("Moving done", () =>
                            {
                                if (isSuppressingMoving) return;
                                SendMovingAction(MovingQuestActionType.QuestFinished, 0);
                                OnLog?.Invoke("[Квест] Переезд Йокке завершен! Награда 5000 mk получена!");
                            }, 0, false);
                        }
                    }
                }

                movingDropOffObj = GameObject.Find("MovingDropOff");

                // Чемодан с деньгами
                moneySuitcaseObj = GameObject.Find("suitcase(itemx)");
                if (moneySuitcaseObj != null)
                {
                    suitcaseFsm = moneySuitcaseObj.GetComponent<PlayMakerFSM>();
                    if (suitcaseFsm != null)
                    {
                        suitcaseFsm.Initialize();
                        if (suitcaseFsm.HasState("Enable murder"))
                        {
                            suitcaseFsm.InsertAction("Enable murder", () =>
                            {
                                if (isSuppressingMoving) return;
                                SendMovingAction(MovingQuestActionType.SuitcaseStolen, 0);
                                OnLog?.Invoke("[Внимание] Чемодан с миллионом взят! Йокке идет в ярость!");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitMovingQuest Error] {ex.Message}");
            }
        }

        private void InitFleetariVandalism()
        {
            try
            {
                // Клапан сброса нечистот Gifu
                var spillObj = GameObject.Find("OpenSpill");
                if (spillObj != null)
                {
                    gifuSpillValveFsm = spillObj.GetComponent<PlayMakerFSM>();
                    if (gifuSpillValveFsm != null)
                    {
                        gifuSpillValveFsm.Initialize();
                        string openState = gifuSpillValveFsm.HasState("Open") ? "Open" : "Pump";
                        gifuSpillValveFsm.InsertAction(openState, () =>
                        {
                            if (isSuppressingSpill) return;
                            CheckSpillAtInspection();
                        }, 0, false);
                    }
                }

                inspectionAreaTr = GameObject.Find("INSPECTION")?.transform;
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitFleetariVandalism Error] {ex.Message}");
            }
        }

        private void CheckSpillAtInspection()
        {
            if (gifuSpillValveFsm == null) return;

            bool isNearInspection = false;
            if (inspectionAreaTr != null && gifuSpillValveFsm.transform != null)
            {
                float distSqr = (gifuSpillValveFsm.transform.position - inspectionAreaTr.position).sqrMagnitude;
                if (distSqr < 2500f) // Радиус 50 метров
                {
                    isNearInspection = true;
                }
            }

            SendSewageSpill(true, isNearInspection);

            if (isNearInspection)
            {
                OnLog?.Invoke("[Вандализм] Офис техосмотра залит нечистотами! Задание Флитари выполнено!");
                CompleteFleetariJobLocally();
            }
        }

        private void CompleteFleetariJobLocally()
        {
            var fleetariObj = GameObject.Find("Fleetari");
            if (fleetariObj != null)
            {
                var fsm = fleetariObj.GetComponent<PlayMakerFSM>();
                if (fsm != null)
                {
                    fsm.Initialize();
                    if (fsm.HasState("Booze Job 2"))
                    {
                        fsm.SendEvent("BOOZE");
                        fsm.Fsm.Event("Booze Job 2");
                    }
                }
            }
        }

        public void SendStoreBreakIn(bool broken)
        {
            using (var packet = new PacketWriter(PacketType.StoreBreakIn))
            {
                packet.WriteBoolean(broken);
                transport.Send(packet);
            }
        }

        public void SendMovingAction(MovingQuestActionType actionType, int param)
        {
            using (var packet = new PacketWriter(PacketType.MovingQuestAction))
            {
                packet.WriteByte((byte)actionType);
                packet.WriteInt32(param);
                transport.Send(packet);
            }
        }

        public void SendSewageSpill(bool open, bool atInspection)
        {
            using (var packet = new PacketWriter(PacketType.SewageSpillAction))
            {
                packet.WriteBoolean(open);
                packet.WriteBoolean(atInspection);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.StoreBreakIn)
            {
                bool broken = reader.ReadBoolean();
                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.StoreBreakIn))
                    {
                        relay.WriteBoolean(broken);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteStoreBreakIn(broken);
            }
            else if (reader.Type == PacketType.MovingQuestAction)
            {
                MovingQuestActionType act = (MovingQuestActionType)reader.ReadByte();
                int param = reader.ReadInt32();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.MovingQuestAction))
                    {
                        relay.WriteByte((byte)act);
                        relay.WriteInt32(param);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteMovingAction(act, param);
            }
            else if (reader.Type == PacketType.SewageSpillAction)
            {
                bool open = reader.ReadBoolean();
                bool atInspection = reader.ReadBoolean();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.SewageSpillAction))
                    {
                        relay.WriteBoolean(open);
                        relay.WriteBoolean(atInspection);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteSewageSpill(open, atInspection);
            }
        }

        private void ApplyRemoteStoreBreakIn(bool broken)
        {
            try
            {
                isSuppressingWindow = true;
                isWindowBroken = broken;

                if (storeWindowIntact != null)
                {
                    storeWindowIntact.SetActive(!broken);
                }

                for (int i = 0; i < storeWindowsBroken.Count; i++)
                {
                    if (storeWindowsBroken[i] != null)
                    {
                        storeWindowsBroken[i].SetActive(broken);
                    }
                }

                if (broken)
                {
                    OnLog?.Invoke("[Ограбление] Витрина магазина Теймо разбита напарником!");
                }
            }
            finally
            {
                isSuppressingWindow = false;
            }
        }

        private void ApplyRemoteMovingAction(MovingQuestActionType act, int param)
        {
            try
            {
                isSuppressingMoving = true;
                if (act == MovingQuestActionType.QuestFinished && movingFsm != null)
                {
                    movingFsm.SendEvent("MP_DONE");
                    movingFsm.Fsm.Event("Moving done");
                    OnLog?.Invoke("[Квест] Переезд Йокке завершен напарником!");
                }
                else if (act == MovingQuestActionType.SuitcaseStolen && suitcaseFsm != null)
                {
                    suitcaseFsm.SendEvent("MP_STEAL");
                    suitcaseFsm.Fsm.Event("Enable murder");
                    OnLog?.Invoke("[Внимание] Напарник взял чемодан с деньгами! Йокке в ярости!");
                }
            }
            finally
            {
                isSuppressingMoving = false;
            }
        }

        private void ApplyRemoteSewageSpill(bool open, bool atInspection)
        {
            try
            {
                isSuppressingSpill = true;
                if (gifuSpillValveFsm != null)
                {
                    gifuSpillValveFsm.SendEvent(open ? "OPEN" : "CLOSE");
                }

                if (atInspection)
                {
                    OnLog?.Invoke("[Вандализм] Напарник залил смотровую станцию отходами!");
                    CompleteFleetariJobLocally();
                }
            }
            finally
            {
                isSuppressingSpill = false;
            }
        }
    }

    public class StoreWindowWatcher : MonoBehaviour
    {
        public Action OnBroken;

        private void OnDisable()
        {
            OnBroken?.Invoke();
        }
    }
}
