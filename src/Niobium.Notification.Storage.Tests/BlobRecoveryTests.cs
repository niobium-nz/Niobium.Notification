using System.Net;
using Azure;
using Azure.Storage.Blobs.Models;
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
public sealed class BlobRecoveryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static NotifyCommand Request() => new()
    {
        ID = "synthetic-operation", Tenant = Guid.NewGuid(), Channel = "Notice",
        Destination = "recipient@example.invalid", Parameters = new() { ["text"] = "Frozen" },
    };

    [TestMethod]
    public async Task PinnedBlobAdapter_PreservesPrivateCreateOnlyPayload_AndRejectsMissingContent()
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        NotificationSubmissionDomain domain = storage.Domain("tenant", "operation");
        FrozenNotification payload = new("{html:Frozen ✓}", "example.invalid", "synthetic-digest");
        await domain.RegisterAsync("fingerprint", payload);
        NotificationSubmission entry = await domain.GetEntityAsync();
        Assert.AreEqual(PublicAccessType.None, (await storage.Container.GetAccessPolicyAsync()).Value.BlobPublicAccess);
        SubmissionAttempt attempt = await domain.PrepareAttemptAsync("fingerprint", DateTimeOffset.UtcNow, new NotificationRetryPolicy());
        Assert.AreEqual(payload, attempt.Payload);
        using MemoryStream changed = new("changed"u8.ToArray());
        RequestFailedException conflict = await Assert.ThrowsExactlyAsync<RequestFailedException>(() =>
            storage.Files().PutAsync(NotificationSubmissionDomain.PayloadContainer, entry.PayloadName, changed, replaceIfExist: false));
        Assert.AreEqual(409, conflict.Status);
        await storage.Container.DeleteBlobAsync("/" + entry.PayloadName);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => storage.Domain("tenant", "operation")
            .PrepareAttemptAsync("fingerprint", attempt.Due!.Value, new NotificationRetryPolicy()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealTableAndBlob_LostCompletion_RestartReconcilesSameOperation(bool commitBeforeFailure)
    {
        await using LocalStorageFixture storage = new();
        await storage.InitializeAsync();
        IRepository<NotificationSubmission> repository = storage.Repository();
        Clock clock = new();
        NotifyCommand request = Request();
        string partition = request.Tenant.ToString("N");
        string id = NotificationFingerprint.Hash(request.ID);
        List<(string Key, string Json)> submissions = [];
        List<MessagingEntry<NotifyCommand>> scheduled = [];
        using HttpClient client = new(new Handler(async (message, token) =>
        {
            NotificationSubmission claimed = (await storage.Repository().RetrieveAsync(partition, id, cancellationToken: token))!;
            Assert.AreEqual((int)SubmissionState.Sending, claimed.StateCode);
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
        await storage.Domain(partition, id).RegisterAsync(NotificationFingerprint.Of(request), frozen);
        Mock<IMessagingBroker<NotifyCommand>> queue = new(MockBehavior.Strict);
        queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<MessagingEntry<NotifyCommand>>, CancellationToken>((entries, _) => scheduled.AddRange(entries))
            .Returns(Task.CompletedTask);
        NotificationFlow renderer = new(new Mock<IDomainRepository<TemplateDomain, Template>>(MockBehavior.Strict).Object,
            new Mock<IEmailNotificationClient>(MockBehavior.Strict).Object, NullLogger<NotificationFlow>.Instance);
        DurableNotificationFlow Flow(IRepository<NotificationSubmission>? current = null) => new(renderer, storage.Domains(current), Provider(), queue.Object, clock, new NotificationRetryPolicy());
        Mock<IRepository<NotificationSubmission>> uncertain = new(MockBehavior.Strict);
        uncertain.Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Returns((string pk, string rk, IList<string>? _, CancellationToken token) => repository.RetrieveAsync(pk, rk, cancellationToken: token));
        uncertain.Setup(r => r.UpdateAsync(It.IsAny<IEnumerable<NotificationSubmission>>(), true, false, It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<NotificationSubmission> entries, bool _, bool _, CancellationToken token) =>
            {
                if (entries.Single().StateCode != (int)SubmissionState.Accepted)
                    return await repository.UpdateAsync(entries, cancellationToken: token);
                if (commitBeforeFailure) await repository.UpdateAsync(entries, cancellationToken: token);
                throw new IOException("Synthetic lost result-write response.");
            });
        await Assert.ThrowsExactlyAsync<IOException>(() => Flow(uncertain.Object).RunAsync(request));
        Assert.AreEqual(1, submissions.Count);
        Assert.AreEqual(request.ID, scheduled.Single().ID);
        DateTimeOffset first = clock.Now;
        clock.Now = scheduled.Single().Schedule!.Value;
        await Flow().RunAsync(scheduled[0].Value);
        Assert.AreEqual(commitBeforeFailure ? 1 : 2, submissions.Count);
        Assert.IsTrue(submissions.All(call => call.Key == submissions[0].Key && call.Json == frozen.Json));
        NotificationSubmission accepted = (await storage.Repository().RetrieveAsync(partition, id))!;
        Assert.AreEqual((int)SubmissionState.Accepted, accepted.StateCode);
        Assert.AreEqual("synthetic-provider-id", accepted.ProviderID);
        Assert.AreEqual(first, accepted.FirstSubmission);
        Assert.IsNull(accepted.NextAttempt);
        clock.Now = first.AddDays(2);
        await Flow().RunAsync(request);
        Assert.AreEqual(commitBeforeFailure ? 1 : 2, submissions.Count);
    }
}
