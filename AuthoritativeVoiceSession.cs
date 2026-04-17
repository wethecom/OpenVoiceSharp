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
        public bool EnableJitterBuffer { get; set; } = true;
        public bool EnableSpeakerPlaybackBuffers { get; set; } = true;
        public int JitterTargetPackets { get; }
        public int JitterMaxPackets { get; }
        public bool IsRunning { get; private set; }

        private readonly int ExpectedPcmFrameSize;
        private readonly Dictionary<Guid, VoiceJitterBuffer> SpeakerJitterBuffers = new();
        private readonly Dictionary<Guid, VoicePlaybackBuffer> SpeakerPlaybackBuffers = new();
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
            OperatingMode? vadOperatingMode = null,
            int jitterTargetPackets = 3,
            int jitterMaxPackets = 24
        )
        {
            if (jitterTargetPackets < 1)
                throw new ArgumentOutOfRangeException(nameof(jitterTargetPackets));
            if (jitterMaxPackets < jitterTargetPackets + 2)
                throw new ArgumentOutOfRangeException(nameof(jitterMaxPackets));

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
            JitterTargetPackets = jitterTargetPackets;
            JitterMaxPackets = jitterMaxPackets;
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
            SpeakerJitterBuffers.Clear();
            lock (SpeakerPlaybackBuffers)
                SpeakerPlaybackBuffers.Clear();
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
            if (!EnableJitterBuffer)
            {
                DecodeAndEmit(speakerClientId, sequence, payload, length);
                return;
            }

            try
            {
                if (!SpeakerJitterBuffers.TryGetValue(speakerClientId, out VoiceJitterBuffer? buffer))
                {
                    buffer = new VoiceJitterBuffer(JitterTargetPackets, JitterMaxPackets);
                    SpeakerJitterBuffers[speakerClientId] = buffer;
                }

                buffer.Add(sequence, payload, length);
                foreach ((uint bufferedSequence, byte[] bufferedPayload) in buffer.DrainReady())
                    DecodeAndEmit(speakerClientId, bufferedSequence, bufferedPayload, bufferedPayload.Length);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to decode incoming voice frame.", exception);
            }
        }

        private void DecodeAndEmit(Guid speakerClientId, uint sequence, byte[] payload, int length)
        {
            try
            {
                (byte[] decodedData, int decodedLength) = VoiceChatInterface.WhenDataReceived(payload, length);

                if (EnableSpeakerPlaybackBuffers)
                {
                    VoicePlaybackBuffer playbackBuffer;
                    lock (SpeakerPlaybackBuffers)
                    {
                        if (!SpeakerPlaybackBuffers.TryGetValue(speakerClientId, out playbackBuffer!))
                        {
                            playbackBuffer = new VoicePlaybackBuffer();
                            SpeakerPlaybackBuffers[speakerClientId] = playbackBuffer;
                        }
                    }
                    playbackBuffer.Enqueue(decodedData, decodedLength);
                }

                VoiceFrameDecoded?.Invoke(speakerClientId, sequence, decodedData, decodedLength);
            }
            catch (Exception exception)
            {
                SessionError?.Invoke("Failed to decode incoming voice frame.", exception);
            }
        }

        /// <summary>
        /// Reads speaker PCM into output and fills missing bytes with silence.
        /// Returns copied PCM bytes before silence fill.
        /// </summary>
        public int ReadSpeakerPlayback(Guid speakerClientId, byte[] output, int count, int offset = 0)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));

            VoicePlaybackBuffer? buffer;
            lock (SpeakerPlaybackBuffers)
                SpeakerPlaybackBuffers.TryGetValue(speakerClientId, out buffer);

            if (buffer is null)
            {
                if (count > 0)
                    Array.Clear(output, offset, count);
                return 0;
            }

            return buffer.ReadAndFillSilence(output, count, offset);
        }

        /// <summary>
        /// Drains and returns all remaining speaker PCM bytes.
        /// </summary>
        public byte[] FlushSpeakerPlayback(Guid speakerClientId)
        {
            VoicePlaybackBuffer? buffer;
            lock (SpeakerPlaybackBuffers)
                SpeakerPlaybackBuffers.TryGetValue(speakerClientId, out buffer);

            return buffer?.Flush() ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Returns active speaker ids with playback buffers.
        /// </summary>
        public Guid[] GetSpeakersWithPlayback()
        {
            lock (SpeakerPlaybackBuffers)
                return SpeakerPlaybackBuffers.Keys.ToArray();
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
