using System;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.World
{
    /// <summary>
    /// Менеджер сетевой синхронизации бытовых активностей и мини-игр:
    /// 1. Домашний ПК MikroPekka 386 в спальне (питание, дискеты, BBS, игры Pasi, Rally95, Massacre).
    /// 2. Сбор клубники на поле Суонпакка (ручной сбор ягод в ящики, оплата у фермера).
    /// 3. Зерноуборочный комбайн (жатка, барабан обмолота, бункер).
    /// 4. Варка кофе в чайнике (кипячение воды, заваривание молотого кофе, налив в чашку от усталости).
    /// 5. Радар-детектор (крепление на панель Сацумы, обнаружение полицейских радаров).
    /// 6. Еженедельная лотерея по ТВ (розыгрыш тиража, проверка и оплата билетов).
    /// 7. Рыбалка на удочку (заброс блесны/червя, поклевка, трепыхание щуки, рекордный вес).
    /// 8. Наручные часы (ношение на руке, просмотр времени по клавише 'U').
    /// </summary>
    public class DomesticAndMinigamesManager : MonoBehaviour
    {
        public static DomesticAndMinigamesManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Компьютер MikroPekka 386
        private GameObject computerGo;
        private GameObject computerSystemGo;
        private PlayMakerFSM pcTriggerFsm;
        private PlayMakerFSM pcGetOutFsm;
        private GameObject pcButtonGo;
        private GameObject pcFloppyInGo;
        private GameObject pcFloppyOutGo;
        private bool isSuppressingPC = false;

        // Клубничное поле
        private PlayMakerFSM strawberryAchFsm;
        private PlayMakerFSM strawberryPlayerFsm;
        private bool isSuppressingStrawberry = false;

        // Зерноуборочный комбайн
        private PlayMakerFSM combineNormalizeFsm;
        private PlayMakerFSM combineGearFsm;
        private bool isSuppressingCombine = false;

        // Кофейник
        private PlayMakerFSM coffeePanDataFsm;
        private PlayMakerFSM coffeePanEmptyFsm;
        private bool isSuppressingCoffee = false;

        // Радар-детектор
        private PlayMakerFSM radarDataFsm;
        private bool isSuppressingRadar = false;

        // Лотерея
        private PlayMakerFSM lotteryNumbersFsm;
        private bool isSuppressingLottery = false;

        // Рыбалка на удочку
        private PlayMakerFSM pilkkiAnimFsm;
        private PlayMakerFSM pilkkiLaskentaFsm;
        private PlayMakerFSM saalisDataFsm;
        private bool isSuppressingFishing = false;

        // Наручные часы
        private PlayMakerFSM wristwatchDataFsm;
        private GameObject wristwatchHandObj;
        private bool isSuppressingWatch = false;

        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanDomesticObjects();
            OnLog?.Invoke("[Быт/Мини-игры] Менеджер ПК 386, клубники, комбайна, кофе, лотереи, рыбалки и часов инициализирован");
        }

        public void Reset()
        {
            computerGo = null;
            computerSystemGo = null;
            pcTriggerFsm = null;
            pcGetOutFsm = null;
            pcButtonGo = null;
            pcFloppyInGo = null;
            pcFloppyOutGo = null;
            strawberryAchFsm = null;
            strawberryPlayerFsm = null;
            combineNormalizeFsm = null;
            combineGearFsm = null;
            coffeePanDataFsm = null;
            coffeePanEmptyFsm = null;
            radarDataFsm = null;
            lotteryNumbersFsm = null;
            pilkkiAnimFsm = null;
            pilkkiLaskentaFsm = null;
            saalisDataFsm = null;
            wristwatchDataFsm = null;
            wristwatchHandObj = null;
            isSuppressingPC = false;
            isSuppressingStrawberry = false;
            isSuppressingCombine = false;
            isSuppressingCoffee = false;
            isSuppressingRadar = false;
            isSuppressingLottery = false;
            isSuppressingFishing = false;
            isSuppressingWatch = false;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        public void ScanDomesticObjects()
        {
            try
            {
                // 1. Компьютер MikroPekka 386
                computerGo = GameObject.Find("COMPUTER") ?? GameObject.Find("Computer");
                if (computerGo != null)
                {
                    computerSystemGo = PlayMakerUtilities.FindChildRecursive(computerGo.transform, "SYSTEM")?.gameObject ??
                                       GameObject.Find("SYSTEM");

                    var triggerGo = PlayMakerUtilities.FindChildRecursive(computerGo.transform, "TriggerPlayMode")?.gameObject ??
                                    GameObject.Find("TriggerPlayMode");
                    if (triggerGo != null)
                    {
                        pcTriggerFsm = PlayMakerUtilities.FindFsmOnGameObject(triggerGo, "PlayerTrigger");
                        pcGetOutFsm = PlayMakerUtilities.FindFsmOnGameObject(triggerGo, "GetOut");
                    }

                    pcButtonGo = GameObject.Find("386_button");
                    pcFloppyInGo = GameObject.Find("386_floppy_in");
                    pcFloppyOutGo = GameObject.Find("386_floppy_out");
                }

                // 2. Клубничное поле
                var berryGo = GameObject.Find("StrawberryField") ?? GameObject.Find("BerryTrigger");
                if (berryGo != null)
                {
                    strawberryAchFsm = PlayMakerUtilities.FindFsmOnGameObject(berryGo, "Achievement");
                    strawberryPlayerFsm = PlayMakerUtilities.FindFsmOnGameObject(berryGo, "PlayerDistance");

                    if (strawberryAchFsm != null && strawberryAchFsm.HasState("BERRY"))
                    {
                        strawberryAchFsm.InsertAction("BERRY", () =>
                        {
                            if (isSuppressingStrawberry) return;
                            int picked = 1;
                            var pVar = strawberryAchFsm.FsmVariables.FindFsmInt("StrawberriesPicked");
                            if (pVar != null) picked = pVar.Value;
                            SendStrawberryPicked(picked);
                        }, 0, false);
                    }
                }

                // 3. Зерноуборочный комбайн
                var combineGo = GameObject.Find("COMBINE(350-400psi)") ?? GameObject.Find("Combine");
                if (combineGo != null)
                {
                    combineNormalizeFsm = PlayMakerUtilities.FindFsmOnGameObject(combineGo, "Normalize");
                    combineGearFsm = PlayMakerUtilities.FindFsmOnGameObject(combineGo, "GearIndicator");
                }

                // 4. Кофейник (coffee pan(itemx))
                var coffeeGo = GameObject.Find("coffee pan(itemx)") ?? GameObject.Find("DrinkCoffee");
                if (coffeeGo != null)
                {
                    coffeePanDataFsm = PlayMakerUtilities.FindFsmOnGameObject(coffeeGo, "Data");
                    coffeePanEmptyFsm = PlayMakerUtilities.FindFsmOnGameObject(coffeeGo, "Empty");

                    if (coffeePanDataFsm != null && coffeePanDataFsm.HasState("CAPOPEN"))
                    {
                        coffeePanDataFsm.InsertAction("CAPOPEN", () =>
                        {
                            if (isSuppressingCoffee) return;
                            SyncLocalCoffee();
                        }, 0, false);
                    }
                }

                // 5. Радар-детектор (Radar Buster)
                var radarGo = GameObject.Find("Radar Buster") ?? GameObject.Find("Radar");
                if (radarGo != null)
                {
                    radarDataFsm = PlayMakerUtilities.FindFsmOnGameObject(radarGo, "Data");
                }

                // 6. Лотерея (Lottery)
                var lottoGo = GameObject.Find("Lottery");
                if (lottoGo != null)
                {
                    lotteryNumbersFsm = PlayMakerUtilities.FindFsmOnGameObject(lottoGo, "Numbers");
                    if (lotteryNumbersFsm != null && lotteryNumbersFsm.HasState("STOP"))
                    {
                        lotteryNumbersFsm.InsertAction("STOP", () =>
                        {
                            if (isSuppressingLottery) return;
                            var roundVar = lotteryNumbersFsm.FsmVariables.FindFsmInt("CurrentRound");
                            int round = roundVar != null ? roundVar.Value : 1;
                            var potVar = lotteryNumbersFsm.FsmVariables.FindFsmFloat("NationalPotF");
                            float pot = potVar != null ? potVar.Value : 100000f;
                            SendLotteryDraw(round, pot);
                        }, 0, false);
                    }
                }

                // 7. Рыбалка на удочку (Pilkkiminen & Saalis)
                var pilkkiGo = GameObject.Find("Pilkkiminen");
                if (pilkkiGo != null)
                {
                    pilkkiAnimFsm = PlayMakerUtilities.FindFsmOnGameObject(pilkkiGo, "Anim");
                    pilkkiLaskentaFsm = PlayMakerUtilities.FindFsmOnGameObject(pilkkiGo, "Laskenta");

                    if (pilkkiAnimFsm != null && pilkkiAnimFsm.HasState("NOSTA"))
                    {
                        pilkkiAnimFsm.InsertAction("NOSTA", () =>
                        {
                            if (isSuppressingFishing) return;
                            SendFishingAction(1, 1.5f, "Hauki");
                        }, 0, false);
                    }
                }

                var saalisGo = GameObject.Find("Saalis");
                if (saalisGo != null)
                {
                    saalisDataFsm = PlayMakerUtilities.FindFsmOnGameObject(saalisGo, "Data");
                }

                // 8. Наручные часы (Wristwatch)
                var watchGo = GameObject.Find("Wristwatch");
                if (watchGo != null)
                {
                    wristwatchDataFsm = PlayMakerUtilities.FindFsmOnGameObject(watchGo, "Data");
                }
                wristwatchHandObj = GameObject.Find("WristwatchHand");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Быт/Мини-игры] Ошибка сканирования: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 12f;
                if (computerGo == null || combineNormalizeFsm == null || coffeePanDataFsm == null || lotteryNumbersFsm == null)
                {
                    ScanDomesticObjects();
                }
            }
        }

        private void SyncLocalCoffee()
        {
            if (coffeePanDataFsm == null) return;

            float water = 0f;
            float coffee = 0f;
            float boilVol = 0f;
            float caffeine = 0f;

            var wVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Water");
            if (wVar != null) water = wVar.Value;
            var cVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Coffee");
            if (cVar != null) coffee = cVar.Value;
            var bVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("BoilVolume");
            if (bVar != null) boilVol = bVar.Value;
            var cafVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Caffeine");
            if (cafVar != null) caffeine = cafVar.Value;

            SendCoffeeState(water, coffee, boilVol, caffeine);
        }

        public void SendComputerPower(bool powerOn, string diskName, string activeGame)
        {
            if (isSuppressingPC || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.Computer386Sync))
            {
                packet.WriteBoolean(powerOn);
                packet.WriteString(diskName ?? "");
                packet.WriteString(activeGame ?? "");
                transport.Send(packet);
            }
            OnLog?.Invoke($"[ПК 386] Питание ПК: {(powerOn ? "ВКЛ" : "ВЫКЛ")}, дискета: {diskName}, игра: {activeGame}");
        }

        public void SendStrawberryPicked(int berriesCount)
        {
            if (isSuppressingStrawberry || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.StrawberryJobSync))
            {
                packet.WriteInt32(berriesCount);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Клубника] Сбор ягод на поле: {berriesCount} шт.");
        }

        public void SendCombineState(bool threshing, float headerHeight)
        {
            if (isSuppressingCombine || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CombineHarvesterSync))
            {
                packet.WriteBoolean(threshing);
                packet.WriteSingle(headerHeight);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Комбайн] Молотильный барабан: {(threshing ? "РАБОТАЕТ" : "СТОП")}, высота жатки: {headerHeight:F2}");
        }

        public void SendCoffeeState(float water, float coffee, float boilVolume, float caffeine)
        {
            if (isSuppressingCoffee || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CoffeeBrewSync))
            {
                packet.WriteSingle(water);
                packet.WriteSingle(coffee);
                packet.WriteSingle(boilVolume);
                packet.WriteSingle(caffeine);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Кофеварка] Синхронизация кофе (кофе: {coffee:F1}, вода: {water:F1}, объем: {boilVolume:F1})");
        }

        public void SendRadarState(bool installed, bool bolted)
        {
            if (isSuppressingRadar || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.RadarDetectorSync))
            {
                packet.WriteBoolean(installed);
                packet.WriteBoolean(bolted);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Радар-детектор] Установлен: {installed}, прикручен: {bolted}");
        }

        public void SendLotteryDraw(int round, float nationalPot)
        {
            if (isSuppressingLottery || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.LotteryDrawSync))
            {
                packet.WriteInt32(round);
                packet.WriteSingle(nationalPot);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Лотерея] Розыгрыш субботнего тиража #{round} (джекпот: {nationalPot:F0} mk)");
        }

        public void SendFishingAction(byte action, float fishWeight, string fishName)
        {
            if (isSuppressingFishing || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.FishingMinigameSync))
            {
                packet.WriteByte(action); // 1 = caught fish
                packet.WriteSingle(fishWeight);
                packet.WriteString(fishName ?? "Hauki");
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Рыбалка] Поймана рыба: {fishName} весом {fishWeight:F2} кг!");
        }

        public void SendWristwatchState(bool owned, bool viewing)
        {
            if (isSuppressingWatch || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.WristwatchSync))
            {
                packet.WriteBoolean(owned);
                packet.WriteBoolean(viewing);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.Computer386Sync:
                    HandleComputerSync(reader);
                    break;

                case PacketType.StrawberryJobSync:
                    HandleStrawberrySync(reader);
                    break;

                case PacketType.CombineHarvesterSync:
                    HandleCombineSync(reader);
                    break;

                case PacketType.CoffeeBrewSync:
                    HandleCoffeeSync(reader);
                    break;

                case PacketType.RadarDetectorSync:
                    HandleRadarSync(reader);
                    break;

                case PacketType.LotteryDrawSync:
                    HandleLotterySync(reader);
                    break;

                case PacketType.FishingMinigameSync:
                    HandleFishingSync(reader);
                    break;

                case PacketType.WristwatchSync:
                    HandleWatchSync(reader);
                    break;
            }
        }

        private void HandleComputerSync(PacketReader reader)
        {
            bool power = reader.ReadBoolean();
            string disk = reader.ReadString();
            string activeGame = reader.ReadString();

            isSuppressingPC = true;
            try
            {
                if (computerSystemGo != null)
                {
                    computerSystemGo.SetActive(power);
                }
            }
            finally
            {
                isSuppressingPC = false;
            }

            OnLog?.Invoke($"[ПК 386] Друг {(power ? "включил" : "выключил")} ПК MikroPekka (программа: {activeGame})");
        }

        private void HandleStrawberrySync(PacketReader reader)
        {
            int count = reader.ReadInt32();

            isSuppressingStrawberry = true;
            try
            {
                if (strawberryAchFsm != null)
                {
                    var pVar = strawberryAchFsm.FsmVariables.FindFsmInt("StrawberriesPicked");
                    if (pVar != null) pVar.Value = count;
                    strawberryAchFsm.SendEvent("CHECKACH");
                }
            }
            finally
            {
                isSuppressingStrawberry = false;
            }

            OnLog?.Invoke($"[Клубника] Друг собрал клубнику (всего собрано: {count} шт.)");
        }

        private void HandleCombineSync(PacketReader reader)
        {
            bool th = reader.ReadBoolean();
            float h = reader.ReadSingle();

            isSuppressingCombine = true;
            try
            {
                if (combineNormalizeFsm != null)
                {
                    combineNormalizeFsm.SendEvent(th ? "DRIVEOFF" : "STOP");
                }
            }
            finally
            {
                isSuppressingCombine = false;
            }

            OnLog?.Invoke($"[Комбайн] Друг управляет зерноуборочным комбайном (обмолот: {th})");
        }

        private void HandleCoffeeSync(PacketReader reader)
        {
            float water = reader.ReadSingle();
            float coffee = reader.ReadSingle();
            float boilVol = reader.ReadSingle();
            float caffeine = reader.ReadSingle();

            isSuppressingCoffee = true;
            try
            {
                if (coffeePanDataFsm != null)
                {
                    var wVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Water");
                    if (wVar != null) wVar.Value = water;
                    var cVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Coffee");
                    if (cVar != null) cVar.Value = coffee;
                    var bVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("BoilVolume");
                    if (bVar != null) bVar.Value = boilVol;
                    var cafVar = coffeePanDataFsm.FsmVariables.FindFsmFloat("Caffeine");
                    if (cafVar != null) cafVar.Value = caffeine;
                }
            }
            finally
            {
                isSuppressingCoffee = false;
            }

            OnLog?.Invoke($"[Кофеварка] Напарник обновил чайник: кофе {coffee:F1}, вода {water:F1}");
        }

        private void HandleRadarSync(PacketReader reader)
        {
            bool installed = reader.ReadBoolean();
            bool bolted = reader.ReadBoolean();

            isSuppressingRadar = true;
            try
            {
                if (radarDataFsm != null)
                {
                    var iVar = radarDataFsm.FsmVariables.FindFsmBool("Installed");
                    if (iVar != null) iVar.Value = installed;
                    var bVar = radarDataFsm.FsmVariables.FindFsmBool("Bolted");
                    if (bVar != null) bVar.Value = bolted;
                }
            }
            finally
            {
                isSuppressingRadar = false;
            }

            OnLog?.Invoke($"[Радар-детектор] Радар на Сацуме: установлен={installed}, закручен={bolted}");
        }

        private void HandleLotterySync(PacketReader reader)
        {
            int round = reader.ReadInt32();
            float pot = reader.ReadSingle();

            isSuppressingLottery = true;
            try
            {
                if (lotteryNumbersFsm != null)
                {
                    var roundVar = lotteryNumbersFsm.FsmVariables.FindFsmInt("CurrentRound");
                    if (roundVar != null) roundVar.Value = round;
                    var potVar = lotteryNumbersFsm.FsmVariables.FindFsmFloat("NationalPotF");
                    if (potVar != null) potVar.Value = pot;

                    lotteryNumbersFsm.SendEvent("RESULTS");
                }
            }
            finally
            {
                isSuppressingLottery = false;
            }

            OnLog?.Invoke($"[Лотерея] Тираж #{round} разыгран (джекпот: {pot:F0} mk)");
        }

        private void HandleFishingSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            float weight = reader.ReadSingle();
            string name = reader.ReadString();

            isSuppressingFishing = true;
            try
            {
                if (pilkkiAnimFsm != null && action == 1)
                {
                    pilkkiAnimFsm.SendEvent("NOSTA");
                }
                if (saalisDataFsm != null)
                {
                    saalisDataFsm.SendEvent("HAUKI");
                }
            }
            finally
            {
                isSuppressingFishing = false;
            }

            OnLog?.Invoke($"[Рыбалка] Друг поймал рыбу: {name} ({weight:F2} кг)");
        }

        private void HandleWatchSync(PacketReader reader)
        {
            bool owned = reader.ReadBoolean();
            bool viewing = reader.ReadBoolean();

            isSuppressingWatch = true;
            try
            {
                if (wristwatchDataFsm != null)
                {
                    var ownedVar = wristwatchDataFsm.FsmVariables.FindFsmBool("Owned");
                    if (ownedVar != null) ownedVar.Value = owned;
                }
                if (wristwatchHandObj != null)
                {
                    wristwatchHandObj.SetActive(owned);
                }
            }
            finally
            {
                isSuppressingWatch = false;
            }

            OnLog?.Invoke($"[Часы] Наручные часы: {(owned ? "надеты на руку" : "сняты")}");
        }
    }
}
