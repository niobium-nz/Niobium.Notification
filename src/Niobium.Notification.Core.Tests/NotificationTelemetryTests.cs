using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Niobium.Messaging;
using Niobium.Notification.Delivery;
using Niobium.Notification.Server;
using Niobium.Platform.Notification.Email;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using static Niobium.Notification.Core.Tests.Helpers.NotificationTestHelper;

namespace Niobium.Notification.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NotificationTelemetryTests
{
    [TestMethod]
    [DataRow(true, "accepted")]
    [DataRow(false, "rejected")]
    public async Task Legacy_adapter_is_awaited_and_reports_its_result_with_only_fixed_labels(bool accepted, string outcome)
    {
        using Capture capture = new();
        Guid tenant = Guid.NewGuid();
        var (flow, _, sender, _) = CreateSut(CreateDomain(BuildTemplate(tenant, "Notice"), "body", DefaultOptions));
        TaskCompletionSource<bool> completion = new();
        sender.Setup(x => x.SendAsync(It.IsAny<EmailAddress>(), It.IsAny<IEnumerable<EmailAddress>>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .Returns(completion.Task);
        NotifyCommand request = new() { Tenant = tenant, Channel = "Notice", Destination = "private@example.invalid", Parameters = [] };
        Task sending = flow.RunAsync(request);
        Assert.IsFalse(sending.IsCompleted);
        Assert.AreEqual(0, capture.Read().Count);
        completion.SetResult(accepted);
        if (accepted)
            await sending;
        else
            await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => sending);
        AssertProvider(capture, "legacy", outcome, 1);
        Assert.AreEqual(2, capture.Read().Count);
    }

    [TestMethod]
    [DataRow(false, false, "failed")]
    [DataRow(true, false, "failed")]
    [DataRow(true, true, "cancelled")]
    public async Task Adapter_exceptions_and_cancellation_are_preserved(bool cancellation, bool requested, string outcome)
    {
        using Capture capture = new();
        using CancellationTokenSource source = new();
        if (requested)
            source.Cancel();
        Exception expected = cancellation ? new OperationCanceledException(source.Token) : new IOException("private error text");
        Exception? observed = null;
        try
        {
            await NotificationTelemetry.MeasureLegacyAsync(() => Task.FromException<bool>(expected), source.Token);
        }
        catch (Exception error) { observed = error; }
        Assert.AreSame(expected, observed);
        AssertProvider(capture, "legacy", outcome, 1);
        Assert.AreEqual(2, capture.Read().Count);
    }

    [TestMethod]
    public async Task Durable_acceptance_and_late_suppression_report_domain_status_without_an_extra_provider_attempt()
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        await scenario.Run();
        scenario.Clock.Now += TimeSpan.FromDays(30);
        await scenario.Run();
        Assert.AreEqual(1, scenario.Sends);
        AssertProvider(capture, "durable", "accepted", 1);
        AssertDecision(capture, "submit", "sending", "submissionunknown", 1);
        AssertDecision(capture, "suppress", "accepted", "accepted", 1);
        AssertOutcome(capture, "accepted", "accepted", 1);
        Assert.AreEqual(5, capture.Read().Count);
    }

    [TestMethod]
    public async Task Retry_and_early_wake_are_distinct_from_provider_calls_and_recorded_completion()
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        scenario.Outcome(SubmissionOutcome.Retry);
        await scenario.Run();
        await scenario.Run();
        Assert.AreEqual(1, scenario.Sends);
        AssertDecision(capture, "wait", "pending", "retry", 1);
        scenario.Advance();
        scenario.Provider.Setup(p => p.SubmitAsync(It.IsAny<FrozenNotification>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubmissionResult(SubmissionOutcome.Accepted, "private-provider-id"));
        await scenario.Run();
        AssertProvider(capture, "durable", "retry", 1);
        AssertProvider(capture, "durable", "accepted", 1);
        AssertDecision(capture, "submit", "sending", "submissionunknown", 2);
        AssertOutcome(capture, "pending", "retry", 1);
        AssertOutcome(capture, "accepted", "accepted", 1);
        Assert.AreEqual(8, capture.Read().Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Window_or_attempt_exhaustion_reports_the_persisted_hold(bool attemptsExhausted)
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        scenario.Outcome(SubmissionOutcome.Retry);
        await scenario.Run();
        if (attemptsExhausted)
            scenario.Store.Entry = scenario.Store.Entry! with { Attempts = NotificationRetryPolicy.MaximumAttempts };
        else
            scenario.Clock.Now += TimeSpan.FromDays(1);
        await scenario.Run();
        Assert.AreEqual((int)SubmissionState.Held, scenario.Store.Entry!.StateCode);
        Assert.AreEqual(1, scenario.Sends);
        AssertDecision(capture, "suppress", "held", "retrywindowexhausted", 1);
        AssertProvider(capture, "durable", "retry", 1);
        AssertOutcome(capture, "pending", "retry", 1);
    }

    [TestMethod]
    [DataRow(false, "failed")]
    [DataRow(true, "held")]
    public async Task Rejection_uses_the_domains_first_attempt_or_recovery_outcome(bool recovery, string state)
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        if (recovery)
        {
            scenario.Outcome(SubmissionOutcome.Retry);
            await scenario.Run();
            scenario.Advance();
        }
        scenario.Outcome(SubmissionOutcome.Rejected);
        await scenario.Run();
        AssertProvider(capture, "durable", "rejected", 1);
        AssertOutcome(capture, state, "rejected", 1);
    }

