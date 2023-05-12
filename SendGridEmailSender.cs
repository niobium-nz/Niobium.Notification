using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    internal class SendGridEmailSender : IEmailSender
    {
        private const string TEMPLATE_NAME = "{{NAME}}";
        private const string TEMPLATE_CONTACT = "{{CONTACT}}";
        private const string TEMPLATE_MESSAGE = "{{MESSAGE}}";

        private readonly TenantConfigurationProvider configuration;
        private readonly ISendGridClient client;
        private readonly ILogger logger;

        public SendGridEmailSender(TenantConfigurationProvider configuration, ISendGridClient sendGridClient, ILoggerFactory loggerFactory)
        {
            this.configuration = configuration;
            this.client = sendGridClient;
            this.logger = loggerFactory.CreateLogger<SendGridEmailSender>();
        }

        public async Task<bool> SendEmailAsync(string tenant, string message, string name, string contact)
        {
            if (String.IsNullOrWhiteSpace(tenant))
            {
                throw new ArgumentException($"'{nameof(tenant)}' cannot be null or whitespace.", nameof(tenant));
            }

            if (String.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException($"'{nameof(message)}' cannot be null or whitespace.", nameof(message));
            }

            var options = this.configuration.GetOptions(tenant);
            var content = ComposeEmailContent(options.Template, message, name, contact);

            var request = new SendGridMessage
            {
                From = options.From,
                Subject = options.Subject,
                PlainTextContent = content
            };
            request.AddTo(options.To);

            var response = await this.client.SendEmailAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"Failed to queue email with status code {response.StatusCode}: {await response.Body.ReadAsStringAsync()}";
                this.logger.LogError(error);
            }

            return response.IsSuccessStatusCode;
        }

        private static string ComposeEmailContent(string template, string message, string name, string contact)
            => template.Replace(TEMPLATE_NAME, name ?? String.Empty)
                .Replace(TEMPLATE_CONTACT, contact ?? String.Empty)
                .Replace(TEMPLATE_MESSAGE, message ?? String.Empty);
    }
}
