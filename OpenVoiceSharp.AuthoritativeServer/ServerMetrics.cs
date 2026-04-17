namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class ServerMetrics
{
    public long PacketsReceived;
    public long HelloPackets;
    public long AuthHelloPackets;
    public long VoicePacketsReceived;
    public long VoicePacketsRelayed;
    public long ErrorPacketsSent;
    public long InvalidPacketsDropped;
    public long RateLimitedDropped;
    public long ReplayDropped;
    public long PayloadDropped;
    public long AuthFailures;
    public long ClientsJoined;
    public long ClientsLeft;
    public long PingPackets;
}
