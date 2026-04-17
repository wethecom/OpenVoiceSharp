#include "OVSAuthoritativeClient.h"

#include "Common/UdpSocketBuilder.h"
#include "Common/UdpSocketReceiver.h"
#include "HAL/UnrealMemory.h"
#include "IPAddress.h"
#include "Interfaces/IPv4/IPv4Address.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"
#include "Serialization/ArrayReader.h"
#include "SocketSubsystem.h"
#include "Sockets.h"

UOVSAuthoritativeClient::UOVSAuthoritativeClient()
    : Socket(nullptr)
    , Receiver(nullptr)
    , ServerIpValue(0)
    , ServerPortValue(0)
    , bHasIdentity(false)
{
}

void UOVSAuthoritativeClient::InitializeSession(const FGuid& InClientId, const FString& InRoomName, const FString& InUserName)
{
    ClientId = InClientId;
    RoomName = InRoomName;
    UserName = InUserName;
    bHasIdentity = true;
}

bool UOVSAuthoritativeClient::Connect(const FString& InServerIp, int32 InServerPort, int32 InLocalBindPort)
{
    if (InServerPort <= 0 || InServerPort > 65535)
    {
        BroadcastError(TEXT("Server port is out of range."));
        return false;
    }

    FIPv4Address ParsedAddress;
    if (!FIPv4Address::Parse(InServerIp, ParsedAddress))
    {
        BroadcastError(TEXT("Server IP must be a valid IPv4 address (for example 127.0.0.1)."));
        return false;
    }

    Disconnect();

    Socket = FUdpSocketBuilder(TEXT("OVSAuthoritativeClientSocket"))
        .AsReusable()
        .AsNonBlocking()
        .BoundToPort(InLocalBindPort)
        .WithReceiveBufferSize(2 * 1024 * 1024)
        .WithSendBufferSize(2 * 1024 * 1024);

    if (Socket == nullptr)
    {
        BroadcastError(TEXT("Failed to create UDP socket."));
        return false;
    }

    ServerAddress = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->CreateInternetAddr();
    ServerAddress->SetIp(ParsedAddress.Value);
    ServerAddress->SetPort(InServerPort);
    ServerIpValue = ParsedAddress.Value;
    ServerPortValue = InServerPort;

    Receiver = new FUdpSocketReceiver(Socket, FTimespan::FromMilliseconds(2), TEXT("OVSAuthoritativeClientReceiver"));
    Receiver->OnDataReceived().BindUObject(this, &UOVSAuthoritativeClient::HandleSocketData);
    Receiver->Start();
    return true;
}

void UOVSAuthoritativeClient::Disconnect()
{
    if (Receiver != nullptr)
    {
        Receiver->Stop();
        delete Receiver;
        Receiver = nullptr;
    }

    if (Socket != nullptr)
    {
        ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->DestroySocket(Socket);
        Socket = nullptr;
    }

    ServerAddress.Reset();
    ServerIpValue = 0;
    ServerPortValue = 0;
}

bool UOVSAuthoritativeClient::IsConnected() const
{
    return Socket != nullptr && ServerAddress.IsValid();
}

bool UOVSAuthoritativeClient::SendRawPacket(const TArray<uint8>& Data)
{
    if (Data.Num() == 0)
    {
        BroadcastError(TEXT("Cannot send an empty packet."));
        return false;
    }

    return SendInternal(Data);
}

