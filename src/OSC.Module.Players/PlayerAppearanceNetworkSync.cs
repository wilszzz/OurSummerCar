using System;
using System.Collections.Generic;
using System.IO;
using OSC.Core.Modules;
using OSC.Core.Network;
using UnityEngine;

namespace OSC.Module.Players
{
    internal sealed class PlayerAppearanceNetworkSync
    {
        private const int MaxChunksPerTick = 2;
        private const float ConnectedResendDelaySeconds = 1.5f;
        private const float RequestTimeoutSeconds = 12f;

        private struct TransferSlotKey : IEquatable<TransferSlotKey>
        {
            public readonly int PlayerId;
            public readonly byte Slot;

            public TransferSlotKey(int playerId, byte slot)
            {
                PlayerId = playerId;
                Slot = slot;
            }

            public bool Equals(TransferSlotKey other)
            {
                return PlayerId == other.PlayerId && Slot == other.Slot;
            }

            public override bool Equals(object obj)
            {
                return obj is TransferSlotKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (PlayerId * 397) ^ Slot;
                }
            }
        }

        private sealed class OutgoingChunkTransfer
        {
            public int TargetConnectionId;
            public int RequesterPlayerId;
            public byte Slot;
            public uint Crc32;
            public byte[] Data;
            public int Offset;
        }

        private sealed class IncomingChunkTransfer
        {
            public uint Crc32;
            public int TotalLength;
            public byte[] Buffer;
            public bool[] ReceivedChunks;
            public int ReceivedBytes;
        }

        private struct PendingRequestInfo
        {
            public uint Crc32;
            public float RequestedAtRealtime;
        }

        private struct PendingResend
        {
            public int TargetConnectionId;
            public float SendAtRealtime;
        }

        private readonly OscHostContext ctx;
        private readonly string moduleId;
        private readonly SkinPresetCollection profiles;
        private readonly CustomSkinTextureManager texManager;
        private readonly Action<int, PlayerAppearanceState> onRemoteAppearanceReady;
        private readonly Action<string> log;

        private readonly Dictionary<int, PlayerAppearanceState> remoteStates = new Dictionary<int, PlayerAppearanceState>();
        private readonly Dictionary<uint, byte[]> localEncodedByCrc = new Dictionary<uint, byte[]>();
        private readonly List<OutgoingChunkTransfer> outgoingTransfers = new List<OutgoingChunkTransfer>();
        private readonly Dictionary<TransferSlotKey, IncomingChunkTransfer> incomingTransfers = new Dictionary<TransferSlotKey, IncomingChunkTransfer>();
        private readonly Dictionary<TransferSlotKey, PendingRequestInfo> inFlightRequests = new Dictionary<TransferSlotKey, PendingRequestInfo>();
        private readonly List<PendingResend> pendingResends = new List<PendingResend>();
        private readonly List<TransferSlotKey> tempExpiredKeys = new List<TransferSlotKey>();

        private int LocalPlayerId => ctx?.Transport != null ? ctx.Transport.LocalPlayerId : 0;

        public PlayerAppearanceNetworkSync(
            OscHostContext context,
            string moduleId,
            SkinPresetCollection profiles,
            CustomSkinTextureManager texManager,
            Action<int, PlayerAppearanceState> onRemoteAppearanceReady,
            Action<string> log)
        {
            ctx = context;
            this.moduleId = moduleId;
            this.profiles = profiles;
            this.texManager = texManager;
            this.onRemoteAppearanceReady = onRemoteAppearanceReady;
            this.log = log;
        }

        public void CacheLocalEncodedBytes(uint crc32, byte[] encodedBytes)
        {
            if (crc32 == 0 || encodedBytes == null || encodedBytes.Length == 0 || encodedBytes.Length > SkinCatalogConstants.MaxEncodedTextureBytes)
            {
                return;
            }
            localEncodedByCrc[crc32] = encodedBytes;
        }

        public PlayerAppearanceState GetRemoteState(int playerId)
        {
            return remoteStates.TryGetValue(playerId, out PlayerAppearanceState state) ? state : null;
        }

