using System;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.WorldDomestic
{
    public sealed class WorldDomesticModule : IOscModule
    {
        public string ModuleId => "WorldDomestic";
        public string DisplayName => "Дом, Быт и Выживание";
        public string StatusSummary =>
            $"{sleepSystem?.StatusSummary ?? "-"} | {powerWaterSync?.StatusSummary ?? "-"} | {saunaFireSync?.StatusSummary ?? "-"} | {survivalHazardsSync?.StatusSummary ?? "-"}";

        private OscHostContext ctx;
        private CooperativeSleepSystem sleepSystem;
        private HousePowerAndWaterSync powerWaterSync;
        private SaunaAndFireSync saunaFireSync;
        private DomesticSurvivalAndHazardsSync survivalHazardsSync;

        private GameObject guiHostObj;
        private GuiBridgeBehaviour guiHost;

        private sealed class GuiBridgeBehaviour : MonoBehaviour
        {
            public Action DrawCallback;

            private void OnGUI()
            {
                DrawCallback?.Invoke();
            }
        }

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            sleepSystem = new CooperativeSleepSystem(ctx, ModuleId);
            powerWaterSync = new HousePowerAndWaterSync(ctx, ModuleId);
            saunaFireSync = new SaunaAndFireSync(ctx, ModuleId);
            survivalHazardsSync = new DomesticSurvivalAndHazardsSync(ctx, ModuleId);

            ctx.Router.Register(PacketType.PlayerSleepSync, ModuleId, sleepSystem.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.DomesticPowerAndWaterSync, ModuleId, powerWaterSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.DomesticSaunaAndFireSync, ModuleId, saunaFireSync.HandlePacket, relayOnServer: true);
            ctx.Router.Register(PacketType.DomesticAppliancesAndStatsSync, ModuleId, survivalHazardsSync.HandleAppliancesPacket, relayOnServer: true);
            ctx.Router.Register(PacketType.DomesticHazardsAndContainersSync, ModuleId, survivalHazardsSync.HandleHazardsPacket, relayOnServer: true);

            ctx.Events.Subscribe<PlayerUrinateStateEvent>(HandlePlayerUrinate);

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
            EnsureGuiHost();
            sleepSystem?.OnLevelLoaded();
            powerWaterSync?.OnLevelLoaded();
            saunaFireSync?.OnLevelLoaded();
            survivalHazardsSync?.OnLevelLoaded();
            ctx?.Log?.Invoke($"[WorldDomestic] Инициализирован ({StatusSummary}).");
        }

        public void OnUpdate()
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            sleepSystem?.OnUpdate();
            powerWaterSync?.OnUpdate();
            saunaFireSync?.OnUpdate();
            survivalHazardsSync?.OnUpdate();
        }

        public void OnFixedUpdate() { }
        public void OnLateUpdate() { }

        public void OnShutdown()
        {
            ctx?.Events?.Unsubscribe<PlayerUrinateStateEvent>(HandlePlayerUrinate);

            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }

            ctx?.Router?.UnregisterModule(ModuleId);
            ctx?.Ownership?.ReleaseModule(ModuleId);
            ResetAll();

            if (guiHostObj != null)
            {
                UnityEngine.Object.Destroy(guiHostObj);
                guiHostObj = null;
                guiHost = null;
            }
        }

        private void EnsureGuiHost()
        {
            if (guiHostObj != null && guiHost != null) return;
            guiHostObj = new GameObject("OSC_WorldDomestic_GuiHost");
            UnityEngine.Object.DontDestroyOnLoad(guiHostObj);
            guiHost = guiHostObj.AddComponent<GuiBridgeBehaviour>();
            guiHost.DrawCallback = () =>
            {
                if (ctx != null && ctx.IsInGameWorld)
                {
                    sleepSystem?.OnGUI();
                }
            };
        }

        private void ResetAll()
        {
            sleepSystem?.Reset();
            powerWaterSync?.Reset();
            saunaFireSync?.Reset();
            survivalHazardsSync?.Reset();
        }

        private void HandlePlayerUrinate(PlayerUrinateStateEvent evt)
        {
            saunaFireSync?.HandleUrinateBridge(evt);
        }

        private void HandleClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsServer) return;
            sleepSystem?.OnClientConnected(clientId);
            powerWaterSync?.OnClientConnected(clientId);
            saunaFireSync?.OnClientConnected(clientId);
            survivalHazardsSync?.OnClientConnected(clientId);
        }

        private void HandleClientDisconnected(int clientId)
        {
            sleepSystem?.OnClientDisconnected(clientId);
            powerWaterSync?.OnClientDisconnected(clientId);
            saunaFireSync?.OnClientDisconnected(clientId);
            survivalHazardsSync?.OnClientDisconnected(clientId);
        }
    }
}
