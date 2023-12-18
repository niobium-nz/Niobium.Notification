using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    internal class SendGridEmailSender(
        TenantConfigurationProvider configuration,
        ISendGridClient sendGridClient,
        HtmlEncoder encoder,
        ILoggerFactory loggerFactory)
        : IEmailSender
    {
        private const string TEMPLATE_NAME = "{{NAME}}";
        private const string TEMPLATE_CONTACT = "{{CONTACT}}";
        private const string TEMPLATE_MESSAGE = "{{MESSAGE}}";
        private readonly ILogger logger = loggerFactory.CreateLogger<SendGridEmailSender>();

        public async Task<bool> SendEmailAsync(string tenant, string message, string? name, string? contact, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(message);

            message = encoder.Encode(message);

            if (name != null)
            {
                name = encoder.Encode(name);
            }

            if (contact != null)
            {
                contact = encoder.Encode(contact);
            }

            var options = configuration.GetOptions(tenant);
            var content = ComposeEmailContent(options.Template, message, name, contact);

            var request = new SendGridMessage
            {
                From = options.From,
                Subject = options.Subject,
                PlainTextContent = content
            };
            request.AddTo(options.To);

            var response = await sendGridClient.SendEmailAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"Failed to queue email with status code {response.StatusCode}: {await response.Body.ReadAsStringAsync()}";
                this.logger.LogError(error);
            }

            return response.IsSuccessStatusCode;
        }

        private static string ComposeEmailContent(string template, string message, string? name, string? contact)
            => template.Replace(TEMPLATE_NAME, name ?? String.Empty)
                .Replace(TEMPLATE_CONTACT, contact ?? String.Empty)
                .Replace(TEMPLATE_MESSAGE, message);
    }
}
