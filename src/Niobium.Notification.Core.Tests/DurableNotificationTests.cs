using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Azure.Messaging.ServiceBus;
using Moq;
using System.Text.Encodings.Web;
using Niobium.File;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server;
using Niobium.Platform.Notification.Email;
using static Niobium.Notification.Core.Tests.Helpers.NotificationTestHelper;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class DurableNotificationTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedRetry : NotificationRetryPolicy
    {
        public override TimeSpan Delay(int attempt) => TimeSpan.FromMinutes(2);
    }

    internal sealed class MemoryStore
    {
        public NotificationSubmission? Entry { get; set; }
        public Func<NotificationSubmission, bool>? RejectUpdate { get; set; }
        public Action? AfterWrite { get; set; }
        public bool LoseCompletion { get; set; }
        public bool MissingAfterCreate { get; set; }
        public int Creates { get; private set; }
        public readonly Mock<IRepository<NotificationSubmission>> Repository = new(MockBehavior.Strict);
        public readonly Mock<IFileService> Files = new(MockBehavior.Strict);
        public readonly Dictionary<string, byte[]> Payloads = [];
        private int version;
        private readonly object gate = new();

        public MemoryStore()
        {
            Repository.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Entry);
            Repository.Setup(r => r.CreateAsync(It.IsAny<IEnumerable<NotificationSubmission>>(), false, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<NotificationSubmission> values, bool _, DateTimeOffset? _, CancellationToken _) =>
                {
                    lock (gate)
                    {
                        Creates++;
                        if (Entry != null) throw new Niobium.ApplicationException(InternalError.Conflict);
                        NotificationSubmission value = values.Single();
                        if (!MissingAfterCreate) Entry = value with { ETag = (++version).ToString() };
                        AfterWrite?.Invoke();
                        return new[] { value with { ETag = null } }; // Pinned adapter write shape.
                    }
                });
            Repository.Setup(r => r.UpdateAsync(It.IsAny<IEnumerable<NotificationSubmission>>(), true, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<NotificationSubmission> values, bool _, bool _, CancellationToken _) =>
                {
                    lock (gate)
                    {
                        NotificationSubmission value = values.Single();
                        if (LoseCompletion && value.StateCode == (int)SubmissionState.Accepted) throw new IOException("synthetic storage timeout");
                        if (value.ETag != Entry?.ETag || RejectUpdate?.Invoke(value) == true) throw new Niobium.ApplicationException(InternalError.Conflict);
                        Entry = value with { ETag = (++version).ToString() };
                        AfterWrite?.Invoke();
                        return new[] { value with { ETag = null } };
                    }
                });
            Files.Setup(f => f.PutAsync(NotificationSubmission.PayloadContainer, It.IsAny<string>(), It.IsAny<Stream>(), false, null, true, It.IsAny<CancellationToken>()))
                .Returns(async (string _, string name, Stream content, bool _, IDictionary<string, string>? _, bool _, CancellationToken token) =>
                {
                    using MemoryStream copy = new(); await content.CopyToAsync(copy, token);
                    Payloads.Add(name, copy.ToArray());
                });
            Files.Setup(f => f.GetAsync(NotificationSubmission.PayloadContainer, It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string name, bool _, CancellationToken _) => new MemoryStream(Payloads[name]));
        }

        public NotificationSubmissionDomain Domain() => new(new Lazy<IRepository<NotificationSubmission>>(() => Repository.Object), [], Files.Object);
        public IDomainRepository<NotificationSubmissionDomain, NotificationSubmission> Domains()
            => new GenericDomainRepository<NotificationSubmissionDomain, NotificationSubmission>(Domain, new Lazy<IRepository<NotificationSubmission>>(() => Repository.Object));
    }

    internal sealed class Scenario
    {
        public readonly Clock Clock = new();
        public readonly MemoryStore Store = new();
        public readonly Mock<IIdempotentNotificationProvider> Provider = new(MockBehavior.Strict);
        public readonly Mock<IMessagingBroker<NotifyCommand>> Queue = new(MockBehavior.Strict);
        public readonly List<MessagingEntry<NotifyCommand>> Scheduled = [];
        public readonly NotificationFlow Renderer;
        public readonly Mock<IEmailNotificationClient> Legacy;
        public readonly Mock<IDomainRepository<TemplateDomain, Template>> Templates;
        public readonly NotifyCommand Request = new() { ID = "operation-1", Tenant = Guid.NewGuid(), Channel = "Notice", Destination = "recipient@example.invalid", Parameters = new() { ["name"] = "First" } };

        public Scenario(bool missingTemplate = false, Guid? tenant = null, string body = "{{NAME}}")
        {
            if (tenant.HasValue) Request.Tenant = tenant.Value;
            TemplateDomain domain = missingTemplate
                ? new TemplateDomain(new Lazy<IRepository<Template>>(() => new Mock<IRepository<Template>>().Object), [], new Mock<IFileService>().Object, HtmlEncoder.Default, Options.Create(DefaultOptions), NullLogger<TemplateDomain>.Instance)
                : CreateDomain(BuildTemplate(Request.Tenant, Request.Channel), body, DefaultOptions);
            if (missingTemplate) SetDomainKeys(domain, Template.BuildParitionKey(Request.Tenant), Request.Channel);
            (Renderer, Templates, Legacy, _) = CreateSut(domain);
            Provider.Setup(p => p.Freeze(It.IsAny<TemplateDomain.Deliverable>())).Returns(new FrozenNotification("frozen-json", "example.invalid", "credential-digest"));
            Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SubmissionResult(SubmissionOutcome.Accepted, "provider-1"));
            Queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) => Scheduled.AddRange(entries))
                .Returns(Task.CompletedTask);
        }

        public DurableNotificationFlow Flow() => new(Renderer, Store.Domains(), Provider.Object, Queue.Object, Clock, new FixedRetry());
        public Task Run() => Flow().RunAsync(Request, isFirstDelivery: true);
        public int Sends => Provider.Invocations.Count(i => i.Method.Name == nameof(IIdempotentNotificationProvider.SubmitAsync));
        public void Advance() => Clock.Now = Store.Entry!.NextAttempt!.Value;
        public void Outcome(SubmissionOutcome outcome) => Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubmissionResult(outcome));
    }

    [TestMethod]
    public async Task AcceptedReplay_IsSuppressedAfterProviderWindow_WithoutRerendering()
    {
        Scenario s = new();
        await s.Run();
        Assert.AreEqual((int)SubmissionState.Accepted, s.Store.Entry!.StateCode);
        Assert.AreEqual("provider-1", s.Store.Entry.ProviderID);
        Assert.AreEqual(1, s.Scheduled.Count);
        Assert.AreEqual(s.Request.ID, s.Scheduled[0].ID);
        Assert.AreEqual(s.Request.ID, s.Scheduled[0].Value.ID);
        Assert.AreEqual(s.Clock.Now.AddMinutes(2), s.Scheduled[0].Schedule);
        s.Clock.Now += TimeSpan.FromDays(30);
        await s.Run();
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(1, s.Templates.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public async Task LostProviderResponse_RestartRetainsKeyPayloadWindowAndSchedule()
    {
        Scenario s = new();
        s.Outcome(SubmissionOutcome.Retry);
        await s.Run();
        DateTimeOffset? first = s.Store.Entry!.FirstSubmission;
        await s.Run(); // Early queue redelivery must schedule, not send.
        Assert.AreEqual(1, s.Sends);
        s.Advance();
        await s.Run(); // A fresh flow instance represents process restart.
        Assert.AreEqual(2, s.Sends);
        Assert.AreEqual(first, s.Store.Entry.FirstSubmission);
        Assert.AreEqual(1, s.Templates.Invocations.Count);
        var calls = s.Provider.Invocations.Where(i => i.Method.Name == "SubmitAsync").ToArray();
        Assert.AreEqual(calls[0].Arguments[0], calls[1].Arguments[0]);
        Assert.AreEqual(calls[0].Arguments[1], calls[1].Arguments[1]);
        Assert.AreEqual(calls[0].Arguments[2], calls[1].Arguments[2]);
    }

    [TestMethod]
    public async Task CrashAfterProviderAcceptance_RecoveryUsesSameOperation()
    {
        Scenario s = new();
        s.Store.LoseCompletion = true;
        await Assert.ThrowsExactlyAsync<IOException>(s.Run);
        Assert.AreEqual((int)SubmissionState.Sending, s.Store.Entry!.StateCode);
        Assert.HasCount(1, s.Scheduled);
        s.Store.LoseCompletion = false;
        s.Advance();
        await s.Run();
        Assert.AreEqual((int)SubmissionState.Accepted, s.Store.Entry.StateCode);
        Assert.AreEqual(2, s.Sends);
        Assert.AreEqual(1, s.Store.Creates);
    }

    [TestMethod]
    public async Task ScheduleFailure_PreventsProviderCall_AndLeavesRecoverableClaim()
    {
        Scenario s = new();
        s.Queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("synthetic queue failure"));
        await Assert.ThrowsExactlyAsync<IOException>(s.Run);
        Assert.AreEqual(0, s.Sends);
        Assert.AreEqual((int)SubmissionState.Sending, s.Store.Entry!.StateCode);
        Assert.AreEqual(s.Clock.Now, s.Store.Entry.FirstSubmission);
    }

    [TestMethod]
    public async Task ConcurrentMessage_CannotSendWhileClaimIsActive()
    {
        Scenario s = new();
        TaskCompletionSource<SubmissionResult> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(response.Task);
        Task first = s.Run();
        await s.Run();
        Assert.AreEqual(1, s.Sends);
        response.SetResult(new(SubmissionOutcome.Accepted, "provider-1"));
        await first;
        Assert.AreEqual((int)SubmissionState.Accepted, s.Store.Entry!.StateCode);
    }

    [TestMethod]
    [DataRow(1439)]
    [DataRow(1440)]
    [DataRow(2880)]
    public async Task WindowBoundary_HoldsUnknownOutcome_WithoutAnotherSend(int minutes)
    {
        Scenario s = new(); s.Outcome(SubmissionOutcome.Retry);
        await s.Run();
        s.Clock.Now += TimeSpan.FromMinutes(minutes);
        await s.Run();
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry!.StateCode);
        Assert.AreEqual(1, s.Sends);
        await s.Run();
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task HoldConflict_PropagatesForRedeliveryWithoutSending()
    {
        Scenario s = new(); s.Outcome(SubmissionOutcome.Retry); await s.Run();
        s.Clock.Now += TimeSpan.FromDays(1);
        s.Store.RejectUpdate = e => e.StateCode == (int)SubmissionState.Held;
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task AttemptExhaustion_HoldsInsteadOfStartingNewWindow()
    {
        Scenario s = new(); s.Outcome(SubmissionOutcome.Retry);
        await s.Run();
        s.Store.Entry = s.Store.Entry! with { Attempts = NotificationRetryPolicy.MaximumAttempts };
        s.Advance(); await s.Run();
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry.StateCode);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    [DataRow(SubmissionOutcome.Rejected, SubmissionState.Failed)]
    [DataRow(SubmissionOutcome.Hold, SubmissionState.Held)]
    public async Task TerminalOutcome_DoesNotResubmit(SubmissionOutcome outcome, SubmissionState state)
    {
        Scenario s = new(); s.Outcome(outcome);
        await s.Run(); await s.Run();
        Assert.AreEqual((int)state, s.Store.Entry!.StateCode);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task RejectionAfterUnknownAttempt_DoesNotEraseUncertainty()
    {
        Scenario s = new(); s.Outcome(SubmissionOutcome.Retry);
        await s.Run(); s.Advance(); s.Outcome(SubmissionOutcome.Rejected);
        await s.Run();
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry!.StateCode);
    }

    [TestMethod]
    public async Task ChangedContentAndUnknownStateOrVersion_FailClosed()
    {
        Scenario s = new(); s.Outcome(SubmissionOutcome.Retry); await s.Run();
        s.Request.Parameters["name"] = "Changed";
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        s.Request.Parameters["name"] = "First";
        // Simulate an incompatible stored version through the mapper-visible property.
        typeof(NotificationSubmission).GetProperty(nameof(NotificationSubmission.Version))!.SetValue(s.Store.Entry, 1);
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        typeof(NotificationSubmission).GetProperty(nameof(NotificationSubmission.Version))!.SetValue(s.Store.Entry, NotificationSubmission.CurrentVersion);
        s.Store.Entry = s.Store.Entry! with { StateCode = 999 };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task MissingTemplate_PersistsExplicitFailureWithoutSending()
    {
        Scenario s = new(true);
        await s.Run(); await s.Run();
        Assert.AreEqual((int)SubmissionState.Failed, s.Store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.TemplateMissing, s.Store.Entry.OutcomeCode);
        Assert.AreEqual(0, s.Sends);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task ClaimOrCompletionConflict_PropagatesWithoutOverwrite()
    {
        Scenario s = new(); s.Store.RejectUpdate = _ => true;
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(0, s.Sends);
        s.Store.RejectUpdate = e => e.StateCode == (int)SubmissionState.Accepted;
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual((int)SubmissionState.Sending, s.Store.Entry!.StateCode);
    }

    [TestMethod]
    public async Task InvalidIdentityOrUnverifiableInsert_NeverSends()
    {
        Scenario s = new(); s.Request.ID = "";
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        s.Request.ID = "operation-1"; s.Store.MissingAfterCreate = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(s.Run);
        Assert.AreEqual(0, s.Sends);
    }

    [TestMethod]
    [DataRow("11111111-1111-4111-8111-111111111111")]
    [DataRow("22222222-2222-4222-8222-222222222222")]
    public async Task EveryTenantConsumer_UsesEnvelopeIdentityAndSuppressesRedelivery(string tenant)
    {
        Scenario s = new(tenant: Guid.Parse(tenant));
        NotifyCommandConsumer consumer = new(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance);
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json", deliveryCount: 1, messageId: "envelope-identity");
        await consumer.Run(message, default); await consumer.Run(message, default);
        Assert.AreEqual("68F554E657E9EEF383AF38E021EA7CD46AF887AE13EAF84F3F924FEB12CACAA3", s.Store.Entry!.ID);
        Assert.AreEqual(s.Request.Tenant.ToString("N"), s.Store.Entry.Tenant);
        Assert.AreEqual("envelope-identity", s.Scheduled.Single().ID);
        Assert.AreEqual("envelope-identity", s.Scheduled.Single().Value.ID);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public async Task ConsumerWithoutBodyIdentity_ReusesEnvelopeAcrossRestart()
    {
        Scenario s = new();
        var command = new
        {
            s.Request.Tenant, s.Request.Channel, s.Request.Destination, s.Request.Parameters,
        };
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(command)), contentType: "application/json", deliveryCount: 1, messageId: "stable-envelope");
        await new NotifyCommandConsumer(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance).Run(message, default);
        string identity = s.Store.Entry!.ID;
        s.Clock.Now += TimeSpan.FromDays(30);
        await new NotifyCommandConsumer(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance).Run(message, default);
        Assert.AreEqual(identity, s.Store.Entry.ID);
        Assert.AreEqual((int)SubmissionState.Accepted, s.Store.Entry.StateCode);
        Assert.AreEqual("stable-envelope", s.Scheduled.Single().ID);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public async Task EmptyTenantConsumer_NeverSends()
    {
        Scenario s = new();
        s.Request.Tenant = Guid.Empty;
        NotifyCommandConsumer consumer = new(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance);
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json", deliveryCount: 1, messageId: "envelope-identity");
        await consumer.Run(message, default);
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
        Assert.AreEqual(0, s.Store.Repository.Invocations.Count);
        Assert.AreEqual(0, s.Store.Files.Invocations.Count);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public async Task ConsumerWithoutEnvelopeIdentity_NeverSendsOrRegisters(string? identity)
    {
        Scenario s = new();
        NotifyCommandConsumer consumer = new(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance);
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json", deliveryCount: 1, messageId: identity);
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => consumer.Run(message, default));
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
        Assert.AreEqual(0, s.Store.Repository.Invocations.Count);
        Assert.AreEqual(0, s.Store.Files.Invocations.Count);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task ConsumerPersistenceFailure_PropagatesWithoutLegacyFallback()
    {
        Scenario s = new();
        s.Store.Repository.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic storage failure"));
        NotifyCommandConsumer consumer = new(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance);
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json", deliveryCount: 1, messageId: "envelope-identity");
        await Assert.ThrowsExactlyAsync<IOException>(() => consumer.Run(message, default));
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(10)]
    public async Task UntrackedRedelivery_HoldsUnknownOutcomeWithoutRenderingOrSending(int deliveryCount)
    {
        Scenario s = new();
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json",
            deliveryCount: deliveryCount, messageId: s.Request.ID);
        await new NotifyCommandConsumer(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance).Run(message, default);
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.SubmissionUnknown, s.Store.Entry.OutcomeCode);
        Assert.AreEqual(0, s.Store.Entry.Attempts);
        Assert.IsNull(s.Store.Entry.FirstSubmission);
        Assert.IsNull(s.Store.Entry.NextAttempt);
        Assert.IsNull(s.Store.Entry.ProviderID);
        s.Clock.Now += TimeSpan.FromDays(30);
        await s.Run(); // Even a reset envelope cannot bypass the persisted hold.
        Assert.AreEqual(1, s.Store.Creates);
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
        Assert.AreEqual(0, s.Templates.Invocations.Count);
        Assert.AreEqual(0, s.Store.Files.Invocations.Count);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task FirstRegistrationFailure_WithoutRow_HoldsOnBrokerRedelivery()
    {
        Scenario s = new();
        s.Store.MissingAfterCreate = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(s.Run);
        Assert.IsNull(s.Store.Entry);
        Assert.AreEqual(0, s.Sends);
        int rendered = s.Templates.Invocations.Count;
        int blobs = s.Store.Payloads.Count;
        s.Store.MissingAfterCreate = false;
        await s.Flow().RunAsync(s.Request, isFirstDelivery: false);
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.SubmissionUnknown, s.Store.Entry.OutcomeCode);
        Assert.AreEqual(0, s.Store.Entry.Attempts);
        Assert.AreEqual(rendered, s.Templates.Invocations.Count);
        Assert.AreEqual(blobs, s.Store.Payloads.Count);
        Assert.AreEqual(0, s.Sends);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task UntrackedRedelivery_RequiresVerifiedHoldBeforeCompletion()
    {
        Scenario s = new();
        s.Store.MissingAfterCreate = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Flow().RunAsync(s.Request, isFirstDelivery: false));
        Assert.IsNull(s.Store.Entry);
        s.Store.MissingAfterCreate = false;
        await s.Flow().RunAsync(s.Request, isFirstDelivery: false);
        Assert.AreEqual((int)SubmissionState.Held, s.Store.Entry!.StateCode);
        Assert.AreEqual(0, s.Provider.Invocations.Count);
        Assert.AreEqual(0, s.Templates.Invocations.Count);
        Assert.AreEqual(0, s.Store.Files.Invocations.Count);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task RegisteredPendingRedelivery_ContinuesUsingDurableEvidence()
    {
        Scenario s = new();
        NotificationSubmission identity = NotificationSubmission.Create(s.Request);
        NotificationSubmissionDomain domain = await s.Store.Domains().GetAsync(identity.Tenant, identity.ID);
        await domain.RegisterAsync(identity, new FrozenNotification("already-frozen", "example.invalid", "synthetic-digest"));
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json",
            deliveryCount: 2, messageId: s.Request.ID);
        await new NotifyCommandConsumer(s.Flow(), NullLogger<NotifyCommandConsumer>.Instance).Run(message, default);
        Assert.AreEqual((int)SubmissionState.Accepted, s.Store.Entry!.StateCode);
        Assert.AreEqual(1, s.Store.Entry.Attempts);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(0, s.Templates.Invocations.Count);
        var submitted = s.Provider.Invocations.Single(i => i.Method.Name == nameof(IIdempotentNotificationProvider.SubmitAsync));
        Assert.AreEqual("already-frozen", ((FrozenNotification)submitted.Arguments[0]).Json);
        Assert.HasCount(1, s.Scheduled);
    }

    [TestMethod]
    public void Fingerprint_SortsObjectKeysButPreservesArrayOrderAndNulls()
    {
        Scenario s = new();
        s.Request.Parameters = new() { ["b"] = new[] { "one", "two" }, ["a"] = "" };
        string first = NotificationSubmission.Create(s.Request).Fingerprint;
        s.Request.Parameters = new() { ["a"] = "", ["b"] = new[] { "one", "two" } };
        Assert.AreEqual(first, NotificationSubmission.Create(s.Request).Fingerprint);
        s.Request.Parameters["b"] = new[] { "two", "one" };
        Assert.AreNotEqual(first, NotificationSubmission.Create(s.Request).Fingerprint);
        s.Request.Parameters["a"] = null!;
        Assert.AreNotEqual(first, NotificationSubmission.Create(s.Request).Fingerprint);
    }

    [TestMethod]
    public void Backoff_IsBoundedAndJittered()
    {
        NotificationRetryPolicy policy = new();
        foreach (int attempt in new[] { 1, 2, 10, 48, Int32.MaxValue })
        {
            TimeSpan[] delays = Enumerable.Range(0, 10).Select(_ => policy.Delay(attempt)).ToArray();
            Assert.IsTrue(delays.All(d => d >= TimeSpan.FromMinutes(1) && d <= TimeSpan.FromHours(1)));
            if (attempt > 1) Assert.IsTrue(delays.Distinct().Count() > 1);
        }
    }
}
