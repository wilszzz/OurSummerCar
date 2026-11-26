using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles.Doors
{
    public sealed class VehiclesDoorsModule : IOscModule
    {
        private sealed class VehicleRootSpec
        {
            public string Key;
            public int VehicleId;
            public string[] SearchNames;
        }

        private sealed class TrackedVehicleRoot
        {
            public string Key;
            public int VehicleId;
            public Transform Transform;
            public Rigidbody MainRigidbody;
            public Vector3 LastPosition;
            public bool HasLastPosition;
        }

        private static readonly VehicleRootSpec[] VehicleSpecs = new[]
        {
            new VehicleRootSpec
            {
                Key = "Satsuma",
                VehicleId = 0,
                SearchNames = new[] { "SATSUMA(557kg, 248)", "SATSUMA(580kg, 87)", "SATSUMA", "Satsuma" }
            },
            new VehicleRootSpec
            {
                Key = "Hayosiko",
                VehicleId = 1,
                SearchNames = new[] { "HAYOSIKO(1500kg, 250)", "HAYOSIKO", "Hayosiko" }
            },
            new VehicleRootSpec
            {
                Key = "Ruscko",
                VehicleId = 2,
                SearchNames = new[] { "RCO_RUSCKO12(270)", "RCO_RUSCKO12", "Ruscko", "RUSCKO" }
            },
            new VehicleRootSpec
            {
                Key = "Ferndale",
                VehicleId = 3,
                SearchNames = new[] { "FERNDALE(1630kg)", "FERNDALE", "Ferndale" }
            },
            new VehicleRootSpec
            {
                Key = "Gifu",
                VehicleId = 5,
                SearchNames = new[] { "GIFU(750/450psi)", "GIFU", "Gifu" }
            }
        };

        private const byte ModeAngular = 0;
        private const byte ModeLinear = 1;
        private const float TeleportJumpThresholdSqr = 25f; // > 5m за FixedUpdate
        private const float TeleportSuppressSeconds = 0.25f;

        public string ModuleId => "Vehicles.Doors";
        public string DisplayName => "Двери транспорта";
        public string StatusSummary => $"Дверей: {doorList.Count}, Авто: {vehicleRoots.Count}/{VehicleSpecs.Length}";

        private OscHostContext ctx;
        private readonly Dictionary<int, TrackedVehicleDoor> trackedDoors = new Dictionary<int, TrackedVehicleDoor>();
        private readonly List<TrackedVehicleDoor> doorList = new List<TrackedVehicleDoor>();
        private readonly HashSet<int> registeredFsmIds = new HashSet<int>();
        private readonly HashSet<string> registeredSatsumaPanels = new HashSet<string>();
        private readonly Dictionary<string, TrackedVehicleRoot> vehicleRoots = new Dictionary<string, TrackedVehicleRoot>();
        private readonly List<PendingDoorSnapshot> pendingSnapshots = new List<PendingDoorSnapshot>();

        private sealed class PendingDoorSnapshot
        {
            public int ClientId;
            public float SendAtTime;
        }

        private int satsumaPanelScanAttempts;
        private const int MaxSatsumaPanelScanAttempts = 5;
        private float nextScanTime;
        private const float ScanIntervalSeconds = 2.0f;

        private int LocalPlayerId => (ctx?.Transport != null) ? ctx.Transport.LocalPlayerId : 0;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            ctx.Router.Register(PacketType.VehicleDoorToggle, ModuleId, HandleDoorToggle, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleDoorRotation, ModuleId, HandleDoorRotation, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleDoorOwnership, ModuleId, HandleDoorOwnership, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleDoorSnapshot, ModuleId, HandleDoorSnapshot, relayOnServer: false);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
            ctx.Events?.Subscribe<VehicleTeleportEvent>(HandleVehicleTeleportEvent);
        }

        public void OnMenuLoaded() => ResetState();

        public void OnLevelLoaded()
        {
            ResetState();
            ScanAndRegisterDoors();
        }

        public void OnShutdown()
        {
            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }
            ctx?.Events?.Unsubscribe<VehicleTeleportEvent>(HandleVehicleTeleportEvent);
            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetState();
        }

        private void ResetState()
        {
            for (int i = 0; i < doorList.Count; i++)
            {
                RestoreDoorPhysics(doorList[i]);
            }

            trackedDoors.Clear();
            doorList.Clear();
            registeredFsmIds.Clear();
            registeredSatsumaPanels.Clear();
            vehicleRoots.Clear();
            pendingSnapshots.Clear();
            satsumaPanelScanAttempts = 0;
            nextScanTime = 0f;
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            bool needsSatsumaPanels = registeredSatsumaPanels.Count < 4 && satsumaPanelScanAttempts < MaxSatsumaPanelScanAttempts;
            bool needsScan = vehicleRoots.Count < VehicleSpecs.Length || needsSatsumaPanels;
            if (needsScan && Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + ScanIntervalSeconds;
                ScanAndRegisterDoors();
            }

            ProcessPendingSnapshots();
        }

        public void OnFixedUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;

            DetectLocalChassisJumps();

            if (ctx.Transport != null && ctx.Transport.IsRunning)
            {
                SyncOpenDoorChanges();
            }

            ApplyRemoteDoorInterpolation();
        }

        public void OnLateUpdate() { }

        private void ScanAndRegisterDoors()
        {
            try
            {
                int beforeCount = doorList.Count;

                // 1. Поиск всех целевых автомобилей на сцене (с исправленными именами)
                for (int i = 0; i < VehicleSpecs.Length; i++)
                {
                    VehicleRootSpec spec = VehicleSpecs[i];
                    if (vehicleRoots.TryGetValue(spec.Key, out TrackedVehicleRoot existingRoot) && existingRoot.Transform != null)
                    {
                        continue;
                    }

                    GameObject vehGo = FindVehicleObject(spec.SearchNames);
                    if (vehGo == null) continue;

                    var root = new TrackedVehicleRoot
                    {
                        Key = spec.Key,
                        VehicleId = spec.VehicleId,
                        Transform = vehGo.transform,
                        MainRigidbody = vehGo.GetComponent<Rigidbody>(),
                        LastPosition = vehGo.transform.position,
                        HasLastPosition = true
                    };
                    vehicleRoots[spec.Key] = root;

                    RegisterVehicleDoors(vehGo, root);
                }

                // 2. Съёмные панели кузова Satsuma в Database/DatabaseBody (после заполнения vehicleRoots["Satsuma"])
                if (registeredSatsumaPanels.Count < 4 && satsumaPanelScanAttempts < MaxSatsumaPanelScanAttempts)
                {
                    satsumaPanelScanAttempts++;
                    ScanSatsumaDatabasePanels();
                }

                if (doorList.Count > beforeCount)
                {
                    ctx.Log?.Invoke($"[Двери авто] Зарегистрировано дверей/панелей: {doorList.Count} (авто: {vehicleRoots.Count}/{VehicleSpecs.Length})");
                }
            }
            catch (Exception ex)
            {
                ctx.Log?.Invoke($"[Двери авто] Ошибка сканирования: {ex.Message}");
            }
        }

        private static GameObject FindVehicleObject(string[] searchNames)
        {
            if (searchNames == null) return null;
            for (int i = 0; i < searchNames.Length; i++)
            {
                GameObject go = GameObject.Find(searchNames[i]);
                if (go != null) return go;
            }
            return null;
        }

        private void ScanSatsumaDatabasePanels()
        {
            Rigidbody satsumaMainRb = null;
            if (vehicleRoots.TryGetValue("Satsuma", out TrackedVehicleRoot satsumaRoot))
            {
                satsumaMainRb = satsumaRoot.MainRigidbody;
            }

            Transform dbBody = FindDatabaseBodyTransform();

            TryRegisterSatsumaPanel("Door_Left", dbBody, "Database/DatabaseBody/Door_Left", null, satsumaMainRb);
            TryRegisterSatsumaPanel("Door_Right", dbBody, "Database/DatabaseBody/Door_Right", null, satsumaMainRb);
            TryRegisterSatsumaPanel("Bootlid/Handles", dbBody, "Database/DatabaseBody/Bootlid/Handles", "Bootlid", satsumaMainRb);
            TryRegisterSatsumaPanel("Hood", dbBody, "Database/DatabaseBody/Hood", null, satsumaMainRb);
        }

        private static Transform FindDatabaseBodyTransform()
        {
            GameObject dbBodyGo = GameObject.Find("Database/DatabaseBody");
            if (dbBodyGo != null) return dbBodyGo.transform;

            Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform t = allTransforms[i];
                if (t != null && t.name == "DatabaseBody" && t.parent != null && t.parent.name == "Database")
                {
                    return t;
                }
            }
            return null;
        }

        private void TryRegisterSatsumaPanel(string panelKey, Transform dbBody, string directPath, string requiredParentName, Rigidbody satsumaMainRb)
        {
            if (registeredSatsumaPanels.Contains(panelKey)) return;

            GameObject obj = GameObject.Find(directPath);
            if (obj == null && dbBody != null)
            {
                Transform found = dbBody.Find(panelKey);
                if (found != null) obj = found.gameObject;
            }

            // Безопасный fallback без коллизии по общему имени "Handles"
            if (obj == null)
            {
                string shortName = panelKey.Contains("/") ? panelKey.Substring(panelKey.LastIndexOf('/') + 1) : panelKey;
                string parentCheck = !string.IsNullOrEmpty(requiredParentName) ? requiredParentName : "DatabaseBody";
                GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allObjects.Length; i++)
                {
                    GameObject candidate = allObjects[i];
                    if (candidate == null || candidate.name != shortName) continue;
                    Transform parent = candidate.transform.parent;
                    if (parent != null && parent.name == parentCheck)
                    {
                        obj = candidate;
                        break;
                    }
                }
            }

            if (obj == null) return;

            PlayMakerFSM fsm = FindUseFsmInHierarchy(obj);
            if (fsm == null) return;

            if (RegisterDoorFsm(fsm, "Satsuma", satsumaMainRb, isHayosikoSidedoor: false))
            {
                registeredSatsumaPanels.Add(panelKey);
            }
        }

        private void RegisterVehicleDoors(GameObject vehGo, TrackedVehicleRoot root)
        {
            if (vehGo == null || root == null) return;

            // Специфичные двери Hayosiko: сдвижная боковая + задняя распашная
            if (root.Key == "Hayosiko")
            {
                Transform sideDoor = vehGo.transform.Find("SideDoor/door/Collider");
                if (sideDoor == null)
                {
                    Transform sideRoot = PlayMakerBridge.FindChildRecursive(vehGo.transform, "SideDoor");
                    if (sideRoot != null)
                    {
                        sideDoor = sideRoot.Find("door/Collider") ?? PlayMakerBridge.FindChildRecursive(sideRoot, "Collider");
                    }
                }

                if (sideDoor != null)
                {
                    PlayMakerFSM sideFsm = PlayMakerBridge.FindFsm(sideDoor.gameObject, "Use");
                    if (sideFsm != null)
                    {
                        RegisterDoorFsm(sideFsm, root.Key, root.MainRigidbody, isHayosikoSidedoor: true);
                    }
                }

                Transform backDoor = vehGo.transform.Find("RearDoor/doorear")
                                  ?? vehGo.transform.Find("RearDoor")
                                  ?? vehGo.transform.Find("doorear")
                                  ?? PlayMakerBridge.FindChildRecursive(vehGo.transform, "doorear")
                                  ?? PlayMakerBridge.FindChildRecursive(vehGo.transform, "RearDoor")
                                  ?? vehGo.transform.Find("Backdoor/Collider")
                                  ?? vehGo.transform.Find("Backdoor/door/Collider")
                                  ?? PlayMakerBridge.FindChildRecursive(vehGo.transform, "Backdoor");
                if (backDoor != null)
                {
                    PlayMakerFSM backFsm = FindUseFsmInHierarchy(backDoor.gameObject);
                    if (backFsm != null && IsLikelyDoorFsm(backFsm))
                    {
                        RegisterDoorFsm(backFsm, root.Key, root.MainRigidbody, isHayosikoSidedoor: false);
                    }
                }
            }

            // Общее сканирование всех дверей, капотов и багажников на транспорте
            PlayMakerFSM[] fsms = vehGo.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null || fsm.FsmName != "Use") continue;
                if (registeredFsmIds.Contains(fsm.GetInstanceID())) continue;

                if (!IsDoorCandidateName(fsm.transform.name)) continue;
                if (!IsLikelyDoorFsm(fsm)) continue;

                RegisterDoorFsm(fsm, root.Key, root.MainRigidbody, isHayosikoSidedoor: false);
            }
        }

        private static PlayMakerFSM FindUseFsmInHierarchy(GameObject go)
        {
            if (go == null) return null;
            PlayMakerFSM direct = PlayMakerBridge.FindFsm(go, "Use");
            if (direct != null) return direct;

            PlayMakerFSM[] children = go.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].FsmName == "Use")
                {
                    return children[i];
                }
            }
            return null;
        }

        private static bool IsDoorCandidateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "Handle" || name == "Handles") return true;

            string lower = name.ToLower();
            return lower.Contains("door")
                || lower.Contains("doorear")
                || lower.Contains("bootlid")
                || lower.Contains("hood")
                || lower.Contains("trunk")
                || lower.Contains("backdoor")
                || lower.Contains("reardoor");
        }

        private static bool IsLikelyDoorFsm(PlayMakerFSM fsm)
        {
            if (fsm == null) return false;
            fsm.InitializeSafe();

            if (fsm.FsmVariables.FindFsmBool("Open") != null) return true;
            if (fsm.HasState("Open door") || fsm.HasState("Open hood") || fsm.HasState("Open")) return true;
            return false;
        }

        private bool RegisterDoorFsm(PlayMakerFSM fsm, string vehicleKey, Rigidbody vehMainRb, bool isHayosikoSidedoor)
        {
            if (fsm == null) return false;

            int fsmId = fsm.GetInstanceID();
            if (registeredFsmIds.Contains(fsmId)) return true;

            int hash = SceneHasher.GetHash(fsm.gameObject);
            if (hash == 0 || trackedDoors.ContainsKey(hash)) return true;

            if (!ctx.Ownership.TryClaim(fsm, ModuleId, $"VehicleDoorFsm:{vehicleKey}:{fsm.gameObject.name}"))
            {
                return false;
            }

            fsm.InitializeSafe();

            var tracked = new TrackedVehicleDoor
            {
                Hash = hash,
                VehicleKey = vehicleKey,
                Fsm = fsm,
                IsHayosikoSidedoor = isHayosikoSidedoor,
                OwnerId = 0
            };

            if (isHayosikoSidedoor)
            {
                SetupSidedoor(tracked, vehMainRb);
            }
            else
            {
                SetupStandardDoor(tracked, vehMainRb);
            }

            registeredFsmIds.Add(fsmId);
            trackedDoors[hash] = tracked;
            doorList.Add(tracked);
            return true;
        }

        private void SetupSidedoor(TrackedVehicleDoor tracked, Rigidbody vehMainRb)
        {
            PlayMakerFSM fsm = tracked.Fsm;
            tracked.DoorOpenVar = fsm.FsmVariables.FindFsmBool("Open");
            tracked.IsOpen = tracked.DoorOpenVar != null && tracked.DoorOpenVar.Value;

            FsmGameObject doorGoVar = fsm.FsmVariables.FindFsmGameObject("Door");
            if (doorGoVar != null && doorGoVar.Value != null)
            {
                tracked.DoorObject = doorGoVar.Value;
            }
            else if (fsm.gameObject.GetComponent<Rigidbody>() != null)
            {
                tracked.DoorObject = fsm.gameObject;
            }
            else if (fsm.transform.parent != null &&
                     (fsm.transform.parent.name.ToLower().Contains("door") || fsm.transform.parent.GetComponent<Rigidbody>() != null))
            {
                tracked.DoorObject = fsm.transform.parent.gameObject;
            }
            else
            {
                tracked.DoorObject = fsm.gameObject;
            }

            BindDoorRigidbody(tracked, vehMainRb);
            if (!tracked.IsOpen)
            {
                tracked.ClosedLocalPos = GetDoorLocalPos(tracked);
                tracked.HasClosedLocalPos = true;
            }

            string openState = ResolveFirstExistingState(fsm, new[] { "Open door", "Open", "State 1" });
            string closeState = ResolveFirstExistingState(fsm, new[] { "Close door", "Sound", "Close", "State 2" });

            if (!string.IsNullOrEmpty(openState))
            {
                tracked.HasOpenEvent = true;
                fsm.AddSafeGlobalTransition("MP_OPEN", openState);
                fsm.InsertAction(openState, () => OnLocalDoorAction(tracked, true), 0, autoFinish: false);
            }

            if (!string.IsNullOrEmpty(closeState))
            {
                tracked.HasCloseEvent = true;
                fsm.AddSafeGlobalTransition("MP_CLOSE", closeState);
                fsm.InsertAction(closeState, () => OnLocalDoorAction(tracked, false), 0, autoFinish: false);
            }

            if (fsm.HasState("Open door 2"))
            {
                fsm.InsertAction("Open door 2", () => OnLocalDragOwnershipCheck(tracked), 0, autoFinish: false);
            }
            else if (fsm.HasState("Move"))
            {
                fsm.InsertAction("Move", () => OnLocalDragOwnershipCheck(tracked), 0, autoFinish: false);
            }
        }

        private void SetupStandardDoor(TrackedVehicleDoor tracked, Rigidbody vehMainRb)
        {
            PlayMakerFSM fsm = tracked.Fsm;
            tracked.DoorOpenVar = fsm.FsmVariables.FindFsmBool("Open");
            tracked.IsOpen = tracked.DoorOpenVar != null && tracked.DoorOpenVar.Value;

            FsmGameObject doorGoVar = fsm.FsmVariables.FindFsmGameObject("Door");
            FsmGameObject bootVar = fsm.FsmVariables.FindFsmGameObject("Bootlid");

            if (doorGoVar != null && doorGoVar.Value != null)
            {
                tracked.DoorObject = doorGoVar.Value;
            }
            else if (bootVar != null && bootVar.Value != null)
            {
                tracked.DoorObject = bootVar.Value;
            }
            else
            {
                tracked.DoorObject = fsm.gameObject;
            }

            BindDoorRigidbody(tracked, vehMainRb);

            string fsmNameLower = fsm.gameObject.name.ToLower();
            tracked.IsHoodOrBoot = fsm.HasState("Open hood")
                || (bootVar != null && bootVar.Value != null)
                || fsmNameLower.Contains("hood")
                || fsmNameLower.Contains("bootlid")
                || fsmNameLower.Contains("trunk");

            string openState = tracked.IsHoodOrBoot
                ? ResolveFirstExistingState(fsm, new[] { "Open hood", "Open door", "Open", "Open boot", "State 1" })
                : ResolveFirstExistingState(fsm, new[] { "Open door", "Open", "Open hood", "State 1" });

            string closeState = tracked.IsHoodOrBoot
                ? ResolveFirstExistingState(fsm, new[] { "Drop", "State 2", "Sound", "Close door", "Close" })
                : ResolveFirstExistingState(fsm, new[] { "Sound", "Close door", "Shut", "Drop", "State 2", "Close" });

            tracked.Axis = DetectDoorRotationAxis(fsm, tracked.DoorObject, openState, closeState);
            if (!tracked.IsOpen)
            {
                tracked.ClosedAngle = GetDoorAngle(tracked);
                tracked.HasClosedAngle = true;
            }

            if (!string.IsNullOrEmpty(openState))
            {
                tracked.HasOpenEvent = true;
                fsm.AddSafeGlobalTransition("MP_OPEN", openState);
                fsm.InsertAction(openState, () => OnLocalDoorAction(tracked, true), 0, autoFinish: false);
            }

            if (!string.IsNullOrEmpty(closeState))
            {
                tracked.HasCloseEvent = true;
                fsm.AddSafeGlobalTransition("MP_CLOSE", closeState);
                fsm.InsertAction(closeState, () => OnLocalDoorAction(tracked, false), 0, autoFinish: false);
            }

            if (!tracked.IsHoodOrBoot)
            {
                if (fsm.HasState("Open door 2"))
                {
                    fsm.InsertAction("Open door 2", () => OnLocalDragOwnershipCheck(tracked), 0, autoFinish: false);
                }
                else if (fsm.HasState("Open door 3"))
                {
                    fsm.InsertAction("Open door 3", () => OnLocalDragOwnershipCheck(tracked), 0, autoFinish: false);
                }
            }
        }

        private void BindDoorRigidbody(TrackedVehicleDoor tracked, Rigidbody vehMainRb)
        {
            if (tracked.DoorObject == null) return;

            Rigidbody doorRb = tracked.DoorObject.GetComponent<Rigidbody>();
            if (doorRb == null || doorRb == vehMainRb) return;

            // Запрещено захватывать MainRigidbody шасси — берём только Rigidbody самой двери
            if (ctx.Ownership.TryClaim(doorRb, ModuleId, $"VehicleDoorRb:{tracked.VehicleKey}:{tracked.DoorObject.name}"))
            {
                tracked.DoorRb = doorRb;
                tracked.DefaultKinematic = doorRb.isKinematic;
                tracked.CanControlRb = true;
            }
            else
            {
                tracked.DoorRb = doorRb;
                tracked.DefaultKinematic = doorRb.isKinematic;
                tracked.CanControlRb = false;
            }
        }

        private static string ResolveFirstExistingState(PlayMakerFSM fsm, string[] candidates)
        {
            if (fsm == null || candidates == null) return null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (fsm.HasState(candidates[i])) return candidates[i];
            }
            return null;
        }

        private static int DetectDoorRotationAxis(PlayMakerFSM fsm, GameObject doorObj, string openState, string closeState)
        {
            string[] statesToScan = new[] { openState, "Open door 2", "Open door 3", closeState };
            for (int s = 0; s < statesToScan.Length; s++)
            {
                if (string.IsNullOrEmpty(statesToScan[s])) continue;
                FsmState state = fsm.GetState(statesToScan[s]);
                if (state == null || state.Actions == null) continue;

                for (int a = 0; a < state.Actions.Length; a++)
                {
                    if (state.Actions[a] is GetRotation getRot)
                    {
                        if (getRot.xAngle != null && !getRot.xAngle.IsNone) return 0;
                        if (getRot.yAngle != null && !getRot.yAngle.IsNone) return 1;
                        if (getRot.zAngle != null && !getRot.zAngle.IsNone) return 2;
                    }
                }
            }

            if (doorObj != null)
            {
                HingeJoint hinge = doorObj.GetComponent<HingeJoint>();
                if (hinge != null)
                {
                    Vector3 axis = hinge.axis;
                    if (Mathf.Abs(axis.x) >= Mathf.Abs(axis.y) && Mathf.Abs(axis.x) >= Mathf.Abs(axis.z)) return 0;
                    if (Mathf.Abs(axis.y) >= Mathf.Abs(axis.z)) return 1;
                    return 2;
                }
            }

            return 1;
        }

        private void OnLocalDragOwnershipCheck(TrackedVehicleDoor door)
        {
            if (door == null || ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            // Защита от ложного перехвата при автоматическом переходе FSM после удалённого MP_OPEN
            if (Time.time - door.LastRemoteToggleTime <= 0.35f) return;

            bool interacting = Input.GetMouseButton(0) || Input.GetMouseButtonDown(0) || Input.GetMouseButton(1);
            if (!interacting && Time.time - door.LastLocalActionTime > 0.25f) return;

            RestoreDoorPhysics(door);
            RequestDoorOwnership(door);
        }

        private void OnLocalDoorAction(TrackedVehicleDoor door, bool open)
        {
            if (door == null) return;

            if (door.SuppressEcho)
            {
                door.SuppressEcho = false;
                if (Time.time <= door.SuppressEchoUntil)
                {
                    return;
                }
            }

            door.IsOpen = open;
            door.LastLocalActionTime = Time.time;
            if (!open)
            {
                door.LastClosedTransitionTime = Time.time;
            }
            door.HasRemoteTarget = false;
            RestoreDoorPhysics(door);

            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            door.LastSentAngle = -999f;
            door.HasLastSentLocalPos = false;
            if (!open)
            {
                door.TargetAngle = -999f;
            }

            RequestDoorOwnership(door);
            SendDoorToggle(door.Hash, LocalPlayerId, open);
        }

        private void RequestDoorOwnership(TrackedVehicleDoor door)
        {
            if (door == null || ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            int myId = LocalPlayerId;
            if (door.OwnerId == myId) return;

            door.OwnerId = myId;
            SendDoorOwnership(door.Hash, myId);
        }

        private void DetectLocalChassisJumps()
        {
            foreach (var kv in vehicleRoots)
            {
                TrackedVehicleRoot root = kv.Value;
                if (root == null || root.Transform == null) continue;

                Vector3 curPos = root.Transform.position;
                if (root.HasLastPosition)
                {
                    if ((curPos - root.LastPosition).sqrMagnitude > TeleportJumpThresholdSqr)
                    {
                        HandleChassisTeleportOrSpike(root.Key, root.Transform);
                    }
                }
                root.LastPosition = curPos;
                root.HasLastPosition = true;
            }
        }

        private void HandleVehicleTeleportEvent(VehicleTeleportEvent evt)
        {
            string matchedKey = null;
            foreach (var kv in vehicleRoots)
            {
                TrackedVehicleRoot root = kv.Value;
                if (root == null) continue;

                bool matchByTransform = evt.VehicleTransform != null && root.Transform == evt.VehicleTransform;
                bool matchById = evt.VehicleId == root.VehicleId;
                if (matchByTransform || matchById)
                {
                    matchedKey = root.Key;
                    if (root.Transform != null)
                    {
                        root.LastPosition = root.Transform.position;
                        root.HasLastPosition = true;
                    }
                    break;
                }
            }

            if (!string.IsNullOrEmpty(matchedKey) || evt.VehicleTransform != null)
            {
                HandleChassisTeleportOrSpike(matchedKey, evt.VehicleTransform);
            }
        }

        private void HandleChassisTeleportOrSpike(string vehicleKey, Transform vehTransform)
        {
            float now = Time.time;
            int myId = LocalPlayerId;

            for (int i = 0; i < doorList.Count; i++)
            {
                TrackedVehicleDoor d = doorList[i];
                if (d == null) continue;

                bool matchesDoor = false;
                if (d.VehicleKey == "Satsuma")
                {
                    // Съёмные панели Satsuma участвуют в защите от телепорта только если реально установлены на кузове
                    matchesDoor = vehTransform != null && d.DoorObject != null && d.DoorObject.transform.IsChildOf(vehTransform);
                }
                else
                {
                    matchesDoor = (!string.IsNullOrEmpty(vehicleKey) && d.VehicleKey == vehicleKey)
                        || (vehTransform != null && d.DoorObject != null && d.DoorObject.transform.IsChildOf(vehTransform));
                }

                if (!matchesDoor) continue;

                if (d.DoorRb != null && !d.DoorRb.isKinematic)
                {
                    d.DoorRb.velocity = Vector3.zero;
                    d.DoorRb.angularVelocity = Vector3.zero;
                }

                d.SmoothVelocity = 0f;
                d.SuppressOutboundUntil = now + TeleportSuppressSeconds;

                if (d.IsHayosikoSidedoor)
                {
                    d.LastSentLocalPos = GetDoorLocalPos(d);
                    d.HasLastSentLocalPos = true;
                }
                else
                {
                    d.LastSentAngle = GetDoorAngle(d);
                }

                if (d.OwnerId != myId && d.HasRemoteTarget)
                {
                    d.SuppressRemoteInterpUntil = now + 0.10f;
                    if (d.IsHayosikoSidedoor)
                    {
                        SetDoorLocalPos(d, d.TargetLocalPos);
                    }
                    else if (d.TargetAngle > -900f)
                    {
                        SetDoorAngle(d, d.TargetAngle);
                    }
                }
            }
        }

        private void SyncOpenDoorChanges()
        {
            int myId = LocalPlayerId;
            float now = Time.time;

            for (int i = 0; i < doorList.Count; i++)
            {
                TrackedVehicleDoor d = doorList[i];
                if (d == null || d.DoorObject == null) continue;
                if (now < d.SuppressOutboundUntil) continue;
                if (d.OwnerId != myId) continue;

                bool isOpen = d.DoorOpenVar != null ? d.DoorOpenVar.Value : d.IsOpen;
                if (!isOpen) continue;

                if (d.IsHayosikoSidedoor)
                {
                    Vector3 curPos = GetDoorLocalPos(d);
                    if (!d.HasLastSentLocalPos || (curPos - d.LastSentLocalPos).magnitude > 0.005f)
                    {
                        d.LastSentLocalPos = curPos;
                        d.HasLastSentLocalPos = true;
                        SendDoorLinearPosition(d.Hash, myId, curPos);
                    }
                }
                else
                {
                    float curAngle = GetDoorAngle(d);
                    if (d.LastSentAngle < -900f || Mathf.Abs(Mathf.DeltaAngle(curAngle, d.LastSentAngle)) > 0.25f)
                    {
                        d.LastSentAngle = curAngle;
                        SendDoorRotation(d.Hash, myId, curAngle);
                    }
                }
            }
        }

        private void ApplyRemoteDoorInterpolation()
        {
            int myId = LocalPlayerId;
            float now = Time.time;

            for (int i = 0; i < doorList.Count; i++)
            {
                TrackedVehicleDoor d = doorList[i];
                if (d == null || d.DoorObject == null) continue;

                bool isOpen = d.DoorOpenVar != null ? d.DoorOpenVar.Value : d.IsOpen;
                TryLearnClosedTransform(d, isOpen, now);

                if (d.OwnerId == myId)
                {
                    if (d.IsApplyingRemote)
                    {
                        RestoreDoorPhysics(d);
                    }
                    continue;
                }

                if (!isOpen || !d.HasRemoteTarget)
                {
                    if (d.IsApplyingRemote)
                    {
                        RestoreDoorPhysics(d);
                    }
                    continue;
                }

                if (now < d.SuppressRemoteInterpUntil)
                {
                    continue;
                }

                d.IsApplyingRemote = true;
                if (d.CanControlRb && d.DoorRb != null && !d.DoorRb.isKinematic)
                {
                    d.DoorRb.isKinematic = true;
                }

                if (d.IsHayosikoSidedoor)
                {
                    Vector3 cur = GetDoorLocalPos(d);
                    float dist = Vector3.Distance(cur, d.TargetLocalPos);
                    if (dist > 0.002f)
                    {
                        float step = Mathf.Max(0.8f, dist * 16f) * Time.fixedDeltaTime;
                        Vector3 next = Vector3.MoveTowards(cur, d.TargetLocalPos, step);
                        SetDoorLocalPos(d, next);
                    }
                    else
                    {
                        SetDoorLocalPos(d, d.TargetLocalPos);
                    }
                }
                else
                {
                    if (d.TargetAngle < -900f) continue;

                    float cur = GetDoorAngle(d);
                    float delta = Mathf.Abs(Mathf.DeltaAngle(cur, d.TargetAngle));
                    if (delta > 0.08f)
                    {
                        float next = Mathf.SmoothDampAngle(cur, d.TargetAngle, ref d.SmoothVelocity, 0.05f, 540f, Time.fixedDeltaTime);
                        SetDoorAngle(d, next);
                    }
                    else
                    {
                        SetDoorAngle(d, d.TargetAngle);
                        d.SmoothVelocity = 0f;
                    }
                }
            }
        }

        private static void TryLearnClosedTransform(TrackedVehicleDoor d, bool isOpen, float now)
        {
            if (isOpen || d.IsOpen) return;
            if (d.DoorOpenVar != null && d.DoorOpenVar.Value) return;
            if (now - d.LastClosedTransitionTime <= 0.35f) return;

            if (d.IsHayosikoSidedoor)
            {
                if (!d.HasClosedLocalPos)
                {
                    d.ClosedLocalPos = GetDoorLocalPos(d);
                    d.HasClosedLocalPos = true;
                }
            }
            else if (!d.HasClosedAngle)
            {
                d.ClosedAngle = GetDoorAngle(d);
                d.HasClosedAngle = true;
            }
        }

        private static void RestoreDoorPhysics(TrackedVehicleDoor d)
        {
            if (d == null) return;
            d.IsApplyingRemote = false;
            d.SmoothVelocity = 0f;

            if (!d.CanControlRb || d.DoorRb == null) return;
            d.DoorRb.isKinematic = d.DefaultKinematic;
            if (!d.DoorRb.isKinematic)
            {
                d.DoorRb.velocity = Vector3.zero;
                d.DoorRb.angularVelocity = Vector3.zero;
            }
        }

        private static float GetDoorAngle(TrackedVehicleDoor d)
        {
            if (d == null || d.DoorObject == null) return 0f;
            Vector3 euler = d.DoorObject.transform.localEulerAngles;
            return d.Axis == 0 ? euler.x : (d.Axis == 1 ? euler.y : euler.z);
        }

        private static void SetDoorAngle(TrackedVehicleDoor d, float angle)
        {
            if (d == null || d.DoorObject == null) return;
            Vector3 euler = d.DoorObject.transform.localEulerAngles;
            if (d.Axis == 0) euler.x = angle;
            else if (d.Axis == 1) euler.y = angle;
            else euler.z = angle;
            d.DoorObject.transform.localEulerAngles = euler;
        }

        private static Vector3 GetDoorLocalPos(TrackedVehicleDoor d)
        {
            if (d == null || d.DoorObject == null) return Vector3.zero;
            return d.DoorObject.transform.localPosition;
        }

        private static void SetDoorLocalPos(TrackedVehicleDoor d, Vector3 localPos)
        {
            if (d == null || d.DoorObject == null) return;
            d.DoorObject.transform.localPosition = localPos;
        }

        private void SendDoorToggle(int hash, int senderId, bool open)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleDoorToggle))
            {
                p.WriteInt32(hash);
                p.WriteInt32(senderId);
                p.WriteBoolean(open);
                ctx.Transport.Send(p);
            }
        }

        private void SendDoorRotation(int hash, int senderId, float angle)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleDoorRotation))
            {
                p.WriteInt32(hash);
                p.WriteInt32(senderId);
                p.WriteByte(ModeAngular);
                p.WriteSingle(angle);
                ctx.Transport.Send(p);
            }
        }

        private void SendDoorLinearPosition(int hash, int senderId, Vector3 localPos)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleDoorRotation))
            {
                p.WriteInt32(hash);
                p.WriteInt32(senderId);
                p.WriteByte(ModeLinear);
                p.WriteVector3(localPos);
                ctx.Transport.Send(p);
            }
        }

        private void SendDoorOwnership(int hash, int ownerId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleDoorOwnership))
            {
                p.WriteInt32(hash);
                p.WriteInt32(ownerId);
                ctx.Transport.Send(p);
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            SendDoorSnapshotToClient(clientId);
            pendingSnapshots.RemoveAll(s => s.ClientId == clientId);
            pendingSnapshots.Add(new PendingDoorSnapshot { ClientId = clientId, SendAtTime = Time.time + 2.0f });
        }

        private void HandleClientDisconnected(int clientId)
        {
            bool isClient = ctx?.Transport != null && ctx.Transport.IsClient;
            pendingSnapshots.RemoveAll(s => s.ClientId == clientId);

            for (int i = 0; i < doorList.Count; i++)
            {
                TrackedVehicleDoor d = doorList[i];
                if (d == null) continue;

                if (isClient || d.OwnerId == clientId)
                {
                    d.OwnerId = 0;
                    d.HasRemoteTarget = false;
                    RestoreDoorPhysics(d);
                }
            }
        }

        private void ProcessPendingSnapshots()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;
            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                if (Time.time >= pendingSnapshots[i].SendAtTime)
                {
                    SendDoorSnapshotToClient(pendingSnapshots[i].ClientId);
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        private void SendDoorSnapshotToClient(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            if (doorList.Count == 0) return;

            using (var p = new PacketWriter(PacketType.VehicleDoorSnapshot))
            {
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(doorList.Count);
                for (int i = 0; i < doorList.Count; i++)
                {
                    TrackedVehicleDoor d = doorList[i];
                    bool isOpen = d.DoorOpenVar != null ? d.DoorOpenVar.Value : d.IsOpen;
                    p.WriteInt32(d.Hash);
                    p.WriteInt32(d.OwnerId);
                    p.WriteBoolean(isOpen);
                    if (d.IsHayosikoSidedoor)
                    {
                        p.WriteByte(ModeLinear);
                        p.WriteVector3(GetDoorLocalPos(d));
                    }
                    else
                    {
                        p.WriteByte(ModeAngular);
                        p.WriteSingle(GetDoorAngle(d));
                    }
                }
                ctx.Transport.SendTo(p, clientId);
            }
        }

        private void HandleDoorToggle(PacketReader reader, int connectionId)
        {
            int hash = reader.ReadInt32();
            int senderId = reader.ReadInt32();
            bool open = reader.ReadBoolean();

            if (senderId == LocalPlayerId) return;
            if (!trackedDoors.TryGetValue(hash, out TrackedVehicleDoor door)) return;

            door.OwnerId = senderId;
            door.IsOpen = open;
            door.LastSentAngle = -999f;
            door.HasLastSentLocalPos = false;

            if (!open)
            {
                door.HasRemoteTarget = false;
                door.TargetAngle = -999f;
                door.LastClosedTransitionTime = Time.time;
                RestoreDoorPhysics(door);
                if (door.IsHayosikoSidedoor && door.HasClosedLocalPos)
                {
                    SetDoorLocalPos(door, door.ClosedLocalPos);
                }
                else if (!door.IsHayosikoSidedoor && door.HasClosedAngle)
                {
                    SetDoorAngle(door, door.ClosedAngle);
                }
            }
            else
            {
                door.LastRemoteToggleTime = Time.time;
            }

            TriggerDoorFsmEvent(door, open ? "MP_OPEN" : "MP_CLOSE");
        }

        private void HandleDoorRotation(PacketReader reader, int connectionId)
        {
            int hash = reader.ReadInt32();
            int senderId = reader.ReadInt32();
            byte mode = reader.ReadByte();

            float angle = 0f;
            Vector3 localPos = Vector3.zero;
            if (mode == ModeAngular)
            {
                angle = reader.ReadSingle();
            }
            else if (mode == ModeLinear)
            {
                localPos = reader.ReadVector3();
            }
            else
            {
                return;
            }

            if (senderId == LocalPlayerId) return;
            if (!trackedDoors.TryGetValue(hash, out TrackedVehicleDoor door) || door.DoorObject == null) return;
            if (door.OwnerId == LocalPlayerId) return;

            bool isOpen = door.DoorOpenVar != null ? door.DoorOpenVar.Value : door.IsOpen;
            if (!isOpen) return;

            if (door.IsHayosikoSidedoor)
            {
                if (mode != ModeLinear) return;
                door.TargetLocalPos = localPos;
                door.HasRemoteTarget = true;
                door.LastRemoteUpdateTime = Time.time;
            }
            else
            {
                if (mode != ModeAngular) return;
                door.TargetAngle = angle;
                door.HasRemoteTarget = true;
                door.LastRemoteUpdateTime = Time.time;
            }
        }

        private void HandleDoorOwnership(PacketReader reader, int connectionId)
        {
            int hash = reader.ReadInt32();
            int ownerId = reader.ReadInt32();

            if (!trackedDoors.TryGetValue(hash, out TrackedVehicleDoor door)) return;

            door.OwnerId = ownerId;
            if (ownerId == LocalPlayerId)
            {
                door.HasRemoteTarget = false;
                RestoreDoorPhysics(door);
            }
        }

        private void HandleDoorSnapshot(PacketReader reader, int connectionId)
        {
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            int count = reader.ReadInt32();
            float now = Time.time;

            for (int i = 0; i < count; i++)
            {
                int hash = reader.ReadInt32();
                int ownerId = reader.ReadInt32();
                bool isOpen = reader.ReadBoolean();
                byte mode = reader.ReadByte();

                float angle = 0f;
                Vector3 localPos = Vector3.zero;
                if (mode == ModeLinear)
                {
                    localPos = reader.ReadVector3();
                }
                else
                {
                    angle = reader.ReadSingle();
                }

                if (!trackedDoors.TryGetValue(hash, out TrackedVehicleDoor door)) continue;

                bool currentOpen = door.DoorOpenVar != null ? door.DoorOpenVar.Value : door.IsOpen;
                door.OwnerId = ownerId;
                door.IsOpen = isOpen;
                door.LastSentAngle = -999f;
                door.HasLastSentLocalPos = false;
                if (isOpen)
                {
                    door.LastRemoteToggleTime = now;
                    if (!currentOpen)
                    {
                        TriggerDoorFsmEvent(door, "MP_OPEN");
                    }

                    if (door.IsHayosikoSidedoor && mode == ModeLinear)
                    {
                        door.TargetLocalPos = localPos;
                        door.HasRemoteTarget = true;
                        door.LastRemoteUpdateTime = now;
                        SetDoorLocalPos(door, localPos);
                    }
                    else if (!door.IsHayosikoSidedoor && mode == ModeAngular)
                    {
                        door.TargetAngle = angle;
                        door.HasRemoteTarget = true;
                        door.LastRemoteUpdateTime = now;
                        SetDoorAngle(door, angle);
                    }

                    if (door.DoorRb != null && !door.DoorRb.isKinematic)
                    {
                        door.DoorRb.velocity = Vector3.zero;
                        door.DoorRb.angularVelocity = Vector3.zero;
                    }
                }
                else
                {
                    door.HasRemoteTarget = false;
                    door.TargetAngle = -999f;
                    door.LastClosedTransitionTime = now;
                    RestoreDoorPhysics(door);
                    if (door.IsHayosikoSidedoor && door.HasClosedLocalPos)
                    {
                        SetDoorLocalPos(door, door.ClosedLocalPos);
                    }
                    else if (!door.IsHayosikoSidedoor && door.HasClosedAngle)
                    {
                        SetDoorAngle(door, door.ClosedAngle);
                    }

                    if (currentOpen)
                    {
                        TriggerDoorFsmEvent(door, "MP_CLOSE");
                    }
                }
            }
        }

        private static void TriggerDoorFsmEvent(TrackedVehicleDoor door, string eventName)
        {
            if (door == null || door.Fsm == null) return;

            bool hasTarget = (eventName == "MP_OPEN" && door.HasOpenEvent) || (eventName == "MP_CLOSE" && door.HasCloseEvent);
            if (!hasTarget)
            {
                door.SuppressEcho = false;
                return;
            }

            if (!door.Fsm.gameObject.activeInHierarchy || !door.Fsm.enabled)
            {
                door.SuppressEcho = false;
                return;
            }

            door.SuppressEcho = true;
            door.SuppressEchoUntil = Time.time + 0.15f;
            door.Fsm.SendEvent(eventName);
        }
    }
}
