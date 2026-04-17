# OpenVoiceSharp Unity Integration

This package provides Unity-side components for OpenVoiceSharp authoritative voice usage.

## Included Runtime Components

- `UnityVoiceSessionBehaviour`
- `UnityVoicePlaybackSource`

## Important Requirement

This package is an integration layer and expects the core `OpenVoiceSharp` assembly to be available in your Unity project.

This package now includes the managed core DLL at:

- `Plugins/OpenVoiceSharp/OpenVoiceSharp.dll`

You still need native codec/runtime dependencies required by Opus/RNNoise in your Unity project plugins (typically `Assets/Plugins/x86_64`).

## Quick Setup

1. Add this package folder to Unity (local package or copy).
2. Ensure `OpenVoiceSharp` core assembly is present.
3. Create a GameObject and add `UnityVoiceSessionBehaviour`.
4. Configure host/port/room/user.
5. For each remote speaker object, add `UnityVoicePlaybackSource` and set `SpeakerId`.
6. Route decoded speaker IDs from your networking/player system to the correct playback source.

See `Samples~/QuickStart/README.md` for an example flow.