    [TestMethod]
    [DataRow(SubmissionOutcome.Accepted, "unknown", "submissionunknown")]
    [DataRow(SubmissionOutcome.Hold, "held", "hold")]
    [DataRow((SubmissionOutcome)999, "unknown", "hold")]
    public async Task Missing_or_unrecognized_provider_evidence_reports_the_verified_domain_hold(SubmissionOutcome result, string adapterOutcome, string persistedOutcome)
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        scenario.Outcome(result);
        await scenario.Run();
        AssertProvider(capture, "durable", adapterOutcome, 1);
        AssertOutcome(capture, "held", persistedOutcome, 1);
    }

    [TestMethod]
    public async Task Missing_template_reports_suppression_without_a_provider_attempt()
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new(missingTemplate: true);
        await scenario.Renderer.RunAsync(scenario.Request);
        Assert.AreEqual(0, capture.Read().Count);
        await scenario.Run();
        AssertDecision(capture, "suppress", "failed", "templatemissing", 1);
        Assert.AreEqual(1, capture.Read().Count);
        Assert.AreEqual(0, scenario.Sends);
    }

    [TestMethod]
    public async Task Unverified_completion_does_not_report_a_persisted_outcome()
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        scenario.Store.LoseCompletion = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => scenario.Run());
        AssertProvider(capture, "durable", "accepted", 1);
        AssertDecision(capture, "submit", "sending", "submissionunknown", 1);
        Assert.AreEqual(3, capture.Read().Count);
    }

    [TestMethod]
    public async Task Unverified_claim_and_failed_scheduling_do_not_report_provider_calls()
    {
        using Capture capture = new();
        DurableNotificationTests.Scenario scenario = new();
        scenario.Store.RejectUpdate = entry => entry.StateCode == (int)SubmissionState.Sending;
        await Assert.ThrowsExactlyAsync<Niobium.ApplicationException>(() => scenario.Run());
        Assert.AreEqual(0, capture.Read().Count);
        scenario.Store.RejectUpdate = null;
        scenario.Queue.Setup(q => q.EnqueueAsync(It.IsAny<IEnumerable<MessagingEntry<NotifyCommand>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("private queue error"));
        await Assert.ThrowsExactlyAsync<IOException>(() => scenario.Run());
        AssertDecision(capture, "submit", "sending", "submissionunknown", 1);
        Assert.AreEqual(1, capture.Read().Count);
        Assert.AreEqual(0, scenario.Sends);
    }

    [TestMethod]
    public void Unexpected_status_codes_cannot_create_unbounded_metric_dimensions()
    {
        using Capture capture = new();
        NotificationTelemetry.RecordOutcome(new((SubmissionState)12345, (SubmissionOutcome)67890));
        AssertOutcome(capture, "unknown", "unknown", 1);
    }

    [TestMethod]
    [DataRow("Synthetic.Notification.Service", "1.2.345-prerelease")]
    [DataRow("Synthetic.Notification.Service", null)]
    [DataRow(null, "1.2.345-prerelease")]
    [DataRow(" ", "")]
    public void Configured_resource_identity_is_preserved_after_Functions_detection(string? name, string? version)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SERVICE_NAME"] = name, ["SERVICE_VERSION"] = version
        }).Build();
        KeyValuePair<string, object>[] platformIdentity = [new("service.name", name ?? "unknown-service"), new("service.version", version ?? "1.0.0-prerelease")];
        Dictionary<string, object> detected = ResourceBuilder.CreateDefault().AddAttributes(platformIdentity)
            .AddDetector(new FunctionsResourceDetector()).Build().Attributes.ToDictionary();
        ServiceCollection registrations = new();
        registrations.AddOpenTelemetry().ConfigureResource(resource => resource.AddAttributes(platformIdentity)).UseFunctionsWorkerDefaults();
        registrations.AddNotificationTelemetry(configuration);
        using ServiceProvider services = registrations.BuildServiceProvider();
        Resource[] resources = [services.GetRequiredService<MeterProvider>().GetResource(), services.GetRequiredService<TracerProvider>().GetResource()];
        foreach (Resource resource in resources)
        {
            Dictionary<string, object> attributes = resource.Attributes.ToDictionary();
            Assert.AreEqual(String.IsNullOrWhiteSpace(name) ? detected["service.name"] : name, attributes["service.name"]);
            Assert.AreEqual(String.IsNullOrWhiteSpace(version) ? detected["service.version"] : version, attributes["service.version"]);
            Assert.IsTrue(attributes.ContainsKey("process.pid"));
        }
    }

    private static void AssertProvider(Capture capture, string mode, string outcome, long count)
    {
        AssertPoint(capture, "provider.attempts", "{attempt}", count, ("mode", mode), ("outcome", outcome));
        AssertPoint(capture, "provider.duration", "s", count, ("mode", mode), ("outcome", outcome));
    }

    private static void AssertDecision(Capture capture, string action, string state, string outcome, long count)
        => AssertPoint(capture, "submission.decisions", "{decision}", count, ("action", action), ("state", state), ("outcome", outcome));

    private static void AssertOutcome(Capture capture, string state, string outcome, long count)
        => AssertPoint(capture, "submission.outcomes", "{outcome}", count, ("state", state), ("outcome", outcome));

    private static void AssertPoint(Capture capture, string suffix, string unit, long count, params (string Key, string Value)[] tags)
    {
        Point point = capture.Read().Single(p => p.Name == "niobium.notification." + suffix && tags.All(t => p.Tags.GetValueOrDefault(t.Key) as string == t.Value));
        Assert.AreEqual(unit, point.Unit);
        Assert.AreEqual(count, point.Count);
        Assert.IsTrue(double.IsFinite(point.Sum) && point.Sum >= 0);
        Assert.AreEqual(tags.Length, point.Tags.Count, "Metrics must contain only the finite documented attributes.");
    }

    private sealed record Point(string Name, string Unit, long Count, double Sum, Dictionary<string, object?> Tags);
    private sealed class Reader(BaseExporter<Metric> exporter) : BaseExportingMetricReader(exporter);
    private sealed class Exporter : BaseExporter<Metric>
    {
        public List<Point> Points { get; } = [];
        public override ExportResult Export(in Batch<Metric> batch)
        {
            Points.Clear();
            foreach (Metric metric in batch)
            {
                foreach (ref readonly MetricPoint point in metric.GetMetricPoints())
                {
                    Dictionary<string, object?> tags = [];
                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                        tags.Add(tag.Key, tag.Value);
                    bool histogram = metric.MetricType == MetricType.Histogram;
                    Points.Add(new(metric.Name, metric.Unit, histogram ? point.GetHistogramCount() : point.GetSumLong(),
                        histogram ? point.GetHistogramSum() : point.GetSumLong(), tags));
                }
            }
            return ExportResult.Success;
        }
    }
    private sealed class Capture : IDisposable
    {
        private readonly Exporter exporter = new();
        private readonly ServiceProvider services;
        private readonly MeterProvider provider;
        public Capture()
        {
            ServiceCollection registrations = new();
            registrations.AddLogging();
            registrations.AddNotificationTelemetry(new ConfigurationBuilder().Build());
            registrations.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(new Reader(exporter)));
            services = registrations.BuildServiceProvider();
            provider = services.GetRequiredService<MeterProvider>();
        }
        public List<Point> Read()
        {
            Assert.IsTrue(provider.ForceFlush());
            return exporter.Points.ToList();
        }
        public void Dispose() => services.Dispose();
    }
}
