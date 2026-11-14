using Azure;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server;
using Niobium.Platform.Notification.Email;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class MandatorySubmissionTests
{
    [TestMethod]
    public async Task QueueConsumer_IsolatesSharedEnvelopeAcrossTenants_AndSuppressesRestartedReplays()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        const string envelope = "shared-operation";
        List<(NotifyCommand Command, NotificationSubmission Identity)> operations = [];
        foreach (Guid tenant in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            NotifyCommand command = new()
            {
                ID = envelope, Tenant = tenant, Channel = "Notice",
                Destination = "recipient@example.invalid", Parameters = new() { ["name"] = "Example" },
            };
            NotificationSubmission identity = NotificationSubmission.Create(command);
            await storage.Domain(identity.Tenant, identity.ID).RegisterAsync(identity,
                new FrozenNotification("frozen-json", "example.invalid", "synthetic-digest"));
            command.ID = "body-identity-is-not-authoritative";
            operations.Add((command, identity));
        }

        Mock<IEmailNotificationClient> legacy = new(MockBehavior.Strict);
        Mock<IDomainRepository<TemplateDomain, Template>> templates = new(MockBehavior.Strict);
        NotificationFlow renderer = new(templates.Object, legacy.Object, NullLogger<NotificationFlow>.Instance);
        List<string> submittedKeys = [];
        Mock<IIdempotentNotificationProvider> provider = new(MockBehavior.Strict);
        provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<FrozenNotification, string, DateTimeOffset, CancellationToken>((content, key, _, _) =>
            {
                Assert.AreEqual("frozen-json", content.Json);
                submittedKeys.Add(key);
            })
            .ReturnsAsync(new SubmissionResult(SubmissionOutcome.Accepted, "synthetic-provider-id"));
        List<MessagingEntry<NotifyCommand>> scheduled = [];
        Mock<IMessagingBroker<NotifyCommand>> queue = new(MockBehavior.Strict);
        queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) => scheduled.AddRange(entries))
            .Returns(Task.CompletedTask);

        // Rebuild the consumer and repositories for each delivery, including replays.
        for (int delivery = 0; delivery < 2; delivery++)
        {
            foreach (var (command, identity) in operations)
            {
                DurableNotificationFlow flow = new(renderer, storage.Domains(), provider.Object, queue.Object,
                    TimeProvider.System, new NotificationRetryPolicy());
                NotifyCommandConsumer consumer = new(flow, NullLogger<NotifyCommandConsumer>.Instance);
                ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                    body: new BinaryData(JsonMarshaller.Marshall(command)), contentType: "application/json", deliveryCount: delivery + 1, messageId: envelope);
                await consumer.Run(message, default);
                NotificationSubmission stored = (await storage.Repository().RetrieveAsync(identity.Tenant, identity.ID))!;
                Assert.AreEqual((int)SubmissionState.Accepted, stored.StateCode);
                Assert.AreEqual(1, stored.Attempts);
                Assert.AreEqual(identity.Tenant, stored.Tenant);
                Assert.AreEqual(identity.ID, stored.ID);
            }
        }

        Assert.AreEqual(operations[0].Identity.ID, operations[1].Identity.ID);
        Assert.HasCount(2, submittedKeys);
        Assert.AreEqual(2, submittedKeys.Distinct().Count());
        Assert.HasCount(2, scheduled);
        Assert.IsTrue(scheduled.All(wake => wake.ID == envelope && wake.Value.ID == envelope));
        Assert.AreEqual(0, legacy.Invocations.Count);
        Assert.AreEqual(0, templates.Invocations.Count);
    }

    [TestMethod]
    public async Task UntrackedRedelivery_PersistsHoldAcrossRestart_WithoutExternalWork()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotifyCommand command = new()
        {
            ID = "legacy-uncertain", Tenant = Guid.NewGuid(), Channel = "Notice",
            Destination = "recipient@example.invalid", Parameters = [],
        };
        Mock<IEmailNotificationClient> legacy = new(MockBehavior.Strict);
        Mock<IDomainRepository<TemplateDomain, Template>> templates = new(MockBehavior.Strict);
        NotificationFlow renderer = new(templates.Object, legacy.Object, NullLogger<NotificationFlow>.Instance);
        Mock<IIdempotentNotificationProvider> provider = new(MockBehavior.Strict);
        Mock<IMessagingBroker<NotifyCommand>> queue = new(MockBehavior.Strict);
        foreach (int count in new[] { 2, 3, 1 })
        {
            DurableNotificationFlow flow = new(renderer, storage.Domains(), provider.Object, queue.Object,
                TimeProvider.System, new NotificationRetryPolicy());
            ServiceBusReceivedMessage message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: new BinaryData(JsonMarshaller.Marshall(command)), contentType: "application/json",
                deliveryCount: count, messageId: command.ID);
            await new NotifyCommandConsumer(flow, NullLogger<NotifyCommandConsumer>.Instance).Run(message, default);
        }
        NotificationSubmission identity = NotificationSubmission.Create(command);
        NotificationSubmission held = (await storage.Repository().RetrieveAsync(identity.Tenant, identity.ID))!;
        Assert.AreEqual((int)SubmissionState.Held, held.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.SubmissionUnknown, held.OutcomeCode);
        Assert.AreEqual(0, held.Attempts);
        Assert.IsNull(held.FirstSubmission);
        Assert.IsNull(held.NextAttempt);
        Assert.IsFalse((await storage.Container.ExistsAsync()).Value);
        Assert.AreEqual(0, provider.Invocations.Count);
        Assert.AreEqual(0, queue.Invocations.Count);
        Assert.AreEqual(0, templates.Invocations.Count);
        Assert.AreEqual(0, legacy.Invocations.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task HoldAndFreshRegistrationRace_PreserveConditionalWinner(bool holdWins)
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmission identity = NotificationSubmission.Create(new NotifyCommand
        {
            ID = "registration-race", Tenant = Guid.NewGuid(), Channel = "Notice", Parameters = [],
        });
        IRepository<NotificationSubmission> repository = storage.Repository();
        Mock<IRepository<NotificationSubmission>> barrier = new(MockBehavior.Strict);
        barrier.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Returns((string tenant, string id, IList<string>? _, CancellationToken token) =>
                repository.RetrieveAsync(tenant, id, cancellationToken: token));
        TaskCompletionSource bothCreating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource winnerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int creates = 0;
        barrier.Setup(r => r.CreateAsync(It.IsAny<IEnumerable<NotificationSubmission>>(), false, null, It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<NotificationSubmission> values, bool _, DateTimeOffset? _, CancellationToken token) =>
            {
                NotificationSubmission candidate = values.Single();
                int arrived = Interlocked.Increment(ref creates);
                if (arrived == 2) bothCreating.TrySetResult();
                await bothCreating.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                bool winsInsert = (candidate.StateCode == (int)SubmissionState.Held) == holdWins;
                if (!winsInsert) await winnerCreated.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                // The pinned adapter executes both conditional inserts; only order is controlled.
                IEnumerable<NotificationSubmission> result = await repository.CreateAsync(values, cancellationToken: token);
                if (winsInsert) winnerCreated.TrySetResult();
                return result;
            });
        NotificationSubmissionDomain uncertain = storage.Domain(identity.Tenant, identity.ID, barrier.Object);
        NotificationSubmissionDomain fresh = storage.Domain(identity.Tenant, identity.ID, barrier.Object);
        Exception?[] results = await Task.WhenAll(
            Capture(() => uncertain.PrepareRegistrationAsync(identity, isFirstDelivery: false)),
            Capture(() => fresh.RegisterAsync(identity, new FrozenNotification("frozen-json", "example.invalid", "synthetic-digest"))));
        Assert.AreEqual(1, results.Count(result => result == null));
        RequestFailedException conflict = (RequestFailedException)results.Single(result => result != null)!;
        Assert.AreEqual(409, conflict.Status);
        NotificationSubmission winner = (await storage.Repository().RetrieveAsync(identity.Tenant, identity.ID))!;
        Assert.AreEqual((int)(holdWins ? SubmissionState.Held : SubmissionState.Pending), winner.StateCode);
        NotificationSubmissionDomain restarted = storage.Domain(identity.Tenant, identity.ID);
        bool register = await restarted.PrepareRegistrationAsync(identity, isFirstDelivery: !holdWins);
        Assert.IsFalse(register);
        NotificationSubmission preserved = (await storage.Repository().RetrieveAsync(identity.Tenant, identity.ID))!;
        Assert.AreEqual(winner, preserved);
        SubmissionAttempt next = await restarted.PrepareAttemptAsync(identity, DateTimeOffset.UtcNow, new NotificationRetryPolicy());
        Assert.AreEqual(holdWins ? SubmissionAction.Suppress : SubmissionAction.Submit, next.Action);
        if (!holdWins) Assert.AreEqual("frozen-json", next.Payload!.Json);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }
}
