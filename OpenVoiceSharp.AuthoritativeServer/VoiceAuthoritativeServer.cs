using System.Net;
using System.Net.Sockets;

namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class VoiceAuthoritativeServer : IDisposable
{
    private readonly ServerOptions Options;
    private readonly UdpClient Socket;
    private readonly Dictionary<Guid, ClientSession> SessionsById = [];
    private readonly Dictionary<string, HashSet<Guid>> RoomMembers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource StopTokenSource = new();

    public VoiceAuthoritativeServer(ServerOptions options)
    {
        Options = options;
        Socket = new UdpClient(new IPEndPoint(IPAddress.Any, Options.Port));
    }

    public void RequestStop() => StopTokenSource.Cancel();

    public async Task RunAsync()
    {
        Console.WriteLine($"Authoritative voice server listening on udp://0.0.0.0:{Options.Port}");
        using PeriodicTimer cleanupTimer = new(TimeSpan.FromSeconds(1));

        Task receiveLoop = ReceiveLoopAsync(StopTokenSource.Token);
        Task cleanupLoop = CleanupLoopAsync(cleanupTimer, StopTokenSource.Token);

        await Task.WhenAny(receiveLoop, cleanupLoop);
        StopTokenSource.Cancel();

        try
        {
            await Task.WhenAll(receiveLoop, cleanupLoop);
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
            HandlePacket(result.Buffer, result.RemoteEndPoint);
        }
    }

    private async Task CleanupLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            DateTime cutoffUtc = DateTime.UtcNow.AddSeconds(-Options.ClientTimeoutSeconds);
            List<Guid> staleClientIds = [];

            foreach (KeyValuePair<Guid, ClientSession> pair in SessionsById)
            {
                if (pair.Value.LastSeenUtc < cutoffUtc)
                    staleClientIds.Add(pair.Key);
            }

            foreach (Guid staleClientId in staleClientIds)
                RemoveClient(staleClientId, notifyRoom: true);
        }
    }

    private void HandlePacket(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (packet.Length == 0)
            return;

        ClientPacketType packetType = (ClientPacketType)packet[0];
        switch (packetType)
        {
            case ClientPacketType.Hello:
                HandleHello(packet, remoteEndpoint);
                break;
            case ClientPacketType.Voice:
                HandleVoice(packet, remoteEndpoint);
                break;
            case ClientPacketType.Leave:
                HandleLeave(packet, remoteEndpoint);
                break;
            case ClientPacketType.Ping:
                HandlePing(packet, remoteEndpoint);
                break;
            default:
                SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Unknown packet type"), remoteEndpoint);
                break;
        }
    }

    private void HandleHello(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadHello(packet, out Guid clientId, out string room, out string userName))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid hello packet"), remoteEndpoint);
            return;
        }

        room = room.Trim();
        userName = userName.Trim();
        if (room.Length == 0 || room.Length > 64 || userName.Length == 0 || userName.Length > 64)
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid room or user name"), remoteEndpoint);
            return;
        }

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

            SendToEndpoint(Protocol.BuildWelcome(clientId), remoteEndpoint);
            BroadcastToRoom(room, Protocol.BuildPeerJoined(clientId, userName), exceptClientId: clientId);

            Console.WriteLine($"Client {clientId} joined room '{room}' as '{userName}' from {remoteEndpoint}");
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
        Console.WriteLine($"Client {clientId} refreshed session in room '{room}' from {remoteEndpoint}");
    }

    private void HandleVoice(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadVoice(packet, out Guid clientId, out uint sequence, out ReadOnlySpan<byte> payload))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid voice packet"), remoteEndpoint);
            return;
        }

        if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.NotRegistered, "Client not registered"), remoteEndpoint);
            return;
        }

        if (!EndpointsEqual(session.Endpoint, remoteEndpoint))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.UnauthorizedEndpoint, "Invalid source endpoint"), remoteEndpoint);
            return;
        }

        if (payload.Length == 0 || payload.Length > Options.MaxVoicePayloadBytes)
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.PayloadTooLarge, "Voice payload out of range"), remoteEndpoint);
            return;
        }

        if (!session.VoiceRateLimiter.TryConsume(1))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.RateLimited, "Voice packet rate exceeded"), remoteEndpoint);
            return;
        }

        if (sequence <= session.LastVoiceSequence)
            return;

        session.LastVoiceSequence = sequence;
        session.LastSeenUtc = DateTime.UtcNow;

        byte[] relayPacket = Protocol.BuildVoiceRelay(clientId, sequence, payload);
        BroadcastToRoom(session.RoomName, relayPacket, exceptClientId: clientId);
    }

    private void HandleLeave(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadLeave(packet, out Guid clientId))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.InvalidPacket, "Invalid leave packet"), remoteEndpoint);
            return;
        }

        if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
            return;

        if (!EndpointsEqual(session.Endpoint, remoteEndpoint))
        {
            SendToEndpoint(Protocol.BuildError(ErrorCode.UnauthorizedEndpoint, "Invalid source endpoint"), remoteEndpoint);
            return;
        }

        RemoveClient(clientId, notifyRoom: true);
    }

    private void HandlePing(byte[] packet, IPEndPoint remoteEndpoint)
    {
        if (!Protocol.TryReadPing(packet, out Guid clientId))
            return;

        if (SessionsById.TryGetValue(clientId, out ClientSession? session) && EndpointsEqual(session.Endpoint, remoteEndpoint))
            session.LastSeenUtc = DateTime.UtcNow;

        SendToEndpoint(Protocol.BuildPong(clientId), remoteEndpoint);
    }

    private void BroadcastToRoom(string room, byte[] packet, Guid? exceptClientId = null)
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

    private void RemoveClient(Guid clientId, bool notifyRoom)
    {
        if (!SessionsById.TryGetValue(clientId, out ClientSession? session))
            return;

        SessionsById.Remove(clientId);
        RemoveFromRoom(clientId, session.RoomName);

        if (notifyRoom)
            BroadcastToRoom(session.RoomName, Protocol.BuildPeerLeft(clientId), exceptClientId: clientId);

        Console.WriteLine($"Client {clientId} left room '{session.RoomName}'");
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
        _ = Socket.SendAsync(packet, endpoint);
    }

    private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
        => a.Port == b.Port && Equals(a.Address, b.Address);

    public void Dispose()
    {
        StopTokenSource.Cancel();
        Socket.Dispose();
        StopTokenSource.Dispose();
    }
}
