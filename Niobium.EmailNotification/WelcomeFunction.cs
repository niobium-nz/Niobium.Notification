using Azure.Messaging.ServiceBus;
using Cod;
using Cod.Messaging.ServiceBus;
using Cod.Platform.Notification.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.EmailNotification
{
    public class WelcomeFunction(
        IRepository<Template> repo,
        IEmailNotificationClient sender,
        ILogger<WelcomeFunction> logger)
    {
        [Function(nameof(Welcome))]
        public async Task Welcome(
            [ServiceBusTrigger("subscription", AutoCompleteMessages = true, Connection = nameof(ServiceBusOptions))]
            ServiceBusReceivedMessage message,
            CancellationToken cancellationToken)
        {
            if (!message.TryParse(out Subscription? request, out var rawBody))
            {
                logger.LogWarning($"Invalid request: {rawBody}");
                return;
            }

            var template = await repo.RetrieveAsync(
                Template.BuildParitionKey(request.GetTenant()),
                Template.BuildRowKey(request.GetCampaign()),
                cancellationToken: cancellationToken);
            ArgumentNullException.ThrowIfNull(template);

            var body = template.HTML
                .Replace("{{FIRST_NAME}}", request.FirstName)
                .Replace("{{LAST_NAME}}", request.LastName)
                .Replace("{{TENANT}}", request.GetTenant());

            var success = await sender.SendAsync(
                template.From,
                [request.Email],
                template.Subject,
                body,
                cancellationToken);
            if (!success)
            {
                var error = $"Failed sending email to {template.HTML} for {request.GetCampaign()} by {request.GetTenant()}.";
                logger.LogError(error);
                throw new Cod.ApplicationException(InternalError.InternalServerError, internalMessage: error);
            }
        }
    }
}
