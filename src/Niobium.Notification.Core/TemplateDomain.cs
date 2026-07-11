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
        public async Task<Deliverable?> BuildAsync(string? destination, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken = default)
        {
            Template? entity = await this.TryGetEntityAsync(cancellationToken);
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

            string templatePath = $"{entity.Tenant}/{entity.Blob}";
            using Stream stream = await fileService.GetAsync(options.Value.TemplateFolder, templatePath, cancellationToken: cancellationToken)
                ?? throw new ApplicationException(InternalError.InternalServerError, $"Missing template: {templatePath}");
            using StreamReader streamReader = new(stream);
            string body = await streamReader.ReadToEndAsync(cancellationToken: cancellationToken);
            string unsubscribeLink = this.BuildUnsubscribeLink(destination, entity.Tenant, entity.Channel);
            body = body.Replace("{{UNSUBSCRIBE_LINK}}", unsubscribeLink);
            string subject = entity.Subject.Replace("{{UNSUBSCRIBE_LINK}}", unsubscribeLink);
            foreach ((string? key, object? value) in parameters)
            {
                if (value is IEnumerable<Dictionary<string, string>> values)
                {
                    string? section = ExtractRepeatableSection(body, key, out int startIndex, out int endIndex);
                    List<string> repeatedSections = [];
                    if (section != null)
                    {
                        foreach (Dictionary<string, string> dic in values)
                        {
                            string newSection = section;
                            foreach ((string? subKey, string? subValue) in dic)
                            {
                                newSection = newSection.Replace($"{{{{{subKey.ToUpperInvariant()}}}}}", encoder.Encode(subValue));
                            }
                            repeatedSections.Add(newSection);
                        }
                    }

                    if (startIndex > 0)
                    {
                        body = $"{body[..startIndex]}{String.Join(Environment.NewLine, repeatedSections)}{body[endIndex..]}";
                    }
                }
                else
                {
                    string strValue = value?.ToString() ?? String.Empty;
                    subject = subject.Replace($"{{{{{key.ToUpperInvariant()}}}}}", encoder.Encode(strValue));
                    body = body.Replace($"{{{{{key.ToUpperInvariant()}}}}}", encoder.Encode(strValue));
                }
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

        private static string? ExtractRepeatableSection(string body, string sectionName, out int startIndex, out int endIndex)
        {
            sectionName = sectionName.ToUpperInvariant();
            string startTag = $"<!-- {sectionName} BEGIN -->";
            string endTag = $"<!-- {sectionName} END -->";
            startIndex = body.IndexOf(startTag);
            endIndex = body.IndexOf(endTag);
            if (startIndex == -1 || endIndex == -1 || endIndex <= startIndex)
            {
                return null;
            }
            startIndex += startTag.Length;
            return body[startIndex..endIndex].Trim();
        }

        private string BuildUnsubscribeLink(string email, Guid tenant, string channel)
            => $"https://{options.Value.SelfHostName}/unsubscribe?email={WebUtility.UrlEncode(email)}&tenant={WebUtility.UrlEncode(tenant.ToString())}&channel={WebUtility.UrlEncode(channel)}";
    }
}
