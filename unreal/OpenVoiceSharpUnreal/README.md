# OpenVoiceSharp Unreal (Starter Plugin)

This is a starter Unreal runtime plugin for connecting Unreal clients to the `OpenVoiceSharp.AuthoritativeServer` UDP server.

## What this is

- Unreal networking integration scaffold.
- Includes `UOVSAuthoritativeClient` for:
  - UDP connect/disconnect
  - Session identity setup (`ClientId`, room, user)
  - Sending protocol packets: `Hello`, `AuthHello`, `Voice`, `Leave`, `Ping`
  - Receiving raw server packets via delegate callback

## What this is not (yet)

- It is not a full voice stack for Unreal yet.
- Opus encode/decode, microphone capture, jitter buffer, and playback pipeline are still up to your Unreal project integration.
- It does not directly consume the C# `OpenVoiceSharp.dll`; this plugin is native C++ and protocol-focused.

## Files

- `OpenVoiceSharpUnreal.uplugin`
- `Source/OpenVoiceSharpUnreal/OpenVoiceSharpUnreal.Build.cs`
- `Source/OpenVoiceSharpUnreal/Public/OVSAuthoritativeClient.h`
- `Source/OpenVoiceSharpUnreal/Private/OVSAuthoritativeClient.cpp`

## Quick use in Unreal C++

1. Copy `unreal/OpenVoiceSharpUnreal` into your Unreal project's `Plugins` folder.
2. Enable the plugin in Unreal Editor.
3. Create and use the client object:

```cpp
UOVSAuthoritativeClient* Client = NewObject<UOVSAuthoritativeClient>();
Client->InitializeSession(FGuid::NewGuid(), TEXT("lobby"), TEXT("PlayerOne"));
Client->Connect(TEXT("127.0.0.1"), 7777);
Client->SendHello(); // or SendAuthHello(Token) when server auth is enabled
```

## Protocol mapping

This plugin follows `docs/AUTHORITATIVE_SERVER_PROTOCOL.md` packet layouts for:

- Type `1` Hello
- Type `2` Voice
- Type `3` Leave
- Type `4` Ping
- Type `5` AuthHello

Server packet parsing (Welcome/VoiceRelay/Error/etc.) is exposed as raw packet bytes for now through `OnPacketReceived`.
