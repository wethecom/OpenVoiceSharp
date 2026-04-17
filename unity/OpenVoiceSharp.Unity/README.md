# OpenVoiceSharp Unity Integration

This package provides Unity-side components for OpenVoiceSharp authoritative voice usage.

## Architecture

- This Unity package is the client-side layer.
- It connects to the authoritative server project:
  - `OpenVoiceSharp.AuthoritativeServer`
- The server performs room/session validation and packet relaying.

## Included Runtime Components

- `UnityVoiceSessionBehaviour`
- `UnityVoicePlaybackSource`

## Important Requirement

This package includes managed and native dependencies for Windows x64:

- Managed (`Plugins/OpenVoiceSharp/`):
  - `OpenVoiceSharp.dll`
  - `OpusDotNet.dll`
  - `WebRtcVadSharp.dll`
  - `RNNoise.NET.dll`
  - `NAudio.Core.dll`
  - `NAudio.WinMM.dll`

- Native (`Plugins/x86_64/`):
  - `opus.dll`
  - `rnnoise.dll`
  - `WebRtcVad.dll`

No extra codec/runtime DLL copy step is required for Windows x64.

## Quick Setup

1. Add this package folder to Unity (local package or copy).
2. Ensure `OpenVoiceSharp` core assembly is present.
3. Create a GameObject and add `UnityVoiceSessionBehaviour`.
4. Configure host/port/room/user.
5. For each remote speaker object, add `UnityVoicePlaybackSource` and set `SpeakerId`.
6. Route decoded speaker IDs from your networking/player system to the correct playback source.

See `Samples~/QuickStart/README.md` for an example flow.
