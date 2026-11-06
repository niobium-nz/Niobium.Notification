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

    internal sealed class MemoryStore : INotificationSubmissionStore
    {
        public NotificationSubmission? Entry { get; set; }
        public FrozenNotification? Payload { get; set; }
        public Func<NotificationSubmission, bool>? RejectUpdate { get; set; }
        public bool LoseCompletion { get; set; }
        public bool MissingAfterCreate { get; set; }
        public int Creates { get; private set; }
        private int version;
        private readonly object gate = new();

        public Task<NotificationSubmission?> ReadAsync(string partition, string id, CancellationToken token) => Task.FromResult(Entry);
        public Task<bool> CreateAsync(NotificationSubmission value, FrozenNotification? payload, CancellationToken token)
        {
            lock (gate)
            {
                Creates++;
                if (Entry != null) return Task.FromResult(false);
                if (!MissingAfterCreate) Entry = value with { ETag = (++version).ToString() };
                Payload = payload;
                return Task.FromResult(true);
            }
        }
        public Task<NotificationSubmission?> UpdateAsync(NotificationSubmission value, CancellationToken token)
        {
            lock (gate)
            {
                if (LoseCompletion && value.State == "accepted") throw new IOException("synthetic storage timeout");
                if (value.ETag != Entry?.ETag || RejectUpdate?.Invoke(value) == true) return Task.FromResult<NotificationSubmission?>(null);
                Entry = value with { ETag = (++version).ToString() };
                return Task.FromResult<NotificationSubmission?>(Entry);
            }
        }
        public Task<FrozenNotification> ReadPayloadAsync(string name, CancellationToken token) => Task.FromResult(Payload!);
    }

    private sealed class Scenario
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

        public Scenario(bool missingTemplate = false)
        {
            TemplateDomain domain = missingTemplate
                ? new TemplateDomain(new Lazy<IRepository<Template>>(() => new Mock<IRepository<Template>>().Object), [], new Mock<IFileService>().Object, HtmlEncoder.Default, Options.Create(DefaultOptions), NullLogger<TemplateDomain>.Instance)
                : CreateDomain(BuildTemplate(Request.Tenant, Request.Channel), "{{NAME}}", DefaultOptions);
            if (missingTemplate) SetDomainKeys(domain, Template.BuildParitionKey(Request.Tenant), Request.Channel);
            (Renderer, Templates, Legacy, _) = CreateSut(domain);
            Provider.Setup(p => p.Freeze(It.IsAny<TemplateDomain.Deliverable>())).Returns(new FrozenNotification("frozen-json", "example.invalid", "credential-digest"));
            Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SubmissionResult(SubmissionResult.Accepted, "provider-1"));
            Queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) => Scheduled.AddRange(entries))
                .Returns(Task.CompletedTask);
        }

        public DurableNotificationFlow Flow() => new(Renderer, Store, Provider.Object, Queue.Object, Clock, new FixedRetry());
        public Task Run() => Flow().RunAsync(Request);
        public int Sends => Provider.Invocations.Count(i => i.Method.Name == nameof(IIdempotentNotificationProvider.SubmitAsync));
        public void Advance() => Clock.Now = Store.Entry!.NextAttempt!.Value;
        public void Outcome(string outcome) => Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubmissionResult(outcome));
    }

    [TestMethod]
    public async Task AcceptedReplay_IsSuppressedAfterProviderWindow_WithoutRerendering()
    {
        Scenario s = new();
        await s.Run();
        Assert.AreEqual("accepted", s.Store.Entry!.State);
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
        s.Outcome(SubmissionResult.Retry);
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
        Assert.AreEqual("sending", s.Store.Entry!.State);
        Assert.HasCount(1, s.Scheduled);
        s.Store.LoseCompletion = false;
        s.Advance();
        await s.Run();
        Assert.AreEqual("accepted", s.Store.Entry.State);
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
        Assert.AreEqual("sending", s.Store.Entry!.State);
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
        response.SetResult(new(SubmissionResult.Accepted, "provider-1"));
        await first;
        Assert.AreEqual("accepted", s.Store.Entry!.State);
    }

    [TestMethod]
    [DataRow(1439)]
    [DataRow(1440)]
    [DataRow(2880)]
    public async Task WindowBoundary_HoldsUnknownOutcome_WithoutAnotherSend(int minutes)
    {
        Scenario s = new(); s.Outcome(SubmissionResult.Retry);
        await s.Run();
        s.Clock.Now += TimeSpan.FromMinutes(minutes);
        await s.Run();
        Assert.AreEqual("held", s.Store.Entry!.State);
        Assert.AreEqual(1, s.Sends);
        await s.Run();
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task HoldConflict_PropagatesForRedeliveryWithoutSending()
    {
        Scenario s = new(); s.Outcome(SubmissionResult.Retry); await s.Run();
        s.Clock.Now += TimeSpan.FromDays(1);
        s.Store.RejectUpdate = e => e.State == "held";
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task AttemptExhaustion_HoldsInsteadOfStartingNewWindow()
    {
        Scenario s = new(); s.Outcome(SubmissionResult.Retry);
        await s.Run();
        s.Store.Entry = s.Store.Entry! with { Attempts = NotificationRetryPolicy.MaximumAttempts };
        s.Advance(); await s.Run();
        Assert.AreEqual("held", s.Store.Entry.State);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    [DataRow("rejected", "failed")]
    [DataRow("hold", "held")]
    public async Task TerminalOutcome_DoesNotResubmit(string outcome, string state)
    {
        Scenario s = new(); s.Outcome(outcome);
        await s.Run(); await s.Run();
        Assert.AreEqual(state, s.Store.Entry!.State);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task RejectionAfterUnknownAttempt_DoesNotEraseUncertainty()
    {
        Scenario s = new(); s.Outcome(SubmissionResult.Retry);
        await s.Run(); s.Advance(); s.Outcome(SubmissionResult.Rejected);
        await s.Run();
        Assert.AreEqual("held", s.Store.Entry!.State);
    }

    [TestMethod]
    public async Task ChangedContentAndUnknownStateOrVersion_FailClosed()
    {
        Scenario s = new(); s.Outcome(SubmissionResult.Retry); await s.Run();
        s.Request.Parameters["name"] = "Changed";
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        s.Request.Parameters["name"] = "First";
        s.Store.Entry = s.Store.Entry! with { Version = 2 };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        s.Store.Entry = s.Store.Entry with { Version = 1, State = "future-state" };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
    }

    [TestMethod]
    public async Task MissingTemplate_PersistsExplicitFailureWithoutSending()
    {
        Scenario s = new(true);
        await s.Run(); await s.Run();
        Assert.AreEqual("failed", s.Store.Entry!.State);
        Assert.AreEqual("template_missing", s.Store.Entry.Outcome);
        Assert.AreEqual(0, s.Sends);
        Assert.HasCount(0, s.Scheduled);
    }

    [TestMethod]
    public async Task ClaimOrCompletionConflict_PropagatesWithoutOverwrite()
    {
        Scenario s = new(); s.Store.RejectUpdate = _ => true;
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(0, s.Sends);
        s.Store.RejectUpdate = e => e.State == "accepted";
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(s.Run);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual("sending", s.Store.Entry!.State);
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
    public async Task EnabledConsumer_UsesEnvelopeIdentityAndDurableProvider()
    {
        Scenario s = new();
        NotifyCommandConsumer consumer = new(s.Renderer, NullLogger<NotifyCommandConsumer>.Instance, s.Flow(), Options.Create(new NotificationDeliveryOptions { Enabled = true }));
        ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(JsonMarshaller.Marshall(s.Request)), contentType: "application/json", messageId: "envelope-identity");
        await consumer.Run(message, default); await consumer.Run(message, default);
        Assert.AreEqual(NotificationFingerprint.Hash("envelope-identity"), s.Store.Entry!.RowKey);
        Assert.AreEqual(1, s.Sends);
        Assert.AreEqual(0, s.Legacy.Invocations.Count);
    }

    [TestMethod]
    public void Fingerprint_SortsObjectKeysButPreservesArrayOrderAndNulls()
    {
        Scenario s = new();
        s.Request.Parameters = new() { ["b"] = new[] { "one", "two" }, ["a"] = "" };
        string first = NotificationFingerprint.Of(s.Request);
        s.Request.Parameters = new() { ["a"] = "", ["b"] = new[] { "one", "two" } };
        Assert.AreEqual(first, NotificationFingerprint.Of(s.Request));
        s.Request.Parameters["b"] = new[] { "two", "one" };
        Assert.AreNotEqual(first, NotificationFingerprint.Of(s.Request));
        s.Request.Parameters["a"] = null!;
        Assert.AreNotEqual(first, NotificationFingerprint.Of(s.Request));
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
