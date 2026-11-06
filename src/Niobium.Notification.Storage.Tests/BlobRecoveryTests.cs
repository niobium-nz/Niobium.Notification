using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Database.StorageTable;
using Niobium.File;
using Niobium.File.Blob;
using Niobium.Identity;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;
using Niobium.Platform.Notification.Email;
using Niobium.Platform.Notification.Email.Resend;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class BlobRecoveryTests
{
    private sealed class IsolatedTables(Uri uri, TableSharedKeyCredential key, string name) : TableServiceClient(uri, key)
    {
        public override TableClient GetTableClient(string tableName)
        {
            Assert.AreEqual(TableNotificationSubmissionStore.TableName, tableName);
            return base.GetTableClient(name);
        }
    }

    private sealed class IsolatedBlobs(Uri uri, StorageSharedKeyCredential key, string name) : BlobServiceClient(uri, key)
    {
        public BlobContainerClient OwnedContainer => base.GetBlobContainerClient(name);
        public override BlobContainerClient GetBlobContainerClient(string containerName)
        {
            Assert.AreEqual(TableNotificationSubmissionStore.PayloadContainer, containerName);
            return OwnedContainer;
        }
    }

    private sealed class StorageFixture : IAsyncDisposable
    {
        // Published Azurite key. No application credentials or cloud endpoints are accepted.
        private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
        private readonly string tableName = "NotificationTest" + Guid.NewGuid().ToString("N");
        private readonly IsolatedTables tables;
        private readonly IsolatedBlobs blobs;
        private readonly Mock<IAzureTableClientFactory> tableFactory = new(MockBehavior.Strict);
        private readonly ConcurrentDictionary<string, BlobServiceClient> clients;
        private readonly Type factoryType;
        private readonly string cacheKey;

        public StorageFixture()
        {
            Uri tableUri = Endpoint("NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT");
            Uri blobUri = Endpoint("NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT");
            tables = new(tableUri, new("devstoreaccount1", EmulatorKey), tableName);
            blobs = new(blobUri, new("devstoreaccount1", EmulatorKey), "notification-test-" + Guid.NewGuid().ToString("N"));
            tableFactory.Setup(f => f.CreateClientAsync(It.IsAny<IEnumerable<DatabasePermissions>>(), TableNotificationSubmissionStore.TableName, null, It.IsAny<CancellationToken>())).ReturnsAsync(tables);

            // The pinned 4.0.60 factory is internal/sealed and creates a managed-identity
            // client. Seed only this fixture's cache entry with a real loopback SDK client;
            // the actual CloudBlobRepository implementation still performs all Blob I/O.
            // This does not test cloud authentication or alter the framework package.
            factoryType = typeof(StorageBlobOptions).Assembly.GetType("Niobium.File.Blob.AzureBlobClientFactory", throwOnError: true)!;
            clients = (ConcurrentDictionary<string, BlobServiceClient>)factoryType.GetField("clients", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            cacheKey = blobUri.AbsoluteUri.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N");
            Assert.IsTrue(clients.TryAdd(cacheKey, blobs));
        }

        public BlobContainerClient Container => blobs.OwnedContainer;

        public TableNotificationSubmissionStore NewStore()
        {
            object factory = Activator.CreateInstance(factoryType,
                Options.Create(new StorageBlobOptions { FullyQualifiedDomainName = cacheKey }),
                new ConfigurationBuilder().Build(),
                new Lazy<IAuthenticator>(() => throw new AssertFailedException("Identity access is forbidden in a local fixture.")))!;
            Type repositoryType = typeof(StorageBlobOptions).Assembly.GetType("Niobium.File.Blob.CloudBlobRepository", throwOnError: true)!;
            IFileService files = (IFileService)Activator.CreateInstance(repositoryType, factory)!;
            return new(tableFactory.Object, files);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    await tables.DeleteTableAsync(tableName);
                }
                catch (RequestFailedException error) when (error.Status == 404) { }
            }
            finally
            {
                try { await Container.DeleteIfExistsAsync(); }
                finally { Assert.IsTrue(clients.TryRemove(cacheKey, out _)); }
            }
        }

        private static Uri Endpoint(string setting)
        {
            string value = Environment.GetEnvironmentVariable(setting)
                ?? throw new InvalidOperationException($"Set {setting} to a disposable loopback Azurite endpoint.");
            Uri uri = new(value);
            Assert.IsTrue(uri.IsLoopback && uri.Scheme == "http" && String.IsNullOrEmpty(uri.Query)
                && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment)
                && uri.AbsolutePath.TrimEnd('/') == "/devstoreaccount1");
            return uri;
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class LostCompletion(INotificationSubmissionStore inner, bool commitBeforeFailure) : INotificationSubmissionStore
    {
        public Task<NotificationSubmission?> ReadAsync(string partition, string id, CancellationToken token) => inner.ReadAsync(partition, id, token);
        public Task<bool> CreateAsync(NotificationSubmission value, FrozenNotification? payload, CancellationToken token) => inner.CreateAsync(value, payload, token);
        public Task<FrozenNotification> ReadPayloadAsync(string name, CancellationToken token) => inner.ReadPayloadAsync(name, token);
        public async Task<NotificationSubmission?> UpdateAsync(NotificationSubmission value, CancellationToken token)
        {
            if (value.State != "accepted") return await inner.UpdateAsync(value, token);
            if (commitBeforeFailure) Assert.IsNotNull(await inner.UpdateAsync(value, token));
            throw new IOException("Synthetic lost result-write response.");
        }
    }

    private static NotifyCommand Request() => new()
    {
        ID = "synthetic-operation", Tenant = Guid.NewGuid(), Channel = "Notice",
        Destination = "recipient@example.invalid", Parameters = new() { ["text"] = "Frozen" },
    };

    private static NotificationSubmission Entry(NotifyCommand request) => new()
    {
        PartitionKey = request.Tenant.ToString("N"), RowKey = NotificationFingerprint.Hash(request.ID),
        Fingerprint = NotificationFingerprint.Of(request), PayloadName = "payload/frozen.json",
    };

    [TestMethod]
    public async Task PinnedBlobAdapter_PreservesPrivateCreateOnlyPayload_AndRejectsMissingContent()
    {
        await using StorageFixture storage = new();
        TableNotificationSubmissionStore store = storage.NewStore();
        NotificationSubmission entry = Entry(Request());
        FrozenNotification payload = new("{\"html\":\"Frozen ✓\"}", "example.invalid", "synthetic-digest");
        Assert.IsTrue(await store.CreateAsync(entry, payload, default));
        Assert.AreEqual(PublicAccessType.None, (await storage.Container.GetAccessPolicyAsync()).Value.BlobPublicAccess);
        Assert.IsTrue((await storage.Container.GetBlobClient("/" + entry.PayloadName).ExistsAsync()).Value);
        Assert.AreEqual(payload, await storage.NewStore().ReadPayloadAsync(entry.PayloadName, default));

        RequestFailedException conflict = await Assert.ThrowsExactlyAsync<RequestFailedException>(() =>
            store.CreateAsync(entry with { RowKey = "new-operation" }, payload with { Json = "changed" }, default));
        Assert.AreEqual(409, conflict.Status);
        Assert.IsNull(await store.ReadAsync(entry.PartitionKey, "new-operation", default));
        Assert.AreEqual(payload, await storage.NewStore().ReadPayloadAsync(entry.PayloadName, default));

        await storage.Container.DeleteBlobAsync("/" + entry.PayloadName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => storage.NewStore().ReadPayloadAsync(entry.PayloadName, default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealTableAndBlob_LostCompletion_RestartReconcilesSameOperation(bool commitBeforeFailure)
    {
        await using StorageFixture storage = new();
        TableNotificationSubmissionStore store = storage.NewStore();
        Clock clock = new();
        NotifyCommand request = Request();
        NotificationSubmission entry = Entry(request);
        List<(string Key, string Json)> submissions = [];
        List<MessagingEntry<NotifyCommand>> scheduled = [];
        using HttpClient client = new(new Handler(async (message, token) =>
        {
            NotificationSubmission claimed = (await storage.NewStore().ReadAsync(entry.PartitionKey, entry.RowKey, token))!;
            Assert.AreEqual("sending", claimed.State);
            Assert.IsTrue(scheduled.Count >= claimed.Attempts, "Recovery must be scheduled before provider submission.");
            submissions.Add((message.Headers.GetValues("Idempotency-Key").Single(), await message.Content!.ReadAsStringAsync(token)));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"synthetic-provider-id\"}") };
        }));
        ResendServiceOptions options = new() { GlobalAPIKey = "synthetic-provider-key", DomainScopedAPIKeys = [] };
        ResendSubmissionProvider Provider() => new(client, Options.Create(options), clock);
        FrozenNotification frozen = Provider().Freeze(new TemplateDomain.Deliverable
        {
            From = "sender@example.invalid", To = request.Destination!, Subject = "Frozen subject", Body = "<p>Frozen ✓</p>",
        });
        Assert.IsTrue(await store.CreateAsync(entry, frozen, default));
        Mock<IMessagingBroker<NotifyCommand>> queue = new(MockBehavior.Strict);
        queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) => scheduled.AddRange(entries))
            .Returns(Task.CompletedTask);
        // Any template access or legacy send would fail: recovery must use stored content.
        NotificationFlow renderer = new(new Mock<IDomainRepository<TemplateDomain, Template>>(MockBehavior.Strict).Object,
            new Mock<IEmailNotificationClient>(MockBehavior.Strict).Object, NullLogger<NotificationFlow>.Instance);
        DurableNotificationFlow Flow(INotificationSubmissionStore current) => new(renderer, current, Provider(), queue.Object, clock, new NotificationRetryPolicy());

        await Assert.ThrowsExactlyAsync<IOException>(() => Flow(new LostCompletion(store, commitBeforeFailure)).RunAsync(request));
        Assert.AreEqual(1, submissions.Count);
        Assert.AreEqual(request.ID, scheduled.Single().ID);
        DateTimeOffset first = clock.Now;
        clock.Now = scheduled.Single().Schedule!.Value;
        await Flow(storage.NewStore()).RunAsync(scheduled[0].Value);
        Assert.AreEqual(commitBeforeFailure ? 1 : 2, submissions.Count);
        Assert.IsTrue(submissions.All(call => call.Key == submissions[0].Key && call.Json == frozen.Json));
        NotificationSubmission accepted = (await storage.NewStore().ReadAsync(entry.PartitionKey, entry.RowKey, default))!;
        Assert.AreEqual("accepted", accepted.State);
        Assert.AreEqual("synthetic-provider-id", accepted.ProviderID);
        Assert.AreEqual(first, accepted.FirstSubmission);
        Assert.IsNull(accepted.NextAttempt);

        clock.Now = first.AddDays(2);
        await Flow(storage.NewStore()).RunAsync(request);
        Assert.AreEqual(commitBeforeFailure ? 1 : 2, submissions.Count);
    }
}
