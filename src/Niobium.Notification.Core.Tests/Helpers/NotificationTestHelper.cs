using System.Reflection;
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

    public static NotifyCommand BuildCommand(Guid tenant, string channel, string? destination, Dictionary<string, object> parameters)
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
        Mock<IRepository<Template>> repoMock = new(MockBehavior.Loose);
        Mock<IFileService> fsMock = fileServiceMock ?? new Mock<IFileService>(MockBehavior.Strict);
        if (body != null)
        {
            _ = fsMock.Setup(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(body)));
        }

        Mock<ILogger<TemplateDomain>> loggerMock = templateLoggerMock ?? new Mock<ILogger<TemplateDomain>>();

        TemplateDomain domain = new(
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
        Type type = domain.GetType();
        while (type != null)
        {
            FieldInfo? templateField = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                                     .FirstOrDefault(f => f.FieldType == typeof(Template));
            if (templateField != null)
            {
                templateField.SetValue(domain, template);
                return;
            }

            PropertyInfo? prop = type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
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
        Type type = obj.GetType();
        while (type != null)
        {
            PropertyInfo? prop = type.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(obj, value);
                return;
            }
            FieldInfo? field = type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
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
        Mock<IDomainRepository<TemplateDomain, Template>> repoMock = new(MockBehavior.Strict);
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string pk, string rk, bool create, CancellationToken ct) => domain);

        Mock<IEmailNotificationClient> emailMock = new(MockBehavior.Strict);
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(true);

        Mock<ILogger<NotificationFlow>> flowLoggerMock = new();

        NotificationFlow sut = new(repoMock.Object, emailMock.Object, flowLoggerMock.Object);
        return (sut, repoMock, emailMock, flowLoggerMock);
    }
}
