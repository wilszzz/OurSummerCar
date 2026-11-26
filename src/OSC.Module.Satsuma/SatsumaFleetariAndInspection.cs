using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Satsuma
{
    /// <summary>
    /// Синхронизация автосервиса Флетари (заказ, таймер/статус работ, ключи Ferndale, диностенд),
    /// почтового каталога запчастей и конверта, баллона N2O, техосмотра Линделла (Katsastus + номера RegPlateGen)
    /// и координации общего кошелька через SharedWalletChangedEvent (PacketType.SatsumaFleetariAndCatalogSync = 99).
    /// </summary>
    internal sealed class SatsumaFleetariAndInspection
    {
        private sealed class PendingSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private static readonly MethodInfo MiGenerateRegPlate = typeof(RegPlateGen).GetMethod(
            "GenerateRegPlate",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new[] { typeof(string) },
            null);

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        // Автосервис Флетари
        private PlayMakerFSM fleetariDeskOrderFsm;
        private PlayMakerFSM fleetariJobFsm;
        private PlayMakerFSM ferndaleKeysFsm;
        private PlayMakerFSM dynoFsm;
        private FsmBool playerKeyFerndaleVar;

        // Каталог запчастей и конверт
        private PlayMakerFSM partsMagazineFsm;
        private PlayMakerFSM envelopeFsm;

        // Баллон N2O
        private PlayMakerFSM n2oValveFsm;

        // Техосмотр Линделла (Katsastus) и номера
        private PlayMakerFSM inspectionFsm;
        private PlayMakerFSM inspectionResultsFsm;
        private PlayMakerFSM inspectionOrderFsm;
        private PlayMakerFSM regPlateFrontFsm;
        private PlayMakerFSM regPlateRearFsm;
        private GameObject satsumaRoot;

        // Кэшированные переменные состояния (для снимка и дельта-опроса)
        private string lastOrderJob = string.Empty;
        private float lastOrderCost;
        private bool hasActiveOrder;

        private float lastJobTimer = -1f;
        private bool lastJobActive;
        private bool lastJobCompleted;
        private string lastJobState = string.Empty;
        private float lastJobPollSyncTime;

        private bool lastFerndaleKeys;
        private string lastFerndaleKeysState = string.Empty;

        private bool lastDynoActive;
        private float lastDynoPower;
        private string lastDynoState = string.Empty;

        private int lastCatalogHash;
        private int lastCatalogQty;
        private bool hasPendingCatalogOrder;
        private string lastEnvelopeState = string.Empty;

        private bool lastN2OOpen;
        private float lastN2OPressure = -1f;
        private float lastN2OSyncTime;

        private bool hasInspectionResult;
        private bool lastInspectionPassed;
        private byte lastInspectionCheckmarks;
        private bool lastPlatesInstalled;
        private string lastPlateString = "VBX-403";

        // Общий кошелёк
        private FsmFloat playerMoneyVar;
        private float lastKnownMoney = -1f;
        private float walletWatchUntilTime;
        private float suppressWalletDetectUntilTime;
        private bool isApplyingWalletEvent;

        // Флаги анти-эхо
        private bool isSuppressingOrder;
        private bool isSuppressingJobStatus;
        private bool isSuppressingFerndaleKeys;
        private bool isSuppressingDyno;
        private bool isSuppressingCatalog;
        private bool isSuppressingN2O;
        private bool isSuppressingInspection;
        private bool isSuppressingPlates;

        // Таймеры сканирования и отложенные снимки
        private float nextScanTime;
        private float nextPollTime;
        private readonly HashSet<int> hookedFsmIds = new HashSet<int>();
        private readonly List<PendingSnapshot> pendingSnapshots = new List<PendingSnapshot>();

        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;
        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public string StatusSummary =>
            $"Флетари: {(fleetariDeskOrderFsm != null ? "OK" : "-")}, Каталог: {(partsMagazineFsm != null ? "OK" : "-")}, N2O: {(n2oValveFsm != null ? "OK" : "-")}, Техосмотр: {(inspectionResultsFsm != null ? "OK" : "-")}";

        public SatsumaFleetariAndInspection(OscHostContext context, string moduleId)
        {
            ctx = context;
            this.moduleId = moduleId;
        }

        public void OnLevelLoaded()
        {
            Reset();
            ScanObjects();
        }

        public void Reset()
        {
            fleetariDeskOrderFsm = null;
            fleetariJobFsm = null;
            ferndaleKeysFsm = null;
            dynoFsm = null;
            playerKeyFerndaleVar = null;

            partsMagazineFsm = null;
            envelopeFsm = null;
            n2oValveFsm = null;

            inspectionFsm = null;
            inspectionResultsFsm = null;
            inspectionOrderFsm = null;
            regPlateFrontFsm = null;
            regPlateRearFsm = null;
            satsumaRoot = null;

            lastOrderJob = string.Empty;
            lastOrderCost = 0f;
            hasActiveOrder = false;

            lastJobTimer = -1f;
            lastJobActive = false;
            lastJobCompleted = false;
            lastJobState = string.Empty;
            lastJobPollSyncTime = 0f;

            lastFerndaleKeys = false;
            lastFerndaleKeysState = string.Empty;

            lastDynoActive = false;
            lastDynoPower = 0f;
            lastDynoState = string.Empty;

            lastCatalogHash = 0;
            lastCatalogQty = 0;
            hasPendingCatalogOrder = false;
            lastEnvelopeState = string.Empty;

            lastN2OOpen = false;
            lastN2OPressure = -1f;
            lastN2OSyncTime = 0f;

            hasInspectionResult = false;
            lastInspectionPassed = false;
            lastInspectionCheckmarks = 0;
            lastPlatesInstalled = false;
            lastPlateString = "VBX-403";

            playerMoneyVar = null;
            lastKnownMoney = -1f;
            walletWatchUntilTime = 0f;
            suppressWalletDetectUntilTime = 0f;
            isApplyingWalletEvent = false;

            isSuppressingOrder = false;
            isSuppressingJobStatus = false;
            isSuppressingFerndaleKeys = false;
            isSuppressingDyno = false;
            isSuppressingCatalog = false;
            isSuppressingN2O = false;
            isSuppressingInspection = false;
            isSuppressingPlates = false;

            nextScanTime = 0f;
            nextPollTime = 0f;
            hookedFsmIds.Clear();
            pendingSnapshots.Clear();
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            float now = Time.time;
            if (now >= nextScanTime)
            {
                nextScanTime = now + 5.0f;
                if (NeedsRescan())
                {
                    ScanObjects();
                }
            }

            CheckPendingWalletPublish(now);

            if (!IsNetworkRunning) return;

            ProcessPendingSnapshots(now);

            if (now >= nextPollTime)
            {
                nextPollTime = now + 0.35f;
                PollFleetariJobAndDyno(now);
                PollFerndaleKeys();
                PollEnvelopeAndN2O(now);
            }
        }

        public void OnClientConnected(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ScanObjects();
            SendSnapshotToClient(clientId);

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == clientId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }
            pendingSnapshots.Add(new PendingSnapshot
            {
                ClientId = clientId,
                SendAtTime = Time.time + 2.0f
            });
        }

        public void OnClientDisconnected(int clientId)
        {
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (pendingSnapshots[i].ClientId == clientId)
                {
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        public void HandleSharedWalletEvent(SharedWalletChangedEvent evt)
        {
            if (evt.SourceModuleId == moduleId) return;

            EnsureMoneyVar();
            isApplyingWalletEvent = true;
            suppressWalletDetectUntilTime = Time.time + 0.6f;
            walletWatchUntilTime = 0f;
            try
            {
                if (playerMoneyVar != null)
                {
                    playerMoneyVar.Value = evt.Money;
                    lastKnownMoney = evt.Money;
                }
            }
            finally
            {
                isApplyingWalletEvent = false;
            }
        }

        private void ProcessPendingSnapshots(float now)
        {
            if (!ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (now >= pendingSnapshots[i].SendAtTime)
                {
                    int clientId = pendingSnapshots[i].ClientId;
                    pendingSnapshots.RemoveAt(i);
                    ScanObjects();
                    SendSnapshotToClient(clientId);
                }
            }
        }

        private bool NeedsRescan()
        {
            return fleetariDeskOrderFsm == null
                || partsMagazineFsm == null
                || envelopeFsm == null
                || n2oValveFsm == null
                || inspectionResultsFsm == null
                || playerMoneyVar == null;
        }

        #region Discovery & Hooks

        private void ScanObjects()
        {
            try
            {
                EnsureMoneyVar();

                if (satsumaRoot == null)
                {
                    satsumaRoot = GameObject.Find("SATSUMA(557kg, 248)")
                               ?? GameObject.Find("SATSUMA(580kg, 87)")
                               ?? GameObject.Find("Satsuma");
                }

                ScanFleetari();
                ScanCatalogAndN2O();
                ScanInspectionAndPlates();
            }
            catch (Exception ex)
            {
                ctx?.Log?.Invoke($"[Satsuma.Fleetari] Ошибка сканирования: {ex.Message}");
            }
        }

        private void EnsureMoneyVar()
        {
            if (playerMoneyVar != null) return;
            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                playerMoneyVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
                if (playerMoneyVar != null && lastKnownMoney < 0f)
                {
                    lastKnownMoney = playerMoneyVar.Value;
                }

                if (playerKeyFerndaleVar == null)
                {
                    playerKeyFerndaleVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerKeyFerndale")
                                        ?? PlayMakerGlobals.Instance.Variables.FindFsmBool("FerndaleKeys");
                    if (playerKeyFerndaleVar != null)
                    {
                        lastFerndaleKeys = playerKeyFerndaleVar.Value;
                    }
                }
            }
        }

        private bool TryClaimSafe(PlayMakerFSM fsm, string claimContext)
        {
            if (fsm == null || ctx == null || ctx.Ownership == null) return false;
            string currentOwner = ctx.Ownership.GetOwner(fsm);
            if (currentOwner != null && currentOwner != moduleId)
            {
                return false;
            }
            return ctx.Ownership.TryClaim(fsm, moduleId, claimContext);
        }

        private void ScanFleetari()
        {
            // 1. Стойка заказов Флетари
            if (fleetariDeskOrderFsm == null)
            {
                GameObject deskOrderGo = GameObject.Find("REPAIRSHOP/Order") ?? GameObject.Find("Order");
                if (deskOrderGo != null)
                {
                    PlayMakerFSM orderFsm = PlayMakerBridge.FindFsm(deskOrderGo, "Use")
                                         ?? PlayMakerBridge.FindFsm(deskOrderGo, "Order")
                                         ?? deskOrderGo.GetComponent<PlayMakerFSM>();

                    if (orderFsm != null && !hookedFsmIds.Contains(orderFsm.GetInstanceID()) && TryClaimSafe(orderFsm, "Satsuma_FleetariOrder"))
                    {
                        fleetariDeskOrderFsm = orderFsm;
                        hookedFsmIds.Add(orderFsm.GetInstanceID());
                        orderFsm.InitializeSafe();

                        if (orderFsm.HasState("Check car"))
                        {
                            orderFsm.AddSafeGlobalTransition("MP_SYNC_ORDER", "Check car");
                            orderFsm.InsertAction("Check car", () =>
                            {
                                if (isSuppressingOrder) return;

                                FsmFloat priceVar = orderFsm.FsmVariables.FindFsmFloat("Price");
                                FsmString purchVar = orderFsm.FsmVariables.FindFsmString("Purchase");
                                string job = purchVar != null ? purchVar.Value : "FleetariJob";
                                float cost = priceVar != null ? priceVar.Value : 0f;

                                lastOrderJob = job;
                                lastOrderCost = cost;
                                hasActiveOrder = true;
                                BeginWalletWatch();
                                SendFleetariOrder(job, cost);
                            }, 0, true);
                        }
                    }

                    if (fleetariJobFsm == null)
                    {
                        fleetariJobFsm = PlayMakerBridge.FindFsm(deskOrderGo, "Data");
                    }
                }
            }

            if (fleetariJobFsm == null)
            {
                GameObject repairShopGo = GameObject.Find("REPAIRSHOP");
                if (repairShopGo != null)
                {
                    fleetariJobFsm = PlayMakerBridge.FindFsm(repairShopGo, "Data")
                                  ?? PlayMakerBridge.FindFsm(repairShopGo, "Work")
                                  ?? PlayMakerBridge.FindFsm(repairShopGo, "Job");
                }
            }

            if (fleetariJobFsm != null)
            {
                fleetariJobFsm.InitializeSafe();
                if (string.IsNullOrEmpty(lastJobState))
                {
                    ReadJobStatus(out lastJobTimer, out lastJobActive, out lastJobCompleted, out lastJobState);
                }
            }

            // 2. Ключи от Ferndale
            if (ferndaleKeysFsm == null)
            {
                GameObject ferndaleGo = GameObject.Find("FERNDALE(1630kg)") ?? GameObject.Find("Ferndale");
                if (ferndaleGo != null)
                {
                    ferndaleKeysFsm = PlayMakerBridge.FindFsm(ferndaleGo, "Keys")
                                   ?? PlayMakerBridge.FindFsm(ferndaleGo, "Ignition");
                    if (ferndaleKeysFsm != null)
                    {
                        ferndaleKeysFsm.InitializeSafe();
                        lastFerndaleKeysState = ferndaleKeysFsm.ActiveStateName ?? string.Empty;
                    }
                }
            }

            // 3. Диностенд (Dyno)
            if (dynoFsm == null)
            {
                GameObject dynoGo = GameObject.Find("Dyno") ?? GameObject.Find("Dynamometer") ?? GameObject.Find("REPAIRSHOP/Dyno");
                if (dynoGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(dynoGo, "Dyno")
                                    ?? PlayMakerBridge.FindFsm(dynoGo, "Use")
                                    ?? dynoGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Satsuma_Dyno"))
                    {
                        dynoFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();
                        ReadDynoState(out lastDynoActive, out lastDynoPower, out lastDynoState);

                        if (fsm.HasState("Pay"))
                        {
                            fsm.InsertAction("Pay", () =>
                            {
                                if (isSuppressingDyno) return;
                                BeginWalletWatch();
                                ReadDynoState(out bool active, out float power, out string st);
                                SendDynoState(active, power, st);
                            }, 0, true);
                        }
                    }
                }
            }
        }

        private void ScanCatalogAndN2O()
        {
            // 1. Журнал запчастей
            if (partsMagazineFsm == null)
            {
                GameObject magGo = GameObject.Find("parts magazine(itemx)") ?? GameObject.Find("PartsCatalogPivot");
                if (magGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(magGo, "Activate")
                                    ?? PlayMakerBridge.FindFsm(magGo, "Open")
                                    ?? magGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null && !hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Satsuma_PartsMagazine"))
                    {
                        partsMagazineFsm = fsm;
                        hookedFsmIds.Add(fsm.GetInstanceID());
                        fsm.InitializeSafe();

                        if (fsm.HasState("State 2"))
                        {
                            fsm.InsertAction("State 2", () =>
                            {
                                if (isSuppressingCatalog) return;
                                BeginWalletWatch();
                                SendCatalogOrder(1, 1);
                            }, 0, true);
                        }

                        if (fsm.HasState("Order"))
                        {
                            fsm.InsertAction("Order", () =>
                            {
                                if (isSuppressingCatalog) return;
                                BeginWalletWatch();
                                SendCatalogOrder(1, 1);
                            }, 0, true);
                        }
                    }
                }
            }

            // 2. Почтовый конверт заказа запчастей
            if (envelopeFsm == null)
            {
                GameObject envGo = GameObject.Find("envelope(xxxxx)") ?? GameObject.Find("EnvelopeSpawn") ?? GameObject.Find("PostOrder");
                if (envGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(envGo, "Envelope") ?? envGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        envelopeFsm = fsm;
                        fsm.InitializeSafe();
                        lastEnvelopeState = fsm.ActiveStateName ?? string.Empty;

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Satsuma_CatalogEnvelope"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            if (fsm.HasState("Order"))
                            {
                                fsm.InsertAction("Order", () =>
                                {
                                    if (isSuppressingCatalog) return;
                                    BeginWalletWatch();
                                    SendEnvelopePost(true, fsm.ActiveStateName ?? "Order");
                                }, 0, true);
                            }
                        }
                    }
                }
            }

            // 3. Баллон закиси азота (N2O)
            if (n2oValveFsm == null)
            {
                GameObject n2oGo = GameObject.Find("n2obottle(itemx)") ?? GameObject.Find("n2obottle") ?? GameObject.Find("n2o bottle(itemx)");
                if (n2oGo != null)
                {
                    PlayMakerFSM fsm = PlayMakerBridge.FindFsm(n2oGo, "Use") ?? n2oGo.GetComponent<PlayMakerFSM>();
                    if (fsm != null)
                    {
                        n2oValveFsm = fsm;
                        fsm.InitializeSafe();
                        ReadN2OState(out lastN2OOpen, out lastN2OPressure);

                        if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Satsuma_N2OValve"))
                        {
                            hookedFsmIds.Add(fsm.GetInstanceID());
                            if (fsm.HasState("Open"))
                            {
                                fsm.InsertAction("Open", () =>
                                {
                                    if (isSuppressingN2O) return;
                                    ReadN2OState(out _, out float pressure);
                                    SendN2OState(true, pressure);
                                }, 0, true);
                            }
                            if (fsm.HasState("Close"))
                            {
                                fsm.InsertAction("Close", () =>
                                {
                                    if (isSuppressingN2O) return;
                                    ReadN2OState(out _, out float pressure);
                                    SendN2OState(false, pressure);
                                }, 0, true);
                            }
                        }
                    }
                }
            }
        }

        private void ScanInspectionAndPlates()
        {
            // 1. Линия техосмотра A-Katsastus (инспектор Линделл)
            if (inspectionResultsFsm == null || inspectionFsm == null)
            {
                GameObject inspGo = GameObject.Find("InspectionProcess") ?? GameObject.Find("INSPECTION");
                if (inspGo != null)
                {
                    if (inspectionFsm == null)
                    {
                        inspectionFsm = PlayMakerBridge.FindFsm(inspGo, "Inspect")
                                     ?? PlayMakerBridge.FindFsm(inspGo, "Information")
                                     ?? inspGo.GetComponent<PlayMakerFSM>();
                    }

                    PlayMakerFSM resultsFsm = PlayMakerBridge.FindFsm(inspGo, "Results");
                    if (resultsFsm != null && !hookedFsmIds.Contains(resultsFsm.GetInstanceID()) && TryClaimSafe(resultsFsm, "Satsuma_InspectionResults"))
                    {
                        inspectionResultsFsm = resultsFsm;
                        hookedFsmIds.Add(resultsFsm.GetInstanceID());
                        resultsFsm.InitializeSafe();

                        if (resultsFsm.HasState("Results"))
                        {
                            resultsFsm.InsertAction("Results", () =>
                            {
                                if (isSuppressingInspection) return;

                                FsmBool passVar = resultsFsm.FsmVariables.FindFsmBool("Pass");
                                bool passed = passVar != null && passVar.Value;
                                byte checkmarks = ReadInspectionCheckmarks(resultsFsm);

                                hasInspectionResult = true;
                                lastInspectionPassed = passed;
                                lastInspectionCheckmarks = checkmarks;
                                lastPlatesInstalled = passed || IsAnyPlateInstalled();
                                lastPlateString = GetCurrentPlateString();

                                BeginWalletWatch();
                                SendInspectionResult(passed, checkmarks, lastPlatesInstalled, lastPlateString);
                            }, 0, true);
                        }
                    }
                }
            }

            // 2. Оплата техосмотра (INSPECTION/Functions/Order)
            if (inspectionOrderFsm == null)
            {
                GameObject inspRoot = GameObject.Find("INSPECTION");
                Transform orderTr = inspRoot != null ? inspRoot.transform.Find("Functions/Order") : null;
                if (orderTr != null)
                {
                    PlayMakerFSM orderFsm = orderTr.GetComponent<PlayMakerFSM>();
                    if (orderFsm != null && !hookedFsmIds.Contains(orderFsm.GetInstanceID()) && TryClaimSafe(orderFsm, "Satsuma_InspectionOrder"))
                    {
                        inspectionOrderFsm = orderFsm;
                        hookedFsmIds.Add(orderFsm.GetInstanceID());
                        orderFsm.InitializeSafe();

                        if (orderFsm.HasState("Pay"))
                        {
                            orderFsm.AddSafeGlobalTransition("MP_TRIGGER", "Pay");
                            orderFsm.InsertAction("Pay", () =>
                            {
                                if (isSuppressingInspection) return;
                                BeginWalletWatch();
                            }, 0, true);
                        }
                    }
                }
            }

            // 3. Номерные знаки (защитно: Vehicles.Controls уже может владеть ими — не конфликтуем в OwnershipRegistry)
            if (regPlateFrontFsm == null) regPlateFrontFsm = FindAndOptionallyHookPlate("RegPlateFront");
            if (regPlateRearFsm == null) regPlateRearFsm = FindAndOptionallyHookPlate("RegPlateRear");
        }

        private PlayMakerFSM FindAndOptionallyHookPlate(string objectName)
        {
            GameObject plateGo = (satsumaRoot != null ? PlayMakerBridge.FindChildRecursive(satsumaRoot.transform, objectName)?.gameObject : null)
                              ?? GameObject.Find(objectName);
            if (plateGo == null) return null;

            PlayMakerFSM fsm = PlayMakerBridge.FindFsm(plateGo, "Data");
            if (fsm == null) return null;
            fsm.InitializeSafe();

            if (!hookedFsmIds.Contains(fsm.GetInstanceID()) && TryClaimSafe(fsm, "Satsuma_" + objectName))
            {
                hookedFsmIds.Add(fsm.GetInstanceID());
                fsm.AddSafeGlobalTransition("MP_SYNC_INSTALL", "Installed");
                fsm.AddSafeGlobalTransition("MP_SYNC_DISABLE", "Disable plate");

                if (fsm.HasState("Installed"))
                {
                    fsm.InsertAction("Installed", () =>
                    {
                        if (isSuppressingPlates) return;
                        lastPlatesInstalled = true;
                        lastPlateString = GetCurrentPlateString();
                        SendInspectionResult(lastInspectionPassed, lastInspectionCheckmarks, true, lastPlateString);
                    }, 0, true);
                }
                if (fsm.HasState("Disable plate"))
                {
                    fsm.InsertAction("Disable plate", () =>
                    {
                        if (isSuppressingPlates) return;
                        lastPlatesInstalled = false;
                        lastPlateString = GetCurrentPlateString();
                        SendInspectionResult(lastInspectionPassed, lastInspectionCheckmarks, false, lastPlateString);
                    }, 0, true);
                }
            }

            return fsm;
        }

        #endregion

        #region Polling & Wallet Coordination

        private void BeginWalletWatch()
        {
            EnsureMoneyVar();
            walletWatchUntilTime = Time.time + 2.0f;
            if (playerMoneyVar != null && lastKnownMoney < 0f)
            {
                lastKnownMoney = playerMoneyVar.Value;
            }
        }

        private void CheckPendingWalletPublish(float now)
        {
            if (playerMoneyVar == null || isApplyingWalletEvent) return;

            float currentMoney = playerMoneyVar.Value;
            if (lastKnownMoney < 0f)
            {
                lastKnownMoney = currentMoney;
                return;
            }

            if (now < suppressWalletDetectUntilTime)
            {
                lastKnownMoney = currentMoney;
                return;
            }

            if (now <= walletWatchUntilTime && Mathf.Abs(currentMoney - lastKnownMoney) >= 0.5f)
            {
                lastKnownMoney = currentMoney;
                walletWatchUntilTime = 0f;

                ctx?.Events?.Publish(new SharedWalletChangedEvent
                {
                    Money = currentMoney,
                    SourceModuleId = moduleId,
                    IsAuthoritative = ctx.Transport != null && ctx.Transport.IsServer
                });
            }
        }

        private void PollFleetariJobAndDyno(float now)
        {
            if (!isSuppressingJobStatus && fleetariJobFsm != null)
            {
                ReadJobStatus(out float timer, out bool active, out bool completed, out string st);
                bool stateChanged = active != lastJobActive || completed != lastJobCompleted || !string.Equals(st, lastJobState, StringComparison.Ordinal);
                bool timerTrickle = ctx.Transport.IsServer && (now - lastJobPollSyncTime) >= 2.0f && Mathf.Abs(timer - lastJobTimer) > 0.5f;

                if (stateChanged || timerTrickle)
                {
                    lastJobTimer = timer;
                    lastJobActive = active;
                    lastJobCompleted = completed;
                    lastJobState = st;
                    lastJobPollSyncTime = now;
                    SendFleetariJobStatus(timer, active, completed, st);
                }
            }

            if (!isSuppressingDyno && dynoFsm != null)
            {
                ReadDynoState(out bool active, out float power, out string st);
                if (active != lastDynoActive || Mathf.Abs(power - lastDynoPower) > 0.5f || !string.Equals(st, lastDynoState, StringComparison.Ordinal))
                {
                    lastDynoActive = active;
                    lastDynoPower = power;
                    lastDynoState = st;
                    SendDynoState(active, power, st);
                }
            }
        }

        private void PollFerndaleKeys()
        {
            if (isSuppressingFerndaleKeys) return;

            bool hasKeys = playerKeyFerndaleVar != null ? playerKeyFerndaleVar.Value : lastFerndaleKeys;
            string st = ferndaleKeysFsm != null ? (ferndaleKeysFsm.ActiveStateName ?? string.Empty) : string.Empty;

            if (hasKeys != lastFerndaleKeys || !string.Equals(st, lastFerndaleKeysState, StringComparison.Ordinal))
            {
                lastFerndaleKeys = hasKeys;
                lastFerndaleKeysState = st;
                SendFerndaleKeys(hasKeys, st);
            }
        }

        private void PollEnvelopeAndN2O(float now)
        {
            if (!isSuppressingCatalog && envelopeFsm != null)
            {
                string st = envelopeFsm.ActiveStateName ?? string.Empty;
                if (!string.Equals(st, lastEnvelopeState, StringComparison.Ordinal))
                {
                    lastEnvelopeState = st;
                    bool ordered = st.IndexOf("Order", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   st.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) >= 0;
                    hasPendingCatalogOrder = ordered;
                    SendEnvelopePost(ordered, st);
                }
            }

            if (!isSuppressingN2O && n2oValveFsm != null)
            {
                ReadN2OState(out bool open, out float pressure);
                bool openChanged = open != lastN2OOpen;
                bool pressureChanged = Mathf.Abs(pressure - lastN2OPressure) > 1.0f && (now - lastN2OSyncTime) >= 1.0f;

                if (openChanged || pressureChanged)
                {
                    lastN2OOpen = open;
                    lastN2OPressure = pressure;
                    lastN2OSyncTime = now;
                    SendN2OState(open, pressure);
                }
            }
        }

        #endregion

        #region Sending

        private void SendFleetariOrder(string jobType, float cost)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.FleetariOrder);
                p.WriteInt32(LocalPlayerId);
                p.WriteString(jobType ?? "FleetariJob");
                p.WriteSingle(cost);
                ctx.Transport.Send(p);
            }
        }

        private void SendFleetariJobStatus(float timer, bool active, bool completed, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.FleetariJobStatus);
                p.WriteInt32(LocalPlayerId);
                p.WriteSingle(timer);
                p.WriteBoolean(active);
                p.WriteBoolean(completed);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendFerndaleKeys(bool hasKeys, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.FerndaleKeys);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(hasKeys);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendDynoState(bool active, float power, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.DynoState);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(active);
                p.WriteSingle(power);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendCatalogOrder(int itemHash, int quantity)
        {
            if (!IsNetworkRunning) return;
            lastCatalogHash = itemHash;
            lastCatalogQty = quantity;
            hasPendingCatalogOrder = true;

            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.CatalogOrder);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(itemHash);
                p.WriteInt32(quantity);
                ctx.Transport.Send(p);
            }
        }

        private void SendEnvelopePost(bool ordered, string stateName)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.EnvelopePost);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(ordered);
                p.WriteString(stateName ?? string.Empty);
                ctx.Transport.Send(p);
            }
        }

        private void SendN2OState(bool valveOpen, float pressure)
        {
            if (!IsNetworkRunning) return;
            lastN2OOpen = valveOpen;
            lastN2OPressure = pressure;
            lastN2OSyncTime = Time.time;

            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.N2OValve);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(valveOpen);
                p.WriteSingle(pressure);
                ctx.Transport.Send(p);
            }
        }

        private void SendInspectionResult(bool passed, byte checkmarks, bool platesInstalled, string plateString)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.InspectionResult);
                p.WriteInt32(LocalPlayerId);
                p.WriteBoolean(passed);
                p.WriteByte(checkmarks);
                p.WriteBoolean(platesInstalled);
                p.WriteString(plateString ?? "VBX-403");
                ctx.Transport.Send(p);
            }
        }

        private void SendSnapshotToClient(int clientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            ReadJobStatus(out float jobTimer, out bool jobActive, out bool jobCompleted, out string jobState);
            ReadDynoState(out bool dynoActive, out float dynoPower, out string dynoState);
            ReadN2OState(out bool n2oOpen, out float n2oPressure);
            bool hasKeys = playerKeyFerndaleVar != null ? playerKeyFerndaleVar.Value : lastFerndaleKeys;
            string keysState = ferndaleKeysFsm != null ? (ferndaleKeysFsm.ActiveStateName ?? string.Empty) : lastFerndaleKeysState;
            string envState = envelopeFsm != null ? (envelopeFsm.ActiveStateName ?? string.Empty) : lastEnvelopeState;
            string plateStr = GetCurrentPlateString();
            bool platesInstalled = lastPlatesInstalled || IsAnyPlateInstalled();

            using (var p = new PacketWriter(PacketType.SatsumaFleetariAndCatalogSync))
            {
                p.WriteByte(SatsumaProtocol.FleetariSnapshot);
                p.WriteInt32(LocalPlayerId);

                p.WriteBoolean(hasActiveOrder);
                p.WriteString(lastOrderJob);
                p.WriteSingle(lastOrderCost);
                p.WriteSingle(jobTimer);
                p.WriteBoolean(jobActive);
                p.WriteBoolean(jobCompleted);
                p.WriteString(jobState);

                p.WriteBoolean(hasKeys);
                p.WriteString(keysState);

                p.WriteBoolean(dynoActive);
                p.WriteSingle(dynoPower);
                p.WriteString(dynoState);

                p.WriteBoolean(hasPendingCatalogOrder);
                p.WriteInt32(lastCatalogHash);
                p.WriteInt32(lastCatalogQty);
                p.WriteString(envState);

                p.WriteBoolean(n2oOpen);
                p.WriteSingle(n2oPressure);

                p.WriteBoolean(hasInspectionResult);
                p.WriteBoolean(lastInspectionPassed);
                p.WriteByte(lastInspectionCheckmarks);
                p.WriteBoolean(platesInstalled);
                p.WriteString(plateStr);

                ctx.Transport.SendTo(p, clientId);
            }
        }

        #endregion

        #region Packet Handling

        public void HandleFleetariPacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 5) return;
            byte subType = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            switch (subType)
            {
                case SatsumaProtocol.FleetariOrder:
                    ApplyFleetariOrder(reader.ReadString(), reader.ReadSingle(), triggerOrderFsm: true);
                    break;

                case SatsumaProtocol.FleetariJobStatus:
                    ApplyFleetariJobStatus(reader.ReadSingle(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadString());
                    break;

                case SatsumaProtocol.FerndaleKeys:
                    ApplyFerndaleKeys(reader.ReadBoolean(), reader.ReadString());
                    break;

                case SatsumaProtocol.DynoState:
                    ApplyDynoState(reader.ReadBoolean(), reader.ReadSingle(), reader.ReadString());
                    break;

                case SatsumaProtocol.CatalogOrder:
                    ApplyCatalogOrder(reader.ReadInt32(), reader.ReadInt32(), triggerEnvelope: true);
                    break;

                case SatsumaProtocol.EnvelopePost:
                    ApplyEnvelopePost(reader.ReadBoolean(), reader.ReadString());
                    break;

                case SatsumaProtocol.N2OValve:
                    ApplyN2OState(reader.ReadBoolean(), reader.ReadSingle());
                    break;

                case SatsumaProtocol.InspectionResult:
                    ApplyInspectionResult(reader.ReadBoolean(), reader.ReadByte(), reader.ReadBoolean(), reader.ReadString(), triggerResultsFsm: true);
                    break;

                case SatsumaProtocol.FleetariSnapshot:
                    ApplyFleetariSnapshot(reader);
                    break;
            }
        }

        private void ApplyFleetariOrder(string job, float cost, bool triggerOrderFsm)
        {
            lastOrderJob = job ?? string.Empty;
            lastOrderCost = cost;
            hasActiveOrder = true;
            suppressWalletDetectUntilTime = Time.time + 0.6f;

            isSuppressingOrder = true;
            try
            {
                if (fleetariDeskOrderFsm != null)
                {
                    FsmFloat priceVar = fleetariDeskOrderFsm.FsmVariables.FindFsmFloat("Price");
                    FsmString purchVar = fleetariDeskOrderFsm.FsmVariables.FindFsmString("Purchase");
                    if (priceVar != null) priceVar.Value = cost;
                    if (purchVar != null) purchVar.Value = lastOrderJob;

                    if (triggerOrderFsm)
                    {
                        fleetariDeskOrderFsm.SendEvent("MP_SYNC_ORDER");
                    }
                }

                if (triggerOrderFsm && fleetariJobFsm != null)
                {
                    fleetariJobFsm.SendEvent("SAVE");
                }

                if (playerMoneyVar != null) lastKnownMoney = playerMoneyVar.Value;
            }
            finally
            {
                isSuppressingOrder = false;
            }
        }

        private void ApplyFleetariJobStatus(float timer, bool active, bool completed, string stateName)
        {
            lastJobTimer = timer;
            lastJobActive = active;
            lastJobCompleted = completed;
            lastJobState = stateName ?? string.Empty;

            isSuppressingJobStatus = true;
            try
            {
                if (fleetariJobFsm != null && fleetariJobFsm.FsmVariables != null)
                {
                    SetFirstFloat(fleetariJobFsm, timer, "Time", "Timer", "JobTime", "WorkTime", "WaitTime", "OrderTime");
                    SetFirstBool(fleetariJobFsm, active, "Job", "Ordered", "Order", "Active");
                    SetFirstBool(fleetariJobFsm, completed, "Done", "Complete", "Completed", "Ready");

                    if (completed && fleetariJobFsm.HasState("Done") && fleetariJobFsm.ActiveStateName != "Done")
                    {
                        fleetariJobFsm.SendEvent("DONE");
                    }
                }
            }
            finally
            {
                isSuppressingJobStatus = false;
            }
        }

        private void ApplyFerndaleKeys(bool hasKeys, string stateName)
        {
            lastFerndaleKeys = hasKeys;
            lastFerndaleKeysState = stateName ?? string.Empty;

            isSuppressingFerndaleKeys = true;
            try
            {
                EnsureMoneyVar();
                if (playerKeyFerndaleVar != null)
                {
                    playerKeyFerndaleVar.Value = hasKeys;
                }

                if (ferndaleKeysFsm != null && ferndaleKeysFsm.FsmVariables != null)
                {
                    FsmBool keysBool = ferndaleKeysFsm.FsmVariables.FindFsmBool("Keys")
                                    ?? ferndaleKeysFsm.FsmVariables.FindFsmBool("Loan")
                                    ?? ferndaleKeysFsm.FsmVariables.FindFsmBool("Active");
                    if (keysBool != null) keysBool.Value = hasKeys;
                }
            }
            finally
            {
                isSuppressingFerndaleKeys = false;
            }
        }

        private void ApplyDynoState(bool active, float power, string stateName)
        {
            lastDynoActive = active;
            lastDynoPower = power;
            lastDynoState = stateName ?? string.Empty;

            isSuppressingDyno = true;
            try
            {
                if (dynoFsm != null && dynoFsm.FsmVariables != null)
                {
                    SetFirstBool(dynoFsm, active, "Running", "Active", "Test", "Paid");
                    SetFirstFloat(dynoFsm, power, "Power", "HP", "Torque", "PeakPower");
                }
            }
            finally
            {
                isSuppressingDyno = false;
            }
        }

        private void ApplyCatalogOrder(int itemHash, int qty, bool triggerEnvelope)
        {
            lastCatalogHash = itemHash;
            lastCatalogQty = qty;
            hasPendingCatalogOrder = true;
            suppressWalletDetectUntilTime = Time.time + 0.6f;

            isSuppressingCatalog = true;
            try
            {
                if (triggerEnvelope && envelopeFsm != null)
                {
                    envelopeFsm.SendEvent("ORDER");
                    lastEnvelopeState = envelopeFsm.ActiveStateName ?? string.Empty;
                }
                if (playerMoneyVar != null) lastKnownMoney = playerMoneyVar.Value;
            }
            finally
            {
                isSuppressingCatalog = false;
            }
        }

        private void ApplyEnvelopePost(bool ordered, string stateName)
        {
            hasPendingCatalogOrder = ordered;
            lastEnvelopeState = stateName ?? string.Empty;

            isSuppressingCatalog = true;
            try
            {
                if (envelopeFsm != null && envelopeFsm.FsmVariables != null)
                {
                    SetFirstBool(envelopeFsm, ordered, "Ordered", "Order", "Mail", "Post");
                    if (ordered && envelopeFsm.ActiveStateName != stateName && !string.IsNullOrEmpty(stateName) && envelopeFsm.HasState("Order"))
                    {
                        envelopeFsm.SendEvent("ORDER");
                    }
                }
            }
            finally
            {
                isSuppressingCatalog = false;
            }
        }

        private void ApplyN2OState(bool open, float pressure)
        {
            lastN2OOpen = open;
            lastN2OPressure = pressure;

            isSuppressingN2O = true;
            try
            {
                if (n2oValveFsm != null)
                {
                    FsmFloat pVar = n2oValveFsm.FsmVariables.FindFsmFloat("Pressure");
                    if (pVar != null) pVar.Value = pressure;

                    FsmBool openVar = n2oValveFsm.FsmVariables.FindFsmBool("Open")
                                   ?? n2oValveFsm.FsmVariables.FindFsmBool("Valve");
                    if (openVar != null) openVar.Value = open;

                    n2oValveFsm.SendEvent(open ? "OPEN" : "CLOSE");
                }
            }
            finally
            {
                isSuppressingN2O = false;
            }
        }

        private void ApplyInspectionResult(bool passed, byte checkmarks, bool platesInstalled, string plateString, bool triggerResultsFsm)
        {
            hasInspectionResult = true;
            lastInspectionPassed = passed;
            lastInspectionCheckmarks = checkmarks;
            lastPlatesInstalled = platesInstalled;
            if (!string.IsNullOrEmpty(plateString))
            {
                lastPlateString = plateString;
            }

            suppressWalletDetectUntilTime = Time.time + 0.6f;

            isSuppressingInspection = true;
            isSuppressingPlates = true;
            try
            {
                if (inspectionResultsFsm != null && inspectionResultsFsm.FsmVariables != null)
                {
                    FsmBool passVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Pass");
                    if (passVar != null) passVar.Value = passed;

                    SetInspectionFlag(inspectionResultsFsm, "Body", (checkmarks & 1) != 0);
                    SetInspectionFlag(inspectionResultsFsm, "Brakes", (checkmarks & 2) != 0);
                    SetInspectionFlag(inspectionResultsFsm, "Emissions", (checkmarks & 4) != 0);
                    SetInspectionFlag(inspectionResultsFsm, "Exhaust", (checkmarks & 8) != 0);

                    if (triggerResultsFsm)
                    {
                        inspectionResultsFsm.SendEvent(passed ? "PASS" : "FAIL");
                    }
                }

                ApplyPlateNumber(lastPlateString);

                // Если наш модуль владеет RegPlateFront/Rear (не заняты Vehicles.Controls) — применяем FSM-переход
                if (regPlateFrontFsm != null && ctx?.Ownership?.GetOwner(regPlateFrontFsm) == moduleId)
                {
                    regPlateFrontFsm.SendEvent(platesInstalled ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
                }
                if (regPlateRearFsm != null && ctx?.Ownership?.GetOwner(regPlateRearFsm) == moduleId)
                {
                    regPlateRearFsm.SendEvent(platesInstalled ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
                }

                if (playerMoneyVar != null) lastKnownMoney = playerMoneyVar.Value;
            }
            finally
            {
                isSuppressingInspection = false;
                isSuppressingPlates = false;
            }
        }

        private void ApplyFleetariSnapshot(PacketReader reader)
        {
            if (reader.UnreadBytes < 20) return;

            bool activeOrder = reader.ReadBoolean();
            string orderJob = reader.ReadString();
            float orderCost = reader.ReadSingle();
            float jobTimer = reader.ReadSingle();
            bool jobActive = reader.ReadBoolean();
            bool jobCompleted = reader.ReadBoolean();
            string jobState = reader.ReadString();

            bool hasKeys = reader.ReadBoolean();
            string keysState = reader.ReadString();

            bool dynoActive = reader.ReadBoolean();
            float dynoPower = reader.ReadSingle();
            string dynoState = reader.ReadString();

            bool pendingCatalog = reader.ReadBoolean();
            int catHash = reader.ReadInt32();
            int catQty = reader.ReadInt32();
            string envState = reader.ReadString();

            bool n2oOpen = reader.ReadBoolean();
            float n2oPressure = reader.ReadSingle();

            bool hasInsp = reader.ReadBoolean();
            bool inspPassed = reader.ReadBoolean();
            byte inspMarks = reader.ReadByte();
            bool platesInstalled = reader.ReadBoolean();
            string plateStr = reader.ReadString();

            if (activeOrder)
            {
                ApplyFleetariOrder(orderJob, orderCost, triggerOrderFsm: false);
            }
            ApplyFleetariJobStatus(jobTimer, jobActive, jobCompleted, jobState);
            ApplyFerndaleKeys(hasKeys, keysState);
            ApplyDynoState(dynoActive, dynoPower, dynoState);
            if (pendingCatalog)
            {
                ApplyCatalogOrder(catHash, catQty, triggerEnvelope: false);
            }
            ApplyEnvelopePost(pendingCatalog, envState);
            if (n2oValveFsm != null)
            {
                ApplyN2OState(n2oOpen, n2oPressure);
            }
            if (hasInsp || platesInstalled || !string.IsNullOrEmpty(plateStr))
            {
                ApplyInspectionResult(inspPassed, inspMarks, platesInstalled, plateStr, triggerResultsFsm: false);
            }
        }

        #endregion

        #region Variable Helpers

        private void ReadJobStatus(out float timer, out bool active, out bool completed, out string stateName)
        {
            timer = 0f;
            active = false;
            completed = false;
            stateName = string.Empty;

            if (fleetariJobFsm == null) return;
            stateName = fleetariJobFsm.ActiveStateName ?? string.Empty;
            if (fleetariJobFsm.FsmVariables == null) return;

            TryReadFirstFloat(fleetariJobFsm, out timer, "Time", "Timer", "JobTime", "WorkTime", "WaitTime", "OrderTime");
            TryReadFirstBool(fleetariJobFsm, out active, "Job", "Ordered", "Order", "Active");
            TryReadFirstBool(fleetariJobFsm, out completed, "Done", "Complete", "Completed", "Ready");
        }

        private void ReadDynoState(out bool active, out float power, out string stateName)
        {
            active = false;
            power = 0f;
            stateName = string.Empty;

            if (dynoFsm == null) return;
            stateName = dynoFsm.ActiveStateName ?? string.Empty;
            if (dynoFsm.FsmVariables == null) return;

            TryReadFirstBool(dynoFsm, out active, "Running", "Active", "Test", "Paid");
            TryReadFirstFloat(dynoFsm, out power, "Power", "HP", "Torque", "PeakPower");
        }

        private void ReadN2OState(out bool open, out float pressure)
        {
            open = false;
            pressure = 0f;
            if (n2oValveFsm == null || n2oValveFsm.FsmVariables == null) return;

            FsmBool openVar = n2oValveFsm.FsmVariables.FindFsmBool("Open")
                           ?? n2oValveFsm.FsmVariables.FindFsmBool("Valve");
            if (openVar != null)
            {
                open = openVar.Value;
            }
            else if (!string.IsNullOrEmpty(n2oValveFsm.ActiveStateName))
            {
                open = n2oValveFsm.ActiveStateName.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            FsmFloat pVar = n2oValveFsm.FsmVariables.FindFsmFloat("Pressure");
            if (pVar != null) pressure = pVar.Value;
        }

        private static byte ReadInspectionCheckmarks(PlayMakerFSM resultsFsm)
        {
            if (resultsFsm == null || resultsFsm.FsmVariables == null) return 0;
            byte checkmarks = 0;
            if (ReadBoolFlag(resultsFsm, "Body")) checkmarks |= 1;
            if (ReadBoolFlag(resultsFsm, "Brakes")) checkmarks |= 2;
            if (ReadBoolFlag(resultsFsm, "Emissions")) checkmarks |= 4;
            if (ReadBoolFlag(resultsFsm, "Exhaust")) checkmarks |= 8;
            return checkmarks;
        }

        private static bool ReadBoolFlag(PlayMakerFSM fsm, string name)
        {
            FsmBool b = fsm.FsmVariables.FindFsmBool(name);
            return b != null && b.Value;
        }

        private static void SetInspectionFlag(PlayMakerFSM fsm, string name, bool value)
        {
            FsmBool b = fsm.FsmVariables.FindFsmBool(name);
            if (b != null) b.Value = value;
        }

        private bool IsAnyPlateInstalled()
        {
            if (regPlateFrontFsm != null && regPlateFrontFsm.ActiveStateName == "Installed") return true;
            if (regPlateRearFsm != null && regPlateRearFsm.ActiveStateName == "Installed") return true;
            return false;
        }

        private string GetCurrentPlateString()
        {
            RegPlateGen[] gens = satsumaRoot != null
                ? satsumaRoot.GetComponentsInChildren<RegPlateGen>(true)
                : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();

            if (gens != null && gens.Length > 0 && gens[0] != null && !string.IsNullOrEmpty(gens[0].PlateString))
            {
                return gens[0].PlateString;
            }
            return !string.IsNullOrEmpty(lastPlateString) ? lastPlateString : "VBX-403";
        }

        private void ApplyPlateNumber(string plateNum)
        {
            if (string.IsNullOrEmpty(plateNum)) return;

            RegPlateGen[] gens = satsumaRoot != null
                ? satsumaRoot.GetComponentsInChildren<RegPlateGen>(true)
                : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();

            if (gens == null) return;
            for (int i = 0; i < gens.Length; i++)
            {
                RegPlateGen gen = gens[i];
                if (gen == null) continue;
                gen.PlateString = plateNum;
                try
                {
                    if (MiGenerateRegPlate != null)
                    {
                        MiGenerateRegPlate.Invoke(gen, new object[] { plateNum });
                    }
                    else
                    {
                        gen.SendMessage("GenerateRegPlate", plateNum, SendMessageOptions.DontRequireReceiver);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TryReadFirstFloat(PlayMakerFSM fsm, out float val, params string[] names)
        {
            val = 0f;
            for (int i = 0; i < names.Length; i++)
            {
                FsmFloat fv = fsm.FsmVariables.FindFsmFloat(names[i]);
                if (fv != null)
                {
                    val = fv.Value;
                    return;
                }
            }
        }

        private static void SetFirstFloat(PlayMakerFSM fsm, float val, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                FsmFloat fv = fsm.FsmVariables.FindFsmFloat(names[i]);
                if (fv != null)
                {
                    fv.Value = val;
                    return;
                }
            }
        }

        private static void TryReadFirstBool(PlayMakerFSM fsm, out bool val, params string[] names)
        {
            val = false;
            for (int i = 0; i < names.Length; i++)
            {
                FsmBool bv = fsm.FsmVariables.FindFsmBool(names[i]);
                if (bv != null)
                {
                    val = bv.Value;
                    return;
                }
            }
        }

        private static void SetFirstBool(PlayMakerFSM fsm, bool val, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                FsmBool bv = fsm.FsmVariables.FindFsmBool(names[i]);
                if (bv != null)
                {
                    bv.Value = val;
                    return;
                }
            }
        }

        #endregion
    }
}
