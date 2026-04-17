using Godot;
using System;
using System.Threading.Tasks;

[GlobalClass]
public partial class OpenVoiceSharpSessionNode : Node
{
    [Signal]
    public delegate void SessionStartedEventHandler();

    [Signal]
    public delegate void SessionStoppedEventHandler();

    [Signal]
    public delegate void SessionErrorEventHandler(string message);

    [Signal]
    public delegate void VoiceFrameDecodedEventHandler(string speakerId, long sequence, byte[] pcmData, int length);

    [Export]
    public string ServerHost { get; set; } = "127.0.0.1";

    [Export]
    public int ServerPort { get; set; } = 7777;

    [Export]
    public string RoomName { get; set; } = "lobby";

    [Export]
    public string UserName { get; set; } = "Player";

    [Export]
    public string AuthToken { get; set; } = string.Empty;

    [Export]
    public int Bitrate { get; set; } = 24000;

    [Export]
    public bool UseNoiseSuppression { get; set; } = true;

    [Export]
    public bool PushToTalkMode { get; set; }

    private AuthoritativeVoiceSession? _session;
    private bool _isStarting;
    private bool _isStopping;

    public bool IsSessionRunning => _session != null;

    public async Task<bool> StartSessionAsync()
    {
        if (_session != null || _isStarting)
        {
            return true;
        }

        _isStarting = true;
        try
        {
            var session = new AuthoritativeVoiceSession(
                ServerHost,
                ServerPort,
                RoomName,
                UserName,
                AuthToken,
                bitrate: Bitrate,
                enableNoiseSuppression: UseNoiseSuppression);

            session.VoiceFrameDecoded += OnVoiceFrameDecoded;

            await session.StartAsync().ConfigureAwait(false);

            _session = session;

            if (PushToTalkMode && _session.Recorder.IsRecording)
            {
                _session.Recorder.StopRecording();
            }

            CallDeferred(nameof(EmitSessionStarted));
            return true;
        }
        catch (Exception ex)
        {
            CallDeferred(nameof(EmitSessionError), ex.Message);
            return false;
        }
        finally
        {
            _isStarting = false;
        }
    }

    public async Task StopSessionAsync()
    {
        if (_session == null || _isStopping)
        {
            return;
        }

        _isStopping = true;
        try
        {
            var activeSession = _session;
            _session = null;

            activeSession.VoiceFrameDecoded -= OnVoiceFrameDecoded;
            await activeSession.StopAsync().ConfigureAwait(false);
            activeSession.Dispose();

            CallDeferred(nameof(EmitSessionStopped));
        }
        catch (Exception ex)
        {
            CallDeferred(nameof(EmitSessionError), ex.Message);
        }
        finally
        {
            _isStopping = false;
        }
    }

    public int ReadSpeakerPlayback(string speakerId, byte[] pcmOut, int length)
    {
        if (_session == null)
        {
            return 0;
        }

        if (!Guid.TryParse(speakerId, out Guid parsedSpeakerId))
        {
            return 0;
        }

        return _session.ReadSpeakerPlayback(parsedSpeakerId, pcmOut, length);
    }

    public void BeginPushToTalk()
    {
        if (_session == null || !PushToTalkMode)
        {
            return;
        }

        if (!_session.Recorder.IsRecording)
        {
            _session.Recorder.StartRecording();
        }
    }

    public void EndPushToTalk()
    {
        if (_session == null || !PushToTalkMode)
        {
            return;
        }

        if (_session.Recorder.IsRecording)
        {
            _session.Recorder.StopRecording();
        }
    }

    public override void _ExitTree()
    {
        if (_session != null)
        {
            var activeSession = _session;
            _session = null;
            activeSession.VoiceFrameDecoded -= OnVoiceFrameDecoded;
            activeSession.Dispose();
        }
    }

    private void OnVoiceFrameDecoded(Guid speakerId, uint sequence, byte[] pcmData, int length)
    {
        CallDeferred(nameof(EmitVoiceFrameDecoded), speakerId.ToString(), (long)sequence, pcmData, length);
    }

    private void EmitSessionStarted()
    {
        EmitSignal(SignalName.SessionStarted);
    }

    private void EmitSessionStopped()
    {
        EmitSignal(SignalName.SessionStopped);
    }

    private void EmitSessionError(string message)
    {
        EmitSignal(SignalName.SessionError, message);
    }

    private void EmitVoiceFrameDecoded(string speakerId, long sequence, byte[] pcmData, int length)
    {
        EmitSignal(SignalName.VoiceFrameDecoded, speakerId, sequence, pcmData, length);
    }
}
