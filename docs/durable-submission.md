# Durable queued notification submission (B02)

The [model contract guide](model-contracts.md) defines business key names and
model-owned identity, payload and provider-key representations. The domain keeps
the lifecycle/recovery decisions described below.

This implements the opt-in `NotifyCommand` path for B02 / Q28 #1. Provider
submission is distinct from recipient delivery. B06 callbacks, cancellation and
recipient mail-server confirmation are not implemented here. Direct HTTP and
subscription-triggered calls to `NotificationFlow` keep their existing behavior.

The [submission instruments](submission-observability.md) observe adapter calls,
domain decisions and verified saved outcomes. They do not change this protocol or
provide an exact inventory of held operations. `SubmissionStatus` is an in-memory
read-only result of the domain's existing validation/persistence, not a new stored
field or a second implementation of transition rules in the flow.

## Identity and persistence

`NotificationOptions:EnableDurableSubmission` defaults to false. When enabled for a sender, the queue
consumer uses the Service Bus `MessageId` as the operation ID, including for old
producers that omit the body ID. Scheduled retries carry the same ID in both
places. Invoicing already supplies its full invoice identity in both places.
The storage key is the sender tenant plus SHA-256 of that operation ID. Reusing
the identity with different channel, destination, display name or parameters
fails with a conflict. Object-key ordering is normalized; array ordering remains
significant. A deliberately requested additional copy needs a new operation ID.

`NotificationSubmission` is a new internal Table entity, not a public Contracts
change. It stores schema version, request fingerprint, immutable payload pointer,
integer state/outcome codes, ETag, first-submission time, next-attempt time, attempt
count, provider ID and a unique write identity. Domain decisions use `SubmissionState`
and `SubmissionOutcome` enums. Schema version 2 rejects the unpublished version 1
draft's string state fields; it does not reinterpret or replay those records.
The provider key format remains unchanged. Unknown versions/codes fail closed.

`NotificationSubmissionDomain` owns these rules and uses the existing framework
`IRepository<NotificationSubmission>` through `GenericDomain.SaveAsync`. There is
no application Table SDK adapter. The pinned mapper loses ETags from write results,
so the domain reads back and verifies its own write identity and complete snapshot
before using the returned version. Conflicts propagate for redelivery. Existing
framework packages are unchanged; see [domain/data access](domain-data-access.md).

The rendered provider JSON is frozen once in the private
`notification-submissions` blob container. It includes sender, recipient, subject
and HTML, so a later template/configuration edit cannot change a retry. Blobs use
unique names and create-only writes. A conditional Table insert chooses one
payload; losing/uncertain inserts can leave orphan candidates. No automatic blob
or inbox deletion is implemented. Never delete blobs solely because a create
response was lost; first reconcile committed references.

The snapshot also retains the sender-domain credential selection and its SHA-256
fingerprint, never the credential itself. A changed credential holds the operation
for investigation instead of assuming a different provider account shares the old
idempotency namespace. Rotations within the same provider account may therefore
need an operator-reviewed continuation. Both the payload and this metadata require
the same access controls as other private notification data.

## Submission and recovery

1. Load or conditionally register the operation and its frozen content. A missing
   template creates an explicit Failed/TemplateMissing outcome without sending.
2. Suppress Accepted, Failed and Held records. Accepted submissions remain
   suppressed after the provider's 24-hour window; no expiry is configured.
3. Check the persisted first-submission boundary and due time. Early redelivery
   only schedules a wake for the stored due time; it does not call the provider.
4. Conditionally claim Sending, persist the first-submission time, increment
   the attempt count and save the next-attempt time.
5. Successfully schedule a recovery message on the existing `notifycommand`
   queue **before** calling Resend. A scheduling error propagates so the current
   message is not acknowledged successfully.
6. Submit the exact saved bytes with `Idempotency-Key` derived from tenant and
   operation ID. Record provider acceptance/ID, pending retry, failure or hold
   using the claim's ETag. Lost responses or result-save failures are recoverable
   through the already scheduled message and the same provider key.

The installed Niobium broker supports `MessagingEntry.Schedule` and projects it
to Service Bus `ScheduledEnqueueTime`. No new timer dependency, worker, queue or
framework upgrade is needed. Duplicate scheduled wakes are tolerated by the same
conditional claim. Queue duplicate detection must remain disabled for this design:
it could otherwise discard a recovery message carrying the same message ID. A
future change to broker duplicate detection needs a separately versioned wake
envelope that preserves logical operation identity.

