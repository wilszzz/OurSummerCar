using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using OSC.Core.Bridges;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.Players
{
    public sealed class PlayersModule : IOscModule
    {
        public string ModuleId => "Players";
        public string DisplayName => "Игроки и Анимации";
        public string StatusSummary => $"В сети: {puppets.Count}, 3D: {(CharacterAssetLoader.IsLoaded ? "OK" : CharacterAssetLoader.StatusMessage)}";

        private OscHostContext ctx;
        private GameObject runtimeHostObj;
        private RuntimeCoroutineBridge coroutineHost;
        private readonly LocalPlayerAnimationTracker animTracker = new LocalPlayerAnimationTracker();
        private readonly Dictionary<int, RemotePuppet> puppets = new Dictionary<int, RemotePuppet>();
        private readonly Dictionary<int, string> nicknames = new Dictionary<int, string>();

        private SkinPresetCollection skinProfiles;
        private CustomSkinTextureManager texManager;
        private PlayerAppearanceNetworkSync appearanceSync;
        private OptionsMenuSkinUI optionsMenuSkinUi;
        private SkinWardrobeStudioUI wardrobeUi;
        private bool isApplyingAppearanceChange;

        private Transform localPlayer;
        private CharacterController localCc;
        private Transform localCamPivot;
        private FsmFloat playerUrineVar;
        private bool isLocalPeeing;
        private bool localSeatedByBridge;
        private bool localSleepingByBridge;
        private float nextSendTime;
        private float nextPeeEventTime;
        private float nextHandshakeTime;
        private string lastSentNick = string.Empty;

        private sealed class RuntimeCoroutineBridge : MonoBehaviour
        {
            public Action OnUpdateCallback;
            public Action OnGuiCallback;

            private void Update()
            {
                OnUpdateCallback?.Invoke();
            }

            private void OnGUI()
            {
                OnGuiCallback?.Invoke();
            }
        }

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            skinProfiles = new SkinPresetCollection();
            texManager = new CustomSkinTextureManager(ctx.Log);
            texManager.EnsureFoldersAndScan();
            skinProfiles.Load(texManager.ProfilesConfigPath, ctx.Log);

            appearanceSync = new PlayerAppearanceNetworkSync(ctx, ModuleId, skinProfiles, texManager, HandleRemoteAppearanceReady, ctx.Log);
            wardrobeUi = new SkinWardrobeStudioUI(skinProfiles, texManager, HandleLocalAppearanceChanged, ctx.Log);
            optionsMenuSkinUi = new OptionsMenuSkinUI(skinProfiles, texManager, HandleLocalAppearanceChanged, ctx.Log);

            ctx.Router.Register(PacketType.HandshakeRequest, ModuleId, HandleHandshakeRequest);
            ctx.Router.Register(PacketType.HandshakeResponse, ModuleId, HandleHandshakeResponse);
            ctx.Router.Register(PacketType.PlayerSync, ModuleId, HandlePlayerSync, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerGesture, ModuleId, HandlePlayerGesture, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerUrinate, ModuleId, HandlePlayerUrinate, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerSwear, ModuleId, HandlePlayerSwear, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerAppearance, ModuleId, appearanceSync.HandlePacket, relayOnServer: true);

            ctx.Events.Subscribe<RemotePlayerSeatEvent>(HandleRemoteSeatBridge);
            ctx.Events.Subscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancyBridge);
            ctx.Events.Subscribe<PlayerHeldItemEvent>(HandleHeldItemBridge);
            ctx.Events.Subscribe<LocalPlayerSleepStateEvent>(HandleLocalSleepBridge);
            ctx.Events.Subscribe<RemotePlayerSleepStateEvent>(HandleRemoteSleepBridge);
            ctx.Events.Subscribe<ToggleSkinWardrobeEvent>(HandleToggleSkinWardrobe);

            ctx.Transport.OnClientConnected += HandleClientConnected;
            ctx.Transport.OnClientDisconnected += HandleClientDisconnected;

            animTracker.OnGestureAction += SendGesture;
            animTracker.OnSwearAction += SendSwear;

            EnsureCoroutineHost();
            CharacterAssetLoader.LoadAssets(ctx.Log);
            PrepareAndCacheActiveLocalTextures();
        }

        public void OnMenuLoaded()
        {
            ClearWorldState();
            optionsMenuSkinUi?.Cleanup();
            wardrobeUi?.Cleanup();
            EnsureCoroutineHost();
        }

        public void OnLevelLoaded()
        {
            ClearWorldState();
            EnsureCoroutineHost();
            CharacterAssetLoader.LoadAssets(ctx.Log);
            PrepareAndCacheActiveLocalTextures();
            wardrobeUi?.RefreshPreview();
            if (PlayMakerGlobals.Instance?.Variables != null)
            {
                playerUrineVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerUrine");
            }
        }

        public void OnShutdown()
        {
            if (ctx?.Events != null)
            {
                ctx.Events.Unsubscribe<RemotePlayerSeatEvent>(HandleRemoteSeatBridge);
                ctx.Events.Unsubscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancyBridge);
                ctx.Events.Unsubscribe<PlayerHeldItemEvent>(HandleHeldItemBridge);
                ctx.Events.Unsubscribe<LocalPlayerSleepStateEvent>(HandleLocalSleepBridge);
                ctx.Events.Unsubscribe<RemotePlayerSleepStateEvent>(HandleRemoteSleepBridge);
                ctx.Events.Unsubscribe<ToggleSkinWardrobeEvent>(HandleToggleSkinWardrobe);
            }
            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }
            ctx?.Router?.UnregisterModule(ModuleId);
            optionsMenuSkinUi?.Cleanup();
            wardrobeUi?.Cleanup();
            texManager?.DisposeTextures();
            appearanceSync?.Reset();
            ClearWorldState();
            if (runtimeHostObj != null)
            {
                UnityEngine.Object.Destroy(runtimeHostObj);
                runtimeHostObj = null;
                coroutineHost = null;
            }
        }

        private void EnsureCoroutineHost()
        {
            if (runtimeHostObj == null)
            {
                runtimeHostObj = new GameObject("OSC_Players_CoroutineHost");
                UnityEngine.Object.DontDestroyOnLoad(runtimeHostObj);
                coroutineHost = runtimeHostObj.AddComponent<RuntimeCoroutineBridge>();
            }
            if (coroutineHost != null)
            {
                coroutineHost.OnUpdateCallback = HandleBridgeUpdate;
                coroutineHost.OnGuiCallback = HandleBridgeGui;
            }
        }

        private void HandleBridgeUpdate()
        {
            if ((Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.F9)) && GUIUtility.keyboardControl == 0)
            {
                wardrobeUi?.Toggle();
            }
            wardrobeUi?.OnUpdate();
            appearanceSync?.OnUpdate();
        }

        private void HandleBridgeGui()
        {
            wardrobeUi?.OnGUI();
        }

        private void ClearWorldState()
        {
            foreach (var p in puppets.Values) p.Destroy();
            puppets.Clear();
            animTracker.Reset();
            if (ctx?.Transport == null || !ctx.Transport.IsRunning)
            {
                appearanceSync?.Reset();
            }
            localPlayer = null;
            localCc = null;
            localCamPivot = null;
            localSeatedByBridge = false;
            localSleepingByBridge = false;
            isLocalPeeing = false;
            nextPeeEventTime = 0f;
            lastSentNick = string.Empty;
        }

        public void OnUpdate()
        {
            optionsMenuSkinUi?.TickUpdate();
            animTracker.IsLocalSeatedByBridge = localSeatedByBridge;
            animTracker.Tick(coroutineHost);

            UpdateLocalUrine();

            foreach (var p in puppets.Values)
            {
                p.UpdateInterpolation(Time.deltaTime);
            }

            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;

            if (ctx.LocalNickname != lastSentNick || Time.time >= nextHandshakeTime)
            {
                nextHandshakeTime = Time.time + 2.5f;
                SendHandshake(ctx.LocalNickname);
            }

            if (Time.time >= nextSendTime)
            {
                nextSendTime = Time.time + 0.05f;
                SendLocalTransform();
            }
        }

        public void OnFixedUpdate() { }
        public void OnLateUpdate() { }

        private void UpdateLocalUrine()
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            bool peeing = false;
            try { peeing = cInput.GetButton("Urinate"); } catch { }
            if (!peeing) peeing = Input.GetKey(KeyCode.P);
            if (peeing && playerUrineVar != null && playerUrineVar.Value <= 0.5f) peeing = false;

            if (peeing != isLocalPeeing)
            {
                isLocalPeeing = peeing;
                using (var p = new PacketWriter(PacketType.PlayerUrinate))
                {
                    p.WriteInt32(ctx.Transport.LocalPlayerId);
                    p.WriteBoolean(isLocalPeeing);
                    ctx.Transport.Send(p);
                }
                PublishLocalUrinateBridge(isLocalPeeing);
                nextPeeEventTime = Time.time + 0.2f;
            }
            else if (isLocalPeeing && Time.time >= nextPeeEventTime)
            {
                nextPeeEventTime = Time.time + 0.2f;
                PublishLocalUrinateBridge(true);
            }
        }

        private void PublishLocalUrinateBridge(bool peeing)
        {
            Transform originT = localCamPivot != null ? localCamPivot : localPlayer;
            Vector3 pos = originT != null ? originT.position : Vector3.zero;
            Vector3 fwd = originT != null ? originT.forward : Vector3.forward;
            ctx?.Events?.Publish(new PlayerUrinateStateEvent
            {
                PlayerId = ctx.Transport != null ? ctx.Transport.LocalPlayerId : 0,
                IsLocal = true,
                IsPeeing = peeing,
                Position = pos,
                Forward = fwd
            });
        }

        private void SendLocalTransform()
        {
            if (localPlayer == null || !localPlayer.gameObject.activeInHierarchy)
            {
                GameObject pObj = GameObject.Find("PLAYER") ?? GameObject.FindWithTag("Player");
                if (pObj != null)
                {
                    localPlayer = pObj.transform;
                    localCc = pObj.GetComponent<CharacterController>();
                }
            }
            if (localPlayer == null) return;

            if (localCamPivot == null && Camera.main != null) localCamPivot = Camera.main.transform;

            Vector3 pos = localPlayer.position;
            float rotY = localCamPivot != null ? localCamPivot.eulerAngles.y : localPlayer.eulerAngles.y;
            float pitch = 0f;
            float lean = 0f;

            if (localCamPivot != null)
            {
                pitch = localCamPivot.localEulerAngles.x;
                if (pitch > 180f) pitch -= 360f;
                lean = localCamPivot.localEulerAngles.z;
                if (lean > 180f) lean -= 360f;
            }

            bool leaningQ = Input.GetKey(KeyCode.Q);
            if (leaningQ) lean = 0f;
            else if (Mathf.Abs(lean) < 0.5f && Input.GetKey(KeyCode.E)) lean = -15f;

            bool isAirborne = !animTracker.IsPlayerInCar && !localSeatedByBridge && localCc != null && !localCc.isGrounded;

            byte animState = 0;
            if (animTracker.IsSmoking) animState |= 1;
            if (animTracker.CurrentCrouchLevel == 1) animState |= 2;
            else if (animTracker.CurrentCrouchLevel >= 2) animState |= 32;
            if (animTracker.IsPlayerInCar || localSeatedByBridge) animState |= 4;
            if (isLocalPeeing) animState |= 8;
            if (leaningQ) animState |= 64;
            if (localSleepingByBridge) animState |= 128;

            ctx.Events.Publish(new LocalPlayerTransformUpdatedEvent
            {
                Position = pos,
                Nickname = ctx.LocalNickname
            });

            using (var p = new PacketWriter(PacketType.PlayerSync))
            {
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteVector3(pos);
                p.WriteSingle(rotY);
                p.WriteSingle(pitch);
                p.WriteByte(animState);
                p.WriteSingle(lean);
                p.WriteBoolean(isAirborne);
                ctx.Transport.Send(p);
            }
        }

        private void SendHandshake(string nick)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            lastSentNick = nick;
            PacketType t = ctx.Transport.IsServer ? PacketType.HandshakeResponse : PacketType.HandshakeRequest;
            using (var p = new PacketWriter(t))
            {
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteString(nick ?? "Player");
                ctx.Transport.Send(p);
            }
        }

        private void SendGesture(byte idx, bool start, byte extra)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.PlayerGesture))
            {
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteByte(idx);
                p.WriteBoolean(start);
                p.WriteByte(extra);
                ctx.Transport.Send(p);
            }
        }

        private void SendSwear(byte swearId)
        {
            if (ctx.Transport == null || !ctx.Transport.IsRunning) return;
            using (var p = new PacketWriter(PacketType.PlayerSwear))
            {
                p.WriteInt32(ctx.Transport.LocalPlayerId);
                p.WriteByte(swearId);
                ctx.Transport.Send(p);
            }
        }

        private void HandleClientConnected(int clientId)
        {
            SendHandshake(ctx.LocalNickname);
            appearanceSync?.OnClientConnected(clientId);
        }

        private void HandleClientDisconnected(int clientId)
        {
            int targetId = ctx.Transport.IsClient ? 0 : clientId;
            if (puppets.TryGetValue(targetId, out var puppet))
            {
                puppet.Destroy();
                puppets.Remove(targetId);
            }
            appearanceSync?.OnClientDisconnected(clientId);
        }

        private void HandleHandshakeRequest(PacketReader reader, int connectionId)
        {
            int id = reader.ReadInt32();
            string nick = reader.ReadString();
            if (id == ctx.Transport.LocalPlayerId) return;

            nicknames[id] = nick;
            if (puppets.TryGetValue(id, out var p)) p.SetNickname(nick);

            if (ctx.Transport.IsServer)
            {
                using (var resp = new PacketWriter(PacketType.HandshakeResponse))
                {
                    resp.WriteInt32(0);
                    resp.WriteString(ctx.LocalNickname ?? "Хост");
                    ctx.Transport.SendTo(resp, connectionId);
                }
            }
        }

        private void HandleHandshakeResponse(PacketReader reader, int connectionId)
        {
            int id = reader.ReadInt32();
            string nick = reader.ReadString();
            if (id == ctx.Transport.LocalPlayerId) return;

            nicknames[id] = nick;
            if (puppets.TryGetValue(id, out var p)) p.SetNickname(nick);
        }

        private void HandlePlayerSync(PacketReader reader, int connectionId)
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            int playerId = reader.ReadInt32();
            if (playerId == ctx.Transport.LocalPlayerId || playerId < 0 || playerId > 32) return;

            Vector3 pos = reader.ReadVector3();
            float rotY = reader.ReadSingle();
            float pitch = reader.ReadSingle();
            byte animState = reader.ReadByte();
            float lean = reader.ReadSingle();
            bool airborne = reader.ReadBoolean();

            if (!puppets.TryGetValue(playerId, out var puppet) || puppet.RootObject == null)
            {
                puppet?.Destroy();
                puppet = new RemotePuppet(playerId, pos, Quaternion.Euler(0f, rotY, 0f), ctx.Log);
                puppets[playerId] = puppet;
                if (nicknames.TryGetValue(playerId, out string n)) puppet.SetNickname(n);
                PlayerAppearanceState remoteAppearance = appearanceSync?.GetRemoteState(playerId);
                if (remoteAppearance != null)
                {
                    puppet.ApplyAppearance(remoteAppearance, texManager);
                }
            }
            puppet.UpdateTarget(pos, rotY, pitch, animState, lean, airborne);
        }

        private void HandlePlayerGesture(PacketReader reader, int connectionId)
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            int playerId = reader.ReadInt32();
            if (playerId == ctx.Transport.LocalPlayerId) return;
            byte idx = reader.ReadByte();
            bool start = reader.ReadBoolean();
            byte extra = reader.ReadByte();

            if (puppets.TryGetValue(playerId, out var p))
            {
                if (start) p.TriggerGesture(idx, extra);
                else p.StopGesture(idx);
            }
        }

        private void HandlePlayerUrinate(PacketReader reader, int connectionId)
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            int playerId = reader.ReadInt32();
            if (playerId == ctx.Transport.LocalPlayerId) return;
            bool peeing = reader.ReadBoolean();
            if (puppets.TryGetValue(playerId, out var p))
            {
                p.SetPeeing(peeing);
                if (p.RootObject != null)
                {
                    ctx.Events?.Publish(new PlayerUrinateStateEvent
                    {
                        PlayerId = playerId,
                        IsLocal = false,
                        IsPeeing = peeing,
                        Position = p.RootObject.transform.position + Vector3.up * 0.62f,
                        Forward = p.RootObject.transform.forward
                    });
                }
            }
        }

        private void HandlePlayerSwear(PacketReader reader, int connectionId)
        {
            if (ctx == null || !ctx.IsInGameWorld) return;
            int playerId = reader.ReadInt32();
            if (playerId == ctx.Transport.LocalPlayerId) return;
            byte swearId = reader.ReadByte();
            if (puppets.TryGetValue(playerId, out var p)) p.PlaySwear(swearId);
        }

        private void HandleRemoteSeatBridge(RemotePlayerSeatEvent evt)
        {
            if (puppets.TryGetValue(evt.PlayerId, out var puppet))
            {
                puppet.ApplySeatEvent(evt);
            }
        }

        private void HandleLocalOccupancyBridge(LocalPlayerOccupancyEvent evt)
        {
            localSeatedByBridge = evt.IsSeated;
        }

        private void HandleHeldItemBridge(PlayerHeldItemEvent evt)
        {
            if (puppets.TryGetValue(evt.PlayerId, out var puppet))
            {
                if (evt.IsHolding) puppet.HoldItem(evt.ItemObject);
                else puppet.DropItem(evt.ItemObject);
            }
        }

        private void HandleLocalSleepBridge(LocalPlayerSleepStateEvent evt)
        {
            localSleepingByBridge = evt.IsSleeping;
        }

        private void HandleRemoteSleepBridge(RemotePlayerSleepStateEvent evt)
        {
            if (puppets.TryGetValue(evt.PlayerId, out var puppet))
            {
                puppet.ApplySleepStateEvent(evt);
            }
        }

        private void HandleToggleSkinWardrobe(ToggleSkinWardrobeEvent evt)
        {
            wardrobeUi?.Toggle();
        }

        private void HandleLocalAppearanceChanged()
        {
            if (isApplyingAppearanceChange) return;
            isApplyingAppearanceChange = true;
            try
            {
                PrepareAndCacheActiveLocalTextures();
                appearanceSync?.BroadcastLocalAppearance();
                optionsMenuSkinUi?.RefreshVisualsFromActive();
                wardrobeUi?.RefreshPreview();
            }
            finally
            {
                isApplyingAppearanceChange = false;
            }
        }

        private void HandleRemoteAppearanceReady(int playerId, PlayerAppearanceState state)
        {
            if (state != null && puppets.TryGetValue(playerId, out var puppet) && puppet != null)
            {
                puppet.ApplyAppearance(state, texManager);
            }
        }

        private void PrepareAndCacheActiveLocalTextures()
        {
            if (skinProfiles?.Active == null || texManager == null) return;
            for (int i = 0; i < 4; i++)
            {
                var slot = (CustomTexSlot)i;
                string relPath = skinProfiles.Active.CustomRelativePaths[i];
                if (string.IsNullOrEmpty(relPath)) continue;

                if (texManager.TryPrepareLocalTexture(relPath, slot, out uint crc, out byte[] encodedBytes, out _, out string err))
                {
                    skinProfiles.Active.SetCustomTexture(slot, relPath, crc, encodedBytes != null ? encodedBytes.Length : 0);
                    appearanceSync?.CacheLocalEncodedBytes(crc, encodedBytes);
                }
                else if (skinProfiles.Active.HasCustomTexture(slot))
                {
                    ctx?.Log?.Invoke($"[Игроки/Скины] Не удалось загрузить локальный скин '{relPath}' ({slot}): {err}");
                    skinProfiles.Active.ClearCustomTexture(slot);
                }
            }
        }
    }
}
