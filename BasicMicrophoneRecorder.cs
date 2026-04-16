using NAudio.Wave;

namespace OpenVoiceSharp
{
    /// <summary>
    /// Handles basic microphone recording for a voice chat interface using NAudio.
    /// </summary>
    public sealed class BasicMicrophoneRecorder : IDisposable
    {
        // events
        public delegate void AudioInputChangedEvent(int index, WaveInCapabilities microphone);
        public event AudioInputChangedEvent? AudioInputChanged;

        public delegate void MicrophoneDataAvailableEvent(byte[] pcmData, int length);
        public event MicrophoneDataAvailableEvent? DataAvailable;

        public delegate void MicrophoneStoppedRecordingEvent(StoppedEventArgs arguments);
        public event MicrophoneStoppedRecordingEvent? RecordingStopped;

        // wave format/recorder
        private readonly WaveFormat WaveFormat;
        private readonly WaveInEvent MicrophoneRecorder;

        // setting to a specific microphone
        public int CurrentMicrophoneIndex { get; private set; } = 0; // default
        public WaveInCapabilities CurrentMicrophone { get; private set; }
        private bool IsDisposed;

        public void SetMicrophone(int index)
        {
            ThrowIfDisposed();

            WaveInCapabilities[] microphones = GetMicrophones();
            if (microphones.Length == 0)
                throw new InvalidOperationException("No microphones are available.");
            if (index < 0 || index >= microphones.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            bool wasRecording = IsRecording;
            if (wasRecording)
                StopRecording();

            CurrentMicrophone = microphones[index];
            CurrentMicrophoneIndex = index;
            MicrophoneRecorder.DeviceNumber = CurrentMicrophoneIndex;

            AudioInputChanged?.Invoke(CurrentMicrophoneIndex, CurrentMicrophone);

            if (wasRecording)
                StartRecording();
        }

        public static WaveInCapabilities[] GetMicrophones()
        {
            WaveInCapabilities[] microphones = new WaveInCapabilities[WaveInEvent.DeviceCount];

            for (int i = 0; i < microphones.Length; i++)
                microphones[i] = WaveInEvent.GetCapabilities(i);

            return microphones;
        }

        public void SetToDefaultMicrophone() => SetMicrophone(0);

        // recording
        public bool IsRecording { get; private set; } = false;

        public void StartRecording()
        {
            ThrowIfDisposed();
            if (IsRecording) return;
            IsRecording = true;

            MicrophoneRecorder.StartRecording();
        }

        public void StopRecording()
        {
            ThrowIfDisposed();
            if (!IsRecording) return;
            IsRecording = false;

            MicrophoneRecorder.StopRecording();
        }

        private void WhenRecordingStopped(object? sender, StoppedEventArgs e) => RecordingStopped?.Invoke(e);
        private void WhenDataAvailable(object? sender, WaveInEventArgs e) => DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);

        public BasicMicrophoneRecorder(bool stereo = false)
        {
            // wave format for recording
            WaveFormat = new(rate: VoiceChatInterface.SampleRate, bits: 16, channels: stereo ? 2 : 1);

            // recorder and events
            MicrophoneRecorder = new()
            {
                BufferMilliseconds = VoiceChatInterface.FrameLength,
                WaveFormat = WaveFormat,
                DeviceNumber = 0
            };
            MicrophoneRecorder.DataAvailable += WhenDataAvailable;
            MicrophoneRecorder.RecordingStopped += WhenRecordingStopped;

            // set to default microphone
            SetToDefaultMicrophone();
        }

        public void Dispose()
        {
            if (IsDisposed) return;

            try
            {
                if (IsRecording)
                    MicrophoneRecorder.StopRecording();
            }
            catch
            {
                // ignored: dispose should not throw on shutdown paths
            }

            MicrophoneRecorder.DataAvailable -= WhenDataAvailable;
            MicrophoneRecorder.RecordingStopped -= WhenRecordingStopped;
            MicrophoneRecorder.Dispose();
            IsDisposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(BasicMicrophoneRecorder));
        }
    }
}
