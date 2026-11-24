using System;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Environment
{
    /// <summary>
    /// Менеджер сетевой синхронизации мировых опасностей и окружения:
    /// 1. Гнезда шершней в Ruscko и коттедже (атака роя, уничтожение пеной огнетушителя).
    /// 2. Пассажирский поезд и рейсовый автобус (гудки, столкновения, поездка по билету).
    /// 3. Гроза, удары молний и выбивание керамических пробок в главном электрощитке дома.
    /// 4. Пожар и сгорание дома до основания (burnt_house_base, burnt_house_stack).
    /// 5. Полицейский алкотестер и полоса с шипами на блокпосту.
    /// 6. Тюрьма и заключение (отбывание наказания в камере, счетчик дней, надзиратель).
    /// 7. Физиология игрока (рой мух от загрязнения, приступы рвоты при алкогольном отравлении).
    /// </summary>
    public class WorldHazardsManager : MonoBehaviour
    {
        public static WorldHazardsManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Осиные гнезда
        private GameObject rusckoWaspHive;
        private GameObject cottageWaspHive;
        private PlayMakerFSM rusckoWaspFsm;
        private PlayMakerFSM cottageWaspFsm;
        private bool isSuppressingWasp = false;

        // Поезд и Автобус
        private PlayMakerFSM trainMoveFsm;
        private PlayMakerFSM trainWhistleFsm;
        private PlayMakerFSM trainPlayerFsm;
        private PlayMakerFSM busThrottleFsm;
        private PlayMakerFSM busFsm;
        private bool isSuppressingTrainBus = false;

        // Электрощиток и пробки дома
        private PlayMakerFSM fuseTableFsm;
        private PlayMakerFSM[] individualFuseFsms = new PlayMakerFSM[7];
        private bool isSuppressingFuses = false;

        // Сгоревший дом
        private GameObject burntHouseBase;
        private GameObject burntHouseStack;
        private GameObject normalHouse;

        // Полицейский алкотестер
        private PlayMakerFSM alcometerFsm;
        private bool isSuppressingAlco = false;

        // Тюрьма
        private GameObject inJailObj;
        private PlayMakerFSM guardReactFsm;
        private PlayMakerFSM guardSpeakFsm;
        private bool isSuppressingJail = false;

        // Физиология: Мухи и Рвота
        private PlayMakerFSM fliesFsm;
        private PlayMakerFSM vomitFsm;
        private PlayMakerFSM vomitProjectorFsm;
        private bool isSuppressingPhysiology = false;

        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanHazardObjects();
            OnLog?.Invoke("[Опасности/Мир] Менеджер шершней, поезда, автобуса, молний, пожаров, тюрьмы и физиологии инициализирован");
        }

        public void Reset()
        {
            rusckoWaspHive = null;
            cottageWaspHive = null;
            rusckoWaspFsm = null;
            cottageWaspFsm = null;
            trainMoveFsm = null;
            trainWhistleFsm = null;
            trainPlayerFsm = null;
            busThrottleFsm = null;
            busFsm = null;
            fuseTableFsm = null;
            for (int i = 0; i < individualFuseFsms.Length; i++) individualFuseFsms[i] = null;
            burntHouseBase = null;
            burntHouseStack = null;
            normalHouse = null;
            alcometerFsm = null;
            inJailObj = null;
            guardReactFsm = null;
            guardSpeakFsm = null;
            fliesFsm = null;
            vomitFsm = null;
            vomitProjectorFsm = null;
            isSuppressingWasp = false;
            isSuppressingTrainBus = false;
            isSuppressingFuses = false;
            isSuppressingAlco = false;
            isSuppressingJail = false;
            isSuppressingPhysiology = false;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void ScanHazardObjects()
        {
            try
            {
                // 1. Осиные гнезда (WaspHiveTrigger в Ruscko и на чердаке коттеджа)
                var allHives = GameObject.FindObjectsOfType<PlayMakerFSM>();
                if (allHives != null)
                {
                    for (int i = 0; i < allHives.Length; i++)
                    {
                        var fsm = allHives[i];
                        if (fsm != null && fsm.gameObject != null)
                        {
                            if (fsm.gameObject.name == "TargetHive")
                            {
                                bool isRuscko = fsm.transform.root.name.IndexOf("ruscko", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                fsm.transform.root.name.IndexOf("rusto", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (isRuscko)
                                {
                                    rusckoWaspFsm = fsm;
                                    rusckoWaspHive = fsm.gameObject;
                                }
                                else
                                {
                                    cottageWaspFsm = fsm;
                                    cottageWaspHive = fsm.gameObject;
                                }
                            }
                            else if (fsm.gameObject.name == "WaspHiveTrigger" && fsm.HasState("Put out"))
                            {
                                bool isRuscko = fsm.transform.root.name.IndexOf("ruscko", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                fsm.transform.root.name.IndexOf("rusto", StringComparison.OrdinalIgnoreCase) >= 0;
                                fsm.InsertAction("Put out", () =>
                                {
                                    if (isSuppressingWasp) return;
                                    SendWaspNestDestroyed(isRuscko);
                                }, 0, false);
                            }
                        }
                    }
                }

                // 2. Поезд и Автобус
                var trainGo = GameObject.Find("TRAIN");
                if (trainGo != null)
                {
                    trainMoveFsm = PlayMakerUtilities.FindFsmOnGameObject(trainGo, "Move");
                    trainWhistleFsm = PlayMakerUtilities.FindFsmOnGameObject(trainGo, "Whistle");
                    trainPlayerFsm = PlayMakerUtilities.FindFsmOnGameObject(trainGo, "Player");

                    if (trainWhistleFsm != null && trainWhistleFsm.HasState("State 2"))
                    {
                        trainWhistleFsm.InsertAction("State 2", () =>
                        {
                            if (isSuppressingTrainBus) return;
                            SendTrainWhistle();
                        }, 0, false);
                    }
                }

                var busGo = GameObject.Find("BUS");
                if (busGo != null)
                {
                    busThrottleFsm = PlayMakerUtilities.FindFsmOnGameObject(busGo, "Throttle");
                    busFsm = PlayMakerUtilities.FindFsmOnGameObject(busGo, "DriverSpeaking") ?? busGo.GetComponent<PlayMakerFSM>();
                }

                // 3. Электрощиток с пробками дома (FuseTable и Fuse1..Fuse7)
                var fuseTableGo = GameObject.Find("FuseTable") ?? GameObject.Find("Fusetable") ?? GameObject.Find("DashFusebox");
                if (fuseTableGo != null)
                {
                    fuseTableFsm = PlayMakerUtilities.FindFsmOnGameObject(fuseTableGo, "Fuses") ?? fuseTableGo.GetComponent<PlayMakerFSM>();
                }

                for (int f = 1; f <= 7; f++)
                {
                    var fGo = GameObject.Find($"Fuse{f}");
                    if (fGo != null)
                    {
                        individualFuseFsms[f - 1] = PlayMakerUtilities.FindFsmOnGameObject(fGo, "Assembly");
                    }
                }

                // 4. Сгоревший дом
                burntHouseBase = GameObject.Find("burnt_house_base");
                burntHouseStack = GameObject.Find("burnt_house_stack");
                normalHouse = GameObject.Find("HOUSE") ?? GameObject.Find("House");

                // 5. Алкотестер полиции
                var alcoGo = GameObject.Find("AlcometerTrigger") ?? GameObject.Find("CopRadar1");
                if (alcoGo != null)
                {
                    alcometerFsm = PlayMakerUtilities.FindFsmOnGameObject(alcoGo, "Use") ??
                                   PlayMakerUtilities.FindFsmOnGameObject(alcoGo, "InitRotate") ??
                                   alcoGo.GetComponent<PlayMakerFSM>();

                    if (alcometerFsm != null && alcometerFsm.HasState("Blowing"))
                    {
                        alcometerFsm.InsertAction("Blowing", () =>
                        {
                            if (isSuppressingAlco) return;
                            SendAlcometerBlow();
                        }, 0, false);
                    }
                }

                // 6. Тюрьма и охранник
                inJailObj = GameObject.Find("InJail") ?? GameObject.Find("JAIL");
                var guardGo = GameObject.Find("Guard");
                if (guardGo != null)
                {
                    guardReactFsm = PlayMakerUtilities.FindFsmOnGameObject(guardGo, "React");
                    guardSpeakFsm = PlayMakerUtilities.FindFsmOnGameObject(guardGo, "Speak");
                }

                // 7. Физиология: Мухи и Рвота
                var fliesGo = GameObject.Find("Flies");
                if (fliesGo != null)
                {
                    fliesFsm = PlayMakerUtilities.FindFsmOnGameObject(fliesGo, "Dirtiness");
                }

                var vomitGo = GameObject.Find("Vomit");
                if (vomitGo != null)
                {
                    vomitFsm = PlayMakerUtilities.FindFsmOnGameObject(vomitGo, "Anim");
                    if (vomitFsm != null && vomitFsm.HasState("1"))
                    {
                        vomitFsm.InsertAction("1", () =>
                        {
                            if (isSuppressingPhysiology) return;
                            SendPlayerPhysiology(0, true);
                        }, 0, false);
                    }
                }

                var vomitProjGo = GameObject.Find("VomitProjector");
                if (vomitProjGo != null)
                {
                    vomitProjectorFsm = PlayMakerUtilities.FindFsmOnGameObject(vomitProjGo, "Effect");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Опасности/Мир] Ошибка сканирования: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 10f;
                if (trainMoveFsm == null || busThrottleFsm == null || alcometerFsm == null)
                {
                    ScanHazardObjects();
                }
            }
        }

        public void SendWaspNestDestroyed(bool isRuscko)
        {
            if (isSuppressingWasp || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.WaspNestSync))
            {
                packet.WriteBoolean(isRuscko);
                packet.WriteBoolean(true); // Уничтожено
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Шершни] Гнездо ос в {(isRuscko ? "Ruscko" : "коттедже")} уничтожено огнетушителем!");
        }

        public void SendTrainWhistle()
        {
            if (isSuppressingTrainBus || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TrainBusSync))
            {
                packet.WriteByte(1); // Гудок поезда
                packet.WriteBoolean(true);
                transport.Send(packet);
            }
        }

        public void SendBusPassengerRide(bool insideBus)
        {
            if (isSuppressingTrainBus || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TrainBusSync))
            {
                packet.WriteByte(2); // Автобус
                packet.WriteBoolean(insideBus);
                transport.Send(packet);
            }
        }

        public void SendLightningBlownFuses()
        {
            if (isSuppressingFuses || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.ThunderstormSync))
            {
                packet.WriteByte(1); // Выбивание пробок молнией
                transport.Send(packet);
            }
            OnLog?.Invoke("[Гроза] Удар молнии выбил пробки в щитке дома!");
        }

        public void SendHouseBurnDown()
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.HouseBurnSync))
            {
                packet.WriteBoolean(true);
                transport.Send(packet);
            }
            OnLog?.Invoke("[Пожар] Дом полностью сгорел дотла!");
        }

        public void SendAlcometerBlow()
        {
            if (isSuppressingAlco || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.PoliceRoadblockSync))
            {
                packet.WriteByte(1); // Замер алкоголя
                transport.Send(packet);
            }
            OnLog?.Invoke("[Полиция] Замер алкоголя в выдыхаемом воздухе (алкотестер)");
        }

        public void SendJailSentence(bool inJail, int daysLeft)
        {
            if (isSuppressingJail || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.JailSentenceSync))
            {
                packet.WriteBoolean(inJail);
                packet.WriteInt32(daysLeft);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Тюрьма] Статус заключения: {inJail} (осталось дней: {daysLeft})");
        }

        public void SendPlayerPhysiology(int dirtiness, bool vomiting)
        {
            if (isSuppressingPhysiology || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.PlayerPhysiologySync))
            {
                packet.WriteInt32(dirtiness);
                packet.WriteBoolean(vomiting);
                transport.Send(packet);
            }
            if (vomiting) OnLog?.Invoke("[Физиология] Сильный приступ рвоты у персонажа!");
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.WaspNestSync:
                    HandleWaspNestSync(reader);
                    break;

                case PacketType.TrainBusSync:
                    HandleTrainBusSync(reader);
                    break;

                case PacketType.ThunderstormSync:
                    HandleThunderstormSync(reader);
                    break;

                case PacketType.HouseBurnSync:
                    HandleHouseBurnSync(reader);
                    break;

                case PacketType.PoliceRoadblockSync:
                    HandleAlcoSync(reader);
                    break;

                case PacketType.JailSentenceSync:
                    HandleJailSync(reader);
                    break;

                case PacketType.PlayerPhysiologySync:
                    HandlePhysiologySync(reader);
                    break;
            }
        }

        private void HandleWaspNestSync(PacketReader reader)
        {
            bool isRuscko = reader.ReadBoolean();
            bool destroyed = reader.ReadBoolean();

            isSuppressingWasp = true;
            try
            {
                var fsm = isRuscko ? rusckoWaspFsm : cottageWaspFsm;
                if (fsm != null)
                {
                    var hiveVar = fsm.FsmVariables.FindFsmInt("WaspsInHive");
                    if (hiveVar != null) hiveVar.Value = 0;
                    fsm.SendEvent("SLEEP");
                }

                var target = isRuscko ? rusckoWaspHive : cottageWaspHive;
                if (target != null && destroyed)
                {
                    target.SetActive(false);
                }
            }
            finally
            {
                isSuppressingWasp = false;
            }

            OnLog?.Invoke($"[Шершни] Гнездо ос в {(isRuscko ? "Ruscko" : "коттедже")} обезврежено напарником!");
        }

        private void HandleTrainBusSync(PacketReader reader)
        {
            byte type = reader.ReadByte();
            bool state = reader.ReadBoolean();

            isSuppressingTrainBus = true;
            try
            {
                if (type == 1 && trainWhistleFsm != null)
                {
                    trainWhistleFsm.SendEvent("WHISTLE");
                    OnLog?.Invoke("[Поезд] Оглушительный гудок приближающегося товарного поезда!");
                }
                else if (type == 2)
                {
                    OnLog?.Invoke($"[Автобус] Друг {(state ? "сел в салон рейсового автобуса" : "вышел из автобуса")}");
                }
            }
            finally
            {
                isSuppressingTrainBus = false;
            }
        }

        private void HandleThunderstormSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            isSuppressingFuses = true;
            try
            {
                if (action == 1)
                {
                    if (fuseTableFsm != null) fuseTableFsm.SendEvent("BLOW");
                    for (int i = 0; i < individualFuseFsms.Length; i++)
                    {
                        if (individualFuseFsms[i] != null) individualFuseFsms[i].SendEvent("BLOW");
                    }
                }
            }
            finally
            {
                isSuppressingFuses = false;
            }
            OnLog?.Invoke("[Гроза] Удар молнии выбил керамические пробки в электрощитке дома!");
        }

        private void HandleHouseBurnSync(PacketReader reader)
        {
            bool burned = reader.ReadBoolean();
            if (burned)
            {
                if (burntHouseBase != null) burntHouseBase.SetActive(true);
                if (burntHouseStack != null) burntHouseStack.SetActive(true);
                if (normalHouse != null) normalHouse.SetActive(false);
                OnLog?.Invoke("[Пожар] Дом сгорел дотла!");
            }
        }

        private void HandleAlcoSync(PacketReader reader)
        {
            byte act = reader.ReadByte();
            isSuppressingAlco = true;
            try
            {
                if (act == 1 && alcometerFsm != null)
                {
                    alcometerFsm.SendEvent("BLOW");
                }
            }
            finally
            {
                isSuppressingAlco = false;
            }
            OnLog?.Invoke("[Полиция] Напарник проходит проверку на алкотестере у патруля");
        }

        private void HandleJailSync(PacketReader reader)
        {
            bool inJail = reader.ReadBoolean();
            int days = reader.ReadInt32();

            isSuppressingJail = true;
            try
            {
                if (inJailObj != null)
                {
                    inJailObj.SetActive(inJail);
                }
                if (guardSpeakFsm != null && inJail)
                {
                    guardSpeakFsm.SendEvent("SPEAK");
                }
            }
            finally
            {
                isSuppressingJail = false;
            }

            OnLog?.Invoke($"[Тюрьма] Напарник {(inJail ? $"отбывает заключение в камере ({days} дн.)" : "вышел на свободу из тюрьмы!")}");
        }

        private void HandlePhysiologySync(PacketReader reader)
        {
            int dirt = reader.ReadInt32();
            bool vomiting = reader.ReadBoolean();

            isSuppressingPhysiology = true;
            try
            {
                if (vomiting && vomitFsm != null)
                {
                    vomitFsm.SendEvent("1");
                    if (vomitProjectorFsm != null) vomitProjectorFsm.SendEvent("DIRTY");
                }
                if (fliesFsm != null)
                {
                    if (dirt > 0)
                    {
                        string flyState = $"Fly{Math.Min(6, dirt)}";
                        if (fliesFsm.HasState(flyState)) fliesFsm.SendEvent(flyState);
                    }
                    else
                    {
                        fliesFsm.SendEvent("CLEAN");
                    }
                }
            }
            finally
            {
                isSuppressingPhysiology = false;
            }

            if (vomiting) OnLog?.Invoke("[Физиология] Напарника рвет от тяжелого похмелья!");
        }
    }
}
