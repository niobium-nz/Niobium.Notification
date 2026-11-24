using Moq;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class NotificationSubmissionDomainTests
{
    private static readonly FrozenNotification Payload = new("frozen-json", "example.invalid", "synthetic-digest");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    private static NotificationSubmissionDomain Domain(DurableNotificationTests.MemoryStore store, string id = "operation")
    {
        NotificationSubmissionDomain domain = store.Domain();
        NotificationSubmission request = Request(id);
        domain.Initialize(request.Tenant, request.ID);
        return domain;
    }

    private static NotificationSubmission Request(string id = "operation", string channel = "Notice")
        => NotificationSubmission.Create(new NotifyCommand
        {
            ID = id, Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Channel = channel, Parameters = [],
        });

    [TestMethod]
    public async Task Registration_FreezesBeforeSave_AndReloadsWriteVersion()
    {
        DurableNotificationTests.MemoryStore store = new();
        store.AfterWrite = () => Assert.HasCount(1, store.Payloads);
        NotificationSubmissionDomain domain = Domain(store);
        await domain.RegisterAsync(Request(), Payload);
        NotificationSubmission persisted = await domain.GetEntityAsync();
        Assert.IsFalse(String.IsNullOrWhiteSpace(persisted.ETag));
        Assert.IsFalse(String.IsNullOrWhiteSpace(persisted.WriteID));
        Assert.AreEqual(NotificationSubmission.CurrentVersion, persisted.Version);
        SubmissionAttempt attempt = await Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy());
        Assert.AreEqual(Payload, attempt.Payload);
        Assert.AreEqual(SubmissionAction.Submit, attempt.Action);
    }

    [TestMethod]
    public async Task Registration_DoesNotImportLifecycleOrPersistenceMetadataFromRequest()
    {
        DurableNotificationTests.MemoryStore store = new();
        NotificationSubmission request = Request() with
        {
            ETag = "stale-version", Timestamp = Now, Attempts = 20, FirstSubmission = Now,
            NextAttempt = Now.AddHours(1), ProviderID = "previous-provider-id",
            StateCode = (int)SubmissionState.Accepted, OutcomeCode = (int)SubmissionOutcome.Accepted,
        };
        await Domain(store).RegisterAsync(request, Payload);
        Assert.AreEqual(1, store.Creates);
        Assert.AreEqual((int)SubmissionState.Pending, store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.None, store.Entry.OutcomeCode);
        Assert.AreEqual(0, store.Entry.Attempts);
        Assert.IsNull(store.Entry.FirstSubmission);
        Assert.IsNull(store.Entry.NextAttempt);
        Assert.IsNull(store.Entry.ProviderID);
        Assert.AreNotEqual(request.PayloadName, store.Entry.PayloadName);
        Assert.AreEqual(request.GetReceiptCorrelation(), store.Entry.GetReceiptCorrelation());
        Assert.AreEqual(request.Operation, store.Entry.Operation);
        Assert.AreEqual(request.Channel, store.Entry.Channel);
    }

    [TestMethod]
    public async Task ExistingRegistration_ValidatesFingerprintWithoutRewriting()
    {
        DurableNotificationTests.MemoryStore store = new();
        await Domain(store).RegisterAsync(Request(), Payload);
        await Domain(store).RegisterAsync(Request(), Payload with { Json = "changed" });
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).RegisterAsync(Request(channel: "Changed"), Payload));
        Assert.AreEqual(1, store.Creates);
        Assert.HasCount(1, store.Payloads);
    }

    [TestMethod]
    public async Task BlobFailure_NeverPersistsAnInboxPointer()
    {
        DurableNotificationTests.MemoryStore store = new();
        store.Files.Setup(f => f.PutAsync(NotificationSubmission.PayloadContainer, It.IsAny<string>(), It.IsAny<Stream>(), false, null, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic blob failure"));
        await Assert.ThrowsExactlyAsync<IOException>(() => Domain(store).RegisterAsync(Request(), Payload));
        Assert.AreEqual(0, store.Creates);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("null")]
    public async Task MissingOrNullPayload_FailsBeforeClaim(string? content)
    {
        DurableNotificationTests.MemoryStore store = new();
        await Domain(store).RegisterAsync(Request(), Payload);
        store.Files.Setup(f => f.GetAsync(NotificationSubmission.PayloadContainer, It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => content == null ? null : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy()));
        Assert.AreEqual(0, store.Entry!.Attempts);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("*")]
    [DataRow("")]
    public async Task Claim_RequiresConditionalVersion(string? etag)
    {
        DurableNotificationTests.MemoryStore store = new();
        await Domain(store).RegisterAsync(Request(), Payload);
        store.Entry = store.Entry! with { ETag = etag };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy()));
        Assert.AreEqual(0, store.Entry.Attempts);
    }

    [TestMethod]
    public async Task ReadBack_MustNotBorrowAnInterveningWritersVersion()
    {
        DurableNotificationTests.MemoryStore store = new();
        await Domain(store).RegisterAsync(Request(), Payload);
        store.AfterWrite = () => store.Entry = store.Entry! with { WriteID = "another-writer", ETag = "another-version" };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy()));
    }

    [TestMethod]
    public async Task RecordOutcome_RequiresAnOwnedClaim_AndRejectsStaleCompletion()
    {
        DurableNotificationTests.MemoryStore store = new();
        NotificationSubmissionDomain domain = Domain(store);
        await domain.RegisterAsync(Request(), Payload);
        SubmissionAttempt claim = await domain.PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy());
        SubmissionResult result = new(SubmissionOutcome.Accepted, "provider-id");
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store, "another-operation").RecordOutcomeAsync(claim, result));
        SubmissionAttempt waiting = await Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => domain.RecordOutcomeAsync(waiting, result));
        await domain.RecordOutcomeAsync(claim, result);
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => domain.RecordOutcomeAsync(claim, result));
    }

    [TestMethod]
    public async Task UnknownOutcomeCode_FailsClosedBeforeProviderWork()
    {
        DurableNotificationTests.MemoryStore store = new();
        await Domain(store).RegisterAsync(Request(), Payload);
        store.Entry = store.Entry! with { OutcomeCode = 999 };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy()));
    }

    [TestMethod]
    public async Task UnexpectedProviderOutcome_IsHeld()
    {
        DurableNotificationTests.MemoryStore store = new();
        NotificationSubmissionDomain domain = Domain(store);
        await domain.RegisterAsync(Request(), Payload);
        SubmissionAttempt claim = await domain.PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy());
        await domain.RecordOutcomeAsync(claim, new SubmissionResult((SubmissionOutcome)999));
        Assert.AreEqual((int)SubmissionState.Held, store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.Hold, store.Entry.OutcomeCode);
    }

    [TestMethod]
    public async Task AcceptedWithoutProviderEvidence_IsHeldAsUnknown()
    {
        DurableNotificationTests.MemoryStore store = new();
        NotificationSubmissionDomain domain = Domain(store);
        await domain.RegisterAsync(Request(), Payload);
        SubmissionAttempt claim = await domain.PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy());
        await domain.RecordOutcomeAsync(claim, new SubmissionResult(SubmissionOutcome.Accepted));
        Assert.AreEqual((int)SubmissionState.Held, store.Entry!.StateCode);
        Assert.AreEqual((int)SubmissionOutcome.SubmissionUnknown, store.Entry.OutcomeCode);
    }

    [TestMethod]
    public async Task MissingIdentityOrCorruptAttempt_CannotStartANewWindow()
    {
        DurableNotificationTests.MemoryStore store = new();
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).RegisterAsync(new NotificationSubmission(), Payload));
        NotificationSubmission invalid = Request();
        EntityMappingHelper.GetMapping(typeof(NotificationSubmission))["Fingerprint"].SetValue(invalid, "");
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).RegisterAsync(invalid, Payload));
        Assert.AreEqual(0, store.Creates);
        await Domain(store).RegisterAsync(Request(), Payload);
        store.Entry = store.Entry! with { Attempts = 1, FirstSubmission = null };
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => Domain(store).PrepareAttemptAsync(Request(), Now, new NotificationRetryPolicy()));
    }
}
