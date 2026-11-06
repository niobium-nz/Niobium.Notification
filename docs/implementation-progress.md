# Notification reliability implementation ledger

This repository is the downstream dependency for the supplied Niobium.Invoicing research plan. Preserve the original plan IDs and acceptance criteria. The owner's accepted recommendations are recorded in [Invoicing Q28](https://github.com/niobium-nz/Niobium.Invoicing/blob/50df240f8212af33a152a13bcce11c848d6b51a8/docs/decisions-and-risks.md#q28-business-decision-follow-up); that decision-only change completed production verification as PR #14. This milestone does not claim those business behaviors are implemented.

## B02 continuation: 2026-11-06

Baseline: clean `origin/main` at `4a93385be1b09a9d97d0c2384e1085f254c7d84c`,
branch `codex/notification-durable-retries`, `/workspace/Niobium.Notification`.
The user explicitly included this repository in the implementation scope.
The original report is preserved unchanged in
[Invoicing PR #17](https://github.com/niobium-nz/Niobium.Invoicing/pull/17), with
SHA-256 `1321b7712c4c9e10b477d03b230e12fce5485fbde3375d94c374fc96290313eb`.

The current task expressly prohibits deployment, publication, merging, production
data access and production migrations. That overrides the standing delivery
sequence for this branch: commits use `[skip ci]` to prevent feature deployment,
and delivery stops at a draft PR. No live readiness/approval status is fabricated.
The earlier safeguards are present on main; this task has not certified their
live repository configuration or run another deployed harness.

| Plan item | Implemented in this branch | Remaining acceptance dependency |
| --- | --- | --- |
| B02 / Q28 #1 | Conditional inbox registration/claims, frozen provider JSON, stable tenant/operation key, durable first-submission boundary and jittered schedule, recovery wake before provider call, persisted provider acceptance, durable replay suppression, explicit held/failed outcomes | Disabled by default pending legacy-operation cutover; enabled queue/provider integration and operator recovery must be verified before rollout |
| B07 / Q28 #13 | Component failure/concurrency/restart cases, real local conditional Table operations and pinned Blob adapter recovery tests | Cloud identity/Blob access, actual Service Bus scheduling and provider replay remain unverified; existing deployed tests still cover the legacy synchronous path |
| B06 / Q28 #2-4 | Preserved as the next milestone; provider submission is never labelled recipient delivery | Activated/verified B02, signed provider events/internal confirmation contract and payment-history compatibility for cancellation |
| B08 / Q28 #6-7 | Inbox/payload retention dependency and orphan-blob reconciliation documented; no automated purge | Invoice/history commit consistency and direct-writer compatibility, retention-clock/hold/privacy validation, backup/restore evidence and ownership |

See [durable submission](durable-submission.md) for the concrete schema, defaults,
failure ordering, provider classification, cutover prerequisites and limits.
The opt-in applies only to `NotifyCommandConsumer`; direct HTTP/contact and
subscription-triggered notification flows retain their existing behavior.
No wire Contracts, infrastructure/workflow definitions, framework code or existing
package versions changed. The new explicitly invoked storage test project reuses
MSTest.Sdk 4.4.0 and Moq 4.20.72 already used here.

Matched framework inspection covered `IFileService`/Blob create-only upload,
`IAzureTableClientFactory`, the existing Resend options/client and global HTTP
resilience defaults, plus `MessagingEntry.Schedule` and the Service Bus broker.
Niobium.Abstractions remains 4.0.61 (`c2462746614ee6c768d725c99e3c54df2e3848c8`);
the relevant platform/file/messaging packages remain 4.0.60
(`1fa044968f504ef575021720c728a17dc72bb067`). The new provider uses the stable
HTTP-handler configuration extension point to remove inherited automatic retries;
no experimental-API suppression was added. Resend's 24-hour behavior was checked
against its official documentation linked in the protocol page.

Verification used the existing reviewed `/workspace/.niobium-tools/env.sh` and
.NET SDK 10.0.403. The initial `--no-restore` build failed because this fresh
checkout had no assets; restore resolved that setup issue. The restored baseline
then built with zero warnings/errors and passed its 45 tests.

| Exact command (after sourcing the environment script) | Result |
| --- | --- |
| `dotnet restore Niobium.Notification.slnx --nologo` | Passed using the configured cache/feeds; not an empty-cache certification |
| `dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed, zero warnings/errors |
| `dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-b02-tests` | 88 passed, zero failed/skipped |
| `python scripts/check-coverage.py /workspace/scratch/notification-b02-tests/coverage.cobertura.xml` | Passed: 536/546 authored lines, 98.17%; Contracts 100%, Core 98.76%, Server 97.52% |
| `dotnet restore src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --nologo` | Passed |
| `NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT=http://127.0.0.1:18492/devstoreaccount1 dotnet test --project src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-restore --report-trx --results-directory /workspace/scratch/notification-b02-storage` | One real-adapter scenario passed, zero failed/skipped; competing inserts/claims, stale ETags, restart read-back and nullable-field removal |
| `git -c core.whitespace=cr-at-eol diff --check` | Passed; existing CRLF file conventions preserved |

The Table test used a disposable Azurite process launched with
`azurite-table --silent --inMemoryPersistence --disableTelemetry --tableHost 127.0.0.1 --tablePort 18492 --skipApiVersionCheck`.
It deleted its uniquely named table; the emulator was stopped after verification.
The live queue, provider and cloud storage were not accessed. Delivery/cancellation,
invoice audit history, MCP and payments are not marked complete by this milestone.

### B02/B07 local Blob and recovery follow-through

The storage suite now also runs the actual Niobium.File.Blob 4.0.60 repository
against a disposable Blob emulator. It verifies a private container, immutable
payload writes, missing-payload failure and the composed durable flow recovering
from a lost result-write response before or after commit. Fresh store/provider
instances must preserve the payload, operation key and first-submission boundary;
accepted state suppresses further submissions beyond the provider window.

The pinned factory has no injectable client constructor. The test seeds and later
removes a unique entry in its internal client cache using a real shared-key SDK
client directed only at loopback Azurite. Actual repository I/O is exercised, but
managed identity is not. Queue scheduling and provider replies remain simulated.
No framework, package, application or workflow changes accompany these tests.

After sourcing `/workspace/.niobium-tools/env.sh`:

| Exact command | Result |
| --- | --- |
| `dotnet build src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed, zero warnings/errors |
| `NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT=http://127.0.0.1:18492/devstoreaccount1 NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT=http://127.0.0.1:18490/devstoreaccount1 dotnet test --project src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-build --no-restore --report-trx --results-directory /workspace/scratch/notification-b02-storage-blob` | Four passed, zero failed/skipped |
| `git -c core.whitespace=cr-at-eol diff --check` | Passed |

The Table emulator command above was reused alongside
`azurite-blob --silent --inMemoryPersistence --disableTelemetry --blobHost 127.0.0.1 --blobPort 18490 --skipApiVersionCheck`.
Owned tables/containers were deleted and both emulator processes stopped.
The earlier 88 application tests and 98.17% authored coverage still describe the
unchanged application source; that suite was not repeated for this test-only change.
These results close the local payload-adapter gap, not the live activation or B06
confirmation-contract dependencies.

## Baseline and reconciliation

- Repository: `niobium-nz/Niobium.Notification`; feature: `codex/notification-reliability`.
- Verified main/baseline: `989b7c4b36154c5da2cdd3391ec297e1f453ea14`.
- Baseline Release build: zero warnings/errors; 23 existing unit tests passed. The earlier README described an unrelated durable-agent template and is replaced with actual repository guidance.
- Baseline delivery gaps: `push: ['*']` omitted slash-containing branches; production could run on PR events without feature success; feature packages could publish; no main protection; `5he11` read-only; no trusted listener or deployed harness. The first all-module measurement, after adding the health probe and its two tests, was 26.44%; the baseline did not enforce broad authored coverage.
- Existing Niobium.Abstractions 4.0.61 was reconciled against framework commit `c2462746614ee6c768d725c99e3c54df2e3848c8`; the project's 4.0.60 framework packages against `1fa044968f504ef575021720c728a17dc72bb067`. No framework/package/Contracts changes are included.
- The pinned Resend adapter returns only a success boolean and provides no idempotency-key/provider-event contract. B02 needs an application extension using existing framework conventions, not an assumed latest-framework capability or silent upgrade.

## Traceability

| Plan / decision | Acceptance requirement | Current change / evidence | Remaining dependency |
| --- | --- | --- | --- |
| Q28 #5, B07, U06-U08 | Protect delivery before notification reliability changes; feature tests/deployment before PR; approved merge before production | Feature workflow, trusted-main readiness controller, author/reviewer guide, merge-only production, conditional merge-only publication | External configuration, actual deployed evidence, current-head review and explicit first-installation policy |
| B07 / Q28 #13 | Meaningful tests across actual HTTP, persistence and notification boundaries | Local HTTP/queue/subscription/event tests; six-case real deployed harness with owned fixtures and approved recipient; health-only production checker | Candidate and trusted runtime reports; future B02/B06 and identity tests remain separate |
| Q28 #1 / B02 | Same operation key and frozen payload; repeated exponential retries with jitter within 24 hours; durable confirmed suppression; hold unresolved sends afterward | Requirement accepted, pinned adapter limitation documented | Implement after safeguards; no retry behavior is changed here |
| Q28 #2-4 / B06 | Delivered means verified recipient mail-server acceptance; unpaid cancellation at any stage before any payment ever; authenticated deduplicated provider events; legacy timestamps remain enqueue evidence | Requirement accepted; current real-provider test explicitly does not claim delivery | B02, provider event/confirmation contract and invoice lifecycle implementation |
| Q28 #6-7 / B08, U04, U06 | Ten-year material history/soft deletion; validated retention clock; restore evidence; one-hour RPO/four-hour RTO engineering targets | Requirement accepted and retained | Actual backup inventory, ownership, restore exercise and legal-clock validation; no production purge/migration authorized |
| Q28 #8 / B05-Q16 | Revisit biller/invoice item-key isolation after MCP | Deferred as directed | Migration compatibility, collision detection and rollback before wider rollout |
| Q28 #9 / B09 | Current template default; future approved versioned selector pinned at freeze | Deferred feature retained | Legacy ignored-selector compatibility before enforcement |
| Q28 #10 / U01 | Preserve calculations now; investigate AU/NZ rounding, fractional quantities and credit policy after MCP | Deferred as directed | Agreed accounting examples and versioned compatibility |
| Q28 #11 / U02-Q13 | Preserve old links; implement versioned new-link expiry/revocation/replacement after MCP | Deferred as directed | New-link policy implementation, not retroactive expiry |
| Q28 #12 / B10-U05 | Read/draft MCP first with scoped authority; durable payment/manual reconciliation before one provider | Separate future capabilities | Concrete privileged-tool and provider contracts; no collection authority inferred |

## Local verification

Commands executed in the managed Niobium.Invoicing environment with its existing .NET SDK setup, plus PowerShell 7 for isolated hook tests:

```sh
source /workspace/.niobium-tools/env.sh
export PATH=/workspace/.niobium-tools/powershell:$PATH
dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1
dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-gates-coverage
python scripts/check-coverage.py /workspace/scratch/notification-gates-coverage/coverage.cobertura.xml
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
git -c core.whitespace=cr-at-eol diff --check
```

Results: Release build zero warnings/errors; 45 application tests with no failures/skips; 20 Node policy/package tests; 32 Python tooling/hook tests. Authored line coverage 286/295 (96.95%): Contracts 100%, Core 97.60%, Server 95.92%. Existing CRLF project-file formatting is preserved. New test assumptions were corrected against the pinned framework; no application behavior was changed to satisfy them.

Actionlint 1.7.12 reports only its two unsupported `concurrency.queue` keys. With those keys omitted in temporary copies, the remaining workflows lint cleanly. The committed workflows retain `queue: max`, already proven in the Invoicing delivery process; this is a local linter-version limitation, not a reason to cancel pending runs. YAML parsing also passes.

Local results are not deployed evidence. Record the exact pushed SHA, workflow/artifact URLs and digests, six candidate checks, immutable trusted baseline and any skipped installation step in the delivery checkpoint/PR. Do not claim the custom status or production verification until their actual results exist. External protections and first-installation approval remain outstanding.

## First feature run and configuration blocker

Feature head `8dc6ef7095694cad05262052bcc8015eb7ca0f3b` ran [42122929609](https://github.com/niobium-nz/Niobium.Notification/actions/runs/42122929609). Build/local checks passed: 45 application tests, 20 Node tests, 30 Python tests and 96.95% coverage. Local evidence artifact `13458698238` was downloaded and its ZIP SHA-256 `3492ef7c9c176d74296d5183dc4b3615cc616ca725633535d4fe281838b53a7a`, TRX and coverage were verified.

Actual test publication and credential preparation succeeded, but deployed integration failed closed with `approved_test_recipient_required`. The run supplied neither the notification recipient nor biller fixture configuration; the harness stopped at configuration validation before health or fixture/provider operations. Deployed artifact `13458404171` records failure with no passed checks. Trusted integration was skipped after the failure, and production was correctly skipped for the feature push. This run does not authorize PR creation or merging.

The follow-up adds a configuration-only preflight before cloud login/provisioning and two tests proving it cannot access resources or log the recipient. The owner must populate the Notification test environment settings independently of Invoicing's. The next run still must perform the complete actual deployment/candidate verification, and the initial trusted-main installation dependency remains explicit.

## Invoice terminology correction (superseded)

The owner requires Invoicing terminology for invoice-related configuration, documentation and tests even when another service is under test. The source biller setting is now `TEST_BILLER_ID` throughout the workflow, preflight, deployed harness and test fixtures. It remains the existing test biller GUID whose `Invoice` template supplies the configured sender. The Notification API's established `Tenant` field is mapped explicitly at the HTTP boundary; its wire/storage contract is unchanged. This rule is recorded in the repository and workspace agent memory.

## Existing environment settings reused (2026-11-06, superseded)

The owner reconfirmed the existing Invoicing test variable `TEST_INVOICE_BILLER` and secret `TEST_NOTIFICATION_RECIPIENT`. All three Notification workflow projections now read that stored biller name into the invoice-oriented runner input `TEST_BILLER_ID`; the recipient secret name is unchanged. The delivery guide and agent memory distinguish stored names, runner names and repository scope. The earlier naming correction did not justify requesting a new biller or mailbox. Notification still needs its own binding to the same approved values because GitHub environments are repository scoped.

This correction changes no application, wire/storage, Bicep or package contract. Local checks passed: YAML parsing and all three stored-to-runner projections, 32 Python preflight/tooling tests, 20 Node policy tests and `git diff --check`. Exact-head CI and real deployed results must still be recorded before PR creation. The earlier missing-configuration failure is not evidence that the owner's Invoicing settings are absent.

## Shared-service boundary correction (2026-11-06)

The owner clarified that Notification is an independent shared service. The earlier invoice terminology and environment-copy guidance above overextended an Invoicing-side naming rule; those instructions are superseded. No `TEST_INVOICE_BILLER`/`TEST_BILLER_ID`, biller identity or `Invoice` template is now consumed by Notification's workflow or deployed harness. Consumer-specific tests remain in Invoicing.

The generic harness instead requires an explicitly approved `TEST_NOTIFICATION_SENDER` and `TEST_NOTIFICATION_RECIPIENT`, creates only its own tenant/template/blob in existing test resources, exercises the real provider, and conditionally cleans its owned records. It never scans or borrows another consumer's sender/template. The sender/recipient values still need service-owned configuration; they are not inferred from the removed invoice binding. Local tests cover the no-existing-template path, sender validation before I/O, address redaction, and the existing send/cleanup failure boundaries. No application, public contract, package or Bicep change is made by this correction. Exact-head CI remains required; unconfigured real tests must not be represented as passed.

Correction validation: 34 Python checks and 20 Node policy checks passed; all three generic sender/recipient workflow projections and YAML parsing passed, as did `git diff --check`. Application code is unchanged; the next exact-head CI run establishes build/coverage and any deployment result.

## Confirmed fixture bindings and manual review (2026-11-06)

The owner configured `TEST_NOTIFICATION_SENDER` as a GitHub `test` environment variable and `TEST_NOTIFICATION_RECIPIENT` as an environment secret. All three workflow projections now read the sender from `vars` and the recipient from `secrets`. No address value is committed, no additional stored setting is requested, and the fixture identity remains independent of Invoicing. Application code, packages, public contracts and Bicep are unchanged.

The connector verified `5he11` now has Write permission. The owner intentionally left Notification native automatic review disabled and will review manually. The delivery guide and agent memory preserve that choice; no native-review completion or delegated approval will be substituted for the owner's formal current-head review. Main protection and this repository's one-time trusted-harness installation decision remain pending. Exact-new-head CI and actual candidate deployment evidence are still required before PR creation.

Local verification passed: `python -m unittest discover -s scripts -p 'test_*.py'` (34), `node --test scripts/delivery-policy.test.cjs` (20), YAML parsing with all three sender-variable/recipient-secret projections checked, and `git diff --check`. The next feature run must establish build/coverage and actual deployment results on the pushed head.
