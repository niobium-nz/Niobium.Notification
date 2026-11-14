using System.Collections.Concurrent;
using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Database.StorageTable;
using Niobium.File;
using Niobium.File.Blob;
using Niobium.Identity;
using Niobium.Notification.Delivery;

namespace Niobium.Notification.Storage.Tests;

internal sealed class LocalStorageFixture : IAsyncDisposable
{
    private sealed class IsolatedTables(Uri uri, TableSharedKeyCredential key, string name) : TableServiceClient(uri, key)
    {
        public override TableClient GetTableClient(string tableName)
        {
            Assert.IsTrue(tableName is nameof(NotificationSubmission) or nameof(NotificationReceipt));
            return base.GetTableClient(tableName == nameof(NotificationSubmission) ? name : name + "Receipt");
        }
    }

    private sealed class IsolatedBlobs(Uri uri, StorageSharedKeyCredential key, string name) : BlobServiceClient(uri, key)
    {
        public BlobContainerClient OwnedContainer => base.GetBlobContainerClient(name);
        public override BlobContainerClient GetBlobContainerClient(string containerName)
        {
            Assert.AreEqual(NotificationSubmission.PayloadContainer, containerName);
            return OwnedContainer;
        }
    }

    // Published Azurite key; cloud endpoints and application credentials are forbidden.
    private const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private readonly string tableName = "NotificationTest" + Guid.NewGuid().ToString("N");
    private readonly IsolatedTables tables;
    private readonly IsolatedBlobs blobs;
    private readonly Mock<IAzureTableClientFactory> tableFactory = new(MockBehavior.Strict);
    private readonly ConcurrentDictionary<string, BlobServiceClient> clients;
    private readonly Type factoryType;
    private readonly string cacheKey;

    public LocalStorageFixture()
    {
        Uri tableUri = Endpoint("NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT");
        Uri blobUri = Endpoint("NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT");
        tables = new(tableUri, new("devstoreaccount1", EmulatorKey), tableName);
        blobs = new(blobUri, new("devstoreaccount1", EmulatorKey), "notification-test-" + Guid.NewGuid().ToString("N"));
        tableFactory.Setup(f => f.CreateClientAsync(It.IsAny<IEnumerable<DatabasePermissions>>(), nameof(NotificationSubmission), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(tables);
        tableFactory.Setup(f => f.CreateClientAsync(It.IsAny<IEnumerable<DatabasePermissions>>(), nameof(NotificationReceipt), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(tables);
        // Pinned internal factory has no injectable client constructor. Substitute only
        // this isolated cache entry; the actual framework Blob repository performs I/O.
        factoryType = typeof(StorageBlobOptions).Assembly.GetType("Niobium.File.Blob.AzureBlobClientFactory", throwOnError: true)!;
        clients = (ConcurrentDictionary<string, BlobServiceClient>)factoryType.GetField("clients", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        cacheKey = blobUri.AbsoluteUri.TrimEnd('/') + "/" + Guid.NewGuid().ToString("N");
        bool added = clients.TryAdd(cacheKey, blobs);
        Assert.IsTrue(added);
    }

    public BlobContainerClient Container => blobs.OwnedContainer;
    public TableClient Table => tables.GetTableClient(nameof(NotificationSubmission));
    public TableClient ReceiptTable => tables.GetTableClient(nameof(NotificationReceipt));
    public async Task InitializeAsync()
    {
        await tables.CreateTableAsync(tableName);
        await tables.CreateTableAsync(tableName + "Receipt");
    }

    public IRepository<NotificationSubmission> Repository()
        => new CloudTableRepository<NotificationSubmission>(tableFactory.Object, NullLogger<CloudTableRepository<NotificationSubmission>>.Instance);

    public IRepository<NotificationReceipt> ReceiptRepository()
        => new CloudTableRepository<NotificationReceipt>(tableFactory.Object, NullLogger<CloudTableRepository<NotificationReceipt>>.Instance);

    public IDomainRepository<NotificationReceiptDomain, NotificationReceipt> ReceiptDomains()
    {
        Lazy<IRepository<NotificationReceipt>> repository = new(ReceiptRepository);
        return new GenericDomainRepository<NotificationReceiptDomain, NotificationReceipt>(() => new(repository, []), repository);
    }

    public IFileService Files()
    {
        object factory = Activator.CreateInstance(factoryType,
            Options.Create(new StorageBlobOptions { FullyQualifiedDomainName = cacheKey }),
            new ConfigurationBuilder().Build(),
            new Lazy<IAuthenticator>(() => throw new AssertFailedException("Identity access is forbidden in a local fixture.")))!;
        Type repositoryType = typeof(StorageBlobOptions).Assembly.GetType("Niobium.File.Blob.CloudBlobRepository", throwOnError: true)!;
        return (IFileService)Activator.CreateInstance(repositoryType, factory)!;
    }

    public NotificationSubmissionDomain Domain(string partition, string id, IRepository<NotificationSubmission>? repository = null)
    {
        NotificationSubmissionDomain domain = new(new Lazy<IRepository<NotificationSubmission>>(() => repository ?? Repository()), [], Files());
        domain.Initialize(partition, id);
        return domain;
    }

    public IDomainRepository<NotificationSubmissionDomain, NotificationSubmission> Domains(IRepository<NotificationSubmission>? repository = null)
    {
        Lazy<IRepository<NotificationSubmission>> lazy = new(() => repository ?? Repository());
        return new GenericDomainRepository<NotificationSubmissionDomain, NotificationSubmission>(() => new(lazy, [], Files()), lazy);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try { await tables.DeleteTableAsync(tableName); }
            catch (RequestFailedException error) when (error.Status == 404) { }
            try { await tables.DeleteTableAsync(tableName + "Receipt"); }
            catch (RequestFailedException error) when (error.Status == 404) { }
        }
        finally
        {
            try { await Container.DeleteIfExistsAsync(); }
            finally { bool removed = clients.TryRemove(cacheKey, out _); Assert.IsTrue(removed); }
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
