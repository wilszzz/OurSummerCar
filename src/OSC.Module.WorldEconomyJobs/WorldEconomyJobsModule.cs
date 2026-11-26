using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;

namespace OSC.Module.WorldEconomyJobs
{
    public sealed class WorldEconomyJobsModule : IOscModule
    {
        public string ModuleId => "WorldEconomyJobs";
        public string DisplayName => "Экономика, Работы, NPC и Сохранения";
        public string StatusSummary =>
            $"{storePubSync?.StatusSummary ?? "-"} | {jobsSync?.StatusSummary ?? "-"} | {storySync?.StatusSummary ?? "-"} | {npcPoliceSync?.StatusSummary ?? "-"} | {saveRespawnSync?.StatusSummary ?? "-"}";

        private OscHostContext ctx;
        private StoreAndPubGamblingSync storePubSync;
        private JobsAndWorkSync jobsSync;
        private StorylinesAndTelephoneSync storySync;
        private NpcTrafficAndPoliceSync npcPoliceSync;
        private SavegameAndRespawnSync saveRespawnSync;

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            storePubSync = new StoreAndPubGamblingSync(ctx, ModuleId);
            jobsSync = new JobsAndWorkSync(ctx, ModuleId);
            storySync = new StorylinesAndTelephoneSync(ctx, ModuleId);
            npcPoliceSync = new NpcTrafficAndPoliceSync(ctx, ModuleId);
            saveRespawnSync = new SavegameAndRespawnSync(ctx, ModuleId);

            ctx.Router.Register(PacketType.EconomyWalletAndStoreSync, ModuleId, storePubSync.HandleStorePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.PubAndGamblingSync, ModuleId, storePubSync.HandlePubPacket, relayOnServer: true);
            ctx.Router.Register(PacketType.JobsSync, ModuleId, jobsSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.StoryAndQuestsSync, ModuleId, storySync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.NpcTrafficAndPoliceSync, ModuleId, npcPoliceSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.SavegameAndRespawnSync, ModuleId, saveRespawnSync.HandlePacket, relayOnServer: true);

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
            storePubSync?.OnLevelLoaded();
            jobsSync?.OnLevelLoaded();
            storySync?.OnLevelLoaded();
            npcPoliceSync?.OnLevelLoaded();
            saveRespawnSync?.OnLevelLoaded();
            ctx?.Log?.Invoke($"[WorldEconomyJobs] Инициализирован ({StatusSummary}).");
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            storePubSync?.OnUpdate();
            jobsSync?.OnUpdate();
            storySync?.OnUpdate();
            npcPoliceSync?.OnUpdate();
            saveRespawnSync?.OnUpdate();
        }

        public void OnFixedUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            npcPoliceSync?.OnFixedUpdate();
        }

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
            storePubSync?.Reset();
            jobsSync?.Reset();
            storySync?.Reset();
            npcPoliceSync?.Reset();
            saveRespawnSync?.Reset();
        }

        private void HandleSharedWalletChanged(SharedWalletChangedEvent evt)
        {
            storePubSync?.HandleSharedWalletEvent(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            storePubSync?.OnClientConnected(clientId);
            jobsSync?.OnClientConnected(clientId);
            storySync?.OnClientConnected(clientId);
            npcPoliceSync?.OnClientConnected(clientId);
            saveRespawnSync?.OnClientConnected(clientId);
        }

        private void HandleClientDisconnected(int clientId)
        {
            storePubSync?.OnClientDisconnected(clientId);
            jobsSync?.OnClientDisconnected(clientId);
            storySync?.OnClientDisconnected(clientId);
            npcPoliceSync?.OnClientDisconnected(clientId);
            saveRespawnSync?.OnClientDisconnected(clientId);
        }
    }
}
