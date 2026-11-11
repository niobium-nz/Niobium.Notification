using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Niobium.Notification.Delivery
{
    /// <summary>Process-lifetime library instruments; observations are not delivery receipts or an inbox census.</summary>
    public static class NotificationTelemetry
    {
        public const string MeterName = "Niobium.Notification.Submission";
        private static readonly Meter Meter = new(MeterName);
        private static readonly Counter<long> ProviderAttempts = Meter.CreateCounter<long>(
            "niobium.notification.provider.attempts", "{attempt}", "Completed provider-adapter invocations, including failures; not unique emails or recipient delivery.");
        private static readonly Histogram<double> ProviderDuration = Meter.CreateHistogram<double>(
            "niobium.notification.provider.duration", "s", "Monotonic elapsed time awaiting the provider adapter.");
        private static readonly Counter<long> Decisions = Meter.CreateCounter<long>(
            "niobium.notification.submission.decisions", "{decision}", "Durable decisions after domain persistence/read validation, including repeated suppression and waits.");
        private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
            "niobium.notification.submission.outcomes", "{outcome}", "Provider outcomes whose domain persistence was verified; not an atomic audit log.");

        public static Task<bool> MeasureLegacyAsync(Func<Task<bool>> submit, CancellationToken cancellationToken)
            => MeasureAsync("legacy", submit, accepted => accepted ? "accepted" : "rejected", cancellationToken);

        public static Task<SubmissionResult> MeasureDurableAsync(Func<Task<SubmissionResult>> submit, CancellationToken cancellationToken)
            => MeasureAsync("durable", submit, result => result.Outcome switch
            {
                SubmissionOutcome.Accepted when !String.IsNullOrWhiteSpace(result.ProviderID) => "accepted",
                SubmissionOutcome.Retry => "retry",
                SubmissionOutcome.Rejected => "rejected",
                SubmissionOutcome.Hold => "held",
                _ => "unknown",
            }, cancellationToken);

        public static void RecordDecision(SubmissionAttempt attempt)
        {
            TagList tags = StatusTags(attempt.Status);
            tags.Add("action", Label(attempt.Action));
            Decisions.Add(1, tags);
        }

        public static void RecordOutcome(SubmissionStatus status) => Outcomes.Add(1, StatusTags(status));

        private static async Task<T> MeasureAsync<T>(string mode, Func<Task<T>> submit, Func<T, string> classify, CancellationToken cancellationToken)
        {
            long started = Stopwatch.GetTimestamp();
            string outcome = "failed";
            try
            {
                T result = await submit();
                outcome = classify(result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            finally
            {
                TagList tags = new() { { "mode", mode }, { "outcome", outcome } };
                ProviderDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
                ProviderAttempts.Add(1, tags);
            }
        }

        private static TagList StatusTags(SubmissionStatus status)
            => new() { { "state", Label(status.State) }, { "outcome", Label(status.Outcome) } };

        private static string Label<T>(T value) where T : struct, Enum
            => Enum.IsDefined(value) ? value.ToString().ToLowerInvariant() : "unknown";
    }
}
