using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    public class WorldInteractionManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        // Колонки на АЗС (98, Дизель, Мазут MPÖ)
        private readonly List<PlayMakerFSM> fuelNozzles = new List<PlayMakerFSM>();
        private readonly List<FsmFloat> fuelMeters = new List<FsmFloat>();

        // Электрощиток и предохранители
        private readonly List<PlayMakerFSM> fuseHolders = new List<PlayMakerFSM>();

        // Кофе и турка
        private PlayMakerFSM coffeePotFsm;
        private PlayMakerFSM coffeeCupFsm;

        // Камин и спички
        private PlayMakerFSM fireplaceFsm;

        // Компьютер
        private PlayMakerFSM computerFsm;

        private bool isSuppressingAction = false;

        public void Reset()
        {
            fuelNozzles.Clear();
            fuelMeters.Clear();
            fuseHolders.Clear();
            coffeePotFsm = null;
            coffeeCupFsm = null;
            fireplaceFsm = null;
            computerFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            InitFuelPumps();
            InitFuseBox();
            InitCoffee();
            InitFireplace();
            InitComputer();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void InitFuelPumps()
        {
            try
            {
                fuelNozzles.Clear();
                fuelMeters.Clear();

                string[] pumpNames = new string[]
                {
                    "gasoline_pistol_98",
                    "gasoline_pistol_Diesel",
                    "gasoline_pistol_PÖ"
                };

                for (int i = 0; i < pumpNames.Length; i++)
                {
                    var pistol = GameObject.Find(pumpNames[i]);
                    if (pistol != null)
                    {
                        var fsm = pistol.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            fsm.Initialize();
                            fuelNozzles.Add(fsm);
                            byte pIdx = (byte)i;

                            var meterVar = fsm.FsmVariables.FindFsmFloat("FuelLitres") ?? fsm.FsmVariables.FindFsmFloat("Amount");
                            fuelMeters.Add(meterVar);

                            string pumpState = fsm.HasState("Pump") ? "Pump" : (fsm.HasState("Pumping") ? "Pumping" : null);
                            if (pumpState != null)
                            {
                                fsm.InsertAction(pumpState, () =>
                                {
                                    if (isSuppressingAction) return;
                                    float amount = meterVar != null ? meterVar.Value : 0f;
                                    SendFuelPumpAction(pIdx, true, amount);
                                }, 0, false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitFuelPumps Error] {ex.Message}");
            }
        }

        private void InitFuseBox()
        {
            try
            {
                fuseHolders.Clear();
                for (int i = 1; i <= 5; i++)
                {
                    string name = $"fuseholder0{i}";
                    var holder = GameObject.Find(name);
                    if (holder != null)
                    {
                        var fsm = holder.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            fsm.Initialize();
                            fuseHolders.Add(fsm);
                            byte fIdx = (byte)(i - 1);

                            string screwState = fsm.HasState("Screw") ? "Screw" : (fsm.HasState("State 1") ? "State 1" : null);
                            if (screwState != null)
                            {
                                fsm.InsertAction(screwState, () =>
                                {
                                    if (isSuppressingAction) return;
                                    SendFuseAction(fIdx, true);
                                }, 0, false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitFuseBox Error] {ex.Message}");
            }
        }

        private void InitCoffee()
        {
            try
            {
                var pot = GameObject.Find("coffee_pan_cap") ?? GameObject.Find("HandCoffeeHome");
                if (pot != null)
                {
                    coffeePotFsm = pot.GetComponent<PlayMakerFSM>();
                    if (coffeePotFsm != null)
                    {
                        coffeePotFsm.Initialize();
                        string boilState = coffeePotFsm.HasState("Boil") ? "Boil" : (coffeePotFsm.HasState("State 2") ? "State 2" : null);
                        if (boilState != null)
                        {
                            coffeePotFsm.InsertAction(boilState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendCoffeeAction(1);
                                OnLog?.Invoke("[Кухня] Кофе в турке закипел!");
                            }, 0, false);
                        }
                    }
                }

                var cup = GameObject.Find("coffee_cup");
                if (cup != null) coffeeCupFsm = cup.GetComponent<PlayMakerFSM>();
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitCoffee Error] {ex.Message}");
            }
        }

        private void InitFireplace()
        {
            try
            {
                var fp = GameObject.Find("Fireplace");
                if (fp != null)
                {
                    fireplaceFsm = fp.GetComponent<PlayMakerFSM>();
                    if (fireplaceFsm != null)
                    {
                        fireplaceFsm.Initialize();
                        string burnState = fireplaceFsm.HasState("Burn") ? "Burn" : (fireplaceFsm.HasState("State 2") ? "State 2" : null);
                        if (burnState != null)
                        {
                            fireplaceFsm.InsertAction(burnState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendFireplaceAction(true);
                                OnLog?.Invoke("[Гостиная] Камин растоплен спичками!");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitFireplace Error] {ex.Message}");
            }
        }

        private void InitComputer()
        {
            try
            {
                var comp = GameObject.Find("COMPUTER") ?? GameObject.Find("computer(Clone)");
                if (comp != null)
                {
                    computerFsm = comp.GetComponent<PlayMakerFSM>();
                    if (computerFsm != null)
                    {
                        computerFsm.Initialize();
                        string onState = computerFsm.HasState("ON") ? "ON" : (computerFsm.HasState("Power") ? "Power" : null);
                        if (onState != null)
                        {
                            computerFsm.InsertAction(onState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendComputerAction(1);
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitComputer Error] {ex.Message}");
            }
        }

        public void SendFuelPumpAction(byte pumpIndex, bool pumping, float litres)
        {
            using (var packet = new PacketWriter(PacketType.FuelPumpAction))
            {
                packet.WriteByte(pumpIndex);
                packet.WriteBoolean(pumping);
                packet.WriteSingle(litres);
                transport.Send(packet);
            }
        }

        public void SendFuseAction(byte fuseIndex, bool screwedIn)
        {
            using (var packet = new PacketWriter(PacketType.FuseBoxAction))
            {
                packet.WriteByte(fuseIndex);
                packet.WriteBoolean(screwedIn);
                transport.Send(packet);
            }
        }

        public void SendCoffeeAction(byte action)
        {
            using (var packet = new PacketWriter(PacketType.CoffeeBrewAction))
            {
                packet.WriteByte(action);
                transport.Send(packet);
            }
        }

        public void SendFireplaceAction(bool burning)
        {
            using (var packet = new PacketWriter(PacketType.FireplaceAction))
            {
                packet.WriteBoolean(burning);
                transport.Send(packet);
            }
        }

        public void SendComputerAction(byte action)
        {
            using (var packet = new PacketWriter(PacketType.ComputerAction))
            {
                packet.WriteByte(action);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.FuelPumpAction)
            {
                byte pIdx = reader.ReadByte();
                bool pumping = reader.ReadBoolean();
                float litres = reader.ReadSingle();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.FuelPumpAction))
                    {
                        relay.WriteByte(pIdx);
                        relay.WriteBoolean(pumping);
                        relay.WriteSingle(litres);
                        transport.Send(relay, connectionId);
                    }
                }

                if (pIdx < fuelNozzles.Count && fuelNozzles[pIdx] != null)
                {
                    if (pIdx < fuelMeters.Count && fuelMeters[pIdx] != null)
                    {
                        fuelMeters[pIdx].Value = litres;
                    }
                    OnLog?.Invoke($"[АЗС] Заправка топливом: {litres:F1} л.");
                }
            }
            else if (reader.Type == PacketType.FuseBoxAction)
            {
                byte fIdx = reader.ReadByte();
                bool screwed = reader.ReadBoolean();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.FuseBoxAction))
                    {
                        relay.WriteByte(fIdx);
                        relay.WriteBoolean(screwed);
                        transport.Send(relay, connectionId);
                    }
                }

                if (fIdx < fuseHolders.Count && fuseHolders[fIdx] != null)
                {
                    fuseHolders[fIdx].SendEvent("SCREW");
                    OnLog?.Invoke($"[Электричество] Предохранитель #{fIdx + 1} заменен напарником.");
                }
            }
            else if (reader.Type == PacketType.CoffeeBrewAction)
            {
                if (coffeePotFsm != null)
                {
                    coffeePotFsm.SendEvent("BOIL");
                    OnLog?.Invoke("[Кухня] Напарник сварил свежий кофе!");
                }
            }
            else if (reader.Type == PacketType.FireplaceAction)
            {
                bool burning = reader.ReadBoolean();
                if (fireplaceFsm != null)
                {
                    fireplaceFsm.SendEvent(burning ? "IGNITE" : "EXTINGUISH");
                    OnLog?.Invoke("[Гостиная] Напарник растопил камин.");
                }
            }
            else if (reader.Type == PacketType.ComputerAction)
            {
                if (computerFsm != null)
                {
                    computerFsm.SendEvent("POWER");
                    OnLog?.Invoke("[Комната] Компьютер включен напарником.");
                }
            }
        }
    }
}
