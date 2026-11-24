using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Jobs
{
    public class ExpandedQuestsManager : MonoBehaviour
    {
        private NetworkTransport transport;
        public Action<string> OnLog;

        // Сено (Тохвакка)
        private PlayMakerFSM tohvakkaFsm;
        private FsmInt haybaleCountVar;

        // Клубника (Мансикка)
        private PlayMakerFSM strawberryFieldFsm;

        // Автосвалка Флитари (4 остова машин)
        private readonly List<PlayMakerFSM> junkcarFsms = new List<PlayMakerFSM>();

        // Бабушка (Муммо)
        private PlayMakerFSM grandmaFsm;

        // Карточная игра Вентти (Свинопас)
        private PlayMakerFSM venttiPigFsm;

        // Полицейский блокпост
        private PlayMakerFSM policeBreathFsm;

        private bool isSuppressingAction = false;

        public void Reset()
        {
            tohvakkaFsm = null;
            haybaleCountVar = null;
            strawberryFieldFsm = null;
            junkcarFsms.Clear();
            grandmaFsm = null;
            venttiPigFsm = null;
            policeBreathFsm = null;
        }

        public void Initialize(NetworkTransport netTransport)
        {
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            InitTohvakka();
            InitStrawberry();
            InitJunkcars();
            InitGrandma();
            InitVentti();
            InitPolice();
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void InitTohvakka()
        {
            try
            {
                var tohvakkaObj = GameObject.Find("Tohvakka") ?? GameObject.Find("TargetHaybales");
                if (tohvakkaObj != null)
                {
                    tohvakkaFsm = tohvakkaObj.GetComponent<PlayMakerFSM>();
                    if (tohvakkaFsm != null)
                    {
                        tohvakkaFsm.Initialize();
                        haybaleCountVar = tohvakkaFsm.FsmVariables.FindFsmInt("Bales") ?? tohvakkaFsm.FsmVariables.FindFsmInt("Count");
                        string doneState = tohvakkaFsm.HasState("Pay") ? "Pay" : (tohvakkaFsm.HasState("Done") ? "Done" : null);
                        if (doneState != null)
                        {
                            tohvakkaFsm.InsertAction(doneState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendQuestAction(ExpandedQuestType.HaybalePaid, 0);
                                OnLog?.Invoke("[Квест] Доставка рулонов сена Тохвакке оплачена!");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitTohvakka Error] {ex.Message}");
            }
        }

        private void InitStrawberry()
        {
            try
            {
                var field = GameObject.Find("StrawberryField") ?? GameObject.Find("strawberry_pick");
                if (field != null)
                {
                    strawberryFieldFsm = field.GetComponent<PlayMakerFSM>();
                    if (strawberryFieldFsm != null)
                    {
                        strawberryFieldFsm.Initialize();
                        string pickState = strawberryFieldFsm.HasState("Pick") ? "Pick" : (strawberryFieldFsm.HasState("Box full") ? "Box full" : null);
                        if (pickState != null)
                        {
                            strawberryFieldFsm.InsertAction(pickState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendQuestAction(ExpandedQuestType.StrawberryHarvested, 1);
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitStrawberry Error] {ex.Message}");
            }
        }

        private void InitJunkcars()
        {
            try
            {
                junkcarFsms.Clear();
                string[] carNames = new string[] { "JunkCar1", "JunkCar2", "JunkCar3", "JunkCar4" };
                for (int i = 0; i < carNames.Length; i++)
                {
                    var car = GameObject.Find(carNames[i]) ?? GameObject.Find($"junkcar0{i + 1}");
                    if (car != null)
                    {
                        var fsm = car.GetComponent<PlayMakerFSM>();
                        if (fsm != null)
                        {
                            fsm.Initialize();
                            junkcarFsms.Add(fsm);
                            int cIdx = i;
                            string payState = fsm.HasState("Pay") ? "Pay" : (fsm.HasState("Fleetari") ? "Fleetari" : null);
                            if (payState != null)
                            {
                                fsm.InsertAction(payState, () =>
                                {
                                    if (isSuppressingAction) return;
                                    SendQuestAction(ExpandedQuestType.JunkcarDelivered, cIdx);
                                    OnLog?.Invoke($"[Флитари] Остов автомобиля #{cIdx + 1} сдан! Деньги получены.");
                                }, 0, false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitJunkcars Error] {ex.Message}");
            }
        }

        private void InitGrandma()
        {
            try
            {
                var mummo = GameObject.Find("Grandma") ?? GameObject.Find("MUMMO");
                if (mummo != null)
                {
                    grandmaFsm = mummo.GetComponent<PlayMakerFSM>();
                    if (grandmaFsm != null)
                    {
                        grandmaFsm.Initialize();
                        string eatState = grandmaFsm.HasState("Food") ? "Food" : (grandmaFsm.HasState("Pay") ? "Pay" : null);
                        if (eatState != null)
                        {
                            grandmaFsm.InsertAction(eatState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendQuestAction(ExpandedQuestType.GrandmaGroceries, 0);
                                OnLog?.Invoke("[Бабушка] Продукты доставлены! Бабушка угощает кофе и дает деньги.");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitGrandma Error] {ex.Message}");
            }
        }

        private void InitVentti()
        {
            try
            {
                var ventti = GameObject.Find("VenttiPig") ?? GameObject.Find("SpawnToVenttiPig");
                if (ventti != null)
                {
                    venttiPigFsm = ventti.GetComponent<PlayMakerFSM>();
                    if (venttiPigFsm != null)
                    {
                        venttiPigFsm.Initialize();
                        string dealState = venttiPigFsm.HasState("Deal") ? "Deal" : (venttiPigFsm.HasState("Card") ? "Card" : null);
                        if (dealState != null)
                        {
                            venttiPigFsm.InsertAction(dealState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendQuestAction(ExpandedQuestType.VenttiCardDeal, 0);
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitVentti Error] {ex.Message}");
            }
        }

        private void InitPolice()
        {
            try
            {
                var breath = GameObject.Find("breathtest3") ?? GameObject.Find("BreathTest");
                if (breath != null)
                {
                    policeBreathFsm = breath.GetComponent<PlayMakerFSM>();
                    if (policeBreathFsm != null)
                    {
                        policeBreathFsm.Initialize();
                        string blowState = policeBreathFsm.HasState("Blow") ? "Blow" : (policeBreathFsm.HasState("Test") ? "Test" : null);
                        if (blowState != null)
                        {
                            policeBreathFsm.InsertAction(blowState, () =>
                            {
                                if (isSuppressingAction) return;
                                SendQuestAction(ExpandedQuestType.PoliceBlowTest, 0);
                                OnLog?.Invoke("[Полиция] Пройден тест на алкотестере!");
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[InitPolice Error] {ex.Message}");
            }
        }

        public void SendQuestAction(ExpandedQuestType questType, int param)
        {
            using (var packet = new PacketWriter(PacketType.ExpandedQuestAction))
            {
                packet.WriteByte((byte)questType);
                packet.WriteInt32(param);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.ExpandedQuestAction)
            {
                ExpandedQuestType qType = (ExpandedQuestType)reader.ReadByte();
                int param = reader.ReadInt32();

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.ExpandedQuestAction))
                    {
                        relay.WriteByte((byte)qType);
                        relay.WriteInt32(param);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteQuestAction(qType, param);
            }
        }

        private void ApplyRemoteQuestAction(ExpandedQuestType qType, int param)
        {
            try
            {
                isSuppressingAction = true;
                switch (qType)
                {
                    case ExpandedQuestType.HaybalePaid:
                        if (tohvakkaFsm != null) tohvakkaFsm.SendEvent("PAY");
                        OnLog?.Invoke("[Квест] Тохвакка оплатил сено напарнику.");
                        break;

                    case ExpandedQuestType.StrawberryHarvested:
                        OnLog?.Invoke("[Ферма] Напарник собрал ящик клубники.");
                        break;

                    case ExpandedQuestType.JunkcarDelivered:
                        if (param >= 0 && param < junkcarFsms.Count && junkcarFsms[param] != null)
                        {
                            junkcarFsms[param].SendEvent("FLEETARI");
                        }
                        OnLog?.Invoke($"[Флитари] Напарник сдал остов авто #{param + 1}!");
                        break;

                    case ExpandedQuestType.GrandmaGroceries:
                        if (grandmaFsm != null) grandmaFsm.SendEvent("FOOD");
                        OnLog?.Invoke("[Бабушка] Напарник привез продукты бабушке.");
                        break;

                    case ExpandedQuestType.VenttiCardDeal:
                        if (venttiPigFsm != null) venttiPigFsm.SendEvent("DEAL");
                        OnLog?.Invoke("[Вентти] Свинопас сдал карту за столом.");
                        break;

                    case ExpandedQuestType.PoliceBlowTest:
                        if (policeBreathFsm != null) policeBreathFsm.SendEvent("BLOW");
                        OnLog?.Invoke("[Полиция] Напарник дует в алкотестер на блокпосту.");
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
