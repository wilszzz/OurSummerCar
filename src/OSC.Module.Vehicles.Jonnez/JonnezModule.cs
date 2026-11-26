using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.Vehicles.Jonnez
{
    public sealed class JonnezModule : IOscModule
    {
        public string ModuleId => "Vehicles.Jonnez";
        public string DisplayName => "Мопед Jonnez ES";
        public string StatusSummary
        {
            get
            {
                if (stability == null || stability.JonnezObject == null)
                {
                    return "Jonnez: не найден";
                }
                string stand = equipment != null && equipment.IsKickstandDown ? "Подножка: опущена" : "Подножка: поднята";
                string cargo = equipment != null && equipment.IsStowedInHayosiko ? ", в кузове Hayosiko" : string.Empty;
                return $"Jonnez: активен ({stand}{cargo})";
            }
        }

        private OscHostContext ctx;
        private JonnezStabilityAndCollisions stability;
        private JonnezEquipmentAndCargo equipment;

        private bool isDrivenLocally;
        private int remoteDriverId = -1;
        private bool isCarried;
        private float nextScanTime;

        private bool IsDrivenRemotely => remoteDriverId >= 0 && !isDrivenLocally;

        public void Initialize(OscHostContext context)
        {
            ctx = context;
            stability = new JonnezStabilityAndCollisions(ctx.Log);
            equipment = new JonnezEquipmentAndCargo(ctx, ModuleId, stability);

            ctx.Router.Register(PacketType.JonnezStateSync, ModuleId, HandleJonnezStateSync, relayOnServer: true);

            ctx.Events.Subscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancy);
            ctx.Events.Subscribe<RemotePlayerSeatEvent>(HandleRemoteSeat);
            ctx.Events.Subscribe<SpecialObjectCarryEvent>(HandleSpecialCarry);
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
            ctx?.Events?.Unsubscribe<SpecialObjectCarryEvent>(HandleSpecialCarry);
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
                nextScanTime = Time.time + 2.0f;
                TryDiscover();
            }

            bool kickstandDown = equipment == null || equipment.IsKickstandDown;
            stability.TickUpdate(isDrivenLocally, IsDrivenRemotely, kickstandDown);
            equipment.TickUpdate(isDrivenLocally, IsDrivenRemotely, isCarried);
        }

        public void OnFixedUpdate()
        {
            stability.TickFixedUpdate(isDrivenLocally, IsDrivenRemotely);
        }

        public void OnLateUpdate()
        {
            stability.TickLateUpdate(IsDrivenRemotely);
            equipment.TickLateUpdate(isDrivenLocally, IsDrivenRemotely, isCarried);
        }

        private void ResetState()
        {
            isDrivenLocally = false;
            remoteDriverId = -1;
            isCarried = false;
            nextScanTime = 0f;
            equipment?.Reset();
            stability?.Reset();
        }

        private void TryDiscover()
        {
            if (stability.BindIfNeeded(out bool newlyBound))
            {
                if (newlyBound)
                {
                    equipment.DiscoverEquipment();
                }
            }
        }

        private void HandleLocalOccupancy(LocalPlayerOccupancyEvent evt)
        {
            equipment.OnLocalOccupancyChanged(evt);
            if (!stability.IsJonnezEvent(evt.VehicleId, evt.VehicleTransform)) return;

            bool kickstandDown = equipment.IsKickstandDown;
            if (evt.IsSeated)
            {
                isDrivenLocally = evt.IsDriver;
                remoteDriverId = -1;
                stability.OnMounted(isLocalDriver: evt.IsDriver, kickstandDown: kickstandDown);
                if (evt.IsDriver)
                {
                    equipment.SendCenterOfMassPacket(stability.ActiveCenterOfMass);
                }
            }
            else
            {
                isDrivenLocally = false;
                stability.OnDismounted(kickstandDown);
            }
        }

        private void HandleRemoteSeat(RemotePlayerSeatEvent evt)
        {
            equipment.OnRemoteSeatChanged(evt);
            if (!stability.IsJonnezEvent(evt.VehicleId, evt.VehicleTransform)) return;

            bool kickstandDown = equipment.IsKickstandDown;
            if (evt.IsSeated && evt.IsDriver)
            {
                remoteDriverId = evt.PlayerId;
                stability.OnMounted(isLocalDriver: false, kickstandDown: kickstandDown);
            }
            else if (!evt.IsSeated && remoteDriverId == evt.PlayerId)
            {
                remoteDriverId = -1;
                stability.OnDismounted(kickstandDown);
            }
        }

        private void HandleSpecialCarry(SpecialObjectCarryEvent evt)
        {
            if (evt.TargetObject != null &&
                (evt.TargetObject == stability.JonnezObject || evt.TargetObject.transform == stability.JonnezTransform))
            {
                isCarried = evt.IsCarried;
            }

            stability.HandleSpecialCarry(evt);
            equipment.OnSpecialObjectCarry(evt);
        }

        private void HandleVehicleTeleport(VehicleTeleportEvent evt)
        {
            stability.HandleTeleport(evt);
            equipment.OnVehicleTeleport(evt);
        }

        private void HandleRemoteTelemetry(VehicleRemoteTelemetryEvent evt)
        {
            stability.HandleRemoteTelemetry(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            if (!ctx.Transport.IsServer) return;

            using (var p = new PacketWriter(PacketType.JonnezStateSync))
            {
                p.WriteByte((byte)JonnezSubPacket.FullSnapshot);
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteBoolean(equipment.IsKickstandDown);
                p.WriteVector3(stability.ActiveCenterOfMass);
                p.WriteBoolean(equipment.IsStowedInHayosiko);
                p.WriteInt32(equipment.CargoAuthorityPlayerId);
                p.WriteVector3(equipment.CargoLocalPosition);
                p.WriteQuaternion(equipment.CargoLocalRotation);
                p.WriteBoolean(equipment.LocalHelmetEquipped);
                p.WriteInt32(equipment.HelmetItemHash);
                ctx.Transport.SendTo(p, clientId);
            }
        }

        private void HandleClientDisconnected(int clientId)
        {
            int targetId = (ctx?.Transport != null && ctx.Transport.IsClient) ? 0 : clientId;
            if (remoteDriverId == targetId)
            {
                remoteDriverId = -1;
                stability.OnDismounted(equipment.IsKickstandDown);
            }
            equipment.OnRemoteDisconnected(targetId);
        }

        private void HandleJonnezStateSync(PacketReader reader, int connectionId)
        {
            JonnezSubPacket sub = (JonnezSubPacket)reader.ReadByte();
            int senderId = reader.ReadInt32();
            int localId = ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;
            if (senderId == localId) return;

            switch (sub)
            {
                case JonnezSubPacket.KickstandState:
                {
                    bool down = reader.ReadBoolean();
                    equipment.ApplyRemoteKickstand(down);
                    break;
                }
                case JonnezSubPacket.CenterOfMassState:
                {
                    Vector3 com = reader.ReadVector3();
                    stability.ApplyRemoteCenterOfMass(com);
                    break;
                }
                case JonnezSubPacket.HelmetState:
                {
                    bool equipped = reader.ReadBoolean();
                    int itemHash = reader.ReadInt32();
                    equipment.ApplyRemoteHelmetState(senderId, equipped, itemHash);
                    break;
                }
                case JonnezSubPacket.CargoHayosikoState:
                {
                    bool stowed = reader.ReadBoolean();
                    int authId = reader.ReadInt32();
                    Vector3 localPos = reader.ReadVector3();
                    Quaternion localRot = reader.ReadQuaternion();
                    equipment.ApplyRemoteCargoState(stowed, authId, localPos, localRot);
                    break;
                }
                case JonnezSubPacket.FullSnapshot:
                {
                    bool kickstandDown = reader.ReadBoolean();
                    Vector3 com = reader.ReadVector3();
                    bool stowed = reader.ReadBoolean();
                    int authId = reader.ReadInt32();
                    Vector3 localPos = reader.ReadVector3();
                    Quaternion localRot = reader.ReadQuaternion();
                    bool hostHelmet = reader.ReadBoolean();
                    int helmetHash = reader.ReadInt32();

                    equipment.ApplyRemoteKickstand(kickstandDown);
                    stability.ApplyRemoteCenterOfMass(com);
                    equipment.ApplyRemoteCargoState(stowed, authId, localPos, localRot);
                    equipment.ApplyRemoteHelmetState(senderId, hostHelmet, helmetHash);
                    break;
                }
            }
        }
    }
}
