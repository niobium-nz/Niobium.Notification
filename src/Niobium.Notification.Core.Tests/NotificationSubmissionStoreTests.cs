using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Moq;
using Niobium.Database.StorageTable;
using Niobium.File;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class NotificationSubmissionStoreTests
{
    private sealed class StorageResponse(string? etag = "\"version-2\"") : Response
    {
        public override int Status => 204;
        public override string ReasonPhrase => "No Content";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "synthetic";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => name.Equals("ETag", StringComparison.OrdinalIgnoreCase) && etag != null;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => etag == null ? [] : [new HttpHeader("ETag", etag)];
        protected override bool TryGetHeader(string name, out string value) { value = ContainsHeader(name) ? etag! : String.Empty; return ContainsHeader(name); }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { bool found = TryGetHeader(name, out string value); values = found ? [value] : []; return found; }
    }

    private sealed class Scenario
    {
        public readonly Mock<TableClient> Table = new(MockBehavior.Strict);
        public readonly Mock<IFileService> Files = new(MockBehavior.Strict);
        public readonly TableNotificationSubmissionStore Store;
        public readonly NotificationSubmission Entry = new() { PartitionKey = "tenant", RowKey = "operation", Fingerprint = "fingerprint", PayloadName = "payload", ETag = "version-1" };
        public readonly FrozenNotification Payload = new("{\"html\":\"frozen\"}", "example.invalid", "key-digest");
        public Scenario()
        {
            Mock<TableServiceClient> service = new(MockBehavior.Strict);
            service.Setup(x => x.GetTableClient(TableNotificationSubmissionStore.TableName)).Returns(Table.Object);
            Mock<IAzureTableClientFactory> factory = new(MockBehavior.Strict);
            factory.Setup(x => x.CreateClientAsync(It.IsAny<IEnumerable<DatabasePermissions>>(), TableNotificationSubmissionStore.TableName, null, It.IsAny<CancellationToken>())).ReturnsAsync(service.Object);
            Store = new(factory.Object, Files.Object);
            Table.Setup(x => x.CreateIfNotExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(new TableItem("NotificationSubmission"), new StorageResponse()));
            Table.Setup(x => x.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new StorageResponse());
            Table.Setup(x => x.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageResponse());
        }
    }

    [TestMethod]
    public async Task Create_FreezesPayloadBeforeConditionalInsert_AndReadRoundTripsState()
    {
        Scenario s = new(); byte[]? saved = null; TableEntity? row = null;
        s.Files.Setup(x => x.PutAsync(TableNotificationSubmissionStore.PayloadContainer, "payload", It.IsAny<Stream>(), false, null, true, It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, Stream content, bool _, IDictionary<string, string>? _, bool _, CancellationToken token) => { using MemoryStream stream = new(); await content.CopyToAsync(stream, token); saved = stream.ToArray(); });
        s.Table.Setup(x => x.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TableEntity, CancellationToken>((value, _) => { Assert.IsNotNull(saved); row = value; row.ETag = new ETag("created-version"); })
            .ReturnsAsync(new StorageResponse());
        Assert.IsTrue(await s.Store.CreateAsync(s.Entry, s.Payload, default));
        s.Table.Setup(x => x.GetEntityIfExistsAsync<TableEntity>("tenant", "operation", null, It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(row!, new StorageResponse()));
        NotificationSubmission? read = await s.Store.ReadAsync("tenant", "operation", default);
        Assert.AreEqual(s.Entry with { ETag = "created-version" }, read);
        s.Files.Setup(x => x.GetAsync(TableNotificationSubmissionStore.PayloadContainer, "payload", false, It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(saved!));
        Assert.AreEqual(s.Payload, await s.Store.ReadPayloadAsync("payload", default));
    }

    [TestMethod]
    public async Task LostInsertRace_DoesNotOverwriteOrDeleteCandidateBlob()
    {
        Scenario s = new();
        s.Table.Setup(x => x.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(409, "synthetic"));
        Assert.IsFalse(await s.Store.CreateAsync(s.Entry, null, default));
        Assert.AreEqual(0, s.Files.Invocations.Count);
    }

    [TestMethod]
    public async Task ConditionalReplace_RequiresVersionAndReturnsExactWriteETag()
    {
        Scenario s = new();
        NotificationSubmission next = s.Entry with { State = "accepted", ProviderID = "provider-id", FirstSubmission = DateTimeOffset.UtcNow, Attempts = 2, Outcome = "accepted" };
        NotificationSubmission? saved = await s.Store.UpdateAsync(next, default);
        Assert.AreEqual(next with { ETag = "version-2" }, saved);
        s.Table.Verify(x => x.UpdateEntityAsync(It.Is<TableEntity>(e => e.GetString("ProviderID") == "provider-id" && e.GetInt32("Attempts") == 2 && e["NextAttempt"] == null), new ETag("version-1"), TableUpdateMode.Replace, It.IsAny<CancellationToken>()), Times.Once);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Store.UpdateAsync(next with { ETag = "*" }, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Store.UpdateAsync(next with { ETag = null }, default));
    }

    [TestMethod]
    public async Task StaleVersion_IsConflict_UnknownStorageErrorsPropagate()
    {
        Scenario s = new();
        s.Table.Setup(x => x.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(412, "synthetic"));
        Assert.IsNull(await s.Store.UpdateAsync(s.Entry, default));
        s.Table.Setup(x => x.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(503, "synthetic"));
        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => s.Store.UpdateAsync(s.Entry, default));
        s.Table.Setup(x => x.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), TableUpdateMode.Replace, It.IsAny<CancellationToken>())).ReturnsAsync(new StorageResponse(null));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Store.UpdateAsync(s.Entry, default));
    }

    [TestMethod]
    public async Task MissingTableOrPayload_IsExplicit_AndReadErrorsPropagate()
    {
        Scenario s = new();
        s.Table.Setup(x => x.GetEntityIfExistsAsync<TableEntity>("tenant", "operation", null, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(404, "synthetic"));
        Assert.IsNull(await s.Store.ReadAsync("tenant", "operation", default));
        s.Table.Setup(x => x.GetEntityIfExistsAsync<TableEntity>("tenant", "operation", null, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestFailedException(503, "synthetic"));
        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => s.Store.ReadAsync("tenant", "operation", default));
        s.Files.Setup(x => x.GetAsync(TableNotificationSubmissionStore.PayloadContainer, "payload", false, It.IsAny<CancellationToken>())).ReturnsAsync((Stream?)null);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Store.ReadPayloadAsync("payload", default));
        s.Files.Setup(x => x.GetAsync(TableNotificationSubmissionStore.PayloadContainer, "payload", false, It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream("null"u8.ToArray()));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.Store.ReadPayloadAsync("payload", default));
    }

    [TestMethod]
    public async Task BlobFailure_NeverCreatesInboxPointer()
    {
        Scenario s = new();
        s.Files.Setup(x => x.PutAsync(TableNotificationSubmissionStore.PayloadContainer, "payload", It.IsAny<Stream>(), false, null, true, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("synthetic blob failure"));
        await Assert.ThrowsExactlyAsync<IOException>(() => s.Store.CreateAsync(s.Entry, s.Payload, default));
        s.Table.Verify(x => x.AddEntityAsync(It.IsAny<TableEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
