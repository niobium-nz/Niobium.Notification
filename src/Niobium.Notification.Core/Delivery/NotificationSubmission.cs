namespace Niobium.Notification.Delivery
{
    public enum SubmissionState { Pending = 1, Sending = 2, Accepted = 3, Failed = 4, Held = 5 }

    public enum SubmissionOutcome
    {
        None = 0, TemplateMissing = 1, SubmissionUnknown = 2, RetryWindowExhausted = 3,
        Accepted = 4, Retry = 5, Rejected = 6, Hold = 7,
    }

    // Persistence snapshot owned by NotificationSubmissionDomain. Integer columns follow
    // the pinned mapper's supported scalar types; all decisions use the enums above.
    public sealed record NotificationSubmission
    {
        public const int CurrentVersion = 2;

        [EntityKey(EntityKeyKind.PartitionKey)]
        public string PartitionKey { get; init; } = String.Empty;
        [EntityKey(EntityKeyKind.RowKey)]
        public string RowKey { get; init; } = String.Empty;
        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; init; }
        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; init; }
        public int Version { get; init; }
        public string Fingerprint { get; init; } = String.Empty;
        public string PayloadName { get; init; } = String.Empty;
        public string WriteID { get; init; } = String.Empty;
        public int StateCode { get; init; }
        public int OutcomeCode { get; init; }
        public DateTimeOffset? FirstSubmission { get; init; }
        public DateTimeOffset? NextAttempt { get; init; }
        public int Attempts { get; init; }
        public string? ProviderID { get; init; }
    }

    public sealed record FrozenNotification(string Json, string SenderDomain, string CredentialFingerprint);
    public sealed record SubmissionResult(SubmissionOutcome Outcome, string? ProviderID = null);
    public readonly record struct SubmissionStatus(SubmissionState State, SubmissionOutcome Outcome);
    public enum SubmissionAction { Suppress, Wait, Submit }

    public sealed record SubmissionAttempt
    {
        internal SubmissionAttempt(SubmissionAction action, SubmissionStatus status, DateTimeOffset? due = null,
            DateTimeOffset? deadline = null, FrozenNotification? payload = null, NotificationSubmission? claim = null)
        {
            Action = action; Status = status; Due = due; Deadline = deadline; Payload = payload; Claim = claim;
        }

        public SubmissionAction Action { get; }
        public SubmissionStatus Status { get; }
        public DateTimeOffset? Due { get; }
        public DateTimeOffset? Deadline { get; }
        public FrozenNotification? Payload { get; }
        internal NotificationSubmission? Claim { get; }
    }

    public interface IIdempotentNotificationProvider
    {
        FrozenNotification Freeze(TemplateDomain.Deliverable content);
        Task<SubmissionResult> SubmitAsync(FrozenNotification content, string key, DateTimeOffset deadline, CancellationToken cancellationToken);
    }
}
