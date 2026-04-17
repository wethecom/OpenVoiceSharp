using System;
using System.Threading.Tasks;
using UnityEngine;

namespace OpenVoiceSharp.Unity
{
    /// <summary>
    /// Unity MonoBehaviour wrapper around AuthoritativeVoiceSession.
    /// </summary>
    public sealed class UnityVoiceSessionBehaviour : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverHost = "127.0.0.1";
        [SerializeField] private int serverPort = 7777;
        [SerializeField] private string roomName = "lobby";
        [SerializeField] private string userName = "Player";
        [SerializeField] private string authToken = "";
        [SerializeField] private bool autoConnectOnStart = true;

        [Header("Audio")]
        [SerializeField] private int bitrate = VoiceChatInterface.DefaultBitrate;
        [SerializeField] private bool stereo = false;
        [SerializeField] private bool enableNoiseSuppression = true;
        [SerializeField] private bool favorAudioStreaming = false;

        [Header("Jitter")]
        [SerializeField] private bool enableJitterBuffer = true;
        [SerializeField] private int jitterTargetPackets = 3;
        [SerializeField] private int jitterMaxPackets = 24;

        public AuthoritativeVoiceSession? Session { get; private set; }
        public bool IsConnected => Session?.Client.IsConnected ?? false;

        private bool isConnecting;

        private void Awake()
        {
            CreateSessionIfNeeded();
        }

        private async void Start()
        {
            if (autoConnectOnStart)
                await ConnectAsync();
        }

        private void OnDestroy()
        {
            DisposeSession();
        }

        public async Task ConnectAsync()
        {
            CreateSessionIfNeeded();
            if (Session is null || IsConnected || isConnecting)
                return;

            isConnecting = true;
            try
            {
                await Session.StartAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[OpenVoiceSharp.Unity] Connect failed: {exception}");
            }
            finally
            {
                isConnecting = false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (Session is null)
                return;

            try
            {
                await Session.StopAsync();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[OpenVoiceSharp.Unity] Disconnect warning: {exception.Message}");
            }
        }

        private void CreateSessionIfNeeded()
        {
            if (Session is not null)
                return;

            string? token = string.IsNullOrWhiteSpace(authToken) ? null : authToken;
            Session = new AuthoritativeVoiceSession(
                serverHost,
                serverPort,
                roomName,
                userName,
                token,
                bitrate,
                stereo,
                enableNoiseSuppression,
                favorAudioStreaming,
                vadOperatingMode: null,
                jitterTargetPackets: jitterTargetPackets,
                jitterMaxPackets: jitterMaxPackets
            )
            {
                EnableJitterBuffer = enableJitterBuffer
            };
            Session.SessionError += OnSessionError;
        }

        private void DisposeSession()
        {
            if (Session is null)
                return;

            Session.SessionError -= OnSessionError;
            Session.Dispose();
            Session = null;
        }

        private void OnSessionError(string message, Exception? exception)
        {
            if (exception is not null)
                Debug.LogWarning($"[OpenVoiceSharp.Unity] {message} {exception.Message}");
            else
                Debug.LogWarning($"[OpenVoiceSharp.Unity] {message}");
        }
    }
}
