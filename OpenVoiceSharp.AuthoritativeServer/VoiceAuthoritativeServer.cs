using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class VoiceAuthoritativeServer : IDisposable
{
    private readonly ServerOptions Options;
    private readonly UdpClient Socket;
    private readonly WordPressAuthVerifier? WordPressAuthVerifier;
    private readonly Dictionary<Guid, ClientSession> SessionsById = [];
    private readonly Dictionary<string, HashSet<Guid>> RoomMembers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource StopTokenSource = new();
    private readonly ServerMetrics Metrics = new();
    private readonly object StateLock = new();
    private TcpListener? StatsListener;

    public VoiceAuthoritativeServer(ServerOptions options)
    {
        Options = options;
        Socket = new UdpClient(new IPEndPoint(IPAddress.Any, Options.Port));
        if (!string.IsNullOrWhiteSpace(Options.WordPressVerifyUrl))
        {
            WordPressAuthVerifier = new WordPressAuthVerifier(
                Options.WordPressVerifyUrl,
                Options.WordPressSharedSecret,
                Options.WordPressTimeoutSeconds
            );
        }
    }

    public void RequestStop() => StopTokenSource.Cancel();

    public async Task RunAsync()
    {
        Console.WriteLine($"Authoritative voice server listening on udp://0.0.0.0:{Options.Port}");
        if (WordPressAuthVerifier is not null)
            Console.WriteLine("WordPress auth verification is enabled.");
        if (Options.StatsPort > 0)
            Console.WriteLine($"Stats endpoint enabled on http://127.0.0.1:{Options.StatsPort}/stats");
        using PeriodicTimer cleanupTimer = new(TimeSpan.FromSeconds(1));

        Task receiveLoop = ReceiveLoopAsync(StopTokenSource.Token);
        Task cleanupLoop = CleanupLoopAsync(cleanupTimer, StopTokenSource.Token);
        Task statsLoop = Options.StatsPort > 0
            ? StatsLoopAsync(StopTokenSource.Token)
            : Task.CompletedTask;

        await Task.WhenAny(receiveLoop, cleanupLoop, statsLoop);
        StopTokenSource.Cancel();

        try
        {
            await Task.WhenAll(receiveLoop, cleanupLoop, statsLoop);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result = await Socket.ReceiveAsync(cancellationToken);
            await HandlePacketAsync(result.Buffer, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanupLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        int ticks = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddSeconds(-Options.ClientTimeoutSeconds);
            List<Guid> staleClientIds = [];

            lock (StateLock)
            {
                foreach (KeyValuePair<Guid, ClientSession> pair in SessionsById)
                {
                    if (pair.Value.LastSeenUtc < cutoffUtc)
                        staleClientIds.Add(pair.Key);
                }
            }

            foreach (Guid staleClientId in staleClientIds)
                RemoveClient(staleClientId, notifyRoom: true);

            ticks++;
            if (ticks % 10 == 0)
                LogMetrics();
        }
    }

    private async Task HandlePacketAsync(byte[] packet, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
    {
        if (packet.Length == 0)
            return;
        Interlocked.Increment(ref Metrics.PacketsReceived);

        ClientPacketType packetType = (ClientPacketType)packet[0];
        switch (packetType)
        {
            case ClientPacketType.Hello:
                Interlocked.Increment(ref Metrics.HelloPackets);
                await HandleHelloAsync(packet, remoteEndpoint, isAuthHello: false, cancellationToken).ConfigureAwait(false);
                break;
            case ClientPacketType.AuthHello:
                Interlocked.Increment(ref Metrics.AuthHelloPackets);
                await HandleHelloAsync(packet, remoteEndpoint, isAuthHello: true, cancellationToken).ConfigureAwait(false);
                break;
            case ClientPacketType.Voice:
                Interlocked.Increment(ref Metrics.VoicePacketsReceived);
                HandleVoice(packet, remoteEndpoint);
                break;
            case ClientPacketType.Leave:
                HandleLeave(packet, remoteEndpoint);
                break;
            case ClientPacketType.Ping:
                Interlocked.Increment(ref Metrics.PingPackets);
                HandlePing(packet, remoteEndpoint);
                break;
            default:
                SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Unknown packet type"), remoteEndpoint);
                Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
                break;
        }
    }

    private async Task HandleHelloAsync(
        byte[] packet,
        IPEndPoint remoteEndpoint,
        bool isAuthHello,
        CancellationToken cancellationToken
    )
    {
        Guid clientId;
        string room;
        string userName;
        string authToken = string.Empty;

        bool parsed = isAuthHello
            ? Protocol.TryReadAuthHello(packet, out clientId, out room, out userName, out authToken)
            : Protocol.TryReadHello(packet, out clientId, out room, out userName);
        if (!parsed)
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid hello packet"), remoteEndpoint);
            Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
            return;
        }

        if (WordPressAuthVerifier is not null)
        {
            if (!isAuthHello || string.IsNullOrWhiteSpace(authToken))
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.AuthFailed, "Authentication token is required."), remoteEndpoint);
                Interlocked.Increment(ref Metrics.AuthFailures);
                return;
            }

            (bool valid, string message) = await WordPressAuthVerifier.VerifyAsync(authToken, cancellationToken).ConfigureAwait(false);
            if (!valid)
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.AuthFailed, message), remoteEndpoint);
                Interlocked.Increment(ref Metrics.AuthFailures);
                LogEvent("auth_failed", ("endpoint", remoteEndpoint.ToString()), ("reason", message));
                return;
            }
        }

        room = room.Trim();
        userName = userName.Trim();
        if (room.Length == 0 || room.Length > 64 || userName.Length == 0 || userName.Length > 64)
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid room or user name"), remoteEndpoint);
            Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
            return;
        }

        lock (StateLock)
        {
            if (!RoomMembers.TryGetValue(room, out HashSet<Guid>? existingRoomMembers))
            {
                existingRoomMembers = [];
                RoomMembers[room] = existingRoomMembers;
            }

            if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
            {
                if (existingRoomMembers.Count >= Options.MaxRoomMembers)
                {
                    SendToEndpoint(Protocol.BuildError(ErrorCode.RoomFull, "Room is full"), remoteEndpoint);
                    return;
                }

                session = new ClientSession(clientId, room, userName, remoteEndpoint, Options.MaxVoicePacketsPerSecond);
                SessionsById[clientId] = session;
                existingRoomMembers.Add(clientId);
                Interlocked.Increment(ref Metrics.ClientsJoined);

                SendToEndpoint(Protocol.BuildWelcome(clientId), remoteEndpoint);
                BroadcastToRoom(room, Protocol.BuildPeerJoined(clientId, userName), exceptClientId: clientId);

                LogEvent("client_joined", ("clientId", clientId), ("room", room), ("user", userName), ("endpoint", remoteEndpoint.ToString()));
                return;
            }

            // Rejoin or endpoint migration for existing client id.
            if (!StringComparer.Ordinal.Equals(session.RoomName, room))
            {
                RemoveFromRoom(session.ClientId, session.RoomName);
                if (existingRoomMembers.Count >= Options.MaxRoomMembers)
                {
                    SendToEndpoint(Protocol.BuildError(ErrorCode.RoomFull, "Room is full"), remoteEndpoint);
                    return;
                }

                existingRoomMembers.Add(session.ClientId);
                session.RoomName = room;
            }

            if (!StringComparer.Ordinal.Equals(session.UserName, userName))
                session.UserName = userName;

            session.Endpoint = remoteEndpoint;
            session.LastSeenUtc = DateTime.UtcNow;

            SendToEndpoint(Protocol.BuildWelcome(clientId), remoteEndpoint);
            LogEvent("client_refreshed", ("clientId", clientId), ("room", room), ("endpoint", remoteEndpoint.ToString()));
        }
    }

    private void HandleVoice(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadVoice(packet, out Guid clientId, out uint sequence, out ReadOnlySpan<byte> payload))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid voice packet"), remoteEndpoint);
            Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
            return;
        }

        lock (StateLock)
        {
            if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.NotRegistered, "Client not registered"), remoteEndpoint);
                return;
            }

            if (!EndpointsEqual(session.Endpoint, remoteEndpoint))
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.UnauthorizedEndpoint, "Invalid source endpoint"), remoteEndpoint);
                Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
                return;
            }

            if (payload.Length == 0 || payload.Length > Options.MaxVoicePayloadBytes)
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.PayloadTooLarge, "Voice payload out of range"), remoteEndpoint);
                Interlocked.Increment(ref Metrics.PayloadDropped);
                return;
            }

            if (!session.VoiceRateLimiter.TryConsume(1))
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.RateLimited, "Voice packet rate exceeded"), remoteEndpoint);
                Interlocked.Increment(ref Metrics.RateLimitedDropped);
                return;
            }

            if (!session.VoiceSequenceWindow.TryAccept(sequence))
            {
                Interlocked.Increment(ref Metrics.ReplayDropped);
                return;
            }
            session.LastSeenUtc = DateTime.UtcNow;

            byte[] relayPacket = Protocol.BuildVoiceRelay(clientId, sequence, payload);
            BroadcastToRoom(session.RoomName, relayPacket, exceptClientId: clientId);
            Interlocked.Increment(ref Metrics.VoicePacketsRelayed);
        }
    }

    private void HandleLeave(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadLeave(packet, out Guid clientId))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid leave packet"), remoteEndpoint);
            Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
            return;
        }

        lock (StateLock)
        {
            if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
                return;

            if (!EndpointsEqual(session.Endpoint, remoteEndpoint))
            {
                SendToEndpoint(Protocol.BuildError(ErrorCode.UnauthorizedEndpoint, "Invalid source endpoint"), remoteEndpoint);
                Interlocked.Increment(ref Metrics.InvalidPacketsDropped);
                return;
            }

            RemoveClient(clientId, notifyRoom: true);
        }
    }

    private void HandlePing(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadPing(packet, out Guid clientId))
            return;

        lock (StateLock)
        {
            if (SessionsById.TryGetValue(clientId, out ClientSession? session) && EndpointsEqual(session.Endpoint, remoteEndpoint))
                session.LastSeenUtc = DateTime.UtcNow;
        }

        SendToEndpoint(Protocol.BuildPong(clientId), remoteEndpoint);
    }

    private void BroadcastToRoom(string room, byte[] packet, Guid? exceptClientId = null)
    {
        lock (StateLock)
        {
            if (!RoomMembers.TryGetValue(room, out HashSet<Guid>? memberIds))
                return;

            foreach (Guid memberId in memberIds)
            {
                if (exceptClientId.HasValue && memberId == exceptClientId.Value)
                    continue;
                if (!SessionsById.TryGetValue(memberId, out ClientSession? memberSession))
                    continue;

                SendToEndpoint(packet, memberSession.Endpoint);
            }
        }
    }

    private void RemoveClient(Guid clientId, bool notifyRoom)
    {
        lock (StateLock)
        {
            if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
                return;

            SessionsById.Remove(clientId);
            RemoveFromRoom(clientId, session.RoomName);

            if (notifyRoom)
                BroadcastToRoom(session.RoomName, Protocol.BuildPeerLeft(clientId), exceptClientId: clientId);
            Interlocked.Increment(ref Metrics.ClientsLeft);

            LogEvent("client_left", ("clientId", clientId), ("room", session.RoomName));
        }
    }

    private void RemoveFromRoom(Guid clientId, string roomName)
    {
        if (!RoomMembers.TryGetValue(roomName, out HashSet<Guid>? memberIds))
            return;

        memberIds.Remove(clientId);
        if (memberIds.Count == 0)
            RoomMembers.Remove(roomName);
    }

    private void SendToEndpoint(byte[] packet, IPEndPoint endpoint)
    {
        if (packet.Length > 0 && packet[0] == (byte)ServerPacketType.Error)
            Interlocked.Increment(ref Metrics.ErrorPacketsSent);

        _ = Socket.SendAsync(packet, endpoint);
    }

    private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
        => a.Port == b.Port && Equals(a.Address, b.Address);

    public void Dispose()
    {
        StopTokenSource.Cancel();
        WordPressAuthVerifier?.Dispose();
        StatsListener?.Stop();
        StatsListener = null;
        Socket.Dispose();
        StopTokenSource.Dispose();
    }

    private void LogMetrics()
    {
        int activeClients;
        int activeRooms;
        lock (StateLock)
        {
            activeClients = SessionsById.Count;
            activeRooms = RoomMembers.Count;
        }

        LogEvent(
            "metrics",
            ("activeClients", activeClients),
            ("activeRooms", activeRooms),
            ("packetsReceived", Interlocked.Read(ref Metrics.PacketsReceived)),
            ("helloPackets", Interlocked.Read(ref Metrics.HelloPackets)),
            ("authHelloPackets", Interlocked.Read(ref Metrics.AuthHelloPackets)),
            ("voicePacketsReceived", Interlocked.Read(ref Metrics.VoicePacketsReceived)),
            ("voicePacketsRelayed", Interlocked.Read(ref Metrics.VoicePacketsRelayed)),
            ("pingPackets", Interlocked.Read(ref Metrics.PingPackets)),
            ("errorPacketsSent", Interlocked.Read(ref Metrics.ErrorPacketsSent)),
            ("invalidPacketsDropped", Interlocked.Read(ref Metrics.InvalidPacketsDropped)),
            ("payloadDropped", Interlocked.Read(ref Metrics.PayloadDropped)),
            ("rateLimitedDropped", Interlocked.Read(ref Metrics.RateLimitedDropped)),
            ("replayDropped", Interlocked.Read(ref Metrics.ReplayDropped)),
            ("authFailures", Interlocked.Read(ref Metrics.AuthFailures)),
            ("clientsJoined", Interlocked.Read(ref Metrics.ClientsJoined)),
            ("clientsLeft", Interlocked.Read(ref Metrics.ClientsLeft))
        );
    }

    private static void LogEvent(string eventName, params (string key, object? value)[] fields)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["event"] = eventName
        };

        foreach ((string key, object? value) in fields)
            payload[key] = value;

        Console.WriteLine(JsonSerializer.Serialize(payload));
    }

    private async Task StatsLoopAsync(CancellationToken cancellationToken)
    {
        StatsListener = new TcpListener(IPAddress.Loopback, Options.StatsPort);
        StatsListener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await StatsListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleStatsClientAsync(client), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (ObjectDisposedException)
        {
            // expected on shutdown
        }
    }

    private async Task HandleStatsClientAsync(TcpClient client)
    {
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
            return;

        while (true)
        {
            string? headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
                break;
        }

        bool isStatsRequest = requestLine.StartsWith("GET /stats", StringComparison.OrdinalIgnoreCase);
        string body = isStatsRequest ? BuildStatsSnapshotJson() : "{\"error\":\"not_found\"}";
        string status = isStatsRequest ? "200 OK" : "404 Not Found";

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string responseHeaders =
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(responseHeaders);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
    }

    private string BuildStatsSnapshotJson()
    {
        int activeClients;
        int activeRooms;
        lock (StateLock)
        {
            activeClients = SessionsById.Count;
            activeRooms = RoomMembers.Count;
        }

        Dictionary<string, object?> snapshot = new(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["activeClients"] = activeClients,
            ["activeRooms"] = activeRooms,
            ["packetsReceived"] = Interlocked.Read(ref Metrics.PacketsReceived),
            ["helloPackets"] = Interlocked.Read(ref Metrics.HelloPackets),
            ["authHelloPackets"] = Interlocked.Read(ref Metrics.AuthHelloPackets),
            ["voicePacketsReceived"] = Interlocked.Read(ref Metrics.VoicePacketsReceived),
            ["voicePacketsRelayed"] = Interlocked.Read(ref Metrics.VoicePacketsRelayed),
            ["pingPackets"] = Interlocked.Read(ref Metrics.PingPackets),
            ["errorPacketsSent"] = Interlocked.Read(ref Metrics.ErrorPacketsSent),
            ["invalidPacketsDropped"] = Interlocked.Read(ref Metrics.InvalidPacketsDropped),
            ["payloadDropped"] = Interlocked.Read(ref Metrics.PayloadDropped),
            ["rateLimitedDropped"] = Interlocked.Read(ref Metrics.RateLimitedDropped),
            ["replayDropped"] = Interlocked.Read(ref Metrics.ReplayDropped),
            ["authFailures"] = Interlocked.Read(ref Metrics.AuthFailures),
            ["clientsJoined"] = Interlocked.Read(ref Metrics.ClientsJoined),
            ["clientsLeft"] = Interlocked.Read(ref Metrics.ClientsLeft)
        };

        return JsonSerializer.Serialize(snapshot);
    }
}
