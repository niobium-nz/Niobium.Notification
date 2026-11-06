using Azure.Data.Tables;
using Moq;
using Niobium.Database.StorageTable;
using Niobium.File;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class SubmissionConcurrencyTests
{
    // Published Azurite fixture key, never an application/cloud credential.
    private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private sealed class IsolatedTables(Uri uri, TableSharedKeyCredential key, string name, TableClientOptions options) : TableServiceClient(uri, key, options)
    {
        public override TableClient GetTableClient(string tableName) => base.GetTableClient(name);
    }

    [TestMethod]
    public async Task RealTable_InsertRace_ETagClaims_PayloadAndRestartRoundTrip()
    {
        string endpoint = Environment.GetEnvironmentVariable("NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT")
            ?? throw new InvalidOperationException("Set NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT to a disposable loopback Azurite instance.");
        Uri uri = new(endpoint);
        Assert.IsTrue(uri.IsLoopback && uri.Scheme == "http" && String.IsNullOrEmpty(uri.Query) && uri.AbsolutePath.TrimEnd('/') == "/devstoreaccount1");
        string tableName = "NotificationTest" + Guid.NewGuid().ToString("N");
        TableClientOptions options = new(); options.Retry.MaxRetries = 0;
        IsolatedTables service = new(uri, new TableSharedKeyCredential("devstoreaccount1", EmulatorKey), tableName, options);
        Mock<IAzureTableClientFactory> factory = new();
        factory.Setup(f => f.CreateClientAsync(It.IsAny<IEnumerable<DatabasePermissions>>(), TableNotificationSubmissionStore.TableName, null, It.IsAny<CancellationToken>())).ReturnsAsync(service);
        Dictionary<string, byte[]> payloads = [];
        Mock<IFileService> files = new(MockBehavior.Strict);
        files.Setup(f => f.PutAsync(TableNotificationSubmissionStore.PayloadContainer, It.IsAny<string>(), It.IsAny<Stream>(), false, null, true, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string name, Stream content, bool _, IDictionary<string, string>? _, bool _, CancellationToken token) =>
            {
                using MemoryStream buffer = new(); await content.CopyToAsync(buffer, token);
                lock (payloads) payloads.Add(name, buffer.ToArray());
            });
        files.Setup(f => f.GetAsync(TableNotificationSubmissionStore.PayloadContainer, It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string name, bool _, CancellationToken _) => new MemoryStream(payloads[name]));
        TableNotificationSubmissionStore store = new(factory.Object, files.Object);
        NotificationSubmission input = new() { PartitionKey = "synthetic-tenant", RowKey = "synthetic-operation", Fingerprint = "fingerprint", PayloadName = "first" };
        FrozenNotification payload = new("{\"html\":\"frozen\"}", "example.invalid", "synthetic-digest");
        try
        {
            Assert.IsNull(await store.ReadAsync(input.PartitionKey, input.RowKey, default));
            bool[] created = await Task.WhenAll(store.CreateAsync(input, payload, default), store.CreateAsync(input with { PayloadName = "second" }, payload, default));
            Assert.AreEqual(1, created.Count(x => x));
            NotificationSubmission stored = (await store.ReadAsync(input.PartitionKey, input.RowKey, default))!;
            Assert.IsFalse(String.IsNullOrEmpty(stored.ETag));
            Assert.AreEqual(payload, await store.ReadPayloadAsync(stored.PayloadName, default));
            DateTimeOffset first = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
            NotificationSubmission claim = stored with { State = "sending", Attempts = 1, FirstSubmission = first, NextAttempt = first.AddMinutes(2), Outcome = "submission_unknown" };
            NotificationSubmission?[] claims = await Task.WhenAll(store.UpdateAsync(claim, default), store.UpdateAsync(claim, default));
            Assert.AreEqual(1, claims.Count(x => x != null));
            NotificationSubmission winner = claims.Single(x => x != null)!;
            Assert.AreNotEqual(stored.ETag, winner.ETag);
            Assert.IsNull(await store.UpdateAsync(stored with { State = "accepted" }, default));
            TableNotificationSubmissionStore restarted = new(factory.Object, files.Object);
            Assert.AreEqual(winner, await restarted.ReadAsync(input.PartitionKey, input.RowKey, default));
            NotificationSubmission accepted = (await restarted.UpdateAsync(winner with { State = "accepted", NextAttempt = null, ProviderID = "provider-id", Outcome = "accepted" }, default))!;
            NotificationSubmission reread = (await restarted.ReadAsync(input.PartitionKey, input.RowKey, default))!;
            Assert.AreEqual(accepted, reread);
            Assert.IsNull(reread.NextAttempt);
            Assert.AreEqual(first, reread.FirstSubmission);
            Assert.AreEqual(payload, await restarted.ReadPayloadAsync(reread.PayloadName, default));
        }
        finally
        {
            await service.DeleteTableAsync(tableName);
        }
    }
}
