# OpenVoiceSharp Authoritative UDP Protocol

This protocol is room-based and server-authoritative for voice packet routing.

## Transport

- UDP only.
- Default server endpoint: `0.0.0.0:7777`.
- Packet layout uses little-endian integers.

## Client -> Server

### `Hello` (`type=1`)

Register or refresh a client session.

```text
byte    Type            = 1
bytes16 ClientId        (GUID bytes)
byte    RoomLength      (0-255)
bytesN  RoomUtf8
byte    UserLength      (0-255)
bytesM  UserUtf8
```

### `Voice` (`type=2`)

Submit one encoded Opus frame.

```text
byte    Type            = 2
bytes16 ClientId
uint32  Sequence
uint16  PayloadLength
bytesN  OpusPayload
```

### `Leave` (`type=3`)

```text
byte    Type            = 3
bytes16 ClientId
```

### `Ping` (`type=4`)

```text
byte    Type            = 4
bytes16 ClientId
```

### `AuthHello` (`type=5`)

`AuthHello` is required when WordPress verification is enabled on the server.

```text
byte    Type            = 5
bytes16 ClientId        (GUID bytes)
byte    RoomLength
bytesN  RoomUtf8
byte    UserLength
bytesM  UserUtf8
uint16  TokenLength
bytesK  AuthTokenUtf8
```

## Server -> Client

### `Welcome` (`type=11`)

```text
byte    Type            = 11
bytes16 ClientId
```

### `VoiceRelay` (`type=12`)

Forwarded voice packet from another peer in the same room.

```text
byte    Type            = 12
bytes16 SpeakerClientId
uint32  Sequence
uint16  PayloadLength
bytesN  OpusPayload
```

### `Error` (`type=13`)

```text
byte    Type            = 13
byte    ErrorCode
byte    MessageLength
bytesN  MessageUtf8
```

Codes:

- `1` InvalidPacket
- `2` NotRegistered
- `3` UnauthorizedEndpoint
- `4` RoomFull
- `5` RateLimited
- `6` PayloadTooLarge
- `7` AuthFailed

### `PeerJoined` (`type=14`)

```text
byte    Type            = 14
bytes16 ClientId
byte    UserLength
bytesN  UserUtf8
```

### `PeerLeft` (`type=15`)

```text
byte    Type            = 15
bytes16 ClientId
```

### `Pong` (`type=16`)

```text
byte    Type            = 16
bytes16 ClientId
```

## Server Behavior

- A client can only send voice from the endpoint that sent `Hello`.
- Voice packets are rate-limited per client.
- Voice sequence uses a 64-packet anti-replay window (duplicates/replays are dropped, limited out-of-order packets are accepted).
- Voice payload max size is configurable (`--max-voice-bytes`).
- Inactive clients are removed automatically (`--timeout-seconds`).

## Running Server

```bash
dotnet run --project OpenVoiceSharp.AuthoritativeServer -- \
  --port 7777 \
  --max-room-members 64 \
  --max-voice-bytes 4096 \
  --max-pps 80 \
  --timeout-seconds 30 \
  --stats-port 9090
```

Read live stats:

```bash
curl http://127.0.0.1:9090/stats
```

## WordPress Verification Mode

Enable token verification against a WordPress endpoint:

```bash
dotnet run --project OpenVoiceSharp.AuthoritativeServer -- \
  --port 7777 \
  --wp-verify-url "https://your-site.com/wp-json/openvoicesharp/v1/verify" \
  --wp-shared-secret "server-to-wp-shared-secret" \
  --wp-timeout-seconds 5
```

Server request behavior:

- Sends `Authorization: Bearer <token>` to your `--wp-verify-url`.
- Sends `X-OpenVoiceSharp-Secret` header when `--wp-shared-secret` is provided.
- Expects JSON containing one of: `valid`, `success`, or `authenticated` boolean fields (either at root or under `data`).
