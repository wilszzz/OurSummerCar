using System;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Items
{
    /// <summary>
    /// Сетевой менеджер наборов инструментов:
    /// 1. Spanner Set (набор рожковых ключей)
    /// 2. Ratchet Set (набор трещотки с головками)
    /// 
    /// Симметричная P2P-модель: любой игрок может мгновенно открыть/закрыть ящик.
    /// Реализовано по архитектуре WreckMP (NetItemsManager).
    /// </summary>
    public class ToolSetManager
    {
        public static ToolSetManager Instance { get; private set; }

        private readonly NetworkTransport transport;

        // FSM крышки набора рожковых ключей (Spanner Set)
        private PlayMakerFSM spannerSetTopFsm;
        private FsmBool spannerSetOpenVar;

        // FSM крышки набора трещотки (Ratchet Set)
        private PlayMakerFSM ratchetSetTopFsm;
        private FsmBool ratchetSetOpenVar;

        // Флаги подавления эха при сетевом переключении
        private bool isUpdatingSpanner = false;
        private bool isUpdatingRatchet = false;

        private float nextSearchTime = 0f;

        public event Action<string> OnLog;

        public void Reset()
        {
            spannerSetTopFsm = null;
            ratchetSetTopFsm = null;
            spannerSetOpenVar = null;
            ratchetSetOpenVar = null;
            nextSearchTime = 0f;
        }

        public ToolSetManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeToolSets()
        {
            TryFindAndHookToolSets();
        }

        public void Update()
        {
            if (spannerSetTopFsm == null || ratchetSetTopFsm == null)
            {
                if (Time.time >= nextSearchTime)
                {
                    nextSearchTime = Time.time + 30.0f;
                    TryFindAndHookToolSets();
                }
            }
        }

        private void TryFindAndHookToolSets()
        {
            try
            {
                // 1. Spanner Set (набор рожковых ключей в чемоданчике)
                if (spannerSetTopFsm == null)
                {
                    Transform spannerTop = null;
                    Transform items = GameObject.Find("ITEMS")?.transform;
                    if (items != null)
                    {
                        spannerTop = items.Find("spanner set(itemx)/Pivot/top");
                    }
                    if (spannerTop == null)
                    {
                        GameObject spannerGo = GameObject.Find("spanner set(itemx)");
                        if (spannerGo != null)
                        {
                            spannerTop = spannerGo.transform.Find("Pivot/top");
                        }
                    }

                    if (spannerTop != null)
                    {
                        spannerSetTopFsm = spannerTop.GetComponent<PlayMakerFSM>();
                        if (spannerSetTopFsm != null)
                        {
                            InjectTopFsm(spannerSetTopFsm, false, out spannerSetOpenVar);
                            OnLog?.Invoke("[Инструменты] Чемоданчик с рожковыми ключами (Spanner Set) синхронизирован.");
                        }
                    }
                }

                // 2. Ratchet Set (набор трещотки)
                if (ratchetSetTopFsm == null)
                {
                    Transform ratchetTop = null;
                    GameObject ratchetGo = GameObject.Find("DatabaseOrders/Ratchet Set") 
                                         ?? GameObject.Find("Database/DatabaseOrders/Ratchet Set")
                                         ?? GameObject.Find("Ratchet Set");
                    if (ratchetGo != null)
                    {
                        ratchetTop = ratchetGo.transform.Find("Hinge/Pivot/top");
                    }

                    if (ratchetTop != null)
                    {
                        ratchetSetTopFsm = ratchetTop.GetComponent<PlayMakerFSM>();
                        if (ratchetSetTopFsm != null)
                        {
                            InjectTopFsm(ratchetSetTopFsm, true, out ratchetSetOpenVar);
                            OnLog?.Invoke("[Инструменты] Набор трещотки (Ratchet Set) синхронизирован.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ToolSetManager] Ошибка поиска инструментов: {ex.Message}");
            }
        }

        private void InjectTopFsm(PlayMakerFSM fsm, bool isRatchet, out FsmBool isOpenVar)
        {
            fsm.Initialize();
            isOpenVar = fsm.FsmVariables.FindFsmBool("Open");

            FsmEvent mpOpen = fsm.AddEvent("MP_OPEN");
            fsm.AddGlobalTransition(mpOpen, "Open");

            FsmEvent mpClose = fsm.AddEvent("MP_CLOSE");
            fsm.AddGlobalTransition(mpClose, "Close");

            fsm.InsertAction("Open", () => OnLocalStateChanged(isRatchet, true), 0, false);
            fsm.InsertAction("Close", () => OnLocalStateChanged(isRatchet, false), 0, false);
            fsm.Initialize();
        }

        private void OnLocalStateChanged(bool isRatchet, bool isOpen)
        {
            if (isRatchet)
            {
                if (isUpdatingRatchet)
                {
                    isUpdatingRatchet = false;
                    return;
                }
            }
            else
            {
                if (isUpdatingSpanner)
                {
                    isUpdatingSpanner = false;
                    return;
                }
            }

            if (!transport.IsRunning) return;

            SendToolSetToggle(isRatchet, isOpen);
            OnLog?.Invoke($"[Инструменты] {(isRatchet ? "Трещотка" : "Набор ключей")} переключен локально -> {(isOpen ? "Открыт" : "Закрыт")}");
        }

        private void SendToolSetToggle(bool isRatchet, bool isOpen)
        {
            using (var packet = new PacketWriter(PacketType.ToolSetToggle))
            {
                packet.WriteBoolean(isRatchet);
                packet.WriteBoolean(isOpen);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.ToolSetToggle)
            {
                bool isRatchet = reader.ReadBoolean();
                bool targetOpen = reader.ReadBoolean();

                var fsm = isRatchet ? ratchetSetTopFsm : spannerSetTopFsm;
                var openVar = isRatchet ? ratchetSetOpenVar : spannerSetOpenVar;

                if (fsm == null) return;

                // Если состояние уже совпадает, ничего делать не нужно
                if (openVar != null && openVar.Value == targetOpen) return;

                if (isRatchet) isUpdatingRatchet = true;
                else isUpdatingSpanner = true;

                fsm.SendEvent(targetOpen ? "MP_OPEN" : "MP_CLOSE");
                OnLog?.Invoke($"[Инструменты] Удаленный игрок {(targetOpen ? "открыл" : "закрыл")} {(isRatchet ? "трещотку" : "набор ключей")}");
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (spannerSetOpenVar != null)
            {
                SendToolSetToggle(false, spannerSetOpenVar.Value);
            }
            if (ratchetSetOpenVar != null)
            {
                SendToolSetToggle(true, ratchetSetOpenVar.Value);
            }
        }
    }
}
