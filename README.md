# <img src="https://raw.githubusercontent.com/realcoloride/OpenVoiceSharp/master/openvoicesharp.png" alt="OpenVoiceSharp" width="28" height="28"> OpenVoiceSharp

A C# voice chat and audio streaming library with an authoritative UDP server.

## Project Status

OpenVoiceSharp is usable today for Windows x64 projects that want low-latency voice with Opus encoding and a server-authoritative relay model.

Current status by area:

- Core C# library: working
- Authoritative UDP server: working
- Unity package: integration scaffold with managed/native dependency packaging
- Godot package: integration scaffold with demo scene
- Unreal package: protocol/client scaffold

## What You Get

- Opus encode/decode voice pipeline (`VoiceChatInterface`)
- Optional RNNoise suppression and WebRTC VAD
- Microphone capture helper (`BasicMicrophoneRecorder`)
- Authoritative UDP server project (`OpenVoiceSharp.AuthoritativeServer`)
- High-level client/session helpers:
  - `AuthoritativeVoiceClient`
  - `AuthoritativeVoiceSession`
- Playback remainder handling helpers:
  - `ReadSpeakerPlayback(...)`
  - `FlushSpeakerPlayback(...)`

## Requirements

- Windows x64
- .NET 6+ (library also targets .NET Standard 2.1)
- Native codec/runtime dependencies for Opus/RNNoise/WebRTC VAD

## Repository Layout

- `OpenVoiceSharp.AuthoritativeServer` - authoritative UDP voice server
- `docs/AUTHORITATIVE_SERVER_PROTOCOL.md` - packet protocol
- `unity/OpenVoiceSharp.Unity` - Unity integration scaffold
- `godot/OpenVoiceSharp.Godot` - Godot 4 C# integration scaffold and demo
- `unreal/OpenVoiceSharpUnreal` - Unreal starter plugin scaffold

## Quick Start

### 1. Run the server

```bash
dotnet run --project OpenVoiceSharp.AuthoritativeServer -- --port 7777
```

Optional stats endpoint:

```bash
dotnet run --project OpenVoiceSharp.AuthoritativeServer -- --port 7777 --stats-port 9090
```

Read stats:

```bash
curl http://127.0.0.1:9090/stats
```

Optional WordPress auth verification:

```bash
dotnet run --project OpenVoiceSharp.AuthoritativeServer -- \
  --port 7777 \
  --wp-verify-url "https://your-site.com/wp-json/openvoicesharp/v1/verify" \
  --wp-shared-secret "server-to-wp-shared-secret"
```

### 2. Connect from client code

```csharp
var session = new AuthoritativeVoiceSession(
    "127.0.0.1",
    7777,
    "lobby",
    "PlayerOne",
    authToken: "wordpress-access-token"
);

session.VoiceFrameDecoded += (speakerId, sequence, pcmData, length) =>
{
    // Send PCM to your playback pipeline.
};

await session.StartAsync();
```

### 3. Read playback safely (no stuck tail audio)

```csharp
Guid speakerId = /* target speaker */;
byte[] pcmOut = new byte[1920];
int copied = session.ReadSpeakerPlayback(speakerId, pcmOut, pcmOut.Length);
// `copied` bytes are real audio; remainder is silence-filled.
```

## Engine Integrations

### Unity

- Path: `unity/OpenVoiceSharp.Unity`
- Intended use: production client integration with packaged managed/native dependencies for Windows x64.

### Unity 5-Minute Checklist

1. Start the server locally:
   - `dotnet run --project OpenVoiceSharp.AuthoritativeServer -- --port 7777`
2. Copy `unity/OpenVoiceSharp.Unity` into your Unity project (recommended under `Assets/OpenVoiceSharp.Unity`).
3. Verify managed plugin DLLs exist under:
   - `Assets/OpenVoiceSharp.Unity/Plugins/OpenVoiceSharp/`
4. Verify native plugin DLLs exist under:
   - `Assets/OpenVoiceSharp.Unity/Plugins/x86_64/`
5. In Unity, create/connect an `AuthoritativeVoiceSession` using:
   - host `127.0.0.1`, port `7777`, room `"lobby"`, user `"PlayerOne"`
6. On decoded frames, submit PCM to your playback path; for fixed-size callbacks use:
   - `ReadSpeakerPlayback(...)` to avoid stuck remainder audio.
7. If using WordPress auth on server, pass your token in `authToken` when creating the client/session.

### Godot (4 C#)

- Path: `godot/OpenVoiceSharp.Godot`
- Includes demo: `godot/OpenVoiceSharp.Godot/demo/DemoVoiceUI.tscn`

### Unreal

- Path: `unreal/OpenVoiceSharpUnreal`
- Scope: starter plugin scaffold and UDP protocol client API.

## Architecture

- Server is authoritative and room-based.
- Clients send encoded voice frames.
- Server validates endpoint/session and relays to room peers.
- Clients decode and feed playback.
- Optional jitter/playback buffering smooths network variation.

See full packet definitions in:

- `docs/AUTHORITATIVE_SERVER_PROTOCOL.md`

## Notes and Limits

- Current native dependency setup is focused on Windows x64.
- Engine folders are integration layers around the core server/protocol design.
- Advanced gameplay voice features (teams, proximity rules, moderation policy, etc.) are intended to be implemented at the game/application layer.

## Contributing

Contributions, bug reports, and feedback are welcome:

- Issues: [GitHub Issues](https://github.com/realcoloride/OpenVoiceSharp/issues)

## License

OpenVoiceSharp is MIT licensed.

This project depends on third-party libraries with their own licenses:

- [NAudio](https://github.com/naudio/NAudio) - MIT
- [OpusDotNet](https://github.com/mrphil2105/OpusDotNet) - MIT
- [Opus](https://opus-codec.org/) - BSD
- [WebRtcVadSharp](https://github.com/ladenedge/WebRtcVadSharp) - MIT
- [WebRTC VAD](https://webrtc.org/support/license) - WebRTC license terms
- [YellowDogMan.RRNoise.NET](https://github.com/Yellow-Dog-Man/RNNoise.Net) - MIT
- [RNNoise](https://github.com/xiph/rnnoise) - BSD
