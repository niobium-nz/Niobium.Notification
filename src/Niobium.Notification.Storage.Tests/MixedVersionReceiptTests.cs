using Azure.Data.Tables;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class MixedVersionReceiptTests
{
    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, true)]
    public async Task PriorProductionWriter_CannotReplaceReceiptBearingVersion3Rows(int version, bool fenced)
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotifyCommand command = new()
        {
            Tenant = Guid.NewGuid(), ID = "mixed-version-owned-fixture", Channel = "Notice", Parameters = [],
        };
        NotificationSubmission identity = NotificationSubmission.Create(command);
        await storage.Domain(identity.Tenant, identity.ID).RegisterAsync(identity,
            FrozenNotification.Create("synthetic-frozen-json", "example.invalid", "synthetic-key"));
        TableEntity before = (await storage.Table.GetEntityAsync<TableEntity>(identity.Tenant, identity.ID)).Value;
        Assert.AreEqual(3, before.GetInt32(nameof(NotificationSubmission.Version)));
        Assert.IsFalse(String.IsNullOrWhiteSpace(before.GetString(nameof(NotificationSubmission.ReceiptProof))));
        if (version == 2)
        {
            // Owned loopback-only control reproduces the reviewed defect. The
            // application never downgrades a version-3 row or upgrades a legacy row.
            before[nameof(NotificationSubmission.Version)] = 2;
            await storage.Table.UpdateEntityAsync(before, before.ETag, TableUpdateMode.Replace);
            before = (await storage.Table.GetEntityAsync<TableEntity>(identity.Tenant, identity.ID)).Value;
        }

        // These are the prior production model/domain source, compiled in an
        // isolated test namespace, and the actual pinned Table/Blob adapters.
        LegacyV2.NotificationSubmission oldIdentity = LegacyV2.NotificationSubmission.Create(command);
        LegacyV2.NotificationSubmissionDomain oldWriter = new(new(storage.LegacyRepository), [], storage.Files());
        oldWriter.Initialize(oldIdentity.Tenant, oldIdentity.ID);
        if (fenced)
        {
            await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => oldWriter.PrepareRegistrationAsync(oldIdentity, true));
            await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => oldWriter.PrepareAttemptAsync(oldIdentity, DateTimeOffset.UtcNow, new NotificationRetryPolicy()));
        }
        else
        {
            LegacyV2.SubmissionAttempt attempt = await oldWriter.PrepareAttemptAsync(oldIdentity, DateTimeOffset.UtcNow, new NotificationRetryPolicy());
            Assert.AreEqual(LegacyV2.SubmissionAction.Submit, attempt.Action);
        }
        TableEntity after = (await storage.Table.GetEntityAsync<TableEntity>(identity.Tenant, identity.ID)).Value;
        if (fenced)
        {
            Assert.AreEqual(before.ETag, after.ETag);
            foreach (string field in new[] { nameof(NotificationSubmission.ReceiptProof), nameof(NotificationSubmission.Operation), nameof(NotificationSubmission.Channel) })
                Assert.AreEqual(before.GetString(field), after.GetString(field));
        }
        else
        {
            // Proves that merely adding columns without the fence loses evidence.
            Assert.AreNotEqual(before.ETag, after.ETag);
            Assert.IsFalse(after.ContainsKey(nameof(NotificationSubmission.ReceiptProof)));
            Assert.IsFalse(after.ContainsKey(nameof(NotificationSubmission.Operation)));
            Assert.IsFalse(after.ContainsKey(nameof(NotificationSubmission.Channel)));
        }
    }
}
