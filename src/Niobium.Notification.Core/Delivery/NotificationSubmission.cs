using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Niobium.Notification.Delivery
{
    public enum SubmissionState { Pending = 1, Sending = 2, Accepted = 3, Failed = 4, Held = 5 }

    public enum SubmissionOutcome
    {
        None = 0, TemplateMissing = 1, SubmissionUnknown = 2, RetryWindowExhausted = 3,
        Accepted = 4, Retry = 5, Rejected = 6, Hold = 7,
    }

    // Owns the persisted representation. NotificationSubmissionDomain owns lifecycle
    // decisions; the mapper translates the business key names into Table columns.
    public sealed record NotificationSubmission
    {
        public const int CurrentVersion = 2;
        public const string PayloadContainer = "notification-submissions";

        [EntityKey(EntityKeyKind.PartitionKey)]
        public string Tenant { get; private init; } = String.Empty;
        [EntityKey(EntityKeyKind.RowKey)]
        public string ID { get; private init; } = String.Empty;
        [EntityKey(EntityKeyKind.ETag)]
        public string? ETag { get; init; }
        [EntityKey(EntityKeyKind.Timestamp)]
        public DateTimeOffset? Timestamp { get; init; }
        public int Version { get; private init; }
        public string Fingerprint { get; private init; } = String.Empty;
        public string PayloadName { get; private init; } = String.Empty;
        public string WriteID { get; init; } = String.Empty;
        public int StateCode { get; init; }
        public int OutcomeCode { get; init; }
        public DateTimeOffset? FirstSubmission { get; init; }
        public DateTimeOffset? NextAttempt { get; init; }
        public int Attempts { get; init; }
        public string? ProviderID { get; init; }

        /// <summary>Captures a request's stable identity and data contract without persisting it.</summary>
        public static NotificationSubmission Create(NotifyCommand request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (String.IsNullOrWhiteSpace(request.ID) || request.Tenant == Guid.Empty)
            {
                throw new ApplicationException(InternalError.BadRequest, "A stable notification identity is required.");
            }
            string tenant = request.Tenant.ToString("N");
            string id = Hash(request.ID);
            return Create(tenant, id, FingerprintOf(request));
        }

        // A new registration gets a new reference and no persisted lifecycle/version
        // metadata from an input snapshot. The domain chooses its initial outcome.
        internal NotificationSubmission NewRegistration() => Create(this.Tenant, this.ID, this.Fingerprint);

        private static NotificationSubmission Create(string tenant, string id, string fingerprint)
            => new()
            {
                Tenant = tenant, ID = id, Version = CurrentVersion,
                Fingerprint = fingerprint, PayloadName = CreatePayloadName(tenant, id),
            };

        public bool MatchesRequest(NotificationSubmission request)
            => this.Tenant == request.Tenant && this.ID == request.ID && this.Fingerprint == request.Fingerprint;

        // Methods, rather than computed public properties, keep derived values out
        // of the framework's public-property Table mapping.
        public string GetProviderIdempotencyKey() => $"notification-v1/{this.Tenant}/{this.ID}";

        private static string CreatePayloadName(string tenant, string id) => $"{tenant}/{id}/{Guid.NewGuid():N}.json";

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private static string FingerprintOf(NotifyCommand command)
        {
            JsonElement content = JsonSerializer.SerializeToElement(new
            {
                command.Tenant, command.Channel, command.Destination, command.DestinationDisplayName, command.Parameters,
            });
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                WriteCanonical(writer, content);
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
            }
            else
            {
                element.WriteTo(writer);
            }
        }
    }

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
