using Godot;
using System;
using System.Text;
using System.Threading.Tasks;

public partial class DemoVoiceUI : Control
{
    private LineEdit _serverHost = null!;
    private SpinBox _serverPort = null!;
    private LineEdit _roomName = null!;
    private LineEdit _userName = null!;
    private LineEdit _authToken = null!;
    private Label _status = null!;
    private RichTextLabel _log = null!;
    private Button _connectButton = null!;
    private Button _disconnectButton = null!;
    private CheckButton _pushToTalkMode = null!;
    private Button _holdToTalkButton = null!;
    private AudioStreamPlayer _playbackPlayer = null!;
    private Timer _playbackTimer = null!;

    private OpenVoiceSharpSessionNode _voiceSession = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private string _activeSpeakerId = string.Empty;
    private byte[] _pcmScratch = Array.Empty<byte>();

    private const int SampleRate = 48000;
    private const float PlaybackBufferSeconds = 0.3f;
    private const int PlaybackChunkSamples = 960; // 20ms @ 48kHz

    public override void _Ready()
    {
        _serverHost = GetNode<LineEdit>("Root/Margin/VBox/ConnectionGrid/ServerHost");
        _serverPort = GetNode<SpinBox>("Root/Margin/VBox/ConnectionGrid/ServerPort");
        _roomName = GetNode<LineEdit>("Root/Margin/VBox/ConnectionGrid/RoomName");
        _userName = GetNode<LineEdit>("Root/Margin/VBox/ConnectionGrid/UserName");
        _authToken = GetNode<LineEdit>("Root/Margin/VBox/ConnectionGrid/AuthToken");
        _status = GetNode<Label>("Root/Margin/VBox/Status");
        _log = GetNode<RichTextLabel>("Root/Margin/VBox/LogOutput");
        _connectButton = GetNode<Button>("Root/Margin/VBox/ButtonRow/ConnectButton");
        _disconnectButton = GetNode<Button>("Root/Margin/VBox/ButtonRow/DisconnectButton");
        _pushToTalkMode = GetNode<CheckButton>("Root/Margin/VBox/ButtonRow/PushToTalkMode");
        _holdToTalkButton = GetNode<Button>("Root/Margin/VBox/ButtonRow/HoldToTalkButton");
        _playbackPlayer = GetNode<AudioStreamPlayer>("PlaybackPlayer");
        _playbackTimer = GetNode<Timer>("PlaybackTimer");

        _voiceSession = new OpenVoiceSharpSessionNode();
        AddChild(_voiceSession);

        _voiceSession.Connect(OpenVoiceSharpSessionNode.SignalName.SessionStarted, Callable.From(OnSessionStarted));
        _voiceSession.Connect(OpenVoiceSharpSessionNode.SignalName.SessionStopped, Callable.From(OnSessionStopped));
        _voiceSession.Connect(OpenVoiceSharpSessionNode.SignalName.SessionError, Callable.From<string>(OnSessionError));
        _voiceSession.Connect(OpenVoiceSharpSessionNode.SignalName.VoiceFrameDecoded, Callable.From<string, long, byte[], int>(OnVoiceFrameDecoded));

        _connectButton.Pressed += OnConnectPressed;
        _disconnectButton.Pressed += OnDisconnectPressed;
        _pushToTalkMode.Toggled += OnPushToTalkModeToggled;
        _holdToTalkButton.ButtonDown += OnHoldToTalkDown;
        _holdToTalkButton.ButtonUp += OnHoldToTalkUp;
        _playbackTimer.Timeout += OnPlaybackTimer;

        ConfigurePlayback();
        SetStatus("Idle");
        UpdateUiState(false);
    }

    public override void _ExitTree()
    {
        _connectButton.Pressed -= OnConnectPressed;
        _disconnectButton.Pressed -= OnDisconnectPressed;
        _pushToTalkMode.Toggled -= OnPushToTalkModeToggled;
        _holdToTalkButton.ButtonDown -= OnHoldToTalkDown;
        _holdToTalkButton.ButtonUp -= OnHoldToTalkUp;
        _playbackTimer.Timeout -= OnPlaybackTimer;
    }

