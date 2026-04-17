using System;
using UnityEngine;

namespace OpenVoiceSharp.Unity
{
    /// <summary>
    /// Unity audio playback component for one remote speaker id.
    /// Reads PCM from AuthoritativeVoiceSession playback buffers and outputs audio in OnAudioFilterRead.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class UnityVoicePlaybackSource : MonoBehaviour
    {
        [SerializeField] private UnityVoiceSessionBehaviour? sessionBehaviour;
        [SerializeField] private string speakerId = "";
        [SerializeField] private bool logParseErrors;

        private Guid parsedSpeakerId;
        private bool hasValidSpeakerId;
        private byte[] pcmBuffer = Array.Empty<byte>();
        private float[] monoFloatBuffer = Array.Empty<float>();

        public string SpeakerId
        {
            get => speakerId;
            set
            {
                speakerId = value;
                ParseSpeakerId();
            }
        }

        private void Awake()
        {
            ParseSpeakerId();
        }

        private void OnValidate()
        {
            ParseSpeakerId();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (channels <= 0)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }

            if (!TryGetSession(out AuthoritativeVoiceSession? session) || !hasValidSpeakerId)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }

            int monoSampleCount = data.Length / channels;
            int bytesNeeded = monoSampleCount * 2;
            EnsureTempBuffers(bytesNeeded, monoSampleCount);

            // This call fills remaining bytes with silence automatically.
            session.ReadSpeakerPlayback(parsedSpeakerId, pcmBuffer, bytesNeeded);
            VoiceUtilities.Convert16BitToFloat(pcmBuffer, monoFloatBuffer, bytesNeeded);

            int sampleIndex = 0;
            for (int i = 0; i < monoSampleCount; i++)
            {
                float sample = monoFloatBuffer[i];
                for (int c = 0; c < channels; c++)
                    data[sampleIndex++] = sample;
            }
        }

        private bool TryGetSession(out AuthoritativeVoiceSession? session)
        {
            session = sessionBehaviour?.Session;
            return session is not null && sessionBehaviour is not null && sessionBehaviour.IsConnected;
        }

        private void ParseSpeakerId()
        {
            if (Guid.TryParse(speakerId, out Guid parsed))
            {
                parsedSpeakerId = parsed;
                hasValidSpeakerId = true;
                return;
            }

            hasValidSpeakerId = false;
            if (logParseErrors && !string.IsNullOrWhiteSpace(speakerId))
                Debug.LogWarning($"[OpenVoiceSharp.Unity] Invalid speaker id guid: {speakerId}");
        }

        private void EnsureTempBuffers(int bytesNeeded, int monoSampleCount)
        {
            if (pcmBuffer.Length < bytesNeeded)
                pcmBuffer = new byte[bytesNeeded];
            if (monoFloatBuffer.Length < monoSampleCount)
                monoFloatBuffer = new float[monoSampleCount];
        }
    }
}
