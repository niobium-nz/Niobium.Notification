using Azure.Data.Tables;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Deployment.Tests;

// Read-only evidence for the existing owned send. The deployed webhook/consumer
// must create and publish this receipt; the runner never inserts one.
internal sealed class OwnedReceiptFixture
{
    private readonly NotificationSubmission identity;
    private readonly string providerMessageID;
    private readonly string proof;

    internal OwnedReceiptFixture(NotificationSubmission identity, TableEntity accepted)
    {
        this.identity = identity;
        providerMessageID = accepted.GetString(nameof(NotificationSubmission.ProviderID)) ?? String.Empty;
        proof = accepted.GetString(nameof(NotificationSubmission.ReceiptProof)) ?? String.Empty;
        Require(accepted.PartitionKey == identity.Tenant && accepted.RowKey == identity.ID
            && !String.IsNullOrWhiteSpace(providerMessageID) && !String.IsNullOrWhiteSpace(proof),
            "live_receipt_correlation_missing");
    }

    internal async Task WaitForPublishedDeliveryAsync(TableClient receipts, CancellationToken cancellationToken)
    {
        while (true)
        {
            bool published = await HasPublishedDeliveryAsync(receipts, cancellationToken);
            if (published) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    internal async Task<bool> HasPublishedDeliveryAsync(TableClient receipts, CancellationToken cancellationToken)
    {
        string filter = TableClient.CreateQueryFilter($"PartitionKey eq {"resend"} and Tenant eq {identity.Tenant} and SubmissionID eq {identity.ID}");
        int count = 0;
        bool delivered = false;
        await foreach (TableEntity receipt in receipts.QueryAsync<TableEntity>(filter, maxPerPage: 100, cancellationToken: cancellationToken))
        {
            Require(++count <= 100, "live_receipt_query_limit");
            Require(receipt.PartitionKey == "resend"
                && receipt.GetString(nameof(NotificationReceipt.Tenant)) == identity.Tenant
                && receipt.GetString(nameof(NotificationReceipt.SubmissionID)) == identity.ID
                && receipt.GetString(nameof(NotificationReceipt.CorrelationProof)) == proof
                && receipt.GetString(nameof(NotificationReceipt.ProviderMessageID)) == providerMessageID,
                "live_receipt_identity_changed");
            Require(receipt.GetInt32(nameof(NotificationReceipt.Version)) == NotificationReceipt.CurrentVersion
                && !String.IsNullOrWhiteSpace(receipt.GetString(nameof(NotificationReceipt.ProviderEventID)))
                && !String.IsNullOrWhiteSpace(receipt.GetString(nameof(NotificationReceipt.BodyFingerprint)))
                && receipt.GetDateTimeOffset(nameof(NotificationReceipt.OccurredAt)) != null,
                "live_receipt_evidence_incomplete");
            int? publication = receipt.GetInt32(nameof(NotificationReceipt.PublicationCode));
            Require(publication != (int)ReceiptPublication.Uncorrelated, "live_receipt_correlation_held");
            Require(publication is (int)ReceiptPublication.Pending or (int)ReceiptPublication.Published,
                "live_receipt_evidence_incomplete");
            if (receipt.GetInt32(nameof(NotificationReceipt.KindCode)) == (int)NotificationDeliveryKind.Delivered
                && publication == (int)ReceiptPublication.Published)
            {
                Require(receipt.GetDateTimeOffset(nameof(NotificationReceipt.PublishedAt)) != null,
                    "live_receipt_evidence_incomplete");
                delivered = true;
            }
        }
        return delivered;
    }

    private static void Require(bool valid, string code) { if (!valid) throw new VerificationFailure(code); }
}
