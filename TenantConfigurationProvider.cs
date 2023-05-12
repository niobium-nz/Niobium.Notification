using Microsoft.Extensions.Configuration;
using SendGrid.Helpers.Mail;

namespace Niobium.EmailNotification
{
    internal class TenantConfigurationProvider
    {
        private const string KEY_TENANT = "{{TENANT}}";
        private const string CONFIG_KEY_FROM_NAME = "From:Name";
        private const string CONFIG_KEY_FROM_EMAIL = "From:Email";
        private const string CONFIG_KEY_TENANT_PREFIX = "Tenant:";
        private const string CONFIG_KEY_TO_NAME = CONFIG_KEY_TENANT_PREFIX + KEY_TENANT + ":To:Name";
        private const string CONFIG_KEY_TO_EMAIL = CONFIG_KEY_TENANT_PREFIX + KEY_TENANT + ":To:Email";
        private const string CONFIG_KEY_SUBJECT = CONFIG_KEY_TENANT_PREFIX + KEY_TENANT + ":Subject";
        private const string CONFIG_KEY_TEMPLATE = CONFIG_KEY_TENANT_PREFIX + KEY_TENANT + ":Template";

        private readonly IConfiguration configuration;

        public TenantConfigurationProvider(IConfiguration configuration) => this.configuration = configuration;

        public EmailOptions GetOptions(string tenant)
            => String.IsNullOrWhiteSpace(tenant)
                ? throw new ArgumentException($"'{nameof(tenant)}' cannot be null or whitespace.", nameof(tenant))
                : new(
                   new EmailAddress(
                       this.configuration[CONFIG_KEY_FROM_EMAIL] ?? throw new NotSupportedException($"Missing '{CONFIG_KEY_FROM_EMAIL}' configuration."),
                       this.configuration[CONFIG_KEY_FROM_NAME]),
                   new EmailAddress(
                       this.configuration[CONFIG_KEY_TO_EMAIL.Replace(KEY_TENANT, tenant)] ?? throw new NotSupportedException($"Missing '{CONFIG_KEY_TO_EMAIL.Replace(KEY_TENANT, tenant)}' configuration."),
                       this.configuration[CONFIG_KEY_TO_NAME.Replace(KEY_TENANT, tenant)]),
                   this.configuration[CONFIG_KEY_SUBJECT.Replace(KEY_TENANT, tenant)] ?? throw new NotSupportedException($"Missing '{CONFIG_KEY_SUBJECT.Replace(KEY_TENANT, tenant)}' configuration."),
                   this.configuration[CONFIG_KEY_TEMPLATE.Replace(KEY_TENANT, tenant)] ?? throw new NotSupportedException($"Missing '{CONFIG_KEY_TEMPLATE.Replace(KEY_TENANT, tenant)}' configuration.")
            );
    }
}
