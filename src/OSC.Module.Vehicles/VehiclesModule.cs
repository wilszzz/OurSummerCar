using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using OSC.Core.Utilities;
using UnityEngine;

namespace OSC.Module.Vehicles
{
    public sealed class VehiclesModule : IOscModule
    {
        private sealed class RemoteSeatAssignment
        {
            public int PlayerId;
            public bool IsDriver;
            public byte VehicleId;
            public int SeatIndex;
        }

        private sealed class PendingSnapshotSchedule
        {
            public int ClientId;
            public float NextSendTime;
            public int Stage; // 0 -> +1.5с, 1 -> +4.0с
        }

        public string ModuleId => "Vehicles";
        public string DisplayName => "Транспорт и Пассажиры";
        public string StatusSummary
        {
            get
            {
                int bound = 0;
                for (int i = 0; i < vehicles.Count; i++)
                {
                    if (vehicles[i].IsBound) bound++;
                }

                string role = localDrivenVehicle != null
                    ? $"За рулём ({localDrivenVehicle.Kind})"
                    : (passengerSystem.IsLocalSeated ? "Пассажир" : "Пешком");
                return $"Авто: {bound}/{vehicles.Count}, {role}";
            }
        }

        private OscHostContext ctx;
        private readonly List<TrackedVehicle> vehicles = new List<TrackedVehicle>();
        private readonly Dictionary<byte, TrackedVehicle> vehiclesById = new Dictionary<byte, TrackedVehicle>();
        private readonly Dictionary<int, RemoteSeatAssignment> remoteSeats = new Dictionary<int, RemoteSeatAssignment>();
        private readonly List<PendingSnapshotSchedule> pendingSnapshots = new List<PendingSnapshotSchedule>();
        private readonly Dictionary<byte, Dictionary<string, VehiclePhysicsAuthorityEvent>> authorityOverrides =
            new Dictionary<byte, Dictionary<string, VehiclePhysicsAuthorityEvent>>();

        private readonly CollisionAndExitGuard collisionGuard = new CollisionAndExitGuard();
        private readonly PassengerSeatSystem passengerSystem = new PassengerSeatSystem();

        private TrackedVehicle localDrivenVehicle;
        private float localDriveEnterTime;
        private float nextDiscoveryTime;
        private float nextSeatRefreshTime;

        private Transform playerTr;
        private GameObject handPushObject;
        private FsmBool playerInCarVar;
        private bool missingPlayerInCarLogged;

        private int LocalPlayerId => (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;
        private bool IsNetworkRunning => ctx != null && ctx.Transport != null && ctx.Transport.IsRunning;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            passengerSystem.Initialize(collisionGuard, ctx.Log);
            passengerSystem.OnLocalPassengerStateChanged += HandleLocalPassengerStateChanged;

            ctx.Router.Register(PacketType.VehiclePhysicsSync, ModuleId, HandleVehiclePhysicsSync, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleDrivingMode, ModuleId, HandleVehicleDrivingMode, relayOnServer: true);
            ctx.Router.Register(PacketType.VehiclePassengerSeat, ModuleId, HandleVehiclePassengerSeat, relayOnServer: true);
            ctx.Router.Register(PacketType.VehiclePassengerSnapshot, ModuleId, HandleVehiclePassengerSnapshot, relayOnServer: false);
            ctx.Router.Register(PacketType.VehicleControlSync, ModuleId, HandleVehicleControlSync, relayOnServer: true);

            ctx.Events.Subscribe<SpecialObjectCarryEvent>(HandleSpecialObjectCarry);
            ctx.Events.Subscribe<VehiclePhysicsAuthorityEvent>(HandleVehiclePhysicsAuthority);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
        }

        public void OnMenuLoaded() => ResetWorldState();

        public void OnLevelLoaded()
        {
            ResetWorldState();
            BuildVehicleRegistry();
            DiscoverVehiclesAndPlayer();
        }

        public void OnShutdown()
        {
            passengerSystem.OnLocalPassengerStateChanged -= HandleLocalPassengerStateChanged;
            ctx?.Events?.Unsubscribe<SpecialObjectCarryEvent>(HandleSpecialObjectCarry);
            ctx?.Events?.Unsubscribe<VehiclePhysicsAuthorityEvent>(HandleVehiclePhysicsAuthority);

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetWorldState();
        }

        private void ResetWorldState()
        {
            passengerSystem.Reset();
            collisionGuard.Reset();

            vehicles.Clear();
            vehiclesById.Clear();
            remoteSeats.Clear();
            pendingSnapshots.Clear();
            authorityOverrides.Clear();

            localDrivenVehicle = null;
            localDriveEnterTime = 0f;
            nextDiscoveryTime = 0f;
            nextSeatRefreshTime = 0f;
            playerTr = null;
            handPushObject = null;
            playerInCarVar = null;
            missingPlayerInCarLogged = false;
        }

        private void BuildVehicleRegistry()
        {
            vehicles.Clear();
            vehiclesById.Clear();

            for (int i = 0; i < VehicleDefinitions.All.Length; i++)
            {
                var tracked = new TrackedVehicle(VehicleDefinitions.All[i]);
                vehicles.Add(tracked);
                vehiclesById[tracked.Id] = tracked;
            }
        }

        public void OnUpdate()
        {
            if (Time.time >= nextDiscoveryTime)
            {
                nextDiscoveryTime = Time.time + 1.5f;
                DiscoverVehiclesAndPlayer();
            }

            CheckDrivingWatchdog();
            CheckHandPush();

            passengerSystem.TickUpdate(localDrivenVehicle != null, vehicles);
            collisionGuard.TickUpdate(vehicles, playerTr);

            float dt = Time.deltaTime;
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (!veh.IsBound) continue;

                if (veh.LocalPushTimer > 0f)
                {
                    veh.LocalPushTimer -= dt;
                    if (veh.LocalPushTimer <= 0f)
                    {
                        veh.LocalPushTimer = 0f;
                        if (IsNetworkRunning && !veh.OverrideSuppressWorldTransformSync && IsLocalPhysicsAuthority(veh))
                        {
                            SendVehiclePhysicsSync(veh, true);
                        }
                    }
                }

                if (veh.RemotePushTimer > 0f)
                {
                    veh.RemotePushTimer -= dt;
                    if (veh.RemotePushTimer <= 0f)
                    {
                        veh.RemotePushTimer = 0f;
                        UpdateVehicleKinematicOwnership(veh);
                    }
                }

                Vector3 curPos = veh.Transform.position;
                if (!veh.HasTrackedPosition)
                {
                    veh.LastTrackedPosition = curPos;
                    veh.HasTrackedPosition = true;
                }
                else
                {
                    if (IsLocalPhysicsAuthority(veh) && (curPos - veh.LastTrackedPosition).sqrMagnitude > 100f)
                    {
                        veh.ZeroAllManagedVelocities();
                        veh.SteeringTeleportGuardUntil = Time.time + 0.35f;
                        veh.LastPhysicsSendTime = 0f; // Обход 1 Гц троттлинга в покое
                        if (IsNetworkRunning && !veh.OverrideSuppressWorldTransformSync)
                        {
                            SendVehiclePhysicsSync(veh, false);
                        }
                        PublishTeleportEvent(veh, curPos, isLocalInitiated: true);
                    }
                    veh.LastTrackedPosition = curPos;
                }

                veh.StepRemoteControls(dt);
            }

