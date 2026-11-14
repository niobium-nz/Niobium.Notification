using System.Text;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class NotificationSubmissionContractTests
{
    // Compatibility vectors captured from the implementation at a8bc0e4.
    private const string Tenant = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
    private const string ID = "187F0349DD12B6DC73D76D86F421CD454FACCCC36EF9A2BA6956B37ABBB31102";
    private const string Fingerprint = "208476BD65EF8385CF968E58F840849FD05A5300689EE61FF032C5569C62E493";
    private const string CredentialFingerprint = "B49688D91597F5FF9BB8E3AAF3B02DA7FFE52CD7FB0BA9FD3E1D060A7FB58576";

    private static NotifyCommand Request() => new()
    {
        ID = "operation-1", Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Channel = "Notice", Destination = "recipient@example.invalid",
        Parameters = new() { ["b"] = new[] { "one", "two" }, ["a"] = "" },
    };

    [TestMethod]
    public void RequestSnapshot_PreservesExistingIdentityFingerprintAndProviderKey()
    {
        NotifyCommand command = Request();
        NotificationSubmission first = NotificationSubmission.Create(command);
        NotificationSubmission replay = NotificationSubmission.Create(command);
        Assert.AreEqual(Tenant, first.Tenant);
        Assert.AreEqual(ID, first.ID);
        Assert.AreEqual(Fingerprint, first.Fingerprint);
        Assert.AreEqual(3, first.Version);
        Assert.IsTrue(first.HasSupportedVersion());
        Assert.AreEqual("notification-v1/aaaaaaaabbbbccccddddeeeeeeeeeeee/187F0349DD12B6DC73D76D86F421CD454FACCCC36EF9A2BA6956B37ABBB31102",
            first.GetProviderIdempotencyKey());
        Assert.IsTrue(first.MatchesRequest(replay));
        Assert.AreNotEqual(first.PayloadName, replay.PayloadName);
        StringAssert.Matches(first.PayloadName, new(@"^aaaaaaaabbbbccccddddeeeeeeeeeeee/187F0349DD12B6DC73D76D86F421CD454FACCCC36EF9A2BA6956B37ABBB31102/[0-9a-f]{32}\.json$"));
        command.ID = "operation-✓";
        Assert.AreEqual("DCCDF785AEF137C99608F1C3D164F333F6ED1C0B40CB8E3CCF21F78827705F2D", NotificationSubmission.Create(command).ID);
        Assert.AreEqual(ID, first.ID);
        command.Parameters["a"] = "changed";
        Assert.AreEqual(Fingerprint, first.Fingerprint);
        Assert.IsFalse(first.MatchesRequest(NotificationSubmission.Create(command)));
    }

    [TestMethod]
    public void BusinessKeyProperties_MapToExistingStorageColumnsWithoutDerivedColumns()
    {
        var mapping = EntityMappingHelper.GetMapping(typeof(NotificationSubmission));
        Assert.AreEqual(nameof(NotificationSubmission.Tenant), mapping["PartitionKey"].Name);
        Assert.AreEqual(nameof(NotificationSubmission.ID), mapping["RowKey"].Name);
        Assert.AreEqual(17, mapping.Count);
        Assert.IsFalse(mapping.ContainsKey(nameof(NotificationSubmission.Tenant)));
        Assert.IsFalse(mapping.ContainsKey(nameof(NotificationSubmission.ID)));
        Assert.IsFalse(mapping.ContainsKey(nameof(NotificationSubmission.PayloadContainer)));
        NotificationSubmission entry = NotificationSubmission.Create(Request());
        Assert.AreEqual(Tenant, EntityMappingHelper.GetField<string>(entry, EntityKeyKind.PartitionKey));
        Assert.AreEqual(ID, EntityMappingHelper.GetField<string>(entry, EntityKeyKind.RowKey));
    }

    [TestMethod]
    public async Task FrozenPayload_ReadsExistingJsonAndOwnsCredentialComparison()
    {
        string existing = "{\"Json\":\"frozen-json\",\"SenderDomain\":\"example.invalid\",\"CredentialFingerprint\":\"" + CredentialFingerprint + "\"}";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(existing));
        FrozenNotification stored = await FrozenNotification.ReadAsync(stream);
        Assert.AreEqual("frozen-json", stored.Json);
        Assert.AreEqual("example.invalid", stored.SenderDomain);
        Assert.IsTrue(stored.MatchesCredential("synthetic-provider-key"));
        Assert.IsFalse(stored.MatchesCredential("rotated-provider-key"));
        FrozenNotification created = FrozenNotification.Create("frozen-json", "example.invalid", "synthetic-provider-key");
        Assert.AreEqual(stored, created);
        Assert.AreEqual(existing, Encoding.UTF8.GetString(created.Serialize()));
    }
}
