using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;
using Niobium.Platform.Notification.Email.Resend;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class ReceiptRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static async Task<NotificationSubmission> RegisterAsync(LocalStorageFixture storage)
    {
        NotificationSubmission identity = NotificationSubmission.Create(new NotifyCommand
        {
            Tenant = Guid.NewGuid(), ID = "synthetic-original-operation", Channel = "Notice", Parameters = [],
        });
        using HttpClient client = new();
        ResendSubmissionProvider provider = new(client, Options.Create(new ResendServiceOptions { GlobalAPIKey = "synthetic-provider-key", DomainScopedAPIKeys = [] }), TimeProvider.System);
        FrozenNotification payload = provider.Freeze(new TemplateDomain.Deliverable
        {
            From = "sender@example.invalid", To = "recipient@example.invalid", Subject = "Synthetic", Body = "Synthetic body",
        }, identity.GetReceiptCorrelation());
        NotificationSubmissionDomain domain = storage.Domain(identity.Tenant, identity.ID);
        await domain.RegisterAsync(identity, payload);
        await domain.PrepareAttemptAsync(identity, Now.AddSeconds(-1), new NotificationRetryPolicy());
        return (await storage.Repository().RetrieveAsync(identity.Tenant, identity.ID))!;
    }

    private static CaptureNotificationReceiptCommand Command(NotificationSubmission submission, string eventID = "synthetic-event", NotificationDeliveryKind kind = NotificationDeliveryKind.Delivered)
        => CaptureNotificationReceiptCommand.Capture("resend", eventID, "synthetic-provider-message", kind, Now, Now.AddSeconds(1),
            System.Text.Encoding.UTF8.GetBytes(eventID), submission.GetReceiptCorrelation());

    [TestMethod]
    public async Task NewRegistration_PreservesModelOwnedProofInStoredRowAndFrozenProviderTags()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmission saved = await RegisterAsync(storage);
        Assert.AreEqual("synthetic-original-operation", saved.Operation);
        Assert.AreEqual("Notice", saved.Channel);
        Assert.IsTrue(saved.MatchesReceiptCorrelation(saved.GetReceiptCorrelation()));
        using Stream content = await storage.Files().GetAsync(NotificationSubmission.PayloadContainer, saved.PayloadName, createIfNotExist: false)
            ?? throw new AssertFailedException("Missing frozen fixture.");
        FrozenNotification payload = await FrozenNotification.ReadAsync(content);
        using JsonDocument json = JsonDocument.Parse(payload.Json);
        Dictionary<string, string> tags = json.RootElement.GetProperty("tags").EnumerateArray()
            .ToDictionary(tag => tag.GetProperty("name").GetString()!, tag => tag.GetProperty("value").GetString()!);
        Assert.AreEqual(saved.Tenant, tags["niobium_tenant"]);
        Assert.AreEqual(saved.ID, tags["niobium_submission"]);
        Assert.AreEqual(saved.ReceiptProof, tags["niobium_proof"]);
        TableEntity row = (await storage.Table.GetEntityAsync<TableEntity>(saved.Tenant, saved.ID)).Value;
        Assert.AreEqual(saved.ReceiptProof, row.GetString(nameof(NotificationSubmission.ReceiptProof)));
        Assert.IsFalse(row.ContainsKey(nameof(NotificationSubmission.Tenant)));
    }

    [TestMethod]
    public async Task EarlyReceipt_PersistsAcrossFailedPublicationAndRecreatedDomains()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmission submission = await RegisterAsync(storage);
        CaptureNotificationReceiptCommand command = Command(submission);
        Mock<IMessagingBroker<NotificationDeliveryEvent>> events = new(MockBehavior.Strict);
        events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic publication failure"));
        CaptureNotificationReceiptFlow Flow() => new(storage.ReceiptDomains(), storage.Domains(), events.Object, TimeProvider.System);
        await Assert.ThrowsExactlyAsync<IOException>(() => Flow().RunAsync(command));
        NotificationReceipt identity = NotificationReceipt.Create(command);
        NotificationReceipt pending = (await storage.ReceiptRepository().RetrieveAsync(identity.Provider, identity.ID))!;
        Assert.AreEqual((int)ReceiptPublication.Pending, pending.PublicationCode);
        Assert.AreEqual(submission.ReceiptProof, pending.CorrelationProof);
        Assert.IsFalse(String.IsNullOrWhiteSpace(pending.ETag));
        List<NotificationDeliveryEvent> published = [];
        events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>, CancellationToken>((entries, _) => published.AddRange(entries.Select(entry => entry.Value!)))
            .Returns(Task.CompletedTask);
        await Flow().RunAsync(command);
        await Flow().RunAsync(command);
        Assert.HasCount(1, published);
        Assert.AreEqual(submission.Operation, published[0].NotificationID);
        TableEntity raw = (await storage.ReceiptTable.GetEntityAsync<TableEntity>(identity.Provider, identity.ID)).Value;
        Assert.AreEqual("resend", raw.PartitionKey);
        Assert.AreEqual((int)ReceiptPublication.Published, raw.GetInt32(nameof(NotificationReceipt.PublicationCode)));
        Assert.IsFalse(raw.ContainsKey(nameof(NotificationReceipt.Provider)));
        Assert.IsFalse(raw.ContainsKey(nameof(NotificationReceipt.ID)));
        NotificationSubmission unchanged = (await storage.Repository().RetrieveAsync(submission.Tenant, submission.ID))!;
        Assert.AreEqual(submission, unchanged);
    }

    [TestMethod]
    public async Task DistinctOutOfOrderReceipts_PreserveEachFactWithoutOverwritingHistory()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmission submission = await RegisterAsync(storage);
        List<NotificationDeliveryEvent> published = [];
        Mock<IMessagingBroker<NotificationDeliveryEvent>> events = new(MockBehavior.Strict);
        events.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<NotificationDeliveryEvent>>, CancellationToken>((entries, _) => published.AddRange(entries.Select(entry => entry.Value!)))
            .Returns(Task.CompletedTask);
        CaptureNotificationReceiptFlow flow = new(storage.ReceiptDomains(), storage.Domains(), events.Object, TimeProvider.System);
        await flow.RunAsync(Command(submission, "delivered", NotificationDeliveryKind.Delivered));
        CaptureNotificationReceiptCommand delayed = CaptureNotificationReceiptCommand.Capture("resend", "delayed", "synthetic-provider-message",
            NotificationDeliveryKind.Delayed, Now.AddSeconds(-1), Now.AddSeconds(2), "delayed"u8, submission.GetReceiptCorrelation());
        await flow.RunAsync(delayed);
        List<TableEntity> rows = [];
        await foreach (TableEntity row in storage.ReceiptTable.QueryAsync<TableEntity>()) rows.Add(row);
        Assert.HasCount(2, rows);
        Assert.HasCount(2, published);
        Assert.AreEqual(NotificationDeliveryKind.Delivered, published[0].Kind);
        Assert.AreEqual(NotificationDeliveryKind.Delayed, published[1].Kind);
        Assert.IsTrue(published[1].OccurredAt < published[0].OccurredAt);
        Assert.AreNotEqual(published[0].ID, published[1].ID);
    }

    [TestMethod]
    public async Task CompetingPublicationAcknowledgement_RejectsStaleVersion()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmission submission = await RegisterAsync(storage);
        CaptureNotificationReceiptCommand command = Command(submission);
        NotificationReceipt identity = NotificationReceipt.Create(command);
        NotificationReceiptDomain first = await storage.ReceiptDomains().GetAsync(identity.Provider, identity.ID);
        await first.RegisterAsync(command);
        NotificationReceiptDomain stale = await storage.ReceiptDomains().GetAsync(identity.Provider, identity.ID, forceLoad: true);
        await stale.GetEntityAsync();
        await first.MarkPublishedAsync(Now);
        RequestFailedException failure = await Assert.ThrowsExactlyAsync<RequestFailedException>(() => stale.MarkPublishedAsync(Now));
        Assert.AreEqual(412, failure.Status);
        Assert.AreEqual((int)ReceiptPublication.Published, (await storage.ReceiptRepository().RetrieveAsync(identity.Provider, identity.ID))!.PublicationCode);
    }
}
