# Rich domains and data access in Notification

This guide addresses the owner's five review threads on
[PR #2](https://github.com/niobium-nz/Niobium.Notification/pull/2), reviewed from head
`46e93c85f79322eb5b765295c0e323eb60f8fdb0`. The references inspected were Store
`1db8661ad7f3ea763e57bf8365bce4b362c77713`, Invoicing main
`74d7a8f877f38b943f106a8f4745c522fdb4d213` and framework
`5c527723a607c1d03c002d4aa70531c9a55a767e`.

## Decisions and ownership

[TemplateDomain](../src/Niobium.Notification.Core/TemplateDomain.cs) renders a
notification; [SubscriptionDomain](../src/Niobium.Notification.Core/SubscriptionDomain.cs)
owns subscription changes. Both use `GenericDomain<TEntity>` and the framework
repository. Store's Order/ShippingOption domains and Invoicing's InvoiceDomain
show the same distinction between a stored entity and an object with business behavior.

[NotificationSubmissionDomain](../src/Niobium.Notification.Core/Delivery/NotificationSubmissionDomain.cs)
now owns registration, identity/version validation, claim eligibility, first-attempt
window, terminal suppression and recording provider outcomes. It persists using
`GenericDomain.SaveAsync` and `IRepository<NotificationSubmission>`, and freezes
payloads through `IFileService`. Its public operations express business intent.

[DurableNotificationFlow](../src/Niobium.Notification.Core/Delivery/DurableNotificationFlow.cs)
obtains a domain via `IDomainRepository`, renders/freezes content when registration
is needed, schedules the recovery wake before submission, calls the provider and
returns its result to the domain. It no longer decides or writes lifecycle states.
The Server-specific Table store and application-level SDK mapping are removed.
`IAzureTableClientFactory` now appears only in tests that instantiate the actual
framework adapter against isolated local storage.

## Enums, configuration and readable actions

`SubmissionState`, `SubmissionOutcome` and `SubmissionAction` replace string-based
decisions. The internal entity stores explicit integer `StateCode`/`OutcomeCode`
columns, matching the supported scalar mapper and Store's enum/code pattern.
Unknown enum codes fail closed. The unpublished inbox schema is version 2; version 1
rows from an earlier draft are rejected, not reinterpreted or replayed automatically.
The provider idempotency key format stays unchanged.

The application enable flag is `NotificationOptions:EnableDurableSubmission`, default
false. The separate `NotificationDeliveryOptions` type/binding is removed. Existing
host/template options and provider-specific Resend configuration remain in their
own established classes. The old draft setting does not enable this version.

Across Notification, execute I/O and state-changing operations outside `if`
conditions, name their results and then branch. Queue parsing was made explicit
as well. Pure predicates and lookups may remain in conditions. These rules are
recorded in AGENTS.md for future changes.

## What the framework supports and what it does not

The existing repository provides conditional create, ETag-checked update, replacement,
and retrieval, so the data-access design needs no new backend API. It was initially
validated with Abstractions 4.0.61 and Table 4.0.60. The current package alignment
updates existing integration references to 4.2.70 and existing lower-level references
to 4.2.66. Platform is resolved transitively at 4.2.70; runtime Abstractions/Helper
and Table/Blob adapters resolve at 4.2.66. Package repository metadata identifies
the latter release as `5c527723a607c1d03c002d4aa70531c9a55a767e`. Its Table/Blob and
relevant abstraction/helper source files match the previously inspected behavior.
See [HTTP package adoption](http-client-configuration.md) for the runtime graph and
the [framework guide](https://github.com/niobium-nz/Niobium/blob/1e370fccd38635f7209c1c4b01ddafcbe10ea618/docs/domain-data-access.md)
for exact source commits and the adapter/domain boundary.

The 4.2.66 mapper still drops response ETags from returned write models. The domain verifies
a read-back using its unique `WriteID` and complete expected snapshot before accepting
the new ETag. This prevents a concurrent writer's version from being used as a claim.
A future framework fix should preserve write ETags centrally; it would remove the
extra read without changing this business contract. An uncertain write or conflict
propagates for queue recovery; force-save is never used to bypass it.

The generic repository does not create tables. `infra/main.bicep` creates the
input `storageTableNames` in the existing data account. `main.bicepparam` reads
`STORAGE_TABLE_NAMES`, defaulting to `Subscription,Template,NotificationSubmission`.
Business names remain outside the Bicep resource declarations; the new table
uses its entity type name, as the historical tables do. See
[resource-name configuration](delivery-and-operations.md#resource-name-configuration)
for defaults and workflow projection. This replaces runtime table creation; no
deployment was performed. Blob creation still uses the existing
`IFileService` behavior. Claims, blobs, queues and provider calls are not atomic;
[durable submission](durable-submission.md) records the failure order and cutover.

Tests cover domain rules with repository fakes, plus the real pinned repository
and Blob adapter against loopback Azurite. SDK client injection exists only in
isolated fixtures, not application business logic. Exact commands/results and
remaining live verification are recorded in [the ledger](implementation-progress.md).
