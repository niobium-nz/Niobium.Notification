using Microsoft.Extensions.Logging;
using Niobium.Platform.Notification.Email;

namespace Niobium.Notification
{
    public class NotificationFlow(
        IDomainRepository<TemplateDomain, Template> repo,
        IEmailNotificationClient sender,
        ILogger<NotificationFlow> logger) : IFlow
    {
        public async Task RunAsync(NotifyCommand request, CancellationToken cancellationToken = default)
        {
            var domain = await repo.GetAsync(
            Template.BuildParitionKey(request.Tenant),
            Template.BuildRowKey(request.Channel),
            cancellationToken: cancellationToken);

            var deliverable = await domain.BuildAsync(request.Destination, request.Parameters, cancellationToken);

            if (deliverable == null)
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(deliverable.Subject))
            {
                throw new ApplicationException(InternalError.InternalServerError, "Subject is required for email notification.");
            }

            var success = await sender.SendAsync(
                new EmailAddress { Address = deliverable.From, DisplayName = deliverable.FromName },
                [new EmailAddress { Address = deliverable.To, DisplayName = deliverable.ToName }],
                deliverable.Subject,
                deliverable.Body,
                cancellationToken);
            if (!success)
            {
                var error = $"Failed sending email to {deliverable.To} for {request.Channel} by {request.Channel}.";
                logger.LogError(error);
                throw new ApplicationException(InternalError.InternalServerError, internalMessage: error);
            }
        }
    }
}
