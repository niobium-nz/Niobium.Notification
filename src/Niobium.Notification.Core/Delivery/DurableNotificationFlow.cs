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
        public async Task RunAsync(NotifyCommand request, bool isFirstDelivery, CancellationToken cancellationToken = default)
        {
            NotificationSubmission identity = NotificationSubmission.Create(request);
            NotificationSubmissionDomain submission = await submissions.GetAsync(identity.Tenant, identity.ID, forceLoad: true, cancellationToken: cancellationToken);
            bool registrationRequired = await submission.PrepareRegistrationAsync(identity, isFirstDelivery, cancellationToken);
            if (registrationRequired)
            {
                TemplateDomain.Deliverable? content = await renderer.PrepareAsync(request, cancellationToken);
                FrozenNotification? payload = content == null ? null : provider.Freeze(content, identity.GetReceiptCorrelation());
                await submission.RegisterAsync(identity, payload, cancellationToken);
            }

            SubmissionAttempt attempt = await submission.PrepareAttemptAsync(identity, clock.GetUtcNow(), retryPolicy, cancellationToken);
            NotificationTelemetry.RecordDecision(attempt);
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
            SubmissionResult result = await NotificationTelemetry.MeasureDurableAsync(
                () => provider.SubmitAsync(attempt.Payload!, identity.GetProviderIdempotencyKey(), attempt.Deadline!.Value, cancellationToken), cancellationToken);
            SubmissionStatus outcome = await submission.RecordOutcomeAsync(attempt, result, cancellationToken);
            NotificationTelemetry.RecordOutcome(outcome);
        }
    }
}
