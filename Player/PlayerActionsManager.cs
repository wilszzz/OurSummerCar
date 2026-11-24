using System;
using HutongGames.PlayMaker;
using MSCLoader;
using OurSummerCar.Core;
using OurSummerCar.Network;
using UnityEngine;

namespace OurSummerCar.Player
{
    public class PlayerActionsManager : MonoBehaviour
    {
        public static PlayerActionsManager Instance { get; private set; }
        private NetworkTransport transport;
        private PlayerManager playerManager;
        public Action<string> OnLog;

        private bool isLocalPeeing = false;
        public bool IsLocalPeeing => isLocalPeeing;
        private FsmFloat playerUrineVar;
        private Transform kiuasTr;
        private PlayMakerFSM kiuasFsm;

        private readonly string[] swearAudioNames = new string[]
        {
            "vittu", "saatana", "perkele", "haista_paska", "jumalauta"
        };

        public void Reset()
        {
            isLocalPeeing = false;
            playerUrineVar = null;
            kiuasTr = null;
            kiuasFsm = null;
        }

        public void Initialize(NetworkTransport netTransport, PlayerManager pm = null)
        {
            Instance = this;
            transport = netTransport;
            playerManager = pm;
            transport.OnPacketReceived += HandlePacket;

            if (PlayMakerGlobals.Instance != null && PlayMakerGlobals.Instance.Variables != null)
            {
                playerUrineVar = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerUrine");
            }

            var kiuasObj = GameObject.Find("Kiuas") ?? GameObject.Find("SmokeHomeKiuas");
            if (kiuasObj != null)
            {
                kiuasTr = kiuasObj.transform;
                kiuasFsm = kiuasObj.GetComponent<PlayMakerFSM>();
            }
        }

        private void OnDestroy()
        {
            if (transport != null)
            {
                transport.OnPacketReceived -= HandlePacket;
            }
        }

        private void Update()
        {
            // 1. Проверка cInput ("Urinate") или клавиши P
            bool peeing = false;
            try
            {
                peeing = cInput.GetButton("Urinate");
            }
            catch { }

            if (!peeing)
            {
                peeing = Input.GetKey(KeyCode.P);
            }

            // Мочеиспускание возможно только если у персонажа есть моча в организме (шкала > 0.5)
            if (peeing && playerUrineVar != null && playerUrineVar.Value <= 0.5f)
            {
                peeing = false;
            }

            if (peeing != isLocalPeeing)
            {
                isLocalPeeing = peeing;
                SendUrinate(isLocalPeeing);

                if (isLocalPeeing)
                {
                    CheckPeeOnSauna();
                }
            }

            // Клавиша N и маты обрабатываются нативно через Speech FSM в LocalPlayerAnimationTracker,
            // исключая дублирование звуков и одновременную ругань обоих игроков.
        }

        private void CheckPeeOnSauna()
        {
            if (kiuasTr != null)
            {
                var player = GameObject.Find("PLAYER");
                if (player != null)
                {
                    float distSqr = (player.transform.position - kiuasTr.position).sqrMagnitude;
                    if (distSqr < 4.0f) // В радиусе 2 метров от каменки сауны
                    {
                        if (kiuasFsm != null)
                        {
                            kiuasFsm.SendEvent("STEAM");
                            OnLog?.Invoke("[Сауна] Вы помочились на каменку сауны! Пошел едкий пар!");
                        }
                    }
                }
            }
        }

        public void SendUrinate(bool peeing)
        {
            int myId = (transport != null && transport.IsServer) ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.PlayerUrinate))
            {
                packet.WriteInt32(myId);
                packet.WriteBoolean(peeing);
                transport.Send(packet);
            }
        }

        public void SendSwear(byte swearIndex)
        {
            int myId = (transport != null && transport.IsServer) ? 0 : 1;
            using (var packet = new PacketWriter(PacketType.PlayerSwear))
            {
                packet.WriteInt32(myId);
                packet.WriteByte(swearIndex);
                transport.Send(packet);
            }
        }

        private void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader.Type == PacketType.PlayerUrinate)
            {
                int senderId = reader.ReadInt32();
                bool peeing = reader.ReadBoolean();

                int myId = (transport != null && transport.IsServer) ? 0 : 1;
                if (senderId == myId) return;

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.PlayerUrinate))
                    {
                        relay.WriteInt32(senderId);
                        relay.WriteBoolean(peeing);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteUrinate(senderId, peeing);
            }
            else if (reader.Type == PacketType.PlayerSwear)
            {
                int senderId = reader.ReadInt32();
                byte swearIdx = reader.ReadByte();

                int myId = (transport != null && transport.IsServer) ? 0 : 1;
                if (senderId == myId) return;

                if (transport.IsServer)
                {
                    using (var relay = new PacketWriter(PacketType.PlayerSwear))
                    {
                        relay.WriteInt32(senderId);
                        relay.WriteByte(swearIdx);
                        transport.Send(relay, connectionId);
                    }
                }

                ApplyRemoteSwear(senderId, swearIdx);
            }
        }

        private void ApplyRemoteUrinate(int senderId, bool peeing)
        {
            RemotePuppet puppet = playerManager != null ? playerManager.GetPuppet(senderId) : null;
            if (puppet != null)
            {
                puppet.SetPeeing(peeing);

                if (peeing && kiuasTr != null && puppet.RootObject != null)
                {
                    float distSqr = (puppet.RootObject.transform.position - kiuasTr.position).sqrMagnitude;
                    if (distSqr < 4.0f && kiuasFsm != null)
                    {
                        kiuasFsm.SendEvent("STEAM");
                        OnLog?.Invoke("[Сауна] Напарник помочился на каменку! Пошел едкий пар!");
                    }
                }
            }

            if (peeing)
            {
                OnLog?.Invoke("[Сетка] Напарник мочится.");
            }
        }

        private void ApplyRemoteSwear(int senderId, byte swearIdx)
        {
            OnLog?.Invoke("[Сетка] Напарник ругается по-фински!");
            RemotePuppet puppet = playerManager != null ? playerManager.GetPuppet(senderId) : null;
            if (puppet != null)
            {
                puppet.PlaySwearAudio(swearIdx);
            }
        }
    }
}
