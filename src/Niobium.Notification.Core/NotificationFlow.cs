using Microsoft.Extensions.Logging;
using Niobium.Platform.Notification.Email;
using Niobium.Notification.Delivery;

namespace Niobium.Notification
{
    public class NotificationFlow(
        IDomainRepository<TemplateDomain, Template> repo,
        IEmailNotificationClient sender,
        ILogger<NotificationFlow> logger) : IFlow
    {
        public async Task<TemplateDomain.Deliverable?> PrepareAsync(NotifyCommand request, CancellationToken cancellationToken = default)
        {
            TemplateDomain domain = await repo.GetAsync(
            Template.BuildParitionKey(request.Tenant),
            Template.BuildRowKey(request.Channel),
            cancellationToken: cancellationToken);

            TemplateDomain.Deliverable? deliverable = await domain.BuildAsync(request.Destination, request.Parameters, cancellationToken);

            if (deliverable == null)
            {
                return null;
            }

            if (String.IsNullOrWhiteSpace(deliverable.Subject))
            {
                throw new ApplicationException(InternalError.InternalServerError, "Subject is required for email notification.");
            }

            return deliverable;
        }

        public async Task RunAsync(NotifyCommand request, CancellationToken cancellationToken = default)
        {
            TemplateDomain.Deliverable? deliverable = await this.PrepareAsync(request, cancellationToken);
            if (deliverable == null)
            {
                return;
            }

            bool success = await NotificationTelemetry.MeasureLegacyAsync(() => sender.SendAsync(
                new EmailAddress { Address = deliverable.From, DisplayName = deliverable.FromName },
                [new EmailAddress { Address = deliverable.To, DisplayName = deliverable.ToName }],
                deliverable.Subject!,
                deliverable.Body,
                cancellationToken), cancellationToken);
            if (!success)
            {
                string error = $"Failed sending email to {deliverable.To} for {request.Channel} by {request.Channel}.";
                logger.LogError(error);
                throw new ApplicationException(InternalError.InternalServerError, internalMessage: error);
            }
        }
    }
}
