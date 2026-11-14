using System.Collections.Concurrent;
using System.Net;
using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;
using Niobium.Platform.Notification.Email;
using Niobium.Platform.Notification.Email.Resend;

namespace Niobium.Notification.Storage.Tests;

[TestClass]
public sealed class ConcurrentRecoveryTests
{
    [TestMethod]
    public async Task CompetingConsumers_ClaimOneStoredVersion_OnlyWinnerSubmits()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        using Scenario scenario = new(storage);
        await scenario.RegisterAsync();
        IRepository<NotificationSubmission> repository = storage.Repository();
        TaskCompletionSource bothClaiming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int claims = 0;
        ConcurrentQueue<string?> claimVersions = new();
        Mock<IRepository<NotificationSubmission>> barrier = ForwardReads(repository);
        barrier.Setup(r => r.UpdateAsync(It.IsAny<IEnumerable<NotificationSubmission>>(), true, false, It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<NotificationSubmission> entries, bool _, bool _, CancellationToken token) =>
            {
                NotificationSubmission entry = entries.Single();
                if (entry.StateCode == (int)SubmissionState.Sending && entry.Attempts == 1)
                {
                    claimVersions.Enqueue(entry.ETag);
                    int arrived = Interlocked.Increment(ref claims);
                    if (arrived == 2) bothClaiming.TrySetResult();
                    await bothClaiming.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
                // Only timing is controlled here. The pinned adapter performs both writes.
                return await repository.UpdateAsync(entries, cancellationToken: token);
            });

        Exception?[] outcomes = await Task.WhenAll(
            Capture(() => scenario.Flow(barrier.Object).RunAsync(scenario.Request)),
            Capture(() => scenario.Flow(barrier.Object).RunAsync(scenario.Request)));

        Assert.AreEqual(2, claims);
        Assert.AreEqual(1, claimVersions.Distinct().Count(), "Both consumers must compete for the same ETag.");
        Assert.AreEqual(1, outcomes.Count(error => error == null));
        Exception? loser = outcomes.Single(error => error != null);
        Assert.IsInstanceOfType<RequestFailedException>(loser);
        Assert.AreEqual(412, ((RequestFailedException)loser!).Status);
        Assert.AreEqual(1, scenario.Calls.Count);
        Assert.AreEqual(1, scenario.Wakes.Count);
        NotificationSubmission accepted = await scenario.StoredAsync();
        Assert.AreEqual((int)SubmissionState.Accepted, accepted.StateCode);
        Assert.AreEqual(1, accepted.Attempts);
        Assert.AreEqual("accepted-provider-id", accepted.ProviderID);
        await scenario.AssertSuppressedAfterWindowAsync(1);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, "{\"id\":\"late-provider-id\"}")]
    [DataRow(HttpStatusCode.BadRequest, "{}")]
    [DataRow(HttpStatusCode.ServiceUnavailable, "{}")]
    [DataRow(HttpStatusCode.Conflict, "{\"name\":\"invalid_idempotent_request\"}")]
    public async Task LateProviderResult_CannotOverwriteRecoveredAcceptance(HttpStatusCode lateStatus, string lateBody)
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        TaskCompletionSource firstSubmitting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Scenario scenario = new(storage, async (call, token) =>
        {
            if (call == 1)
            {
                firstSubmitting.TrySetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                return new(lateStatus) { Content = new StringContent(lateBody) };
            }
            return Scenario.Accepted();
        });
        await scenario.RegisterAsync();
        DateTimeOffset first = scenario.Clock.Now;
        Task<Exception?> original = Capture(() => scenario.Flow().RunAsync(scenario.Request));
        NotificationSubmission recovered;
        try
        {
            await firstSubmitting.Task.WaitAsync(TimeSpan.FromSeconds(15));
            MessagingEntry<NotifyCommand> wake = scenario.Wakes.Single();
            scenario.Clock.Now = wake.Schedule!.Value;
            await scenario.Flow().RunAsync(wake.Value);
            recovered = await scenario.StoredAsync();
            Assert.AreEqual((int)SubmissionState.Accepted, recovered.StateCode);
            Assert.AreEqual(2, recovered.Attempts);
            Assert.AreEqual(first, recovered.FirstSubmission);
            Assert.AreEqual("accepted-provider-id", recovered.ProviderID);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await original;
        }

        Exception? lateError = await original;
        Assert.IsInstanceOfType<RequestFailedException>(lateError);
        Assert.AreEqual(412, ((RequestFailedException)lateError!).Status);
        NotificationSubmission final = await scenario.StoredAsync();
        Assert.AreEqual(recovered, final, "A late result must not alter the winner's state, ETag or evidence.");
        Assert.AreEqual(2, scenario.Calls.Count);
        Assert.AreEqual(2, scenario.Wakes.Count);
        Assert.AreEqual(1, scenario.Calls.Distinct().Count(), "Retries must use the same provider key and frozen bytes.");
        Assert.IsTrue(scenario.Wakes.All(wake => wake.ID == scenario.Request.ID && wake.Value.ID == scenario.Request.ID));
        await scenario.AssertSuppressedAfterWindowAsync(2);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static Mock<IRepository<NotificationSubmission>> ForwardReads(IRepository<NotificationSubmission> repository)
    {
        Mock<IRepository<NotificationSubmission>> result = new(MockBehavior.Strict);
        result.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Returns((string pk, string rk, IList<string>? _, CancellationToken token) => repository.RetrieveAsync(pk, rk, cancellationToken: token));
        return result;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly LocalStorageFixture storage;
        private readonly HttpClient client;
        private readonly ResendSubmissionProvider provider;
        private readonly Mock<IMessagingBroker<NotifyCommand>> queue = new(MockBehavior.Strict);
        private readonly NotificationFlow renderer = new(new Mock<IDomainRepository<TemplateDomain, Template>>(MockBehavior.Strict).Object,
            new Mock<IEmailNotificationClient>(MockBehavior.Strict).Object, NullLogger<NotificationFlow>.Instance);
        private int callCount;

        public Scenario(LocalStorageFixture storage, Func<int, CancellationToken, Task<HttpResponseMessage>>? response = null)
        {
            this.storage = storage;
            queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) =>
                {
                    foreach (MessagingEntry<NotifyCommand> entry in entries) Wakes.Enqueue(entry);
                }).Returns(Task.CompletedTask);
            client = new(new Handler(async (message, token) =>
            {
                NotificationSubmission claim = await StoredAsync();
                Assert.AreEqual((int)SubmissionState.Sending, claim.StateCode);
                Assert.IsTrue(Wakes.Count >= claim.Attempts, "Recovery must be scheduled before each provider call.");
                Calls.Enqueue((message.Headers.GetValues("Idempotency-Key").Single(), await message.Content!.ReadAsStringAsync(token)));
                int call = Interlocked.Increment(ref callCount);
                return response == null ? Accepted() : await response(call, token);
            }));
            provider = new(client, Options.Create(new ResendServiceOptions
            {
                GlobalAPIKey = "synthetic-provider-key", DomainScopedAPIKeys = [],
            }), Clock);
        }

        public TestClock Clock { get; } = new();
        public NotifyCommand Request { get; } = new()
        {
            ID = "synthetic-concurrent-operation", Tenant = Guid.NewGuid(), Channel = "Notice",
            Destination = "recipient@example.invalid", Parameters = new() { ["text"] = "Frozen" },
        };
        public ConcurrentQueue<(string Key, string Json)> Calls { get; } = new();
        public ConcurrentQueue<MessagingEntry<NotifyCommand>> Wakes { get; } = new();
        private string Partition => Request.Tenant.ToString("N");
        private string ID => NotificationFingerprint.Hash(Request.ID);

        public Task RegisterAsync() => storage.Domain(Partition, ID).RegisterAsync(NotificationFingerprint.Of(Request),
            provider.Freeze(new TemplateDomain.Deliverable
            {
                From = "sender@example.invalid", To = Request.Destination!, Subject = "Frozen subject", Body = "<p>Frozen content</p>",
            }));

        public DurableNotificationFlow Flow(IRepository<NotificationSubmission>? repository = null)
            => new(renderer, storage.Domains(repository), provider, queue.Object, Clock, new NotificationRetryPolicy());

        public async Task<NotificationSubmission> StoredAsync()
            => (await storage.Repository().RetrieveAsync(Partition, ID))!;

        public async Task AssertSuppressedAfterWindowAsync(int expectedCalls)
        {
            Clock.Now = Clock.Now.AddDays(2);
            int wakes = Wakes.Count;
            await Flow().RunAsync(Request);
            Assert.AreEqual(expectedCalls, Calls.Count);
            Assert.AreEqual(wakes, Wakes.Count);
        }

        public static HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"accepted-provider-id\"}"),
        };

        public void Dispose() => client.Dispose();
    }
}
