using Azure.Data.Tables;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class SubmissionContractCompatibilityTests
{
    [TestMethod]
    public async Task ExistingTableAndBlobShape_LoadsBusinessKeys_AndRetainsPhysicalContract()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        // Existing version-2 values, independent of the refactored factory/mapper.
        const string tenant = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        const string id = "187F0349DD12B6DC73D76D86F421CD454FACCCC36EF9A2BA6956B37ABBB31102";
        const string payloadName = "aaaaaaaabbbbccccddddeeeeeeeeeeee/187F0349DD12B6DC73D76D86F421CD454FACCCC36EF9A2BA6956B37ABBB31102/11111111111111111111111111111111.json";
        TableEntity legacy = new(tenant, id)
        {
            ["Version"] = 2,
            ["Fingerprint"] = "208476BD65EF8385CF968E58F840849FD05A5300689EE61FF032C5569C62E493",
            ["PayloadName"] = payloadName, ["WriteID"] = "legacy-write",
            ["StateCode"] = 1, ["OutcomeCode"] = 0, ["Attempts"] = 0,
        };
        await storage.Table.AddEntityAsync(legacy);
        using MemoryStream content = new("{\"Json\":\"frozen-json\",\"SenderDomain\":\"example.invalid\",\"CredentialFingerprint\":\"B49688D91597F5FF9BB8E3AAF3B02DA7FFE52CD7FB0BA9FD3E1D060A7FB58576\"}"u8.ToArray());
        await storage.Files().PutAsync("notification-submissions", payloadName, content, replaceIfExist: false);

        NotificationSubmission identity = NotificationSubmission.Create(new NotifyCommand
        {
            ID = "operation-1", Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Channel = "Notice", Destination = "recipient@example.invalid",
            Parameters = new() { ["b"] = new[] { "one", "two" }, ["a"] = "" },
        });
        NotificationSubmissionDomain domain = storage.Domain(identity.Tenant, identity.ID);
        NotificationSubmission loaded = await domain.GetEntityAsync();
        Assert.AreEqual(tenant, loaded.Tenant);
        Assert.AreEqual(id, loaded.ID);
        Assert.AreEqual(payloadName, loaded.PayloadName);
        Assert.AreEqual(2, loaded.Version);
        Assert.IsFalse(String.IsNullOrWhiteSpace(loaded.ETag));
        SubmissionAttempt attempt = await domain.PrepareAttemptAsync(identity, DateTimeOffset.UtcNow, new NotificationRetryPolicy());
        Assert.AreEqual(SubmissionAction.Submit, attempt.Action);
        Assert.AreEqual("frozen-json", attempt.Payload!.Json);
        Assert.IsTrue(attempt.Payload.MatchesCredential("synthetic-provider-key"));
        await domain.RecordOutcomeAsync(attempt, new(SubmissionOutcome.Accepted, "provider-id"));

        TableEntity stored = (await storage.Table.GetEntityAsync<TableEntity>(tenant, id)).Value;
        Assert.AreEqual(tenant, stored.PartitionKey);
        Assert.AreEqual(id, stored.RowKey);
        Assert.AreEqual(payloadName, stored.GetString("PayloadName"));
        Assert.AreEqual(2, stored.GetInt32("Version"));
        Assert.IsFalse(stored.ContainsKey(nameof(NotificationSubmission.ReceiptProof)));
        Assert.AreEqual(3, stored.GetInt32("StateCode"));
        Assert.IsFalse(stored.ContainsKey("Tenant"));
        Assert.IsFalse(stored.ContainsKey("ID"));
        Assert.IsFalse(stored.ContainsKey("PayloadContainer"));
        NotificationSubmission restarted = (await storage.Repository().RetrieveAsync(tenant, id))!;
        Assert.AreEqual(loaded.Fingerprint, restarted.Fingerprint);
        Assert.AreEqual(payloadName, restarted.PayloadName);
        Assert.AreEqual((int)SubmissionState.Accepted, restarted.StateCode);
    }
}
