using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Gambling
{
    public class GamblingManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        // Видеопокер в пабе
        private GameObject videoPokerObj;
        private PlayMakerFSM videoPokerBreakFsm;
        private PlayMakerFSM videoPokerDataFsm;
        private readonly Dictionary<string, PlayMakerFSM> pokerButtons = new Dictionary<string, PlayMakerFSM>();

        // Игровой автомат в магазине (Markka-lama / Slot machine)
        private GameObject slotMachineObj;
        private PlayMakerFSM slotInsertCoinFsm;
        private PlayMakerFSM slotButtonFsm;
        private PlayMakerFSM slotCashOutFsm;
        private PlayMakerFSM hookSlotFsm;

        private bool isSuppressingAction = false;

        public void Reset()
        {
            videoPokerObj = null;
            videoPokerBreakFsm = null;
            videoPokerDataFsm = null;
            pokerButtons.Clear();
            slotMachineObj = null;
            slotInsertCoinFsm = null;
            slotButtonFsm = null;
            slotCashOutFsm = null;
            hookSlotFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            InitVideoPoker();
            InitSlotMachine();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void InitVideoPoker()
        {
            try
            {
                videoPokerObj = GameObject.Find("VideoPoker") ?? GameObject.Find("videopoker");
                if (videoPokerObj != null)
                {
                    var fsms = videoPokerObj.GetComponents<PlayMakerFSM>();
                    for (int i = 0; i < fsms.Length; i++)
                    {
                        if (fsms[i].FsmName == "Break") videoPokerBreakFsm = fsms[i];
                        if (fsms[i].FsmName == "Data") videoPokerDataFsm = fsms[i];
                    }

                    if (videoPokerBreakFsm != null)
                    {
                        videoPokerBreakFsm.Initialize();
                        videoPokerBreakFsm.InsertAction("Destroy", () =>
                        {
                            if (isSuppressingAction) return;
                            SendGamblingAction(GamblingActionType.MachineBreakTowed, 1, 0);
                            OnLog?.Invoke("[Ограбление] Видеопокер сорван с креплений!");
                        }, 0, false);
                    }
                }

                // Кнопки покера
                string[] buttonNames = new string[]
                {
                    "Pokeri-Buttons-InsertCoin",
                    "Pokeri-Buttons-Bet",
                    "Pokeri-Buttons-Deal",
                    "Pokeri-Buttons-TakeWin",
                    "Pokeri-Buttons-Double",
                    "Pokeri-Buttons-Double-High",
                    "Pokeri-Buttons-Double-Low",
                    "Pokeri-Buttons-Hold1",
                    "Pokeri-Buttons-Hold2",
                    "Pokeri-Buttons-Hold3",
                    "Pokeri-Buttons-Hold4",
                    "Pokeri-Buttons-Hold5"
                };

                for (int i = 0; i < buttonNames.Length; i++)
                {
                    var btnObj = GameObject.Find(buttonNames[i]);
                    if (btnObj != null)
                    {
                        var fsm = btnObj.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            fsm.Initialize();
                            pokerButtons[buttonNames[i]] = fsm;
                            byte idx = (byte)i;
                            string targetState = fsm.HasState("ButtonPressed") ? "ButtonPressed" : (fsm.HasState("Insert Coin") ? "Insert Coin" : null);
                            if (targetState != null)
                            {
                                fsm.InsertAction(targetState, () =>
                                {
                                    if (isSuppressingAction) return;
                                    SendGamblingAction(GamblingActionType.Hold, idx, 0);
                                }, 0, false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitVideoPoker Error] {ex.Message}");
            }
        }

        private void InitSlotMachine()
        {
            try
            {
                slotMachineObj = GameObject.Find("Slotmachine") ?? GameObject.Find("SlotMachine");
                
                var coinObj = GameObject.Find("slot_insert_coin");
                if (coinObj != null) slotInsertCoinFsm = coinObj.GetComponent<PlayMakerFSM>();

                var btnObj = GameObject.Find("slot_button");
                if (btnObj != null)
                {
                    slotButtonFsm = btnObj.GetComponent<PlayMakerFSM>();
                    if (slotButtonFsm != null)
                    {
                        slotButtonFsm.Initialize();
                        string st = slotButtonFsm.HasState("State 1") ? "State 1" : (slotButtonFsm.HasState("Spin") ? "Spin" : null);
                        if (st != null)
                        {
                            slotButtonFsm.InsertAction(st, () =>
                            {
                                if (isSuppressingAction) return;
                                SendGamblingAction(GamblingActionType.SlotSpin, 0, 0);
                            }, 0, false);
                        }
                    }
                }

                var cashOutObj = GameObject.Find("slot_cash_out");
                if (cashOutObj != null) slotCashOutFsm = cashOutObj.GetComponent<PlayMakerFSM>();

                var hookObj = GameObject.Find("HookSlot");
                if (hookObj != null)
                {
                    hookSlotFsm = hookObj.GetComponent<PlayMakerFSM>();
                    if (hookSlotFsm != null)
                    {
                        hookSlotFsm.Initialize();
                        if (hookSlotFsm.HasState("Remove rope"))
                        {
                            hookSlotFsm.InsertAction("Remove rope", () =>
                            {
                                if (isSuppressingAction) return;
                                SendGamblingAction(GamblingActionType.MachineBreakTowed, 2, 0);
                                OnLog?.Invoke("[Ограбление] Игровой автомат сорван буксировочным тросом!");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitSlotMachine Error] {ex.Message}");
            }
        }

        public void SendGamblingAction(GamblingActionType action, byte buttonIndex, int value)
        {
            using (var packet = new PacketWriter(PacketType.GamblingAction))
            {
                packet.WriteByte((byte)action);
                packet.WriteByte(buttonIndex);
                packet.WriteInt32(value);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.GamblingAction)
            {
                GamblingActionType action = (GamblingActionType)reader.ReadByte();
                byte btnIdx = reader.ReadByte();
                int val = reader.ReadInt32();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.GamblingAction))
                    {
                        relay.WriteByte((byte)action);
                        relay.WriteByte(btnIdx);
                        relay.WriteInt32(val);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteGamblingAction(action, btnIdx, val);
            }
        }

        private void ApplyRemoteGamblingAction(GamblingActionType action, byte btnIdx, int val)
        {
            try
            {
                isSuppressingAction = true;
                switch (action)
                {
                    case GamblingActionType.Hold:
                    case GamblingActionType.Bet:
                    case GamblingActionType.Deal:
                    case GamblingActionType.InsertCoin:
                    case GamblingActionType.TakeWin:
                        // Нажатие кнопки видеопокера
                        string[] buttonNames = new string[]
                        {
                            "Pokeri-Buttons-InsertCoin",
                            "Pokeri-Buttons-Bet",
                            "Pokeri-Buttons-Deal",
                            "Pokeri-Buttons-TakeWin",
                            "Pokeri-Buttons-Double",
                            "Pokeri-Buttons-Double-High",
                            "Pokeri-Buttons-Double-Low",
                            "Pokeri-Buttons-Hold1",
                            "Pokeri-Buttons-Hold2",
                            "Pokeri-Buttons-Hold3",
                            "Pokeri-Buttons-Hold4",
                            "Pokeri-Buttons-Hold5"
                        };
                        if (btnIdx < buttonNames.Length && pokerButtons.TryGetValue(buttonNames[btnIdx], out var fsm) && fsm != null)
                        {
                            string st = fsm.HasState("ButtonPressed") ? "ButtonPressed" : (fsm.HasState("Insert Coin") ? "Insert Coin" : null);
                            if (st != null) fsm.Fsm.Event(st);
                        }
                        break;

                    case GamblingActionType.SlotSpin:
                        if (slotButtonFsm != null)
                        {
                            string st = slotButtonFsm.HasState("State 1") ? "State 1" : "Spin";
                            slotButtonFsm.Fsm.Event(st);
                        }
                        break;

                    case GamblingActionType.MachineBreakTowed:
                        if (btnIdx == 1 && videoPokerBreakFsm != null)
                        {
                            videoPokerBreakFsm.SendEvent("DESTROY");
                            OnLog?.Invoke("[Ограбление] Видеопокер сорван напарником!");
                        }
                        else if (btnIdx == 2 && hookSlotFsm != null)
                        {
                            hookSlotFsm.SendEvent("ROPE_BREAK");
                            OnLog?.Invoke("[Ограбление] Автомат сорван напарником!");
                        }
                        break;
                }
            }
            finally
            {
                isSuppressingAction = false;
            }
        }
    }
}
