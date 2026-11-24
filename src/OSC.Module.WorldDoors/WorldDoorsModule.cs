using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.WorldDoors
{
    public sealed class WorldDoorsModule : IOscModule
    {
        private sealed class TrackedDoor
        {
            public int Index;
            public GameObject GameObject;
            public PlayMakerFSM Fsm;
            public FsmBool DoorOpenVar;
            public FsmEvent ToggleEvent;
            public bool SuppressEcho;
        }

        public string ModuleId => "WorldDoors";
        public string DisplayName => "Двери и Гараж";
        public string StatusSummary => $"Двери: {doors.Count}, Гараж: {(garageReady ? "OK" : "Поиск")}";

        private OscHostContext ctx;
        private readonly List<TrackedDoor> doors = new List<TrackedDoor>();
        private bool doorsInitialized;

        // Гаражные ворота (изолированный владелец DoorLeft / DoorRight)
        private bool garageReady;
        private float nextGarageSearchTime;
        private Transform garageLeft;
        private Transform garageRight;
        private Rigidbody garageLeftRb;
        private Rigidbody garageRightRb;
        private PlayMakerFSM garageLeftFsm;
        private PlayMakerFSM garageRightFsm;
        private FsmFloat garageLeftRotVar;
        private FsmFloat garageRightRotVar;
        private bool garageLeftDefaultKin;
        private bool garageRightDefaultKin;

        private float lastSentLeftAngle = -999f;
        private float lastSentRightAngle = -999f;
        private float targetLeftAngle;
        private float targetRightAngle;
        private float velLeft;
        private float velRight;
        private float lastLocalMoveLeft = -10f;
        private float lastLocalMoveRight = -10f;
        private float lastRemoteLeft = -10f;
        private float lastRemoteRight = -10f;
        private bool leftRemoteApplying;
        private bool rightRemoteApplying;
        private float nextGarageSyncTime;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            ctx.Router.Register(PacketType.DoorToggle, ModuleId, HandleDoorToggle, relayOnServer: true);
            ctx.Router.Register(PacketType.DoorSnapshot, ModuleId, HandleDoorSnapshot);
            ctx.Router.Register(PacketType.GarageDoorSync, ModuleId, HandleGarageDoorSync, relayOnServer: true);
            ctx.Transport.OnClientConnected += HandleClientConnected;
        }

        public void OnMenuLoaded() => ResetState();

        public void OnLevelLoaded()
        {
            ResetState();
            InitInteriorDoors();
            TryInitGarageDoors();
        }

        public void OnShutdown()
        {
            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
            }
            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetState();
        }

        private void ResetState()
        {
            doors.Clear();
            doorsInitialized = false;
            garageReady = false;
            nextGarageSearchTime = 0f;
            garageLeft = null;
            garageRight = null;
            garageLeftRb = null;
            garageRightRb = null;
            garageLeftFsm = null;
            garageRightFsm = null;
            garageLeftRotVar = null;
            garageRightRotVar = null;
            leftRemoteApplying = false;
            rightRemoteApplying = false;
            lastSentLeftAngle = -999f;
            lastSentRightAngle = -999f;
            lastLocalMoveLeft = -10f;
            lastLocalMoveRight = -10f;
            lastRemoteLeft = -10f;
            lastRemoteRight = -10f;
        }

        public void OnUpdate()
        {
            if (!doorsInitialized)
            {
                InitInteriorDoors();
            }

            if (!garageReady || garageLeft == null || garageRight == null)
            {
                if (Time.time >= nextGarageSearchTime)
                {
                    nextGarageSearchTime = Time.time + 1.5f;
                    TryInitGarageDoors();
                }
                return;
            }

            if (ctx?.Transport != null && ctx.Transport.IsRunning && Time.time >= nextGarageSyncTime)
            {
                nextGarageSyncTime = Time.time + 0.033f;
                SendLocalGarageChanges();
            }
        }

        public void OnFixedUpdate()
        {
            if (!garageReady || garageLeft == null || garageRight == null) return;
            StepRemoteGarageDoors();
        }

        public void OnLateUpdate() { }

        private void InitInteriorDoors()
        {
            doors.Clear();

            List<GameObject> candidates = (from x in Resources.FindObjectsOfTypeAll<GameObject>()
                                         where (x.name.StartsWith("Door") && !x.name.StartsWith("DoorLeft") && !x.name.StartsWith("DoorRight")) ||
                                               (x.name == "coll" && x.transform.parent != null && x.transform.parent.name == "door" && x.transform.root.name == "COMBINE(350-400psi)")
                                         select x).ToList();

            GameObject fridge = GameObject.Find("YARD/Building/KITCHEN/Fridge/Pivot/Handle");
            if (fridge != null) candidates.Add(fridge);

            candidates.Sort((a, b) => string.CompareOrdinal(SceneHasher.GetScenePath(a.transform), SceneHasher.GetScenePath(b.transform)));

            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject go = candidates[i];
                if (go == null) continue;

                Transform handle = go.transform.Find("Pivot/Handle") ?? go.transform.Find("Handle") ?? go.transform;
                PlayMakerFSM fsm = PlayMakerBridge.FindFsm(handle.gameObject, "Use");
                if (fsm == null) continue;

                fsm.InitializeSafe();
                if (fsm.FsmEvents == null || !fsm.FsmEvents.Any(e => e != null && e.Name == "OPENDOOR")) continue;
                if (!ctx.Ownership.TryClaim(fsm, ModuleId, "InteriorDoor")) continue;

                Rigidbody doorRb = go.GetComponent<Rigidbody>();
                if (doorRb != null)
                {
                    UnityEngine.Object.Destroy(doorRb);
                }

                FsmBool doorOpen = fsm.FsmVariables.FindFsmBool("DoorOpen");
                FsmEvent toggleEvent = fsm.AddSafeGlobalTransition("MP_TOGGLEDOOR", "Check position");

                var tracked = new TrackedDoor
                {
                    Index = doors.Count,
                    GameObject = go,
                    Fsm = fsm,
                    DoorOpenVar = doorOpen,
                    ToggleEvent = toggleEvent,
                    SuppressEcho = false
                };

                fsm.InsertAction("Check position", () =>
                {
                    if (tracked.SuppressEcho)
                    {
                        tracked.SuppressEcho = false;
                        return;
                    }
                    bool targetOpen = tracked.DoorOpenVar != null && !tracked.DoorOpenVar.Value;
                    SendDoorToggle(tracked.Index, targetOpen);
                }, 0);

                doors.Add(tracked);
            }

            doorsInitialized = doors.Count > 0;
            if (doorsInitialized)
            {
                ctx.Log?.Invoke($"[Двери] Подключено межкомнатных дверей: {doors.Count}");
            }
        }

        private void TryInitGarageDoors()
        {
            if (garageReady) return;

            Transform garageDoors = GameObject.Find("YARD")?.transform.Find("Building/Garage/GarageDoors")
                                    ?? GameObject.Find("GarageDoors")?.transform;
            if (garageDoors == null) return;

            garageLeft = garageDoors.Find("DoorLeft");
            garageRight = garageDoors.Find("DoorRight");
            if (garageLeft == null || garageRight == null) return;

            garageLeftRb = garageLeft.GetComponent<Rigidbody>();
            garageRightRb = garageRight.GetComponent<Rigidbody>();
            if (garageLeftRb != null)
            {
                ctx.Ownership.TryClaim(garageLeftRb, ModuleId, "GarageLeftRb");
                garageLeftDefaultKin = garageLeftRb.isKinematic;
            }
            if (garageRightRb != null)
            {
                ctx.Ownership.TryClaim(garageRightRb, ModuleId, "GarageRightRb");
                garageRightDefaultKin = garageRightRb.isKinematic;
            }

            garageLeftFsm = PlayMakerBridge.FindFsm((garageLeft.Find("Coll") ?? garageLeft).gameObject, "Use");
            garageRightFsm = PlayMakerBridge.FindFsm((garageRight.Find("Coll") ?? garageRight).gameObject, "Use");

            if (garageLeftFsm != null && ctx.Ownership.TryClaim(garageLeftFsm, ModuleId, "GarageLeftFsm"))
            {
                garageLeftFsm.InitializeSafe();
                garageLeftRotVar = garageLeftFsm.FsmVariables.FindFsmFloat("Rotation");
                garageLeftFsm.InsertAction("Open", () => MarkLocalGarageMove(true), 0);
                garageLeftFsm.InsertAction("Close", () => MarkLocalGarageMove(true), 0);
            }

            if (garageRightFsm != null && ctx.Ownership.TryClaim(garageRightFsm, ModuleId, "GarageRightFsm"))
            {
                garageRightFsm.InitializeSafe();
                garageRightRotVar = garageRightFsm.FsmVariables.FindFsmFloat("Rotation");
                garageRightFsm.InsertAction("Open", () => MarkLocalGarageMove(false), 0);
                garageRightFsm.InsertAction("Close", () => MarkLocalGarageMove(false), 0);
            }

            targetLeftAngle = garageLeft.localEulerAngles.z;
            targetRightAngle = garageRight.localEulerAngles.z;
            lastSentLeftAngle = targetLeftAngle;
            lastSentRightAngle = targetRightAngle;
            garageReady = true;
            ctx.Log?.Invoke("[Гараж] Створки ворот подключены (FixedUpdate + контроль кинематики).");
        }

        private void MarkLocalGarageMove(bool isLeft)
        {
            if (isLeft)
            {
                lastLocalMoveLeft = Time.time;
                RestoreGaragePhysics(garageLeftRb, garageLeftDefaultKin, ref leftRemoteApplying);
            }
            else
            {
                lastLocalMoveRight = Time.time;
                RestoreGaragePhysics(garageRightRb, garageRightDefaultKin, ref rightRemoteApplying);
            }
        }

        private static bool IsInteracting(PlayMakerFSM fsm)
        {
            if (fsm == null) return false;
            string s = fsm.ActiveStateName;
            return s == "Open" || s == "Close";
        }

        private void SendLocalGarageChanges()
        {
            float curLeft = garageLeft.localEulerAngles.z;
            float curRight = garageRight.localEulerAngles.z;

            bool leftHold = IsInteracting(garageLeftFsm);
            bool rightHold = IsInteracting(garageRightFsm);
            float dLeft = Mathf.Abs(Mathf.DeltaAngle(curLeft, lastSentLeftAngle));
            float dRight = Mathf.Abs(Mathf.DeltaAngle(curRight, lastSentRightAngle));

            if (leftHold || (!leftRemoteApplying && dLeft > 0.25f)) lastLocalMoveLeft = Time.time;
            if (rightHold || (!rightRemoteApplying && dRight > 0.25f)) lastLocalMoveRight = Time.time;

            bool leftActive = leftHold || (!leftRemoteApplying && (Time.time - lastLocalMoveLeft < 0.6f));
            bool rightActive = rightHold || (!rightRemoteApplying && (Time.time - lastLocalMoveRight < 0.6f));

            if ((leftActive && dLeft > 0.2f) || (rightActive && dRight > 0.2f))
            {
                lastSentLeftAngle = curLeft;
                lastSentRightAngle = curRight;
                SendGarageAngles(curLeft, curRight, leftActive, rightActive);
            }
        }

        private void StepRemoteGarageDoors()
        {
            StepOneDoor(garageLeft, garageLeftRb, garageLeftDefaultKin, garageLeftFsm, garageLeftRotVar,
                targetLeftAngle, ref velLeft, lastLocalMoveLeft, lastRemoteLeft, ref leftRemoteApplying);

            StepOneDoor(garageRight, garageRightRb, garageRightDefaultKin, garageRightFsm, garageRightRotVar,
                targetRightAngle, ref velRight, lastLocalMoveRight, lastRemoteRight, ref rightRemoteApplying);
        }

        private static void StepOneDoor(
            Transform door,
            Rigidbody rb,
            bool defaultKin,
            PlayMakerFSM fsm,
            FsmFloat rotVar,
            float targetAngle,
            ref float vel,
            float lastLocalTime,
            float lastRemoteTime,
            ref bool isApplyingRemote)
        {
            if (door == null) return;

            bool isLocal = IsInteracting(fsm) || (Time.time - lastLocalTime < 0.6f);
            if (isLocal || (Time.time - lastRemoteTime > 1.2f))
            {
                if (isApplyingRemote)
                {
                    RestoreGaragePhysics(rb, defaultKin, ref isApplyingRemote);
                }
                return;
            }

            isApplyingRemote = true;
            if (rb != null && !rb.isKinematic)
            {
                rb.isKinematic = true;
            }

            float cur = door.localEulerAngles.z;
            float delta = Mathf.Abs(Mathf.DeltaAngle(cur, targetAngle));
            float next = (delta > 0.08f)
                ? Mathf.SmoothDampAngle(cur, targetAngle, ref vel, 0.05f, 540f, Time.fixedDeltaTime)
                : targetAngle;

            Vector3 euler = door.localEulerAngles;
            door.localEulerAngles = new Vector3(euler.x, euler.y, next);
            if (rotVar != null) rotVar.Value = next;

            if (delta <= 0.08f && Time.time - lastRemoteTime > 0.35f)
            {
                vel = 0f;
                RestoreGaragePhysics(rb, defaultKin, ref isApplyingRemote);
            }
        }

        private static void RestoreGaragePhysics(Rigidbody rb, bool defaultKin, ref bool isApplyingRemote)
        {
            isApplyingRemote = false;
            if (rb == null) return;
            rb.isKinematic = defaultKin;
            if (!rb.isKinematic)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private void SendDoorToggle(int doorIndex, bool targetOpen)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.DoorToggle))
            {
                p.WriteInt32(doorIndex);
                p.WriteBoolean(targetOpen);
                ctx.Transport.Send(p);
            }
        }

        private void SendGarageAngles(float leftAngle, float rightAngle, bool leftActive, bool rightActive)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.GarageDoorSync))
            {
                p.WriteSingle(leftAngle);
                p.WriteSingle(rightAngle);
                p.WriteBoolean(leftActive);
                p.WriteBoolean(rightActive);
                ctx.Transport.Send(p);
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (!ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.DoorSnapshot))
            {
                p.WriteInt32(doors.Count);
                for (int i = 0; i < doors.Count; i++)
                {
                    p.WriteBoolean(doors[i].DoorOpenVar != null && doors[i].DoorOpenVar.Value);
                }
                ctx.Transport.SendTo(p, clientId);
            }

            if (garageReady && garageLeft != null && garageRight != null)
            {
                SendGarageAngles(garageLeft.localEulerAngles.z, garageRight.localEulerAngles.z, true, true);
            }
        }

        private void HandleDoorToggle(PacketReader reader, int connectionId)
        {
            int idx = reader.ReadInt32();
            bool targetOpen = reader.ReadBoolean();
            if (idx < 0 || idx >= doors.Count) return;

            TrackedDoor d = doors[idx];
            if (d.Fsm == null || d.DoorOpenVar == null) return;

            d.SuppressEcho = true;
            d.DoorOpenVar.Value = !targetOpen;
            d.Fsm.Fsm.Event(d.ToggleEvent);
        }

        private void HandleDoorSnapshot(PacketReader reader, int connectionId)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                bool open = reader.ReadBoolean();
                if (i < doors.Count)
                {
                    TrackedDoor d = doors[i];
                    if (d.DoorOpenVar != null && d.DoorOpenVar.Value != open)
                    {
                        d.SuppressEcho = true;
                        d.DoorOpenVar.Value = !open;
                        d.Fsm.Fsm.Event(d.ToggleEvent);
                    }
                }
            }
        }

        private void HandleGarageDoorSync(PacketReader reader, int connectionId)
        {
            float netLeft = reader.ReadSingle();
            float netRight = reader.ReadSingle();
            bool leftActive = reader.ReadBoolean();
            bool rightActive = reader.ReadBoolean();

            if (!garageReady) TryInitGarageDoors();
            if (!garageReady) return;

            if (leftActive && !IsInteracting(garageLeftFsm) && (Time.time - lastLocalMoveLeft >= 0.6f))
            {
                targetLeftAngle = netLeft;
                lastSentLeftAngle = netLeft;
                lastRemoteLeft = Time.time;
            }

            if (rightActive && !IsInteracting(garageRightFsm) && (Time.time - lastLocalMoveRight >= 0.6f))
            {
                targetRightAngle = netRight;
                lastSentRightAngle = netRight;
                lastRemoteRight = Time.time;
            }
        }
    }
}
