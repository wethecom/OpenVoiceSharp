#pragma once

#include "CoreMinimal.h"
#include "Serialization/ArrayReader.h"
#include "UObject/Object.h"
#include "OVSAuthoritativeClient.generated.h"

class FUdpSocketReceiver;
class FSocket;
class FInternetAddr;
struct FIPv4Endpoint;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOVSPacketReceived, const TArray<uint8>&, Data);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOVSErrorMessage, const FString&, ErrorMessage);

UCLASS(BlueprintType)
class OPENVOICESHARPUNREAL_API UOVSAuthoritativeClient : public UObject
{
    GENERATED_BODY()

public:
    UOVSAuthoritativeClient();

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    void InitializeSession(const FGuid& InClientId, const FString& InRoomName, const FString& InUserName);

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool Connect(const FString& InServerIp, int32 InServerPort, int32 InLocalBindPort = 0);

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    void Disconnect();

    UFUNCTION(BlueprintPure, Category = "OpenVoiceSharp")
    bool IsConnected() const;

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendRawPacket(const TArray<uint8>& Data);

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendHello();

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendAuthHello(const FString& AccessToken);

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendVoicePacket(uint32 Sequence, const TArray<uint8>& EncodedVoiceData);

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendLeave();

    UFUNCTION(BlueprintCallable, Category = "OpenVoiceSharp")
    bool SendPing();

    UPROPERTY(BlueprintAssignable, Category = "OpenVoiceSharp")
    FOVSPacketReceived OnPacketReceived;

    UPROPERTY(BlueprintAssignable, Category = "OpenVoiceSharp")
    FOVSErrorMessage OnError;

protected:
    virtual void BeginDestroy() override;

private:
    void HandleSocketData(const FArrayReaderPtr& Data, const FIPv4Endpoint& Endpoint);
    bool SendInternal(const TArray<uint8>& Data);
    void BroadcastError(const FString& Message);
    bool ValidateIdentity(FString& ErrorReason) const;
    static bool AppendLengthPrefixedUtf8ByteString(TArray<uint8>& Out, const FString& Value, FString& ErrorReason);
    static void AppendGuidBytes(TArray<uint8>& Out, const FGuid& Value);
    static void AppendUInt16LE(TArray<uint8>& Out, uint16 Value);
    static void AppendUInt32LE(TArray<uint8>& Out, uint32 Value);

private:
    FSocket* Socket;
    FUdpSocketReceiver* Receiver;
    TSharedPtr<FInternetAddr> ServerAddress;
    uint32 ServerIpValue;
    int32 ServerPortValue;
    FGuid ClientId;
    FString RoomName;
    FString UserName;
    bool bHasIdentity;
};