            if (Time.time >= nextSeatRefreshTime)
            {
                nextSeatRefreshTime = Time.time + 0.5f;
                RefreshRemoteSeatBindings();
            }

            if (IsNetworkRunning)
            {
                ProcessPendingSnapshots();
                StreamOwnedVehiclesPhysics();
                StreamLocalDriverControls();
            }
        }

        public void OnFixedUpdate()
        {
            passengerSystem.TickFixedUpdate();
            collisionGuard.TickGroundGuard();
        }

        public void OnLateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (!veh.IsBound) continue;
                if (ShouldInterpolateRemotely(veh))
                {
                    if (veh.StepRemoteTransform(dt))
                    {
                        PublishTeleportEvent(veh, veh.TargetPosition, isLocalInitiated: false);
                    }
                }
            }

            // Строго ПОСЛЕ интерполяции транспорта обновляем камеру пассажира, чтобы полностью убрать дрожание
            passengerSystem.TickLateUpdate();
            collisionGuard.TickGroundGuard();
        }

        private void DiscoverVehiclesAndPlayer()
        {
            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null && playerInCarVar == null)
            {
                playerInCarVar = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerInCar");
            }

            if (playerTr == null || !playerTr.gameObject.activeInHierarchy)
            {
                GameObject pObj = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (pObj != null)
                {
                    playerTr = pObj.transform;
                    Transform hp = pObj.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/Hand Push")
                        ?? pObj.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Hand Push");
                    if (hp != null) handPushObject = hp.gameObject;
                }
            }

            if (handPushObject == null)
            {
                handPushObject = GameObject.Find("Hand Push");
            }

            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (veh.IsBound)
                {
                    if (veh.SteeringWheelTransform == null && !string.IsNullOrEmpty(veh.Spec.SteeringWheelPath))
                    {
                        veh.ResolveSteeringTransform();
                    }
                    continue;
                }

                GameObject found = VehicleDefinitions.FindVehicleObject(veh.Spec);
                if (found == null) continue;

                veh.Bind(found, ctx.Log);

                if (veh.MainRigidbody != null)
                {
                    ctx.Ownership.TryClaim(veh.MainRigidbody, ModuleId, $"Vehicle_{veh.Kind}");
                }

                if (veh.Spec.HasDriverSeat)
                {
                    HookDrivingFsm(veh);
                }

                passengerSystem.EnsureSeatsForVehicle(veh);
                ApplyResolvedAuthorityOverride(veh);
                UpdateVehicleKinematicOwnership(veh);
                ctx.Log?.Invoke($"[Транспорт] Подключён {veh.GameObject.name} (ID={veh.Id}, сидений={veh.PassengerSeats.Count})");
            }
        }

        private void HookDrivingFsm(TrackedVehicle veh)
        {
            if (veh == null || !veh.IsBound) return;

            PlayMakerFSM drivingFsm = null;
            var fsms = veh.GameObject.GetComponentsInChildren<PlayMakerFSM>(true);
            for (int i = 0; i < fsms.Length; i++)
            {
                PlayMakerFSM fsm = fsms[i];
                if (fsm == null) continue;

                if (fsm.gameObject.name == "PassengerTrigger" || fsm.gameObject.name.StartsWith("OSC_PassengerSeat"))
                {
                    continue;
                }

                bool hasEnter = fsm.HasState("Reset view") || fsm.HasState("Player in car");
                bool hasExit = fsm.HasState("Create player");
                if (fsm.gameObject.name == "DriveTrigger" && (hasEnter || hasExit))
                {
                    drivingFsm = fsm;
                    break;
                }
                if (drivingFsm == null && hasEnter && hasExit)
                {
                    drivingFsm = fsm;
                }
            }

            if (drivingFsm == null) return;
            if (!ctx.Ownership.TryClaim(drivingFsm, ModuleId, $"DriveTrigger_{veh.Kind}")) return;

            drivingFsm.InitializeSafe();
            veh.DrivingFsm = drivingFsm;

            if (drivingFsm.HasState("Press return"))
            {
                drivingFsm.InsertAction("Press return", () =>
                {
                    if (veh.IsDrivenRemotely || passengerSystem.IsLocalSeated)
                    {
                        drivingFsm.SendEvent("FINISHED");
                    }
                }, 0, false);
            }

            if (drivingFsm.HasState("Reset view"))
            {
                drivingFsm.InsertAction("Reset view", () => HandleLocalDrivingEntered(veh), 0, false);
            }
            else if (drivingFsm.HasState("Player in car"))
            {
                drivingFsm.InsertAction("Player in car", () => HandleLocalDrivingEntered(veh), 0, false);
            }

            if (drivingFsm.HasState("Create player"))
            {
                drivingFsm.InsertAction("Create player", () => HandleLocalDrivingExited(veh), 0, false);
            }
        }

        private void CheckDrivingWatchdog()
        {
            if (localDrivenVehicle == null) return;

            if (!localDrivenVehicle.IsBound)
            {
                HandleLocalDrivingExited(localDrivenVehicle);
                return;
            }

            // Даём 0.45с на переход FSM после входа
            if (Time.time - localDriveEnterTime < 0.45f) return;

            if (playerInCarVar != null)
            {
                if (!playerInCarVar.Value)
                {
                    HandleLocalDrivingExited(localDrivenVehicle);
                }
            }
            else if (!missingPlayerInCarLogged)
            {
                missingPlayerInCarLogged = true;
                ctx.Log?.Invoke("[Транспорт] Предупреждение: глобальная переменная PlayerInCar не найдена.");
            }
        }

        private void HandleLocalDrivingEntered(TrackedVehicle veh)
        {
            if (veh == null || veh.IsDrivenLocally) return;

            if (passengerSystem.IsLocalSeated)
            {
                passengerSystem.ForceExitLocal(teleportToSafeExit: false);
            }

            localDrivenVehicle = veh;
            localDriveEnterTime = Time.time;

            veh.IsDrivenLocally = true;
            veh.DriverPlayerId = LocalPlayerId;
            veh.OwnerPlayerId = LocalPlayerId;
            veh.RemotePushTimer = 0f;
            veh.LocalPushTimer = 0f;

            veh.RestoreLocalSteeringControl();
            if (!veh.OverrideForceKinematic)
            {
                veh.SetKinematic(false);
                veh.SetInterpolationActive(true);
                veh.WakeUp();
            }

            collisionGuard.SetLocalVehicleCollisionsIgnored(veh, FindPlayerTransform(), true);

            ctx.Events.Publish(new LocalPlayerOccupancyEvent
            {
                IsSeated = true,
                IsDriver = true,
                VehicleId = veh.Id,
                VehicleTransform = veh.Transform
            });

            UpdateVehicleKinematicOwnership(veh);

            if (IsNetworkRunning && !veh.OverrideSuppressWorldTransformSync && IsLocalPhysicsAuthority(veh))
            {
                SendDrivingModePacket(veh.Id, true);
                SendVehiclePhysicsSync(veh, false);
            }
            else if (IsNetworkRunning)
            {
                SendDrivingModePacket(veh.Id, true);
            }

            ctx.Log?.Invoke($"[Транспорт] Локальный игрок сел за руль {veh.GameObject.name}");
        }

        private void HandleLocalDrivingExited(TrackedVehicle veh)
        {
            if (veh == null || !veh.IsDrivenLocally) return;

            // Сначала отправляем финальный снимок скорости и позиции, чтобы у хоста авто плавно катилось дальше
            if (IsNetworkRunning)
            {
                SendVehiclePhysicsSync(veh, false);
                SendDrivingModePacket(veh.Id, false);
            }

            veh.IsDrivenLocally = false;
            veh.DriverPlayerId = -1;
            veh.OwnerPlayerId = 0; // Суверенитет возвращается Хосту
            veh.SetInterpolationActive(false);

            if (localDrivenVehicle == veh)
            {
                localDrivenVehicle = null;
            }

            Transform pTr = FindPlayerTransform();
            collisionGuard.SetLocalVehicleCollisionsIgnored(veh, pTr, false, graceSecondsAfterExit: 0.65f);
            if (pTr != null)
            {
                collisionGuard.StartPostExitGroundGuard(pTr, veh, durationSeconds: 0.45f);
            }

            UpdateVehicleKinematicOwnership(veh);

            ctx.Events.Publish(new LocalPlayerOccupancyEvent
            {
                IsSeated = false,
                IsDriver = false,
                VehicleId = veh.Id,
                VehicleTransform = veh.Transform
            });

            ctx.Log?.Invoke($"[Транспорт] Локальный игрок покинул руль {veh.GameObject.name}");
        }

        private void HandleLocalPassengerStateChanged(PassengerSeatRuntime seat, bool entered)
        {
            if (seat == null || seat.Vehicle == null) return;

            TrackedVehicle veh = seat.Vehicle;
            ctx.Events.Publish(new LocalPlayerOccupancyEvent
            {
                IsSeated = entered,
                IsDriver = false,
                VehicleId = veh.Id,
                VehicleTransform = veh.Transform
            });

            UpdateVehicleKinematicOwnership(veh);

            if (IsNetworkRunning)
            {
                SendPassengerSeatPacket(veh.Id, (byte)seat.Spec.SeatIndex, entered);
            }
        }

        private void CheckHandPush()
        {
            if (handPushObject == null || !handPushObject.activeInHierarchy) return;
            Transform camTr = Camera.main != null ? Camera.main.transform : playerTr;
            if (camTr == null) return;

            Vector3 origin = camTr.position;
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (!veh.IsBound || veh.MainRigidbody == null) continue;
                if (veh.DriverPlayerId >= 0 || veh.IsCarriedLocally || veh.IsCarriedRemotely) continue;

                float distSq = (origin - veh.Transform.position).sqrMagnitude;
                bool near = distSq < 20.25f; // 4.5 м
                if (!near)
                {
                    RaycastHit[] hits = Physics.RaycastAll(origin, camTr.forward, 4.5f);
                    for (int h = 0; h < hits.Length; h++)
                    {
                        Collider col = hits[h].collider;
                        if (col != null && !col.isTrigger && (col.transform == veh.Transform || col.transform.IsChildOf(veh.Transform)))
                        {
                            near = true;
                            break;
                        }
                    }
                }

                if (near)
                {
                    veh.SetKinematic(false);
                    veh.WakeUp();
                    veh.LocalPushTimer = 1.5f;
                }
            }
        }

        private bool IsLocalPhysicsAuthority(TrackedVehicle veh)
        {
            if (veh == null || !veh.IsBound) return false;
            if (veh.OverrideSuppressWorldTransformSync) return false;

            if (veh.HasAuthorityOverride)
            {
                if (veh.OverrideForceKinematic) return false;
                if (veh.OverrideForceDynamic || veh.OverrideAuthorityPlayerId == LocalPlayerId) return true;
                return false;
            }

            if (veh.IsDrivenLocally || veh.IsCarriedLocally || veh.LocalPushTimer > 0f) return true;
            if (veh.IsDrivenRemotely || veh.IsPushedRemotely || veh.IsCarriedRemotely) return false;

            // Без водителя физику авто авторитарно симулирует Хост (0)
            return ctx.Transport != null && ctx.Transport.IsServer && veh.OwnerPlayerId == 0;
        }

        private bool ShouldInterpolateRemotely(TrackedVehicle veh)
        {
            if (veh == null || !veh.HasRemoteState) return false;
            if (veh.OverrideSuppressWorldTransformSync) return false;
            if (veh.IsCarriedLocally || veh.IsCarriedRemotely) return false;

            if (veh.HasAuthorityOverride)
            {
                if (veh.OverrideForceDynamic || veh.OverrideAuthorityPlayerId == LocalPlayerId) return false;
                if (veh.OverrideForceKinematic) return true;
            }

            if (veh.IsDrivenLocally || veh.LocalPushTimer > 0f) return false;

            if (veh.IsDrivenRemotely || veh.IsPushedRemotely) return true;

            // Если локальный клиент сидит пассажиром в беспилотной катящейся машине Хоста, интерполируем кинематически без тряски
            if (ctx.Transport != null && ctx.Transport.IsClient &&
                passengerSystem.ActiveLocalSeat != null &&
                passengerSystem.ActiveLocalSeat.Vehicle == veh &&
                (Time.time - veh.LastRemotePhysicsTime) < 0.65f)
            {
                return true;
            }

            return false;
        }

        private void UpdateVehicleKinematicOwnership(TrackedVehicle veh)
        {
            if (veh == null || !veh.IsBound) return;

            if (veh.HasAuthorityOverride)
            {
                if (veh.OverrideForceKinematic)
                {
                    veh.SetKinematic(true);
                    return;
                }
                if (veh.OverrideForceDynamic)
                {
                    veh.SetKinematic(false);
                    return;
                }
            }

            if (veh.IsDrivenLocally || veh.LocalPushTimer > 0f)
            {
                veh.SetKinematic(false);
                return;
            }

            if (veh.IsCarriedLocally || veh.IsCarriedRemotely || veh.IsDrivenRemotely || veh.IsPushedRemotely)
            {
                veh.SetKinematic(true);
                return;
            }

            // Для беспилотной машины: на Хосте всегда физическая, на Клиенте — кинематическая только пока клиент-пассажир едет накатом с Хоста
            bool clientPassengerRolling = ctx.Transport != null && ctx.Transport.IsClient &&
                passengerSystem.ActiveLocalSeat != null &&
                passengerSystem.ActiveLocalSeat.Vehicle == veh &&
                veh.HasRemoteState && (Time.time - veh.LastRemotePhysicsTime) < 0.65f;

            veh.SetKinematic(clientPassengerRolling);
        }

        private void StreamOwnedVehiclesPhysics()
        {
            float now = Time.time;
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (!veh.IsBound || veh.MainRigidbody == null) continue;
                if (!IsLocalPhysicsAuthority(veh)) continue;

                bool isResting = !veh.IsDrivenLocally && !veh.IsCarriedLocally && veh.LocalPushTimer <= 0f
                    && veh.MainRigidbody.velocity.sqrMagnitude < 0.0005f
                    && veh.MainRigidbody.angularVelocity.sqrMagnitude < 0.0005f;

                float interval = isResting ? 1.0f : 0.04f; // 25 Гц при движении, 1 Гц heartbeat в покое
                if (now - veh.LastPhysicsSendTime >= interval)
                {
                    SendVehiclePhysicsSync(veh, isResting);
                }
            }
        }

        private void StreamLocalDriverControls()
        {
            if (localDrivenVehicle == null || !localDrivenVehicle.IsBound) return;

            float now = Time.time;
            if (now - localDrivenVehicle.LastControlsSendTime < 0.05f) return; // 20 Гц
            localDrivenVehicle.LastControlsSendTime = now;

            float steerAngle = localDrivenVehicle.GetSteeringVisualAngle();
            float steerIn = localDrivenVehicle.CarController != null ? localDrivenVehicle.CarController.steerInput : 0f;
            float throttleIn = localDrivenVehicle.CarController != null ? localDrivenVehicle.CarController.throttleInput : 0f;
            float brakeIn = localDrivenVehicle.CarController != null ? localDrivenVehicle.CarController.brakeInput : 0f;
            float handbrakeIn = localDrivenVehicle.CarController != null ? localDrivenVehicle.CarController.handbrakeInput : 0f;
            float clutchIn = localDrivenVehicle.CarController != null ? localDrivenVehicle.CarController.clutchInput : 0f;
            int gear = localDrivenVehicle.Drivetrain != null ? localDrivenVehicle.Drivetrain.gear : 0;
            float rpm = localDrivenVehicle.Drivetrain != null ? localDrivenVehicle.Drivetrain.rpm : 0f;

            using (var p = new PacketWriter(PacketType.VehicleControlSync))
            {
                p.WriteByte(localDrivenVehicle.Id);
                p.WriteInt32(LocalPlayerId);
                p.WriteSingle(steerAngle);
                p.WriteSingle(steerIn);
                p.WriteSingle(throttleIn);
                p.WriteSingle(brakeIn);
                p.WriteSingle(handbrakeIn);
                p.WriteSingle(clutchIn);
                p.WriteInt32(gear);
                p.WriteSingle(rpm);
                ctx.Transport.Send(p);
            }
        }

        private void SendVehiclePhysicsSync(TrackedVehicle veh, bool isResting)
        {
            if (!IsNetworkRunning || veh == null || !veh.IsBound || veh.MainRigidbody == null) return;
            if (veh.OverrideSuppressWorldTransformSync) return;

            veh.LastPhysicsSendTime = Time.time;
            byte flags = 0;
            if (veh.IsDrivenLocally) flags |= 1;
            if (veh.LocalPushTimer > 0f) flags |= 2;
            if (isResting) flags |= 4;
            if (veh.IsCarriedLocally) flags |= 8;

            using (var p = new PacketWriter(PacketType.VehiclePhysicsSync))
            {
                p.WriteByte(veh.Id);
                p.WriteInt32(LocalPlayerId);
                p.WriteByte(flags);
                p.WriteVector3(veh.Transform.position);
                p.WriteQuaternion(veh.Transform.rotation);
                p.WriteVector3(veh.MainRigidbody.velocity);
                p.WriteVector3(veh.MainRigidbody.angularVelocity);
                p.WriteSingle(veh.GetSteeringVisualAngle());
                ctx.Transport.Send(p);
            }
        }

        private void SendDrivingModePacket(byte vehId, bool entering)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehicleDrivingMode))
            {
                p.WriteByte(vehId);
                p.WriteBoolean(entering);
                p.WriteInt32(LocalPlayerId);
                ctx.Transport.Send(p);
            }
        }

        private void SendPassengerSeatPacket(byte vehId, byte seatIndex, bool entering)
        {
            if (!IsNetworkRunning) return;
            using (var p = new PacketWriter(PacketType.VehiclePassengerSeat))
            {
                p.WriteByte(vehId);
                p.WriteByte(seatIndex);
                p.WriteBoolean(entering);
                p.WriteInt32(LocalPlayerId);
                ctx.Transport.Send(p);
            }
        }

        private void HandleVehiclePhysicsSync(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            byte flags = reader.ReadByte();
            Vector3 pos = reader.ReadVector3();
            Quaternion rot = reader.ReadQuaternion();
            Vector3 vel = reader.ReadVector3();
            Vector3 angVel = reader.ReadVector3();
            float steerAngle = reader.UnreadBytes >= 4 ? reader.ReadSingle() : 0f;

            if (!vehiclesById.TryGetValue(vehId, out TrackedVehicle veh) || !veh.IsBound) return;

            bool allowByOverride = veh.HasAuthorityOverride
                && veh.OverrideForceKinematic
                && veh.OverrideAuthorityPlayerId != LocalPlayerId
                && senderId == veh.OverrideAuthorityPlayerId;

            if (veh.HasAuthorityOverride && !allowByOverride)
            {
                if (veh.OverrideAuthorityPlayerId == LocalPlayerId || veh.OverrideForceDynamic || senderId != veh.OverrideAuthorityPlayerId)
                {
                    return;
                }
            }

            // Пока локальный игрок владеет физикой авто (и нет ForceKinematic-переопределения), отклоняем чужие/устаревшие пакеты
            if (!allowByOverride && (veh.IsDrivenLocally || veh.LocalPushTimer > 0f || veh.IsCarriedLocally)) return;

            bool isRemoteDriving = (flags & 1) != 0;
            bool isRemotePushed = (flags & 2) != 0;
            bool isResting = (flags & 4) != 0;
            bool isRemoteCarried = (flags & 8) != 0;

            if (!allowByOverride)
            {
                // Если кто-то другой рулит или толкает — принимаем пакет, иначе принимаем пакет только от текущего владельца (обычно Хоста 0)
                if (veh.DriverPlayerId >= 0 && senderId != veh.DriverPlayerId && !isRemoteDriving) return;
                if (veh.DriverPlayerId < 0 && !isRemoteDriving && !isRemotePushed && !isRemoteCarried && senderId != veh.OwnerPlayerId && senderId != 0) return;
            }

            veh.TargetPosition = pos;
            veh.TargetRotation = rot;
            veh.TargetVelocity = vel;
            veh.TargetAngularVelocity = angVel;
            veh.RemoteSteerAngle = steerAngle;
            veh.HasRemoteState = true;
            veh.LastRemotePhysicsTime = Time.time;

            if (isRemoteDriving)
            {
                if (veh.DriverPlayerId != senderId)
                {
                    AssignRemoteDriver(veh, senderId, entering: true);
                }
            }

            if (isRemotePushed)
            {
                veh.RemotePushTimer = 1.6f;
                veh.OwnerPlayerId = senderId;
            }

            if (isRemoteCarried)
            {
                veh.IsCarriedRemotely = true;
                veh.CarrierPlayerId = senderId;
            }

            UpdateVehicleKinematicOwnership(veh);

            if (veh.OverrideSuppressWorldTransformSync)
            {
                return;
            }

            if (ShouldInterpolateRemotely(veh))
            {
                if ((veh.Transform.position - pos).sqrMagnitude > 36f)
                {
                    veh.Transform.position = pos;
                    veh.Transform.rotation = rot;
                    veh.TargetVelocity = Vector3.zero;
                    veh.TargetAngularVelocity = Vector3.zero;
                    veh.ZeroAllManagedVelocities();
                    veh.SteeringTeleportGuardUntil = Time.time + 0.35f;
                    veh.SnapRemoteSteeringAngle(steerAngle);
                    veh.LastTrackedPosition = pos;
                    veh.HasTrackedPosition = true;
                    PublishTeleportEvent(veh, pos, isLocalInitiated: false);
                }
            }
            else if (veh.MainRigidbody != null && !veh.IsCurrentlyKinematic)
            {
                bool isSnap = (veh.Transform.position - pos).sqrMagnitude > 36f;
                veh.Transform.position = pos;
                veh.Transform.rotation = rot;
                if (isResting || isSnap)
                {
                    veh.TargetVelocity = Vector3.zero;
                    veh.TargetAngularVelocity = Vector3.zero;
                    veh.ZeroAllManagedVelocities();
                    veh.MainRigidbody.Sleep();
                }
                else
                {
                    veh.ApplyLinearAndAngularVelocity(vel, angVel);
                }

                if (isSnap)
                {
                    veh.SteeringTeleportGuardUntil = Time.time + 0.35f;
                    veh.SnapRemoteSteeringAngle(steerAngle);
                    PublishTeleportEvent(veh, pos, isLocalInitiated: false);
                }
                veh.LastTrackedPosition = pos;
                veh.HasTrackedPosition = true;
            }
        }

        private void HandleVehicleDrivingMode(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            bool entering = reader.ReadBoolean();
            int playerId = reader.ReadInt32();
            if (playerId == LocalPlayerId) return;

            if (!vehiclesById.TryGetValue(vehId, out TrackedVehicle veh) || !veh.IsBound) return;
            if (veh.IsDrivenLocally && entering)
            {
                // Конфликт одновременной посадки: Хост имеет приоритет
                if (ctx.Transport.IsServer) return;
                HandleLocalDrivingExited(veh);
            }

            AssignRemoteDriver(veh, playerId, entering);
        }

        private void AssignRemoteDriver(TrackedVehicle veh, int playerId, bool entering)
        {
            if (veh == null) return;

            if (entering)
            {
                // Удаляем игрока с любого прежнего места, если он пересел
                ClearPlayerFromAllSeats(playerId, publishExit: false);

                veh.DriverPlayerId = playerId;
                veh.OwnerPlayerId = playerId;
                veh.RemotePushTimer = 0f;
                UpdateVehicleKinematicOwnership(veh);

                remoteSeats[playerId] = new RemoteSeatAssignment
                {
                    PlayerId = playerId,
                    IsDriver = true,
                    VehicleId = veh.Id,
                    SeatIndex = -1
                };
                PublishSeatEvent(remoteSeats[playerId], isSeated: true);
                if (veh.HasRemoteControls)
                {
                    PublishRemoteTelemetryEvent(veh, isDrivenRemotely: true);
                }
                ctx.Log?.Invoke($"[Транспорт] Игрок #{playerId} сел за руль {veh.GameObject.name}");
            }
            else
            {
                if (veh.DriverPlayerId == playerId)
                {
                    veh.DriverPlayerId = -1;
                    veh.OwnerPlayerId = 0; // Владелец физики возвращается к Хосту
                    veh.HasRemoteControls = false;
                    veh.RemoteSteerInput = 0f;
                    veh.RemoteThrottleInput = 0f;
                    veh.RemoteBrakeInput = 0f;
                    veh.RemoteHandbrakeInput = 0f;
                    veh.RemoteClutchInput = 0f;
                    veh.RemoteRpm = 0f;
                    veh.RestoreLocalSteeringControl();

                    PublishRemoteTelemetryEvent(veh, isDrivenRemotely: false);

                    // Если этот клиент сам вышел и мы Хост — переносим накопленную скорость на физику Хоста
                    UpdateVehicleKinematicOwnership(veh);
                    if (ctx.Transport != null && ctx.Transport.IsServer && veh.HasRemoteState && !veh.IsCurrentlyKinematic && !veh.OverrideSuppressWorldTransformSync)
                    {
                        if ((veh.Transform.position - veh.TargetPosition).sqrMagnitude > 0.25f)
                        {
                            veh.Transform.position = veh.TargetPosition;
                            veh.Transform.rotation = veh.TargetRotation;
                            veh.LastTrackedPosition = veh.TargetPosition;
                            veh.HasTrackedPosition = true;
                        }
                        veh.ApplyLinearAndAngularVelocity(veh.TargetVelocity, veh.TargetAngularVelocity);
                        veh.WakeUp();
                    }
                }

                ClearPlayerFromAllSeats(playerId, publishExit: true);
                ctx.Log?.Invoke($"[Транспорт] Игрок #{playerId} вышел из-за руля {veh.GameObject.name}");
            }
        }

        private void HandleVehiclePassengerSeat(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            byte seatIndex = reader.ReadByte();
            bool entering = reader.ReadBoolean();
            int playerId = reader.ReadInt32();
            if (playerId == LocalPlayerId) return;

            if (!vehiclesById.TryGetValue(vehId, out TrackedVehicle veh) || !veh.IsBound) return;
            AssignRemotePassenger(veh, seatIndex, playerId, entering);
        }

        private void AssignRemotePassenger(TrackedVehicle veh, int seatIndex, int playerId, bool entering)
        {
            if (veh == null) return;

            if (entering)
            {
                ClearPlayerFromAllSeats(playerId, publishExit: false);

                PassengerSeatRuntime seat = FindSeatRuntime(veh, seatIndex);
                if (seat == null) return;

                if (seat.IsOccupiedLocally)
                {
                    // Если места заняли одновременно, клиент уступает Хосту
                    if (ctx.Transport != null && ctx.Transport.IsClient && playerId == 0)
                    {
                        passengerSystem.ForceExitLocal(teleportToSafeExit: true);
                    }
                    else
                    {
                        return;
                    }
                }

                seat.RemoteOccupantId = playerId;
                remoteSeats[playerId] = new RemoteSeatAssignment
                {
                    PlayerId = playerId,
                    IsDriver = false,
                    VehicleId = veh.Id,
                    SeatIndex = seatIndex
                };
                PublishSeatEvent(remoteSeats[playerId], isSeated: true);
                ctx.Log?.Invoke($"[Пассажиры] Игрок #{playerId} сел на место #{seatIndex} в {veh.GameObject.name}");
            }
            else
            {
                ClearPlayerFromAllSeats(playerId, publishExit: true);
                ctx.Log?.Invoke($"[Пассажиры] Игрок #{playerId} вышел с места #{seatIndex} в {veh.GameObject.name}");
            }
        }

        private void HandleVehicleControlSync(PacketReader reader, int connectionId)
        {
            byte vehId = reader.ReadByte();
            int senderId = reader.ReadInt32();
            if (senderId == LocalPlayerId) return;

            float steerAngle = reader.ReadSingle();
            float steerIn = reader.ReadSingle();
            float throttleIn = reader.ReadSingle();
            float brakeIn = reader.ReadSingle();
            float handbrakeIn = reader.ReadSingle();
            float clutchIn = reader.ReadSingle();
            int gear = reader.ReadInt32();
            float rpm = reader.ReadSingle();

            if (!vehiclesById.TryGetValue(vehId, out TrackedVehicle veh) || !veh.IsBound) return;
            if (veh.IsDrivenLocally) return;

            veh.RemoteSteerAngle = steerAngle;
            veh.RemoteSteerInput = steerIn;
            veh.RemoteThrottleInput = throttleIn;
            veh.RemoteBrakeInput = brakeIn;
            veh.RemoteHandbrakeInput = handbrakeIn;
            veh.RemoteClutchInput = clutchIn;
            veh.RemoteGear = gear;
            veh.RemoteRpm = rpm;
            veh.HasRemoteControls = true;

            bool isRemoteDriver = veh.DriverPlayerId == senderId || veh.IsDrivenRemotely;
            PublishRemoteTelemetryEvent(veh, isDrivenRemotely: isRemoteDriver);
        }

        private void HandleVehiclePassengerSnapshot(PacketReader reader, int connectionId)
        {
            byte count = reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte vehId = reader.ReadByte();
                Vector3 pos = reader.ReadVector3();
                Quaternion rot = reader.ReadQuaternion();
                int driverId = reader.ReadInt32();
                float steerAngle = reader.ReadSingle();
                byte passCount = reader.ReadByte();

                if (vehiclesById.TryGetValue(vehId, out TrackedVehicle veh) && veh.IsBound)
                {
                    if (!veh.IsDrivenLocally && veh.LocalPushTimer <= 0f && !veh.OverrideSuppressWorldTransformSync)
                    {
                        bool isSnap = (veh.Transform.position - pos).sqrMagnitude > 36f;
                        veh.TargetPosition = pos;
                        veh.TargetRotation = rot;
                        veh.TargetVelocity = Vector3.zero;
                        veh.TargetAngularVelocity = Vector3.zero;
                        veh.RemoteSteerAngle = steerAngle;
                        veh.HasRemoteState = true;
                        veh.LastRemotePhysicsTime = Time.time;

                        veh.Transform.position = pos;
                        veh.Transform.rotation = rot;
                        veh.LastTrackedPosition = pos;
                        veh.HasTrackedPosition = true;
                        if (isSnap)
                        {
                            veh.ZeroAllManagedVelocities();
                            veh.SteeringTeleportGuardUntil = Time.time + 0.35f;
                            veh.SnapRemoteSteeringAngle(steerAngle);
                            PublishTeleportEvent(veh, pos, isLocalInitiated: false);
                        }
                    }

                    if (driverId >= 0 && driverId != LocalPlayerId)
                    {
                        AssignRemoteDriver(veh, driverId, entering: true);
                    }
                    else if (driverId < 0 && veh.DriverPlayerId >= 0 && !veh.IsDrivenLocally)
                    {
                        AssignRemoteDriver(veh, veh.DriverPlayerId, entering: false);
                    }

                    for (int p = 0; p < passCount; p++)
                    {
                        byte seatIdx = reader.ReadByte();
                        int occupantId = reader.ReadInt32();
                        if (occupantId >= 0 && occupantId != LocalPlayerId)
                        {
                            AssignRemotePassenger(veh, seatIdx, occupantId, entering: true);
                        }
                    }
                }
                else
                {
                    for (int p = 0; p < passCount; p++)
                    {
                        reader.ReadByte();
                        reader.ReadInt32();
                    }
                }
            }
        }

        private void HandleSpecialObjectCarry(SpecialObjectCarryEvent evt)
        {
            if (evt.TargetObject == null) return;
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (!veh.IsBound) continue;
                if (veh.GameObject == evt.TargetObject || evt.TargetObject.transform == veh.Transform)
                {
                    if (evt.IsLocal)
                    {
                        veh.IsCarriedLocally = evt.IsCarried;
                        if (!evt.IsCarried) veh.LocalPushTimer = 1.5f;
                    }
                    else
                    {
                        veh.IsCarriedRemotely = evt.IsCarried;
                        veh.CarrierPlayerId = evt.IsCarried ? evt.PlayerId : -1;
                    }
                    UpdateVehicleKinematicOwnership(veh);
                    break;
                }
            }
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx.Transport == null) return;

            if (ctx.Transport.IsServer)
            {
                SendLateJoinSnapshot(clientId);
                pendingSnapshots.RemoveAll(s => s.ClientId == clientId);
                pendingSnapshots.Add(new PendingSnapshotSchedule { ClientId = clientId, NextSendTime = Time.time + 1.5f, Stage = 0 });
                pendingSnapshots.Add(new PendingSnapshotSchedule { ClientId = clientId, NextSendTime = Time.time + 4.0f, Stage = 1 });
            }
            else if (ctx.Transport.IsClient)
            {
                // Если клиент переподключился, находясь в транспорте, повторно уведомляем сервер
                if (localDrivenVehicle != null)
                {
                    SendDrivingModePacket(localDrivenVehicle.Id, true);
                }
                else if (passengerSystem.ActiveLocalSeat != null && passengerSystem.ActiveLocalSeat.Vehicle != null)
                {
                    SendPassengerSeatPacket(
                        passengerSystem.ActiveLocalSeat.Vehicle.Id,
                        (byte)passengerSystem.ActiveLocalSeat.Spec.SeatIndex,
                        true);
                }
            }
        }

        private void HandleClientDisconnected(int clientId)
        {
            int targetPlayerId = (ctx.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            pendingSnapshots.RemoveAll(s => s.ClientId == clientId);

            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                if (veh.DriverPlayerId == targetPlayerId)
                {
                    AssignRemoteDriver(veh, targetPlayerId, entering: false);
                }
                if (veh.CarrierPlayerId == targetPlayerId)
                {
                    veh.IsCarriedRemotely = false;
                    veh.CarrierPlayerId = -1;
                    UpdateVehicleKinematicOwnership(veh);
                }
            }

            ClearPlayerFromAllSeats(targetPlayerId, publishExit: true);
        }

        private void ProcessPendingSnapshots()
        {
            if (!ctx.Transport.IsServer || pendingSnapshots.Count == 0) return;

            for (int i = pendingSnapshots.Count - 1; i >= 0; i--)
            {
                PendingSnapshotSchedule item = pendingSnapshots[i];
                if (Time.time >= item.NextSendTime)
                {
                    SendLateJoinSnapshot(item.ClientId);
                    pendingSnapshots.RemoveAt(i);
                }
            }
        }

        private void SendLateJoinSnapshot(int targetClientId)
        {
            if (!IsNetworkRunning || !ctx.Transport.IsServer) return;

            var bound = new List<TrackedVehicle>();
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i].IsBound) bound.Add(vehicles[i]);
            }

            using (var p = new PacketWriter(PacketType.VehiclePassengerSnapshot))
            {
                p.WriteByte((byte)bound.Count);
                for (int i = 0; i < bound.Count; i++)
                {
                    TrackedVehicle veh = bound[i];
                    p.WriteByte(veh.Id);
                    p.WriteVector3(veh.Transform.position);
                    p.WriteQuaternion(veh.Transform.rotation);
                    p.WriteInt32(veh.DriverPlayerId);
                    p.WriteSingle(veh.GetSteeringVisualAngle());

                    var occupied = new List<KeyValuePair<byte, int>>();
                    for (int s = 0; s < veh.PassengerSeats.Count; s++)
                    {
                        PassengerSeatRuntime seat = veh.PassengerSeats[s];
                        if (seat.IsOccupiedLocally)
                        {
                            occupied.Add(new KeyValuePair<byte, int>((byte)seat.Spec.SeatIndex, LocalPlayerId));
                        }
                        else if (seat.RemoteOccupantId >= 0)
                        {
                            occupied.Add(new KeyValuePair<byte, int>((byte)seat.Spec.SeatIndex, seat.RemoteOccupantId));
                        }
                    }

                    p.WriteByte((byte)occupied.Count);
                    for (int k = 0; k < occupied.Count; k++)
                    {
                        p.WriteByte(occupied[k].Key);
                        p.WriteInt32(occupied[k].Value);
                    }
                }

                ctx.Transport.SendTo(p, targetClientId);
            }
        }

        private void RefreshRemoteSeatBindings()
        {
            if (remoteSeats.Count == 0) return;
            foreach (var kv in remoteSeats)
            {
                PublishSeatEvent(kv.Value, isSeated: true);
            }
        }

        private void ClearPlayerFromAllSeats(int playerId, bool publishExit)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                TrackedVehicle veh = vehicles[i];
                for (int s = 0; s < veh.PassengerSeats.Count; s++)
                {
                    if (veh.PassengerSeats[s].RemoteOccupantId == playerId)
                    {
                        veh.PassengerSeats[s].RemoteOccupantId = -1;
                    }
                }
            }

            if (remoteSeats.TryGetValue(playerId, out RemoteSeatAssignment prev))
            {
                remoteSeats.Remove(playerId);
                if (publishExit)
                {
                    PublishSeatEvent(prev, isSeated: false);
                }
            }
        }

        private void PublishSeatEvent(RemoteSeatAssignment assignment, bool isSeated)
        {
            if (assignment == null) return;
            if (!vehiclesById.TryGetValue(assignment.VehicleId, out TrackedVehicle veh) || !veh.IsBound) return;

            Vector3 localPos = Vector3.zero;
            Quaternion localRot = Quaternion.identity;
            Transform steeringFork = null;

            if (isSeated)
            {
                if (assignment.IsDriver)
                {
                    localPos = veh.DriverPuppetLocalOffset;
                    localRot = veh.DriverPuppetLocalRotation;
                    steeringFork = veh.SteeringForkForPuppet;
                }
                else
                {
                    PassengerSeatRuntime seat = FindSeatRuntime(veh, assignment.SeatIndex);
                    if (seat != null)
                    {
                        localPos = seat.CalibratedPuppetLocalOffset;
                        localRot = seat.CalibratedPuppetLocalRotation;
                    }
                }
            }

            ctx.Events.Publish(new RemotePlayerSeatEvent
            {
                PlayerId = assignment.PlayerId,
                IsSeated = isSeated,
                IsDriver = isSeated && assignment.IsDriver,
                VehicleId = veh.Id,
                SeatIndex = assignment.SeatIndex,
                VehicleTransform = isSeated ? veh.Transform : null,
                SteeringForkTransform = steeringFork,
                LocalSeatPosition = localPos,
                LocalSeatRotation = localRot,
                UsePassengerAnimation = isSeated
            });
        }

        private static PassengerSeatRuntime FindSeatRuntime(TrackedVehicle veh, int seatIndex)
        {
            if (veh == null) return null;
            for (int i = 0; i < veh.PassengerSeats.Count; i++)
            {
                if (veh.PassengerSeats[i].Spec.SeatIndex == seatIndex)
                {
                    return veh.PassengerSeats[i];
                }
            }
            return null;
        }

        private Transform FindPlayerTransform()
        {
            if (playerTr != null && playerTr.gameObject.activeInHierarchy) return playerTr;
            GameObject pObj = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
            if (pObj != null) playerTr = pObj.transform;
            return playerTr;
        }

        private TrackedVehicle ResolveVehicle(int vehicleId, Transform vehicleTransform)
        {
            if (vehicleId >= 0 && vehicleId <= byte.MaxValue &&
                vehiclesById.TryGetValue((byte)vehicleId, out TrackedVehicle byId))
            {
                return byId;
            }

            if (vehicleTransform != null)
            {
                for (int i = 0; i < vehicles.Count; i++)
                {
                    if (vehicles[i].Transform == vehicleTransform)
                    {
                        return vehicles[i];
                    }
                }
            }

            return null;
        }

        private void HandleVehiclePhysicsAuthority(VehiclePhysicsAuthorityEvent evt)
        {
            TrackedVehicle veh = ResolveVehicle(evt.VehicleId, evt.VehicleTransform);
            if (evt.VehicleId < 0 || evt.VehicleId > byte.MaxValue)
            {
                if (veh == null) return;
            }

            byte vid = veh != null ? veh.Id : (byte)evt.VehicleId;
            string sourceKey = string.IsNullOrEmpty(evt.SourceModuleId) ? "__default" : evt.SourceModuleId;

            if (evt.OverrideActive)
            {
                if (!authorityOverrides.TryGetValue(vid, out var bySource))
                {
                    bySource = new Dictionary<string, VehiclePhysicsAuthorityEvent>();
                    authorityOverrides[vid] = bySource;
                }
                bySource[sourceKey] = evt;
            }
            else if (authorityOverrides.TryGetValue(vid, out var bySource))
            {
                bySource.Remove(sourceKey);
                if (bySource.Count == 0)
                {
                    authorityOverrides.Remove(vid);
                }
            }

            if (veh != null)
            {
                ApplyResolvedAuthorityOverride(veh);
                if (veh.IsBound)
                {
                    UpdateVehicleKinematicOwnership(veh);
                }
            }
        }

        private void ApplyResolvedAuthorityOverride(TrackedVehicle veh)
        {
            if (veh == null) return;

            if (!authorityOverrides.TryGetValue(veh.Id, out var bySource) || bySource.Count == 0)
            {
                veh.ClearAuthorityOverride();
                return;
            }

            bool forceKinematic = false;
            bool forceDynamic = false;
            bool suppressWorldSync = false;
            int authorityPlayerId = 0;
            bool hasAuthorityPlayer = false;

            foreach (var kv in bySource)
            {
                VehiclePhysicsAuthorityEvent ov = kv.Value;
                if (!ov.OverrideActive) continue;

                if (ov.ForceKinematic)
                {
                    forceKinematic = true;
                    authorityPlayerId = ov.AuthorityPlayerId;
                    hasAuthorityPlayer = true;
                }
                if (ov.ForceDynamic)
                {
                    forceDynamic = true;
                    if (!hasAuthorityPlayer)
                    {
                        authorityPlayerId = ov.AuthorityPlayerId;
                        hasAuthorityPlayer = true;
                    }
                }
                if (ov.SuppressWorldTransformSync)
                {
                    suppressWorldSync = true;
                    if (!hasAuthorityPlayer)
                    {
                        authorityPlayerId = ov.AuthorityPlayerId;
                        hasAuthorityPlayer = true;
                    }
                }
                if (!hasAuthorityPlayer)
                {
                    authorityPlayerId = ov.AuthorityPlayerId;
                    hasAuthorityPlayer = true;
                }
            }

            // При конфликте ForceKinematic и ForceDynamic приоритет у кинематики для безопасности симуляции
            if (forceKinematic)
            {
                forceDynamic = false;
            }

            veh.HasAuthorityOverride = true;
            veh.OverrideAuthorityPlayerId = authorityPlayerId;
            veh.OverrideForceKinematic = forceKinematic;
            veh.OverrideForceDynamic = forceDynamic;
            veh.OverrideSuppressWorldTransformSync = suppressWorldSync;
        }

        private void PublishTeleportEvent(TrackedVehicle veh, Vector3 newPosition, bool isLocalInitiated)
        {
            if (ctx?.Events == null || veh == null) return;
            ctx.Events.Publish(new VehicleTeleportEvent
            {
                VehicleId = veh.Id,
                VehicleTransform = veh.Transform,
                NewPosition = newPosition,
                IsLocalInitiated = isLocalInitiated
            });
        }

        private void PublishRemoteTelemetryEvent(TrackedVehicle veh, bool isDrivenRemotely)
        {
            if (ctx?.Events == null || veh == null) return;
            ctx.Events.Publish(new VehicleRemoteTelemetryEvent
            {
                VehicleId = veh.Id,
                VehicleTransform = veh.Transform,
                Rpm = veh.RemoteRpm,
                Gear = veh.RemoteGear,
                ThrottleInput = veh.RemoteThrottleInput,
                SteerAngle = veh.RemoteSteerAngle,
                IsDrivenRemotely = isDrivenRemotely
            });
        }
    }
}
