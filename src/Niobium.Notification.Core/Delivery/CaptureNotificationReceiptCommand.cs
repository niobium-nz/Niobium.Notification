using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Niobium.Messaging;

namespace Niobium.Notification.Delivery;

// Service-private queue contract, created only after provider authentication.
// It excludes addresses, subject, HTML and raw webhook data.
public sealed class CaptureNotificationReceiptCommand : DomainEvent
{
    [JsonInclude] public string Provider { get; private init; } = String.Empty;
    [JsonInclude] public string ProviderMessageID { get; private init; } = String.Empty;
    [JsonInclude] public NotificationDeliveryKind Kind { get; private init; }
    [JsonInclude] public DateTimeOffset OccurredAt { get; private init; }
    [JsonInclude] public DateTimeOffset ReceivedAt { get; private init; }
    [JsonInclude] public string BodyFingerprint { get; private init; } = String.Empty;
    [JsonInclude] public NotificationCorrelation? Correlation { get; private init; }

    public bool HasValidRepresentation() => !String.IsNullOrWhiteSpace(ID) && ID.Length <= 128
        && !String.IsNullOrWhiteSpace(ProviderMessageID) && ProviderMessageID.Length <= 128
        && !String.IsNullOrEmpty(BodyFingerprint) && BodyFingerprint.Length == 64
        && BodyFingerprint.All(character => Char.IsAsciiHexDigitUpper(character))
        && (Correlation == null || Correlation.IsWellFormed());

    public static CaptureNotificationReceiptCommand Capture(string provider, string eventID, string providerMessageID,
        NotificationDeliveryKind kind, DateTimeOffset occurredAt, DateTimeOffset receivedAt,
        ReadOnlySpan<byte> verifiedBody, NotificationCorrelation? correlation) => new()
        {
            ID = eventID, Provider = provider, ProviderMessageID = providerMessageID, Kind = kind,
            OccurredAt = occurredAt.ToUniversalTime(), ReceivedAt = receivedAt.ToUniversalTime(),
            BodyFingerprint = Convert.ToHexString(SHA256.HashData(verifiedBody)), Correlation = correlation,
        };
}
