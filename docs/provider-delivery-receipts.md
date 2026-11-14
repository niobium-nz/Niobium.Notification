# Provider delivery receipts

Queued submission and delivery are separate facts. `email.sent` confirms provider
submission; only a verified `email.delivered` confirms recipient mail-server
acceptance. Neither event proves inbox placement or reading. This implements the
Notification side of Invoicing Q28/B06; an invoice consumer and invoice lifecycle
transitions are separate work. Notification contains no invoice-specific routing.

## Authentication and durable capture

Resend calls `POST /webhooks/resend` on this environment's Function App origin.
There is no `/api` prefix. The endpoint accepts the exact raw body only after
validating the Svix `svix-id`, `svix-timestamp` and `svix-signature` headers:
HMAC-SHA256 over `id.timestamp.body`, decoded `whsec_` key, constant-time comparison,
and a five-minute timestamp tolerance. Multiple header values, invalid signatures,
oversized bodies and malformed recognized events fail closed. The body limit is
64 KiB and reading has a ten-second deadline. Configuration failures return 503;
authentication failures return 401. No raw body, signature or signing key is logged.

The handler acknowledges a recognized event only after the existing typed broker
accepts `CaptureNotificationReceiptCommand`. The private normalized command stores
the provider event/message identifiers, occurrence/capture timestamps, kind,
body digest and opaque correlation tags. It excludes addresses, subject, HTML and
raw webhook data. Unknown signed event types such as opens are acknowledged without
inventing delivery evidence. A queue failure leaves the HTTP call unsuccessful so
the provider can retry its stable event identity.

`NotificationReceiptDomain` registers each provider event with a conditional insert,
rejects reuse of its identity with changed content, verifies persistence and owns
correlation/publication transitions. The flow coordinates repository access and
publishes `NotificationDeliveryEvent` to `notificationdeliveryevent`. Publication
is marked complete only after broker acceptance and a verified conditional write.
A failed/lost acknowledgement can republish the same stable event identity; the
consumer must deduplicate it. There is no atomic transaction across Table and
Service Bus. Broker retries are finite; exhausted receipt commands require audited
operator recovery from the dead-letter queue, not an automatic new email.

## Model ownership and compatibility

`NotificationSubmission` adds the original operation, channel and a random receipt
proof. Its factory privately creates these values. The provider freezes opaque
`niobium_tenant`, `niobium_submission` and `niobium_proof` tags into the existing
immutable payload before submission. The domain persists the matching model proof.
Callers must not reconstruct the submission ID, tenant encoding or proof.

`NotificationReceipt.Provider` and `ID` use `EntityKey` attributes for the physical
Table keys. The model owns its private event-ID hash, version 1 representation and
outgoing event identity. Domains use finite publication/hold enums; repositories
perform the mapped Table I/O. The receipt flow neither mutates a submission nor
defines either model's key/hash representation. An early verified callback can
correlate through the frozen proof before the provider outcome write completes.

Existing submission key values, version 2 rows, provider idempotency keys,
fingerprints and frozen payload bytes remain compatible. Legacy rows/payloads
without correlation stay unchanged. Their receipts are retained as Uncorrelated;
they do not become confirmed deliveries by matching an address, subject or guessed
operation. Missing/mismatched submissions or proofs and contradictory known provider
message IDs also hold the receipt. No migration, historical replay or resend is
performed. Material correction/recovery needs its separate audited operator flow.

## Consumer contract

`NotificationDeliveryEvent` is an additive Contracts type. It contains stable event
`ID`, original `NotificationID`, `Tenant`, opaque `SubmissionID`, `Channel`, provider
event/message IDs, kind and occurrence time. It contains no receipt proof or payload.
The seven kinds are Submitted, Delivered, Delayed, Bounced, Failed, Suppressed and
Complained. Distinct out-of-order receipts remain immutable facts. Consumers must
validate their own operation/tenant/channel correlation, deduplicate event IDs and
apply their own domain transitions. An older delay must not undo a later delivery;
a late delivery must not clear a cancelled invoice.

