using Niobium.Messaging;

namespace Niobium.Notification.Delivery
{
    public sealed class DurableNotificationFlow(
        NotificationFlow renderer,
        INotificationSubmissionStore store,
        IIdempotentNotificationProvider provider,
        IMessagingBroker<NotifyCommand> queue,
        TimeProvider clock,
        NotificationRetryPolicy retryPolicy) : IFlow
    {
        public async Task RunAsync(NotifyCommand request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (String.IsNullOrWhiteSpace(request.ID) || request.Tenant == Guid.Empty)
            {
                throw new ApplicationException(InternalError.BadRequest, "A stable notification identity is required.");
            }

            string partition = request.Tenant.ToString("N");
            string id = NotificationFingerprint.Hash(request.ID);
            string fingerprint = NotificationFingerprint.Of(request);
            NotificationSubmission? entry = await store.ReadAsync(partition, id, cancellationToken);
            if (entry == null)
            {
                TemplateDomain.Deliverable? content = await renderer.PrepareAsync(request, cancellationToken);
                FrozenNotification? payload = content == null ? null : provider.Freeze(content);
                NotificationSubmission candidate = new()
                {
                    PartitionKey = partition,
                    RowKey = id,
                    Fingerprint = fingerprint,
                    PayloadName = $"{partition}/{id}/{Guid.NewGuid():N}.json",
                    State = payload == null ? "failed" : "pending",
                    Outcome = payload == null ? "template_missing" : null,
                };
                _ = await store.CreateAsync(candidate, payload, cancellationToken);
                // Read the winning insert, including its ETag and frozen payload pointer.
                entry = await store.ReadAsync(partition, id, cancellationToken)
                    ?? throw new InvalidOperationException("Notification registration could not be verified.");
            }

            if (entry.Version != 1 || entry.Fingerprint != fingerprint)
            {
                throw new ApplicationException(InternalError.Conflict, "Notification identity or version conflicts with the stored operation.");
            }
            if (entry.State is "accepted" or "failed" or "held")
            {
                return;
            }
            if (entry.State is not ("pending" or "sending"))
            {
                throw new ApplicationException(InternalError.Conflict, "Unknown notification state.");
            }

            DateTimeOffset now = clock.GetUtcNow();
            DateTimeOffset deadline = (entry.FirstSubmission ?? now) + NotificationRetryPolicy.ProviderWindow;
            if (now + NotificationRetryPolicy.SendMargin >= deadline || entry.Attempts >= NotificationRetryPolicy.MaximumAttempts)
            {
                if (await store.UpdateAsync(entry with { State = "held", Outcome = "retry_window_exhausted", NextAttempt = null }, cancellationToken) == null)
                {
                    throw new ApplicationException(InternalError.Conflict, "Notification hold requires reconciliation.");
                }
                return;
            }
            if (entry.NextAttempt > now)
            {
                await this.ScheduleAsync(request, entry.NextAttempt.Value, cancellationToken);
                return;
            }

            FrozenNotification frozen = await store.ReadPayloadAsync(entry.PayloadName, cancellationToken);
            NotificationSubmission? claim = await store.UpdateAsync(entry with
            {
                State = "sending",
                FirstSubmission = entry.FirstSubmission ?? now,
                Attempts = entry.Attempts + 1,
                NextAttempt = now + retryPolicy.Delay(entry.Attempts + 1),
                Outcome = "submission_unknown",
            }, cancellationToken);
            if (claim == null)
            {
                // Another message won the ETag. Redelivery can reconcile its outcome.
                throw new ApplicationException(InternalError.Conflict, "Notification submission is already claimed.");
            }

            // Persist the schedule and enqueue a recovery wake before the irreversible provider call.
            // A failed schedule propagates, leaving the source message available for redelivery.
            await this.ScheduleAsync(request, claim.NextAttempt!.Value, cancellationToken);
            SubmissionResult result = await provider.SubmitAsync(frozen, $"notification-v1/{partition}/{id}", deadline, cancellationToken);
            string state = result.Outcome switch
            {
                SubmissionResult.Accepted => "accepted",
                SubmissionResult.Retry => "pending",
                SubmissionResult.Rejected when claim.Attempts == 1 => "failed",
                _ => "held",
            };
            NotificationSubmission? completed = await store.UpdateAsync(claim with
            {
                State = state,
                ProviderID = result.ProviderID,
                Outcome = result.Outcome,
                NextAttempt = state == "pending" ? claim.NextAttempt : null,
            }, cancellationToken);
            if (completed == null)
            {
                throw new ApplicationException(InternalError.Conflict, "Notification outcome requires reconciliation.");
            }
        }

        private Task ScheduleAsync(NotifyCommand request, DateTimeOffset due, CancellationToken cancellationToken)
            => queue.EnqueueAsync([new MessagingEntry<NotifyCommand> { Value = request, ID = request.ID, Schedule = due }], cancellationToken);
    }
}
