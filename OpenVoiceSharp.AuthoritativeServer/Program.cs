using OpenVoiceSharp.AuthoritativeServer;

var options = ServerOptions.FromArgs(args);

using var server = new VoiceAuthoritativeServer(options);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    server.RequestStop();
};

await server.RunAsync();
