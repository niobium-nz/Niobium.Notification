using System.Net;
using Azure.Messaging.ServiceBus;
using Cod;
using Cod.File;
using Cod.Messaging.ServiceBus;
using Cod.Platform.Notification.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Niobium.Notification.Functions
{
    public class Welcome(
        IRepository<Template> repo,
        IFileService fileService,
        IEmailNotificationClient sender,
        ILogger<Welcome> logger)
    {
        [Function(nameof(Welcome))]
        public async Task Run(
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

            var urlEncodedEmail = WebUtility.UrlEncode(request.Email);
            var unsubscribeEndpoint = request.GetTenant().Replace("www.", "api.");
            var unsubscribeLink = $"https://{unsubscribeEndpoint}/api/unsubscribe?email={urlEncodedEmail}&tenant={request.GetTenant()}&campaign={request.GetCampaign()}";

            var htmlTemplatePath = $"{request.GetTenant()}/{request.GetCampaign()}.html";
            string htmlTemplate;
            using var stream = await fileService.GetAsync("emailtemplates", htmlTemplatePath, cancellationToken: cancellationToken)
                ?? throw new Cod.ApplicationException(InternalError.InternalServerError, $"Missing email template: {htmlTemplatePath}");
            using var streamReader = new StreamReader(stream);
            htmlTemplate = await streamReader.ReadToEndAsync(cancellationToken: cancellationToken);

            var body = htmlTemplate
                .Replace("{{FIRST_NAME}}", request.FirstName.ToUpperInvariant())
                .Replace("{{LAST_NAME}}", string.IsNullOrWhiteSpace(request.LastName) ? string.Empty : request.LastName.ToUpperInvariant())
                .Replace("{{UNSUBSCRIBE_LINK}}", unsubscribeLink);

            var success = await sender.SendAsync(
                new EmailAddress { Address = template.FromAddress, DisplayName = template.FromDisplayName },
                [request.Email],
                template.Subject,
                body,
                cancellationToken);
            if (!success)
            {
                var error = $"Failed sending email to {request.Email} for {request.GetCampaign()} by {request.GetTenant()}.";
                logger.LogError(error);
                throw new Cod.ApplicationException(InternalError.InternalServerError, internalMessage: error);
            }
        }
    }
}
