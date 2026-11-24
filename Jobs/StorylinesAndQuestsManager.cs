using System;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Jobs
{
    /// <summary>
    /// Менеджер сетевой синхронизации сюжетных линий, квестов и поведения ключевых NPC:
    /// 1. Пьяница Йокке (ночные поездки из бара домой, чемодан с миллионами в лесу, нападение с топором, мост).
    /// 2. Кузен Пена (автостоп 'O', совместная поездка на пассажирском сиденье Fittan, диалоги и курение).
    /// 3. Картежник Вентти (игра в 21 очко, ставки денег, техпаспорта на дом и Ruscko).
    /// 4. Романтическая линия Суски (спасение из разбитого Ricochet, доставка в больницу/домой, письмо на кровати).
    /// 5. Танцевальный павильон (субботний рок-концерт группы BAND, свет, музыкальные треки).
    /// 6. Бабушка Сиркка (поездка в церковь, доставка рыбы/сосисок, диалоги и выдача карманных денег).
    /// 7. Дядя Тойво Кессели (выдача фургона Hayosiko и ассенизатора Gifu, конфискация, выкуп).
    /// 8. Кулачные бои у павильона (Fighter 1 и 2, нокауты, выпадение наручных часов).
    /// 9. Мемориалы ДТП (поминальные кресты и свечи на местах смертельных аварий).
    /// </summary>
    public class StorylinesAndQuestsManager : MonoBehaviour
    {
        public static StorylinesAndQuestsManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Пьяница Йокке
        private PlayMakerFSM jokkeHikerFsm;
        private PlayMakerFSM jokkeMoveFsm;
        private PlayMakerFSM jokkeAxeFsm;
        private GameObject jokkeSuitcaseObj;
        private bool isSuppressingJokke = false;

        // Кузен Пена
        private PlayMakerFSM penaTalkFsm;
        private PlayMakerFSM penaSmokingFsm;
        private bool isSuppressingPena = false;

        // Картежник Вентти
        private PlayMakerFSM venttiDealerFsm;
        private bool isSuppressingVentti = false;

        // Суски
        private PlayMakerFSM suskiStagesFsm;
        private bool isSuppressingSuski = false;

        // Танцевальный павильон и рок-группа
        private PlayMakerFSM danceHallFsm;
        private PlayMakerFSM danceLightsFsm;
        private PlayMakerFSM danceBandFsm;
        private bool isSuppressingDance = false;

        // Бабушка Сиркка
        private PlayMakerFSM grandmaNavFsm;
        private PlayMakerFSM grandmaMoveFsm;
        private bool isSuppressingGrandma = false;

        // Дядя Тойво Кессели
        private PlayMakerFSM uncleStatesFsm;
        private PlayMakerFSM uncleSpeakFsm;
        private bool isSuppressingUncle = false;

        // Бойцы у павильона
        private PlayMakerFSM fighter1HitFsm;
        private PlayMakerFSM fighter1DefendFsm;
        private PlayMakerFSM fighter2HitFsm;
        private PlayMakerFSM fighter2DefendFsm;
        private bool isSuppressingFighter = false;

        // Мемориал ДТП
        private PlayMakerFSM crashSiteCandlesFsm;
        private bool isSuppressingCrashSite = false;

        // Домашний телефон и квесты (Telephone)
        private PlayMakerFSM phoneRingFsm;
        private PlayMakerFSM phoneUseFsm;
        private FsmString phoneTopicVar;
        private FsmInt phoneRandomTimesVar;
        private FsmEvent phoneCloseEvent;
        private GameObject phoneLogicGo;
        private bool isSuppressingPhone = false;

        private static readonly string[] VehicleKeyNames = new string[]
        {
            "PlayerKeyHayosiko",
            "PlayerKeyGifu",
            "PlayerKeyRuscko",
            "PlayerKeyFerndale",
            "PlayerKeyHome",
            "PlayerKeySatsuma"
        };

        private float nextScanTime = 0f;
        private float nextKeyCheckTime = 0f;
        private int[] lastKeys = new int[VehicleKeyNames.Length];
        private bool hasInitializedKeys = false;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            ScanStoryObjects();
            OnLog?.Invoke("[Сюжет/NPC] Менеджер сюжетных линий Йокке, Пены, Вентти, Суски, Бабушки, Дяди и Бойцов инициализирован");
        }

        public void Reset()
        {
            jokkeHikerFsm = null;
            jokkeMoveFsm = null;
            jokkeAxeFsm = null;
            jokkeSuitcaseObj = null;
            penaTalkFsm = null;
            penaSmokingFsm = null;
            venttiDealerFsm = null;
            suskiStagesFsm = null;
            danceHallFsm = null;
            danceLightsFsm = null;
            danceBandFsm = null;
            grandmaNavFsm = null;
            grandmaMoveFsm = null;
            uncleStatesFsm = null;
            uncleSpeakFsm = null;
            fighter1HitFsm = null;
            fighter1DefendFsm = null;
            fighter2HitFsm = null;
            fighter2DefendFsm = null;
            crashSiteCandlesFsm = null;
            phoneRingFsm = null;
            phoneUseFsm = null;
            phoneTopicVar = null;
            phoneRandomTimesVar = null;
            phoneCloseEvent = null;
            phoneLogicGo = null;
            isSuppressingPhone = false;
            isSuppressingJokke = false;
            isSuppressingPena = false;
            isSuppressingVentti = false;
            isSuppressingSuski = false;
            isSuppressingDance = false;
            isSuppressingGrandma = false;
            isSuppressingUncle = false;
            isSuppressingFighter = false;
            isSuppressingCrashSite = false;
            hasInitializedKeys = false;
            for (int i = 0; i < lastKeys.Length; i++) lastKeys[i] = 0;
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        public void ScanStoryObjects()
        {
            try
            {
                // 1. Пьяница Йокке
                var jokkeGo = GameObject.Find("JokkeHiker1") ?? GameObject.Find("JokkeHiker2") ?? GameObject.Find("Jokke");
                if (jokkeGo != null)
                {
                    jokkeHikerFsm = PlayMakerUtilities.FindFsmOnGameObject(jokkeGo, "Hitch");
                    jokkeMoveFsm = PlayMakerUtilities.FindFsmOnGameObject(jokkeGo, "Move");

                    if (jokkeHikerFsm != null)
                    {
                        if (jokkeHikerFsm.HasState("CLIMB"))
                        {
                            jokkeHikerFsm.InsertAction("CLIMB", () =>
                            {
                                if (isSuppressingJokke) return;
                                SendJokkeState(1, "ClimbIn");
                            }, 0, false);
                        }
                        if (jokkeHikerFsm.HasState("PROCEED"))
                        {
                            jokkeHikerFsm.InsertAction("PROCEED", () =>
                            {
                                if (isSuppressingJokke) return;
                                SendJokkeState(2, "PaidMoney");
                            }, 0, false);
                        }
                    }
                }

                var axeGo = GameObject.Find("DrunkAxe") ?? GameObject.Find("Ax") ?? GameObject.Find("Axe");
                if (axeGo != null)
                {
                    jokkeAxeFsm = axeGo.GetComponent<PlayMakerFSM>();
                }
                jokkeSuitcaseObj = GameObject.Find("suitcase(itemx)") ?? GameObject.Find("Suitcase");

                // 2. Кузен Пена
                var penaGo = GameObject.Find("Cousin") ?? GameObject.Find("FITTAN");
                if (penaGo != null)
                {
                    penaTalkFsm = PlayMakerUtilities.FindFsmOnGameObject(penaGo, "Talk");
                    penaSmokingFsm = PlayMakerUtilities.FindFsmOnGameObject(penaGo, "Smoking");

                    if (penaTalkFsm != null && penaTalkFsm.HasState("Talking"))
                    {
                        penaTalkFsm.InsertAction("Talking", () =>
                        {
                            if (isSuppressingPena) return;
                            SendPenaRide(true);
                        }, 0, false);
                    }
                }

                // 3. Картежник Вентти
                var venttiGo = GameObject.Find("VenttiPig") ?? GameObject.Find("VenttiPigHouse");
                if (venttiGo != null)
                {
                    venttiDealerFsm = PlayMakerUtilities.FindFsmOnGameObject(venttiGo, "Use") ??
                                      PlayMakerUtilities.FindFsmOnGameObject(venttiGo, "Data") ??
                                      venttiGo.GetComponent<PlayMakerFSM>();
                }

                // 4. Суски
                var suskiStagesGo = GameObject.Find("SuskiStages") ?? GameObject.Find("Suski");
                if (suskiStagesGo != null)
                {
                    suskiStagesFsm = PlayMakerUtilities.FindFsmOnGameObject(suskiStagesGo, "Logic") ?? suskiStagesGo.GetComponent<PlayMakerFSM>();
                    if (suskiStagesFsm != null)
                    {
                        for (int s = 1; s <= 8; s++)
                        {
                            string stateName = s.ToString();
                            byte stageByte = (byte)s;
                            if (suskiStagesFsm.HasState(stateName))
                            {
                                suskiStagesFsm.InsertAction(stateName, () =>
                                {
                                    if (isSuppressingSuski) return;
                                    SendSuskiRescue(stageByte);
                                }, 0, false);
                            }
                        }
                    }
                }

                // 5. Танцевальный павильон и рок-группа
                var danceGo = GameObject.Find("DANCEHALL");
                if (danceGo != null)
                {
                    danceHallFsm = PlayMakerUtilities.FindFsmOnGameObject(danceGo, "OpeningHours");
                    danceLightsFsm = PlayMakerUtilities.FindFsmOnGameObject(danceGo, "Lights");
                }

                var bandGo = GameObject.Find("BAND");
                if (bandGo != null)
                {
                    danceBandFsm = PlayMakerUtilities.FindFsmOnGameObject(bandGo, "Activate") ?? bandGo.GetComponent<PlayMakerFSM>();
                    if (danceBandFsm != null)
                    {
                        if (danceBandFsm.HasState("Song1"))
                        {
                            danceBandFsm.InsertAction("Song1", () =>
                            {
                                if (isSuppressingDance) return;
                                SendDancePavilion(true, 1, true);
                            }, 0, false);
                        }
                        if (danceBandFsm.HasState("Song2"))
                        {
                            danceBandFsm.InsertAction("Song2", () =>
                            {
                                if (isSuppressingDance) return;
                                SendDancePavilion(true, 2, true);
                            }, 0, false);
                        }
                    }
                }

                // 6. Бабушка Сиркка
                var grandmaGo = GameObject.Find("ChurchGrandma") ?? GameObject.Find("Grandma");
                if (grandmaGo != null)
                {
                    grandmaNavFsm = PlayMakerUtilities.FindFsmOnGameObject(grandmaGo, "Navigation");
                    grandmaMoveFsm = PlayMakerUtilities.FindFsmOnGameObject(grandmaGo, "Move");

                    if (grandmaNavFsm != null && grandmaNavFsm.HasState("Waypoint"))
                    {
                        grandmaNavFsm.InsertAction("Waypoint", () =>
                        {
                            if (isSuppressingGrandma) return;
                            var atChurchVar = grandmaNavFsm.FsmVariables.FindFsmBool("AtChurch");
                            bool atChurch = atChurchVar != null && atChurchVar.Value;
                            SendGrandmaDialogue(1, atChurch);
                        }, 0, false);
                    }
                }

                // 7. Дядя Тойво Кессели (UNCLE & Uncle)
                var uncleMainGo = GameObject.Find("UNCLE");
                if (uncleMainGo != null)
                {
                    uncleStatesFsm = PlayMakerUtilities.FindFsmOnGameObject(uncleMainGo, "States");
                    if (uncleStatesFsm != null)
                    {
                        for (int st = 1; st <= 5; st++)
                        {
                            string stName = st.ToString();
                            byte stByte = (byte)st;
                            if (uncleStatesFsm.HasState(stName))
                            {
                                uncleStatesFsm.InsertAction(stName, () =>
                                {
                                    if (isSuppressingUncle) return;
                                    var gifuVar = uncleStatesFsm.FsmVariables.FindFsmBool("GifuKeys");
                                    bool hasGifu = gifuVar != null && gifuVar.Value;
                                    SendUncleStory(stByte, hasGifu, true);
                                }, 0, false);
                            }
                        }
                    }
                }

                var uncleNpcGo = GameObject.Find("Uncle");
                if (uncleNpcGo != null)
                {
                    uncleSpeakFsm = PlayMakerUtilities.FindFsmOnGameObject(uncleNpcGo, "Speak");
                }

                // 8. Бойцы у павильона (Fighter и Fighter2)
                var f1Go = GameObject.Find("Fighter");
                if (f1Go != null)
                {
                    fighter1HitFsm = PlayMakerUtilities.FindFsmOnGameObject(f1Go, "Hit");
                    fighter1DefendFsm = PlayMakerUtilities.FindFsmOnGameObject(f1Go, "Defend");

                    if (fighter1HitFsm != null && fighter1HitFsm.HasState("HIT1"))
                    {
                        fighter1HitFsm.InsertAction("HIT1", () =>
                        {
                            if (isSuppressingFighter) return;
                            SendFighterBrawl(1, 1, false);
                        }, 0, false);
                    }
                }

                var f2Go = GameObject.Find("Fighter2");
                if (f2Go != null)
                {
                    fighter2HitFsm = PlayMakerUtilities.FindFsmOnGameObject(f2Go, "Hit");
                    fighter2DefendFsm = PlayMakerUtilities.FindFsmOnGameObject(f2Go, "Defend");

                    if (fighter2HitFsm != null && fighter2HitFsm.HasState("HIT1"))
                    {
                        fighter2HitFsm.InsertAction("HIT1", () =>
                        {
                            if (isSuppressingFighter) return;
                            SendFighterBrawl(2, 1, false);
                        }, 0, false);
                    }
                }

                // 9. Мемориал ДТП со свечами
                var crashGo = GameObject.Find("CrashSite");
                if (crashGo != null)
                {
                    crashSiteCandlesFsm = PlayMakerUtilities.FindFsmOnGameObject(crashGo, "Candles");
                    if (crashSiteCandlesFsm != null && crashSiteCandlesFsm.HasState("Candle"))
                    {
                        crashSiteCandlesFsm.InsertAction("Candle", () =>
                        {
                            if (isSuppressingCrashSite) return;
                            SendCrashMemorial(true);
                        }, 0, false);
                    }
                }

                // 10. Домашний телефон и квесты заказчиков
                ScanTelephone();
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Сюжет/NPC] Ошибка сканирования: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 12f;
                if (jokkeHikerFsm == null || venttiDealerFsm == null || uncleStatesFsm == null || phoneRingFsm == null || phoneUseFsm == null)
                {
                    ScanStoryObjects();
                }
            }

            // Проверка изменения ключей у хоста (каждые 1.0 сек)
            if (transport.IsServer && Time.time > nextKeyCheckTime)
            {
                nextKeyCheckTime = Time.time + 1.0f;
                CheckHostKeysChanged();
            }
        }

        private void CheckHostKeysChanged()
        {
            bool changed = false;

            for (int i = 0; i < VehicleKeyNames.Length; i++)
            {
                int curVal = GetKeyInt(VehicleKeyNames[i]);
                if (!hasInitializedKeys)
                {
                    lastKeys[i] = curVal;
                }
                else if (curVal != lastKeys[i])
                {
                    lastKeys[i] = curVal;
                    changed = true;
                }
            }

            if (!hasInitializedKeys)
            {
                hasInitializedKeys = true;
                return;
            }

            if (changed)
            {
                OnLog?.Invoke($"[Ключи авто] Обнаружено изменение ключей у хоста. Немедленная отправка пакета клиентам...");
                SendPlayerKeysSnapshot();
            }
        }

        public void SendJokkeState(byte stage, string stateName)
        {
            if (isSuppressingJokke || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.JokkeStorySync))
            {
                packet.WriteByte(stage); // 1 = ClimbIn, 2 = PaidMoney, 3 = SuitcaseFound, 4 = AxeAttack, 5 = BridgeScene
                packet.WriteString(stateName);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Сюжет] Событие Йокке: {stateName} (стадия {stage})");
        }

        public void SendPenaRide(bool inCar)
        {
            if (isSuppressingPena || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.PenaHitchhikeSync))
            {
                packet.WriteBoolean(inCar);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Кузен Пена] Игрок {(inCar ? "сел в салон к кузену Пене" : "вышел из машины Пены")}");
        }

        public void SendVenttiAction(byte action, int betAmount)
        {
            if (isSuppressingVentti || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VenttiGameSync))
            {
                packet.WriteByte(action); // 1 = Deal, 2 = Hit, 3 = Stand, 4 = Steal bag
                packet.WriteInt32(betAmount);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Вентти] Действие за столом картежника: {action} (ставка: {betAmount} mk)");
        }

        public void SendSuskiRescue(byte stage)
        {
            if (isSuppressingSuski || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.SuskiStorySync))
            {
                packet.WriteByte(stage);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Суски] Сюжетное продвижение Суски (стадия #{stage})");
        }

        public void SendDancePavilion(bool musicPlaying, int songIndex, bool lightsOn)
        {
            if (isSuppressingDance || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.DancePavilionSync))
            {
                packet.WriteBoolean(musicPlaying);
                packet.WriteInt32(songIndex);
                packet.WriteBoolean(lightsOn);
                transport.Send(packet);
            }
        }

        public void SendGrandmaDialogue(int dialogueIndex, bool atChurch)
        {
            if (isSuppressingGrandma || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.GrandmaStorySync))
            {
                packet.WriteInt32(dialogueIndex);
                packet.WriteBoolean(atChurch);
                transport.Send(packet);
            }
        }

        public void SendUncleStory(byte stage, bool hasGifuKeys, bool hasVan)
        {
            if (isSuppressingUncle || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.UncleStorySync))
            {
                packet.WriteByte(stage);
                packet.WriteBoolean(hasGifuKeys);
                packet.WriteBoolean(hasVan);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Дядя Кессели] Сюжетная стадия #{stage} (ключи Gifu: {hasGifuKeys}, фургон: {hasVan})");

            // Рассылаем актуализированный снимок ключей клиентам
            if (transport.IsServer)
            {
                SendPlayerKeysSnapshot();
            }
        }

        public void SendPlayerKeysSnapshot(int targetClientId = -1)
        {
            if (transport == null || !transport.IsRunning) return;

            int hayosiko = GetKeyInt("PlayerKeyHayosiko");
            int gifu = GetKeyInt("PlayerKeyGifu");
            int ruscko = GetKeyInt("PlayerKeyRuscko");
            int ferndale = GetKeyInt("PlayerKeyFerndale");
            int home = GetKeyInt("PlayerKeyHome");
            int satsuma = GetKeyInt("PlayerKeySatsuma");

            using (var packet = new PacketWriter(PacketType.PlayerKeysSync))
            {
                packet.WriteInt32(hayosiko);
                packet.WriteInt32(gifu);
                packet.WriteInt32(ruscko);
                packet.WriteInt32(ferndale);
                packet.WriteInt32(home);
                packet.WriteInt32(satsuma);

                if (targetClientId >= 0)
                {
                    transport.Send(packet, targetClientId);
                }
                else
                {
                    transport.Send(packet);
                }
            }
            OnLog?.Invoke($"[Ключи авто] Отправлен статус ключей (Hayosiko: {hayosiko}, Gifu: {gifu}, Ruscko: {ruscko}, Ferndale: {ferndale}, Home: {home}, Satsuma: {satsuma})");
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport != null && transport.IsServer)
            {
                SendPlayerKeysSnapshot(clientId);
                SendActiveQuestMarkersSnapshot(clientId);
            }
        }

        public void SendFighterBrawl(int fighterIndex, byte hitType, bool isDead)
        {
            if (isSuppressingFighter || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.FighterBrawlSync))
            {
                packet.WriteInt32(fighterIndex);
                packet.WriteByte(hitType);
                packet.WriteBoolean(isDead);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Драка] Удар по бойцу #{fighterIndex} (тип {hitType}, нокаут: {isDead})");
        }

        public void SendCrashMemorial(bool active)
        {
            if (isSuppressingCrashSite || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CrashMemorialSync))
            {
                packet.WriteBoolean(active);
                transport.Send(packet);
            }
            OnLog?.Invoke("[Мемориал ДТП] Поминальный мемориал и свечи зажжены на обочине");
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.JokkeStorySync:
                    HandleJokkeSync(reader);
                    break;

                case PacketType.PenaHitchhikeSync:
                    HandlePenaSync(reader);
                    break;

                case PacketType.VenttiGameSync:
                    HandleVenttiSync(reader);
                    break;

                case PacketType.SuskiStorySync:
                    HandleSuskiSync(reader);
                    break;

                case PacketType.DancePavilionSync:
                    HandleDanceSync(reader);
                    break;

                case PacketType.GrandmaStorySync:
                    HandleGrandmaSync(reader);
                    break;

                case PacketType.UncleStorySync:
                    HandleUncleSync(reader);
                    break;

                case PacketType.PlayerKeysSync:
                    HandlePlayerKeysSync(reader);
                    break;

                case PacketType.FighterBrawlSync:
                    HandleFighterSync(reader);
                    break;

                case PacketType.CrashMemorialSync:
                    HandleCrashMemorialSync(reader);
                    break;

                case PacketType.TelephoneRing:
                    HandleTelephoneRing(reader, connectionId);
                    break;

                case PacketType.TelephoneCallAction:
                    HandleTelephoneCallAction(reader, connectionId);
                    break;
            }
        }

        private void HandleJokkeSync(PacketReader reader)
        {
            byte stage = reader.ReadByte();
            string stateName = reader.ReadString();

            isSuppressingJokke = true;
            try
            {
                if (stage == 1 && jokkeHikerFsm != null)
                {
                    jokkeHikerFsm.SendEvent("CLIMB");
                }
                else if (stage == 2 && jokkeHikerFsm != null)
                {
                    jokkeHikerFsm.SendEvent("PROCEED");
                }
                else if (stage == 3 && jokkeSuitcaseObj != null)
                {
                    jokkeSuitcaseObj.SetActive(true);
                }
                else if (stage == 4 && jokkeAxeFsm != null)
                {
                    jokkeAxeFsm.SendEvent("ATTACK");
                }
            }
            finally
            {
                isSuppressingJokke = false;
            }
            OnLog?.Invoke($"[Сюжет] Друг взаимодействует с Йокке: {stateName} (стадия {stage})");
        }

        private void HandlePenaSync(PacketReader reader)
        {
            bool inCar = reader.ReadBoolean();

            isSuppressingPena = true;
            try
            {
                if (penaTalkFsm != null && inCar)
                {
                    penaTalkFsm.SendEvent("TALK");
                }
            }
            finally
            {
                isSuppressingPena = false;
            }

            OnLog?.Invoke($"[Кузен Пена] Друг {(inCar ? "едет в салоне Fittan с кузеном Пеной" : "вышел из машины Пены")}");
        }

        private void HandleVenttiSync(PacketReader reader)
        {
            byte action = reader.ReadByte();
            int bet = reader.ReadInt32();

            isSuppressingVentti = true;
            try
            {
                if (venttiDealerFsm != null)
                {
                    var betVar = venttiDealerFsm.FsmVariables.FindFsmInt("Bet");
                    if (betVar != null) betVar.Value = bet;

                    venttiDealerFsm.SendEvent("DEAL");
                }
            }
            finally
            {
                isSuppressingVentti = false;
            }

            OnLog?.Invoke($"[Вентти] Друг играет в 21 очко у картежника (действие {action}, ставка {bet} mk)");
        }

        private void HandleSuskiSync(PacketReader reader)
        {
            byte stage = reader.ReadByte();

            isSuppressingSuski = true;
            try
            {
                if (suskiStagesFsm != null)
                {
                    var stageVar = suskiStagesFsm.FsmVariables.FindFsmInt("SuskiStage");
                    if (stageVar != null) stageVar.Value = stage;

                    string stateTarget = stage.ToString();
                    suskiStagesFsm.SendEvent(stateTarget);
                    suskiStagesFsm.SendEvent("PLAYERHELP");
                }
            }
            finally
            {
                isSuppressingSuski = false;
            }

            OnLog?.Invoke($"[Суски] Спасение и сюжетная линия Суски (стадия #{stage})");
        }

        private void HandleDanceSync(PacketReader reader)
        {
            bool playing = reader.ReadBoolean();
            int songIdx = reader.ReadInt32();
            bool lights = reader.ReadBoolean();

            isSuppressingDance = true;
            try
            {
                if (danceBandFsm != null)
                {
                    danceBandFsm.SendEvent(playing ? (songIdx == 2 ? "CHANGESONG" : "Song1") : "STOPMUSIC");
                }
                if (danceLightsFsm != null)
                {
                    danceLightsFsm.SendEvent(lights ? "ON" : "OFF");
                }
            }
            finally
            {
                isSuppressingDance = false;
            }

            OnLog?.Invoke($"[Танцы] Танцевальный павильон: рок-концерт {(playing ? $"песня #{songIdx}" : "остановлен")}, свет {(lights ? "ВКЛ" : "ВЫКЛ")}");
        }

        private void HandleGrandmaSync(PacketReader reader)
        {
            int idx = reader.ReadInt32();
            bool atChurch = reader.ReadBoolean();

            isSuppressingGrandma = true;
            try
            {
                if (grandmaNavFsm != null)
                {
                    var churchVar = grandmaNavFsm.FsmVariables.FindFsmBool("AtChurch");
                    if (churchVar != null) churchVar.Value = atChurch;

                    grandmaNavFsm.SendEvent("WALK");
                }
            }
            finally
            {
                isSuppressingGrandma = false;
            }

            OnLog?.Invoke($"[Бабушка] Разговор с бабушкой Сирккой (#{idx}) у {(atChurch ? "церкви" : "дома")}");
        }

        private void HandleUncleSync(PacketReader reader)
        {
            byte stage = reader.ReadByte();
            bool hasGifu = reader.ReadBoolean();
            bool hasVan = reader.ReadBoolean();

            isSuppressingUncle = true;
            try
            {
                if (uncleStatesFsm != null)
                {
                    var stageVar = uncleStatesFsm.FsmVariables.FindFsmInt("UncleStage");
                    if (stageVar != null) stageVar.Value = stage;

                    var gifuVar = uncleStatesFsm.FsmVariables.FindFsmBool("GifuKeys");
                    if (gifuVar != null) gifuVar.Value = hasGifu;

                    uncleStatesFsm.SendEvent(stage.ToString());
                }

                if (uncleSpeakFsm != null)
                {
                    uncleSpeakFsm.SendEvent("PROCEED");
                }

                // Синхронизация глобальных ключей для клиента
                if (hasVan || stage >= 2)
                {
                    SetKeyInt("PlayerKeyHayosiko", 1);
                }

                if (hasGifu)
                {
                    SetKeyInt("PlayerKeyGifu", 1);
                }
            }
            finally
            {
                isSuppressingUncle = false;
            }

            OnLog?.Invoke($"[Дядя Кессели] Обновление сюжетной стадии дяди #{stage} (Gifu: {hasGifu}, Van: {hasVan})");
        }

        private void HandlePlayerKeysSync(PacketReader reader)
        {
            int hayosiko = reader.ReadInt32();
            int gifu = reader.ReadInt32();
            int ruscko = reader.ReadInt32();
            int ferndale = reader.ReadInt32();
            int home = reader.ReadInt32();
            int satsuma = reader.ReadInt32();

            SetKeyInt("PlayerKeyHayosiko", hayosiko);
            SetKeyInt("PlayerKeyGifu", gifu);
            SetKeyInt("PlayerKeyRuscko", ruscko);
            SetKeyInt("PlayerKeyFerndale", ferndale);
            SetKeyInt("PlayerKeyHome", home);
            SetKeyInt("PlayerKeySatsuma", satsuma);

            OnLog?.Invoke($"[Ключи авто] Синхронизированы глобальные права доступа (Hayosiko: {hayosiko}, Gifu: {gifu}, Ruscko: {ruscko}, Ferndale: {ferndale}, Home: {home}, Satsuma: {satsuma})");
        }

        public static int GetKeyInt(string keyName)
        {
            try
            {
                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    var v = PlayMakerGlobals.Instance.Variables.FindFsmInt(keyName);
                    if (v != null) return v.Value;
                }

                var fallback = FsmVariables.GlobalVariables?.FindFsmInt(keyName);
                return fallback != null ? fallback.Value : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void SetKeyInt(string keyName, int val)
        {
            try
            {
                if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
                {
                    var v = PlayMakerGlobals.Instance.Variables.FindFsmInt(keyName);
                    if (v != null)
                    {
                        v.Value = val;
                        return;
                    }
                }

                var fallback = FsmVariables.GlobalVariables?.FindFsmInt(keyName);
                if (fallback != null)
                {
                    fallback.Value = val;
                }
            }
            catch
            {
            }
        }

        private void HandleFighterSync(PacketReader reader)
        {
            int fIdx = reader.ReadInt32();
            byte hitType = reader.ReadByte();
            bool isDead = reader.ReadBoolean();

            isSuppressingFighter = true;
            try
            {
                var hitFsm = (fIdx == 1) ? fighter1HitFsm : fighter2HitFsm;
                var defFsm = (fIdx == 1) ? fighter1DefendFsm : fighter2DefendFsm;

                if (hitFsm != null)
                {
                    hitFsm.SendEvent(isDead ? "DIE" : "HOOKHIT");
                }
                if (defFsm != null)
                {
                    defFsm.SendEvent("HIT");
                }
            }
            finally
            {
                isSuppressingFighter = false;
            }

            OnLog?.Invoke($"[Драка] Друг нанес удар бойцу #{fIdx} (нокаут: {isDead})");
        }

        private void HandleCrashMemorialSync(PacketReader reader)
        {
            bool active = reader.ReadBoolean();

            isSuppressingCrashSite = true;
            try
            {
                if (crashSiteCandlesFsm != null && active)
                {
                    crashSiteCandlesFsm.SendEvent("SPAWN");
                }
            }
            finally
            {
                isSuppressingCrashSite = false;
            }

            OnLog?.Invoke("[Мемориал ДТП] Поминальные свечи зажжены на месте трагедии");
        }

        #region Telephone & Quests Sync

        public void InitializeTelephone()
        {
            ScanTelephone();
        }

        public void ScanTelephone()
        {
            try
            {
                Transform logicTransform = GameObject.Find("YARD")?.transform.Find("Building/LIVINGROOM/Telephone/Logic");
                if (logicTransform == null)
                {
                    var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < allGo.Length; i++)
                    {
                        if (allGo[i].name == "Telephone" && allGo[i].transform.Find("Logic") != null)
                        {
                            logicTransform = allGo[i].transform.Find("Logic");
                            break;
                        }
                    }
                }

                if (logicTransform == null)
                {
                    return;
                }

                // 1. FSM Ring (или Logic) на объекте Ring / Logic
                Transform ringTransform = logicTransform.Find("Ring") ?? logicTransform;
                phoneRingFsm = PlayMakerUtilities.FindFsmOnGameObject(ringTransform.gameObject, "Ring") ??
                               PlayMakerUtilities.FindFsmOnGameObject(ringTransform.gameObject, "Logic") ??
                               ringTransform.GetComponent<PlayMakerFSM>();

                if (phoneRingFsm != null)
                {
                    phoneRingFsm.Initialize();
                    phoneTopicVar = phoneRingFsm.FsmVariables.FindFsmString("Topic");
                    phoneRandomTimesVar = phoneRingFsm.FsmVariables.FindFsmInt("RandomTimes");

                    // Хук на снятие трубки локальным игроком
                    if (phoneRingFsm.HasState("State 2"))
                    {
                        phoneRingFsm.InsertAction("State 2", () =>
                        {
                            if (isSuppressingPhone) return;
                            string topic = phoneTopicVar != null ? phoneTopicVar.Value : string.Empty;
                            string qId = GetQuestIdFromTopic(topic);
                            SendPhoneAnswer(true, qId, topic);
                            ActivateQuestAndMarkers(qId, topic);
                            OnLog?.Invoke($"[Телефон] Локальный игрок поднял трубку. Квест: {qId} ({topic})");
                        }, 0, false);
                    }

                    // Перехват звонка на хосте через OnEnable компонента (по архитектуре WreckMP)
                    var hook = phoneRingFsm.gameObject.GetComponent<TelephoneRingHook>() ??
                               phoneRingFsm.gameObject.AddComponent<TelephoneRingHook>();
                    hook.OnRingEnabled = OnPhoneRingEnabled;
                }

                // 2. FSM Use на UseHandle
                Transform handleTransform = logicTransform.Find("UseHandle");
                if (handleTransform != null)
                {
                    phoneUseFsm = PlayMakerUtilities.FindFsmOnGameObject(handleTransform.gameObject, "Use") ??
                                  handleTransform.GetComponent<PlayMakerFSM>();

                    if (phoneUseFsm != null)
                    {
                        phoneUseFsm.Initialize();
                        phoneCloseEvent = phoneUseFsm.AddEvent("MP_CLOSE");
                        phoneUseFsm.AddGlobalTransition(phoneCloseEvent, "Close phone");

                        if (phoneUseFsm.HasState("Close phone"))
                        {
                            phoneUseFsm.InsertAction("Close phone", () =>
                            {
                                if (isSuppressingPhone) return;
                                SendPhoneAnswer(false, string.Empty, string.Empty);
                                OnLog?.Invoke("[Телефон] Трубка положена.");
                            }, 0, false);
                        }
                    }
                }

                // 3. Отключение PhoneLogic и сброса звонка на клиенте (по архитектуре WreckMP)
                phoneLogicGo = logicTransform.Find("PhoneLogic")?.gameObject;
                if (transport != null && !transport.IsServer)
                {
                    if (phoneLogicGo != null)
                    {
                        phoneLogicGo.SetActive(false);
                    }

                    if (phoneUseFsm != null)
                    {
                        var closeState = phoneUseFsm.GetState("Close phone");
                        if (closeState != null && closeState.Actions != null && closeState.Actions.Length > 9)
                        {
                            closeState.Actions[9].Enabled = false; // Отключаем сброс звонка на клиенте
                        }
                    }
                }

                OnLog?.Invoke("[Телефон/Квесты] Домашний телефон и синхронизация квестов успешно инициализированы");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Телефон Init Error] {ex.Message}");
            }
        }

        private void OnPhoneRingEnabled()
        {
            if (transport == null || !transport.IsServer || isSuppressingPhone) return;

            string topic = phoneTopicVar != null ? phoneTopicVar.Value : string.Empty;
            int randomTimes = phoneRandomTimesVar != null ? phoneRandomTimesVar.Value : 5;
            string questId = GetQuestIdFromTopic(topic);

            SendPhoneRing(topic, randomTimes, questId);
        }

        public void SendPhoneRing(string topic, int randomTimes, string questId)
        {
            if (isSuppressingPhone || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TelephoneRing))
            {
                packet.WriteString(topic ?? string.Empty);
                packet.WriteInt32(randomTimes);
                packet.WriteString(questId ?? string.Empty);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Телефон] Входящий звонок хоста: {topic} (квест: {questId}, гудков: {randomTimes})");
        }

        public void SendPhoneAnswer(bool isPickup, string questId, string topic, int targetClientId = -1)
        {
            if (isSuppressingPhone || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TelephoneCallAction))
            {
                packet.WriteBoolean(isPickup);
                packet.WriteString(questId ?? string.Empty);
                packet.WriteString(topic ?? string.Empty);

                if (targetClientId >= 0)
                {
                    transport.Send(packet, targetClientId);
                }
                else
                {
                    transport.Send(packet);
                }
            }
        }

        private void HandleTelephoneRing(PacketReader reader, int connectionId)
        {
            string topic = reader.ReadString();
            int randomTimes = reader.UnreadBytes >= 4 ? reader.ReadInt32() : 5;
            string questId = reader.UnreadBytes > 0 ? reader.ReadString() : GetQuestIdFromTopic(topic);

            if (transport != null && transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.TelephoneRing))
                {
                    relay.WriteString(topic);
                    relay.WriteInt32(randomTimes);
                    relay.WriteString(questId);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemotePhoneRing(topic, randomTimes, questId);
        }

        private void HandleTelephoneCallAction(PacketReader reader, int connectionId)
        {
            bool isPickup = reader.ReadBoolean();
            string questId = reader.UnreadBytes > 0 ? reader.ReadString() : string.Empty;
            string topic = reader.UnreadBytes > 0 ? reader.ReadString() : string.Empty;

            if (transport != null && transport.IsServer)
            {
                using (var relay = new PacketWriter(PacketType.TelephoneCallAction))
                {
                    relay.WriteBoolean(isPickup);
                    relay.WriteString(questId);
                    relay.WriteString(topic);
                    transport.Send(relay, connectionId);
                }
            }

            ApplyRemotePhoneAction(isPickup, questId, topic);
        }

        private void ApplyRemotePhoneRing(string topic, int randomTimes, string questId)
        {
            if (phoneRingFsm == null)
            {
                ScanTelephone();
                if (phoneRingFsm == null) return;
            }

            isSuppressingPhone = true;
            try
            {
                if (phoneTopicVar != null)
                {
                    phoneTopicVar.Value = topic;
                }

                if (phoneRandomTimesVar != null)
                {
                    phoneRandomTimesVar.Value = randomTimes;
                }

                phoneRingFsm.gameObject.SetActive(true);
                OnLog?.Invoke($"[Телефон] Телефон звонит на клиенте (тема: {topic}, квест: {questId}, гудков: {randomTimes})");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[Telephone ApplyRing Error] {ex.Message}");
            }
            finally
            {
                isSuppressingPhone = false;
            }
        }

        private void ApplyRemotePhoneAction(bool isPickup, string questId, string topic)
        {
            if (phoneRingFsm == null || phoneUseFsm == null)
            {
                ScanTelephone();
            }

            if (isPickup)
            {
                if (phoneRingFsm != null && !phoneRingFsm.gameObject.activeSelf)
                {
                    phoneRingFsm.gameObject.SetActive(true);
                }

                StartCoroutine(C_OnRemoteTelephonePickup(questId, topic));
            }
            else
            {
                isSuppressingPhone = true;
                try
                {
                    if (phoneUseFsm != null && phoneCloseEvent != null)
                    {
                        phoneUseFsm.SendEvent("MP_CLOSE");
                    }
                    else if (phoneRingFsm != null)
                    {
                        phoneRingFsm.SendEvent("ENDCALL");
                    }
                    OnLog?.Invoke("[Телефон] Второй игрок положил трубку.");
                }
                catch (Exception ex)
                {
                    ModConsole.Print($"[Telephone ApplyCallAction Error] {ex.Message}");
                }
                finally
                {
                    isSuppressingPhone = false;
                }
            }
        }

        private System.Collections.IEnumerator C_OnRemoteTelephonePickup(string questId, string topic)
        {
            isSuppressingPhone = true;
            try
            {
                if (phoneTopicVar != null && !string.IsNullOrEmpty(topic))
                {
                    phoneTopicVar.Value = topic;
                }

                if (phoneRingFsm != null)
                {
                    phoneRingFsm.SendEvent("ANSWER");
                }

                ActivateQuestAndMarkers(questId, topic);
                OnLog?.Invoke($"[Телефон] Второй игрок ответил на звонок. Квест активирован: {questId} ({topic})");
            }
            finally
            {
                isSuppressingPhone = false;
            }

            if (phoneRingFsm != null)
            {
                while (phoneRingFsm.ActiveStateName == "State 2")
                {
                    yield return new WaitForEndOfFrame();
                }

                isSuppressingPhone = true;
                try
                {
                    phoneRingFsm.SendEvent("ENDCALL");
                    if (phoneUseFsm != null && phoneCloseEvent != null)
                    {
                        phoneUseFsm.SendEvent("MP_CLOSE");
                    }
                }
                finally
                {
                    isSuppressingPhone = false;
                }
            }
        }

        public void ActivateQuestAndMarkers(string questId, string topic)
        {
            try
            {
                // 1. Посылаем нативное событие в FSM телефона
                if (phoneRingFsm != null && !string.IsNullOrEmpty(topic))
                {
                    if (phoneRingFsm.HasState(topic))
                    {
                        phoneRingFsm.SendEvent(topic);
                    }
                    else
                    {
                        string ev = topic.ToUpperInvariant();
                        if (ev.Contains("WOOD")) phoneRingFsm.SendEvent("WOOD1");
                        else if (ev.Contains("DRUNK")) phoneRingFsm.SendEvent("DRUNK");
                        else if (ev.Contains("SHIT1")) phoneRingFsm.SendEvent("SHIT1");
                        else if (ev.Contains("SHIT2")) phoneRingFsm.SendEvent("SHIT2");
                        else if (ev.Contains("SHIT3")) phoneRingFsm.SendEvent("SHIT3");
                        else if (ev.Contains("SHIT4")) phoneRingFsm.SendEvent("SHIT4");
                        else if (ev.Contains("SHIT5")) phoneRingFsm.SendEvent("SHIT5");
                        else if (ev.Contains("MOVING")) phoneRingFsm.SendEvent("MOVING");
                        else if (ev.Contains("MUMMO1")) phoneRingFsm.SendEvent("MUMMO1");
                        else if (ev.Contains("MUMMO2")) phoneRingFsm.SendEvent("MUMMO2");
                        else if (ev.Contains("MUMMO3")) phoneRingFsm.SendEvent("MUMMO3");
                        else if (ev.Contains("UNCLE")) phoneRingFsm.SendEvent("UNCLE");
                    }
                }

                // 2. Активируем игровой объект маркера задачи на карте в коридоре дома
                string targetCaller = null;
                string combined = ((questId ?? "") + " " + (topic ?? "")).ToUpperInvariant();

                if (combined.Contains("WOOD"))
                {
                    targetCaller = "call_woods";
                }
                else if (combined.Contains("DRUNK"))
                {
                    targetCaller = "call_lift";
                }
                else if (combined.Contains("SHIT") || combined.Contains("SEWAGE"))
                {
                    if (combined.Contains("SHIT2") || combined.Contains("SEWAGE2")) targetCaller = "shitwell02_call";
                    else if (combined.Contains("SHIT3") || combined.Contains("SEWAGE3")) targetCaller = "shitwell03_call";
                    else if (combined.Contains("SHIT4") || combined.Contains("SEWAGE4")) targetCaller = "shitwell04_call";
                    else if (combined.Contains("SHIT5") || combined.Contains("SEWAGE5")) targetCaller = "shitwell05_call";
                    else targetCaller = "shitwell01_call";
                }
                else if (combined.Contains("MOVING")) targetCaller = "call_movinghelp";
                else if (combined.Contains("MUMMO1")) targetCaller = "call_mummo1";
                else if (combined.Contains("MUMMO2")) targetCaller = "call_mummo2";
                else if (combined.Contains("MUMMO3")) targetCaller = "call_mummo3";
                else if (combined.Contains("UNCLE")) targetCaller = "call_uncle";

                if (!string.IsNullOrEmpty(targetCaller))
                {
                    ActivateCallerObject(targetCaller);
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ActivateQuestAndMarkers Error] {ex.Message}");
            }
        }

        private void ActivateCallerObject(string callerName)
        {
            try
            {
                var go = GameObject.Find(callerName);
                if (go != null)
                {
                    go.SetActive(true);
                    OnLog?.Invoke($"[Квест] Маркер задачи на карте активирован: {callerName}");
                    return;
                }

                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].name == callerName)
                    {
                        all[i].SetActive(true);
                        OnLog?.Invoke($"[Квест] Маркер задачи на карте активирован: {callerName}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[ActivateCallerObject Error] {ex.Message}");
            }
        }

        public string GetQuestIdFromTopic(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return string.Empty;
            string upper = topic.ToUpperInvariant();
            if (upper.Contains("WOOD")) return "Jobs_WoodOrder";
            if (upper.Contains("DRUNK")) return "Jobs_DrunkGuy";
            if (upper.Contains("SHIT") || upper.Contains("SEWAGE")) return "Jobs_Sewage";
            if (upper.Contains("MOVING")) return "Jobs_MovingJokke";
            if (upper.Contains("MUMMO") || upper.Contains("GRANDMA")) return "Jobs_Grandma";
            if (upper.Contains("UNCLE")) return "Jobs_Uncle";
            if (upper.Contains("FLEETARI")) return "Jobs_Fleetari";
            if (upper.Contains("SUSKI")) return "Jobs_Suski";
            return topic;
        }

        private void SendActiveQuestMarkersSnapshot(int clientId)
        {
            try
            {
                string[] callers = new string[]
                {
                    "call_woods",
                    "call_lift",
                    "shitwell01_call",
                    "shitwell02_call",
                    "shitwell03_call",
                    "shitwell04_call",
                    "shitwell05_call",
                    "call_movinghelp",
                    "call_mummo1",
                    "call_mummo2",
                    "call_mummo3",
                    "call_uncle"
                };

                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < callers.Length; i++)
                {
                    string cName = callers[i];
                    for (int j = 0; j < all.Length; j++)
                    {
                        if (all[j].name == cName && all[j].activeSelf)
                        {
                            string qId = GetQuestIdFromCallerName(cName);
                            SendPhoneAnswer(true, qId, cName, clientId);
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private string GetQuestIdFromCallerName(string callerName)
        {
            if (callerName == "call_woods") return "Jobs_WoodOrder";
            if (callerName == "call_lift") return "Jobs_DrunkGuy";
            if (callerName.StartsWith("shitwell")) return "Jobs_Sewage";
            if (callerName == "call_movinghelp") return "Jobs_MovingJokke";
            if (callerName.StartsWith("call_mummo")) return "Jobs_Grandma";
            if (callerName == "call_uncle") return "Jobs_Uncle";
            return callerName;
        }

        #endregion
    }

    /// <summary>
    /// Хук OnEnable на объекте Ring телефона (по архитектуре WreckMP TelephoneEventHandler).
    /// Срабатывает в момент, когда ванильная логика MSC активирует телефон для звонка.
    /// </summary>
    public class TelephoneRingHook : MonoBehaviour
    {
        public Action OnRingEnabled;

        private void OnEnable()
        {
            OnRingEnabled?.Invoke();
        }
    }
}
