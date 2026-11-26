using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Items
{
    public sealed class GarageAndToolsSync
    {
        private enum EquipmentAction : byte
        {
            FloorJack = 0,
            MotorHoist = 1,
            LanternToggle = 2
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;

        // Канистры бензина и дизеля
        private PlayMakerFSM gasLidFsm;
        private FsmBool gasOpenVar;
        private FsmBool gasClosedVar;
        private FsmFloat gasFluidVar;
        private bool suppressGasLid;
        private bool isGasLidOpen;
        private float lastSyncedGasFluid = -1f;

        private PlayMakerFSM dieselLidFsm;
        private FsmBool dieselOpenVar;
        private FsmBool dieselClosedVar;
        private FsmFloat dieselFluidVar;
        private bool suppressDieselLid;
        private bool isDieselLidOpen;
        private float lastSyncedDieselFluid = -1f;

        // Наборы ключей и трещотки
        private PlayMakerFSM spannerTopFsm;
        private FsmBool spannerOpenVar;
        private bool suppressSpanner;

        private PlayMakerFSM ratchetTopFsm;
        private FsmBool ratchetOpenVar;
        private bool suppressRatchet;

        // Домкрат, кран двигателя и керосиновые лампы
        private PlayMakerFSM floorJackFsm;
        private FsmFloat floorJackYVar;
        private bool suppressJack;

        private PlayMakerFSM motorHoistFsm;
        private FsmFloat motorHoistAngleVar;
        private bool suppressHoist;

        private readonly List<PlayMakerFSM> lanternFsms = new List<PlayMakerFSM>();
        private bool suppressLantern;

        private float nextSearchTime;
        private float nextFluidPollTime;

        public GarageAndToolsSync(OscHostContext context, string ownerModuleId)
        {
            ctx = context;
            moduleId = ownerModuleId;
        }

        public void Reset()
        {
            gasLidFsm = null;
            gasOpenVar = null;
            gasClosedVar = null;
            gasFluidVar = null;
            suppressGasLid = false;
            isGasLidOpen = false;
            lastSyncedGasFluid = -1f;

            dieselLidFsm = null;
            dieselOpenVar = null;
            dieselClosedVar = null;
            dieselFluidVar = null;
            suppressDieselLid = false;
            isDieselLidOpen = false;
            lastSyncedDieselFluid = -1f;

            spannerTopFsm = null;
            spannerOpenVar = null;
            suppressSpanner = false;

            ratchetTopFsm = null;
            ratchetOpenVar = null;
            suppressRatchet = false;

            floorJackFsm = null;
            floorJackYVar = null;
            suppressJack = false;

            motorHoistFsm = null;
            motorHoistAngleVar = null;
            suppressHoist = false;

            lanternFsms.Clear();
            suppressLantern = false;
            nextSearchTime = 0f;
            nextFluidPollTime = 0f;
        }

        public void InitializeSceneObjects()
        {
            TryFindAndHookAll();
            InitLanterns();
        }

        public void TickUpdate()
        {
            if (Time.time >= nextSearchTime)
            {
                nextSearchTime = Time.time + 5.0f;
                if (gasLidFsm == null || dieselLidFsm == null || spannerTopFsm == null ||
                    ratchetTopFsm == null || floorJackFsm == null || motorHoistFsm == null)
                {
                    TryFindAndHookAll();
                }
            }

            if (ctx?.Transport != null && ctx.Transport.IsRunning && (isGasLidOpen || isDieselLidOpen))
            {
                if (Time.time >= nextFluidPollTime)
                {
                    nextFluidPollTime = Time.time + 1.5f;
                    SendJerryCanFluidIfChanged(force: false);
                }
            }
        }

        private void TryFindAndHookAll()
        {
            Transform itemsTr = GameObject.Find("ITEMS")?.transform;

            if (gasLidFsm == null)
            {
                GameObject gasGo = (itemsTr != null ? itemsTr.Find("gasoline(itemx)")?.gameObject : null)
                    ?? FindInactiveByName("gasoline(itemx)");
                if (gasGo != null)
                {
                    Transform fluid = gasGo.transform.Find("FluidTrigger");
                    if (fluid != null)
                    {
                        var fsm = fluid.GetComponent<PlayMakerFSM>();
                        fsm?.InitializeSafe();
                        gasFluidVar = fsm?.FsmVariables.FindFsmFloat("Fluid");
                    }
                    Transform lid = gasGo.transform.Find("Open");
                    if (lid != null) HookJerryCanLid(lid.gameObject, isDiesel: false);
                }
            }

            if (dieselLidFsm == null)
            {
                GameObject dieselGo = (itemsTr != null ? itemsTr.Find("diesel(itemx)")?.gameObject : null)
                    ?? FindInactiveByName("diesel(itemx)");
                if (dieselGo != null)
                {
                    Transform fluid = dieselGo.transform.Find("FluidTrigger");
                    if (fluid != null)
                    {
                        var fsm = fluid.GetComponent<PlayMakerFSM>();
                        fsm?.InitializeSafe();
                        dieselFluidVar = fsm?.FsmVariables.FindFsmFloat("Fluid");
                    }
                    Transform lid = dieselGo.transform.Find("Open");
                    if (lid != null) HookJerryCanLid(lid.gameObject, isDiesel: true);
                }
            }

            if (spannerTopFsm == null)
            {
                Transform spannerTop = (itemsTr != null ? itemsTr.Find("spanner set(itemx)/Pivot/top") : null)
                    ?? GameObject.Find("spanner set(itemx)")?.transform.Find("Pivot/top");
                if (spannerTop != null)
                {
                    PlayMakerFSM fsm = spannerTop.GetComponent<PlayMakerFSM>();
                    if (fsm != null && ClaimFsm(fsm, "SpannerSet"))
                    {
                        spannerTopFsm = fsm;
                        HookToolSetTop(fsm, isRatchet: false, out spannerOpenVar);
                    }
                }
            }

            if (ratchetTopFsm == null)
            {
                GameObject ratchetGo = GameObject.Find("DatabaseOrders/Ratchet Set")
                    ?? GameObject.Find("Database/DatabaseOrders/Ratchet Set")
                    ?? GameObject.Find("Ratchet Set")
                    ?? (itemsTr != null ? itemsTr.Find("ratchet set(itemx)")?.gameObject : null);
                Transform ratchetTop = ratchetGo != null ? (ratchetGo.transform.Find("Hinge/Pivot/top") ?? ratchetGo.transform.Find("Pivot/top")) : null;
                if (ratchetTop != null)
                {
                    PlayMakerFSM fsm = ratchetTop.GetComponent<PlayMakerFSM>();
                    if (fsm != null && ClaimFsm(fsm, "RatchetSet"))
                    {
                        ratchetTopFsm = fsm;
                        HookToolSetTop(fsm, isRatchet: true, out ratchetOpenVar);
                    }
                }
            }

            if (floorJackFsm == null)
            {
                Transform jackTr = (itemsTr != null ? itemsTr.Find("floor jack(itemx)") : null)
                    ?? GameObject.Find("floor jack(itemx)")?.transform;
                Transform trig = jackTr != null ? jackTr.Find("Trigger") : null;
                if (trig != null)
                {
                    PlayMakerFSM fsm = trig.GetComponent<PlayMakerFSM>();
                    if (fsm != null && ClaimFsm(fsm, "FloorJack"))
                    {
                        floorJackFsm = fsm;
                        fsm.InitializeSafe();
                        floorJackYVar = fsm.FsmVariables.FindFsmFloat("Y");
                        fsm.AddSafeGlobalTransition("MP_LIFT_UP", "Up");
                        fsm.AddSafeGlobalTransition("MP_LIFT_DOWN", "Down");

                        if (fsm.HasState("Up"))
                        {
                            fsm.InsertAction("Up", () =>
                            {
                                if (suppressJack) { suppressJack = false; return; }
                                SendEquipmentState(EquipmentAction.FloorJack, true, floorJackYVar != null ? floorJackYVar.Value : 0f);
                            }, 0, true);
                        }
                        if (fsm.HasState("Down"))
                        {
                            fsm.InsertAction("Down", () =>
                            {
                                if (suppressJack) { suppressJack = false; return; }
                                SendEquipmentState(EquipmentAction.FloorJack, false, 0f);
                            }, 0, true);
                        }
                    }
                }
            }

            if (motorHoistFsm == null)
            {
                Transform hoistTr = (itemsTr != null ? itemsTr.Find("motor hoist(itemx)") : null)
                    ?? GameObject.Find("motor hoist(itemx)")?.transform;
                Transform trig = hoistTr != null ? hoistTr.Find("Pump/Trigger") : null;
                if (trig != null)
                {
                    PlayMakerFSM fsm = trig.GetComponent<PlayMakerFSM>();
                    if (fsm != null && ClaimFsm(fsm, "MotorHoist"))
                    {
                        motorHoistFsm = fsm;
                        fsm.InitializeSafe();
                        motorHoistAngleVar = fsm.FsmVariables.FindFsmFloat("Angle");
                        fsm.AddSafeGlobalTransition("MP_HOIST_UP", "Up");
                        fsm.AddSafeGlobalTransition("MP_HOIST_DOWN", "Down");

                        if (fsm.HasState("Up"))
                        {
                            fsm.InsertAction("Up", () =>
                            {
                                if (suppressHoist) { suppressHoist = false; return; }
                                SendEquipmentState(EquipmentAction.MotorHoist, true, motorHoistAngleVar != null ? motorHoistAngleVar.Value : 0f);
                            }, -1, true);
                        }
                        if (fsm.HasState("Down"))
                        {
                            fsm.InsertAction("Down", () =>
                            {
                                if (suppressHoist) { suppressHoist = false; return; }
                                SendEquipmentState(EquipmentAction.MotorHoist, false, motorHoistAngleVar != null ? motorHoistAngleVar.Value : 0f);
                            }, -1, true);
                        }
                    }
                }
            }
        }

        private void InitLanterns()
        {
            lanternFsms.Clear();
            var allGos = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allGos.Length; i++)
            {
                GameObject go = allGos[i];
                if (go == null || !go.name.StartsWith("lantern", StringComparison.OrdinalIgnoreCase)) continue;
                PlayMakerFSM fsm = PlayMakerBridge.FindFsm(go, "Use");
                if (fsm == null || !ClaimFsm(fsm, $"Lantern_{lanternFsms.Count}")) continue;

                fsm.InitializeSafe();
                int idx = lanternFsms.Count;
                lanternFsms.Add(fsm);
                fsm.AddSafeGlobalTransition("MP_TOGGLE", "State 1");
                if (fsm.HasState("State 1"))
                {
                    fsm.InsertAction("State 1", () =>
                    {
                        if (suppressLantern) { suppressLantern = false; return; }
                        SendEquipmentState(EquipmentAction.LanternToggle, false, idx);
                    }, 0, true);
                }
            }
        }

        private void HookJerryCanLid(GameObject lidGo, bool isDiesel)
        {
            PlayMakerFSM fsm = lidGo.GetComponent<PlayMakerFSM>();
            if (fsm == null || !ClaimFsm(fsm, isDiesel ? "DieselCanLid" : "GasCanLid")) return;

            fsm.InitializeSafe();
            if (isDiesel)
            {
                dieselLidFsm = fsm;
                dieselOpenVar = fsm.FsmVariables.FindFsmBool("Open");
                dieselClosedVar = fsm.FsmVariables.FindFsmBool("Closed");
            }
            else
            {
                gasLidFsm = fsm;
                gasOpenVar = fsm.FsmVariables.FindFsmBool("Open");
                gasClosedVar = fsm.FsmVariables.FindFsmBool("Closed");
            }

            fsm.AddSafeGlobalTransition("MP_TOGGLE", "State 2");
            if (fsm.HasState("State 2"))
            {
                fsm.InsertAction("State 2", () => OnLocalJerryCanLidToggled(isDiesel), 0, true);
            }
        }

        private void OnLocalJerryCanLidToggled(bool isDiesel)
        {
            if (isDiesel)
            {
                if (suppressDieselLid) { suppressDieselLid = false; return; }
            }
            else
            {
                if (suppressGasLid) { suppressGasLid = false; return; }
            }

            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;

            bool isOpen = isDiesel
                ? !(dieselOpenVar != null && dieselOpenVar.Value)
                : !(gasOpenVar != null && gasOpenVar.Value);

            if (isDiesel) isDieselLidOpen = isOpen;
            else isGasLidOpen = isOpen;

            SendJerryCanState(isDiesel, isOpen, includeFluid: !isOpen);
        }

        private void HookToolSetTop(PlayMakerFSM fsm, bool isRatchet, out FsmBool isOpenVar)
        {
            fsm.InitializeSafe();
            isOpenVar = fsm.FsmVariables.FindFsmBool("Open");
            fsm.AddSafeGlobalTransition("MP_OPEN", "Open");
            fsm.AddSafeGlobalTransition("MP_CLOSE", "Close");

            if (fsm.HasState("Open")) fsm.InsertAction("Open", () => OnLocalToolSetChanged(isRatchet, true), 0, true);
            if (fsm.HasState("Close")) fsm.InsertAction("Close", () => OnLocalToolSetChanged(isRatchet, false), 0, true);
        }

        private void OnLocalToolSetChanged(bool isRatchet, bool isOpen)
        {
            if (isRatchet)
            {
                if (suppressRatchet) { suppressRatchet = false; return; }
            }
            else
            {
                if (suppressSpanner) { suppressSpanner = false; return; }
            }

            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;

            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.ToolSetState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(isRatchet);
                p.WriteBoolean(isOpen);
                ctx.Transport.Send(p);
            }
        }

        private void SendJerryCanState(bool isDiesel, bool isOpen, bool includeFluid)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;

            float gasVal = gasFluidVar != null ? gasFluidVar.Value : 0f;
            float dieselVal = dieselFluidVar != null ? dieselFluidVar.Value : 0f;
            if (includeFluid)
            {
                lastSyncedGasFluid = gasVal;
                lastSyncedDieselFluid = dieselVal;
            }

            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.JerryCanState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(true); // hasLidUpdate
                p.WriteBoolean(isDiesel);
                p.WriteBoolean(isOpen);
                p.WriteBoolean(includeFluid);
                if (includeFluid)
                {
                    p.WriteSingle(gasVal);
                    p.WriteSingle(dieselVal);
                }
                ctx.Transport.Send(p);
            }
        }

        private void SendJerryCanFluidIfChanged(bool force)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;

            float gasVal = gasFluidVar != null ? gasFluidVar.Value : 0f;
            float dieselVal = dieselFluidVar != null ? dieselFluidVar.Value : 0f;
            if (!force && Mathf.Abs(gasVal - lastSyncedGasFluid) < 0.1f && Mathf.Abs(dieselVal - lastSyncedDieselFluid) < 0.1f)
            {
                return;
            }

            lastSyncedGasFluid = gasVal;
            lastSyncedDieselFluid = dieselVal;

            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.JerryCanState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(false); // fluid only
                p.WriteBoolean(false);
                p.WriteBoolean(false);
                p.WriteBoolean(true);
                p.WriteSingle(gasVal);
                p.WriteSingle(dieselVal);
                ctx.Transport.Send(p);
            }
        }

        private void SendEquipmentState(EquipmentAction kind, bool isUp, float value)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.ItemSync))
            {
                p.WriteByte((byte)ItemSubPacket.EquipmentState);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteByte((byte)kind);
                p.WriteBoolean(isUp);
                p.WriteSingle(value);
                ctx.Transport.Send(p);
            }
        }

        public void HandleJerryCanStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            bool hasLid = reader.ReadBoolean();
            bool isDiesel = reader.ReadBoolean();
            bool isOpen = reader.ReadBoolean();
            bool hasFluid = reader.ReadBoolean();
            float gasVal = hasFluid ? reader.ReadSingle() : 0f;
            float dieselVal = hasFluid ? reader.ReadSingle() : 0f;

            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            if (hasLid) ApplyJerryCanLid(isDiesel, isOpen);
            if (hasFluid)
            {
                if (gasFluidVar != null) { gasFluidVar.Value = gasVal; lastSyncedGasFluid = gasVal; }
                if (dieselFluidVar != null) { dieselFluidVar.Value = dieselVal; lastSyncedDieselFluid = dieselVal; }
            }
        }

        public void HandleToolSetStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            bool isRatchet = reader.ReadBoolean();
            bool targetOpen = reader.ReadBoolean();
            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            ApplyToolSetState(isRatchet, targetOpen);
        }

        public void HandleEquipmentStatePacket(PacketReader reader)
        {
            int senderId = reader.ReadInt32();
            var kind = (EquipmentAction)reader.ReadByte();
            bool isUp = reader.ReadBoolean();
            float value = reader.ReadSingle();
            if (ctx.Transport != null && senderId == ctx.Transport.LocalPlayerId) return;

            if (kind == EquipmentAction.FloorJack)
            {
                ApplyFloorJack(isUp, value);
            }
            else if (kind == EquipmentAction.MotorHoist)
            {
                ApplyMotorHoist(isUp, value);
            }
            else if (kind == EquipmentAction.LanternToggle)
            {
                int idx = Mathf.RoundToInt(value);
                if (idx >= 0 && idx < lanternFsms.Count && lanternFsms[idx] != null)
                {
                    suppressLantern = true;
                    lanternFsms[idx].SendEvent("MP_TOGGLE");
                }
            }
        }

        private void ApplyJerryCanLid(bool isDiesel, bool isOpen)
        {
            if (isDiesel)
            {
                isDieselLidOpen = isOpen;
                if (dieselLidFsm == null) return;
                if (dieselOpenVar != null && dieselOpenVar.Value == isOpen) return;
                if (dieselOpenVar != null) dieselOpenVar.Value = !isOpen;
                if (dieselClosedVar != null) dieselClosedVar.Value = isOpen;
                suppressDieselLid = true;
                dieselLidFsm.SendEvent("MP_TOGGLE");
            }
            else
            {
                isGasLidOpen = isOpen;
                if (gasLidFsm == null) return;
                if (gasOpenVar != null && gasOpenVar.Value == isOpen) return;
                if (gasOpenVar != null) gasOpenVar.Value = !isOpen;
                if (gasClosedVar != null) gasClosedVar.Value = isOpen;
                suppressGasLid = true;
                gasLidFsm.SendEvent("MP_TOGGLE");
            }
        }

        private void ApplyToolSetState(bool isRatchet, bool targetOpen)
        {
            PlayMakerFSM fsm = isRatchet ? ratchetTopFsm : spannerTopFsm;
            FsmBool openVar = isRatchet ? ratchetOpenVar : spannerOpenVar;
            if (fsm == null) return;
            if (openVar != null && openVar.Value == targetOpen) return;

            if (isRatchet) suppressRatchet = true;
            else suppressSpanner = true;
            fsm.SendEvent(targetOpen ? "MP_OPEN" : "MP_CLOSE");
        }

        private void ApplyFloorJack(bool isUp, float yVal)
        {
            if (floorJackFsm == null) return;
            suppressJack = true;
            if (floorJackYVar != null) floorJackYVar.Value = yVal;
            floorJackFsm.SendEvent(isUp ? "MP_LIFT_UP" : "MP_LIFT_DOWN");
        }

        private void ApplyMotorHoist(bool isUp, float angleVal)
        {
            if (motorHoistFsm == null) return;
            suppressHoist = true;
            if (motorHoistAngleVar != null) motorHoistAngleVar.Value = angleVal;
            motorHoistFsm.SendEvent(isUp ? "MP_HOIST_UP" : "MP_HOIST_DOWN");
        }

        public void WriteSnapshot(PacketWriter writer)
        {
            writer.WriteBoolean(gasOpenVar != null && gasOpenVar.Value);
            writer.WriteBoolean(dieselOpenVar != null && dieselOpenVar.Value);
            writer.WriteSingle(gasFluidVar != null ? gasFluidVar.Value : -1f);
            writer.WriteSingle(dieselFluidVar != null ? dieselFluidVar.Value : -1f);

            writer.WriteBoolean(spannerOpenVar != null && spannerOpenVar.Value);
            writer.WriteBoolean(ratchetOpenVar != null && ratchetOpenVar.Value);

            float jackY = floorJackYVar != null ? floorJackYVar.Value : 0f;
            writer.WriteBoolean(floorJackYVar != null && jackY > 0.01f);
            writer.WriteSingle(jackY);

            float hoistAngle = motorHoistAngleVar != null ? motorHoistAngleVar.Value : 0f;
            writer.WriteBoolean(motorHoistAngleVar != null && Mathf.Abs(hoistAngle) > 0.01f);
            writer.WriteSingle(hoistAngle);
        }

        public void ApplySnapshot(PacketReader reader)
        {
            bool gasOpen = reader.ReadBoolean();
            bool dieselOpen = reader.ReadBoolean();
            float gasFluid = reader.ReadSingle();
            float dieselFluid = reader.ReadSingle();

            bool spannerOpen = reader.ReadBoolean();
            bool ratchetOpen = reader.ReadBoolean();

            bool jackUp = reader.ReadBoolean();
            float jackY = reader.ReadSingle();

            bool hoistMoved = reader.ReadBoolean();
            float hoistAngle = reader.ReadSingle();

            ApplyJerryCanLid(isDiesel: false, isOpen: gasOpen);
            ApplyJerryCanLid(isDiesel: true, isOpen: dieselOpen);
            if (gasFluid >= 0f && gasFluidVar != null) gasFluidVar.Value = gasFluid;
            if (dieselFluid >= 0f && dieselFluidVar != null) dieselFluidVar.Value = dieselFluid;

            ApplyToolSetState(isRatchet: false, targetOpen: spannerOpen);
            ApplyToolSetState(isRatchet: true, targetOpen: ratchetOpen);

            if (jackUp) ApplyFloorJack(true, jackY);
            if (hoistMoved) ApplyMotorHoist(true, hoistAngle);
        }

        private bool ClaimFsm(PlayMakerFSM fsm, string label)
        {
            return ctx.Ownership == null || ctx.Ownership.TryClaim(fsm, moduleId, label);
        }

        private static GameObject FindInactiveByName(string objName)
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == objName) return all[i];
            }
            return null;
        }
    }
}
