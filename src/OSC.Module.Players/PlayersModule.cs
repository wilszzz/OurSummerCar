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

        private Transform localPlayer;
        private CharacterController localCc;
        private Transform localCamPivot;
        private FsmFloat playerUrineVar;
        private bool isLocalPeeing;
        private bool localSeatedByBridge;
        private float nextSendTime;
        private float nextHandshakeTime;
        private string lastSentNick = string.Empty;

        private sealed class RuntimeCoroutineBridge : MonoBehaviour { }

        public void Initialize(OscHostContext context)
        {
            ctx = context;

            ctx.Router.Register(PacketType.HandshakeRequest, ModuleId, HandleHandshakeRequest);
            ctx.Router.Register(PacketType.HandshakeResponse, ModuleId, HandleHandshakeResponse);
            ctx.Router.Register(PacketType.PlayerSync, ModuleId, HandlePlayerSync, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerGesture, ModuleId, HandlePlayerGesture, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerUrinate, ModuleId, HandlePlayerUrinate, relayOnServer: true);
            ctx.Router.Register(PacketType.PlayerSwear, ModuleId, HandlePlayerSwear, relayOnServer: true);

            ctx.Events.Subscribe<RemotePlayerSeatEvent>(HandleRemoteSeatBridge);
            ctx.Events.Subscribe<LocalPlayerOccupancyEvent>(HandleLocalOccupancyBridge);
            ctx.Events.Subscribe<PlayerHeldItemEvent>(HandleHeldItemBridge);

            ctx.Transport.OnClientConnected += HandleClientConnected;
            ctx.Transport.OnClientDisconnected += HandleClientDisconnected;

            animTracker.OnGestureAction += SendGesture;
            animTracker.OnSwearAction += SendSwear;
        }

        public void OnMenuLoaded() => ClearWorldState();

        public void OnLevelLoaded()
        {
            ClearWorldState();
            EnsureCoroutineHost();
            CharacterAssetLoader.LoadAssets(ctx.Log);
            if (PlayMakerGlobals.Instance?.Variables != null)
            {
                playerUrineVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerUrine");
            }
        }

        public void OnShutdown()
        {
            if (ctx?.Transport != null)
            {
                ctx.Transport.OnClientConnected -= HandleClientConnected;
                ctx.Transport.OnClientDisconnected -= HandleClientDisconnected;
            }
            ctx?.Router?.UnregisterModule(ModuleId);
            ClearWorldState();
        }

        private void EnsureCoroutineHost()
        {
            if (runtimeHostObj != null) return;
            runtimeHostObj = new GameObject("OSC_Players_CoroutineHost");
            UnityEngine.Object.DontDestroyOnLoad(runtimeHostObj);
            coroutineHost = runtimeHostObj.AddComponent<RuntimeCoroutineBridge>();
        }

        private void ClearWorldState()
        {
            foreach (var p in puppets.Values) p.Destroy();
            puppets.Clear();
            animTracker.Reset();
            localPlayer = null;
            localCc = null;
            localCamPivot = null;
            localSeatedByBridge = false;
            isLocalPeeing = false;
            lastSentNick = string.Empty;
        }

        public void OnUpdate()
        {
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
            }
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

        private void HandleClientConnected(int clientId) => SendHandshake(ctx.LocalNickname);

        private void HandleClientDisconnected(int clientId)
        {
            int targetId = ctx.Transport.IsClient ? 0 : clientId;
            if (puppets.TryGetValue(targetId, out var puppet))
            {
                puppet.Destroy();
                puppets.Remove(targetId);
            }
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
            }
            puppet.UpdateTarget(pos, rotY, pitch, animState, lean, airborne);
        }

        private void HandlePlayerGesture(PacketReader reader, int connectionId)
        {
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
            int playerId = reader.ReadInt32();
            if (playerId == ctx.Transport.LocalPlayerId) return;
            bool peeing = reader.ReadBoolean();
            if (puppets.TryGetValue(playerId, out var p)) p.SetPeeing(peeing);
        }

        private void HandlePlayerSwear(PacketReader reader, int connectionId)
        {
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
    }
}
