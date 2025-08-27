using System.Net;
using Niobium;
using Niobium.File;
using Niobium.Platform.Notification.Email;
using Microsoft.Extensions.Logging;

namespace Niobium.Notification
{
    internal class GreetingsInitiator(
        IRepository<Template> repo,
        IFileService fileService,
        IEmailNotificationClient sender,
        ILogger<GreetingsInitiator> logger)
        : DomainEventHandler<SubscriptionDomain, SubscribedEvent>
    {
        protected override DomainEventAudience EventSource => DomainEventAudience.External;

        public async override Task HandleCoreAsync(SubscribedEvent e, CancellationToken cancellationToken)
        {
            var request = e.Subscription;
            var template = await repo.RetrieveAsync(
                Template.BuildParitionKey(request.GetTenant()),
                Template.BuildRowKey(request.GetCampaign()),
                cancellationToken: cancellationToken);
            if (template == null)
            {
                logger.LogWarning($"Missing email template for {request.GetCampaign()} by {request.GetTenant()}");
                return;
            }

            var urlEncodedEmail = WebUtility.UrlEncode(request.Email);
            var unsubscribeEndpoint = request.GetTenant().Replace("www.", "api.");
            var unsubscribeLink = $"https://{unsubscribeEndpoint}/api/unsubscribe?email={urlEncodedEmail}&tenant={request.GetTenant()}&campaign={request.GetCampaign()}";

            var htmlTemplatePath = $"{request.GetTenant()}/{request.GetCampaign()}.html";
            string htmlTemplate;
            using var stream = await fileService.GetAsync("emailtemplates", htmlTemplatePath, cancellationToken: cancellationToken)
                ?? throw new ApplicationException(InternalError.InternalServerError, $"Missing email template: {htmlTemplatePath}");
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
                throw new ApplicationException(InternalError.InternalServerError, internalMessage: error);
            }
        }
    }
}
