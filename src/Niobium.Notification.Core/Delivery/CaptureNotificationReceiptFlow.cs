using Niobium.Messaging;

namespace Niobium.Notification.Delivery;

public sealed class CaptureNotificationReceiptFlow(
    IDomainRepository<NotificationReceiptDomain, NotificationReceipt> receipts,
    IDomainRepository<NotificationSubmissionDomain, NotificationSubmission> submissions,
    IMessagingBroker<NotificationDeliveryEvent> events,
    TimeProvider clock) : IFlow
{
    public async Task RunAsync(CaptureNotificationReceiptCommand command, CancellationToken cancellationToken = default)
    {
        NotificationReceiptDomain.Validate(command);
        NotificationReceipt identity = NotificationReceipt.Create(command);
        NotificationReceiptDomain receipt = await receipts.GetAsync(identity.Provider, identity.ID, forceLoad: true, cancellationToken: cancellationToken);
        await receipt.RegisterAsync(command, cancellationToken);
        bool pending = await receipt.NeedsPublicationAsync(cancellationToken);
        if (!pending) return;
        NotificationReceipt stored = await receipt.GetEntityAsync(cancellationToken);
        NotificationSubmission? submission = null;
        NotificationCorrelation? correlation = stored.GetCorrelation();
        if (correlation != null)
        {
            NotificationSubmissionDomain domain = await submissions.GetAsync(correlation.Tenant, correlation.SubmissionID,
                forceLoad: true, cancellationToken: cancellationToken);
            submission = await domain.TryGetEntityAsync(cancellationToken);
        }
        NotificationDeliveryEvent? delivery = await receipt.PreparePublicationAsync(submission, cancellationToken);
        if (delivery == null) return;
        // The provider webhook was durably queued before HTTP acknowledgement.
        // A failure here leaves that broker command available for redelivery.
        // Lost publication acknowledgements may repeat this stable event ID;
        // consumers must deduplicate it. There is no cross-service transaction.
        await events.EnqueueAsync(new MessagingEntry<NotificationDeliveryEvent> { ID = delivery.ID, Value = delivery }, cancellationToken);
        await receipt.MarkPublishedAsync(clock.GetUtcNow(), cancellationToken);
    }
}
