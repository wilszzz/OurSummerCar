using System;
using System.Collections.Generic;

namespace OSC.Core.Network
{
    /// <summary>
    /// Маршрутизатор пакетов с жёсткой изоляцией: каждому PacketType соответствует один модуль-владелец.
    /// </summary>
    public sealed class PacketRouter
    {
        private struct HandlerEntry
        {
            public string OwnerModuleId;
            public bool RelayOnServer;
            public Action<PacketReader, int> Handler;
        }

        private readonly NetworkTransport transport;
        private readonly Dictionary<PacketType, HandlerEntry> handlers = new Dictionary<PacketType, HandlerEntry>();
        private readonly Action<string> log;

        public PacketRouter(NetworkTransport transport, Action<string> log)
        {
            this.transport = transport;
            this.log = log;
            this.transport.OnRawPacketReceived += DispatchPacket;
        }

        public void Register(PacketType type, string moduleId, Action<PacketReader, int> handler, bool relayOnServer = false)
        {
            if (handler == null) return;

            if (handlers.TryGetValue(type, out var existing) && existing.OwnerModuleId != moduleId)
            {
                log?.Invoke($"[PacketRouter] Конфликт пакета {type}: уже занят модулем '{existing.OwnerModuleId}', отклонён '{moduleId}'.");
                return;
            }

            handlers[type] = new HandlerEntry
            {
                OwnerModuleId = moduleId,
                RelayOnServer = relayOnServer,
                Handler = handler
            };
        }

        public void UnregisterModule(string moduleId)
        {
            var keys = new List<PacketType>();
            foreach (var kv in handlers)
            {
                if (kv.Value.OwnerModuleId == moduleId)
                {
                    keys.Add(kv.Key);
                }
            }

            for (int i = 0; i < keys.Count; i++)
            {
                handlers.Remove(keys[i]);
            }
        }

        private void DispatchPacket(byte[] rawData, PacketReader reader, int connectionId)
        {
            if (!handlers.TryGetValue(reader.Type, out var entry)) return;

            if (entry.RelayOnServer && transport.IsServer)
            {
                transport.SendRaw(rawData, connectionId);
            }

            try
            {
                entry.Handler?.Invoke(reader, connectionId);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[PacketRouter] Ошибка в модуле '{entry.OwnerModuleId}' при обработке {reader.Type}: {ex.Message}");
            }
        }
    }
}
