using Niobium.Messaging;

namespace Niobium.Notification.Delivery
{
    public sealed class DurableNotificationFlow(
        NotificationFlow renderer,
        IDomainRepository<NotificationSubmissionDomain, NotificationSubmission> submissions,
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
            NotificationSubmissionDomain submission = await submissions.GetAsync(partition, id, forceLoad: true, cancellationToken: cancellationToken);
            bool registered = await submission.IsRegisteredAsync(cancellationToken);
            if (!registered)
            {
                TemplateDomain.Deliverable? content = await renderer.PrepareAsync(request, cancellationToken);
                FrozenNotification? payload = content == null ? null : provider.Freeze(content);
                await submission.RegisterAsync(fingerprint, payload, cancellationToken);
            }

            SubmissionAttempt attempt = await submission.PrepareAttemptAsync(fingerprint, clock.GetUtcNow(), retryPolicy, cancellationToken);
            if (attempt.Action == SubmissionAction.Suppress)
            {
                return;
            }
            await queue.EnqueueAsync([new MessagingEntry<NotifyCommand> { Value = request, ID = request.ID, Schedule = attempt.Due }], cancellationToken);
            if (attempt.Action == SubmissionAction.Wait)
            {
                return;
            }
            // The recovery wake must be accepted before an irreversible provider submission.
            SubmissionResult result = await provider.SubmitAsync(attempt.Payload!, $"notification-v1/{partition}/{id}", attempt.Deadline!.Value, cancellationToken);
            await submission.RecordOutcomeAsync(attempt, result, cancellationToken);
        }
    }
}
