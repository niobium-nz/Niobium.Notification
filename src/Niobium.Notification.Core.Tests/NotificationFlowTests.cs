using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.File;
using Niobium.Platform.Notification.Email;
using static Niobium.Notification.Core.Tests.Helpers.NotificationTestHelper;

namespace Niobium.Notification.Core.Tests;

/// <summary>
/// Business-focused tests for NotificationFlow using a real TemplateDomain.
/// Each test describes the user-facing behavior of the notification process.
/// Helpers are extracted to NotificationTestHelper for clarity.
/// </summary>
[TestClass]
public sealed class NotificationFlowTests
{
    /// <summary>
    /// Sends a personalized email when a valid campaign and template are present.
    /// Given: a tenant/campaign with a valid template and placeholders in the body.
    /// When: a notification is requested with personalization parameters.
    /// Then: the email is sent with the template subject, HTML-encoded values, and a valid unsubscribe link.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_ValidTemplate_PersonalizedEmailIsDelivered()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "welcome";
        Template template = BuildTemplate(tenant, channel);
        string body = BuildTemplateBody();
        NotificationOptions options = DefaultOptions;
        TemplateDomain domain = CreateDomain(template, body, options);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>>? flowLoggerMock) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "alice@example.com", new Dictionary<string, object>
        {
            ["name"] = "Alice <Admin>",
            ["order_id"] = "#123 & 456"
        });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.Is<EmailAddress>(a => a.Address == template.From && a.DisplayName == template.FromName),
            It.Is<IEnumerable<EmailAddress>>(rcpts => rcpts.Single().Address == request.Destination && rcpts.Single().DisplayName == request.DestinationDisplayName),
            It.Is<string>(s => s == template.Subject),
            It.Is<string>(b => b.Contains("Alice &lt;Admin&gt;") && b.Contains("#123 &amp; 456") && b.Contains("/unsubscribe?email=alice%40example.com") && b.Contains($"tenant={tenant}") && b.Contains($"channel={channel}")),
            It.IsAny<CancellationToken>()), Times.Once);
        flowLoggerMock.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Personalization keys are treated case-insensitively and values are safely encoded.
    /// Given: a template with {{NAME}} and a provided parameter key in any case.
    /// When: a notification is requested using any case variant of the key.
    /// Then: the body contains the HTML-encoded value and no raw {{NAME}} placeholder remains.
    /// </summary>
    [TestMethod]
    [DataRow("name")]
    [DataRow("NAME")]
    [DataRow("NaMe")]
    public async Task SendNotification_PersonalizationKeys_AreCaseInsensitive_AndEncoded(string keyVariant)
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "welcome";
        Template template = BuildTemplate(tenant, channel);
        string body = BuildTemplateBody();
        TemplateDomain domain = CreateDomain(template, body, DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>> _, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        string value = "Alice <Admin>";
        NotifyCommand request = BuildCommand(tenant, channel, "alice@example.com", new Dictionary<string, object>
        {
            [keyVariant] = value
        });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.IsAny<EmailAddress>(),
            It.IsAny<IEnumerable<EmailAddress>>(),
            It.IsAny<string>(),
            It.Is<string>(b => b.Contains("Alice &lt;Admin&gt;") && !b.Contains("{{NAME}}")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Uses a default recipient from the template when the request omits a destination.
    /// Given: a campaign template with a default recipient.
    /// When: the request does not specify a destination.
    /// Then: the email is sent to the default recipient.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_NoDestination_DefaultRecipientIsUsed()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "promo";
        Template template = BuildTemplate(tenant, channel, fallbackTo: "fallback@example.com");
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, null, new Dictionary<string, object> { ["name"] = "Bob" });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.IsAny<EmailAddress>(),
            It.Is<IEnumerable<EmailAddress>>(rcpts => rcpts.Single().Address == template.FallbackTo),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// If no template exists for the campaign, the system quietly skips sending.
    /// Given: no template can be loaded for the requested tenant/campaign.
    /// When: a notification is requested.
    /// Then: no email is sent, and the domain logs a warning about the missing template.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_NoTemplate_Configured_SkipsWithoutError()
    {
        // Given: a domain that cannot load any template
        Guid tenant = Guid.NewGuid();
        string channel = "missing";
        Template template = BuildTemplate(tenant, channel);
        NotificationOptions options = DefaultOptions;

        Mock<IRepository<Template>> repoMockInner = new(MockBehavior.Loose);
        Mock<IFileService> fsMock = new(MockBehavior.Strict);
        Mock<ILogger<TemplateDomain>> templateLoggerMock = new();
        TemplateDomain domain = new(
            new Lazy<IRepository<Template>>(() => repoMockInner.Object),
            Array.Empty<IDomainEventHandler<IDomain<Template>>>(),
            fsMock.Object,
            HtmlEncoder.Default,
            Options.Create(options),
            templateLoggerMock.Object);
        // do NOT preload entity, so BuildAsync will return null
        SetDomainKeys(domain, Template.BuildParitionKey(template.Tenant), Template.BuildRowKey(template.Channel));

        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>>? flowLoggerMock) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "nobody@example.com", []);

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then: no send and domain warns about missing template
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        templateLoggerMock.Verify(x => x.Log(
            It.Is<LogLevel>(l => l == LogLevel.Warning),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Missing email template for")),
            It.IsAny<Exception?>(),
            It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)), Times.AtLeastOnce);
    }

    /// <summary>
    /// Trims the campaign code before selecting the template.
    /// Given: a request whose campaign (channel) has extra whitespace.
    /// When: a notification is requested.
    /// Then: the repository is queried using the trimmed channel value.
    /// </summary>
    [TestMethod]
    [DataRow("welcome")]
    [DataRow(" welcome ")]
    [DataRow("welcome ")]
    public async Task SendNotification_SelectsTemplate_UsingTrimmedChannel(string channel)
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string cleanChannel = channel.Trim();
        Template template = BuildTemplate(tenant, cleanChannel);
        NotificationOptions options = DefaultOptions;

        Mock<IFileService> fsMock = new(MockBehavior.Strict);
        _ = fsMock.Setup(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(BuildTemplateBody())));

        TemplateDomain domain = CreateDomain(template, null, options, fsMock);

        Mock<IDomainRepository<TemplateDomain, Template>> repoMock = new(MockBehavior.Strict);
        string? capturedPk = null, capturedRk = null;
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string pk, string rk, bool create, CancellationToken ct) => { capturedPk = pk; capturedRk = rk; return domain; });

        Mock<IEmailNotificationClient> emailMock = new(MockBehavior.Strict);
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(true);

        Mock<ILogger<NotificationFlow>> flowLoggerMock = new();
        NotificationFlow sut = new(repoMock.Object, emailMock.Object, flowLoggerMock.Object);

        // When
        await sut.RunAsync(BuildCommand(tenant, channel, "user@example.com", []), CancellationToken.None);

        // Then
        _ = capturedPk.Should().Be(Template.BuildParitionKey(tenant));
        _ = capturedRk.Should().Be(Template.BuildRowKey(channel));
    }

    /// <summary>
    /// Rejects sending when the subject is missing.
    /// Given: a template with null/empty/whitespace subject.
    /// When: a notification is requested.
    /// Then: an application validation error is thrown and nothing is sent.
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SendNotification_NoSubject_FailsValidation(string? subject)
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "ops";
        Template template = BuildTemplate(tenant, channel, subject: subject ?? String.Empty);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>>? flowLoggerMock) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "carol@example.com", []);

        // When
        Func<Task> act = async () => await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<ApplicationException>()
            .Where(e => e.ErrorCode == InternalError.InternalServerError);
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Surfaces delivery failures from the email system and logs the failure.
    /// Given: delivery is attempted but the email client returns false.
    /// When: a notification is requested.
    /// Then: an application error is thrown and the failure is logged with recipient and channel.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_DeliveryFails_IsLogged_AndThrows()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "billing";
        Template template = BuildTemplate(tenant, channel);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>>? flowLoggerMock) = CreateSut(domain);
        emailMock.Reset();
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(false);
        NotifyCommand request = BuildCommand(tenant, channel, "dave@example.com", []);

        // When
        Func<Task> act = async () => await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<ApplicationException>()
            .Where(e => e.ErrorCode == InternalError.InternalServerError);

        string expectedError = $"Failed sending email to {request.Destination} for {request.Channel} by {request.Channel}.";
        flowLoggerMock.Verify(x => x.Log(
            It.Is<LogLevel>(l => l == LogLevel.Error),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedError)),
            It.IsAny<Exception?>(),
            It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)));
    }

    /// <summary>
    /// Looks up the correct template and reads the correct file path for the body.
    /// Given: a tenant/campaign and a known blob path.
    /// When: a notification is requested.
    /// Then: the repository keys match tenant/channel and the file service reads {tenant}/{blob} under the configured folder.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_UsesCorrectKeys_AndTemplatePath()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "digest";
        Template template = BuildTemplate(tenant, channel, blob: "digest.html");
        NotificationOptions options = DefaultOptions;

        Mock<IFileService> fsMock = new(MockBehavior.Strict);
        _ = fsMock.Setup(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(BuildTemplateBody())));

        TemplateDomain domain = CreateDomain(template, null, options, fsMock);

        Mock<IDomainRepository<TemplateDomain, Template>> repoMock = new(MockBehavior.Strict);
        string? capturedPk = null, capturedRk = null;
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string pk, string rk, bool create, CancellationToken ct) =>
                {
                    capturedPk = pk; capturedRk = rk; return domain;
                });

        Mock<IEmailNotificationClient> emailMock = new(MockBehavior.Strict);
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(true);

        Mock<ILogger<NotificationFlow>> flowLoggerMock = new();
        NotificationFlow sut = new(repoMock.Object, emailMock.Object, flowLoggerMock.Object);
        NotifyCommand request = BuildCommand(tenant, channel, "erin@example.com", new Dictionary<string, object> { ["name"] = "Erin" });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = capturedPk.Should().Be(Template.BuildParitionKey(tenant));
        _ = capturedRk.Should().Be(Template.BuildRowKey(channel));
        fsMock.Verify(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Propagates the caller's CancellationToken to downstream operations.
    /// Given: a cancellation token supplied by the caller.
    /// When: a notification is requested.
    /// Then: the same token is passed to repository lookup, body build, and email send.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_PropagatesCancellationToken()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "alerts";
        Template template = BuildTemplate(tenant, channel);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);

        Mock<IDomainRepository<TemplateDomain, Template>> repoMock = new(MockBehavior.Strict);
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(domain);

        Mock<IEmailNotificationClient> emailMock = new(MockBehavior.Strict);
        CancellationTokenSource cts = new();
        CancellationToken? seenToken = null;
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<EmailAddress, IEnumerable<EmailAddress>, string, string, CancellationToken>((_, _, _, _, ct) => seenToken = ct)
                 .ReturnsAsync(true);

        Mock<ILogger<NotificationFlow>> flowLoggerMock = new();
        NotificationFlow sut = new(repoMock.Object, emailMock.Object, flowLoggerMock.Object);

        // When
        await sut.RunAsync(BuildCommand(tenant, channel, "frank@example.com", []), cts.Token);

        // Then
        _ = seenToken.Should().NotBeNull();
        _ = seenToken.Should().Be(cts.Token);
    }

    /// <summary>
    /// Allows sending when display names are not supplied.
    /// Given: sender/recipient display names are absent.
    /// When: a notification is requested.
    /// Then: the email is still sent using email addresses only.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_AllowsMissingDisplayNames()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "status";
        Template template = BuildTemplate(tenant, channel, fromName: null);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);

        // When
        await sut.RunAsync(BuildCommand(tenant, channel, "gina@example.com", []), CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.Is<EmailAddress>(a => a.Address == template.From && a.DisplayName == null),
            It.Is<IEnumerable<EmailAddress>>(rcpts => rcpts.Single().DisplayName == null),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Handles repeated requests independently without leaking state.
    /// Given: two identical notifications sent in sequence.
    /// When: the flow processes both.
    /// Then: each results in its own send call.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_RepeatedRequests_AreIndependent()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "news";
        Template template = BuildTemplate(tenant, channel);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>> _, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);

        // When
        await sut.RunAsync(BuildCommand(tenant, channel, "henry@example.com", []), CancellationToken.None);
        await sut.RunAsync(BuildCommand(tenant, channel, "henry@example.com", []), CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// Bubbles unexpected errors from template lookup and does not attempt to send.
    /// Given: the template repository throws.
    /// When: a notification is requested.
    /// Then: the exception is propagated and no email is attempted.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_TemplateLookupFails_BubblesException_NoSend()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "any";
        NotifyCommand request = BuildCommand(tenant, channel, "to@example.com", []);

        Mock<IDomainRepository<TemplateDomain, Template>> repoMock = new(MockBehavior.Strict);
        _ = repoMock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

        Mock<IEmailNotificationClient> emailMock = new(MockBehavior.Strict);
        Mock<ILogger<NotificationFlow>> flowLoggerMock = new();
        NotificationFlow sut = new(repoMock.Object, emailMock.Object, flowLoggerMock.Object);

        // When
        Func<Task> act = async () => await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Validates destination is required when no default exists on the template.
    /// Given: request omits destination and template has no fallback.
    /// When: a notification is requested.
    /// Then: an application validation error is thrown and no email is sent.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_NoDestinationAndNoDefault_FailsValidation_NoSend()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "campaign";
        Template template = BuildTemplate(tenant, channel, fallbackTo: null);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, null, []);

        // When
        Func<Task> act = async () => await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<ApplicationException>()
            .Where(e => e.ErrorCode == InternalError.InternalServerError);
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Fails when the template file cannot be found, and does not attempt delivery.
    /// Given: the template references a blob that does not exist.
    /// When: a notification is requested.
    /// Then: an application error is thrown stating the missing template path.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_TemplateFileMissing_Fails_NoSend()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "withfile";
        Template template = BuildTemplate(tenant, channel);
        NotificationOptions options = DefaultOptions;

        Mock<IFileService> fsMock = new(MockBehavior.Strict);
        _ = fsMock.Setup(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((Stream?)null);

        TemplateDomain domain = CreateDomain(template, null, options, fsMock);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "user@example.com", new Dictionary<string, object> { ["name"] = "U" });

        // When
        Func<Task> act = async () => await sut.RunAsync(request, CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<ApplicationException>()
            .Where(e => e.ErrorCode == InternalError.InternalServerError);
        fsMock.Verify(x => x.GetAsync(options.TemplateFolder, $"{template.Tenant}/{template.Blob}", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        emailMock.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Bubbles exceptions thrown by the email client without logging flow-level errors.
    /// Given: the email client throws during send.
    /// When: a notification is requested.
    /// Then: the exception is propagated and the flow does not log an error (since send did not return).
    /// </summary>
    [TestMethod]
    public async Task SendNotification_EmailClientThrows_Bubbles_NoErrorLog()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "mail";
        Template template = BuildTemplate(tenant, channel);
        TemplateDomain domain = CreateDomain(template, BuildTemplateBody(), DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>>? repoMock, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>>? flowLoggerMock) = CreateSut(domain);

        emailMock.Reset();
        _ = emailMock.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("smtp down"));

        // When
        Func<Task> act = async () => await sut.RunAsync(BuildCommand(tenant, channel, "joe@example.com", []), CancellationToken.None);

        // Then
        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("smtp down");
        flowLoggerMock.Verify(x => x.Log(
            It.Is<LogLevel>(l => l == LogLevel.Error),
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => true),
            It.IsAny<Exception?>(),
            It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)), Times.Never);
    }

    /// <summary>
    /// Renders a repeatable section for each value and encodes content using sub-keys in dictionaries.
    /// Given: a template with a repeatable ITEMS section and two dictionary values (NAME, URL, DESC).
    /// When: a notification is requested.
    /// Then: the body contains two <li> entries with encoded link, name and desc, markers remain, and no raw placeholders remain.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_RepeatableSection_RendersMultipleItems_Encoded()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "repeat";
        Template template = BuildTemplate(tenant, channel);
        string body = "<html><body><ul><!-- ITEMS BEGIN --><li><a href=\"{{URL}}\">{{NAME}}</a> - {{DESC}}</li><!-- ITEMS END --></ul></body></html>";
        TemplateDomain domain = CreateDomain(template, body, DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>> _, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "list@example.com", new Dictionary<string, object>
        {
            ["items"] = new[]
            {
                new Dictionary<string, string> { ["name"] = "One <1>", ["url"] = "https://x.com?q=1&k=2", ["desc"] = "A & B" },
                new Dictionary<string, string> { ["name"] = "Two", ["url"] = "https://y.com?a=3&b=4", ["desc"] = "<X>" }
            }
        });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.IsAny<EmailAddress>(),
            It.IsAny<IEnumerable<EmailAddress>>(),
            It.IsAny<string>(),
            It.Is<string>(b => BodyHasTwoEncodedItems(b)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Removes the repeatable section content when the collection is empty (no list items rendered).
    /// Given: a template with a repeatable ITEMS section and an empty dictionary collection.
    /// When: a notification is requested.
    /// Then: the body contains no <li> items for the section, the section markers remain, and the placeholder is not present.
    /// </summary>
    [TestMethod]
    public async Task SendNotification_RepeatableSection_EmptyCollection_RendersNothing()
    {
        // Given
        Guid tenant = Guid.NewGuid();
        string channel = "repeat-empty";
        Template template = BuildTemplate(tenant, channel);
        string body = "<html><body><ul><!-- ITEMS BEGIN --><li><a href=\"{{URL}}\">{{NAME}}</a> - {{DESC}}</li><!-- ITEMS END --></ul></body></html>";
        TemplateDomain domain = CreateDomain(template, body, DefaultOptions);
        (NotificationFlow? sut, Mock<IDomainRepository<TemplateDomain, Template>> _, Mock<IEmailNotificationClient>? emailMock, Mock<ILogger<NotificationFlow>> _) = CreateSut(domain);
        NotifyCommand request = BuildCommand(tenant, channel, "list@example.com", new Dictionary<string, object>
        {
            ["ITEMS"] = Array.Empty<Dictionary<string, string>>()
        });

        // When
        await sut.RunAsync(request, CancellationToken.None);

        // Then
        emailMock.Verify(x => x.SendAsync(
            It.IsAny<EmailAddress>(),
            It.IsAny<IEnumerable<EmailAddress>>(),
            It.IsAny<string>(),
            It.Is<string>(b => BodyHasNoItemsButMarkers(b)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool BodyHasTwoEncodedItems(string b)
    {
        // Expect two encoded li rows based on dictionaries supplied in test
        bool first = b.Contains("<li><a href=\"https://x.com?q=1&amp;k=2\">One &lt;1&gt;</a> - A &amp; B</li>");
        bool second = b.Contains("<li><a href=\"https://y.com?a=3&amp;b=4\">Two</a> - &lt;X&gt;</li>");
        bool hasMarkers = b.Contains("<!-- ITEMS BEGIN -->") && b.Contains("<!-- ITEMS END -->");
        bool noPlaceholders = !b.Contains("{{ITEMS}}") && !b.Contains("{{URL}}") && !b.Contains("{{NAME}}") && !b.Contains("{{DESC}}");
        static int Count(string s, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = s.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
        bool exactItemCount = Count(b, "<li>") == 2 && Count(b, "</li>") == 2;
        return first && second && hasMarkers && noPlaceholders && exactItemCount;
    }

    private static bool BodyHasNoItemsButMarkers(string b)
    {
        bool noItems = !b.Contains("<li>");
        bool hasMarkers = b.Contains("<!-- ITEMS BEGIN -->") && b.Contains("<!-- ITEMS END -->");
        bool noPlaceholders = !b.Contains("{{ITEMS}}") && !b.Contains("{{URL}}") && !b.Contains("{{NAME}}") && !b.Contains("{{DESC}}");
        string startTag = "<!-- ITEMS BEGIN -->";
        string endTag = "<!-- ITEMS END -->";
        int start = b.IndexOf(startTag, StringComparison.Ordinal);
        int end = b.IndexOf(endTag, StringComparison.Ordinal);
        bool sectionCleared = true;
        if (start >= 0 && end >= 0 && end > start)
        {
            start += startTag.Length;
            string between = b[start..end];
            sectionCleared = String.IsNullOrWhiteSpace(between) || between.Trim() == String.Empty;
        }
        return noItems && hasMarkers && noPlaceholders && sectionCleared;
    }
}
