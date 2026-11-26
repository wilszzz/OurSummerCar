using System;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.Vehicles.Controls
{
    /// <summary>
    /// Модуль Этапа 3: приборка, органы управления, обороты/звук и топливо двигателя,
    /// автомагнитола/CD, буксировочный трос (с R5-владением автопоездом), лебёдка/шланг Gifu,
    /// повреждения кузова/стёкол и PhysicsOptimizer.
    /// </summary>
    public sealed class VehiclesControlsModule : IOscModule
    {
        public string ModuleId => "Vehicles.Controls";
        public string DisplayName => "Приборка, Двигатель, Топливо, Радио, Буксир и Повреждения";
        public string StatusSummary
        {
            get
            {
                int bound = 0;
                for (int i = 0; i < catalog.Length; i++)
                {
                    if (catalog[i].IsBound) bound++;
                }
                return $"Авто: {bound}/{catalog.Length}, Контролы: {dashboardSync?.HookedControlsCount ?? 0}, Радио: {radioSync?.TrackedRadioCount ?? 0}, Сцепки: {towingSync?.ActiveTowLinksCount ?? 0}";
            }
        }

        private OscHostContext ctx;
        private readonly VehicleCatalogEntry[] catalog = ControlsHelpers.CreateCatalog();

        private DashboardControlsSync dashboardSync;
        private EngineAudioAndFuelSync engineFuelSync;
        private CarRadioSync radioSync;
        private TowingAndWinchSync towingSync;
        private VehicleDamageAndGlassSync damageGlassSync;
        private PhysicsOptimizerService physicsOptimizer;

        private float nextDiscoveryTime;

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            dashboardSync = new DashboardControlsSync(ctx, ModuleId, catalog);
            engineFuelSync = new EngineAudioAndFuelSync(ctx, ModuleId, catalog);
            radioSync = new CarRadioSync(ctx, ModuleId);
            towingSync = new TowingAndWinchSync(ctx, ModuleId, catalog);
            damageGlassSync = new VehicleDamageAndGlassSync(ctx, ModuleId, catalog);
            physicsOptimizer = new PhysicsOptimizerService();

            // Регистрируем ТОЛЬКО пакеты подсистемы Controls (69, 71..75).
            // Пакеты 60, 61, 66, 67, 68 принадлежат Stage 2 OSC.Module.Vehicles.
            ctx.Router.Register(PacketType.VehicleSystemToggle, ModuleId, dashboardSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleFuelSync, ModuleId, engineFuelSync.HandleFuelPacket, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleRadioSync, ModuleId, radioSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleTowingSync, ModuleId, towingSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleDamageSync, ModuleId, damageGlassSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.VehicleFluidWearSync, ModuleId, engineFuelSync.HandleFluidWearPacket, relayOnServer: true);

            ctx.Events.Subscribe<LocalPlayerOccupancyEvent>(HandleLocalPlayerOccupancy);
            ctx.Events.Subscribe<RemotePlayerSeatEvent>(HandleRemotePlayerSeat);
            ctx.Events.Subscribe<VehicleRemoteTelemetryEvent>(HandleVehicleRemoteTelemetry);
            ctx.Events.Subscribe<VehicleTeleportEvent>(HandleVehicleTeleport);
            ctx.Events.Subscribe<PartAssembledEvent>(HandlePartAssembled);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
        }

        public void OnMenuLoaded()
        {
            physicsOptimizer?.RestoreVanilla();
            ResetAllSubsystems();
        }

        public void OnLevelLoaded()
        {
            ResetAllSubsystems();
            DiscoverVehicles();

            physicsOptimizer?.ApplyOnLevelLoaded();
            engineFuelSync?.OnLevelLoaded();
            radioSync?.OnLevelLoaded();
            towingSync?.OnLevelLoaded();
            damageGlassSync?.OnLevelLoaded();

            ctx.Log?.Invoke($"[Vehicles.Controls] Инициализирован ({StatusSummary}).");
        }

        public void OnUpdate()
        {
            if (Time.time >= nextDiscoveryTime)
            {
                nextDiscoveryTime = Time.time + 2.0f;
                DiscoverVehicles();
            }

            dashboardSync?.OnUpdate();
            engineFuelSync?.OnUpdate();
            radioSync?.OnUpdate();
            towingSync?.OnUpdate();
            damageGlassSync?.OnUpdate();
        }

        public void OnFixedUpdate()
        {
        }

        public void OnLateUpdate()
        {
        }

        public void OnShutdown()
        {
            physicsOptimizer?.RestoreVanilla();

            if (ctx?.Events != null)
            {
                ctx.Events.Unsubscribe<LocalPlayerOccupancyEvent>(HandleLocalPlayerOccupancy);
                ctx.Events.Unsubscribe<RemotePlayerSeatEvent>(HandleRemotePlayerSeat);
                ctx.Events.Unsubscribe<VehicleRemoteTelemetryEvent>(HandleVehicleRemoteTelemetry);
                ctx.Events.Unsubscribe<VehicleTeleportEvent>(HandleVehicleTeleport);
                ctx.Events.Unsubscribe<PartAssembledEvent>(HandlePartAssembled);
            }

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetAllSubsystems();
        }

        private void ResetAllSubsystems()
        {
            for (int i = 0; i < catalog.Length; i++)
            {
                catalog[i].RootObject = null;
                catalog[i].MainRigidbody = null;
                catalog[i].Drivetrain = null;
                catalog[i].SoundController = null;
                catalog[i].DriverPlayerId = -1;
                catalog[i].IsDrivenLocally = false;
                catalog[i].IsDrivenRemotely = false;
                catalog[i].SuppressCollisionUntilTime = 0f;
            }

            dashboardSync?.Reset();
            engineFuelSync?.Reset();
            radioSync?.Reset();
            towingSync?.Reset();
            damageGlassSync?.Reset();
            nextDiscoveryTime = 0f;
        }

        private void DiscoverVehicles()
        {
            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (veh.IsBound) continue;

                GameObject root = ControlsHelpers.FindVehicleRoot(veh);
                if (root == null) continue;

                veh.RootObject = root;
                veh.MainRigidbody = root.GetComponent<Rigidbody>();
                veh.Drivetrain = root.GetComponent<Drivetrain>() ?? FindFirstInChildren<Drivetrain>(root);
                veh.SoundController = root.GetComponent<SoundController>() ?? FindFirstInChildren<SoundController>(root);

                // Примечание: MainRigidbody и DriveTrigger принадлежат Stage 2 OSC.Module.Vehicles — не захватываем их в OwnershipRegistry.
                dashboardSync?.ScanVehicle(veh);
            }
        }

        private void HandleLocalPlayerOccupancy(LocalPlayerOccupancyEvent evt)
        {
            int localId = (ctx != null && ctx.Transport != null) ? ctx.Transport.LocalPlayerId : 0;
            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (evt.IsSeated && evt.IsDriver && veh.VehicleId == evt.VehicleId)
                {
                    veh.IsDrivenLocally = true;
                    veh.IsDrivenRemotely = false;
                    veh.DriverPlayerId = localId;
                }
                else if (veh.IsDrivenLocally)
                {
                    veh.IsDrivenLocally = false;
                    if (veh.DriverPlayerId == localId)
                    {
                        veh.DriverPlayerId = -1;
                    }
                }
            }

            towingSync?.OnOccupancyChanged();
        }

        private void HandleRemotePlayerSeat(RemotePlayerSeatEvent evt)
        {
            for (int i = 0; i < catalog.Length; i++)
            {
                VehicleCatalogEntry veh = catalog[i];
                if (evt.IsSeated && evt.IsDriver && veh.VehicleId == evt.VehicleId)
                {
                    veh.IsDrivenRemotely = true;
                    veh.DriverPlayerId = evt.PlayerId;
                }
                else if (veh.DriverPlayerId == evt.PlayerId)
                {
                    veh.IsDrivenRemotely = false;
                    veh.DriverPlayerId = -1;
                }
            }

            towingSync?.OnOccupancyChanged();
        }

        private void HandleVehicleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            engineFuelSync?.HandleRemoteTelemetry(evt);
        }

        private void HandleVehicleTeleport(VehicleTeleportEvent evt)
        {
            damageGlassSync?.HandleTeleportEvent(evt);
        }

        private void HandlePartAssembled(PartAssembledEvent evt)
        {
            damageGlassSync?.HandlePartAssembled(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            dashboardSync?.SendSnapshotToClient(clientId);
            engineFuelSync?.SendSnapshotToClient(clientId);
            radioSync?.SendSnapshotToClient(clientId);
            towingSync?.SendSnapshotToClient(clientId);
            damageGlassSync?.SendSnapshotToClient(clientId);
        }

        private void HandleClientDisconnected(int connectionId)
        {
            for (int i = 0; i < catalog.Length; i++)
            {
                if (catalog[i].DriverPlayerId == connectionId)
                {
                    catalog[i].DriverPlayerId = -1;
                    catalog[i].IsDrivenRemotely = false;
                }
            }

            dashboardSync?.OnClientDisconnected(connectionId);
            towingSync?.OnClientDisconnected(connectionId);
        }

        private static T FindFirstInChildren<T>(GameObject root) where T : Component
        {
            if (root == null) return null;
            T[] items = root.GetComponentsInChildren<T>(true);
            return (items != null && items.Length > 0) ? items[0] : null;
        }
    }
}
