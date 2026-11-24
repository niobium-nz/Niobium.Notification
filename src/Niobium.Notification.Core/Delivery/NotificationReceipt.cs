using System.Security.Cryptography;
using System.Text;

namespace Niobium.Notification.Delivery;

public enum ReceiptPublication { Pending = 1, Published = 2, Uncorrelated = 3 }
public enum ReceiptHoldReason
{
    None = 0, MissingCorrelation = 1, SubmissionMissing = 2, CorrelationMismatch = 3,
    ProviderMessageMismatch = 4, LegacyOperationMissing = 5,
}

// Owns persisted event identity and representation. The receipt domain decides
// correlation/publication; a provider event never directly changes an invoice.
public sealed record NotificationReceipt
{
    public const int CurrentVersion = 1;
    public int Version { get; private init; }
    [EntityKey(EntityKeyKind.PartitionKey)] public string Provider { get; private init; } = String.Empty;
    [EntityKey(EntityKeyKind.RowKey)] public string ID { get; private init; } = String.Empty;
    [EntityKey(EntityKeyKind.ETag)] public string? ETag { get; init; }
    [EntityKey(EntityKeyKind.Timestamp)] public DateTimeOffset? Timestamp { get; init; }
    public string ProviderEventID { get; private init; } = String.Empty;
    public string ProviderMessageID { get; private init; } = String.Empty;
    public string BodyFingerprint { get; private init; } = String.Empty;
    public int KindCode { get; private init; }
    public DateTimeOffset OccurredAt { get; private init; }
    public DateTimeOffset ReceivedAt { get; private init; }
    public string? Tenant { get; private init; }
    public string? SubmissionID { get; private init; }
    public string? CorrelationProof { get; private init; }
    public int PublicationCode { get; init; }
    public int HoldReasonCode { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string WriteID { get; init; } = String.Empty;

    public static NotificationReceipt Create(CaptureNotificationReceiptCommand command) => new()
    {
        Version = CurrentVersion, Provider = command.Provider,
        ID = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.ID))),
        ProviderEventID = command.ID, ProviderMessageID = command.ProviderMessageID,
        BodyFingerprint = command.BodyFingerprint, KindCode = (int)command.Kind,
        OccurredAt = command.OccurredAt, ReceivedAt = command.ReceivedAt,
        Tenant = command.Correlation?.Tenant, SubmissionID = command.Correlation?.SubmissionID,
        CorrelationProof = command.Correlation?.Proof,
    };

    public bool MatchesCapture(NotificationReceipt candidate)
        => this.Version == CurrentVersion && candidate.Version == CurrentVersion
            && this.Provider == candidate.Provider && this.ID == candidate.ID && this.ProviderEventID == candidate.ProviderEventID
            && this.ProviderMessageID == candidate.ProviderMessageID && this.BodyFingerprint == candidate.BodyFingerprint
            && this.KindCode == candidate.KindCode && this.OccurredAt == candidate.OccurredAt
            && this.Tenant == candidate.Tenant && this.SubmissionID == candidate.SubmissionID && this.CorrelationProof == candidate.CorrelationProof;

    public NotificationCorrelation? GetCorrelation() => this.Tenant != null && this.SubmissionID != null && this.CorrelationProof != null
        ? new(this.Tenant, this.SubmissionID, this.CorrelationProof) : null;

    internal NotificationDeliveryEvent ToDeliveryEvent(NotificationSubmission submission) => new()
    {
        ID = $"{this.Provider}/{this.ID}", Tenant = submission.GetTenantID(),
        NotificationID = submission.Operation!, SubmissionID = submission.ID, Channel = submission.Channel!,
        Provider = this.Provider, ProviderEventID = this.ProviderEventID, ProviderMessageID = this.ProviderMessageID,
        Kind = (NotificationDeliveryKind)this.KindCode, OccurredAt = this.OccurredAt,
    };
}