    private async void OnConnectPressed()
    {
        _voiceSession.ServerHost = _serverHost.Text.Trim();
        _voiceSession.ServerPort = (int)_serverPort.Value;
        _voiceSession.RoomName = _roomName.Text.Trim();
        _voiceSession.UserName = _userName.Text.Trim();
        _voiceSession.AuthToken = _authToken.Text.Trim();
        _voiceSession.PushToTalkMode = _pushToTalkMode.ButtonPressed;

        SetStatus("Connecting...");
        bool ok = await _voiceSession.StartSessionAsync();
        if (!ok)
        {
            SetStatus("Connection failed");
        }
    }

    private async void OnDisconnectPressed()
    {
        SetStatus("Disconnecting...");
        await _voiceSession.StopSessionAsync();
    }

    private void OnSessionStarted()
    {
        AppendLog("Session started.");
        SetStatus("Connected");
        UpdateUiState(true);
    }

    private void OnSessionStopped()
    {
        AppendLog("Session stopped.");
        SetStatus("Disconnected");
        UpdateUiState(false);
    }

    private void OnSessionError(string message)
    {
        AppendLog($"Error: {message}");
        SetStatus("Error");
    }

    private void OnVoiceFrameDecoded(string speakerId, long sequence, byte[] pcmData, int length)
    {
        _activeSpeakerId = speakerId;
        AppendLog($"Voice frame from {speakerId} seq={sequence} bytes={length}");
    }

    private void OnPushToTalkModeToggled(bool enabled)
    {
        _holdToTalkButton.Disabled = !enabled || !_voiceSession.IsSessionRunning;
        _voiceSession.PushToTalkMode = enabled;
        AppendLog(enabled ? "Push-to-talk enabled." : "Push-to-talk disabled.");
    }

    private void OnHoldToTalkDown()
    {
        _voiceSession.BeginPushToTalk();
    }

    private void OnHoldToTalkUp()
    {
        _voiceSession.EndPushToTalk();
    }

    private void ConfigurePlayback()
    {
        var generator = new AudioStreamGenerator
        {
            MixRate = SampleRate,
            BufferLength = PlaybackBufferSeconds
        };

        _playbackPlayer.Stream = generator;
        _playbackPlayer.Play();
        _playback = _playbackPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        _pcmScratch = new byte[PlaybackChunkSamples * sizeof(short)];
    }

    private void OnPlaybackTimer()
    {
        if (_playback == null || string.IsNullOrWhiteSpace(_activeSpeakerId))
        {
            return;
        }

        if (!_playback.CanPushBuffer(PlaybackChunkSamples))
        {
            return;
        }

        int copied = _voiceSession.ReadSpeakerPlayback(_activeSpeakerId, _pcmScratch, _pcmScratch.Length);
        _ = copied;

        for (int i = 0; i < PlaybackChunkSamples; i++)
        {
            int offset = i * 2;
            short sample = (short)(_pcmScratch[offset] | (_pcmScratch[offset + 1] << 8));
            float value = sample / 32768.0f;
            _playback.PushFrame(new Vector2(value, value));
        }
    }

    private void UpdateUiState(bool connected)
    {
        _connectButton.Disabled = connected;
        _disconnectButton.Disabled = !connected;
        _holdToTalkButton.Disabled = !connected || !_pushToTalkMode.ButtonPressed;
    }

    private void SetStatus(string message)
    {
        _status.Text = $"Status: {message}";
    }

    private void AppendLog(string line)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        var safeLine = new StringBuilder();
        safeLine.Append('[').Append(ts).Append("] ").Append(line);
        _log.AppendText(safeLine.ToString() + "\n");
        _log.ScrollToLine(_log.GetLineCount());
    }
}
