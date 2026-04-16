using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OpenVoiceSharp
{
    /// <summary>
    /// UDP client for the OpenVoiceSharp authoritative voice server.
    /// Handles room join/leave, voice packet send/receive, and peer events.
    /// </summary>
    public sealed class AuthoritativeVoiceClient : IDisposable
    {
        // protocol packet types
        private const byte ClientHello = 1;
        private const byte ClientVoice = 2;
        private const byte ClientLeave = 3;
        private const byte ClientPing = 4;

        private const byte ServerWelcome = 11;
        private const byte ServerVoiceRelay = 12;
        private const byte ServerError = 13;
        private const byte ServerPeerJoined = 14;
        private const byte ServerPeerLeft = 15;
        private const byte ServerPong = 16;

        // events
        public delegate void ConnectedEvent(Guid clientId);
        public event ConnectedEvent? Connected;

        public delegate void PeerJoinedEvent(Guid clientId, string userName);
        public event PeerJoinedEvent? PeerJoined;

        public delegate void PeerLeftEvent(Guid clientId);
        public event PeerLeftEvent? PeerLeft;

        public delegate void VoicePacketReceivedEvent(Guid speakerClientId, uint sequence, byte[] payload, int length);
        public event VoicePacketReceivedEvent? VoicePacketReceived;

        public delegate void ServerErrorEvent(byte errorCode, string message);
        public event ServerErrorEvent? ErrorReceived;

        public delegate void PongReceivedEvent(Guid clientId);
        public event PongReceivedEvent? PongReceived;

        public delegate void DisconnectedEvent();
        public event DisconnectedEvent? Disconnected;

        // settings
        public string ServerHost { get; }
        public int ServerPort { get; }
        public string RoomName { get; private set; }
        public string UserName { get; private set; }
        public Guid ClientId { get; }
        public bool IsConnected { get; private set; }

        private IPEndPoint? ServerEndpoint;
        private UdpClient? UdpClient;
        private CancellationTokenSource? ReceiveCancellationTokenSource;
        private Task? ReceiveTask;
        private TaskCompletionSource<Guid>? PendingWelcomeTaskCompletionSource;
        private int NextVoiceSequence;
        private bool IsDisposed;

        public AuthoritativeVoiceClient(
            string serverHost,
            int serverPort,
            string roomName,
            string userName,
            Guid? clientId = null
        )
        {
            if (string.IsNullOrWhiteSpace(serverHost))
                throw new ArgumentException("Server host is required.", nameof(serverHost));
            if (serverPort <= 0 || serverPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            if (string.IsNullOrWhiteSpace(roomName))
                throw new ArgumentException("Room name is required.", nameof(roomName));
            if (string.IsNullOrWhiteSpace(userName))
                throw new ArgumentException("User name is required.", nameof(userName));

            ServerHost = serverHost;
            ServerPort = serverPort;
            RoomName = roomName.Trim();
            UserName = userName.Trim();
            ClientId = clientId ?? Guid.NewGuid();
        }

        /// <summary>
        /// Connects to the authoritative server and waits for welcome.
        /// </summary>
        /// <param name="handshakeTimeoutMs">Handshake timeout in milliseconds.</param>
        public async Task ConnectAsync(int handshakeTimeoutMs = 5000)
        {
            ThrowIfDisposed();
            if (IsConnected)
                return;
            if (handshakeTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeoutMs));

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(ServerHost).ConfigureAwait(false);
            IPAddress? address = Array.Find(addresses, static ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? Array.Find(addresses, static ip => ip.AddressFamily == AddressFamily.InterNetworkV6);

            if (address is null)
                throw new InvalidOperationException($"Could not resolve server host: {ServerHost}");

            ServerEndpoint = new IPEndPoint(address, ServerPort);
            UdpClient = new UdpClient(address.AddressFamily);
            ReceiveCancellationTokenSource = new CancellationTokenSource();
            PendingWelcomeTaskCompletionSource = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReceiveTask = Task.Run(ReceiveLoopAsync);

            byte[] helloPacket = BuildHelloPacket(ClientId, RoomName, UserName);
            await UdpClient.SendAsync(helloPacket, helloPacket.Length, ServerEndpoint).ConfigureAwait(false);

            Task delayTask = Task.Delay(handshakeTimeoutMs);
            Task completedTask = await Task.WhenAny(PendingWelcomeTaskCompletionSource.Task, delayTask).ConfigureAwait(false);
            if (completedTask != PendingWelcomeTaskCompletionSource.Task)
            {
                await DisconnectAsync().ConfigureAwait(false);
                throw new TimeoutException("Did not receive welcome packet from server in time.");
            }

            // Observe the task in case it faulted.
            _ = await PendingWelcomeTaskCompletionSource.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Sends an encoded Opus payload to the server for room broadcast.
        /// </summary>
        public async Task SendVoiceAsync(byte[] encodedOpusPayload, int length)
        {
            ThrowIfDisposed();
            if (!IsConnected || UdpClient is null || ServerEndpoint is null)
                throw new InvalidOperationException("Client is not connected.");
            if (encodedOpusPayload is null)
                throw new ArgumentNullException(nameof(encodedOpusPayload));
            if (length <= 0 || length > encodedOpusPayload.Length)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(length), "Payload length must be <= 65535.");

            uint sequence = unchecked((uint)Interlocked.Increment(ref NextVoiceSequence));
            byte[] packet = BuildVoicePacket(ClientId, sequence, encodedOpusPayload, length);
            await UdpClient.SendAsync(packet, packet.Length, ServerEndpoint).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends a ping packet.
        /// </summary>
        public async Task PingAsync()
        {
            ThrowIfDisposed();
            if (!IsConnected || UdpClient is null || ServerEndpoint is null)
                throw new InvalidOperationException("Client is not connected.");

            byte[] pingPacket = BuildSingleGuidPacket(ClientPing, ClientId);
            await UdpClient.SendAsync(pingPacket, pingPacket.Length, ServerEndpoint).ConfigureAwait(false);
        }

        /// <summary>
        /// Disconnects from the server and stops receive loop.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (UdpClient is not null && ServerEndpoint is not null)
            {
                try
                {
                    byte[] leavePacket = BuildSingleGuidPacket(ClientLeave, ClientId);
                    await UdpClient.SendAsync(leavePacket, leavePacket.Length, ServerEndpoint).ConfigureAwait(false);
                }
                catch
                {
                    // ignore transport errors during disconnect
                }
            }

            await StopNetworkingAsync().ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync()
        {
            if (UdpClient is null)
                return;

            try
            {
                while (!ReceiveCancellationTokenSource!.IsCancellationRequested)
                {
                    UdpReceiveResult result = await UdpClient.ReceiveAsync().ConfigureAwait(false);
                    if (ServerEndpoint is null || !EndpointsEqual(result.RemoteEndPoint, ServerEndpoint))
                        continue;

                    HandleServerPacket(result.Buffer);
                }
            }
            catch (ObjectDisposedException)
            {
                // expected on shutdown
            }
            catch (SocketException)
            {
                // expected on shutdown/close path
            }
            catch (Exception exception)
            {
                ErrorReceived?.Invoke(0, $"Receive loop failed: {exception.Message}");
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    Disconnected?.Invoke();
                }
            }
        }

        private void HandleServerPacket(byte[] packet)
        {
            if (packet.Length == 0)
                return;

            switch (packet[0])
            {
                case ServerWelcome:
                    HandleWelcome(packet);
                    break;
                case ServerVoiceRelay:
                    HandleVoiceRelay(packet);
                    break;
                case ServerError:
                    HandleServerError(packet);
                    break;
                case ServerPeerJoined:
                    HandlePeerJoined(packet);
                    break;
                case ServerPeerLeft:
                    HandlePeerLeft(packet);
                    break;
                case ServerPong:
                    HandlePong(packet);
                    break;
            }
        }

        private void HandleWelcome(byte[] packet)
        {
            if (packet.Length != 17)
                return;

            Guid welcomeClientId = ReadGuid(packet, 1);
            if (welcomeClientId != ClientId)
                return;

            IsConnected = true;
            PendingWelcomeTaskCompletionSource?.TrySetResult(welcomeClientId);
            Connected?.Invoke(welcomeClientId);
        }

        private void HandleVoiceRelay(byte[] packet)
        {
            if (packet.Length < 23)
                return;

            Guid speakerClientId = ReadGuid(packet, 1);
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(17, 4));
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(21, 2));
            if (packet.Length != 23 + payloadLength)
                return;

            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(packet, 23, payload, 0, payloadLength);
            VoicePacketReceived?.Invoke(speakerClientId, sequence, payload, payload.Length);
        }

        private void HandleServerError(byte[] packet)
        {
            if (packet.Length < 3)
                return;

            byte errorCode = packet[1];
            byte messageLength = packet[2];
            if (packet.Length != 3 + messageLength)
                return;

            string message = Encoding.UTF8.GetString(packet, 3, messageLength);
            ErrorReceived?.Invoke(errorCode, message);
        }

        private void HandlePeerJoined(byte[] packet)
        {
            if (packet.Length < 18)
                return;

            Guid peerClientId = ReadGuid(packet, 1);
            byte userNameLength = packet[17];
            if (packet.Length != 18 + userNameLength)
                return;

            string userName = Encoding.UTF8.GetString(packet, 18, userNameLength);
            PeerJoined?.Invoke(peerClientId, userName);
        }

        private void HandlePeerLeft(byte[] packet)
        {
            if (packet.Length != 17)
                return;

            Guid peerClientId = ReadGuid(packet, 1);
            PeerLeft?.Invoke(peerClientId);
        }

        private void HandlePong(byte[] packet)
        {
            if (packet.Length != 17)
                return;

            Guid pongClientId = ReadGuid(packet, 1);
            if (pongClientId == ClientId)
                PongReceived?.Invoke(pongClientId);
        }

        private async Task StopNetworkingAsync()
        {
            ReceiveCancellationTokenSource?.Cancel();

            try
            {
                UdpClient?.Close();
            }
            catch
            {
                // ignore close errors
            }

            if (ReceiveTask is not null)
            {
                try
                {
                    await ReceiveTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignore receive loop errors on shutdown
                }
            }

            ReceiveTask = null;
            PendingWelcomeTaskCompletionSource = null;
            UdpClient?.Dispose();
            UdpClient = null;
            ReceiveCancellationTokenSource?.Dispose();
            ReceiveCancellationTokenSource = null;
            ServerEndpoint = null;

            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            StopNetworkingAsync().GetAwaiter().GetResult();
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(AuthoritativeVoiceClient));
        }

        private static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
            => a.Port == b.Port && Equals(a.Address, b.Address);

        private static Guid ReadGuid(byte[] data, int startIndex)
        {
            byte[] guidBytes = new byte[16];
            Buffer.BlockCopy(data, startIndex, guidBytes, 0, 16);
            return new Guid(guidBytes);
        }

        private static byte[] BuildSingleGuidPacket(byte packetType, Guid clientId)
        {
            byte[] packet = new byte[17];
            packet[0] = packetType;
            clientId.TryWriteBytes(packet.AsSpan(1, 16));
            return packet;
        }

        private static byte[] BuildHelloPacket(Guid clientId, string roomName, string userName)
        {
            byte[] roomNameBytes = Encoding.UTF8.GetBytes(roomName);
            byte[] userNameBytes = Encoding.UTF8.GetBytes(userName);
            if (roomNameBytes.Length > byte.MaxValue)
                throw new ArgumentException("Room name is too long after UTF8 encoding.", nameof(roomName));
            if (userNameBytes.Length > byte.MaxValue)
                throw new ArgumentException("User name is too long after UTF8 encoding.", nameof(userName));

            byte[] packet = new byte[19 + roomNameBytes.Length + userNameBytes.Length];
            packet[0] = ClientHello;
            clientId.TryWriteBytes(packet.AsSpan(1, 16));
            packet[17] = (byte)roomNameBytes.Length;
            roomNameBytes.CopyTo(packet.AsSpan(18));

            int userLengthIndex = 18 + roomNameBytes.Length;
            packet[userLengthIndex] = (byte)userNameBytes.Length;
            userNameBytes.CopyTo(packet.AsSpan(userLengthIndex + 1));
            return packet;
        }

        private static byte[] BuildVoicePacket(Guid clientId, uint sequence, byte[] payload, int payloadLength)
        {
            byte[] packet = new byte[23 + payloadLength];
            packet[0] = ClientVoice;
            clientId.TryWriteBytes(packet.AsSpan(1, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(17, 4), sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(21, 2), (ushort)payloadLength);
            Buffer.BlockCopy(payload, 0, packet, 23, payloadLength);
            return packet;
        }
    }
}
