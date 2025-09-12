using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.File;
using Niobium.Platform.Notification.Email;

namespace Niobium.Notification.Core.Tests.Helpers;

internal static class NotificationTestHelper
{
    public static NotificationOptions DefaultOptions => new()
    {
        SelfHostName = "test.example",
        TemplateFolder = "templates"
    };

    public static Template BuildTemplate(Guid tenant, string channel, string subject = "Welcome", string from = "noreply@test.example", string? fromName = "Ops", string blob = "welcome.html", string? fallbackTo = null)
        => new()
        {
            Tenant = tenant,
            Channel = channel,
            Subject = subject,
            From = from,
            FromName = fromName,
            Blob = blob,
            FallbackTo = fallbackTo
        };

    public static NotifyCommand BuildCommand(Guid tenant, string channel, string? destination, Dictionary<string, string> parameters)
        => new()
        {
            Tenant = tenant,
            Channel = channel,
            Destination = destination,
            Parameters = parameters,
        };

    public static string BuildTemplateBody() => "<html><body>Hello {{NAME}}; your order is {{ORDER_ID}}. <a href=\"{{UNSUBSCRIBE_LINK}}\">Unsubscribe</a></body></html>";

    public static TemplateDomain CreateDomain(Template template, string? body, NotificationOptions options, Mock<IFileService>? fileServiceMock = null, Mock<ILogger<TemplateDomain>>? templateLoggerMock = null)
    {
        var repoMock = new Mock<IRepository<Template>>(MockBehavior.Loose);
        var fsMock = fileServiceMock ?? new Mock<IFileService>(MockBehavior.Strict);
        if (body != null)
        {
            _ = fsMock.Setup(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(body)));
        }

        var loggerMock = templateLoggerMock ?? new Mock<ILogger<TemplateDomain>>();

        var domain = new TemplateDomain(
            new Lazy<IRepository<Template>>(() => repoMock.Object),
            Array.Empty<IDomainEventHandler<IDomain<Template>>>(),
            fsMock.Object,
            HtmlEncoder.Default,
            Options.Create(options),
            loggerMock.Object);

        PreloadDomainEntity(domain, template);
        SetDomainKeys(domain, Template.BuildParitionKey(template.Tenant), Template.BuildRowKey(template.Channel));
        return domain;
    }

    public static void SetDomainKeys(TemplateDomain domain, string partitionKey, string rowKey)
    {
        SetNonPublicProperty(domain, "PartitionKey", partitionKey);
        SetNonPublicProperty(domain, "RowKey", rowKey);
    }

    private static void PreloadDomainEntity(TemplateDomain domain, Template template)
    {
        var type = domain.GetType();
        while (type != null)
        {
            var templateField = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                                     .FirstOrDefault(f => f.FieldType == typeof(Template));
            if (templateField != null)
            {
                templateField.SetValue(domain, template);
                return;
            }

            var prop = type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                           .FirstOrDefault(p => p.PropertyType == typeof(Template) && p.CanWrite);
            if (prop != null)
            {
                prop.SetValue(domain, template);
                return;
            }
            type = type.BaseType!;
        }
    }

    private static void SetNonPublicProperty(object obj, string name, object? value)
    {
        var type = obj.GetType();
        while (type != null)
        {
            var prop = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(obj, value);
                return;
            }
            var field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                        ?? type.GetField($"<{name}>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(obj, value);
                return;
            }
            type = type.BaseType!;
        }
    }

    public static (NotificationFlow sut, Mock<IDomainRepository<TemplateDomain, Template>> repoMock, Mock<IEmailNotificationClient> emailMock, Mock<ILogger<NotificationFlow>> flowLoggerMock) CreateSut(TemplateDomain domain)
    {
        var repoMock = new Mock<IDomainRepository<TemplateDomain, Template>>(MockBehavior.Strict);
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string pk, string rk, bool create, CancellationToken ct) => domain);

        var emailMock = new Mock<IEmailNotificationClient>(MockBehavior.Strict);
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(true);

        var flowLoggerMock = new Mock<ILogger<NotificationFlow>>();

        var sut = new NotificationFlow(repoMock.Object, emailMock.Object, flowLoggerMock.Object);
        return (sut, repoMock, emailMock, flowLoggerMock);
    }
}
