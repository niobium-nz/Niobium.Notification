using System.Text.Json;
using Niobium.File;

namespace Niobium.Notification.Delivery
{
    /// <summary>Owns registration, claims, retry eligibility and provider outcomes for one operation.</summary>
    public sealed class NotificationSubmissionDomain(
        Lazy<IRepository<NotificationSubmission>> repository,
        IEnumerable<IDomainEventHandler<IDomain<NotificationSubmission>>> eventHandlers,
        IFileService files) : GenericDomain<NotificationSubmission>(repository, eventHandlers)
    {
        public const string PayloadContainer = "notification-submissions";

        public async Task<bool> IsRegisteredAsync(CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            NotificationSubmission? entry = await this.TryGetEntityAsync(cancellationToken);
            return entry != null;
        }

        public async Task RegisterAsync(string fingerprint, FrozenNotification? payload, CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            if (String.IsNullOrWhiteSpace(fingerprint))
            {
                throw new ApplicationException(InternalError.BadRequest, "A notification fingerprint is required.");
            }
            NotificationSubmission? existing = await this.TryGetEntityAsync(cancellationToken);
            if (existing != null)
            {
                Validate(existing, fingerprint);
                return;
            }

            NotificationSubmission candidate = new()
            {
                PartitionKey = this.PartitionKey, RowKey = this.RowKey,
                Version = NotificationSubmission.CurrentVersion, Fingerprint = fingerprint,
                PayloadName = $"{this.PartitionKey}/{this.RowKey}/{Guid.NewGuid():N}.json",
                StateCode = (int)(payload == null ? SubmissionState.Failed : SubmissionState.Pending),
                OutcomeCode = (int)(payload == null ? SubmissionOutcome.TemplateMissing : SubmissionOutcome.None),
            };
            if (payload != null)
            {
                using MemoryStream content = new(JsonSerializer.SerializeToUtf8Bytes(payload));
                await files.PutAsync(PayloadContainer, candidate.PayloadName, content, replaceIfExist: false, cancellationToken: cancellationToken);
            }
            // A losing/uncertain insert can leave an orphan blob. Never delete it without
            // reconciling references. A conflict propagates; redelivery loads the winner.
            await this.SaveVerifiedAsync(candidate, isNew: true, cancellationToken);
        }

        public async Task<SubmissionAttempt> PrepareAttemptAsync(string fingerprint, DateTimeOffset now,
            NotificationRetryPolicy retryPolicy, CancellationToken cancellationToken = default)
        {
            NotificationSubmission entry = await this.GetEntityAsync(cancellationToken);
            Validate(entry, fingerprint);
            SubmissionState state = (SubmissionState)entry.StateCode;
            if (state is SubmissionState.Accepted or SubmissionState.Failed or SubmissionState.Held)
            {
                return new(SubmissionAction.Suppress);
            }

            DateTimeOffset deadline = (entry.FirstSubmission ?? now) + NotificationRetryPolicy.ProviderWindow;
            if (now + NotificationRetryPolicy.SendMargin >= deadline || entry.Attempts >= NotificationRetryPolicy.MaximumAttempts)
            {
                await this.SaveVerifiedAsync(entry with
                {
                    StateCode = (int)SubmissionState.Held,
                    OutcomeCode = (int)SubmissionOutcome.RetryWindowExhausted, NextAttempt = null,
                }, isNew: false, cancellationToken);
                return new(SubmissionAction.Suppress);
            }
            if (entry.NextAttempt > now)
            {
                return new(SubmissionAction.Wait, entry.NextAttempt);
            }

            using Stream content = await files.GetAsync(PayloadContainer, entry.PayloadName, createIfNotExist: false, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is unavailable.");
            FrozenNotification payload = await JsonSerializer.DeserializeAsync<FrozenNotification>(content, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is invalid.");
            NotificationSubmission claim = await this.SaveVerifiedAsync(entry with
            {
                StateCode = (int)SubmissionState.Sending,
                FirstSubmission = entry.FirstSubmission ?? now, Attempts = entry.Attempts + 1,
                NextAttempt = now + retryPolicy.Delay(entry.Attempts + 1),
                OutcomeCode = (int)SubmissionOutcome.SubmissionUnknown,
            }, isNew: false, cancellationToken);
            return new(SubmissionAction.Submit, claim.NextAttempt, deadline, payload, claim);
        }

        public async Task RecordOutcomeAsync(SubmissionAttempt attempt, SubmissionResult result, CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            NotificationSubmission claim = attempt.Claim ?? throw new InvalidOperationException("A claimed attempt is required.");
            if (claim.PartitionKey != this.PartitionKey || claim.RowKey != this.RowKey)
            {
                throw Conflict("The claim belongs to another notification.");
            }
            SubmissionState state = result.Outcome switch
            {
                SubmissionOutcome.Accepted when !String.IsNullOrWhiteSpace(result.ProviderID) => SubmissionState.Accepted,
                SubmissionOutcome.Retry => SubmissionState.Pending,
                SubmissionOutcome.Rejected when claim.Attempts == 1 => SubmissionState.Failed,
                _ => SubmissionState.Held,
            };
            SubmissionOutcome outcome = result.Outcome is SubmissionOutcome.Accepted or SubmissionOutcome.Retry or SubmissionOutcome.Rejected or SubmissionOutcome.Hold
                ? result.Outcome : SubmissionOutcome.Hold;
            if (result.Outcome == SubmissionOutcome.Accepted && String.IsNullOrWhiteSpace(result.ProviderID))
            {
                outcome = SubmissionOutcome.SubmissionUnknown;
            }
            await this.SaveVerifiedAsync(claim with
            {
                StateCode = (int)state, OutcomeCode = (int)outcome, ProviderID = result.ProviderID,
                NextAttempt = state == SubmissionState.Pending ? claim.NextAttempt : null,
            }, isNew: false, cancellationToken);
        }

        private async Task<NotificationSubmission> SaveVerifiedAsync(NotificationSubmission next, bool isNew, CancellationToken cancellationToken)
        {
            if (!isNew && (String.IsNullOrWhiteSpace(next.ETag) || next.ETag == "*"))
            {
                throw Conflict("A conditional notification version is required.");
            }
            NotificationSubmission expected = next with { WriteID = Guid.NewGuid().ToString("N") };
            await this.SaveAsync([expected], cancellationToken: cancellationToken);
            NotificationSubmission saved = await this.GetEntityAsync(cancellationToken);
            if (String.IsNullOrWhiteSpace(saved.ETag))
            {
                // Pinned 4.0.60 drops write ETags during model conversion. Read-back must
                // prove this write still owns the row, not borrow a competing writer's ETag.
                await this.ReloadAsync(cancellationToken);
                saved = await this.TryGetEntityAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Notification persistence could not be verified.");
            }
            if (String.IsNullOrWhiteSpace(saved.ETag) || saved.ETag == "*"
                || saved != expected with { ETag = saved.ETag, Timestamp = saved.Timestamp })
            {
                throw Conflict("Notification changed while verifying persistence.");
            }
            return saved;
        }

        private static void Validate(NotificationSubmission entry, string fingerprint)
        {
            if (entry.Version != NotificationSubmission.CurrentVersion || entry.Fingerprint != fingerprint
                || !Enum.IsDefined((SubmissionState)entry.StateCode) || !Enum.IsDefined((SubmissionOutcome)entry.OutcomeCode)
                || entry.Attempts < 0 || (entry.Attempts > 0 && entry.FirstSubmission == null)
                || (entry.StateCode == (int)SubmissionState.Sending && (entry.Attempts == 0 || entry.NextAttempt == null)))
            {
                throw Conflict("Notification identity, state or version conflicts with the stored operation.");
            }
        }

        private static ApplicationException Conflict(string message) => new(InternalError.Conflict, message);
    }
}
