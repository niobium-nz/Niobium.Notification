# Model names and contract ownership

`NotificationReceipt.Provider/ID` follows the same annotated business-key rule.
Its private hash/event representation belongs to the receipt model; the submission
model owns its opaque receipt proof and tenant decoding. Domains decide whether
correlation permits publication. See [receipt boundaries and compatibility](provider-delivery-receipts.md#model-ownership-and-compatibility).

The owner's rule of 14 November 2026 applies to every Table model. Read the
[framework explanation and examples](https://github.com/niobium-nz/Niobium/blob/main/docs/model-contracts.md)
together with [domain behavior](domain-data-access.md).

## Submission data and business behavior

[NotificationSubmission](../src/Niobium.Notification.Core/Delivery/NotificationSubmission.cs)
uses `Tenant` annotated with `EntityKeyKind.PartitionKey` and `ID` annotated with
`EntityKeyKind.RowKey`. The existing repository maps those values to physical
`PartitionKey` and `RowKey` columns. It does not add columns named Tenant or ID.
The tenant remains its existing GUID `N` string; the submission ID retains the
existing uppercase digest of the command identity. An ID is scoped by its tenant.

Call `NotificationSubmission.Create(command)` to capture the identity and request
fingerprint. The ID algorithm, canonicalization and payload-path construction are
private implementation details. Callers must not hash command IDs, concatenate
blob paths, or format the provider idempotency key. The model supplies
`GetProviderIdempotencyKey()`, `PayloadContainer` and the persisted `PayloadName`.
A new registration gets a fresh model-owned reference without importing an input
snapshot's lifecycle or persistence metadata.

[FrozenNotification](../src/Niobium.Notification.Core/Delivery/FrozenNotification.cs)
owns its serialized shape and credential-fingerprint comparison. The provider
passes a credential to `Create` or `MatchesCredential`; it does not share the
submission ID implementation. The frozen contract retains only the digest.
Private helpers replace the former public `NotificationFingerprint` utility.

[NotificationSubmissionDomain](../src/Niobium.Notification.Core/Delivery/NotificationSubmissionDomain.cs)
owns registration validity, uncertain-redelivery holds, request/claim ownership, initial state, conditional
writes, retry eligibility, the provider window, suppression and outcomes.
It orders the Blob write before saving its reference and uses `IFileService`;
it does not define the path or JSON layout. The flow coordinates rendering,
recovery scheduling and provider calls. These responsibilities remain distinct
even though a model has factory and representation methods.

## Compatibility and verification

Key values, version 2, Table column names, payload container/path convention,
provider idempotency keys and frozen JSON names remain unchanged. Existing
payload references are read from the row, not regenerated on replay. Private
ownership does not authorize changing a persisted algorithm without a rollout:
an incompatible ID can bypass suppression and cause a duplicate external effect.

Public getters with private init setters keep identity creation inside the model.
The pinned Table adapter materializes them through reflection; tests exercise
this through actual Table and Blob adapters. Methods keep non-column derived
values out of the mapper, which enumerates public instance properties and does
not honor JSON ignore attributes as Table exclusions.

[Contract tests](../src/Niobium.Notification.Core.Tests/NotificationSubmissionContractTests.cs)
use fixed vectors captured from pre-refactor commit `a8bc0e4`.
[Storage compatibility](../src/Niobium.Notification.Storage.Tests/SubmissionContractCompatibilityTests.cs)
seeds raw legacy columns and frozen JSON, reads them through the renamed model,
claims/completes the operation, and checks that no Tenant/ID columns appear.
Existing concurrency, lost-response and restart cases remain required.

`Template.Tenant/Channel` and `Subscription.Belonging/Email` already use meaningful
names and annotations. Keep infrastructure key parameters on `GenericDomain`
and SDK test fixtures distinct from business-model property names. The naming
refactor preserved its prior routing policy; current queued routing follows the
[mandatory submission policy](durable-submission.md#all-tenants-use-durable-submission).
