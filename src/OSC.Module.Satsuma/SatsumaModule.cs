using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;

namespace OSC.Module.Satsuma
{
    public sealed class SatsumaModule : IOscModule
    {
        public string ModuleId => "Satsuma";
        public string DisplayName => "Сборка и Тюнинг Satsuma";
        public string StatusSummary =>
            $"{assemblyAndBolts?.StatusSummary ?? "-"} | {tuningAndFluids?.StatusSummary ?? "-"} | {fleetariAndInspection?.StatusSummary ?? "-"}";

        private OscHostContext ctx;
        private SatsumaAssemblyAndBolts assemblyAndBolts;
        private SatsumaTuningAndFluids tuningAndFluids;
        private SatsumaFleetariAndInspection fleetariAndInspection;

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            assemblyAndBolts = new SatsumaAssemblyAndBolts(ctx, ModuleId);
            tuningAndFluids = new SatsumaTuningAndFluids(ctx, ModuleId);
            fleetariAndInspection = new SatsumaFleetariAndInspection(ctx, ModuleId);

            ctx.Router.Register(PacketType.PartAssemble, ModuleId, assemblyAndBolts.HandlePartAssemble, relayOnServer: true);
            ctx.Router.Register(PacketType.PartRemove, ModuleId, assemblyAndBolts.HandlePartRemove, relayOnServer: true);
            ctx.Router.Register(PacketType.PartBoltScrew, ModuleId, assemblyAndBolts.HandlePartBoltScrew, relayOnServer: true);
            ctx.Router.Register(PacketType.PartAssembleSnapshot, ModuleId, assemblyAndBolts.HandlePartAssembleSnapshot, relayOnServer: true);

            ctx.Router.Register(PacketType.SatsumaTuningSync, ModuleId, tuningAndFluids.HandleTuningPacket, relayOnServer: true);
            ctx.Router.Register(PacketType.SatsumaFluidsAndWearSync, ModuleId, tuningAndFluids.HandleFluidsWearPacket, relayOnServer: true);
            ctx.Router.Register(PacketType.SatsumaFleetariAndCatalogSync, ModuleId, fleetariAndInspection.HandleFleetariPacket, relayOnServer: true);

            ctx.Events.Subscribe<SharedWalletChangedEvent>(HandleSharedWalletChanged);

            if (ctx.Transport != null)
            {
                ctx.Transport.OnClientConnected += HandleClientConnected;
                ctx.Transport.OnClientDisconnected += HandleClientDisconnected;
            }
        }

        public void OnMenuLoaded()
        {
            ResetAll();
        }

        public void OnLevelLoaded()
        {
            ResetAll();
            assemblyAndBolts?.OnLevelLoaded();
            tuningAndFluids?.OnLevelLoaded();
            fleetariAndInspection?.OnLevelLoaded();
            ctx?.Log?.Invoke($"[Satsuma] Инициализирован ({StatusSummary}).");
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            assemblyAndBolts?.OnUpdate();
            tuningAndFluids?.OnUpdate();
            fleetariAndInspection?.OnUpdate();
        }

        public void OnFixedUpdate() { }
        public void OnLateUpdate() { }

        public void OnShutdown()
        {
            ctx?.Events?.Unsubscribe<SharedWalletChangedEvent>(HandleSharedWalletChanged);

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetAll();
        }

        private void ResetAll()
        {
            assemblyAndBolts?.Reset();
            tuningAndFluids?.Reset();
            fleetariAndInspection?.Reset();
        }

        private void HandleSharedWalletChanged(SharedWalletChangedEvent evt)
        {
            fleetariAndInspection?.HandleSharedWalletEvent(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            assemblyAndBolts?.OnClientConnected(clientId);
            tuningAndFluids?.OnClientConnected(clientId);
            fleetariAndInspection?.OnClientConnected(clientId);
        }

        private void HandleClientDisconnected(int clientId)
        {
            assemblyAndBolts?.OnClientDisconnected(clientId);
            tuningAndFluids?.OnClientDisconnected(clientId);
            fleetariAndInspection?.OnClientDisconnected(clientId);
        }
    }
}
