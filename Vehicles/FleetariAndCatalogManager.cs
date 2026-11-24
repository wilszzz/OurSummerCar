using System;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Менеджер сетевой синхронизации автосервиса Флитари (Fleetari Repair Shop),
    /// каталога запчастей по почте (PartsMagazine/Envelope) и закиси азота (N2O Bottle).
    /// </summary>
    public class FleetariAndCatalogManager : MonoBehaviour
    {
        public static FleetariAndCatalogManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Автомастерская Флитари
        private PlayMakerFSM fleetariDeskOrderFsm;
        private PlayMakerFSM fleetariJobFsm;
        private PlayMakerFSM ferndaleKeysFsm;
        private PlayMakerFSM dynoFsm;
        private bool isSuppressingOrder = false;

        // Каталог запчастей в спальне
        private PlayMakerFSM partsMagazineFsm;
        private PlayMakerFSM envelopeFsm;
        private bool isSuppressingCatalog = false;

        // Закись азота
        private PlayMakerFSM n2oValveFsm;
        private bool isSuppressingN2O = false;

        private float nextScanTime = 0f;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;

            ScanObjects();
            OnLog?.Invoke("[Флитари/Каталог] Менеджер автосервиса и почтовых заказов инициализирован");
        }

        public void Reset()
        {
            fleetariDeskOrderFsm = null;
            fleetariJobFsm = null;
            ferndaleKeysFsm = null;
            dynoFsm = null;
            partsMagazineFsm = null;
            envelopeFsm = null;
            n2oValveFsm = null;
            isSuppressingOrder = false;
            isSuppressingCatalog = false;
            isSuppressingN2O = false;
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
                // 1. Автосервис Флитари: стойка заказов
                var deskOrderGo = GameObject.Find("REPAIRSHOP/Order") ?? GameObject.Find("Order");
                if (deskOrderGo != null)
                {
                    fleetariDeskOrderFsm = PlayMakerUtilities.FindFsmOnGameObject(deskOrderGo, "Use") ??
                                           PlayMakerUtilities.FindFsmOnGameObject(deskOrderGo, "Order") ??
                                           deskOrderGo.GetComponent<PlayMakerFSM>();
                    if (fleetariDeskOrderFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(fleetariDeskOrderFsm, "MP_SYNC_ORDER", "Check car");

                        fleetariDeskOrderFsm.InsertAction("Check car", () =>
                        {
                            if (isSuppressingOrder) return;
                            var priceVar = fleetariDeskOrderFsm.FsmVariables.FindFsmFloat("Price");
                            var purchVar = fleetariDeskOrderFsm.FsmVariables.FindFsmString("Purchase");
                            string job = purchVar != null ? purchVar.Value : "FleetariJob";
                            float cost = priceVar != null ? priceVar.Value : 0f;
                            SendFleetariOrder(job, cost);
                        }, 0, false);
                    }

                    fleetariJobFsm = PlayMakerUtilities.FindFsmOnGameObject(deskOrderGo, "Data");
                }

                var ferndaleGo = GameObject.Find("FERNDALE(1630kg)") ?? GameObject.Find("Ferndale");
                if (ferndaleGo != null)
                {
                    ferndaleKeysFsm = PlayMakerUtilities.FindFsmOnGameObject(ferndaleGo, "Keys") ??
                                      PlayMakerUtilities.FindFsmOnGameObject(ferndaleGo, "Ignition");
                }

                var dynoGo = GameObject.Find("Dyno") ?? GameObject.Find("Dynamometer");
                if (dynoGo != null)
                {
                    dynoFsm = PlayMakerUtilities.FindFsmOnGameObject(dynoGo, "Dyno") ??
                              dynoGo.GetComponent<PlayMakerFSM>();
                }

                // 2. Каталог запчастей и конверт
                var magGo = GameObject.Find("parts magazine(itemx)") ?? GameObject.Find("PartsCatalogPivot");
                if (magGo != null)
                {
                    partsMagazineFsm = PlayMakerUtilities.FindFsmOnGameObject(magGo, "Activate") ??
                                       PlayMakerUtilities.FindFsmOnGameObject(magGo, "Open") ??
                                       magGo.GetComponent<PlayMakerFSM>();
                    if (partsMagazineFsm != null && partsMagazineFsm.HasState("State 2"))
                    {
                        partsMagazineFsm.InsertAction("State 2", () =>
                        {
                            if (isSuppressingCatalog) return;
                            SendCatalogOrder(1, 1);
                        }, 0, false);
                    }
                }

                var envGo = GameObject.Find("envelope(xxxxx)") ?? GameObject.Find("EnvelopeSpawn");
                if (envGo != null)
                {
                    envelopeFsm = PlayMakerUtilities.FindFsmOnGameObject(envGo, "Envelope") ??
                                  envGo.GetComponent<PlayMakerFSM>();
                }

                // 3. Баллон N2O
                var n2oGo = GameObject.Find("n2obottle(itemx)") ?? GameObject.Find("n2obottle");
                if (n2oGo != null)
                {
                    n2oValveFsm = PlayMakerUtilities.FindFsmOnGameObject(n2oGo, "Use") ??
                                  n2oGo.GetComponent<PlayMakerFSM>();
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Флитари/Каталог] Ошибка сканирования: {ex.Message}");
            }
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 6f;
                if (fleetariDeskOrderFsm == null || partsMagazineFsm == null)
                {
                    ScanObjects();
                }
            }
        }

        public void SendFleetariOrder(string jobType, float cost)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.FleetariOrderSync))
            {
                packet.WriteString(jobType);
                packet.WriteSingle(cost);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Флитари] Заказ ремонта оформлен: {jobType} ({cost} mk)");
        }

        public void SendCatalogOrder(int itemHash, int quantity)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.CatalogOrderSync))
            {
                packet.WriteInt32(itemHash);
                packet.WriteInt32(quantity);
                transport.Send(packet);
            }
            OnLog?.Invoke("[Каталог] Почтовый заказ деталей отправлен на почту Теймо!");
        }

        public void SendNitrousState(bool valveOpen, float pressure)
        {
            if (isSuppressingN2O || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.NitrousBoostSync))
            {
                packet.WriteBoolean(valveOpen);
                packet.WriteSingle(pressure);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.FleetariOrderSync:
                    HandleFleetariOrderSync(reader);
                    break;

                case PacketType.CatalogOrderSync:
                    HandleCatalogOrderSync(reader);
                    break;

                case PacketType.NitrousBoostSync:
                    HandleNitrousSync(reader);
                    break;
            }
        }

        private void HandleFleetariOrderSync(PacketReader reader)
        {
            string job = reader.ReadString();
            float cost = reader.ReadSingle();

            isSuppressingOrder = true;
            try
            {
                if (fleetariDeskOrderFsm != null)
                {
                    var priceVar = fleetariDeskOrderFsm.FsmVariables.FindFsmFloat("Price");
                    var purchVar = fleetariDeskOrderFsm.FsmVariables.FindFsmString("Purchase");
                    if (priceVar != null) priceVar.Value = cost;
                    if (purchVar != null) purchVar.Value = job;

                    fleetariDeskOrderFsm.SendEvent("MP_SYNC_ORDER");
                }

                if (fleetariJobFsm != null)
                {
                    fleetariJobFsm.SendEvent("SAVE");
                }
            }
            finally
            {
                isSuppressingOrder = false;
            }

            OnLog?.Invoke($"[Флитари] Друг оформил заказ в автосервисе: {job} ({cost} mk)");
        }

        private void HandleCatalogOrderSync(PacketReader reader)
        {
            int hash = reader.ReadInt32();
            int qty = reader.ReadInt32();

            isSuppressingCatalog = true;
            try
            {
                if (envelopeFsm != null)
                {
                    envelopeFsm.SendEvent("ORDER");
                }
            }
            finally
            {
                isSuppressingCatalog = false;
            }

            OnLog?.Invoke($"[Каталог] Принят почтовый заказ деталей #{hash} (кол-во: {qty})");
        }

        private void HandleNitrousSync(PacketReader reader)
        {
            bool open = reader.ReadBoolean();
            float pressure = reader.ReadSingle();

            isSuppressingN2O = true;
            try
            {
                if (n2oValveFsm != null)
                {
                    var pVar = n2oValveFsm.FsmVariables.FindFsmFloat("Pressure");
                    if (pVar != null) pVar.Value = pressure;
                    n2oValveFsm.SendEvent(open ? "OPEN" : "CLOSE");
                }
            }
            finally
            {
                isSuppressingN2O = false;
            }
        }
    }
}
