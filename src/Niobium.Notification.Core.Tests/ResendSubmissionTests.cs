using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server.Delivery;
using Niobium.Platform.Notification.Email.Resend;

namespace Niobium.Notification.Core.Tests;

[TestClass]
public sealed class ResendSubmissionTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class UnreadableStream(Exception failure) : MemoryStream("{"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(failure);
    }

    private static TemplateDomain.Deliverable Content() => new()
    {
        From = "sender@example.invalid", FromName = "Sender", To = "recipient@example.invalid", ToName = "Recipient", Subject = "Subject", Body = "<p>Frozen</p>",
    };

    private static ResendServiceOptions Config() => new() { GlobalAPIKey = "synthetic-global-key", DomainScopedAPIKeys = [] };

    [TestMethod]
    public async Task Provider_UsesPerRequestCredentialAndSameFrozenBytesAndKey()
    {
        DurableNotificationTests.Clock clock = new();
        ResendServiceOptions config = Config();
        config.DomainScopedAPIKeys["example.invalid"] = "synthetic-scoped-key";
        List<string> bodies = [];
        using HttpClient client = new(new Handler(async (request, token) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://api.resend.com/emails", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("synthetic-scoped-key", request.Headers.Authorization!.Parameter);
            Assert.AreEqual("stable-operation", request.Headers.GetValues("Idempotency-Key").Single());
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"provider-1\"}") };
        }));
        ResendSubmissionProvider provider = new(client, Options.Create(config), clock);
        TemplateDomain.Deliverable source = Content();
        FrozenNotification frozen = provider.Freeze(source);
        source.Subject = "Changed";
        Assert.IsFalse(frozen.Json.Contains("synthetic-scoped-key", StringComparison.Ordinal));
        Assert.AreEqual(SubmissionOutcome.Accepted, (await provider.SubmitAsync(frozen, "stable-operation", clock.Now.AddDays(1), default)).Outcome);
        SubmissionResult second = await provider.SubmitAsync(frozen, "stable-operation", clock.Now.AddDays(1), default);
        Assert.AreEqual("provider-1", second.ProviderID);
        Assert.AreEqual(bodies[0], bodies[1]);
        Assert.AreEqual(frozen.Json, bodies[0]);
        Assert.IsNull(client.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    [DataRow(200, "{\"id\":\"accepted-id\"}", SubmissionOutcome.Accepted)]
    [DataRow(200, "{}", SubmissionOutcome.Retry)]
    [DataRow(200, "{\"id\":12}", SubmissionOutcome.Retry)]
    [DataRow(200, "invalid json", SubmissionOutcome.Retry)]
    [DataRow(200, "null", SubmissionOutcome.Retry)]
    [DataRow(200, "[]", SubmissionOutcome.Retry)]
    [DataRow(409, "{\"name\":\"concurrent_idempotent_requests\"}", SubmissionOutcome.Retry)]
    [DataRow(409, "{\"name\":\"invalid_idempotent_request\"}", SubmissionOutcome.Hold)]
    [DataRow(409, "{}", SubmissionOutcome.Hold)]
    [DataRow(409, "", SubmissionOutcome.Hold)]
    [DataRow(409, "{", SubmissionOutcome.Hold)]
    [DataRow(409, "<html>upstream conflict</html>", SubmissionOutcome.Hold)]
    [DataRow(409, "null", SubmissionOutcome.Hold)]
    [DataRow(409, "[]", SubmissionOutcome.Hold)]
    [DataRow(429, "", SubmissionOutcome.Retry)]
    [DataRow(408, "", SubmissionOutcome.Retry)]
    [DataRow(503, "", SubmissionOutcome.Retry)]
    [DataRow(401, "", SubmissionOutcome.Rejected)]
    [DataRow(422, "", SubmissionOutcome.Rejected)]
    public async Task Responses_HaveExplicitOutcomes(int status, string body, SubmissionOutcome outcome)
    {
        DurableNotificationTests.Clock clock = new();
        int calls = 0;
        using HttpClient client = new(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }));
        ResendSubmissionProvider provider = new(client, Options.Create(Config()), clock);
        Assert.AreEqual(outcome, (await provider.SubmitAsync(provider.Freeze(Content()), "key", clock.Now.AddDays(1), default)).Outcome);
        Assert.AreEqual(1, calls); // Never retry invisibly inside the provider adapter.
    }

    [TestMethod]
    public async Task CredentialChangeAndWindowCutoff_HoldWithoutNetworkAccess()
    {
        DurableNotificationTests.Clock clock = new();
        ResendServiceOptions config = Config();
        using HttpClient client = new(new Handler((_, _) => throw new AssertFailedException("Network access was not expected.")));
        ResendSubmissionProvider provider = new(client, Options.Create(config), clock);
        FrozenNotification frozen = provider.Freeze(Content());
        Assert.AreEqual(SubmissionOutcome.Hold, (await provider.SubmitAsync(frozen, "key", clock.Now.AddMinutes(1), default)).Outcome);
        config.GlobalAPIKey = "new-synthetic-account";
        Assert.AreEqual(SubmissionOutcome.Hold, (await provider.SubmitAsync(frozen, "key", clock.Now.AddDays(1), default)).Outcome);
        config.GlobalAPIKey = "";
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.Freeze(Content()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NetworkFailureOrTimeout_IsUncertainAndRetryable(bool timeout)
    {
        DurableNotificationTests.Clock clock = new();
        using HttpClient client = new(new Handler((_, _) => timeout ? throw new TaskCanceledException() : throw new HttpRequestException()));
        ResendSubmissionProvider provider = new(client, Options.Create(Config()), clock);
        Assert.AreEqual(SubmissionOutcome.Retry, (await provider.SubmitAsync(provider.Freeze(Content()), "key", clock.Now.AddDays(1), default)).Outcome);
    }

    [TestMethod]
    [DataRow(typeof(TaskCanceledException))]
    [DataRow(typeof(HttpRequestException))]
    [DataRow(typeof(IOException))]
    public async Task UnreadableConflictBody_IsHeld(Type failureType)
    {
        DurableNotificationTests.Clock clock = new();
        Exception failure = (Exception)Activator.CreateInstance(failureType)!;
        using HttpClient client = new(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StreamContent(new UnreadableStream(failure)),
        })));
        ResendSubmissionProvider provider = new(client, Options.Create(Config()), clock);
        Assert.AreEqual(SubmissionOutcome.Hold,
            (await provider.SubmitAsync(provider.Freeze(Content()), "key", clock.Now.AddDays(1), default)).Outcome);
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesForQueueRecovery()
    {
        DurableNotificationTests.Clock clock = new();
        using CancellationTokenSource cancellation = new();
        using HttpClient client = new(new Handler((_, _) => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }));
        ResendSubmissionProvider provider = new(client, Options.Create(Config()), clock);
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SubmitAsync(provider.Freeze(Content()), "key", clock.Now.AddDays(1), cancellation.Token));
    }
}
