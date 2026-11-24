using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Vehicles
{
    /// <summary>
    /// Менеджер сетевой синхронизации глубоких механик двигателя Сацумы:
    /// 1. Жидкости (моторное масло, охлаждающая жидкость радиатора, тормозная жидкость, сцепление, бензобак).
    /// 2. Кипение тосола и пар из-под пробки (SteamRadiator).
    /// 3. Механический износ и поломки (прогар прокладки ГБЦ, обрыв ремня генератора, разрушение коромысел, поршни).
    /// 4. Давление в шинах и проколы колес.
    /// 5. Полный цикл техосмотра A-Katsastus (инспектор Линделл).
    /// </summary>
    public class TrackedVehicleFuelTank
    {
        public byte VehicleId;
        public string Name;
        public string Path;
        public GameObject TankObject;
        public FsmFloat FuelLevelVar;
        public float LastSentFuel = -999f;
        public float LastSyncTime = 0f;
    }

    public class VehicleEngineSimulationManager : MonoBehaviour
    {
        public static VehicleEngineSimulationManager Instance { get; private set; }

        private NetworkTransport transport;
        public Action<string> OnLog;

        // Баки и топливо для всех автомобилей
        private readonly List<TrackedVehicleFuelTank> vehicleFuelTanks = new List<TrackedVehicleFuelTank>
        {
            new TrackedVehicleFuelTank { VehicleId = 0, Name = "Satsuma", Path = "Database/DatabaseMechanics/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 1, Name = "Hayosiko", Path = "HAYOSIKO(1500kg, 250)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 2, Name = "Ruscko", Path = "RCO_RUSCKO12(270)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 3, Name = "Ferndale", Path = "FERNDALE(1630kg)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 4, Name = "Kekmet", Path = "KEKMET(350-400psi)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 5, Name = "Gifu", Path = "GIFU(750/450psi)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 6, Name = "Jonnez", Path = "JONNEZ ES(Clone)/FuelTank" },
            new TrackedVehicleFuelTank { VehicleId = 8, Name = "Boat", Path = "BOAT/GFX/Motor/Pivot/FuelTank" }
        };

        private float nextFuelScanTime = 0f;
        private float nextFuelSyncCheckTime = 0f;

        // Автомобиль и двигатель
        private GameObject satsumaGo;
        private PlayMakerFSM motorFsm;
        private PlayMakerFSM fanBeltFsm;
        private PlayMakerFSM timingChainFsm;

        // Жидкости
        private PlayMakerFSM oilFsm;
        private PlayMakerFSM oilpanFsm;
        private PlayMakerFSM radiatorFsm;
        private GameObject steamRadiatorObj;
        private FuelTank fuelTank;
        private PlayMakerFSM brakeMasterFsm;
        private PlayMakerFSM clutchMasterFsm;

        // Износ и поломки деталей
        private PlayMakerFSM rockerShaftFsm;
        private PlayMakerFSM headGasketFsm;
        private PlayMakerFSM piston1Fsm;
        private PlayMakerFSM piston2Fsm;
        private PlayMakerFSM piston3Fsm;
        private PlayMakerFSM piston4Fsm;

        // Линия техосмотра (A-Katsastus)
        private PlayMakerFSM inspectionFsm;
        private PlayMakerFSM inspectionResultsFsm;

        // Колеса
        private Wheel[] satsumaWheels;

        // КПП и скрежет синхронизаторов
        private PlayMakerFSM gearGrindingFsm;
        private PlayMakerFSM gearboxDamageFsm;
        private bool isSuppressingGrind = false;
        private bool wasGrinding = false;

        // Номерные знаки (A-Katsastus / RegPlateFront & Rear)
        private PlayMakerFSM regPlateFrontFsm;
        private PlayMakerFSM regPlateRearFsm;
        private bool isSuppressingPlates = false;

        private float nextSyncTime = 0f;
        private float nextScanTime = 0f;
        private bool isSuppressingFluids = false;
        private bool isSuppressingInspection = false;
        private bool isSuppressingWear = false;
        private bool isSuppressingTire = false;

        public void Initialize(NetworkTransport netTransport)
        {
            Instance = this;
            transport = netTransport;
            transport.OnPacketReceived += HandlePacket;
            transport.OnClientConnected += HandleClientConnected;

            ScanEngineObjects();
            ScanVehicleFuelTanks();
            OnLog?.Invoke("[Двигатель/Симуляция] Глубокая симуляция двигателя, жидкостей, поломок и техосмотра активирована");
        }

        public void Reset()
        {
            satsumaGo = null;
            motorFsm = null;
            fanBeltFsm = null;
            timingChainFsm = null;
            oilFsm = null;
            oilpanFsm = null;
            radiatorFsm = null;
            steamRadiatorObj = null;
            fuelTank = null;
            brakeMasterFsm = null;
            clutchMasterFsm = null;
            rockerShaftFsm = null;
            headGasketFsm = null;
            piston1Fsm = null;
            piston2Fsm = null;
            piston3Fsm = null;
            piston4Fsm = null;
            inspectionFsm = null;
            inspectionResultsFsm = null;
            satsumaWheels = null;
            gearGrindingFsm = null;
            gearboxDamageFsm = null;
            isSuppressingGrind = false;
            wasGrinding = false;
            regPlateFrontFsm = null;
            regPlateRearFsm = null;
            isSuppressingPlates = false;
            isSuppressingFluids = false;
            isSuppressingInspection = false;
            isSuppressingWear = false;
            isSuppressingTire = false;

            for (int i = 0; i < vehicleFuelTanks.Count; i++)
            {
                vehicleFuelTanks[i].TankObject = null;
                vehicleFuelTanks[i].FuelLevelVar = null;
                vehicleFuelTanks[i].LastSentFuel = -999f;
                vehicleFuelTanks[i].LastSyncTime = 0f;
            }
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
                transport.OnClientConnected -= HandleClientConnected;
            }
        }

        public void ScanEngineObjects()
        {
            try
            {
                // 1. Поиск Сацумы (поддержка разных версий префабов)
                satsumaGo = GameObject.Find("SATSUMA(557kg, 248)") ??
                            GameObject.Find("SATSUMA(580kg, 87)") ??
                            GameObject.Find("Satsuma");

                if (satsumaGo == null)
                {
                    var allGos = GameObject.FindObjectsOfType<GameObject>();
                    for (int i = 0; i < allGos.Length; i++)
                    {
                        if (allGos[i].name.StartsWith("SATSUMA("))
                        {
                            satsumaGo = allGos[i];
                            break;
                        }
                    }
                }

                // 2. Двигатель Сацумы и ремни
                var motorGo = (satsumaGo != null ? PlayMakerUtilities.FindChildRecursive(satsumaGo.transform, "_Motor")?.gameObject : null) ??
                              GameObject.Find("_Motor");

                if (motorGo != null)
                {
                    motorFsm = PlayMakerUtilities.FindFsmOnGameObject(motorGo, "Start") ??
                               PlayMakerUtilities.FindFsmOnGameObject(motorGo, "Calc RPM") ??
                               motorGo.GetComponent<PlayMakerFSM>();

                    fanBeltFsm = PlayMakerUtilities.FindFsmOnGameObject(motorGo, "Fan Belt");
                    if (fanBeltFsm != null && fanBeltFsm.HasState("No belt"))
                    {
                        fanBeltFsm.InsertAction("No belt", () =>
                        {
                            if (isSuppressingWear) return;
                            SendPartBreakage("FanBelt", 2);
                        }, 0, false);
                    }

                    timingChainFsm = PlayMakerUtilities.FindFsmOnGameObject(motorGo, "Timing Chain");
                }

                // 3. Масло и масляный поддон
                var oilGo = GameObject.Find("Oil") ?? GameObject.Find("Combustion");
                if (oilGo != null)
                {
                    oilFsm = PlayMakerUtilities.FindFsmOnGameObject(oilGo, "Oil") ??
                             PlayMakerUtilities.FindFsmOnGameObject(oilGo, "Pressure") ??
                             PlayMakerUtilities.FindFsmOnGameObject(oilGo, "Cylinders");
                }

                var oilpanGo = GameObject.Find("Oilpan");
                if (oilpanGo != null)
                {
                    oilpanFsm = PlayMakerUtilities.FindFsmOnGameObject(oilpanGo, "Data");
                }

                // 4. Радиатор и кипение тосола
                var radGo = GameObject.Find("Radiator") ??
                            GameObject.Find("racing radiator(Clone)") ??
                            GameObject.Find("radiator(Clone)") ??
                            GameObject.Find("Racing Radiator");
                if (radGo != null)
                {
                    radiatorFsm = PlayMakerUtilities.FindFsmOnGameObject(radGo, "Radiator") ?? radGo.GetComponent<PlayMakerFSM>();
                }
                steamRadiatorObj = GameObject.Find("SteamRadiator");

                // 5. Бензобак
                if (satsumaGo != null)
                {
                    fuelTank = satsumaGo.GetComponentInChildren<FuelTank>();
                }
                if (fuelTank == null)
                {
                    fuelTank = GameObject.FindObjectOfType<FuelTank>();
                }

                // 6. Главные цилиндры сцепления и тормозов
                var clutchGo = GameObject.Find("ClutchMasterCylinder");
                if (clutchGo != null)
                {
                    clutchMasterFsm = PlayMakerUtilities.FindFsmOnGameObject(clutchGo, "Data");
                }

                var brakeGo = GameObject.Find("BrakeMasterCylinder");
                if (brakeGo != null)
                {
                    brakeMasterFsm = PlayMakerUtilities.FindFsmOnGameObject(brakeGo, "Data");
                }

                // 7. Поломки: коромысла, прокладка ГБЦ и поршни
                var rockerGo = GameObject.Find("rocker shaft(Clone)") ?? GameObject.Find("trigger_rocker shaft");
                if (rockerGo != null)
                {
                    rockerShaftFsm = PlayMakerUtilities.FindFsmOnGameObject(rockerGo, "Removal") ?? rockerGo.GetComponent<PlayMakerFSM>();
                    if (rockerShaftFsm != null && rockerShaftFsm.HasState("Break"))
                    {
                        rockerShaftFsm.InsertAction("Break", () =>
                        {
                            if (isSuppressingWear) return;
                            SendPartBreakage("RockerShaft", 4);
                        }, 0, false);
                    }
                }

                var gasketGo = GameObject.Find("head gasket(Clone)") ?? GameObject.Find("trigger_head gasket");
                if (gasketGo != null)
                {
                    headGasketFsm = PlayMakerUtilities.FindFsmOnGameObject(gasketGo, "Removal") ?? gasketGo.GetComponent<PlayMakerFSM>();
                    if (headGasketFsm != null && headGasketFsm.HasState("Death"))
                    {
                        headGasketFsm.InsertAction("Death", () =>
                        {
                            if (isSuppressingWear) return;
                            SendPartBreakage("HeadGasket", 1);
                        }, 0, false);
                    }
                }

                piston1Fsm = HookPiston("piston1(Clone)", 1);
                piston2Fsm = HookPiston("piston2(Clone)", 2);
                piston3Fsm = HookPiston("piston3(Clone)", 3);
                piston4Fsm = HookPiston("piston4(Clone)", 4);

                // 8. Колеса Сацумы
                if (satsumaGo != null)
                {
                    satsumaWheels = satsumaGo.GetComponentsInChildren<Wheel>();
                }

                // 9. Линия техосмотра (A-Katsastus)
                var inspGo = GameObject.Find("InspectionProcess") ?? GameObject.Find("INSPECTION");
                if (inspGo != null)
                {
                    inspectionFsm = PlayMakerUtilities.FindFsmOnGameObject(inspGo, "Inspect") ??
                                    PlayMakerUtilities.FindFsmOnGameObject(inspGo, "Information") ??
                                    inspGo.GetComponent<PlayMakerFSM>();

                    inspectionResultsFsm = PlayMakerUtilities.FindFsmOnGameObject(inspGo, "Results");

                    if (inspectionResultsFsm != null && inspectionResultsFsm.HasState("Results"))
                    {
                        inspectionResultsFsm.InsertAction("Results", () =>
                        {
                            if (isSuppressingInspection) return;
                            var passVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Pass");
                            bool passed = passVar != null && passVar.Value;

                            byte checkmarks = 0;
                            var bVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Body");
                            if (bVar != null && bVar.Value) checkmarks |= 1;
                            var brVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Brakes");
                            if (brVar != null && brVar.Value) checkmarks |= 2;
                            var emVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Emissions");
                            if (emVar != null && emVar.Value) checkmarks |= 4;
                            var exVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Exhaust");
                            if (exVar != null && exVar.Value) checkmarks |= 8;

                            SendInspectionFullResult(passed, checkmarks);
                        }, 0, false);
                    }
                }

                // 10. КПП: скрежет шестерен (GearGrinding) и повреждение коробки (GearboxDamage)
                var grindingGo = (satsumaGo != null ? PlayMakerUtilities.FindChildRecursive(satsumaGo.transform, "GearGrinding")?.gameObject : null) ??
                                 GameObject.Find("GearGrinding");
                if (grindingGo != null)
                {
                    gearGrindingFsm = PlayMakerUtilities.FindFsmOnGameObject(grindingGo, "Grinding");
                    if (gearGrindingFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(gearGrindingFsm, "MP_SYNC_GRIND", "State 1");
                        PlayMakerUtilities.AddSafeGlobalTransition(gearGrindingFsm, "MP_SYNC_STOP_GRIND", "State 2");

                        if (gearGrindingFsm.HasState("State 1"))
                        {
                            gearGrindingFsm.InsertAction("State 1", () =>
                            {
                                if (isSuppressingGrind) return;
                                wasGrinding = true;
                                SendGearGrindingSync(true);
                            }, 0, false);
                        }

                        if (gearGrindingFsm.HasState("State 2"))
                        {
                            gearGrindingFsm.InsertAction("State 2", () =>
                            {
                                if (isSuppressingGrind || !wasGrinding) return;
                                wasGrinding = false;
                                SendGearGrindingSync(false);
                            }, 0, false);
                        }
                    }
                }

                var gearboxDmgGo = (satsumaGo != null ? PlayMakerUtilities.FindChildRecursive(satsumaGo.transform, "GearboxDamage")?.gameObject : null) ??
                                   GameObject.Find("GearboxDamage");
                if (gearboxDmgGo != null)
                {
                    gearboxDamageFsm = PlayMakerUtilities.FindFsmOnGameObject(gearboxDmgGo, "Damage");
                }

                // 11. Регистрационные номерные знаки (RegPlateFront & RegPlateRear)
                var plateFrontGo = (satsumaGo != null ? PlayMakerUtilities.FindChildRecursive(satsumaGo.transform, "RegPlateFront")?.gameObject : null) ??
                                   GameObject.Find("RegPlateFront");
                if (plateFrontGo != null)
                {
                    regPlateFrontFsm = PlayMakerUtilities.FindFsmOnGameObject(plateFrontGo, "Data");
                    if (regPlateFrontFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(regPlateFrontFsm, "MP_SYNC_INSTALL", "Installed");
                        PlayMakerUtilities.AddSafeGlobalTransition(regPlateFrontFsm, "MP_SYNC_DISABLE", "Disable plate");

                        if (regPlateFrontFsm.HasState("Installed"))
                        {
                            regPlateFrontFsm.InsertAction("Installed", () =>
                            {
                                if (isSuppressingPlates) return;
                                SendLicensePlatesSync(true, GetCurrentPlateString());
                            }, 0, false);
                        }

                        if (regPlateFrontFsm.HasState("Disable plate"))
                        {
                            regPlateFrontFsm.InsertAction("Disable plate", () =>
                            {
                                if (isSuppressingPlates) return;
                                SendLicensePlatesSync(false, GetCurrentPlateString());
                            }, 0, false);
                        }
                    }
                }

                var plateRearGo = (satsumaGo != null ? PlayMakerUtilities.FindChildRecursive(satsumaGo.transform, "RegPlateRear")?.gameObject : null) ??
                                  GameObject.Find("RegPlateRear");
                if (plateRearGo != null)
                {
                    regPlateRearFsm = PlayMakerUtilities.FindFsmOnGameObject(plateRearGo, "Data");
                    if (regPlateRearFsm != null)
                    {
                        PlayMakerUtilities.AddSafeGlobalTransition(regPlateRearFsm, "MP_SYNC_INSTALL", "Installed");
                        PlayMakerUtilities.AddSafeGlobalTransition(regPlateRearFsm, "MP_SYNC_DISABLE", "Disable plate");

                        if (regPlateRearFsm.HasState("Installed"))
                        {
                            regPlateRearFsm.InsertAction("Installed", () =>
                            {
                                if (isSuppressingPlates) return;
                                SendLicensePlatesSync(true, GetCurrentPlateString());
                            }, 0, false);
                        }

                        if (regPlateRearFsm.HasState("Disable plate"))
                        {
                            regPlateRearFsm.InsertAction("Disable plate", () =>
                            {
                                if (isSuppressingPlates) return;
                                SendLicensePlatesSync(false, GetCurrentPlateString());
                            }, 0, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Двигатель/Симуляция] Ошибка сканирования: {ex.Message}");
            }
        }

        private PlayMakerFSM HookPiston(string goName, int pistonIndex)
        {
            var pGo = GameObject.Find(goName) ?? GameObject.Find($"trigger_piston{pistonIndex}");
            if (pGo != null)
            {
                var fsm = PlayMakerUtilities.FindFsmOnGameObject(pGo, "Removal") ??
                          PlayMakerUtilities.FindFsmOnGameObject(pGo, "Collision") ??
                          pGo.GetComponent<PlayMakerFSM>();
                if (fsm != null && fsm.HasState("Death"))
                {
                    fsm.InsertAction("Death", () =>
                    {
                        if (isSuppressingWear) return;
                        SendPartBreakage($"Piston{pistonIndex}", 3);
                    }, 0, false);
                }
                return fsm;
            }
            return null;
        }

        private void Update()
        {
            if (transport == null || !transport.IsRunning) return;

            UpdateVehicleFuelSync();

            if (Time.time > nextScanTime)
            {
                nextScanTime = Time.time + 10f;
                if (satsumaGo == null || inspectionResultsFsm == null || fuelTank == null)
                {
                    ScanEngineObjects();
                }
            }

            // Периодическая сетевая синхронизация жидкостей (строго хост является авторитетом)
            // При заливке жидкостей или работающем моторе передаем раз в 0.5-2.5 сек
            if (transport.IsServer && Time.time > nextSyncTime)
            {
                nextSyncTime = Time.time + 0.5f;
                SyncLocalFluids();
            }
        }

        #region Vehicle Fuel Synchronization (All Vehicles / Gas Station & CheatBox)

        public void ScanVehicleFuelTanks()
        {
            for (int i = 0; i < vehicleFuelTanks.Count; i++)
            {
                var tank = vehicleFuelTanks[i];
                if (tank.TankObject == null || tank.FuelLevelVar == null)
                {
                    var go = FindFuelTankObject(tank);
                    if (go != null)
                    {
                        tank.TankObject = go;
                        var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                        for (int f = 0; f < fsms.Length; f++)
                        {
                            var fsmVar = fsms[f].FsmVariables.FindFsmFloat("FuelLevel");
                            if (fsmVar != null)
                            {
                                tank.FuelLevelVar = fsmVar;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private GameObject FindFuelTankObject(TrackedVehicleFuelTank tank)
        {
            GameObject go = GameObject.Find(tank.Path);
            if (go != null) return go;

            switch (tank.VehicleId)
            {
                case 0:
                    var db = GameObject.Find("Database");
                    if (db != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(db.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 1:
                    var hayo = GameObject.Find("HAYOSIKO(1500kg, 250)") ?? GameObject.Find("Hayosiko");
                    if (hayo != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(hayo.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 2:
                    var rco = GameObject.Find("RCO_RUSCKO12(270)") ?? GameObject.Find("Ruscko");
                    if (rco != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(rco.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 3:
                    var fern = GameObject.Find("FERNDALE(1630kg)") ?? GameObject.Find("Ferndale");
                    if (fern != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(fern.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 4:
                    var kek = GameObject.Find("KEKMET(350-400psi)") ?? GameObject.Find("Kekmet");
                    if (kek != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(kek.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 5:
                    var gifu = GameObject.Find("GIFU(750/450psi)") ?? GameObject.Find("Gifu");
                    if (gifu != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(gifu.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 6:
                    var jon = GameObject.Find("JONNEZ ES(Clone)") ?? GameObject.Find("JONNEZ ES");
                    if (jon != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(jon.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
                case 8:
                    var boat = GameObject.Find("BOAT");
                    if (boat != null)
                    {
                        var t = PlayMakerUtilities.FindChildRecursive(boat.transform, "FuelTank");
                        if (t != null) return t.gameObject;
                    }
                    break;
            }
            return null;
        }

        private float GetTankFuel(TrackedVehicleFuelTank entry)
        {
            if (entry.FuelLevelVar != null)
            {
                return entry.FuelLevelVar.Value;
            }
            if (entry.VehicleId == 0 && fuelTank != null)
            {
                return fuelTank._currentFuel;
            }
            return -1f;
        }

        private void SetTankFuel(TrackedVehicleFuelTank entry, float fuelLevel)
        {
            if (entry.FuelLevelVar != null)
            {
                entry.FuelLevelVar.Value = fuelLevel;
            }
            if (entry.VehicleId == 0 && fuelTank != null)
            {
                fuelTank._currentFuel = fuelLevel;
            }
        }

        private void UpdateVehicleFuelSync()
        {
            if (transport == null || !transport.IsRunning) return;

            if (Time.time >= nextFuelScanTime)
            {
                nextFuelScanTime = Time.time + 5.0f;
                ScanVehicleFuelTanks();
            }

            if (Time.time < nextFuelSyncCheckTime) return;
            nextFuelSyncCheckTime = Time.time + 0.25f;

            float now = Time.time;
            for (int i = 0; i < vehicleFuelTanks.Count; i++)
            {
                var entry = vehicleFuelTanks[i];
                float currentFuel = GetTankFuel(entry);
                if (currentFuel < 0f) continue;

                // Инициализация при первом обнаружении
                if (entry.LastSentFuel < -900f)
                {
                    entry.LastSentFuel = currentFuel;
                    entry.LastSyncTime = now;
                    continue;
                }

                float delta = Mathf.Abs(currentFuel - entry.LastSentFuel);

                // 1. Быстрое пополнение (заправка на АЗС, CheatBox Fill/Empty) -> дельта > 0.5L
                // 2. Периодическая фоновая синхронизация (расход топлива хостом) -> каждые 2 сек при дельте > 0.05L
                bool shouldSync = delta > 0.5f || (transport.IsServer && now - entry.LastSyncTime >= 2.0f && delta > 0.05f);

                if (shouldSync)
                {
                    entry.LastSentFuel = currentFuel;
                    entry.LastSyncTime = now;
                    SendVehicleFuel(entry.VehicleId, currentFuel);
                }
            }
        }

        public void SendVehicleFuel(byte vehicleId, float fuelLevel)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleFuelSync))
            {
                packet.WriteByte(vehicleId);
                packet.WriteSingle(fuelLevel);
                transport.Send(packet);
            }
        }

        private void HandleVehicleFuelSync(PacketReader reader, int connectionId)
        {
            byte vehicleId = reader.ReadByte();
            float fuelLevel = reader.ReadSingle();

            TrackedVehicleFuelTank entry = null;
            for (int i = 0; i < vehicleFuelTanks.Count; i++)
            {
                if (vehicleFuelTanks[i].VehicleId == vehicleId)
                {
                    entry = vehicleFuelTanks[i];
                    break;
                }
            }

            if (entry != null)
            {
                if (entry.FuelLevelVar == null)
                {
                    ScanVehicleFuelTanks();
                }

                SetTankFuel(entry, fuelLevel);
                entry.LastSentFuel = fuelLevel;
                entry.LastSyncTime = Time.time;
                OnLog?.Invoke($"[Топливо] Обновлен уровень топлива {entry.Name}: {fuelLevel:F1}L");
            }

            // Реле пакета сервером остальным клиентам
            if (transport.IsServer)
            {
                using (var packet = new PacketWriter(PacketType.VehicleFuelSync))
                {
                    packet.WriteByte(vehicleId);
                    packet.WriteSingle(fuelLevel);
                    transport.Send(packet, connectionId);
                }
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (!transport.IsServer) return;

            ScanVehicleFuelTanks();
            for (int i = 0; i < vehicleFuelTanks.Count; i++)
            {
                var entry = vehicleFuelTanks[i];
                float fuel = GetTankFuel(entry);
                if (fuel >= 0f)
                {
                    using (var packet = new PacketWriter(PacketType.VehicleFuelSync))
                    {
                        packet.WriteByte(entry.VehicleId);
                        packet.WriteSingle(fuel);
                        transport.SendTo(packet, clientId);
                    }
                }
            }
        }

        #endregion

        private void SyncLocalFluids()
        {
            if (isSuppressingFluids) return;

            float oil = 1f;
            float coolant = 1f;
            float fuel = fuelTank != null ? fuelTank._currentFuel : 10f;
            float brakeFluid = 1f;
            float clutchFluid = 1f;
            bool isBoiling = steamRadiatorObj != null && steamRadiatorObj.activeInHierarchy;

            if (oilFsm != null)
            {
                var oVar = oilFsm.FsmVariables.FindFsmFloat("Oil");
                if (oVar != null) oil = oVar.Value;
            }
            else if (oilpanFsm != null)
            {
                var oVar = oilpanFsm.FsmVariables.FindFsmFloat("Oil");
                if (oVar != null) oil = oVar.Value;
            }

            if (radiatorFsm != null)
            {
                var cVar = radiatorFsm.FsmVariables.FindFsmFloat("Coolant");
                if (cVar != null) coolant = cVar.Value;
            }

            if (brakeMasterFsm != null)
            {
                var bVar = brakeMasterFsm.FsmVariables.FindFsmFloat("BrakeFluid");
                if (bVar != null) brakeFluid = bVar.Value;
            }

            if (clutchMasterFsm != null)
            {
                var clVar = clutchMasterFsm.FsmVariables.FindFsmFloat("ClutchFluid");
                if (clVar != null) clutchFluid = clVar.Value;
            }

            SendEngineFluids(oil, coolant, fuel, brakeFluid, clutchFluid, isBoiling);
        }

        public void SendEngineFluids(float oilLevel, float coolantLevel, float fuelLevel, float brakeLevel, float clutchLevel, bool isBoiling)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleFluidSync))
            {
                packet.WriteSingle(oilLevel);
                packet.WriteSingle(coolantLevel);
                packet.WriteSingle(fuelLevel);
                packet.WriteSingle(brakeLevel);
                packet.WriteSingle(clutchLevel);
                packet.WriteBoolean(isBoiling);
                transport.Send(packet);
            }
        }

        public void SendPartBreakage(string partName, byte breakageType)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleWearSync))
            {
                packet.WriteString(partName);
                packet.WriteByte(breakageType); // 1 = Gasket blow, 2 = Belt snap, 3 = Piston blow, 4 = Rocker broken
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Двигатель] Поломка детали: {partName} (тип: {breakageType})");
        }

        public void SendTireState(int wheelIndex, float pressure, bool isPunctured)
        {
            if (isSuppressingTire || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.TirePressureSync))
            {
                packet.WriteInt32(wheelIndex);
                packet.WriteSingle(pressure);
                packet.WriteBoolean(isPunctured);
                transport.Send(packet);
            }
        }

        public void SendInspectionFullResult(bool passed, byte checkmarks)
        {
            if (transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.VehicleInspectionSync))
            {
                packet.WriteBoolean(passed);
                packet.WriteByte(checkmarks);
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Техосмотр] Результаты инспекции: {(passed ? "ПРОЙДЕН!" : "НЕ ПРОЙДЕН!")} (отметки: {checkmarks})");
        }

        public void SendGearGrindingSync(bool isGrinding)
        {
            if (isSuppressingGrind || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.GearboxGrindSync))
            {
                packet.WriteBoolean(isGrinding);
                transport.Send(packet);
            }
        }

        public void SendLicensePlatesSync(bool installed, string plateNumber)
        {
            if (isSuppressingPlates || transport == null || !transport.IsRunning) return;

            using (var packet = new PacketWriter(PacketType.LicensePlatesSync))
            {
                packet.WriteBoolean(installed);
                packet.WriteString(plateNumber ?? "VBX-403");
                transport.Send(packet);
            }
            OnLog?.Invoke($"[Номера] Передача состояния номерных знаков: '{plateNumber}' (установлены: {installed})");
        }

        public string GetCurrentPlateString()
        {
            var plateGens = satsumaGo != null ? satsumaGo.GetComponentsInChildren<RegPlateGen>(true) : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();
            if (plateGens != null && plateGens.Length > 0 && plateGens[0] != null)
            {
                return plateGens[0].PlateString ?? "VBX-403";
            }
            return "VBX-403";
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.VehicleFuelSync:
                    HandleVehicleFuelSync(reader, connectionId);
                    break;

                case PacketType.VehicleFluidSync:
                    HandleFluidsSync(reader);
                    break;

                case PacketType.VehicleWearSync:
                    HandleWearSync(reader);
                    break;

                case PacketType.TirePressureSync:
                    HandleTireSync(reader);
                    break;

                case PacketType.VehicleInspectionSync:
                    HandleInspectionSync(reader);
                    break;

                case PacketType.GearboxGrindSync:
                    HandleGearGrindSync(reader);
                    break;

                case PacketType.LicensePlatesSync:
                    HandleLicensePlatesSync(reader);
                    break;
            }
        }

        private void HandleFluidsSync(PacketReader reader)
        {
            float oil = reader.ReadSingle();
            float coolant = reader.ReadSingle();
            float fuel = reader.ReadSingle();
            float brakeFluid = reader.ReadSingle();
            float clutchFluid = reader.ReadSingle();
            bool isBoiling = reader.ReadBoolean();

            isSuppressingFluids = true;
            try
            {
                if (fuelTank != null)
                {
                    fuelTank._currentFuel = fuel;
                }

                if (radiatorFsm != null)
                {
                    var cVar = radiatorFsm.FsmVariables.FindFsmFloat("Coolant");
                    if (cVar != null) cVar.Value = coolant;
                }

                if (oilFsm != null)
                {
                    var oVar = oilFsm.FsmVariables.FindFsmFloat("Oil");
                    if (oVar != null) oVar.Value = oil;
                }
                if (oilpanFsm != null)
                {
                    var oVar = oilpanFsm.FsmVariables.FindFsmFloat("Oil");
                    if (oVar != null) oVar.Value = oil;
                }

                if (brakeMasterFsm != null)
                {
                    var bVar = brakeMasterFsm.FsmVariables.FindFsmFloat("BrakeFluid");
                    if (bVar != null) bVar.Value = brakeFluid;
                }

                if (clutchMasterFsm != null)
                {
                    var clVar = clutchMasterFsm.FsmVariables.FindFsmFloat("ClutchFluid");
                    if (clVar != null) clVar.Value = clutchFluid;
                }

                if (steamRadiatorObj != null)
                {
                    steamRadiatorObj.SetActive(isBoiling);
                }
            }
            finally
            {
                isSuppressingFluids = false;
            }
        }

        private void HandleWearSync(PacketReader reader)
        {
            string partName = reader.ReadString();
            byte breakType = reader.ReadByte();

            isSuppressingWear = true;
            try
            {
                if (breakType == 1 && headGasketFsm != null)
                {
                    headGasketFsm.SendEvent("DEATH");
                    OnLog?.Invoke("[Двигатель] Прогар прокладки ГБЦ! Густой белый дым из глушителя.");
                }
                else if (breakType == 2 && fanBeltFsm != null)
                {
                    fanBeltFsm.SendEvent("SNAP");
                    OnLog?.Invoke("[Двигатель] Ремень генератора оборван со свистом!");
                }
                else if (breakType == 3)
                {
                    if (partName.Contains("1") && piston1Fsm != null) piston1Fsm.SendEvent("DEATH");
                    else if (partName.Contains("2") && piston2Fsm != null) piston2Fsm.SendEvent("DEATH");
                    else if (partName.Contains("3") && piston3Fsm != null) piston3Fsm.SendEvent("DEATH");
                    else if (partName.Contains("4") && piston4Fsm != null) piston4Fsm.SendEvent("DEATH");
                    OnLog?.Invoke($"[Двигатель] Взрыв поршня ({partName}) и разрушение блока цилиндров!");
                }
                else if (breakType == 4 && rockerShaftFsm != null)
                {
                    rockerShaftFsm.SendEvent("BREAK");
                    OnLog?.Invoke("[Двигатель] Коромысло клапана разрушено! Стук в ГБЦ.");
                }
            }
            finally
            {
                isSuppressingWear = false;
            }
        }

        private void HandleTireSync(PacketReader reader)
        {
            int wheelIdx = reader.ReadInt32();
            float pressure = reader.ReadSingle();
            bool punctured = reader.ReadBoolean();

            isSuppressingTire = true;
            try
            {
                if (satsumaWheels != null && wheelIdx >= 0 && wheelIdx < satsumaWheels.Length)
                {
                    // Отражение физического состояния колеса
                    if (punctured)
                    {
                        var collider = satsumaWheels[wheelIdx].GetComponent<WheelCollider>();
                        if (collider != null)
                        {
                            collider.radius = 0.22f; // Спущенное колесо
                        }
                    }
                }
            }
            finally
            {
                isSuppressingTire = false;
            }

            if (punctured)
            {
                OnLog?.Invoke($"[Колеса] Прокол и разрыв покрышки колеса #{wheelIdx + 1}!");
            }
        }

        private void HandleInspectionSync(PacketReader reader)
        {
            bool passed = reader.ReadBoolean();
            byte checkmarks = reader.ReadByte();

            isSuppressingInspection = true;
            try
            {
                if (inspectionResultsFsm != null)
                {
                    var passVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Pass");
                    if (passVar != null) passVar.Value = passed;

                    var bVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Body");
                    if (bVar != null) bVar.Value = (checkmarks & 1) != 0;
                    var brVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Brakes");
                    if (brVar != null) brVar.Value = (checkmarks & 2) != 0;
                    var emVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Emissions");
                    if (emVar != null) emVar.Value = (checkmarks & 4) != 0;
                    var exVar = inspectionResultsFsm.FsmVariables.FindFsmBool("Exhaust");
                    if (exVar != null) exVar.Value = (checkmarks & 8) != 0;

                    inspectionResultsFsm.SendEvent(passed ? "PASS" : "FAIL");
                }
            }
            finally
            {
                isSuppressingInspection = false;
            }

            OnLog?.Invoke($"[Техосмотр] Инспектор Линделл: Сацума {(passed ? "УСПЕШНО прошла техосмотр!" : "провалила техосмотр!")}");
        }

        private void HandleGearGrindSync(PacketReader reader)
        {
            bool isGrinding = reader.ReadBoolean();
            isSuppressingGrind = true;
            try
            {
                if (gearGrindingFsm != null)
                {
                    gearGrindingFsm.SendEvent(isGrinding ? "MP_SYNC_GRIND" : "MP_SYNC_STOP_GRIND");
                }
            }
            finally
            {
                isSuppressingGrind = false;
            }

            if (isGrinding)
            {
                OnLog?.Invoke("[КПП] Скрежет шестерен в коробке передач Сацумы!");
            }
        }

        private void HandleLicensePlatesSync(PacketReader reader)
        {
            bool installed = reader.ReadBoolean();
            string plateNumber = reader.ReadString();

            isSuppressingPlates = true;
            try
            {
                ApplyPlateNumber(plateNumber);

                if (regPlateFrontFsm != null)
                {
                    regPlateFrontFsm.SendEvent(installed ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
                }
                if (regPlateRearFsm != null)
                {
                    regPlateRearFsm.SendEvent(installed ? "MP_SYNC_INSTALL" : "MP_SYNC_DISABLE");
                }
            }
            finally
            {
                isSuppressingPlates = false;
            }

            OnLog?.Invoke($"[Номера] Синхронизированы номерные знаки Сацумы: '{plateNumber}' (Установлены: {installed})");
        }

        private void ApplyPlateNumber(string plateNum)
        {
            if (string.IsNullOrEmpty(plateNum)) return;

            var plateGens = satsumaGo != null ? satsumaGo.GetComponentsInChildren<RegPlateGen>(true) : UnityEngine.Object.FindObjectsOfType<RegPlateGen>();
            if (plateGens != null)
            {
                for (int i = 0; i < plateGens.Length; i++)
                {
                    var gen = plateGens[i];
                    if (gen != null)
                    {
                        gen.PlateString = plateNum;
                        try
                        {
                            var method = typeof(RegPlateGen).GetMethod("GenerateRegPlate",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                                null, new Type[] { typeof(string) }, null);
                            if (method != null)
                            {
                                method.Invoke(gen, new object[] { plateNum });
                            }
                            else
                            {
                                gen.SendMessage("GenerateRegPlate", plateNum, SendMessageOptions.DontRequireReceiver);
                            }
                        }
                        catch { }
                    }
                }
            }
        }
    }
}
