using System.Buffers.Binary;
using System.Text;

namespace OpenVoiceSharp.AuthoritativeServer;

internal enum ClientPacketType : byte
{
    Hello = 1,
    Voice = 2,
    Leave = 3,
    Ping = 4
}

internal enum ServerPacketType : byte
{
    Welcome = 11,
    VoiceRelay = 12,
    Error = 13,
    PeerJoined = 14,
    PeerLeft = 15,
    Pong = 16
}

internal enum ErrorCode : byte
{
    InvalidPacket = 1,
    NotRegistered = 2,
    UnauthorizedEndpoint = 3,
    RoomFull = 4,
    RateLimited = 5,
    PayloadTooLarge = 6
}

internal static class Protocol
{
    public static bool TryReadHello(
        ReadOnlySpan<byte> packet,
        out Guid clientId,
        out string room,
        out string userName
    )
    {
        clientId = Guid.Empty;
        room = string.Empty;
        userName = string.Empty;

        // Type(1) + ClientId(16) + RoomLen(1) + UserLen(1)
        if (packet.Length < 19 || packet[0] != (byte)ClientPacketType.Hello)
            return false;

        clientId = new Guid(packet.Slice(1, 16));
        byte roomLength = packet[17];
        int roomStart = 18;
        int userLenIndex = roomStart + roomLength;

        if (packet.Length < userLenIndex + 1)
            return false;

        byte userLength = packet[userLenIndex];
        int userStart = userLenIndex + 1;
        if (packet.Length != userStart + userLength)
            return false;

        room = Encoding.UTF8.GetString(packet.Slice(roomStart, roomLength));
        userName = Encoding.UTF8.GetString(packet.Slice(userStart, userLength));
        return room.Length > 0 && userName.Length > 0;
    }

    public static bool TryReadVoice(
        ReadOnlySpan<byte> packet,
        out Guid clientId,
        out uint sequence,
        out ReadOnlySpan<byte> payload
    )
    {
        clientId = Guid.Empty;
        sequence = 0;
        payload = default;

        // Type(1) + ClientId(16) + Sequence(4) + PayloadLen(2)
        if (packet.Length < 23 || packet[0] != (byte)ClientPacketType.Voice)
            return false;

        clientId = new Guid(packet.Slice(1, 16));
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(17, 4));
        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(21, 2));

        if (packet.Length != 23 + payloadLength)
            return false;

        payload = packet.Slice(23, payloadLength);
        return true;
    }

    public static bool TryReadLeave(ReadOnlySpan<byte> packet, out Guid clientId)
    {
        clientId = Guid.Empty;
        if (packet.Length != 17 || packet[0] != (byte)ClientPacketType.Leave)
            return false;

        clientId = new Guid(packet.Slice(1, 16));
        return true;
    }

    public static bool TryReadPing(ReadOnlySpan<byte> packet, out Guid clientId)
    {
        clientId = Guid.Empty;
        if (packet.Length != 17 || packet[0] != (byte)ClientPacketType.Ping)
            return false;

        clientId = new Guid(packet.Slice(1, 16));
        return true;
    }

    public static byte[] BuildWelcome(Guid clientId)
    {
        byte[] packet = new byte[17];
        packet[0] = (byte)ServerPacketType.Welcome;
        clientId.TryWriteBytes(packet.AsSpan(1, 16));
        return packet;
    }

    public static byte[] BuildPeerJoined(Guid clientId, string userName)
    {
        byte[] userNameBytes = Encoding.UTF8.GetBytes(userName);
        byte[] packet = new byte[18 + userNameBytes.Length];
        packet[0] = (byte)ServerPacketType.PeerJoined;
        clientId.TryWriteBytes(packet.AsSpan(1, 16));
        packet[17] = checked((byte)userNameBytes.Length);
        userNameBytes.CopyTo(packet.AsSpan(18));
        return packet;
    }

    public static byte[] BuildPeerLeft(Guid clientId)
    {
        byte[] packet = new byte[17];
        packet[0] = (byte)ServerPacketType.PeerLeft;
        clientId.TryWriteBytes(packet.AsSpan(1, 16));
        return packet;
    }

    public static byte[] BuildVoiceRelay(Guid speakerClientId, uint sequence, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[23 + payload.Length];
        packet[0] = (byte)ServerPacketType.VoiceRelay;
        speakerClientId.TryWriteBytes(packet.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(17, 4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(21, 2), checked((ushort)payload.Length));
        payload.CopyTo(packet.AsSpan(23));
        return packet;
    }

    public static byte[] BuildError(ErrorCode code, string message)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        if (messageBytes.Length > byte.MaxValue)
            messageBytes = messageBytes[..byte.MaxValue];

        byte[] packet = new byte[3 + messageBytes.Length];
        packet[0] = (byte)ServerPacketType.Error;
        packet[1] = (byte)code;
        packet[2] = (byte)messageBytes.Length;
        messageBytes.CopyTo(packet.AsSpan(3));
        return packet;
    }

    public static byte[] BuildPong(Guid clientId)
    {
        byte[] packet = new byte[17];
        packet[0] = (byte)ServerPacketType.Pong;
        clientId.TryWriteBytes(packet.AsSpan(1, 16));
        return packet;
    }
}