        public void Reset()
        {
            remoteStates.Clear();
            outgoingTransfers.Clear();
            incomingTransfers.Clear();
            inFlightRequests.Clear();
            pendingResends.Clear();
        }

        public void BroadcastLocalAppearance()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            SendAppearanceStateSync(-1);
        }

        public void OnClientConnected(int clientId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            int remoteId = ctx.Transport.IsClient ? 0 : clientId;
            ClearRemoteClientTransfers(remoteId, clientId);

            SendAppearanceStateSync(clientId);

            float resendAt = Time.realtimeSinceStartup + ConnectedResendDelaySeconds;
            for (int i = 0; i < pendingResends.Count; i++)
            {
                if (pendingResends[i].TargetConnectionId == clientId)
                {
                    pendingResends[i] = new PendingResend { TargetConnectionId = clientId, SendAtRealtime = resendAt };
                    return;
                }
            }
            pendingResends.Add(new PendingResend { TargetConnectionId = clientId, SendAtRealtime = resendAt });
        }

        public void OnClientDisconnected(int clientId)
        {
            int targetId = (ctx?.Transport != null && ctx.Transport.LocalPlayerId == 1) ? 0 : clientId;
            remoteStates.Remove(targetId);
            ClearRemoteClientTransfers(targetId, clientId);

            for (int i = pendingResends.Count - 1; i >= 0; i--)
            {
                if (pendingResends[i].TargetConnectionId == clientId)
                {
                    pendingResends.RemoveAt(i);
                }
            }
        }

        public void OnUpdate()
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning)
            {
                if (outgoingTransfers.Count > 0) outgoingTransfers.Clear();
                if (pendingResends.Count > 0) pendingResends.Clear();
                return;
            }

            float now = Time.realtimeSinceStartup;

            for (int i = pendingResends.Count - 1; i >= 0; i--)
            {
                if (now >= pendingResends[i].SendAtRealtime)
                {
                    int connId = pendingResends[i].TargetConnectionId;
                    pendingResends.RemoveAt(i);
                    SendAppearanceStateSync(connId);
                }
            }

            if (inFlightRequests.Count > 0)
            {
                tempExpiredKeys.Clear();
                foreach (var kv in inFlightRequests)
                {
                    if (now - kv.Value.RequestedAtRealtime >= RequestTimeoutSeconds)
                    {
                        tempExpiredKeys.Add(kv.Key);
                    }
                }
                for (int i = 0; i < tempExpiredKeys.Count; i++)
                {
                    inFlightRequests.Remove(tempExpiredKeys[i]);
                }
            }

            PumpOutgoingChunks();
        }

        public void HandlePacket(PacketReader reader, int connectionId)
        {
            if (reader == null || reader.UnreadBytes < 1) return;

            byte subPacket = reader.ReadByte();
            switch (subPacket)
            {
                case AppearanceSubPacket.AppearanceStateSync:
                    HandleAppearanceStateSync(reader, connectionId);
                    break;
                case AppearanceSubPacket.CustomTextureRequest:
                    HandleCustomTextureRequest(reader, connectionId);
                    break;
                case AppearanceSubPacket.CustomTextureChunk:
                    HandleCustomTextureChunk(reader, connectionId);
                    break;
                case AppearanceSubPacket.CustomTextureAckOrClear:
                    HandleCustomTextureAckOrClear(reader);
                    break;
            }
        }

        private void SendAppearanceStateSync(int targetConnectionId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning || profiles?.Active == null) return;

            PlayerAppearanceState st = profiles.Active;
            byte cleanMask = 0;
            for (int i = 0; i < 4; i++)
            {
                if (st.HasCustomTexture((CustomTexSlot)i))
                {
                    cleanMask |= (byte)(1 << i);
                }
            }

            using (var p = new PacketWriter(PacketType.PlayerAppearance))
            {
                p.WriteByte(AppearanceSubPacket.AppearanceStateSync);
                p.WriteInt32(LocalPlayerId);
                for (int i = 0; i < 6; i++)
                {
                    p.WriteByte(st.SlotIndices[i]);
                }
                p.WriteByte(st.FilterPointMask);
                p.WriteByte(cleanMask);

                for (int i = 0; i < 4; i++)
                {
                    if ((cleanMask & (1 << i)) != 0)
                    {
                        p.WriteInt32(unchecked((int)st.CustomCrc32[i]));
                        p.WriteInt32(st.CustomByteLengths[i]);
                    }
                }

                SendPacket(p, targetConnectionId);
            }
        }

        private void HandleAppearanceStateSync(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 12) return;

            int playerId = reader.ReadInt32();
            if (playerId == LocalPlayerId || playerId < 0 || playerId > 32) return;

            var remote = new PlayerAppearanceState();
            for (int i = 0; i < 6; i++)
            {
                remote.SlotIndices[i] = reader.ReadByte();
            }
            remote.FilterPointMask = reader.ReadByte();
            byte customMask = reader.ReadByte();

            for (int i = 0; i < 4; i++)
            {
                if ((customMask & (1 << i)) == 0) continue;
                if (reader.UnreadBytes < 8) return;

                uint crc = unchecked((uint)reader.ReadInt32());
                int byteLen = reader.ReadInt32();
                if (crc != 0 && byteLen > 0 && byteLen <= SkinCatalogConstants.MaxEncodedTextureBytes)
                {
                    remote.SetCustomTexture((CustomTexSlot)i, string.Empty, crc, byteLen);
                }
            }

            remoteStates[playerId] = remote;

            for (int i = 0; i < 4; i++)
            {
                var slot = (CustomTexSlot)i;
                var key = new TransferSlotKey(playerId, (byte)i);

                if (!remote.HasCustomTexture(slot))
                {
                    incomingTransfers.Remove(key);
                    inFlightRequests.Remove(key);
                    continue;
                }

                uint crc = remote.CustomCrc32[i];
                bool point = remote.IsPointFiltered(slot);
                Texture2D readyTex = texManager != null ? texManager.GetTextureByCrc(crc, point) : null;
                if (readyTex == null)
                {
                    if (!inFlightRequests.TryGetValue(key, out PendingRequestInfo req) || req.Crc32 != crc)
                    {
                        incomingTransfers.Remove(key);
                        inFlightRequests[key] = new PendingRequestInfo
                        {
                            Crc32 = crc,
                            RequestedAtRealtime = Time.realtimeSinceStartup
                        };
                        SendCustomTextureRequest(connectionId, playerId, (byte)i, crc);
                    }
                }
                else
                {
                    inFlightRequests.Remove(key);
                }
            }

            onRemoteAppearanceReady?.Invoke(playerId, remote);
        }

        private void SendCustomTextureRequest(int targetConnectionId, int ownerPlayerId, byte texSlot, uint crc32)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            using (var p = new PacketWriter(PacketType.PlayerAppearance))
            {
                p.WriteByte(AppearanceSubPacket.CustomTextureRequest);
                p.WriteInt32(LocalPlayerId);
                p.WriteInt32(ownerPlayerId);
                p.WriteByte(texSlot);
                p.WriteInt32(unchecked((int)crc32));
                SendPacket(p, targetConnectionId);
            }
        }

        private void HandleCustomTextureRequest(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 13) return;

            int requesterId = reader.ReadInt32();
            int targetOwnerId = reader.ReadInt32();
            byte texSlot = reader.ReadByte();
            uint crc32 = unchecked((uint)reader.ReadInt32());

            if (targetOwnerId != LocalPlayerId) return;
            if (requesterId == LocalPlayerId || requesterId < 0 || requesterId > 32 || texSlot >= 4 || crc32 == 0) return;

            byte[] data = ResolveLocalEncodedBytes((CustomTexSlot)texSlot, crc32);
            if (data == null || data.Length == 0 || data.Length > SkinCatalogConstants.MaxEncodedTextureBytes)
            {
                log?.Invoke($"[{moduleId}] Не найдены локальные байты кастомной текстуры слота {texSlot} (CRC {crc32:X8}) для отправки игроку #{requesterId}.");
                SendCustomTextureAckOrClear(connectionId, LocalPlayerId, texSlot, crc32, isClear: true);
                return;
            }

            for (int i = outgoingTransfers.Count - 1; i >= 0; i--)
            {
                OutgoingChunkTransfer existing = outgoingTransfers[i];
                if (existing.RequesterPlayerId == requesterId && existing.Slot == texSlot)
                {
                    if (existing.Crc32 == crc32)
                    {
                        return;
                    }
                    outgoingTransfers.RemoveAt(i);
                }
            }

            outgoingTransfers.Add(new OutgoingChunkTransfer
            {
                TargetConnectionId = connectionId,
                RequesterPlayerId = requesterId,
                Slot = texSlot,
                Crc32 = crc32,
                Data = data,
                Offset = 0
            });
        }

        private void PumpOutgoingChunks()
        {
            if (outgoingTransfers.Count == 0) return;

            int sentThisTick = 0;
            while (sentThisTick < MaxChunksPerTick && outgoingTransfers.Count > 0)
            {
                OutgoingChunkTransfer tx = outgoingTransfers[0];
                if (tx.Data == null || tx.Offset >= tx.Data.Length)
                {
                    outgoingTransfers.RemoveAt(0);
                    continue;
                }

                int chunkLen = Mathf.Min(SkinCatalogConstants.ChunkSizeBytes, tx.Data.Length - tx.Offset);
                byte[] slice = new byte[chunkLen];
                Buffer.BlockCopy(tx.Data, tx.Offset, slice, 0, chunkLen);

                using (var p = new PacketWriter(PacketType.PlayerAppearance))
                {
                    p.WriteByte(AppearanceSubPacket.CustomTextureChunk);
                    p.WriteInt32(LocalPlayerId);
                    p.WriteByte(tx.Slot);
                    p.WriteInt32(unchecked((int)tx.Crc32));
                    p.WriteInt32(tx.Data.Length);
                    p.WriteInt32(tx.Offset);
                    p.WriteByte((byte)(chunkLen & 0xFF));
                    p.WriteByte((byte)((chunkLen >> 8) & 0xFF));
                    p.WriteByteArray(slice);
                    SendPacket(p, tx.TargetConnectionId);
                }

                tx.Offset += chunkLen;
                sentThisTick++;

                if (tx.Offset >= tx.Data.Length)
                {
                    outgoingTransfers.RemoveAt(0);
                }
            }
        }

        private void HandleCustomTextureChunk(PacketReader reader, int connectionId)
        {
            if (reader.UnreadBytes < 19) return;

            int ownerPlayerId = reader.ReadInt32();
            byte texSlot = reader.ReadByte();
            uint crc32 = unchecked((uint)reader.ReadInt32());
            int totalLength = reader.ReadInt32();
            int offset = reader.ReadInt32();
            ushort chunkLen = (ushort)(reader.ReadByte() | (reader.ReadByte() << 8));

            if (ownerPlayerId == LocalPlayerId || ownerPlayerId < 0 || ownerPlayerId > 32 || texSlot >= 4 || crc32 == 0) return;
            if (totalLength <= 0 || totalLength > SkinCatalogConstants.MaxEncodedTextureBytes) return;
            if (chunkLen == 0 || chunkLen > SkinCatalogConstants.ChunkSizeBytes || offset < 0 || offset + chunkLen > totalLength) return;
            if (reader.UnreadBytes < 4 + chunkLen) return;

            byte[] data = reader.ReadByteArray();
            if (data == null || data.Length != chunkLen) return;

            var key = new TransferSlotKey(ownerPlayerId, texSlot);
            bool hasRemoteState = remoteStates.TryGetValue(ownerPlayerId, out PlayerAppearanceState remoteState);
            if (hasRemoteState && remoteState.HasCustomTexture((CustomTexSlot)texSlot) && remoteState.CustomCrc32[texSlot] != crc32)
            {
                return;
            }

            bool requested = inFlightRequests.TryGetValue(key, out PendingRequestInfo req) && req.Crc32 == crc32;
            bool expectedByState = hasRemoteState && remoteState.HasCustomTexture((CustomTexSlot)texSlot) && remoteState.CustomCrc32[texSlot] == crc32;
            if (!requested && !expectedByState) return;

            if (!incomingTransfers.TryGetValue(key, out IncomingChunkTransfer tx) || tx.Crc32 != crc32 || tx.TotalLength != totalLength)
            {
                int chunkCount = (totalLength + SkinCatalogConstants.ChunkSizeBytes - 1) / SkinCatalogConstants.ChunkSizeBytes;
                tx = new IncomingChunkTransfer
                {
                    Crc32 = crc32,
                    TotalLength = totalLength,
                    Buffer = new byte[totalLength],
                    ReceivedChunks = new bool[chunkCount],
                    ReceivedBytes = 0
                };
                incomingTransfers[key] = tx;
            }

            if (offset % SkinCatalogConstants.ChunkSizeBytes != 0) return;
            int chunkIdx = offset / SkinCatalogConstants.ChunkSizeBytes;
            if (chunkIdx < 0 || chunkIdx >= tx.ReceivedChunks.Length) return;

            if (!tx.ReceivedChunks[chunkIdx])
            {
                Buffer.BlockCopy(data, 0, tx.Buffer, offset, chunkLen);
                tx.ReceivedChunks[chunkIdx] = true;
                tx.ReceivedBytes += chunkLen;
            }

            if (tx.ReceivedBytes < tx.TotalLength) return;

            incomingTransfers.Remove(key);
            inFlightRequests.Remove(key);

            uint computedCrc = SkinCrc32.Compute(tx.Buffer);
            if (computedCrc != crc32)
            {
                log?.Invoke($"[{moduleId}] Повреждённая текстура слота {texSlot} от игрока #{ownerPlayerId} (CRC {computedCrc:X8} != {crc32:X8}).");
                if (hasRemoteState && remoteState.CustomCrc32[texSlot] == crc32)
                {
                    remoteState.ClearCustomTexture((CustomTexSlot)texSlot);
                    onRemoteAppearanceReady?.Invoke(ownerPlayerId, remoteState);
                }
                return;
            }

            bool pointFilter = hasRemoteState && remoteState.IsPointFiltered((CustomTexSlot)texSlot);
            if (texManager != null && texManager.StoreRemoteTextureBytes(crc32, tx.Buffer, pointFilter, out _))
            {
                SendCustomTextureAckOrClear(connectionId, ownerPlayerId, texSlot, crc32, isClear: false);
                if (hasRemoteState)
                {
                    onRemoteAppearanceReady?.Invoke(ownerPlayerId, remoteState);
                }
            }
        }

        private void SendCustomTextureAckOrClear(int targetConnectionId, int ownerPlayerId, byte texSlot, uint crc32, bool isClear)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;

            using (var p = new PacketWriter(PacketType.PlayerAppearance))
            {
                p.WriteByte(AppearanceSubPacket.CustomTextureAckOrClear);
                p.WriteInt32(ownerPlayerId);
                p.WriteByte(texSlot);
                p.WriteInt32(unchecked((int)crc32));
                p.WriteBoolean(isClear);
                SendPacket(p, targetConnectionId);
            }
        }

        private void HandleCustomTextureAckOrClear(PacketReader reader)
        {
            if (reader.UnreadBytes < 10) return;

            int ownerPlayerId = reader.ReadInt32();
            byte texSlot = reader.ReadByte();
            uint crc32 = unchecked((uint)reader.ReadInt32());
            bool isClear = reader.ReadBoolean();

            if (!isClear || ownerPlayerId == LocalPlayerId || ownerPlayerId < 0 || ownerPlayerId > 32 || texSlot >= 4) return;

            var key = new TransferSlotKey(ownerPlayerId, texSlot);
            incomingTransfers.Remove(key);
            inFlightRequests.Remove(key);

            if (remoteStates.TryGetValue(ownerPlayerId, out PlayerAppearanceState st) &&
                st.HasCustomTexture((CustomTexSlot)texSlot) &&
                st.CustomCrc32[texSlot] == crc32)
            {
                st.ClearCustomTexture((CustomTexSlot)texSlot);
                onRemoteAppearanceReady?.Invoke(ownerPlayerId, st);
            }
        }

        private byte[] ResolveLocalEncodedBytes(CustomTexSlot slot, uint crc32)
        {
            if (localEncodedByCrc.TryGetValue(crc32, out byte[] cached) && cached != null && cached.Length > 0)
            {
                return cached;
            }

            if (texManager != null)
            {
                int slotIdx = (int)slot;
                if (profiles?.Active != null && profiles.Active.HasCustomTexture(slot) && profiles.Active.CustomCrc32[slotIdx] == crc32)
                {
                    string relPath = profiles.Active.CustomRelativePaths[slotIdx];
                    if (!string.IsNullOrEmpty(relPath) &&
                        texManager.TryPrepareLocalTexture(relPath, slot, out uint preparedCrc, out byte[] preparedBytes, out _, out _) &&
                        preparedCrc == crc32 &&
                        preparedBytes != null &&
                        preparedBytes.Length > 0)
                    {
                        localEncodedByCrc[crc32] = preparedBytes;
                        return preparedBytes;
                    }
                }

                if (profiles?.Presets != null)
                {
                    for (int i = 0; i < profiles.Presets.Length; i++)
                    {
                        PlayerAppearanceState preset = profiles.Presets[i];
                        if (preset == null || !preset.HasCustomTexture(slot) || preset.CustomCrc32[slotIdx] != crc32) continue;
                        string relPath = preset.CustomRelativePaths[slotIdx];
                        if (!string.IsNullOrEmpty(relPath) &&
                            texManager.TryPrepareLocalTexture(relPath, slot, out uint preparedCrc, out byte[] preparedBytes, out _, out _) &&
                            preparedCrc == crc32 &&
                            preparedBytes != null &&
                            preparedBytes.Length > 0)
                        {
                            localEncodedByCrc[crc32] = preparedBytes;
                            return preparedBytes;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(texManager.CachePath))
                {
                    string cacheFile = Path.Combine(texManager.CachePath, crc32.ToString("X8") + ".bin");
                    if (File.Exists(cacheFile))
                    {
                        try
                        {
                            byte[] diskBytes = File.ReadAllBytes(cacheFile);
                            if (diskBytes != null && diskBytes.Length > 0 &&
                                diskBytes.Length <= SkinCatalogConstants.MaxEncodedTextureBytes &&
                                SkinCrc32.Compute(diskBytes) == crc32)
                            {
                                localEncodedByCrc[crc32] = diskBytes;
                                return diskBytes;
                            }
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        private void ClearRemoteClientTransfers(int remotePlayerId, int connectionId)
        {
            for (int s = 0; s < 4; s++)
            {
                var key = new TransferSlotKey(remotePlayerId, (byte)s);
                incomingTransfers.Remove(key);
                inFlightRequests.Remove(key);
            }

            for (int i = outgoingTransfers.Count - 1; i >= 0; i--)
            {
                OutgoingChunkTransfer tx = outgoingTransfers[i];
                if (tx.RequesterPlayerId == remotePlayerId || tx.TargetConnectionId == connectionId)
                {
                    outgoingTransfers.RemoveAt(i);
                }
            }
        }

        private void SendPacket(PacketWriter packet, int targetConnectionId)
        {
            if (ctx?.Transport == null || !ctx.Transport.IsRunning) return;
            if (targetConnectionId >= 0) ctx.Transport.SendTo(packet, targetConnectionId);
            else ctx.Transport.Send(packet);
        }
    }
}
