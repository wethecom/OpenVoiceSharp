using System.Net;

namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class ClientSession
{
    public Guid ClientId { get; }
    public string RoomName { get; set; }
    public string UserName { get; set; }
    public IPEndPoint Endpoint { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public uint LastVoiceSequence { get; set; }
    public TokenBucket VoiceRateLimiter { get; }

    public ClientSession(Guid clientId, string roomName, string userName, IPEndPoint endpoint, int maxVoicePacketsPerSecond)
    {
        ClientId = clientId;
        RoomName = roomName;
        UserName = userName;
        Endpoint = endpoint;
        LastSeenUtc = DateTime.UtcNow;
        VoiceRateLimiter = new TokenBucket(maxVoicePacketsPerSecond);
    }
}
