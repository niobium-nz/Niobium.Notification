using System.Net;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Niobium.File;

namespace Niobium.Notification
{
    public partial class TemplateDomain(
        Lazy<IRepository<Template>> repository,
        IEnumerable<IDomainEventHandler<IDomain<Template>>> eventHandlers,
        IFileService fileService,
        HtmlEncoder encoder,
        IOptions<NotificationOptions> options,
        ILogger<TemplateDomain> logger)
            : GenericDomain<Template>(repository, eventHandlers)
    {
        public async Task<Deliverable?> BuildAsync(string? destination, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            var entity = await this.TryGetEntityAsync(cancellationToken);
            if (entity == null)
            {
                logger.LogWarning($"Missing email template for {new StorageKey(this.PartitionKey ?? String.Empty, this.RowKey ?? String.Empty)}");
                return null;
            }

            destination ??= entity.FallbackTo;
            _ = destination ?? throw new ApplicationException(InternalError.InternalServerError, $"Destination is required for email notification {entity.Tenant}#{entity.Channel}.");

            if (String.IsNullOrWhiteSpace(entity.Subject))
            {
                throw new ApplicationException(InternalError.InternalServerError, $"Subject is required for email notification {entity.Tenant}#{entity.Channel}.");
            }

            var templatePath = $"{entity.Tenant}/{entity.Blob}";
            using var stream = await fileService.GetAsync(options.Value.TemplateFolder, templatePath, cancellationToken: cancellationToken)
                ?? throw new ApplicationException(InternalError.InternalServerError, $"Missing template: {templatePath}");
            using var streamReader = new StreamReader(stream);
            var body = await streamReader.ReadToEndAsync(cancellationToken: cancellationToken);
            var unsubscribeLink = this.BuildUnsubscribeLink(destination, entity.Tenant, entity.Channel);
            body = body.Replace("{{UNSUBSCRIBE_LINK}}", unsubscribeLink);
            var subject = entity.Subject.Replace("{{UNSUBSCRIBE_LINK}}", unsubscribeLink);
            foreach (var (key, value) in parameters)
            {
                subject = subject.Replace($"{{{{{key.ToUpperInvariant()}}}}}", encoder.Encode(value));
                body = body.Replace($"{{{{{key.ToUpperInvariant()}}}}}", encoder.Encode(value));
            }

            return new Deliverable
            {
                Body = body,
                From = entity.From,
                FromName = entity.FromName,
                Subject = subject,
                To = destination,
            };
        }

        private string BuildUnsubscribeLink(string email, Guid tenant, string channel)
            => $"https://{options.Value.SelfHostName}/unsubscribe?email={WebUtility.UrlEncode(email)}&tenant={WebUtility.UrlEncode(tenant.ToString())}&channel={WebUtility.UrlEncode(channel)}";
    }
}
