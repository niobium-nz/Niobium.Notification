# Notification submission measurements

This is the Notification portion of B07 / Q28 #13's initial reliability
measurements. The plan ID and acceptance criteria remain unchanged. Invoicing
owns queue-age and queue-processing measurements; Notification owns submission
adapter results and durable recovery observations. B02 activation and B06
recipient mail-server confirmation remain separate requirements.

## Instruments

`NotificationTelemetry` owns a process-lifetime `System.Diagnostics.Metrics.Meter`
named `Niobium.Notification.Submission`. Server registers it with the existing
Niobium OpenTelemetry provider/exporter. No package upgrade is needed.

| Instrument | Type and unit | Labels | Observation |
| --- | --- | --- | --- |
| `niobium.notification.provider.attempts` | Counter, `{attempt}` | `mode`, `outcome` | One completed provider-adapter invocation, including exceptions |
| `niobium.notification.provider.duration` | Histogram, `s` | `mode`, `outcome` | Monotonic elapsed time awaiting that adapter invocation |
| `niobium.notification.submission.decisions` | Counter, `{decision}` | `action`, `state`, `outcome` | Durable preparation after the domain validates a read or verifies its write |
| `niobium.notification.submission.outcomes` | Counter, `{outcome}` | `state`, `outcome` | Provider result after the domain verifies its persisted completion |

Provider `mode` is `legacy` or `durable`. Provider `outcome` is one of
`accepted`, `rejected`, `retry`, `held`, `unknown`, `failed`, `cancelled`.
Legacy `true`/`false` becomes accepted/rejected. Durable acceptance requires a
nonblank provider ID; otherwise it is unknown. A thrown exception is failed;
`OperationCanceledException` is cancelled only when the supplied token was
cancelled. Return values, exceptions and cancellation propagate unchanged.

Decision `action` is `submit`, `wait` or `suppress`. Status labels are the lower-case
`SubmissionState` and `SubmissionOutcome` enum names: states are `pending`,
`sending`, `accepted`, `failed`, `held`; outcomes are `none`, `templatemissing`,
`submissionunknown`, `retrywindowexhausted`, `accepted`, `retry`, `rejected`, `hold`.
An undefined enum value becomes `unknown`. Metrics carry no tenant, channel,
operation ID, provider ID, address, credential, message body or exception text.

## Interpretation

Domain rules remain on `NotificationSubmissionDomain`, using the pinned generic
repository and file service. `SubmissionAttempt.Status` and the result of
`RecordOutcomeAsync` expose only the status the domain has already validated.
There is no extra storage read, stored column, ETag change or transition policy.
The flow records these results without deciding business states itself.

A scheduled recovery wake is still required before invoking the durable adapter.
A failed schedule can produce a submit decision but no adapter invocation. A
successful adapter response followed by a failed completion write can produce
provider acceptance but no saved-outcome observation. Those are distinct facts.
An absent template produces a durable failed/suppress decision without a provider
call; a legacy render failure emits no provider-call observation.

These are event observations, not an exact inbox census or atomic audit log.
Repeated terminal suppression and early redelivery increase decision counts.
An observer crash can lose a measurement after persistence. Do not sum held or
unknown events and call the result the current number of affected operations.
Initial sending claims are deliberately `submissionunknown` before any provider
call. `retry` includes known transient failures and uncertain responses, and a
durable adapter can return a hold before making an HTTP request. The legacy
framework may make multiple HTTP attempts inside one adapter invocation.

Provider acceptance is neither a unique-email count nor proof of recipient delivery.
These measurements support investigation of failures, retry/hold activity and
latency. They cannot supply B06 delivery receipts, an uncertain-send backlog,
recovery ownership, capacity targets or a delivery SLA.

## Export and queries

The host uses the existing `APPLICATIONINSIGHTS_CONNECTION_STRING` exporter.
Explicit `SERVICE_NAME` and `SERVICE_VERSION` are restored after the installed
Functions 1.2.0 detector, which otherwise overwrites the configured resource.
Missing settings retain the detector/default values and other metadata survives.
The shared resource applies to metrics and traces, including existing telemetry;
queries relying on the previous fallback identity need to use the configured one.
The test workflow uses `Niobium.Notification.Server.Test` and its unique
`buildVersion` plus `-prerelease`. Production uses its existing production identity.

Azure Monitor represents these instruments in `customMetrics`. This example
counts adapter results for the test service; restrict `application_Version` when
examining one deployment:

```kusto
customMetrics
| where timestamp > ago(1h) and cloud_RoleName == "Niobium.Notification.Server.Test"
| where name == "niobium.notification.provider.attempts"
| summarize invocations = sum(coalesce(valueSum, value))
    by mode = tostring(customDimensions.mode), outcome = tostring(customDimensions.outcome)
```

For duration, use `sum(coalesce(valueSum, value)) / sum(coalesce(valueCount, 1))`
to calculate a weighted mean in seconds. Aggregated Azure Monitor metric rows
are not raw samples; percentiles over their `value` column are not invocation
latency percentiles. The exporter need not preserve the instrument's unit string.

## Verification

Application tests use the actual OpenTelemetry registration, SDK collection and
domain/flow behavior. They cover adapter results and exceptions, early/late
redelivery, exhausted windows, missing templates, failed claims/scheduling/saves,
bounded labels and configured/missing service identity. Table/Blob regression
tests retain the real pinned Niobium adapters against isolated loopback Azurite.

The deployed candidate harness first verifies the actual test Function App,
current workflow version and exact exporter destination. It uses its existing
approved ContactUs submission, then queries only the two legacy provider
instruments from that version and test window. Polling is bounded to six minutes,
returns only a count of instruments and never retries the send. Metadata/query
failures are sanitized. Fixture cleanup still checks ownership and ETags.

This proves aggregate export for the current deployment and window, not attribution
of a metric to a specific email. Durable decision/outcome instruments are locally
verified; live enabled-path verification still belongs to the B02 cutover. The
trusted immutable-main suite is independently required, and production verification
continues to perform only GET `/health`.