The queue is in the existing Notification namespace, using the existing typed broker
and service identity. It is a competing-consumer queue, not a broadcast topic.
An authorized consumer needs scoped Listen access to this queue. Do not add unrelated
consumers expecting independent copies or introduce a second namespace into the
framework's queue-name sender cache. Cross-service consumption and its deployment
permissions must be verified in the consuming service before claiming completion.

## Required environment configuration

In both GitHub environments, `test` and `prod`, set the environment secret
`APPSETTING_RESENDWEBHOOKOPTIONS__SIGNINGSECRETSJSON` to a JSON array containing one
to four Resend webhook signing secrets, for example `["whsec_<endpoint-key>"]`.
This is an example shape, not a usable key. Include every sending account's webhook
key when domain-scoped sending keys belong to different accounts. For rotation,
configure the new key alongside the current key, change the provider endpoint, then
retire the old key after its retry window. Do not paste values into chat or PRs.

The workflow projects the encrypted value through
`APPSETTING_KV_RESENDWEBHOOKOPTIONS__SIGNINGSECRETSJSON`. Existing generic hooks
write the existing vault secret `RESENDWEBHOOKOPTIONS--SIGNINGSECRETSJSON` and refresh
the Function App's `RESENDWEBHOOKOPTIONS__SIGNINGSECRETSJSON` Key Vault reference.
Only names/empty placeholders enter Bicep parameters. A configuration-only check
fails before package/provision/deploy when the bundle is absent or malformed.
There is no tenant enrollment or delivery-confirmation enable switch.

Configure each relevant Resend account's webhook to the respective environment's
HTTPS origin plus `/webhooks/resend`, subscribing to `email.sent`, `email.delivered`,
`email.delivery_delayed`, `email.bounced`, `email.failed`, `email.suppressed` and
`email.complained`. Keep test and production endpoints/keys distinct. The managed
GitHub connector cannot configure Resend or GitHub environment secrets; this
requires an operator with those existing configuration permissions.

The additive resources are `NotificationReceipt`, `capturenotificationreceiptcommand`
and `notificationdeliveryevent` in existing storage/Service Bus. Their business
names are defaults in `infra/main.bicepparam`; any supplied name-list override must
retain all existing names and include these additions. Generic infrastructure
modules remain environment agnostic.

This change also projects a new setting in the protected deployment workflow.
The current main readiness controller intentionally rejects a different workflow
blob until reviewed activation on main. Local/candidate success cannot substitute
for that status. Follow [the installation procedure](delivery-and-operations.md#required-repository-administration-and-installation)
with concrete diff, test and independent-review evidence; do not fabricate readiness,
direct-push main or infer an exception from an earlier installation.

## Verification and limits

Local tests cover the official Svix fixed vector, raw-byte tampering, timestamp
boundaries, key rotation, request limits, capture privacy, duplicate/conflicting
events, early correlation, failed/lost publication, conditional-write conflicts and
malformed queue envelopes. Real loopback Table/Blob tests verify private-init mapping,
persisted frozen tags, restart recovery, out-of-order immutable receipts and actual
stale ETags. These are not evidence of a configured provider webhook.

The deployed candidate fixture observes the already-owned live send and requires
its real Delivered receipt to reach Published with matching provider ID and stored
proof. It creates no fake receipt, sends no additional email and only reads its own
receipt records. It retains the durable receipt/submission evidence. This check
shares the existing work deadline and fixed cleanup reserve. The immutable-main
harness independently verifies its installed checks. Production verification remains
GET `/health` only; no callback, notification, queue or fixture operation is performed.

Record exact-head candidate/trusted/storage evidence, native review, separate formal
approval, protected merge, production report and package result before calling this
delivered. Do not describe local tests or provider submission as confirmed delivery.

Protocol references: [Resend webhook verification](https://resend.com/docs/webhooks/verify-webhooks-requests),
[delivered semantics and payload](https://resend.com/docs/webhooks/emails/delivered),
[retry and ordering behavior](https://resend.com/docs/webhooks/create-webhook),
and [Svix manual verification](https://docs.svix.com/receiving/verifying-payloads/how-manual).
