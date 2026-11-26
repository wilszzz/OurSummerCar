using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.Vehicles.Boat
{
    public sealed class BoatModule : IOscModule
    {
        public string ModuleId => "Vehicles.Boat";
        public string DisplayName => "Лодка и Причаливание";
        public string StatusSummary
        {
            get
            {
                if (motorSync == null || motorSync.BoatObject == null)
                {
                    return "Лодка: не найдена";
                }
                string eng = motorSync.IsEngineRunning ? "Мотор: работает" : "Мотор: заглушен";
                string gear = motorSync.CurrentGear == (byte)BoatGearState.First
                    ? "Вперёд"
                    : (motorSync.CurrentGear == (byte)BoatGearState.Reverse ? "Реверс" : "Нейтраль");
                string moor = mooringSystem != null && mooringSystem.IsMoored ? ", пришвартована" : string.Empty;
                return $"Лодка: {eng}, {gear}{moor}";
            }
        }

        private OscHostContext ctx;
        private BoatMotorSync motorSync;
        private BoatMooringSystem mooringSystem;
        private BoatPassengerSafety passengerSafety;
        private float nextScanTime;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            motorSync = new BoatMotorSync(ctx, ModuleId);
            mooringSystem = new BoatMooringSystem(ctx, ModuleId, motorSync);
            passengerSafety = new BoatPassengerSafety(ctx, ModuleId, motorSync);

            ctx.Router.Register(PacketType.BoatEngineSync, ModuleId, HandleBoatEngineSync, relayOnServer: true);
            ctx.Router.Register(PacketType.BoatMooringSync, ModuleId, HandleBoatMooringSync, relayOnServer: true);

            ctx.Events.Subscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancy);
            ctx.Events.Subscribe<RemotePlayerSeatEvent>(HandleRemoteSeat);
            ctx.Events.Subscribe<VehicleTeleportEvent>(HandleVehicleTeleport);
            ctx.Events.Subscribe<VehicleRemoteTelemetryEvent>(HandleRemoteTelemetry);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
        }

        public void OnMenuLoaded()
        {
            ResetState();
        }

        public void OnLevelLoaded()
        {
            ResetState();
            TryDiscover();
        }

        public void OnShutdown()
        {
            ctx?.Events?.Unsubscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancy);
            ctx?.Events?.Unsubscribe<RemotePlayerSeatEvent>(HandleRemoteSeat);
            ctx?.Events?.Unsubscribe<VehicleTeleportEvent>(HandleVehicleTeleport);
            ctx?.Events?.Unsubscribe<VehicleRemoteTelemetryEvent>(HandleRemoteTelemetry);

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetState();
        }

        public void OnUpdate()
        {
            if (Time.time >= nextScanTime)
            {
                nextScanTime = Time.time + 3.0f;
                TryDiscover();
            }

            motorSync.TickUpdate(
                passengerSafety.IsLocalDriver,
                passengerSafety.IsRemoteDriver,
                passengerSafety.IsLocalPhysicsOwner);
            mooringSystem.TickUpdate(passengerSafety.IsLocalSeatedOnBoat);
            passengerSafety.TickUpdate();
        }

        public void OnFixedUpdate()
        {
        }

        public void OnLateUpdate()
        {
        }

        private void ResetState()
        {
            nextScanTime = 0f;
            passengerSafety?.Reset();
            mooringSystem?.Reset();
            motorSync?.Reset();
        }

        private void TryDiscover()
        {
            if (motorSync.DiscoverAndHook())
            {
                mooringSystem.Discover();
            }
        }

        private void HandleLocalOccupancy(LocalPlayerOccupancyEvent evt)
        {
            passengerSafety.OnLocalOccupancyChanged(evt);
        }

        private void HandleRemoteSeat(RemotePlayerSeatEvent evt)
        {
            passengerSafety.OnRemoteSeatChanged(evt);
        }

        private void HandleVehicleTeleport(VehicleTeleportEvent evt)
        {
            motorSync.HandleTeleport(evt);
            mooringSystem.HandleTeleport(evt);
        }

        private void HandleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            motorSync.HandleRemoteTelemetry(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            motorSync.SendSnapshotTo(clientId);
            mooringSystem.SendSnapshotTo(clientId);
        }

        private void HandleClientDisconnected(int clientId)
        {
            int targetId = (ctx?.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            passengerSafety.OnRemoteDisconnected(targetId);
        }

        private void HandleBoatEngineSync(PacketReader reader, int connectionId)
        {
            motorSync.HandlePacket(reader, connectionId);
        }

        private void HandleBoatMooringSync(PacketReader reader, int connectionId)
        {
            mooringSystem.HandlePacket(reader, connectionId);
        }
    }
}
