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
                    body: new BinaryData(JsonMarshaller.Marshall(command)), contentType: "application/json", messageId: envelope);
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
}
