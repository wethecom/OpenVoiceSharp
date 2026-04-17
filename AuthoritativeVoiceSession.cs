using WebRtcVadSharp;

namespace OpenVoiceSharp
{
    /// <summary>
    /// High-level helper that wires microphone capture, Opus encode/decode,
    /// and authoritative server networking into one session.
    /// </summary>
    public sealed class AuthoritativeVoiceSession : IDisposable
    {
        public delegate void DecodedVoiceFrameEvent(Guid speakerClientId, uint sequence, byte[] pcmData, int length);
        public event DecodedVoiceFrameEvent? VoiceFrameDecoded;

        public delegate void SessionErrorEvent(string message, Exception? exception);
        public event SessionErrorEvent? SessionError;

        public AuthoritativeVoiceClient Client { get; }
        public VoiceChatInterface VoiceChatInterface { get; }
        public BasicMicrophoneRecorder Recorder { get; }

        public bool GateOutgoingByVoiceActivity { get; set; } = true;
        public bool IsRunning { get; private set; }

        private readonly int ExpectedPcmFrameSize;
        private bool IsDisposed;
        private bool IsSubscribed;

        public AuthoritativeVoiceSession(
            string serverHost,
            int serverPort,
            string roomName,
            string userName,
            string? authToken = null,
            int bitrate = VoiceChatInterface.DefaultBitrate,
            bool stereo = false,
            bool enableNoiseSuppression = true,
            bool favorAudioStreaming = false,
            OperatingMode? vadOperatingMode = null
        )
        {
            Client = new AuthoritativeVoiceClient(serverHost, serverPort, roomName, userName, authToken);
            VoiceChatInterface = new VoiceChatInterface(
                bitrate,
                stereo,
                enableNoiseSuppression,
                favorAudioStreaming,
                vadOperatingMode
            );
            Recorder = new BasicMicrophoneRecorder(stereo);
            ExpectedPcmFrameSize = VoiceUtilities.GetSampleSize(stereo ? 2 : 1);
        }

        /// <summary>
        /// Connects to the authoritative server and starts microphone capture.
        /// </summary>
        public async Task StartAsync(int handshakeTimeoutMs = 5000)
        {
            ThrowIfDisposed();
            if (IsRunning)
                return;

            SubscribeEvents();

            try
            {
                await Client.ConnectAsync(handshakeTimeoutMs).ConfigureAwait(false);
                Recorder.StartRecording();
                IsRunning = true;
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to start voice session.", exception);
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Stops microphone capture and disconnects from the server.
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning && !IsSubscribed)
                return;

            try
            {
                if (Recorder.IsRecording)
                    Recorder.StopRecording();
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to stop microphone recorder cleanly.", exception);
            }

            try
            {
                await Client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to disconnect voice client cleanly.", exception);
            }

            UnsubscribeEvents();
            IsRunning = false;
        }

        private void SubscribeEvents()
        {
            if (IsSubscribed)
                return;

            Recorder.DataAvailable += OnMicrophoneDataAvailable;
            Client.VoicePacketReceived += OnVoicePacketReceived;
            Client.ErrorReceived += OnClientErrorReceived;
            IsSubscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!IsSubscribed)
                return;

            Recorder.DataAvailable -= OnMicrophoneDataAvailable;
            Client.VoicePacketReceived -= OnVoicePacketReceived;
            Client.ErrorReceived -= OnClientErrorReceived;
            IsSubscribed = false;
        }

        private void OnClientErrorReceived(byte errorCode, string message)
            => SessionError?.Invoke($"Server error ({errorCode}): {message}", null);

        private void OnMicrophoneDataAvailable(byte[] pcmData, int length)
        {
            if (!IsRunning)
                return;

            if (length != ExpectedPcmFrameSize)
                return;

            if (GateOutgoingByVoiceActivity && !VoiceChatInterface.IsSpeaking(pcmData))
                return;

            try
            {
                (byte[] encodedData, int encodedLength) = VoiceChatInterface.SubmitAudioData(pcmData, length);
                _ = SendEncodedFrameAsync(encodedData, encodedLength);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to encode or enqueue outgoing voice frame.", exception);
            }
        }

        private async Task SendEncodedFrameAsync(byte[] encodedData, int encodedLength)
        {
            try
            {
                await Client.SendVoiceAsync(encodedData, encodedLength).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to send outgoing voice frame.", exception);
            }
        }

        private void OnVoicePacketReceived(Guid speakerClientId, uint sequence, byte[] payload, int length)
        {
            try
            {
                (byte[] decodedData, int decodedLength) = VoiceChatInterface.WhenDataReceived(payload, length);
                VoiceFrameDecoded?.Invoke(speakerClientId, sequence, decodedData, decodedLength);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to decode incoming voice frame.", exception);
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            StopAsync().GetAwaiter().GetResult();
            Recorder.Dispose();
            VoiceChatInterface.Dispose();
            Client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AuthoritativeVoiceSession));
        }
    }
}
