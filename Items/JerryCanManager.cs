using System;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Items
{
    /// <summary>
    /// Сетвой менеджер канистр (бензин и дизель) и уровня топлива.
    /// Симметричная P2P-модель с мгновенным локальным откликом без задержек.
    /// Реализовано по архитектуре WreckMP (NetItemsManager).
    /// </summary>
    public class JerryCanManager
    {
        public static JerryCanManager Instance { get; private set; }

        private readonly NetworkTransport transport;

        // FSM канистры бензина
        private PlayMakerFSM gasolineLidFsm;
        private FsmBool gasolineOpenVar;
        private FsmBool gasolineClosedVar;
        private FsmFloat gasolineFluidVar;

        // FSM канистры дизеля
        private PlayMakerFSM dieselLidFsm;
        private FsmBool dieselOpenVar;
        private FsmBool dieselClosedVar;
        private FsmFloat dieselFluidVar;

        // Флаг подавления эхо-пакетов при удаленном применении
        private bool isUpdatingGasoline = false;
        private bool isUpdatingDiesel = false;

        // Таймер периодической синхронизации уровня топлива (только когда крышка открыта)
        private float fluidSyncTimer = 0f;
        private float lastSyncedGas = -1f;
        private float lastSyncedDiesel = -1f;
        private bool isGasolineLidOpen = false;
        private bool isDieselLidOpen = false;

        private bool isInitialized = false;

        public event Action<string> OnLog;

        public void Reset()
        {
            isInitialized = false;
            gasolineLidFsm = null;
            gasolineOpenVar = null;
            gasolineClosedVar = null;
            gasolineFluidVar = null;
            dieselLidFsm = null;
            dieselOpenVar = null;
            dieselClosedVar = null;
            dieselFluidVar = null;
            lastSyncedGas = -1f;
            lastSyncedDiesel = -1f;
        }

        public JerryCanManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeJerryCans()
        {
            if (isInitialized) return;

            try
            {
                Transform items = GameObject.Find("ITEMS")?.transform;

                // 1. Бензин
                Transform gasTrans = items != null ? items.Find("gasoline(itemx)") : null;
                if (gasTrans == null)
                {
                    var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < allGo.Length; i++)
                    {
                        if (allGo[i].name == "gasoline(itemx)")
                        {
                            gasTrans = allGo[i].transform;
                            break;
                        }
                    }
                }

                if (gasTrans != null)
                {
                    Transform gasFluid = gasTrans.Find("FluidTrigger");
                    if (gasFluid != null)
                    {
                        var dataFsm = gasFluid.GetComponent<PlayMakerFSM>();
                        if (dataFsm != null && dataFsm.FsmVariables != null)
                        {
                            gasolineFluidVar = dataFsm.FsmVariables.FindFsmFloat("Fluid");
                        }
                    }

                    Transform gasLid = gasTrans.Find("Open");
                    if (gasLid != null)
                    {
                        SetupJerryCanLidFsm(gasLid, false);
                    }
                }

                // 2. Дизель
                Transform dieselTrans = items != null ? items.Find("diesel(itemx)") : null;
                if (dieselTrans == null)
                {
                    var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < allGo.Length; i++)
                    {
                        if (allGo[i].name == "diesel(itemx)")
                        {
                            dieselTrans = allGo[i].transform;
                            break;
                        }
                    }
                }

                if (dieselTrans != null)
                {
                    Transform dieselFluid = dieselTrans.Find("FluidTrigger");
                    if (dieselFluid != null)
                    {
                        var dataFsm = dieselFluid.GetComponent<PlayMakerFSM>();
                        if (dataFsm != null && dataFsm.FsmVariables != null)
                        {
                            dieselFluidVar = dataFsm.FsmVariables.FindFsmFloat("Fluid");
                        }
                    }

                    Transform dieselLid = dieselTrans.Find("Open");
                    if (dieselLid != null)
                    {
                        SetupJerryCanLidFsm(dieselLid, true);
                    }
                }

                isInitialized = true;
                OnLog?.Invoke("[Канистры] Канистры с бензином и дизелем подключены.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[JerryCanManager] Ошибка инициализации: {ex.Message}");
            }
        }

        private void SetupJerryCanLidFsm(Transform lid, bool isDiesel)
        {
            var playMaker = lid.GetComponent<PlayMakerFSM>();
            if (playMaker == null) return;

            playMaker.Initialize();

            if (isDiesel)
            {
                dieselLidFsm = playMaker;
                dieselOpenVar = playMaker.FsmVariables.FindFsmBool("Open");
                dieselClosedVar = playMaker.FsmVariables.FindFsmBool("Closed");
            }
            else
            {
                gasolineLidFsm = playMaker;
                gasolineOpenVar = playMaker.FsmVariables.FindFsmBool("Open");
                gasolineClosedVar = playMaker.FsmVariables.FindFsmBool("Closed");
            }

            // Перехватываем переход в состояние "State 2" (переключение крышки)
            playMaker.InsertAction("State 2", () => OnLocalLidToggle(isDiesel), 0, false);

            // Добавляем глобальный переход по событию MP_TOGGLE
            FsmEvent mpToggle = playMaker.AddEvent("MP_TOGGLE");
            playMaker.AddGlobalTransition(mpToggle, "State 2");
            playMaker.Initialize();
        }

        private void OnLocalLidToggle(bool isDiesel)
        {
            if (isDiesel)
            {
                if (isUpdatingDiesel)
                {
                    isUpdatingDiesel = false;
                    return;
                }
            }
            else
            {
                if (isUpdatingGasoline)
                {
                    isUpdatingGasoline = false;
                    return;
                }
            }

            if (!transport.IsRunning) return;

            // Локальный игрок открыл/закрыл крышку — определяем новое состояние
            bool isOpen = isDiesel
                ? !(dieselOpenVar != null && dieselOpenVar.Value)
                : !(gasolineOpenVar != null && gasolineOpenVar.Value);

            if (isDiesel) isDieselLidOpen = isOpen;
            else isGasolineLidOpen = isOpen;

            SendJerryCanLid(isDiesel, isOpen);

            // При закрытии крышки также сразу синхронизируем уровень топлива
            if (!isOpen)
            {
                SendFluidSync();
            }

            OnLog?.Invoke($"[Канистры] Крышка {(isDiesel ? "дизеля" : "бензина")} переключена локально -> {(isOpen ? "Открыта" : "Закрыта")}");
        }

        public void Update()
        {
            if (!isInitialized || !transport.IsRunning) return;

            // Опрашиваем уровень топлива только когда хотя бы одна крышка открыта и идёт переливание!
            // Если все крышки закрыты — частота опроса = 0 Гц (0% CPU).
            if (isGasolineLidOpen || isDieselLidOpen)
            {
                fluidSyncTimer -= Time.deltaTime;
                if (fluidSyncTimer <= 0f)
                {
                    fluidSyncTimer = 2.0f; // Во время переливания синхронизируем каждые 2 секунды
                    SendFluidSync();
                }
            }
        }

        private void SendJerryCanLid(bool isDiesel, bool isOpen)
        {
            using (var packet = new PacketWriter(PacketType.JerryCanLid))
            {
                packet.WriteBoolean(isDiesel);
                packet.WriteBoolean(isOpen);
                transport.Send(packet);
            }
        }

        private void SendFluidSync()
        {
            if (!transport.IsRunning) return;

            float gas = gasolineFluidVar != null ? gasolineFluidVar.Value : 0f;
            float diesel = dieselFluidVar != null ? dieselFluidVar.Value : 0f;

            if (Mathf.Abs(gas - lastSyncedGas) < 0.1f && Mathf.Abs(diesel - lastSyncedDiesel) < 0.1f)
            {
                return; // Топливо не переливали — пакет не шлём
            }

            lastSyncedGas = gas;
            lastSyncedDiesel = diesel;

            using (var packet = new PacketWriter(PacketType.JerryCanFluid))
            {
                packet.WriteSingle(gas);
                packet.WriteSingle(diesel);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.JerryCanLid)
            {
                bool isDiesel = reader.ReadBoolean();
                bool isOpen = reader.ReadBoolean();

                if (isDiesel)
                {
                    isDieselLidOpen = isOpen;
                    if (dieselLidFsm == null) return;
                    if (dieselOpenVar != null) dieselOpenVar.Value = !isOpen;
                    if (dieselClosedVar != null) dieselClosedVar.Value = isOpen;

                    isUpdatingDiesel = true;
                    dieselLidFsm.SendEvent("MP_TOGGLE");
                }
                else
                {
                    isGasolineLidOpen = isOpen;
                    if (gasolineLidFsm == null) return;
                    if (gasolineOpenVar != null) gasolineOpenVar.Value = !isOpen;
                    if (gasolineClosedVar != null) gasolineClosedVar.Value = isOpen;

                    isUpdatingGasoline = true;
                    gasolineLidFsm.SendEvent("MP_TOGGLE");
                }

                OnLog?.Invoke($"[Канистры] Удаленный игрок переключил крышку {(isDiesel ? "дизеля" : "бензина")}");
            }
            else if (reader.Type == PacketType.JerryCanFluid)
            {
                float gas = reader.ReadSingle();
                float diesel = reader.ReadSingle();

                if (gasolineFluidVar != null) gasolineFluidVar.Value = gas;
                if (dieselFluidVar != null) dieselFluidVar.Value = diesel;
            }
        }

        private void HandleClientConnected(int clientId)
        {
            // Отправляем текущие состояния новому игроку
            if (gasolineOpenVar != null)
            {
                SendJerryCanLid(false, gasolineOpenVar.Value);
            }
            if (dieselOpenVar != null)
            {
                SendJerryCanLid(true, dieselOpenVar.Value);
            }
            SendFluidSync();
        }
    }
}
