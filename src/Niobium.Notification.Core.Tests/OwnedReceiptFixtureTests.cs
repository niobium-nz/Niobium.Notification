using Azure;
using Azure.Data.Tables;
using Moq;
using Niobium.Notification.Delivery;
using Niobium.Notification.Deployment.Tests;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class OwnedReceiptFixtureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly NotificationSubmission identity = NotificationSubmission.Create(new NotifyCommand
    {
        ID = "owned-operation", Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Channel = "ContactUs", Destination = "approved@example.invalid", Parameters = [],
    });

    private TableEntity Accepted() => new(identity.Tenant, identity.ID)
    {
        [nameof(NotificationSubmission.ProviderID)] = "provider-message",
        [nameof(NotificationSubmission.ReceiptProof)] = "opaque-persisted-proof",
    };

    private TableEntity Receipt() => new("resend", "opaque-receipt-id")
    {
        [nameof(NotificationReceipt.Version)] = NotificationReceipt.CurrentVersion,
        [nameof(NotificationReceipt.Tenant)] = identity.Tenant,
        [nameof(NotificationReceipt.SubmissionID)] = identity.ID,
        [nameof(NotificationReceipt.CorrelationProof)] = "opaque-persisted-proof",
        [nameof(NotificationReceipt.ProviderMessageID)] = "provider-message",
        [nameof(NotificationReceipt.ProviderEventID)] = "provider-event",
        [nameof(NotificationReceipt.BodyFingerprint)] = "opaque-body-fingerprint",
        [nameof(NotificationReceipt.OccurredAt)] = Now,
        [nameof(NotificationReceipt.PublishedAt)] = Now,
        [nameof(NotificationReceipt.KindCode)] = (int)NotificationDeliveryKind.Delivered,
        [nameof(NotificationReceipt.PublicationCode)] = (int)ReceiptPublication.Published,
    };

    private Mock<TableClient> Table(params TableEntity[] rows)
    {
        Mock<TableClient> table = new(MockBehavior.Strict);
        string expected = TableClient.CreateQueryFilter($"PartitionKey eq {"resend"} and Tenant eq {identity.Tenant} and SubmissionID eq {identity.ID}");
        table.Setup(t => t.QueryAsync<TableEntity>(expected, 100, null, It.IsAny<CancellationToken>()))
            .Returns(AsyncPageable<TableEntity>.FromPages([Page<TableEntity>.FromValues(rows, null, Mock.Of<Response>())]));
        return table;
    }

    [TestMethod]
    [DataRow(NotificationDeliveryKind.Submitted, ReceiptPublication.Published, false)]
    [DataRow(NotificationDeliveryKind.Delivered, ReceiptPublication.Pending, false)]
    [DataRow(NotificationDeliveryKind.Delivered, ReceiptPublication.Published, true)]
    public async Task OnlyOwnedDeliveredAndPublishedEvidencePasses(NotificationDeliveryKind kind, ReceiptPublication publication, bool expected)
    {
        TableEntity receipt = Receipt();
        receipt[nameof(NotificationReceipt.KindCode)] = (int)kind;
        receipt[nameof(NotificationReceipt.PublicationCode)] = (int)publication;
        Mock<TableClient> table = Table(receipt);
        Assert.AreEqual(expected, await new OwnedReceiptFixture(identity, Accepted()).HasPublishedDeliveryAsync(table.Object, default));
        table.Verify(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), 100, null, It.IsAny<CancellationToken>()), Times.Once);
        table.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("PartitionKey")]
    [DataRow("Tenant")]
    [DataRow("SubmissionID")]
    [DataRow("CorrelationProof")]
    [DataRow("ProviderMessageID")]
    public async Task UnrelatedOrChangedEvidenceNeverPasses(string field)
    {
        TableEntity row = Receipt();
        row[field] = "unrelated";
        VerificationFailure failure = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            new OwnedReceiptFixture(identity, Accepted()).HasPublishedDeliveryAsync(Table(row).Object, default));
        Assert.AreEqual("live_receipt_identity_changed", failure.Message);
    }

    [TestMethod]
    [DataRow("Version")]
    [DataRow("ProviderEventID")]
    [DataRow("BodyFingerprint")]
    [DataRow("OccurredAt")]
    [DataRow("PublishedAt")]
    [DataRow("PublicationCode")]
    public async Task IncompleteEvidenceNeverPasses(string field)
    {
        TableEntity row = Receipt();
        row.Remove(field);
        VerificationFailure failure = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            new OwnedReceiptFixture(identity, Accepted()).HasPublishedDeliveryAsync(Table(row).Object, default));
        Assert.AreEqual("live_receipt_evidence_incomplete", failure.Message);
    }

    [TestMethod]
    public async Task NoReceiptWaitsAndAHeldReceiptFailsWithFixedDiagnostic()
    {
        OwnedReceiptFixture fixture = new(identity, Accepted());
        Assert.IsFalse(await fixture.HasPublishedDeliveryAsync(Table().Object, default));
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.WaitForPublishedDeliveryAsync(Table().Object, stopped.Token));
        TableEntity held = Receipt();
        held[nameof(NotificationReceipt.PublicationCode)] = (int)ReceiptPublication.Uncorrelated;
        VerificationFailure failure = await Assert.ThrowsExactlyAsync<VerificationFailure>(() => fixture.HasPublishedDeliveryAsync(Table(held).Object, default));
        Assert.AreEqual("live_receipt_correlation_held", failure.Message);
    }

    [TestMethod]
    public async Task ReceiptScanIsBoundedEvenWhenProviderReturnsExcessEvents()
    {
        VerificationFailure failure = await Assert.ThrowsExactlyAsync<VerificationFailure>(() =>
            new OwnedReceiptFixture(identity, Accepted()).HasPublishedDeliveryAsync(Table(Enumerable.Range(0, 101).Select(_ => Receipt()).ToArray()).Object, default));
        Assert.AreEqual("live_receipt_query_limit", failure.Message);
    }

    [TestMethod]
    public void ReceiptProofMustComeFromThePersistedOwnedSubmission()
    {
        TableEntity accepted = Accepted();
        accepted.Remove(nameof(NotificationSubmission.ReceiptProof));
        Assert.ThrowsExactly<VerificationFailure>(() => new OwnedReceiptFixture(identity, accepted));
        accepted = Accepted();
        accepted.RowKey = "someone-elses-operation";
        Assert.ThrowsExactly<VerificationFailure>(() => new OwnedReceiptFixture(identity, accepted));
    }
}
