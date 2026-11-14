using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server;
using Niobium.Platform.Captcha.ReCaptcha;
using Niobium.Platform.Notification.Email;
using static Niobium.Notification.Core.Tests.Helpers.NotificationTestHelper;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class EntryPointTests
{
    private static HttpRequest Request(string body = "")
    {
        HttpRequest request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return request;
    }

    private static ServiceBusReceivedMessage Message(object body)
        => ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(body)), contentType: "application/json", deliveryCount: 1, messageId: "synthetic-message");

    private static Mock<IVisitorRiskAssessor> Assessor()
    {
        Mock<IVisitorRiskAssessor> mock = new(MockBehavior.Strict);
        mock.Setup(x => x.AssessAsync(It.IsAny<string>(), It.IsAny<string?>(), null, null, true, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return mock;
    }

    private static (SubscriptionDomain domain, Mock<IRepository<Subscription>> repository, List<Subscription> saved) SubscriptionStore()
    {
        List<Subscription> saved = [];
        Mock<IRepository<Subscription>> repository = new(MockBehavior.Strict);
        repository.Setup(x => x.CreateAsync(It.IsAny<IEnumerable<Subscription>>(), true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Subscription> entries, bool _, DateTimeOffset? _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                Subscription[] values = entries.ToArray();
                saved.AddRange(values);
                return values;
            });
        repository.Setup(x => x.UpdateAsync(It.IsAny<IEnumerable<Subscription>>(), true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Subscription> entries, bool _, bool _, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                Subscription[] values = entries.ToArray();
                saved.AddRange(values);
                return values;
            });
        return (new SubscriptionDomain(new Lazy<IRepository<Subscription>>(() => repository.Object), []), repository, saved);
    }

    [TestMethod]
    public async Task ContactUs_AwaitsProviderAndPassesCancellation()
    {
        Guid tenant = Guid.NewGuid();
        var (flow, _, sender, _) = CreateSut(CreateDomain(BuildTemplate(tenant, Constants.ContactUsChannel, fallbackTo: "approved@example.invalid"), "{{MESSAGE}}", DefaultOptions));
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        sender.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), cancellation.Token)).Returns(completion.Task);
        Mock<IVisitorRiskAssessor> assessor = Assessor();
        ContactUsRequest input = new() { ID = Guid.NewGuid(), Tenant = tenant, Message = "Invoice verification", Token = "synthetic" };
        Task<IActionResult> pending = new DeliverContactUs(HtmlEncoder.Default, flow, assessor.Object).ContactUs(Request(), input, cancellation.Token);
        Assert.IsFalse(pending.IsCompleted);
        completion.SetResult(true);
        Assert.AreEqual(201, ((StatusCodeResult)await pending).StatusCode);
        assessor.Verify(x => x.AssessAsync("synthetic", input.ID.ToString(), null, null, true, cancellation.Token), Times.Once);
        sender.Verify(x => x.SendAsync(It.IsAny<EmailAddress>(), It.Is<IEnumerable<EmailAddress>>(r => r.Single().Address == "approved@example.invalid"), It.IsAny<string>(), It.IsAny<string>(), cancellation.Token), Times.Once);
    }

    [TestMethod]
    public async Task ContactUs_InvalidInputNeverAssessesOrSends()
    {
        var (flow, repo, sender, _) = CreateSut(CreateDomain(BuildTemplate(Guid.NewGuid(), "ContactUs"), "body", DefaultOptions));
        Mock<IVisitorRiskAssessor> assessor = new(MockBehavior.Strict);
        IActionResult result = await new DeliverContactUs(HtmlEncoder.Default, flow, assessor.Object).ContactUs(Request(),
            new ContactUsRequest { ID = Guid.NewGuid(), Tenant = Guid.NewGuid(), Message = "", Token = "synthetic" }, default);
        Assert.IsInstanceOfType<ContentResult>(result);
        Assert.AreEqual(400, ((ContentResult)result).StatusCode);
        Assert.AreEqual(0, repo.Invocations.Count);
        Assert.AreEqual(0, sender.Invocations.Count);
        Assert.AreEqual(0, assessor.Invocations.Count);
    }

    [TestMethod]
    public async Task Subscribe_RequiresTokenAndValidInputBeforePersistence()
    {
        int created = 0;
        Subscribe function = new(() => { created++; return SubscriptionStore().domain; }, new Mock<IVisitorRiskAssessor>(MockBehavior.Strict).Object);
        SubscribeCommand input = new() { Tenant = Guid.NewGuid(), Campaign = "Invoice", Email = "person@example.invalid" };
        Assert.AreEqual(403, ((StatusCodeResult)await function.Run(Request(), input, default)).StatusCode);
        input.Token = "synthetic";
        input.Email = "not-an-email";
        Assert.AreEqual(400, ((ContentResult)await function.Run(Request(), input, default)).StatusCode);
        Assert.AreEqual(0, created);
    }

    [TestMethod]
    public async Task Subscribe_NormalizesInputAndPersistsSubscription()
    {
        var (domain, repository, saved) = SubscriptionStore();
        using CancellationTokenSource cancellation = new();
        SubscribeCommand input = new() { Tenant = Guid.NewGuid(), Campaign = "Invoice", Email = " Person@Example.Invalid ", Token = " synthetic ", FirstName = " Person ", LastName = " Example ", Track = " Test " };
        IActionResult result = await new Subscribe(() => domain, Assessor().Object).Run(Request(), input, cancellation.Token);
        Assert.IsInstanceOfType<OkResult>(result);
        Assert.HasCount(1, saved);
        Assert.AreEqual("person@example.invalid", saved[0].Email);
        Assert.AreEqual(input.Tenant, saved[0].GetTenant());
        Assert.AreEqual("Invoice", saved[0].GetChannel());
        Assert.AreEqual("Person", saved[0].FirstName);
        Assert.AreEqual("Example", saved[0].LastName);
        Assert.AreEqual("Test", saved[0].Source);
        Assert.IsNull(saved[0].Unsubscribed);
        repository.Verify(x => x.CreateAsync(It.IsAny<IEnumerable<Subscription>>(), true, null, cancellation.Token), Times.Once);
    }

    [TestMethod]
    public async Task Unsubscribe_UsesNormalizedKeyAndConditionalUpdate()
    {
        var (domain, repository, saved) = SubscriptionStore();
        Guid tenant = Guid.NewGuid();
        Subscription original = new() { Belonging = Subscription.BuildBelonging(tenant, Constants.DefaultChannel), Email = "person@example.invalid", ETag = "original-etag", Subscribed = DateTimeOffset.UtcNow.AddDays(-1) };
        domain.Initialize(original);
        Mock<IDomainRepository<SubscriptionDomain, Subscription>> repo = new(MockBehavior.Strict);
        using CancellationTokenSource cancellation = new();
        repo.Setup(x => x.GetAsync(original.Belonging, original.Email, false, cancellation.Token)).ReturnsAsync(domain);
        IActionResult result = await new Unsubscribe(repo.Object).Run(Request(), " Person@Example.Invalid ", tenant, null, cancellation.Token);
        Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.HasCount(1, saved);
        Assert.IsNotNull(saved[0].Unsubscribed);
        Assert.AreEqual(original.Subscribed, saved[0].Subscribed);
        repository.Verify(x => x.UpdateAsync(It.IsAny<IEnumerable<Subscription>>(), true, false, cancellation.Token), Times.Once);
    }

    [TestMethod]
    [DataRow("", true)]
    [DataRow("person@example.invalid", false)]
    public async Task Unsubscribe_InvalidIdentityNeverLoadsData(string email, bool validTenant)
    {
        Mock<IDomainRepository<SubscriptionDomain, Subscription>> repo = new(MockBehavior.Strict);
        Assert.IsInstanceOfType<BadRequestResult>(await new Unsubscribe(repo.Object).Run(Request(), email, validTenant ? Guid.NewGuid() : Guid.Empty, null, default));
        Assert.AreEqual(0, repo.Invocations.Count);
    }

    [TestMethod]
    public async Task SubscribeQueue_RejectsBadTransportEmptyTenantAndInvalidEmail()
    {
        int created = 0;
        SubscribeCommandConsumer consumer = new(() => { created++; return SubscriptionStore().domain; }, NullLogger<SubscribeCommandConsumer>.Instance);
        await consumer.Run(ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("not-json")), default);
        SubscribeCommand command = new() { Tenant = Guid.Empty, Campaign = "Invoice", Email = "person@example.invalid" };
        await consumer.Run(Message(command), default);
        command.Tenant = Guid.NewGuid(); command.Email = "not-an-email";
        await consumer.Run(Message(command), default);
        Assert.AreEqual(0, created);
    }

    [TestMethod]
    public async Task SubscribeQueue_PersistsValidatedCommand()
    {
        var (domain, _, saved) = SubscriptionStore();
        SubscribeCommand command = new() { Tenant = Guid.NewGuid(), Campaign = "Invoice", Email = " Person@Example.Invalid " };
        await new SubscribeCommandConsumer(() => domain, NullLogger<SubscribeCommandConsumer>.Instance).Run(Message(command), default);
        Assert.HasCount(1, saved);
        Assert.AreEqual(command.Tenant, saved[0].GetTenant());
        Assert.AreEqual("person@example.invalid", saved[0].Email);
    }

    [TestMethod]
    public async Task NotifyQueue_TransformsJsonParametersAndAwaitsProvider()
    {
        DurableNotificationTests.Scenario s = new(body: "{{NAME}} {{COUNT}} <!-- ITEMS BEGIN -->{{LABEL}}<!-- ITEMS END -->");
        TemplateDomain.Deliverable? frozen = null;
        s.Provider.Setup(p => p.Freeze(It.IsAny<TemplateDomain.Deliverable>()))
            .Callback<TemplateDomain.Deliverable>(content => frozen = content)
            .Returns(new FrozenNotification("frozen-json", "example.invalid", "credential-digest"));
        NotifyCommand command = new() { Tenant = s.Request.Tenant, Channel = s.Request.Channel, Destination = " person@example.invalid ", DestinationDisplayName = " Person ", Parameters = new() { ["name"] = "<Notice>", ["count"] = 2, ["items"] = new object[] { new { label = "<Line>" }, "ignored" } } };
        await new NotifyCommandConsumer(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance).Run(Message(command), default);
        Assert.IsNotNull(frozen);
        Assert.AreEqual("person@example.invalid", frozen.To);
        Assert.IsTrue(frozen.Body.Contains("&lt;Notice&gt;") && frozen.Body.Contains("&lt;Line&gt;") && frozen.Body.Contains("2"));
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public async Task NotifyQueue_InvalidTransportOrCommandNeverSends()
    {
        DurableNotificationTests.Scenario s = new();
        NotifyCommandConsumer consumer = new(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance);
        await consumer.Run(ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("invalid")), default);
        await consumer.Run(Message(new NotifyCommand { Tenant = Guid.NewGuid(), Channel = "", Parameters = [] }), default);
        Assert.AreEqual(0, s.Templates.Invocations.Count);
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Store.Repository.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public async Task SubscribedQueue_ForwardsEventAndCancellationOnlyAfterParsing()
    {
        Mock<IExternalEventAdaptor<Subscription, SubscribedEvent>> adaptor = new(MockBehavior.Strict);
        using CancellationTokenSource cancellation = new();
        Subscription subscription = new() { Belonging = Subscription.BuildBelonging(Guid.NewGuid(), "Invoice"), Email = "person@example.invalid", Subscribed = DateTimeOffset.UtcNow };
        adaptor.Setup(x => x.OnEvent(It.Is<SubscribedEvent>(e => e.Subscription.Email == subscription.Email && e.ID == "synthetic-message"), cancellation.Token)).Returns(Task.CompletedTask);
        SubscribedEventConsumer consumer = new(adaptor.Object, NullLogger<SubscribedEventConsumer>.Instance);
        await consumer.Run(ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("invalid")), cancellation.Token);
        Assert.AreEqual(0, adaptor.Invocations.Count);
        await consumer.Run(Message(new SubscribedEvent { ID = "synthetic-message", Subscription = subscription }), cancellation.Token);
        Assert.AreEqual(1, adaptor.Invocations.Count);
    }

    [TestMethod]
    [DataRow("First Last", "First", "Last")]
    [DataRow("First", "First", null)]
    public async Task GoogleAds_LocalAdapterPersistsNormalizedLead(string name, string first, string? last)
    {
        // Exercise the compiled legacy adapter entirely in memory; never send its key to a live service.
        string key = (string)typeof(SubscribeFromGoogleAds).GetField("CampaignKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var (domain, _, saved) = SubscriptionStore();
        GoogleAdsLeadForm input = new() { LeadID = "synthetic", GoogleKey = key, UserColumnData = [new() { ColumnID = "FULL_NAME", StringValue = name }, new() { ColumnID = "EMAIL", StringValue = " Person@Example.Invalid " }] };
        string body = JsonMarshaller.Marshall(input, JsonMarshallingFormat.SnakeCase);
        IActionResult result = await new SubscribeFromGoogleAds(() => domain, NullLogger<SubscribeFromGoogleAds>.Instance).Run(Request(body), default);
        Assert.IsInstanceOfType<OkResult>(result);
        Assert.HasCount(1, saved);
        Assert.AreEqual(first, saved[0].FirstName); Assert.AreEqual(last, saved[0].LastName);
        Assert.AreEqual("person@example.invalid", saved[0].Email); Assert.AreEqual("Google", saved[0].Source);
    }

    [TestMethod]
    public async Task GoogleAds_RejectsNullWrongKeyAndIncompleteLeadWithoutPersistence()
    {
        int calls = 0;
        SubscribeFromGoogleAds function = new(() => { calls++; return SubscriptionStore().domain; }, NullLogger<SubscribeFromGoogleAds>.Instance);
        await Assert.ThrowsExactlyAsync<InvalidCastException>(() => function.Run(Request("null"), default));
        GoogleAdsLeadForm input = new() { LeadID = "synthetic", GoogleKey = "not-the-key", UserColumnData = [] };
        Assert.IsInstanceOfType<BadRequestResult>(await function.Run(Request(JsonMarshaller.Marshall(input, JsonMarshallingFormat.SnakeCase)), default));
        input.GoogleKey = (string)typeof(SubscribeFromGoogleAds).GetField("CampaignKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.IsInstanceOfType<BadRequestResult>(await function.Run(Request(JsonMarshaller.Marshall(input, JsonMarshallingFormat.SnakeCase)), default));
        Assert.AreEqual(0, calls);
    }
}
