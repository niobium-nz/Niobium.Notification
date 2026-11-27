using Azure.Messaging.ServiceBus;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Deployment.Tests;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class RoutedReceiptObservationTests
{
    [TestMethod]
    public async Task ObservationStartsAfterLargeRetainedBacklogAndReadsOnlyTheOwnedReceipt()
    {
        // Gaps represent expired messages. An ordinal count is not a sequence number.
        long[] retained = Enumerable.Range(1, 1_501).Select(index => 170L + index * 7L).ToArray();
        List<long> probes = [];
        Mock<ServiceBusReceiver> receiver = new(MockBehavior.Strict);
        receiver.Setup(r => r.PeekMessageAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long? start, CancellationToken _) =>
            {
                probes.Add(start!.Value);
                long next = retained.FirstOrDefault(sequence => sequence >= start.Value);
                return next == 0 ? null! : ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: next);
            });

        long position = await RoutedReceiptObservation.CaptureNextSequenceAsync(receiver.Object, CancellationToken.None);

        Assert.AreEqual(retained[^1] + 1, position);
        Assert.IsTrue(probes.Count < 50, "Tail discovery must not scan the retained backlog.");

        Guid tenant = Guid.NewGuid();
        NotifyCommand command = new()
        {
            ID = "owned-fixture-operation", Channel = "ContactUs", Tenant = tenant,
            Destination = "synthetic@example.invalid", Parameters = [],
        };
        NotificationDeliveryEvent receipt = new()
        {
            ID = "resend/owned-delivery", Tenant = tenant, NotificationID = command.ID, Channel = command.Channel,
            SubmissionID = "owned-submission", Provider = "resend", ProviderEventID = "owned-provider-event",
            ProviderMessageID = "owned-provider-message", Kind = NotificationDeliveryKind.Delivered,
            OccurredAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
        };
        MessagingEntry<NotificationDeliveryEvent> entry = new() { ID = receipt.ID, Value = receipt };
        receiver.Setup(r => r.PeekMessagesAsync(100, position, CancellationToken.None))
            .ReturnsAsync((IReadOnlyList<ServiceBusReceivedMessage>)[
                ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: position, messageId: "other-fixture-message"),
                ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString(entry.Body),
                    sequenceNumber: position + 1, messageId: entry.ID, contentType: "application/json"),
            ]);

        await RoutedReceiptObservation.ObserveAsync(receiver.Object, position, receipt.ID, command, receipt.SubmissionID,
            receipt.ProviderMessageID, CancellationToken.None);
        receiver.Verify(r => r.PeekMessagesAsync(100, position, CancellationToken.None), Times.Once);
    }

    [TestMethod]
    public async Task EmptyQueueStartsAtItsFirstSequence()
    {
        Mock<ServiceBusReceiver> receiver = new(MockBehavior.Strict);
        receiver.Setup(r => r.PeekMessageAsync(0, CancellationToken.None)).ReturnsAsync((ServiceBusReceivedMessage)null!);
        Assert.AreEqual(0L, await RoutedReceiptObservation.CaptureNextSequenceAsync(receiver.Object, CancellationToken.None));
    }
}
