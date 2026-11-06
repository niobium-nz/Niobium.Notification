namespace Niobium.Notification.Delivery
{
    // Internal persistence contract. Never serialize this as NotifyCommand.
    public sealed record NotificationSubmission
    {
        public required string PartitionKey { get; init; }
        public required string RowKey { get; init; }
        public required string Fingerprint { get; init; }
        public required string PayloadName { get; init; }
        public string? ETag { get; init; }
        public int Version { get; init; } = 1;
        public string State { get; init; } = "pending";
        public DateTimeOffset? FirstSubmission { get; init; }
        public DateTimeOffset? NextAttempt { get; init; }
        public int Attempts { get; init; }
        public string? ProviderID { get; init; }
        public string? Outcome { get; init; }
    }

    public sealed record FrozenNotification(string Json, string SenderDomain, string CredentialFingerprint);

    public sealed record SubmissionResult(string Outcome, string? ProviderID = null)
    {
        public const string Accepted = "accepted";
        public const string Retry = "retry";
        public const string Rejected = "rejected";
        public const string Hold = "hold";
    }

    public interface INotificationSubmissionStore
    {
        Task<NotificationSubmission?> ReadAsync(string partition, string id, CancellationToken cancellationToken);
        Task<bool> CreateAsync(NotificationSubmission submission, FrozenNotification? payload, CancellationToken cancellationToken);
        Task<NotificationSubmission?> UpdateAsync(NotificationSubmission submission, CancellationToken cancellationToken);
        Task<FrozenNotification> ReadPayloadAsync(string name, CancellationToken cancellationToken);
    }

    public interface IIdempotentNotificationProvider
    {
        FrozenNotification Freeze(TemplateDomain.Deliverable content);
        Task<SubmissionResult> SubmitAsync(FrozenNotification content, string key, DateTimeOffset deadline, CancellationToken cancellationToken);
    }

    public sealed class NotificationDeliveryOptions
    {
        // Activation requires a reviewed cutover for messages previously sent without inbox records.
        public bool Enabled { get; set; }
    }
}