bool UOVSAuthoritativeClient::SendHello()
{
    FString ErrorReason;
    if (!ValidateIdentity(ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    // Protocol format:
    // [1 byte: type=1][16 bytes: clientId][1 byte+utf8: room][1 byte+utf8: user]
    TArray<uint8> Packet;
    Packet.Reserve(1 + 16 + 2 + RoomName.Len() + UserName.Len());
    Packet.Add(1);
    AppendGuidBytes(Packet, ClientId);

    if (!AppendLengthPrefixedUtf8ByteString(Packet, RoomName, ErrorReason) ||
        !AppendLengthPrefixedUtf8ByteString(Packet, UserName, ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    return SendInternal(Packet);
}

bool UOVSAuthoritativeClient::SendAuthHello(const FString& AccessToken)
{
    FString ErrorReason;
    if (!ValidateIdentity(ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    FTCHARToUTF8 TokenUtf8(*AccessToken);
    const int32 TokenSize = TokenUtf8.Length();
    if (TokenSize < 0 || TokenSize > 65535)
    {
        BroadcastError(TEXT("Access token is too large."));
        return false;
    }

    // Protocol format:
    // [1 byte: type=5][16 bytes: clientId][1 byte+utf8: room][1 byte+utf8: user][2 bytes LE token len][token utf8]
    TArray<uint8> Packet;
    Packet.Reserve(1 + 16 + 2 + RoomName.Len() + UserName.Len() + 2 + TokenSize);
    Packet.Add(5);
    AppendGuidBytes(Packet, ClientId);

    if (!AppendLengthPrefixedUtf8ByteString(Packet, RoomName, ErrorReason) ||
        !AppendLengthPrefixedUtf8ByteString(Packet, UserName, ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    AppendUInt16LE(Packet, static_cast<uint16>(TokenSize));
    if (TokenSize > 0)
    {
        Packet.Append(reinterpret_cast<const uint8*>(TokenUtf8.Get()), TokenSize);
    }

    return SendInternal(Packet);
}

bool UOVSAuthoritativeClient::SendVoicePacket(uint32 Sequence, const TArray<uint8>& EncodedVoiceData)
{
    FString ErrorReason;
    if (!ValidateIdentity(ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    if (EncodedVoiceData.Num() == 0)
    {
        BroadcastError(TEXT("Encoded voice packet cannot be empty."));
        return false;
    }

    if (EncodedVoiceData.Num() > 65535)
    {
        BroadcastError(TEXT("Encoded voice packet is too large."));
        return false;
    }

    // Protocol format:
    // [1 byte: type=2][16 bytes: clientId][4 bytes LE: sequence][2 bytes LE: payloadLength][payload]
    TArray<uint8> Packet;
    Packet.Reserve(1 + 16 + 4 + 2 + EncodedVoiceData.Num());
    Packet.Add(2);
    AppendGuidBytes(Packet, ClientId);
    AppendUInt32LE(Packet, Sequence);
    AppendUInt16LE(Packet, static_cast<uint16>(EncodedVoiceData.Num()));
    Packet.Append(EncodedVoiceData);
    return SendInternal(Packet);
}

bool UOVSAuthoritativeClient::SendLeave()
{
    FString ErrorReason;
    if (!ValidateIdentity(ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    TArray<uint8> Packet;
    Packet.Reserve(1 + 16);
    Packet.Add(3);
    AppendGuidBytes(Packet, ClientId);
    return SendInternal(Packet);
}

bool UOVSAuthoritativeClient::SendPing()
{
    FString ErrorReason;
    if (!ValidateIdentity(ErrorReason))
    {
        BroadcastError(ErrorReason);
        return false;
    }

    TArray<uint8> Packet;
    Packet.Reserve(1 + 16);
    Packet.Add(4);
    AppendGuidBytes(Packet, ClientId);
    return SendInternal(Packet);
}

void UOVSAuthoritativeClient::BeginDestroy()
{
    Disconnect();
    Super::BeginDestroy();
}

void UOVSAuthoritativeClient::HandleSocketData(const FArrayReaderPtr& Data, const FIPv4Endpoint& Endpoint)
{
    if (!Data.IsValid() || Data->Num() <= 0)
    {
        return;
    }

    if (!ServerAddress.IsValid())
    {
        return;
    }

    if (Endpoint.Address.Value != ServerIpValue || Endpoint.Port != ServerPortValue)
    {
        return;
    }

    TArray<uint8> Out;
    Out.Append(Data->GetData(), Data->Num());
    OnPacketReceived.Broadcast(Out);
}

bool UOVSAuthoritativeClient::SendInternal(const TArray<uint8>& Data)
{
    if (!IsConnected())
    {
        BroadcastError(TEXT("Client is not connected."));
        return false;
    }

    int32 BytesSent = 0;
    const bool bSent = Socket->SendTo(Data.GetData(), Data.Num(), BytesSent, *ServerAddress);
    if (!bSent || BytesSent != Data.Num())
    {
        BroadcastError(TEXT("Failed to send packet over UDP."));
        return false;
    }

    return true;
}

void UOVSAuthoritativeClient::BroadcastError(const FString& Message)
{
    OnError.Broadcast(Message);
}

bool UOVSAuthoritativeClient::ValidateIdentity(FString& ErrorReason) const
{
    if (!bHasIdentity)
    {
        ErrorReason = TEXT("Identity was not initialized. Call InitializeSession first.");
        return false;
    }

    if (!ClientId.IsValid())
    {
        ErrorReason = TEXT("ClientId is invalid. Provide a valid GUID.");
        return false;
    }

    return true;
}

bool UOVSAuthoritativeClient::AppendLengthPrefixedUtf8ByteString(TArray<uint8>& Out, const FString& Value, FString& ErrorReason)
{
    FTCHARToUTF8 Utf8(*Value);
    const int32 Size = Utf8.Length();
    if (Size < 0 || Size > 255)
    {
        ErrorReason = TEXT("Room/User value is too large. Max UTF-8 length is 255 bytes.");
        return false;
    }

    Out.Add(static_cast<uint8>(Size));
    if (Size > 0)
    {
        Out.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Size);
    }

    return true;
}

void UOVSAuthoritativeClient::AppendGuidBytes(TArray<uint8>& Out, const FGuid& Value)
{
    AppendUInt32LE(Out, Value.A);
    AppendUInt32LE(Out, Value.B);
    AppendUInt32LE(Out, Value.C);
    AppendUInt32LE(Out, Value.D);
}

void UOVSAuthoritativeClient::AppendUInt16LE(TArray<uint8>& Out, uint16 Value)
{
    Out.Add(static_cast<uint8>(Value & 0xFF));
    Out.Add(static_cast<uint8>((Value >> 8) & 0xFF));
}

void UOVSAuthoritativeClient::AppendUInt32LE(TArray<uint8>& Out, uint32 Value)
{
    Out.Add(static_cast<uint8>(Value & 0xFF));
    Out.Add(static_cast<uint8>((Value >> 8) & 0xFF));
    Out.Add(static_cast<uint8>((Value >> 16) & 0xFF));
    Out.Add(static_cast<uint8>((Value >> 24) & 0xFF));
}