The initial retry policy doubles from 60 seconds, caps at one hour, applies
80%-100% jitter and enforces a one-minute minimum. There are at most 48 claims.
These are implementation defaults to review, not a delivery SLA. The 24-hour
window starts before the first possible provider call and is never reset.
The domain and provider both stop new calls one minute before its end; a provider
request has a 30-second timeout. Window/attempt exhaustion holds the record for
investigation and an explicit resend decision. No automatic new operation ID is
created. Provider/network timeouts remain uncertain; a subsequent rejection does
not erase the uncertainty from a prior attempt.

The new HttpClient removes Niobium's globally installed resilience handler using
the stable handler-configuration extension point. This separate removal remains
necessary because `ConfigureAutoRedirect` preserves resilience handlers. The client
does not follow redirects or
perform hidden HTTP retries. HTTP 408/429/5xx and the provider's concurrent-key
409 are retryable. A 409 retries only when its JSON object positively identifies
`concurrent_idempotent_requests`. Other conflict responses, malformed/empty bodies
and failures reading a known 409 body hold the operation for investigation. A
definitive first-attempt 4xx fails. Malformed success responses remain uncertain
and retryable. No raw provider response, recipient or credential is logged by
this path.

Redirect configuration uses `ConfigureAutoRedirect(false)` from the published
Niobium.Platform 4.2.70 package, preserving the factory's existing transport. The
[HTTP client guide](http-client-configuration.md) records the release, upgraded
integration references, transitive dependencies and published-package validation.

Resend's [idempotency documentation](https://resend.com/docs/dashboard/emails/idempotency-keys)
was checked on 2026-11-06: keys are retained for 24 hours, repeated identical
requests return the original result, differing payloads fail, and concurrent-key
requests may be retried. This implementation relies on that provider contract;
it does not claim exactly-once recipient delivery or protection against arbitrary
clock jumps, an indefinitely suspended worker, queue loss or out-of-band sends.

## Sender-tenant rollout filter

`NotificationOptions:DurableSubmissionTenants` optionally narrows the existing
enable switch to explicitly selected sender tenants. It is one comma-separated
string of nonempty GUIDs, not an indexed configuration array. For example, the
generic settings path accepts
`APPSETTING_NOTIFICATIONOPTIONS__DURABLESUBMISSIONTENANTS` with a value such as
`11111111-1111-4111-8111-111111111111,22222222-2222-4222-8222-222222222222`.
These are illustrative IDs, not approved activation targets.

| Configuration | Queue consumer behavior |
| --- | --- |
| Enable switch false | Existing legacy path, regardless of the filter |
| Enable switch true; filter absent | Existing all-tenant durable behavior |
| Enable switch true; valid filter | Durable path for listed sender tenants; existing legacy path for other tenants |
| Enable switch true; empty or malformed filter | Fail before either provider or the inbox is called |

The raw string is validated in full before routing. GUID array binding can silently
discard invalid entries; an invalid entry must never broaden a configured cohort.
Whitespace around GUIDs is accepted, but empty entries and the empty GUID are not.
The consumer rejects an empty command tenant before either route, independently
of the enable switch or filter. DataAnnotations on a non-nullable GUID alone do
not establish a valid tenant.
Filtering does not establish that a tenant or its operation IDs are new, grant
queue access, or supply historical suppression evidence.

Keep a selected tenant enrolled while any of its operations can be replayed,
including scheduled wakes and accepted operations. Removing that tenant or
disabling the switch can route an old operation through the legacy sender and
bypass its inbox evidence. Clearing the filter entirely broadens activation to
all tenants. These are cutover changes requiring quiescence and reconciliation,
not safe rollback actions. A whole-tenant filter cannot separate old and new
operation IDs within one existing tenant.

This change supplies routing and local verification only. It does not choose a
production cohort or enable the queue path in an environment. The enabled real
queue/provider checks and operational decisions below remain required.

## Activation and remaining dependencies

The implementation was merged in PR #2. Ordinary feature deployment and review
follow the authorized [delivery process](delivery-and-operations.md). The option
remains disabled, and merging instrumentation does not enable it or migrate data.
Before activation, review the concrete internal schema and failure policy and:

- Quiesce old queue consumers. They can send without consulting this inbox.
- Decide how to seed suppression records for previously submitted operation IDs,
  or explicitly delimit an approved new-operation cohort. A recent queue enqueue
  time alone cannot prove an old logical operation has never sent. Do not replay
  historical invoices to populate the new store.
  The optional sender-tenant filter can delimit newly established tenants; it
  cannot make an existing tenant's earlier operation IDs safe by itself.
- Provision `NotificationSubmission` through the generic `storageTableNames` input
  to `infra/main.bicep` in the existing data account before enabling the option.
  `main.bicepparam` reads `STORAGE_TABLE_NAMES` and includes the existing
  `Subscription` and `Template` tables in its default list; see
  [resource-name configuration](delivery-and-operations.md#resource-name-configuration).
  The generic repository does not create tables. Verify the app identity can use it, create/read the private
  payload container through `IFileService`, and schedule to the existing queue.
  No new Azure stack is required. Verify the current deployed resources and access
  at activation; a source declaration alone is not evidence of working queue recovery.
- Verify configured provider keys belong to the intended account and preserve
  their selection across retries. Do not infer key/account continuity on rotation.
- Exercise the enabled consumer, actual scheduled queue redelivery, provider
  replay and terminal holds using the approved real test fixtures before promotion.
  The current deployed harness still exercises the legacy synchronous path.
- Assign an operator for held records, credential changes and Service Bus dead
  letters. If scheduling fails until the source is dead-lettered, no successful
  provider call occurs on that attempt, but recovery requires redrive. A missing
  or purged wake is not repaired by an independent scanner in this version.

Keep accepted/held inbox evidence while the associated operations can be replayed.
Future B08 retention must reconcile both referenced and orphan blobs and preserve
suppression records; deleting the inbox can turn an old replay into a fresh send.
There is no purge, production recovery endpoint or automatic abandoned-writer
reset in this change.

## Local verification

Unit/component tests cover retry identity/content, canonicalization, concurrent
claims, restart, lost provider responses/result saves, scheduling failures,
timeouts, credential changes, window boundaries, exhaustion, missing templates,
storage failures and the actual runtime HTTP-handler composition.

`Niobium.Notification.Storage.Tests` remains outside the solution and runs
automatically on feature pushes through [Storage integration](delivery-and-operations.md#automatic-local-storage-verification).
The reusable runner owns disposable loopback emulators; a direct project invocation
still fails when its emulator settings are absent rather than silently skipping.
It uses the real pinned Niobium Table repository to test competing
inserts, competing ETag claims, stale rejection, round trips and cleared nullable
fields. Additional cases exercise the pinned Niobium Blob adapter against Azurite:
private-container creation, leading-slash path round trips, create-only content,
missing payloads and durable flow recovery after a lost completion-write response,
both before and after that write commits. Reconstructed domains/providers reuse the
persisted key, payload and window; accepted operations stay suppressed after two days.

`ConcurrentRecoveryTests` adds real-adapter concurrency evidence. A barrier lets two
independent domains read the same ETag before their actual Table claims compete;
only the winner schedules and submits. Four late-response cases pause an original
provider call, advance the injected clock to its wake, and let a reconstructed flow
persist acceptance. The original call then returns acceptance, rejection, retry or
hold. Each stale result receives Table HTTP 412 and leaves the winner's complete
record, ETag and provider evidence unchanged. Calls use identical provider keys and
frozen bytes; a later replay after the provider window neither sends nor schedules.
The clock, queue and HTTP responses are controlled locally. This proves the pinned
storage/domain boundary, not real Service Bus timing or provider deduplication.

Set both `NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT` and
`NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT` to disposable loopback Azurite endpoints.
Every case creates and deletes only its uniquely named emulator table/container.
The Blob fixture seeds one isolated client-cache entry in the pinned internal
factory with a real shared-key emulator client, then runs the unmodified Niobium
repository. The entry is removed during cleanup. This deliberate test-only seam
does not certify managed identity or modify framework code. Queue scheduling and
provider HTTP replies are simulated; no live queue/provider behavior is claimed.
Exact commands and results are in the implementation ledger.
