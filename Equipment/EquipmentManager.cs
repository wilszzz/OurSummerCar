using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Equipment
{
    /// <summary>
    /// Сетевой менеджер тяжелого гаражного оборудования и инвентаря:
    /// 1. Гидравлический подкатной домкрат (Floor Jack): рычаг подъема, винт сброса давления, высота подъема Y.
    /// 2. Кран-гусь для снятия/установки двигателя (Motor Hoist): угол стрелы (Angle), цепи, подъем/опускание.
    /// 3. Керосиновая лампа (Lantern): выключатель огня, фитиль.
    /// </summary>
    public class EquipmentManager
    {
        public static EquipmentManager Instance { get; private set; }

        private readonly NetworkTransport transport;

        // Домкрат (Floor Jack)
        private Transform jackTr;
        private Rigidbody jackRb;
        private PlayMakerFSM floorJackFsm;
        private FsmFloat floorJackY;
        private bool isSuppressingJack = false;

        private Vector3 lastSentJackPos;
        private Quaternion lastSentJackRot = Quaternion.identity;
        private Vector3 targetJackPos;
        private Quaternion targetJackRot = Quaternion.identity;
        private float lastNetworkUpdateJack = 0f;
        private float lastLocalMoveJack = 0f;
        private bool hasReceivedJackTransform = false;

        // Кран для двигателя (Motor Hoist)
        private Transform hoistTr;
        private Rigidbody hoistRb;
        private PlayMakerFSM motorHoistFsm;
        private FsmFloat motorHoistAngle;
        private Transform motorHoistArm;
        private bool isSuppressingHoist = false;

        private Vector3 lastSentHoistPos;
        private Quaternion lastSentHoistRot = Quaternion.identity;
        private Vector3 targetHoistPos;
        private Quaternion targetHoistRot = Quaternion.identity;
        private float lastNetworkUpdateHoist = 0f;
        private float lastLocalMoveHoist = 0f;
        private bool hasReceivedHoistTransform = false;

        private float transformCheckTimer = 0f;

        // Керосиновая лампа
        private readonly List<PlayMakerFSM> lanterns = new List<PlayMakerFSM>();
        private bool isSuppressingLantern = false;

        private bool isInitialized = false;
        public event Action<string> OnLog;

        public void Reset()
        {
            isInitialized = false;
            jackTr = null;
            jackRb = null;
            floorJackFsm = null;
            floorJackY = null;
            hasReceivedJackTransform = false;

            hoistTr = null;
            hoistRb = null;
            motorHoistFsm = null;
            motorHoistAngle = null;
            motorHoistArm = null;
            hasReceivedHoistTransform = false;

            lanterns.Clear();
        }

        public EquipmentManager(NetworkTransport transport)
        {
            Instance = this;
            this.transport = transport;
            this.transport.OnPacketReceived += HandlePacket;
            this.transport.OnClientConnected += HandleClientConnected;
        }

        public void InitializeEquipment()
        {
            if (isInitialized) return;

            try
            {
                // 1. Поиск и инициализация домкрата
                InitFloorJack();

                // 2. Поиск и инициализация крана для двигателя
                InitMotorHoist();

                // 3. Поиск и инициализация фонарей
                InitLanterns();

                isInitialized = true;
                OnLog?.Invoke("[Оборудование] Домкрат, кран двигателя и фонари успешно синхронизированы.");
            }
            catch (Exception ex)
            {
                ModConsole.Print($"[EquipmentManager Init Error] {ex.Message}");
            }
        }

        private void InitFloorJack()
        {
            jackTr = GameObject.Find("ITEMS")?.transform.Find("floor jack(itemx)") ?? GameObject.Find("floor jack(itemx)")?.transform;
            if (jackTr != null)
            {
                jackRb = jackTr.GetComponent<Rigidbody>();
                lastSentJackPos = targetJackPos = jackTr.position;
                lastSentJackRot = targetJackRot = jackTr.rotation;

                Transform trigger = jackTr.Find("Trigger");
                if (trigger != null)
                {
                    floorJackFsm = trigger.GetComponent<PlayMakerFSM>();
                    if (floorJackFsm != null)
                    {
                        floorJackFsm.Initialize();
                        floorJackY = floorJackFsm.FsmVariables.FindFsmFloat("Y");

                        var mpLiftUp = floorJackFsm.AddEvent("MP_LIFT_UP");
                        floorJackFsm.AddGlobalTransition(mpLiftUp, "Up");
                        var mpLiftDown = floorJackFsm.AddEvent("MP_LIFT_DOWN");
                        floorJackFsm.AddGlobalTransition(mpLiftDown, "Down");

                        floorJackFsm.InsertAction("Up", () =>
                        {
                            if (isSuppressingJack) { isSuppressingJack = false; return; }
                            float yVal = floorJackY != null ? floorJackY.Value : 0f;
                            SendFloorJack(true, yVal);
                            OnLog?.Invoke($"[Домкрат] Подъем (высота: {yVal:F2})");
                        }, 0, false);

                        floorJackFsm.InsertAction("Down", () =>
                        {
                            if (isSuppressingJack) { isSuppressingJack = false; return; }
                            SendFloorJack(false, 0f);
                            OnLog?.Invoke("[Домкрат] Сброс давления / спуск");
                        }, 0, false);
                    }
                }
            }
        }

        private void InitMotorHoist()
        {
            hoistTr = GameObject.Find("ITEMS")?.transform.Find("motor hoist(itemx)") ?? GameObject.Find("motor hoist(itemx)")?.transform;
            if (hoistTr != null)
            {
                hoistRb = hoistTr.GetComponent<Rigidbody>();
                lastSentHoistPos = targetHoistPos = hoistTr.position;
                lastSentHoistRot = targetHoistRot = hoistTr.rotation;

                motorHoistArm = hoistTr.Find("motorhoist_arm");
                Transform trigger = hoistTr.Find("Pump/Trigger");
                if (trigger != null)
                {
                    motorHoistFsm = trigger.GetComponent<PlayMakerFSM>();
                    if (motorHoistFsm != null)
                    {
                        motorHoistFsm.Initialize();
                        motorHoistAngle = motorHoistFsm.FsmVariables.FindFsmFloat("Angle");

                        var mpUp = motorHoistFsm.AddEvent("MP_HOIST_UP");
                        motorHoistFsm.AddGlobalTransition(mpUp, "Up");
                        var mpDown = motorHoistFsm.AddEvent("MP_HOIST_DOWN");
                        motorHoistFsm.AddGlobalTransition(mpDown, "Down");

                        motorHoistFsm.InsertAction("Up", () =>
                        {
                            if (isSuppressingHoist) { isSuppressingHoist = false; return; }
                            float angle = motorHoistAngle != null ? motorHoistAngle.Value : 0f;
                            SendMotorHoist(true, angle);
                            OnLog?.Invoke($"[Кран двигателя] Подъем стрелы ({angle:F1}°)");
                        }, -1, false);

                        motorHoistFsm.InsertAction("Down", () =>
                        {
                            if (isSuppressingHoist) { isSuppressingHoist = false; return; }
                            float angle = motorHoistAngle != null ? motorHoistAngle.Value : 0f;
                            SendMotorHoist(false, angle);
                            OnLog?.Invoke($"[Кран двигателя] Опускание стрелы ({angle:F1}°)");
                        }, -1, false);
                    }
                }
            }
        }

        private void InitLanterns()
        {
            lanterns.Clear();
            var allGo = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGo.Length; i++)
            {
                if (allGo[i].name.StartsWith("lantern"))
                {
                    var fsm = allGo[i].GetComponent<PlayMakerFSM>();
                    if (fsm != null && fsm.FsmName == "Use")
                    {
                        fsm.Initialize();
                        int idx = lanterns.Count;
                        lanterns.Add(fsm);

                        var mpToggle = fsm.AddEvent("MP_TOGGLE");
                        fsm.AddGlobalTransition(mpToggle, "State 1");

                        fsm.InsertAction("State 1", () =>
                        {
                            if (isSuppressingLantern) { isSuppressingLantern = false; return; }
                            SendLanternToggle((byte)idx);
                        }, 0, false);
                    }
                }
            }
        }

        public void Update()
        {
            if (!isInitialized || !transport.IsRunning) return;

            // Динамический поиск объектов, если при загрузке они еще не появились на сцене
            if (jackTr == null)
            {
                jackTr = GameObject.Find("ITEMS")?.transform.Find("floor jack(itemx)") ?? GameObject.Find("floor jack(itemx)")?.transform;
                if (jackTr != null)
                {
                    jackRb = jackTr.GetComponent<Rigidbody>();
                    lastSentJackPos = targetJackPos = jackTr.position;
                    lastSentJackRot = targetJackRot = jackTr.rotation;
                }
            }

            if (hoistTr == null)
            {
                hoistTr = GameObject.Find("ITEMS")?.transform.Find("motor hoist(itemx)") ?? GameObject.Find("motor hoist(itemx)")?.transform;
                if (hoistTr != null)
                {
                    hoistRb = hoistTr.GetComponent<Rigidbody>();
                    lastSentHoistPos = targetHoistPos = hoistTr.position;
                    lastSentHoistRot = targetHoistRot = hoistTr.rotation;
                }
            }

            float dt = Time.deltaTime;
            transformCheckTimer += dt;

            // Проверка физического перемещения и отправка сетевых координат (20 раз в сек)
            if (transformCheckTimer >= 0.05f)
            {
                transformCheckTimer = 0f;

                // 1. Домкрат (Floor Jack)
                if (jackTr != null)
                {
                    bool isRemotelyUpdating = (Time.time - lastNetworkUpdateJack < 1.0f);
                    if (!isRemotelyUpdating)
                    {
                        float posDelta = Vector3.Distance(jackTr.position, lastSentJackPos);
                        float rotDelta = Quaternion.Angle(jackTr.rotation, lastSentJackRot);

                        if (posDelta > 0.05f || rotDelta > 2.0f)
                        {
                            lastSentJackPos = jackTr.position;
                            lastSentJackRot = jackTr.rotation;
                            lastLocalMoveJack = Time.time;
                            SendEquipmentTransform(0, lastSentJackPos, lastSentJackRot);
                        }
                    }
                }

                // 2. Кран двигателя (Motor Hoist)
                if (hoistTr != null)
                {
                    bool isRemotelyUpdating = (Time.time - lastNetworkUpdateHoist < 1.0f);
                    if (!isRemotelyUpdating)
                    {
                        float posDelta = Vector3.Distance(hoistTr.position, lastSentHoistPos);
                        float rotDelta = Quaternion.Angle(hoistTr.rotation, lastSentHoistRot);

                        if (posDelta > 0.05f || rotDelta > 2.0f)
                        {
                            lastSentHoistPos = hoistTr.position;
                            lastSentHoistRot = hoistTr.rotation;
                            lastLocalMoveHoist = Time.time;
                            SendEquipmentTransform(1, lastSentHoistPos, lastSentHoistRot);
                        }
                    }
                }
            }

            // Плавная сетевая интерполяция на принимающей стороне
            if (jackTr != null && hasReceivedJackTransform)
            {
                if (Time.time - lastNetworkUpdateJack < 3.0f && (Time.time - lastLocalMoveJack >= 0.8f))
                {
                    jackTr.position = Vector3.Lerp(jackTr.position, targetJackPos, dt * 12f);
                    jackTr.rotation = Quaternion.Slerp(jackTr.rotation, targetJackRot, dt * 12f);
                    if (jackRb != null && !jackRb.isKinematic)
                    {
                        jackRb.velocity = Vector3.zero;
                        jackRb.angularVelocity = Vector3.zero;
                    }
                }
            }

            if (hoistTr != null && hasReceivedHoistTransform)
            {
                if (Time.time - lastNetworkUpdateHoist < 3.0f && (Time.time - lastLocalMoveHoist >= 0.8f))
                {
                    hoistTr.position = Vector3.Lerp(hoistTr.position, targetHoistPos, dt * 12f);
                    hoistTr.rotation = Quaternion.Slerp(hoistTr.rotation, targetHoistRot, dt * 12f);
                    if (hoistRb != null && !hoistRb.isKinematic)
                    {
                        hoistRb.velocity = Vector3.zero;
                        hoistRb.angularVelocity = Vector3.zero;
                    }
                }
            }
        }

        private void SendEquipmentTransform(byte equipId, Vector3 pos, Quaternion rot)
        {
            if (transport == null || !transport.IsRunning) return;
            using (var packet = new PacketWriter(PacketType.HeavyEquipmentTransformSync))
            {
                packet.WriteByte(equipId);
                packet.WriteSingle(pos.x); packet.WriteSingle(pos.y); packet.WriteSingle(pos.z);
                packet.WriteSingle(rot.x); packet.WriteSingle(rot.y); packet.WriteSingle(rot.z); packet.WriteSingle(rot.w);
                transport.Send(packet);
            }
        }

        private void SendFloorJack(bool isUp, float y)
        {
            using (var packet = new PacketWriter(PacketType.FloorJackSync))
            {
                packet.WriteBoolean(isUp);
                packet.WriteSingle(y);
                transport.Send(packet);
            }
        }

        private void SendMotorHoist(bool isUp, float angle)
        {
            using (var packet = new PacketWriter(PacketType.MotorHoistSync))
            {
                packet.WriteBoolean(isUp);
                packet.WriteSingle(angle);
                transport.Send(packet);
            }
        }

        private void SendLanternToggle(byte lanternIndex)
        {
            using (var packet = new PacketWriter(PacketType.LanternSync))
            {
                packet.WriteByte(lanternIndex);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            switch (reader.Type)
            {
                case PacketType.FloorJackSync:
                {
                    bool isUp = reader.ReadBoolean();
                    float y = reader.ReadSingle();

                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.FloorJackSync))
                        {
                            relay.WriteBoolean(isUp);
                            relay.WriteSingle(y);
                            transport.Send(relay, connectionId);
                        }
                    }

                    if (floorJackFsm != null)
                    {
                        isSuppressingJack = true;
                        if (floorJackY != null) floorJackY.Value = y;
                        floorJackFsm.SendEvent(isUp ? "MP_LIFT_UP" : "MP_LIFT_DOWN");
                    }
                    break;
                }

                case PacketType.MotorHoistSync:
                {
                    bool isUp = reader.ReadBoolean();
                    float angle = reader.ReadSingle();

                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.MotorHoistSync))
                        {
                            relay.WriteBoolean(isUp);
                            relay.WriteSingle(angle);
                            transport.Send(relay, connectionId);
                        }
                    }

                    if (motorHoistFsm != null)
                    {
                        isSuppressingHoist = true;
                        if (motorHoistAngle != null) motorHoistAngle.Value = angle;
                        motorHoistFsm.SendEvent(isUp ? "MP_HOIST_UP" : "MP_HOIST_DOWN");
                    }
                    break;
                }

                case PacketType.LanternSync:
                {
                    byte lanternIdx = reader.ReadByte();

                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.LanternSync))
                        {
                            relay.WriteByte(lanternIdx);
                            transport.Send(relay, connectionId);
                        }
                    }

                    if (lanternIdx < lanterns.Count && lanterns[lanternIdx] != null)
                    {
                        isSuppressingLantern = true;
                        lanterns[lanternIdx].SendEvent("MP_TOGGLE");
                    }
                    break;
                }

                case PacketType.HeavyEquipmentTransformSync:
                {
                    byte equipId = reader.ReadByte();
                    float px = reader.ReadSingle(); float py = reader.ReadSingle(); float pz = reader.ReadSingle();
                    float rx = reader.ReadSingle(); float ry = reader.ReadSingle(); float rz = reader.ReadSingle(); float rw = reader.ReadSingle();

                    Vector3 pos = new Vector3(px, py, pz);
                    Quaternion rot = new Quaternion(rx, ry, rz, rw);

                    if (transport.IsServer)
                    {
                        using (var relay = new PacketWriter(PacketType.HeavyEquipmentTransformSync))
                        {
                            relay.WriteByte(equipId);
                            relay.WriteSingle(px); relay.WriteSingle(py); relay.WriteSingle(pz);
                            relay.WriteSingle(rx); relay.WriteSingle(ry); relay.WriteSingle(rz); relay.WriteSingle(rw);
                            transport.Send(relay, connectionId);
                        }
                    }

                    if (equipId == 0) // Floor Jack
                    {
                        targetJackPos = pos;
                        targetJackRot = rot;
                        lastNetworkUpdateJack = Time.time;
                        hasReceivedJackTransform = true;

                        if (jackTr != null && Vector3.Distance(jackTr.position, pos) > 3.0f)
                        {
                            jackTr.position = pos;
                            jackTr.rotation = rot;
                        }
                    }
                    else if (equipId == 1) // Motor Hoist
                    {
                        targetHoistPos = pos;
                        targetHoistRot = rot;
                        lastNetworkUpdateHoist = Time.time;
                        hasReceivedHoistTransform = true;

                        if (hoistTr != null && Vector3.Distance(hoistTr.position, pos) > 3.0f)
                        {
                            hoistTr.position = pos;
                            hoistTr.rotation = rot;
                        }
                    }
                    break;
                }
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (transport.IsServer)
            {
                if (floorJackY != null && floorJackY.Value > 0f)
                {
                    SendFloorJack(true, floorJackY.Value);
                }
                if (motorHoistAngle != null && motorHoistAngle.Value != 0f)
                {
                    SendMotorHoist(true, motorHoistAngle.Value);
                }
                if (jackTr != null)
                {
                    SendEquipmentTransform(0, jackTr.position, jackTr.rotation);
                }
                if (hoistTr != null)
                {
                    SendEquipmentTransform(1, hoistTr.position, hoistTr.rotation);
                }
            }
        }
    }
}
