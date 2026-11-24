using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class NotificationReceiptTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private sealed class ReceiptStore
    {
        internal NotificationReceipt? Entry;
        internal bool LosePublishedResponse;
        internal bool ConflictOnUpdate;
        internal Action? AfterWrite;
        internal int Creates;
        internal readonly Mock<IRepository<NotificationReceipt>> Repository = new(MockBehavior.Strict);
        private int version;

        internal ReceiptStore()
        {
            Repository.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(() => Entry);
            Repository.Setup(r => r.CreateAsync(It.IsAny<IEnumerable<NotificationReceipt>>(), false, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<NotificationReceipt> entries, bool _, DateTimeOffset? _, CancellationToken _) =>
                {
                    if (Entry != null) throw new IOException("Synthetic concurrent insert");
                    Creates++;
                    NotificationReceipt value = entries.Single();
                    Entry = value with { ETag = (++version).ToString() };
                    AfterWrite?.Invoke();
                    return new[] { value with { ETag = null } };
                });
            Repository.Setup(r => r.UpdateAsync(It.IsAny<IEnumerable<NotificationReceipt>>(), true, false, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<NotificationReceipt> entries, bool _, bool _, CancellationToken _) =>
                {
                    NotificationReceipt value = entries.Single();
                    if (ConflictOnUpdate || value.ETag != Entry?.ETag) throw new IOException("Synthetic stale version");
                    if (LosePublishedResponse && value.PublicationCode == (int)ReceiptPublication.Published)
                        throw new IOException("Synthetic publication persistence failure");
                    Entry = value with { ETag = (++version).ToString() };
                    AfterWrite?.Invoke();
                    return new[] { value with { ETag = null } };
                });
        }

        internal NotificationReceiptDomain Domain() => new(new(() => Repository.Object), []);
        internal IDomainRepository<NotificationReceiptDomain, NotificationReceipt> Domains()
            => new GenericDomainRepository<NotificationReceiptDomain, NotificationReceipt>(Domain, new(() => Repository.Object));
    }

    private sealed class Scenario
    {
        internal readonly ReceiptStore Receipts = new();
        internal readonly DurableNotificationTests.MemoryStore Submissions = new();
        internal readonly Mock<IMessagingBroker<NotificationDeliveryEvent>> Events = new(MockBehavior.Strict);
        internal readonly List<NotificationDeliveryEvent> Published = [];
        internal readonly NotificationSubmission Identity = NotificationSubmission.Create(new NotifyCommand
        {
            Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), ID = "original-business-operation", Channel = "Notice", Parameters = [],
        });

        internal Scenario()
        {
            Submissions.Entry = Identity with { ETag = "original", StateCode = (int)SubmissionState.Sending,
                OutcomeCode = (int)SubmissionOutcome.SubmissionUnknown, FirstSubmission = Now.AddSeconds(-1), Attempts = 1, NextAttempt = Now.AddMinutes(1) };
            Events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>, CancellationToken>((entries, _) => Published.AddRange(entries.Select(entry => entry.Value!)))
                .Returns(Task.CompletedTask);
        }

        internal CaptureNotificationReceiptCommand Command(NotificationCorrelation? correlation = null, string eventID = "provider-event", string providerID = "provider-message", string body = "verified bytes")
            => CaptureNotificationReceiptCommand.Capture("resend", eventID, providerID, NotificationDeliveryKind.Delivered,
                Now, Now.AddSeconds(1), System.Text.Encoding.UTF8.GetBytes(body), correlation ?? Identity.GetReceiptCorrelation());
        internal CaptureNotificationReceiptFlow Flow() => new(Receipts.Domains(), Submissions.Domains(), Events.Object, new DurableNotificationTests.Clock { Now = Now });
    }

    [TestMethod]
    public async Task EarlyReceipt_CorrelatesFrozenProofBeforeProviderOutcomeWrite_AndDeduplicatesReplay()
    {
        Scenario s = new();
        NotificationSubmission before = s.Submissions.Entry!;
        CaptureNotificationReceiptCommand command = s.Command();
        await s.Flow().RunAsync(command);
        await s.Flow().RunAsync(command);
        Assert.HasCount(1, s.Published);
        Assert.AreEqual(1, s.Receipts.Creates);
        Assert.AreEqual((int)ReceiptPublication.Published, s.Receipts.Entry!.PublicationCode);
        Assert.AreEqual(before, s.Submissions.Entry);
        NotificationDeliveryEvent delivery = s.Published.Single();
        Assert.AreEqual("original-business-operation", delivery.NotificationID);
        Assert.AreEqual(s.Identity.ID, delivery.SubmissionID);
        Assert.AreEqual(Guid.ParseExact(s.Identity.Tenant, "N"), delivery.Tenant);
        Assert.AreEqual("Notice", delivery.Channel);
        Assert.AreEqual(NotificationDeliveryKind.Delivered, delivery.Kind);
        Assert.AreEqual(Now, delivery.OccurredAt);
        Assert.IsFalse(JsonMarshaller.Marshall(delivery).Contains(s.Identity.ReceiptProof!));
    }

    [TestMethod]
    public async Task FailedPublication_LeavesPendingDurableEvidenceForQueueRedelivery()
    {
        Scenario s = new();
        s.Events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic unknown broker response"));
        await Assert.ThrowsExactlyAsync<IOException>(() => s.Flow().RunAsync(s.Command()));
        Assert.AreEqual((int)ReceiptPublication.Pending, s.Receipts.Entry!.PublicationCode);
        Assert.IsNull(s.Receipts.Entry.PublishedAt);
        s.Events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await s.Flow().RunAsync(s.Command());
        Assert.AreEqual((int)ReceiptPublication.Published, s.Receipts.Entry.PublicationCode);
        Assert.AreEqual(1, s.Receipts.Creates);
    }

    [TestMethod]
    public async Task LostCompletion_RepeatsSameEventIdentity_WithoutRepeatingProviderSend()
    {
        Scenario s = new();
        s.Receipts.LosePublishedResponse = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => s.Flow().RunAsync(s.Command()));
        s.Receipts.LosePublishedResponse = false;
        await s.Flow().RunAsync(s.Command());
        Assert.HasCount(2, s.Published);
        Assert.AreEqual(s.Published[0].ID, s.Published[1].ID);
        Assert.AreEqual(s.Published[0].ProviderEventID, s.Published[1].ProviderEventID);
        Assert.AreEqual(0, s.Submissions.Creates);
        Assert.AreEqual(1, s.Submissions.Entry!.Attempts);
    }

    [TestMethod]
    [DataRow("proof", ReceiptHoldReason.CorrelationMismatch)]
    [DataRow("tenant", ReceiptHoldReason.CorrelationMismatch)]
    [DataRow("submission", ReceiptHoldReason.CorrelationMismatch)]
    [DataRow("missing", ReceiptHoldReason.SubmissionMissing)]
    [DataRow("provider", ReceiptHoldReason.ProviderMessageMismatch)]
    [DataRow("legacy", ReceiptHoldReason.LegacyOperationMissing)]
    public async Task UnmatchedReceipt_IsRetainedWithoutPublishing(string mismatch, ReceiptHoldReason reason)
    {
        Scenario s = new();
        NotificationCorrelation correlation = s.Identity.GetReceiptCorrelation();
        if (mismatch == "proof") correlation = correlation with { Proof = "anotherproof" };
        if (mismatch == "tenant") correlation = correlation with { Tenant = "other" };
        if (mismatch == "submission") correlation = correlation with { SubmissionID = "other" };
        if (mismatch == "missing") s.Submissions.Entry = null;
        if (mismatch == "provider") s.Submissions.Entry = s.Submissions.Entry! with { ProviderID = "another-provider-message" };
        if (mismatch == "legacy") s.Submissions.Entry = s.Submissions.Entry! with { FirstSubmission = null };
        CaptureNotificationReceiptCommand command = s.Command(correlation);
        await s.Flow().RunAsync(command);
        await s.Flow().RunAsync(command);
        Assert.HasCount(0, s.Published);
        Assert.AreEqual((int)ReceiptPublication.Uncorrelated, s.Receipts.Entry!.PublicationCode);
        Assert.AreEqual((int)reason, s.Receipts.Entry.HoldReasonCode);
        Assert.AreEqual(1, s.Receipts.Creates);
    }

    [TestMethod]
    public async Task LegacyReceiptWithoutTags_IsPreservedAsUnknownCorrelation()
    {
        Scenario s = new();
        CaptureNotificationReceiptCommand command = CaptureNotificationReceiptCommand.Capture("resend", "legacy-event", "provider-message",
            NotificationDeliveryKind.Delivered, Now, Now, "legacy"u8, null);
        await s.Flow().RunAsync(command);
        Assert.HasCount(0, s.Published);
        Assert.AreEqual((int)ReceiptHoldReason.MissingCorrelation, s.Receipts.Entry!.HoldReasonCode);
        s.Submissions.Repository.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ReusedEventIDWithChangedBody_FailsWithoutRepublishing()
    {
        Scenario s = new();
        await s.Flow().RunAsync(s.Command());
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => s.Flow().RunAsync(s.Command(body: "changed event")));
        Assert.HasCount(1, s.Published);
    }

    [TestMethod]
    public async Task ChangedReadbackAndStaleETag_NeverForcePublicationState()
    {
        Scenario s = new();
        s.Receipts.AfterWrite = () => s.Receipts.Entry = s.Receipts.Entry! with { WriteID = "other-writer" };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => s.Flow().RunAsync(s.Command()));
        Assert.HasCount(0, s.Published);
        s.Receipts.AfterWrite = null;
        s.Receipts.ConflictOnUpdate = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => s.Flow().RunAsync(s.Command()));
        Assert.AreEqual((int)ReceiptPublication.Pending, s.Receipts.Entry!.PublicationCode);
    }

    [TestMethod]
    public void BusinessNamedReceiptKeys_MapToPhysicalColumns()
    {
        var map = EntityMappingHelper.GetMapping(typeof(NotificationReceipt));
        Assert.AreEqual(nameof(NotificationReceipt.Provider), map["PartitionKey"].Name);
        Assert.AreEqual(nameof(NotificationReceipt.ID), map["RowKey"].Name);
        Assert.IsFalse(map.ContainsKey(nameof(NotificationReceipt.Provider)));
        Assert.IsFalse(map.ContainsKey(nameof(NotificationReceipt.ID)));
        Scenario s = new();
        Assert.AreEqual(NotificationReceipt.Create(s.Command()).ID, NotificationReceipt.Create(s.Command()).ID);
        Assert.AreNotEqual(NotificationReceipt.Create(s.Command()).ID, NotificationReceipt.Create(s.Command(eventID: "other")).ID);
    }

    [TestMethod]
    public async Task InvalidReceiptRepresentation_FailsBeforeAnyRepositoryRead()
    {
        Scenario s = new();
        foreach (CaptureNotificationReceiptCommand command in new[]
        {
            new CaptureNotificationReceiptCommand(),
            CaptureNotificationReceiptCommand.Capture("untrusted-provider", "event", "message", NotificationDeliveryKind.Delivered, Now, Now, "body"u8, null),
            CaptureNotificationReceiptCommand.Capture("resend", "event", "message", (NotificationDeliveryKind)99, Now, Now, "body"u8, null),
            CaptureNotificationReceiptCommand.Capture("resend", "event", "message", NotificationDeliveryKind.Delivered, Now, Now, "body"u8, new("invalid/partition", "id", "proof")),
        })
            await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => s.Flow().RunAsync(command));
        s.Receipts.Repository.VerifyNoOtherCalls();
        s.Submissions.Repository.VerifyNoOtherCalls();
        s.Events.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task UnsupportedStoredState_IsNotSilentlyAcknowledged()
    {
        Scenario s = new();
        s.Receipts.Entry = NotificationReceipt.Create(s.Command()) with { ETag = "original", PublicationCode = 99 };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => s.Flow().RunAsync(s.Command()));
        Assert.HasCount(0, s.Published);
    }

    [TestMethod]
    public async Task ReceiptConsumer_RequiresStableEnvelopeAndExecutesThePersistedFlow()
    {
        Scenario s = new();
        var consumer = new Niobium.Notification.Server.CaptureNotificationReceiptConsumer(s.Flow());
        CaptureNotificationReceiptCommand command = s.Command();
        string body = JsonMarshaller.Marshall(command);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => consumer.Run(
            Azure.Messaging.ServiceBus.ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(body), messageId: "another-id", contentType: "application/json"), default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => consumer.Run(
            Azure.Messaging.ServiceBus.ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData("invalid"), messageId: command.ID, contentType: "application/json"), default));
        Assert.AreEqual(0, s.Receipts.Creates);
        await consumer.Run(Azure.Messaging.ServiceBus.ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(body), messageId: command.ID, contentType: "application/json"), default);
        Assert.HasCount(1, s.Published);
        NotificationDeliveryEvent roundtrip = JsonMarshaller.Unmarshall<NotificationDeliveryEvent>(JsonMarshaller.Marshall(s.Published[0]))!;
        Assert.AreEqual(s.Published[0].ID, roundtrip.ID);
        Assert.AreEqual(s.Published[0].Tenant, roundtrip.Tenant);
        Assert.AreEqual(s.Published[0].Kind, roundtrip.Kind);
        Assert.AreEqual(s.Published[0].OccurredAt, roundtrip.OccurredAt);
    }
}
