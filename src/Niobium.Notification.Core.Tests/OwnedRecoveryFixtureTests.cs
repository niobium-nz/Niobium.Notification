using Azure;
using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Moq;
using Niobium.Notification.Delivery;
using Niobium.Notification.Deployment.Tests;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class OwnedRecoveryFixtureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private const string Sender = "sender@example.invalid";
    private readonly NotifyCommand command = new()
    {
        ID = "deployment-recovery-synthetic", Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Channel = "ContactUs", Destination = "approved@example.invalid", Parameters = new() { ["MESSAGE"] = "Synthetic" },
    };

    private TableEntity Template() => new(command.Tenant.ToString(), command.Channel)
    {
        ["VerificationRun"] = command.Tenant.ToString(), ["FallbackTo"] = command.Destination, ["From"] = Sender,
    };

    private TableEntity Accepted(OwnedRecoveryFixture fixture) => new(fixture.Identity.Tenant, fixture.Identity.ID)
    {
        ETag = new ETag("observed-etag"),
        [nameof(NotificationSubmission.Version)] = 2,
        [nameof(NotificationSubmission.Fingerprint)] = fixture.Identity.Fingerprint,
        [nameof(NotificationSubmission.PayloadName)] = fixture.Identity.PayloadName,
        [nameof(NotificationSubmission.StateCode)] = (int)SubmissionState.Accepted,
        [nameof(NotificationSubmission.OutcomeCode)] = (int)SubmissionOutcome.Accepted,
        [nameof(NotificationSubmission.Attempts)] = 1,
        [nameof(NotificationSubmission.ProviderID)] = "synthetic-provider-id",
        [nameof(NotificationSubmission.FirstSubmission)] = Now.AddMinutes(-1),
        [nameof(NotificationSubmission.WriteID)] = "accepted-write",
        ["FutureCompatibleColumn"] = "preserved",
    };

    private ServiceBusReceivedMessage Wake(string? id = null, ServiceBusMessageState state = ServiceBusMessageState.Scheduled,
        int seconds = 60, long sequence = 42) => ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: id ?? command.ID, serviceBusMessageState: state, scheduledEnqueueTime: Now.AddSeconds(seconds), sequenceNumber: sequence);

    private void WithCorrelation(TableEntity row)
    {
        row[nameof(NotificationSubmission.Version)] = 3;
        row["Operation"] = command.ID;
        row["Channel"] = command.Channel;
        row["ReceiptProof"] = "opaque-model-owned-proof";
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OwnedAcceptance_IsRestoredOnceWithObservedETagAndPreservedFrozenIdentity(bool correlated)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        if (correlated) WithCorrelation(accepted);
        TableEntity? written = null;
        Mock<TableClient> table = new(MockBehavior.Strict);
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<TableEntity, ETag, TableUpdateMode, CancellationToken>((row, _, _, _) => written = row)
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.GetEntityAsync<TableEntity>(fixture.Identity.Tenant, fixture.Identity.ID, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response.FromValue(written!, Mock.Of<Response>()));

        await fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default);

        Assert.IsNotNull(written);
        Assert.AreEqual((int)SubmissionState.Sending, written.GetInt32(nameof(NotificationSubmission.StateCode)));
        Assert.AreEqual((int)SubmissionOutcome.SubmissionUnknown, written.GetInt32(nameof(NotificationSubmission.OutcomeCode)));
        Assert.IsFalse(written.ContainsKey(nameof(NotificationSubmission.ProviderID)));
        Assert.AreNotEqual("accepted-write", written.GetString(nameof(NotificationSubmission.WriteID)));
        foreach (string key in new[] { "PartitionKey", "RowKey", nameof(NotificationSubmission.Fingerprint),
            nameof(NotificationSubmission.Version), nameof(NotificationSubmission.PayloadName), nameof(NotificationSubmission.FirstSubmission),
            nameof(NotificationSubmission.Attempts), "FutureCompatibleColumn" })
        {
            Assert.AreEqual(accepted[key], written[key], key);
        }
        if (correlated)
        {
            foreach (string key in new[] { "Operation", "Channel", "ReceiptProof" })
                Assert.AreEqual(accepted[key], written[key], key);
        }
        Assert.AreEqual((int)SubmissionState.Accepted, accepted.GetInt32(nameof(NotificationSubmission.StateCode)));
        Assert.AreEqual("synthetic-provider-id", accepted.GetString(nameof(NotificationSubmission.ProviderID)));
        table.Verify(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()), Times.Once);
        table.Verify(t => t.GetEntityAsync<TableEntity>(fixture.Identity.Tenant, fixture.Identity.ID, null, It.IsAny<CancellationToken>()), Times.Once);
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("Version", "string")]
    [DataRow("Version", "missing")]
    [DataRow("Version", "future")]
    [DataRow("Operation", "missing")]
    [DataRow("Operation", "other")]
    [DataRow("Channel", "missing")]
    [DataRow("Channel", "other")]
    [DataRow("ReceiptProof", "missing")]
    [DataRow("ReceiptProof", "empty")]
    [DataRow("ReceiptProof", "wrong-type")]
    public async Task InvalidReceiptRepresentation_NeverWrites(string field, string mutation)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        WithCorrelation(accepted);
        if (mutation == "missing") accepted.Remove(field);
        else accepted[field] = mutation switch { "future" => 4, "empty" => " ", "wrong-type" => 42, _ => "other" };
        Mock<TableClient> table = new(MockBehavior.Strict);
        await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default));
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("Operation")]
    [DataRow("Channel")]
    [DataRow("ReceiptProof")]
    public void LegacyVersion_CannotCarryReceiptMetadata(string field)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity row = Accepted(fixture);
        row[field] = "unexpected";
        VerificationFailure error = Assert.ThrowsExactly<VerificationFailure>(() => fixture.ValidateSubmission(row));
        Assert.AreEqual("live_legacy_submission_correlation_invalid", error.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SubmissionVersion_CannotChangeAfterFirstObservation(bool upgrade)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity legacy = Accepted(fixture);
        TableEntity correlated = new(legacy);
        WithCorrelation(correlated);
        fixture.ValidateSubmission(upgrade ? legacy : correlated);
        VerificationFailure error = Assert.ThrowsExactly<VerificationFailure>(() =>
            fixture.ValidateSubmission(upgrade ? correlated : legacy));
        Assert.AreEqual("live_submission_correlation_changed", error.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedCorrelation_OnFaultReadbackOrLaterRecoveryFails(bool afterFaultReadback)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        WithCorrelation(accepted);
        TableEntity? written = null;
        Mock<TableClient> table = new(MockBehavior.Strict);
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .Callback<TableEntity, ETag, TableUpdateMode, CancellationToken>((row, _, _, _) => written = row)
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.GetEntityAsync<TableEntity>(fixture.Identity.Tenant, fixture.Identity.ID, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                TableEntity row = new(written!);
                if (!afterFaultReadback) row["ReceiptProof"] = "changed-proof";
                return Response.FromValue(row, Mock.Of<Response>());
            });
        if (afterFaultReadback)
        {
            await fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default);
            TableEntity recovered = new(accepted) { ["ReceiptProof"] = "changed-proof" };
            VerificationFailure error = Assert.ThrowsExactly<VerificationFailure>(() => fixture.ValidateSubmission(recovered));
            Assert.AreEqual("live_submission_correlation_changed", error.Message);
        }
        else
        {
            VerificationFailure error = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
                fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default));
            Assert.AreEqual("live_submission_correlation_changed", error.Message);
        }
        table.Verify(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()), Times.Once);
        table.Verify(t => t.GetEntityAsync<TableEntity>(fixture.Identity.Tenant, fixture.Identity.ID, null, It.IsAny<CancellationToken>()), Times.Once);
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("PartitionKey", "unrelated-tenant")]
    [DataRow("RowKey", "unrelated-operation")]
    [DataRow("Fingerprint", "unrelated-request")]
    [DataRow("ProviderID", "")]
    [DataRow("PayloadName", "")]
    [DataRow("ETag", "*")]
    [DataRow("ETag", "")]
    [DataRow("FirstSubmission", "remove")]
    [DataRow("Version", "wrong")]
    [DataRow("StateCode", "wrong")]
    [DataRow("OutcomeCode", "wrong")]
    [DataRow("Attempts", "wrong")]
    public async Task UnownedOrIncompleteAcceptance_NeverWrites(string field, string value)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        if (field == "ETag") accepted.ETag = new ETag(value);
        else if (value == "remove") accepted.Remove(field);
        else accepted[field] = value == "wrong" ? -1 : value;
        Mock<TableClient> table = new(MockBehavior.Strict);
        await Assert.ThrowsExactlyAsync<VerificationFailure>(() => fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default));
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("PartitionKey")]
    [DataRow("RowKey")]
    [DataRow("VerificationRun")]
    [DataRow("FallbackTo")]
    [DataRow("From")]
    public async Task ChangedTemplateOwnershipOrApprovedMailbox_NeverWrites(string field)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity template = Template();
        template[field] = "unrelated";
        Mock<TableClient> table = new(MockBehavior.Strict);
        VerificationFailure error = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            fixture.RestoreLostOutcomeAsync(table.Object, Accepted(fixture), template, Wake(), Now, default));
        Assert.AreEqual("live_template_ownership_required", error.Message);
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("other", ServiceBusMessageState.Scheduled, 60)]
    [DataRow(null, ServiceBusMessageState.Active, 60)]
    [DataRow(null, ServiceBusMessageState.Scheduled, 10)]
    [DataRow(null, ServiceBusMessageState.Scheduled, -1)]
    public async Task WrongOrDueWake_NeverWrites(string? id, ServiceBusMessageState state, int seconds)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        Mock<TableClient> table = new(MockBehavior.Strict);
        await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            fixture.RestoreLostOutcomeAsync(table.Object, Accepted(fixture), Template(), Wake(id, state, seconds), Now, default));
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StaleETagOrLostWriteResponse_IsNeverRetriedOrForceSaved(bool stale)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        Exception failure = stale ? new RequestFailedException(412, "Synthetic conflict") : new IOException("Synthetic lost response");
        Mock<TableClient> table = new(MockBehavior.Strict);
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        Exception actual = await Assert.ThrowsAsync<Exception>(() =>
            fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default));
        Assert.AreSame(failure, actual);
        table.Verify(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()), Times.Once);
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task ChangedReadback_IsNotReportedAsVerified()
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        TableEntity accepted = Accepted(fixture);
        Mock<TableClient> table = new(MockBehavior.Strict);
        table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), accepted.ETag, TableUpdateMode.Replace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        table.Setup(t => t.GetEntityAsync<TableEntity>(fixture.Identity.Tenant, fixture.Identity.ID, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(accepted, Mock.Of<Response>()));
        VerificationFailure error = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            fixture.RestoreLostOutcomeAsync(table.Object, accepted, Template(), Wake(), Now, default));
        Assert.AreEqual("live_uncertain_snapshot_not_verified", error.Message);
    }

    [TestMethod]
    public async Task ScheduledWakeSearch_OnlyPeeksAndSkipsUnrelatedOrActiveEnvelopes()
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        ServiceBusReceivedMessage wake = Wake(sequence: 8);
        Mock<ServiceBusReceiver> reader = new(MockBehavior.Strict);
        reader.Setup(r => r.PeekMessagesAsync(100, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Wake("unrelated", sequence: 5), Wake(state: ServiceBusMessageState.Active, sequence: 6) });
        reader.Setup(r => r.PeekMessagesAsync(100, 7, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { wake });
        Assert.AreSame(wake, await fixture.ScheduledWakeAsync(reader.Object, default));
        reader.Verify(r => r.PeekMessagesAsync(100, 0, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.PeekMessagesAsync(100, 7, It.IsAny<CancellationToken>()), Times.Once);
        reader.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MissingWake_StopsAtEmptyPageOrBoundedScan(bool empty)
    {
        OwnedRecoveryFixture fixture = new(command, Sender);
        Mock<ServiceBusReceiver> reader = new(MockBehavior.Strict);
        reader.Setup(r => r.PeekMessagesAsync(100, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, long? next, CancellationToken _) => empty ? [] : new[] { Wake("unrelated", sequence: next!.Value) });
        VerificationFailure error = await Assert.ThrowsExactlyAsync<VerificationFailure>(() => fixture.ScheduledWakeAsync(reader.Object, default));
        Assert.AreEqual("live_scheduled_recovery_missing", error.Message);
        reader.Verify(r => r.PeekMessagesAsync(100, It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Exactly(empty ? 1 : 100));
        reader.VerifyNoOtherCalls();
    }
}
