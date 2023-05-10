using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    public class EmailSender
    {
        private const string TEMPLATE_NAME = "{{NAME}}";
        private const string TEMPLATE_CONTACT = "{{CONTACT}}";
        private const string TEMPLATE_MESSAGE = "{{MESSAGE}}";

        private readonly ISendGridClient client;
        private readonly ILogger logger;
        private readonly EmailOptions options;

        public EmailSender(IOptions<EmailOptions> emailOptions, ISendGridClient sendGridClient, ILoggerFactory loggerFactory)
        {
            this.client = sendGridClient;
            this.logger = loggerFactory.CreateLogger<EmailSender>();
            this.options = emailOptions.Value;
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

            var subject = GetTenantConfig(tenant, this.options.Subject);
            var to = GetTenantConfig(tenant, this.options.To);
            var content = this.ComposeEmailContent(tenant, message, name, contact);

            var request = new SendGridMessage
            {
                From = this.options.From,
                Subject = subject,
                PlainTextContent = content
            };
            request.AddTo(to);

            var response = await this.client.SendEmailAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"Failed to queue email with status code {response.StatusCode}: {await response.Body.ReadAsStringAsync()}";
                this.logger.LogError(error);
            }

            return response.IsSuccessStatusCode;
        }

        private string ComposeEmailContent(string tenant, string message, string name, string contact)
        {
            var template = GetTenantConfig(tenant, this.options.Template);
            return template.Replace(TEMPLATE_NAME, name ?? string.Empty)
                .Replace(TEMPLATE_CONTACT, contact ?? string.Empty)
                .Replace(TEMPLATE_MESSAGE, message ?? string.Empty);
        }

        private static T GetTenantConfig<T>(string tenant, Dictionary<string, T>? configStore)
            => configStore != null && !configStore.ContainsKey(tenant) ? configStore[tenant] : throw new ArgumentException($"Tenant doesn't get correctly configured: {tenant}");
    }
}
