using Niobium.File;

namespace Niobium.Notification.Delivery
{
    /// <summary>Owns registration, claims, retry eligibility and provider outcomes for one operation.</summary>
    public sealed class NotificationSubmissionDomain(
        Lazy<IRepository<NotificationSubmission>> repository,
        IEnumerable<IDomainEventHandler<IDomain<NotificationSubmission>>> eventHandlers,
        IFileService files) : GenericDomain<NotificationSubmission>(repository, eventHandlers)
    {
        public async Task RegisterAsync(NotificationSubmission request, FrozenNotification? payload, CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            ArgumentNullException.ThrowIfNull(request);
            this.ValidateIdentity(request);
            if (String.IsNullOrWhiteSpace(request.Fingerprint))
            {
                throw new ApplicationException(InternalError.BadRequest, "A notification fingerprint is required.");
            }
            NotificationSubmission? existing = await this.TryGetEntityAsync(cancellationToken);
            if (existing != null)
            {
                Validate(existing, request);
                return;
            }

            NotificationSubmission candidate = request.NewRegistration() with
            {
                StateCode = (int)(payload == null ? SubmissionState.Failed : SubmissionState.Pending),
                OutcomeCode = (int)(payload == null ? SubmissionOutcome.TemplateMissing : SubmissionOutcome.None),
            };
            if (payload != null)
            {
                using MemoryStream content = new(payload.Serialize());
                await files.PutAsync(NotificationSubmission.PayloadContainer, candidate.PayloadName, content, replaceIfExist: false, cancellationToken: cancellationToken);
            }
            // A losing/uncertain insert can leave an orphan blob. Never delete it without
            // reconciling references. A conflict propagates; redelivery loads the winner.
            await this.SaveVerifiedAsync(candidate, isNew: true, cancellationToken);
        }

        /// <summary>Determines whether fresh content is needed, holding unknown prior delivery outcomes.</summary>
        public async Task<bool> PrepareRegistrationAsync(NotificationSubmission request, bool isFirstDelivery,
            CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            ArgumentNullException.ThrowIfNull(request);
            this.ValidateIdentity(request);
            NotificationSubmission? existing = await this.TryGetEntityAsync(cancellationToken);
            if (existing != null)
            {
                Validate(existing, request);
                return false;
            }

            if (isFirstDelivery)
            {
                return true;
            }

            // A legacy consumer may have sent before losing settlement without an
            // inbox row. A create-only hold cannot overwrite another registration.
            // Do not render or store content while that prior outcome is unknown.
            NotificationSubmission held = request.NewRegistration() with
            {
                StateCode = (int)SubmissionState.Held,
                OutcomeCode = (int)SubmissionOutcome.SubmissionUnknown,
            };
            await this.SaveVerifiedAsync(held, isNew: true, cancellationToken);
            return false;
        }

        public async Task<SubmissionAttempt> PrepareAttemptAsync(NotificationSubmission request, DateTimeOffset now,
            NotificationRetryPolicy retryPolicy, CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            ArgumentNullException.ThrowIfNull(request);
            this.ValidateIdentity(request);
            NotificationSubmission entry = await this.GetEntityAsync(cancellationToken);
            Validate(entry, request);
            SubmissionState state = (SubmissionState)entry.StateCode;
            if (state is SubmissionState.Accepted or SubmissionState.Failed or SubmissionState.Held)
            {
                return new(SubmissionAction.Suppress, Status(entry));
            }

            DateTimeOffset deadline = (entry.FirstSubmission ?? now) + NotificationRetryPolicy.ProviderWindow;
            if (now + NotificationRetryPolicy.SendMargin >= deadline || entry.Attempts >= NotificationRetryPolicy.MaximumAttempts)
            {
                NotificationSubmission held = await this.SaveVerifiedAsync(entry with
                {
                    StateCode = (int)SubmissionState.Held,
                    OutcomeCode = (int)SubmissionOutcome.RetryWindowExhausted, NextAttempt = null,
                }, isNew: false, cancellationToken);
                return new(SubmissionAction.Suppress, Status(held));
            }
            if (entry.NextAttempt > now)
            {
                return new(SubmissionAction.Wait, Status(entry), entry.NextAttempt);
            }

            using Stream content = await files.GetAsync(NotificationSubmission.PayloadContainer, entry.PayloadName, createIfNotExist: false, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Frozen notification content is unavailable.");
            FrozenNotification payload = await FrozenNotification.ReadAsync(content, cancellationToken);
            NotificationSubmission claim = await this.SaveVerifiedAsync(entry with
            {
                StateCode = (int)SubmissionState.Sending,
                FirstSubmission = entry.FirstSubmission ?? now, Attempts = entry.Attempts + 1,
                NextAttempt = now + retryPolicy.Delay(entry.Attempts + 1),
                OutcomeCode = (int)SubmissionOutcome.SubmissionUnknown,
            }, isNew: false, cancellationToken);
            return new(SubmissionAction.Submit, Status(claim), claim.NextAttempt, deadline, payload, claim);
        }

        public async Task<SubmissionStatus> RecordOutcomeAsync(SubmissionAttempt attempt, SubmissionResult result, CancellationToken cancellationToken = default)
        {
            this.CheckInitialized();
            NotificationSubmission claim = attempt.Claim ?? throw new InvalidOperationException("A claimed attempt is required.");
            this.ValidateIdentity(claim);
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
            NotificationSubmission saved = await this.SaveVerifiedAsync(claim with
            {
                StateCode = (int)state, OutcomeCode = (int)outcome, ProviderID = result.ProviderID,
                NextAttempt = state == SubmissionState.Pending ? claim.NextAttempt : null,
            }, isNew: false, cancellationToken);
            return Status(saved);
        }

        private static SubmissionStatus Status(NotificationSubmission entry)
            => new((SubmissionState)entry.StateCode, (SubmissionOutcome)entry.OutcomeCode);

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

        private void ValidateIdentity(NotificationSubmission entry)
        {
            if (entry.Tenant != this.PartitionKey || entry.ID != this.RowKey)
            {
                throw Conflict("The submission belongs to another notification.");
            }
        }

        private static void Validate(NotificationSubmission entry, NotificationSubmission request)
        {
            if (!entry.HasSupportedVersion() || !entry.MatchesRequest(request)
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
