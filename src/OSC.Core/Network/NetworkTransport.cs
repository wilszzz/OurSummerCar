using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OSC.Core.Network
{
    public sealed class NetworkTransport
    {
        private sealed class ConnectedClient
        {
            public int Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public Thread ReadThread;
            public bool IsAlive = true;
        }

        private struct EventItem
        {
            public enum EventType { Connected, Disconnected, Data }
            public EventType Type;
            public int ConnectionId;
            public byte[] Data;
        }

        private TcpListener serverListener;
        private Thread serverAcceptThread;
        private readonly List<ConnectedClient> serverClients = new List<ConnectedClient>();

        private TcpClient clientSocket;
        private NetworkStream clientStream;
        private Thread clientReadThread;

        private readonly object queueLock = new object();
        private readonly object clientSendLock = new object();
        private readonly Queue<EventItem> eventQueue = new Queue<EventItem>();

        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsConnecting { get; private set; }
        public bool IsRunning => IsServer || (IsClient && clientSocket != null && clientSocket.Connected);
        public int LocalPlayerId => IsServer ? 0 : 1;

        public int ConnectedClientsCount
        {
            get
            {
                lock (serverClients) return serverClients.Count;
            }
        }

        public long LastPingRTT { get; private set; } = -1;

        public event Action<int> OnClientConnected;
        public event Action<int> OnClientDisconnected;
        public event Action<byte[], PacketReader, int> OnRawPacketReceived;
        public event Action<string> OnLog;

        private int currentServerPort = -1;

        public void StartServer(int port)
        {
            if (IsServer && serverListener != null && currentServerPort == port)
            {
                Log($"[Сервер] Уже запущен на порту {port}.");
                return;
            }

            Stop();
            IsServer = true;
            IsClient = false;
            currentServerPort = port;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    serverListener = new TcpListener(IPAddress.Any, port);
                    try { serverListener.ExclusiveAddressUse = false; } catch { }
                    try { serverListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
                    serverListener.Start();
                    serverAcceptThread = new Thread(ServerAcceptLoop) { IsBackground = true, Name = "OSC_ServerAcceptThread" };
                    serverAcceptThread.Start();
                    Log($"[Сервер] Запущен на порту {port}. Ожидание подключений...");
                    return;
                }
                catch (SocketException sex)
                {
                    if (serverListener != null)
                    {
                        try { serverListener.Stop(); } catch { }
                        serverListener = null;
                    }

                    if (attempt < 2)
                    {
                        Thread.Sleep(150);
                    }
                    else
                    {
                        currentServerPort = -1;
                        Log($"[Сервер] Ошибка запуска: {sex.Message}");
                        Stop();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    currentServerPort = -1;
                    Log($"[Сервер] Ошибка запуска: {ex.Message}");
                    Stop();
                    return;
                }
            }
        }

        public void Connect(string host, int port)
        {
            Stop();
            IsServer = false;
            IsClient = true;
            IsConnecting = true;

            Log($"[Клиент] Подключение к {host}:{port}...");

            var connectThread = new Thread(() =>
            {
                try
                {
                    var sock = new TcpClient { NoDelay = true };
                    var asyncResult = sock.BeginConnect(host, port, null, null);
                    bool success = asyncResult.AsyncWaitHandle.WaitOne(6000, true);

                    if (!success || !sock.Connected)
                    {
                        try { sock.Close(); } catch { }
                        IsConnecting = false;
                        IsClient = false;
                        Log($"[Клиент] Таймаут подключения к {host}:{port}");
                        return;
                    }

                    sock.EndConnect(asyncResult);
                    clientSocket = sock;
                    clientStream = sock.GetStream();
                    clientReadThread = new Thread(ClientReadLoop) { IsBackground = true, Name = "OSC_ClientReadThread" };
                    clientReadThread.Start();

                    IsConnecting = false;
                    EnqueueEvent(new EventItem { Type = EventItem.EventType.Connected, ConnectionId = 0 });
                    Log($"[Клиент] Подключено к {host}:{port}");
                    SendPing();
                }
                catch (Exception ex)
                {
                    IsConnecting = false;
                    Log($"[Клиент] Ошибка подключения: {ex.Message}");
                    Stop();
                }
            })
            { IsBackground = true, Name = "OSC_ConnectThread" };

            connectThread.Start();
        }

        public void Stop()
        {
            IsConnecting = false;
            currentServerPort = -1;

            if (serverListener != null)
            {
                try
                {
                    if (serverListener.Server != null)
                    {
                        try { serverListener.Server.LingerState = new LingerOption(true, 0); } catch { }
                        try { serverListener.Server.Close(0); } catch { }
                    }
                    serverListener.Stop();
                }
                catch { }
                serverListener = null;
            }

            if (serverAcceptThread != null && serverAcceptThread.IsAlive)
            {
                try { serverAcceptThread.Abort(); } catch { }
                serverAcceptThread = null;
            }

            lock (serverClients)
            {
                foreach (var c in serverClients)
                {
                    c.IsAlive = false;
                    try
                    {
                        c.Stream?.Close();
                        if (c.Client?.Client != null)
                        {
                            try { c.Client.Client.LingerState = new LingerOption(true, 0); } catch { }
                            try { c.Client.Client.Close(0); } catch { }
                        }
                        c.Client?.Close();
                    }
                    catch { }
                    if (c.ReadThread != null && c.ReadThread.IsAlive)
                    {
                        try { c.ReadThread.Abort(); } catch { }
                    }
                }
                serverClients.Clear();
            }

            if (clientSocket != null)
            {
                try
                {
                    clientStream?.Close();
                    if (clientSocket.Client != null)
                    {
                        try { clientSocket.Client.LingerState = new LingerOption(true, 0); } catch { }
                        try { clientSocket.Client.Close(0); } catch { }
                    }
                    clientSocket.Close();
                }
                catch { }
                clientSocket = null;
                clientStream = null;
            }

            if (clientReadThread != null && clientReadThread.IsAlive)
            {
                try { clientReadThread.Abort(); } catch { }
                clientReadThread = null;
            }

            lock (queueLock)
            {
                eventQueue.Clear();
            }

            IsServer = false;
            IsClient = false;
            LastPingRTT = -1;
            Log("[Сеть] Соединение остановлено.");
        }

        public void Send(PacketWriter packet, int excludeConnectionId = -1)
        {
            if (packet == null) return;
            SendRaw(packet.GetBytes(), excludeConnectionId);
        }

        public void SendRaw(byte[] rawPayload, int excludeConnectionId = -1)
        {
            if (rawPayload == null || rawPayload.Length == 0) return;

            byte[] framed = new byte[rawPayload.Length + 4];
            Buffer.BlockCopy(BitConverter.GetBytes(rawPayload.Length), 0, framed, 0, 4);
            Buffer.BlockCopy(rawPayload, 0, framed, 4, rawPayload.Length);

            if (IsServer)
            {
                lock (serverClients)
                {
                    foreach (var c in serverClients)
                    {
                        if (!c.IsAlive || c.Id == excludeConnectionId) continue;
                        try
                        {
                            lock (c.Stream)
                            {
                                c.Stream.Write(framed, 0, framed.Length);
                            }
                        }
                        catch { }
                    }
                }
            }
            else if (IsClient)
            {
                lock (clientSendLock)
                {
                    if (clientStream != null)
                    {
                        try { clientStream.Write(framed, 0, framed.Length); } catch { }
                    }
                }
            }
        }

        public void SendTo(PacketWriter packet, int targetConnectionId)
        {
            if (packet == null) return;
            byte[] raw = packet.GetBytes();
            byte[] framed = new byte[raw.Length + 4];
            Buffer.BlockCopy(BitConverter.GetBytes(raw.Length), 0, framed, 0, 4);
            Buffer.BlockCopy(raw, 0, framed, 4, raw.Length);

            if (IsServer)
            {
                lock (serverClients)
                {
                    foreach (var c in serverClients)
                    {
                        if (!c.IsAlive || c.Id != targetConnectionId) continue;
                        try
                        {
                            lock (c.Stream)
                            {
                                c.Stream.Write(framed, 0, framed.Length);
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
            else
            {
                SendRaw(raw);
            }
        }

        public void SendPing()
        {
            using (var p = new PacketWriter(PacketType.Ping))
            {
                p.WriteInt64(DateTime.UtcNow.Ticks);
                Send(p);
            }
        }

        public void PollEvents()
        {
            while (true)
            {
                EventItem ev;
                lock (queueLock)
                {
                    if (eventQueue.Count == 0) break;
                    ev = eventQueue.Dequeue();
                }

                switch (ev.Type)
                {
                    case EventItem.EventType.Connected:
                        OnClientConnected?.Invoke(ev.ConnectionId);
                        break;
                    case EventItem.EventType.Disconnected:
                        OnClientDisconnected?.Invoke(ev.ConnectionId);
                        break;
                    case EventItem.EventType.Data:
                        HandleIncomingPacket(ev.Data, ev.ConnectionId);
                        break;
                }
            }
        }

        private void HandleIncomingPacket(byte[] data, int connectionId)
        {
            if (data == null || data.Length == 0) return;
            using (var reader = new PacketReader(data))
            {
                if (reader.Type == PacketType.Ping)
                {
                    if (reader.UnreadBytes >= 8)
                    {
                        long sentTicks = reader.ReadInt64();
                        using (var pong = new PacketWriter(PacketType.Pong))
                        {
                            pong.WriteInt64(sentTicks);
                            SendTo(pong, connectionId);
                        }
                    }
                    return;
                }
                if (reader.Type == PacketType.Pong)
                {
                    if (reader.UnreadBytes >= 8)
                    {
                        long sentTicks = reader.ReadInt64();
                        if (sentTicks > 0)
                        {
                            long rtt = (DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond;
                            if (rtt >= 0 && rtt < 60000)
                            {
                                LastPingRTT = rtt;
                            }
                        }
                    }
                    return;
                }

                OnRawPacketReceived?.Invoke(data, reader, connectionId);
            }
        }

        private void ServerAcceptLoop()
        {
            while (IsServer && serverListener != null)
            {
                try
                {
                    var tcp = serverListener.AcceptTcpClient();
                    tcp.NoDelay = true;

                    lock (serverClients)
                    {
                        serverClients.RemoveAll(c => !c.IsAlive || c.Client == null || !c.Client.Connected);
                        if (serverClients.Count >= 1)
                        {
                            Log("[Сервер] Отклонено дополнительное подключение (лимит 2 игрока).");
                            try { tcp.Close(); } catch { }
                            continue;
                        }
                    }

                    const int id = 1;
                    var client = new ConnectedClient
                    {
                        Id = id,
                        Client = tcp,
                        Stream = tcp.GetStream()
                    };
                    client.ReadThread = new Thread(() => ServerClientReadLoop(client)) { IsBackground = true, Name = "OSC_ServerClientRead" };
                    client.ReadThread.Start();

                    lock (serverClients) { serverClients.Add(client); }
                    EnqueueEvent(new EventItem { Type = EventItem.EventType.Connected, ConnectionId = id });
                    Log($"[Сервер] Клиент #{id} подключился.");
                }
                catch (ThreadAbortException) { break; }
                catch { break; }
            }
        }

        private void ServerClientReadLoop(ConnectedClient client)
        {
            try
            {
                while (client.IsAlive && client.Client.Connected)
                {
                    byte[] packet = ReadFramedPacket(client.Stream);
                    if (packet == null) break;
                    EnqueueEvent(new EventItem { Type = EventItem.EventType.Data, ConnectionId = client.Id, Data = packet });
                }
            }
            catch { }
            finally
            {
                client.IsAlive = false;
                lock (serverClients) { serverClients.Remove(client); }
                EnqueueEvent(new EventItem { Type = EventItem.EventType.Disconnected, ConnectionId = client.Id });
                Log($"[Сервер] Клиент #{client.Id} отключился.");
            }
        }

        private void ClientReadLoop()
        {
            try
            {
                while (IsClient && clientSocket != null && clientSocket.Connected)
                {
                    byte[] packet = ReadFramedPacket(clientStream);
                    if (packet == null) break;
                    EnqueueEvent(new EventItem { Type = EventItem.EventType.Data, ConnectionId = 0, Data = packet });
                }
            }
            catch { }
            finally
            {
                EnqueueEvent(new EventItem { Type = EventItem.EventType.Disconnected, ConnectionId = 0 });
                Log("[Клиент] Отключено от сервера.");
            }
        }

        private static byte[] ReadFramedPacket(NetworkStream stream)
        {
            byte[] lenBytes = ReadExact(stream, 4);
            if (lenBytes == null) return null;

            int length = BitConverter.ToInt32(lenBytes, 0);
            if (length <= 0 || length > 16 * 1024 * 1024) return null;

            return ReadExact(stream, length);
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int r = stream.Read(buffer, read, count - read);
                if (r <= 0) return null;
                read += r;
            }
            return buffer;
        }

        private void EnqueueEvent(EventItem item)
        {
            lock (queueLock) { eventQueue.Enqueue(item); }
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }
}
