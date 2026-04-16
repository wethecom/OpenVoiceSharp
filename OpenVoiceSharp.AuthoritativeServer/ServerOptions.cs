namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed record ServerOptions
{
    public int Port { get; init; } = 7777;
    public int MaxRoomMembers { get; init; } = 64;
    public int MaxVoicePayloadBytes { get; init; } = 4096;
    public int MaxVoicePacketsPerSecond { get; init; } = 80;
    public int ClientTimeoutSeconds { get; init; } = 30;

    public static ServerOptions FromArgs(string[] args)
    {
        ServerOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (i + 1 >= args.Length)
                break;

            switch (arg)
            {
                case "--port":
                    if (int.TryParse(args[++i], out int port) && port > 0 && port <= 65535)
                        options = options with { Port = port };
                    break;
                case "--max-room-members":
                    if (int.TryParse(args[++i], out int maxRoomMembers) && maxRoomMembers > 1)
                        options = options with { MaxRoomMembers = maxRoomMembers };
                    break;
                case "--max-voice-bytes":
                    if (int.TryParse(args[++i], out int maxVoiceBytes) && maxVoiceBytes >= 128)
                        options = options with { MaxVoicePayloadBytes = maxVoiceBytes };
                    break;
                case "--max-pps":
                    if (int.TryParse(args[++i], out int maxPps) && maxPps >= 10)
                        options = options with { MaxVoicePacketsPerSecond = maxPps };
                    break;
                case "--timeout-seconds":
                    if (int.TryParse(args[++i], out int timeoutSeconds) && timeoutSeconds >= 5)
                        options = options with { ClientTimeoutSeconds = timeoutSeconds };
                    break;
            }
        }

        return options;
    }
}
