# OpenVoiceSharp Godot (Starter Package)

This is a Godot 4 C# integration scaffold for connecting Godot clients to `OpenVoiceSharp.AuthoritativeServer`.

## What you get

- `addons/openvoicesharp/OpenVoiceSharpSessionNode.cs`
  - Wraps `AuthoritativeVoiceSession`
  - Connect/start/stop helpers
  - Godot signals for decoded voice and lifecycle
  - Helper to read per-speaker playback remainder
- `addons/openvoicesharp/plugin.cfg` and `OpenVoiceSharpPlugin.cs` starter plugin shell

## What this does not include yet

- Native Godot microphone capture/playback graph implementation
- Opus/RNNoise/WebRTC native runtime packaging per platform
- UI for rooms/users/device selection

## Install in your Godot project

1. Copy this folder into your Godot project:
   - `res://addons/openvoicesharp/`
2. Add managed OpenVoiceSharp dependencies to your Godot project:
   - `OpenVoiceSharp.dll`
   - `OpusDotNet.dll`
   - `RNNoise.NET.dll`
   - `WebRtcVadSharp.dll`
   - `NAudio.Core.dll` (if used on your target runtime path)
   - `NAudio.WinMM.dll` (Windows microphone path)
3. Ensure native runtime DLLs are available for your target platform (for example Windows x64).
4. Enable the plugin in Godot: `Project -> Project Settings -> Plugins -> OpenVoiceSharp`.
5. Add `OpenVoiceSharpSessionNode` to a scene and set exported properties.

## Minimal usage

```csharp
public override async void _Ready()
{
    var voice = GetNode<OpenVoiceSharpSessionNode>("OpenVoiceSharpSessionNode");
    voice.ServerHost = "127.0.0.1";
    voice.ServerPort = 7777;
    voice.RoomName = "lobby";
    voice.UserName = "PlayerOne";
    voice.AuthToken = "";

    bool ok = await voice.StartSessionAsync();
GD.Print($"Voice connected: {ok}");
}
```

## Demo scene included

- Scene: `res://demo/DemoVoiceUI.tscn`
- Script: `res://demo/DemoVoiceUI.cs`

The demo includes:

- Server/room/user/token inputs
- Connect and disconnect buttons
- Push-to-talk mode toggle
- Hold-to-talk button (active only when push-to-talk mode is enabled)
- Basic playback pumping from `ReadSpeakerPlayback(...)`

To run:

1. Copy `godot/OpenVoiceSharp.Godot` contents into a Godot 4 C# project.
2. Ensure the `addons/openvoicesharp` plugin is enabled.
3. Add dependencies (managed + native) from `addons/openvoicesharp/lib/README.md`.
4. Open `res://demo/DemoVoiceUI.tscn` and run scene.

## Architecture note

- Godot package = client integration layer.
- `OpenVoiceSharp.AuthoritativeServer` = authoritative UDP voice relay server.
