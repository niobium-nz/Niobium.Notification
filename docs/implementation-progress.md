# Notification reliability implementation ledger

## B07 automatic storage recovery verification: 2026-11-14

Baseline: Notification main `72f100bfc5a742de42a4aa860e5e5e41585ff06f`, on
`codex/notification-storage-ci` in `/workspace/Niobium.Notification-storage-ci`.
Parent Invoicing main is `41deb0b6e6a030c66ac384c0ccf0f06716c467a8`; its complete
original report remains readable and unchanged, SHA-256
`1321b7712c4c9e10b477d03b230e12fce5485fbde3375d94c374fc96290313eb`.

The nine real Table/Blob cases added under B02/B07 previously ran only when
explicitly invoked. The new `Storage integration` workflow runs that existing
suite on every feature push and uploads its TRX and emulator logs. The Linux
runner starts owned in-memory Azurite Table/Blob processes on loopback, supplies
both fixture endpoints, requires a fresh report with every case passing and
stops its process groups on completion, failure, timeout or interruption.
Missing, empty and skipped results fail verification. Each invocation keeps its
own result directory and never deletes a prior run's files.

This is additional local-adapter evidence, required by the documented delivery
process alongside `Azure Deployment`. It has repository-read permission only,
no Azure environment or readiness-status write permission. The existing main
controller still independently certifies the deployed candidate/trusted suites;
its protected workflow/helper definitions and repository rules are unchanged.
Native review and separate formal current-head approval remain required.

The framework graph remains Table/Blob/Abstractions 4.2.66 and integrations
4.2.70. NuGet repository metadata and corresponding Table/Blob source were
rechecked at `5c527723a607c1d03c002d4aa70531c9a55a767e`; Platform 4.2.70 is
from `f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0`. No package reference or
framework source changed. Azurite is pinned to the existing local version 3.37.0.

Verification used .NET 10.0.403 and the existing local package cache:

```sh
dotnet restore src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --nologo
dotnet build src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-restore --nologo --disable-build-servers -m:1
python scripts/run-storage-integration.py --results-directory /workspace/scratch/notification-storage-ci-results
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
git diff --check
```

The build passed with the same six existing NU1603 warnings and zero errors.
All nine storage cases passed with no failures/skips; all 20 Node and 46 Python
checks passed. The first Python invocation lacked PowerShell on PATH; adding the
already installed workspace PowerShell resolved that environment-only failure.
Local subprocess fault probes confirmed propagation of test exit 23, rejection
of missing/skipped reports, and exit 143 on SIGTERM. Each probe verified that both
emulator ports were released and the test child stopped. Empty and failing TRX
counts were also rejected. Workflow YAML parsing and Python compilation passed.
The current-head CI, native/separate reviews and terminal delivery evidence belong
on the author-created PR, rather than being inferred from these local results.

| Plan item | Acceptance gained and remaining dependency |
| --- | --- |
| B07 | Existing actual-adapter recovery/concurrency cases become automatic feature-push checks with retained reports and owned-process cleanup. |
| B02 | Automatic local checks strengthen regression protection; enabled real queue scheduling, provider replay and cutover evidence remain required. No tenant cohort is selected and no environment is enabled. |
| B06 / U06 | Verified B02 activation and the pending cohort/operator details remain dependencies. Recipient mail-server acceptance and cancellation-before-any-payment rules are unchanged. |
| Other Invoicing plan IDs | The parent reconciliation remains authoritative; no lifecycle, payment, retention, template, key or machine-authority decision changes here. |

## B02 / B07 concurrent recovery integration: 2026-11-14

Baseline: Notification main `708b9c27774d6895a61e187c20ed75f60f985376`,
isolated on `codex/notification-concurrent-recovery` in
`/workspace/Niobium.Notification-concurrent-recovery`. Parent Invoicing main is
`bbc978acdbc29583115638c270df6df9027fdbf3`. Its complete original research report
is readable and unchanged, SHA-256
`1321b7712c4c9e10b477d03b230e12fce5485fbde3375d94c374fc96290313eb`.

The existing raw repository race and lost-completion cases did not exercise two
complete durable flows competing for the same stored claim or a late provider
result after recovery. Five new `ConcurrentRecoveryTests` cases close that local
B02/B07 evidence gap using the actual rich domain, Resend adapter and pinned Table
and Blob adapters against disposable loopback Azurite:

- Two consumers read one persisted ETag before the test releases their competing
  claims. The losing write receives HTTP 412; exactly one wake and provider call
  occur and the stored record becomes Accepted.
- An original provider call remains pending while a reconstructed flow claims the
  due wake and records acceptance. Four original responses (Accepted, Rejected,
  Retry and Hold) all fail their stale conditional result write with HTTP 412.
  The complete accepted record, including ETag, provider ID and first-submission
  boundary, stays unchanged.
- Both attempts retain identical provider keys and frozen JSON. Replaying the
  accepted operation after two days neither submits nor creates another wake.

The barrier controls timing only: it forwards reads and writes to the real pinned
repository with ETag checks enabled. The injected clock and simulated queue/HTTP
responses do not establish cloud scheduling, live provider idempotency or recipient
delivery. Existing activation dependencies remain unchanged. No runtime code,
package, schema, pipeline definition or environment setting changes in this step.

The resolved Niobium Table/Blob/Abstractions versions remain 4.2.66, matched to
framework `5c527723a607c1d03c002d4aa70531c9a55a767e`; integrations remain 4.2.70
at `f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0`. The corresponding conditional
repository writes and domain save/read-back behavior were inspected without
modifying the framework. The same six NU1603 minimum-version warnings occur in
the baseline and final builds; zero build errors.

Verification used .NET SDK 10.0.403 after sourcing the existing workspace tool
environment. Owned Azurite Table/Blob processes used loopback ports 18494/18493,
in-memory persistence, disabled telemetry and API-version-check compatibility;
both processes were stopped after the suite. Every test cleans its unique table
and private payload container.

```sh
dotnet restore src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --nologo
dotnet build src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-restore --nologo --disable-build-servers -m:1
NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT=http://127.0.0.1:18494/devstoreaccount1 NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT=http://127.0.0.1:18493/devstoreaccount1 dotnet test --project src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-build --no-restore --report-trx --results-directory /workspace/scratch/notification-concurrent-storage/results
dotnet restore Niobium.Notification.slnx --nologo
dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1
dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-concurrent-unit
python scripts/check-coverage.py /workspace/scratch/notification-concurrent-unit/coverage.cobertura.xml
git diff --check
```

All nine storage cases and 142 application cases passed, zero failures/skips.
Application coverage remains 98.38% (609/619), with Contracts 100%, Core 99.14% and
Server 97.15%. The opt-in storage project remains outside the automatic pipeline;
its local evidence does not replace mandatory exact-head feature deployment,
candidate/trusted checks or native and independent review. Those results and the
eventual merge/production evidence belong on the author-created PR.

## B02 sender-tenant rollout filter: 2026-11-12

This continuation starts from clean main
`9857b67d1199713017dcc97cf64dc1bee985be21`, isolated in
`/workspace/Niobium.Notification-durable-cohorts` on
`codex/notification-durable-cohorts`. The parent Invoicing baseline is
`36020f1dfa725dd7505b5fb961add8e5083fb585`; its complete original report remains
available with SHA-256
`1321b7712c4c9e10b477d03b230e12fce5485fbde3375d94c374fc96290313eb`.

B02's current switch enables every sender. The optional
`NotificationOptions:DurableSubmissionTenants` filter prepares selective
activation using an explicitly chosen sender-tenant cohort. The consumer delegates
selected commands to the existing durable flow/domain and preserves the legacy
path for other tenants. An absent filter preserves the existing global switch;
an empty or malformed configured filter fails before sending when enabled.
The full filter is validated even if an earlier entry matches the command.
See [rollout semantics and limits](durable-submission.md#sender-tenant-rollout-filter).

The first regression test demonstrated that the default .NET GUID-array binder
silently discarded an invalid entry. The final setting therefore binds one raw
comma-separated string and validates every GUID before routing. This avoids
interpreting a malformed selection as all tenants. Runtime tests cover actual
consumer dispatch, envelope identity, accepted replay suppression, untouched inbox
and provider behavior outside the cohort, and malformed configuration before sends.

| Acceptance item | Result and remaining boundary |
| --- | --- |
| B02 / Q28 #1: controlled activation | Optional sender-tenant routing is implemented; no cohort is configured or enabled by this source change. |
| B02 identity and suppression | Selected consumers reuse the existing envelope-derived operation key, frozen payload, domain state machine and scheduling. |
| Compatibility | The false default and absent-filter global behavior are preserved. Direct HTTP/subscription paths, wire/storage formats, retry policy and packages are unchanged. |
| Live activation | Still requires an approved new-operation cohort or verified historical suppression, quiesced old consumers, provider/queue verification and an owner for held/dead-lettered outcomes. The owner has been asked for the cohort and operational owner; no answer is inferred. |

Package references remain Niobium integrations 4.2.70 and lower-level references
4.2.66. The existing options binding and rich-domain/repository boundaries were
checked against the application source and the package-matched framework guides.
The clean baseline Release build had zero errors and the six previously recorded
NU1603 warnings; all 128 baseline application tests passed. Final verification and
exact-head delivery evidence are recorded below and on the author-created PR.

Local verification used .NET SDK 10.0.403:

```sh
dotnet restore Niobium.Notification.slnx --nologo
dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1
dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-cohorts-review-tests
python scripts/check-coverage.py /workspace/scratch/notification-cohorts-review-tests/coverage.cobertura.xml
git -c core.whitespace=cr-at-eol diff --check
```

Restore and Release build succeeded with zero errors and the same six baseline
NU1603 warnings. All 142 application tests passed, with no failures or skips.
Authored line coverage is 98.38% combined (609/619), with Contracts 100%, Core
99.14% and Server 97.15%; the unchanged coverage thresholds pass. Whitespace and
relative Markdown link checks passed. These local results do not establish enabled
cloud queue/provider behavior; exact-head CI and subsequent delivery remain
separate evidence recorded on the PR.

[Native review on PR #4](https://github.com/niobium-nz/Niobium.Notification/pull/4#discussion_r4547027227)
identified that an empty command tenant could fall through to the legacy sender
with a filter, because DataAnnotations does not reject an empty non-nullable GUID.
The separate approver independently confirmed the issue. Four regression cases
first failed across enabled/disabled and filtered/unfiltered modes. The consumer
now rejects an empty tenant immediately after parsing, before transformation or
either route. All four cases pass with no provider, inbox, payload-file or queue
interaction. Existing valid-command routing remains unchanged.

This repository is the downstream dependency for the supplied Niobium.Invoicing research plan. Preserve the original plan IDs and acceptance criteria. The owner's accepted recommendations are recorded in [Invoicing Q28](https://github.com/niobium-nz/Niobium.Invoicing/blob/50df240f8212af33a152a13bcce11c848d6b51a8/docs/decisions-and-risks.md#q28-business-decision-follow-up); that decision-only change completed production verification as PR #14. This milestone does not claim those business behaviors are implemented.

## Formal approval handoff corrected: 2026-11-12

The owner corrected the implementation agent for stopping after native review and
GitHub reviewer assignment without invoking the agreed separate approval agent.
The agent had incorrectly marked the goal blocked. The required `pr_approver`
handoff is now [versioned in this repository](pr-approval-handoff.md), required by
AGENTS.md, and mirrored in Store and Invoicing. It does not depend on a workspace
instruction file surviving a resumed goal. Genuine independent approval remains
separate from self-approval or a delegated rubber stamp; native reviews remain
required. Pending review is active work until findings, formal approval, merge
and production verification have been handled. The independent reviewer is active;
final-head verification and recorded decisions belong in PR #3.

## B07 submission and recovery measurements: 2026-11-12 (Pacific/Auckland)

Work started from clean Notification main `adb9e59dd4428e1c8bf496fc40a42699bd003ba0`
in the isolated `codex/notification-submission-observability` worktree. The original
Invoicing report remains unchanged at `docs/invoicing-plan.md`, SHA-256
`1321b7712c4c9e10b477d03b230e12fce5485fbde3375d94c374fc96290313eb`.
This implements the Notification portion of Q28 #13's initial B07 measurements.
It does not finish B07 or enable the B02 durable path. Invoicing's queue measurements
are tracked separately in [PR #18](https://github.com/niobium-nz/Niobium.Invoicing/pull/18).

The [instrument guide](submission-observability.md) defines provider-adapter
invocation count/duration and domain-verified durable decisions/outcomes. The
domain returns an immutable status after its existing validation/persistence;
the flow does not reconstruct transition rules. Storage and public wire contracts,
retry policy, activation flag and packages remain unchanged. The host restores
configured service identity after the installed Functions detector so the existing
Azure Monitor exporter can identify the current deployment.

The candidate deployed harness requires the two active legacy provider instruments
from the current test version/window. It uses the existing approved send, verifies
the exporter destination and performs only bounded read polling. It does not add a
send or create resources. Query failures fail the check without retrying provider
submission, and owned fixture cleanup remains mandatory. Durable-path cloud
verification and an exact uncertain-send inventory remain separate dependencies.

| Local verification | Result |
| --- | --- |
| Release restore/build of `Niobium.Notification.slnx` | Passed, zero errors; the same six NU1603 warnings (three distinct published-package constraints) as clean main |
| Application tests with TRX and Cobertura | 128 passed, zero failed/skipped; includes 22 focused telemetry scenarios |
| Authored coverage gate | 595/605 lines, 98.35%; Contracts 100%, Core 99.12%, Server 97.12% |
| Real pinned Table/Blob adapters against loopback Azurite | Four passed, zero failed/skipped; owned emulators stopped |
| Python deployment/tooling tests | 46 passed; classic/regional hostnames, metadata mismatch, bounded query retries, permission failures, output sanitization and cleanup without resending covered |
| Node delivery-policy tests | 20 passed |

Commands are the README's local verification sequence, plus the explicit storage
suite with `NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT=http://127.0.0.1:18492/devstoreaccount1`
and `NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT=http://127.0.0.1:18490/devstoreaccount1`.
PowerShell 7 must be on PATH for the tooling tests; the initial local attempt without
it failed before the existing hook tests ran, then all 42 passed with the installed
runtime. The framework remains on published Platform integrations 4.2.70 and
Abstractions/File/Messaging adapters 4.2.66 with the provenance recorded below.

The first feature run [43025781050](https://github.com/niobium-nz/Niobium.Notification/actions/runs/43025781050)
passed build/local tests and deployed the correct commit, then failed telemetry
configuration before any fixture write or provider send. The new hostname matcher
had assumed a regional/hash segment; the existing app uses Azure's classic
`app.azurewebsites.net` hostname. The matcher now accepts both supported forms
while retaining an exact ARM hostname check. A regression test covers both forms
and rejects a mismatch. This failed run does not count as deployed verification.

Run [43026798576](https://github.com/niobium-nz/Niobium.Notification/actions/runs/43026798576)
then passed metadata/destination checks, the approved provider submission and owned
fixture cleanup, but its first metrics query failed. That generic diagnostic did
not establish the cause. Query errors now expose only allowlisted error classes;
authorization and invalid queries fail immediately, while first-ingestion table
absence and transient read failures can retry within the existing six-minute bound.
The send is never retried and successful export remains mandatory. The next run
must establish the actual query/export result; this run is not a passing gate.

At this local checkpoint, deployed telemetry and native review evidence are still
pending. Push normally, verify both candidate and trusted-main suites, then create
the ready PR, request `5he11`, follow native review and fix findings. The owner has
confirmed automatic review is enabled for all PRs/every push and authorized ordinary
PR/test-deployment work; no renewed approval for those actions is needed. A formal
current-head approval and all protections remain required before merging. Later
execution evidence belongs in the PR/workflow artifacts so it does not invalidate a
tested head merely to record its successful run.

## PR #2 test deployment and readiness requested: 2026-11-07

The owner asked for the failed test-deployment and PR-readiness checks to reach
success. This authorizes ordinary feature validation in the existing test
environment and supersedes the earlier draft-only deployment restriction for that
purpose. Durable submission remains disabled; queue-path activation still needs
the cutover and verification described in [the protocol](durable-submission.md).

The latest review head `c82caac96da9a0374a56feb9e4bb0056361e7930` has formal owner
approval and completed automatic code/security reviews. Its
[readiness run](https://github.com/niobium-nz/Niobium.Notification/actions/runs/42361430055)
failed because `[skip ci]` prevented a feature deployment run. Repeating readiness
alone cannot supply the missing authenticated deployment evidence. This follow-up
uses a normal feature push to run build, tests, test deployment, candidate
integration and the trusted main harness. The trusted controller must publish
`Test deployment verified` from that actual successful run. A new commit also
requires the ordinary current-head review check; no earlier result is substituted.

Execution results belong in the PR and workflow artifacts. Do not interpret this
request as authorization to enable the durable queue path or to bypass any merge
gate. The earlier no-deployment checkpoints below are historical for feature
validation.

## PR #2 package alignment and retry-policy review: 2026-11-07

The owner requested upgrading existing Niobium references instead of adding
redundant Server references in
[the package review](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4501144615).
The extra Server references to Platform, Abstractions and Helper are removed.
Existing Platform integration references in Server/Core now use 4.2.70, and
existing Abstractions/File/Messaging references in Contracts/Core use 4.2.66.
These are each package's latest stable versions checked on NuGet.org at this date.
Core's Email package brings Platform 4.2.70 transitively. Server's restored graph
resolves Abstractions/Helper and the Table, Blob and ServiceBus adapters at 4.2.66.

Package metadata ties the 4.2.70 libraries to framework release commit
`f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0`, and the 4.2.66 libraries to
`5c527723a607c1d03c002d4aa70531c9a55a767e`. The Table/Blob source is unchanged from
the previously tested adapters. The ServiceBus update uses
`AzureWebJobsServiceBus:clientId` instead of the storage identity key; the existing
`infra/main.bicep` already supplies the corresponding ServiceBus setting.
Live cloud-identity behavior remains a deployed verification item.

The [handler-removal question](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4501146414)
was checked against the shared extension and its resilience-preservation test.
`ConfigureAutoRedirect(false)` preserves resilience handlers, so the loop remains
necessary for durable retry ownership. The alternative
`RemoveAllResilienceHandlers()` is experimental in Microsoft.Extensions.Http.Resilience
10.9.0 and produces EXTEXP0001. The current stable hook is retained with a clarifying
comment; no diagnostic is suppressed. The package convention and this distinction
are documented in [Niobium PR #3](https://github.com/niobium-nz/Niobium/pull/3), the
[HTTP guide](http-client-configuration.md) and AGENTS.md.

| Verification | Result |
| --- | --- |
| `dotnet restore Niobium.Notification.slnx --source https://api.nuget.org/v3/index.json --nologo` | Passed; three distinct NU1603 warnings described below |
| `dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed; zero errors, six warnings because the three dependency warnings repeat in Server/tests |
| Application tests with `--no-build --no-restore --report-trx --coverage --coverage-output-format cobertura` | 106 passed, zero failed/skipped; host composition verifies no resilience handler and disabled redirects |
| `python scripts/check-coverage.py /workspace/scratch/notification-review-package-tests/coverage.cobertura.xml` | 98.18% overall; Contracts 100%, Core 98.99%, Server 96.93% |
| Storage test project restore/build and its four real loopback Table/Blob scenarios | Four passed, zero failed/skipped; zero build errors, the same six NU1603 warnings |

The three published 4.2.70 integration packages request Database.StorageTable,
File.Blob and Messaging.ServiceBus at minimum version 4.2.0, which was not published.
NuGet selects the available 4.2.66 adapters and reports NU1603. The framework's
version-generation script explains that metadata; a packaging correction needs a
separate framework change and release. The warnings remain visible, and no extra
lower-level reference is introduced to hide them.

Storage validation used the existing loopback fixture settings at ports 18492 and
18490. Owned fixtures were cleaned up and both owned emulators stopped. No provider
request or cloud resource was used. This update uses `[skip ci]` under the existing
Notification no-merge/no-deployment/no-publication boundary. Local compatibility
results do not satisfy deployed readiness. This section supersedes the earlier
explicit-Server-pin approach below.

## Historical initial published redirect adoption: 2026-11-07

The package-reference approach in this checkpoint is superseded by the subsequent
package-alignment review follow-up. Its results apply to that earlier graph only.

The owner approved merging Niobium PR #2, waiting for publication and updating
Notification's package reference. Framework merge
`f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0` completed
[Publish run 42358398851](https://github.com/niobium-nz/Niobium/actions/runs/42358398851),
including 18 transport tests and publication of 19 affected packages. A clean
NuGet.org restore then verified Niobium.Platform 4.2.70 was available; its package
repository metadata matches the merge commit.

At this checkpoint, the Server pinned Platform 4.2.70 and its published Abstractions/Helper 4.2.66
dependencies. Explicit pins avoid NU1603 because Platform's 4.2.0 minimum versions
do not exist on the feed. The dependency module uses `ConfigureAutoRedirect(false)`
instead of creating an HttpClientHandler, and the composition test checks the
factory's SocketsHttpHandler with redirects disabled and no HTTP resilience retries.
The runtime graph contains no local candidate package. The Contracts project and
existing Table/Blob adapter pins are unchanged.

| Verification | Result |
| --- | --- |
| `dotnet restore Niobium.Notification.slnx --source https://api.nuget.org/v3/index.json --nologo` | Passed without warnings |
| `dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed, zero warnings/errors |
| Application tests with `--no-build --no-restore --report-trx --coverage --coverage-output-format cobertura` | 106 passed, zero failed/skipped |
| `python scripts/check-coverage.py /workspace/scratch/notification-published-http-tests/coverage.cobertura.xml` | 98.17% overall; Contracts 100%, Core 98.99%, Server 96.92% |
| Storage test project restore/build and its four loopback Table/Blob scenarios | Four passed, zero failed/skipped; zero build warnings/errors |

The storage tests used the existing loopback fixture settings at ports 18492 and
18490. Their owned tables/containers were cleaned up and both owned Azurite processes
were stopped. [HTTP configuration](http-client-configuration.md) records the package
source revisions, release evidence and adopted implementation. This supersedes the
pending-release checkpoint below. Framework publication was authorized and completed;
Notification remains open for review under its existing no-merge/no-deployment boundary.
This feature update uses `[skip ci]` and does not claim deployed readiness.

## Historical shared redirect support checkpoint: 2026-11-07

The owner's [new transport comment](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500908254)
requests framework-owned redirect configuration. [Niobium PR #2](https://github.com/niobium-nz/Niobium/pull/2)
adds `ConfigureAutoRedirect(bool)`, preserving the existing transport rather than
replacing it. Framework head `63d61bc8a7302ba213a352705a8891ddeecf4c13` passed its
[PR build and tests](https://github.com/niobium-nz/Niobium/actions/runs/42357187515),
including all 18 transport tests and 16 workflow script assertions. Publication was
skipped for that PR event, as intended.

An isolated copy of Notification head `49faf17589b7d2950197dc7cf9bf87dbdfa60a7d`
was updated to use the extension and tested against local framework candidate
packages. Release build passed with zero warnings/errors; all 106 application tests
passed and coverage remained 98.17%. The composition test verified disabled redirects
and absent resilience retries on the factory's `SocketsHttpHandler`. The
[adoption guide](http-client-configuration.md) records the candidate versions,
source changes and exact remaining release dependency.

The candidate was restored from a scratch local feed plus NuGet.org, then validated
with the ordinary application commands using
`-p:BUILD_BUILDNUMBER=0.0.0-local.redirect.2` for its Release build so generated local
packages had a prerelease version. Tests used `--no-build --no-restore --report-trx
--coverage --coverage-output-format cobertura`; the standard coverage gate passed.
No candidate package or source copy is used by this branch or a deployed service.

At that checkpoint, Notification's source and pinned packages remained unchanged
while the framework PR awaited approval and release. The comment had a reply linking
the tested candidate, and the documentation update used `[skip ci]`. The published
adoption above supersedes that dependency and retains Notification's review boundary.

## PR #2 review-policy documentation follow-up: 2026-11-07

The native review of `9132976ad6524edbf356341a99a870309d037918`
[found the README still instructing manual-only review](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500222647).
The README now matches the owner's automatic-review authorization in AGENTS.md.
The delivery guide records observed automatic code/security execution on PR #2,
keeps formal owner approval separate and requires a fresh receipt for each head.
Historical manual-only and unverified-activation entries below are explicitly marked
as superseded; the already-installed main listener/harness is distinguished from
its original installation constraints.

This follow-up changes documentation only. Relative file links and whitespace
checks passed. Application code is unchanged from the 106-test, 98.17% coverage
result below, so those tests were not repeated. Notification remains open for review;
no merge, deployment, publication or native-setting administration was performed.

## PR #2 malformed-conflict review follow-up: 2026-11-07

The native review of `3933e1c7693cd7d563e370004482e3c69e1baf43`
[identified malformed HTTP 409 responses being retried](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500181826).
The provider now retains the received status across response-body failures. A
known conflict is held unless its body positively identifies the provider's
concurrent-request code. Empty, truncated, non-object and non-JSON conflict bodies,
body-read timeouts, HTTP errors and I/O errors all hold. Failures before a response
and malformed success bodies remain uncertain/retryable. Caller cancellation
continues to propagate for queue recovery.

Regression cases exercise these outcomes through HttpClient, including faulting
response streams. This is an HTTP classification correction; storage and domain
transitions are unchanged. The earlier Table/Blob scenarios were not repeated.
After sourcing `/workspace/.niobium-tools/env.sh`:

| Exact command | Result |
| --- | --- |
| `dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed, zero warnings/errors |
| `dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-conflict-tests-final` | 106 passed, zero failed/skipped |
| `python scripts/check-coverage.py /workspace/scratch/notification-conflict-tests-final/coverage.cobertura.xml` | 537/547 lines, 98.17%; Contracts 100%, Core 98.99%, Server 96.92%; all thresholds passed |
| `git diff --check` | Passed |

Notification remains open for owner review and undeployed, with the durable option
disabled. The infrastructure validation limits and table-input checks below still
apply. Earlier dated sections record their own source revisions and evidence.

## PR #2 table-input review follow-up: 2026-11-07

The owner's [new review comment](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500079992)
asks for resource names to follow the existing queue parameter pattern. Starting
from `eea196e71adfb24ad8d4cadc435f81e9506f7bfb`, `main.bicep` now accepts generic
`storageTableNames` and creates only the names supplied by that input.
`main.bicepparam` reads `STORAGE_TABLE_NAMES` with the application default
`Subscription,Template,NotificationSubmission`, including the historical service
entities. The new name stays singular PascalCase and matches the entity type used
by the framework repository. No runtime mapping or table rename is introduced.

[Resource-name configuration](delivery-and-operations.md#resource-name-configuration)
and AGENTS.md record the ownership boundary, defaults, naming and explicit workflow
projection needed for GitHub variables. Current workflows use parameter defaults;
no workflow or host-module change is needed for these tables. The shared framework
guide link is pinned to its immutable documentation commit, now merged in Niobium
PR #1.

Validation used Bicep CLI 0.49.2 and PowerShell 7.6.9. The latter was installed in
this workspace from the official release with its published SHA-256 verified after
the initial hook check found `pwsh` missing.

| Check | Result |
| --- | --- |
| Bicep `format` on `infra/main.bicep` and `infra/main.bicepparam`, writing only temporary output | Syntax accepted for both files |
| Bicep `build-params` on a temporary copy using `using none`, preserving every parameter binding | Passed for absent, overridden and explicitly empty `STORAGE_TABLE_NAMES`; this isolates binding evaluation from external modules and does not validate resource types |
| `python -m unittest discover -s scripts -p test_app_settings.py` with PowerShell on PATH | Four passed |
| Run `SetAppSettings.ps1` and `ClearAppSettings.ps1` on a temporary copy of the actual parameter file with synthetic settings | Table binding preserved and original file restored exactly |
| `/workspace/scratch/bicep-v0.49.2 build infra/main.bicep --outfile /workspace/scratch/notification-table-main.json` | Blocked by proxy 403 restoring existing MCR AVM modules (BCP192); full resource type validation remains unverified |
| Relative documentation file targets and `git -c core.whitespace=cr-at-eol diff --check` | Passed |

No application code changed in this follow-up; the previous 96 application tests,
four Table/Blob scenarios and 98.15% coverage remain evidence for that unchanged
code and were not repeated. The owner requested merging the other approved PRs;
Notification remains open for review. This follow-up retains Notification's
no-deployment/no-merge/no-publication boundary and uses `[skip ci]`. The option
remains disabled and no storage data was changed.

## PR #2 domain review follow-up: 2026-11-07

The owner requested an investigation of DDD, rich domains and Niobium data access
across Notification, Store, Invoicing and the framework. Work continued from clean
Notification head `46e93c85f79322eb5b765295c0e323eb60f8fdb0` on the existing PR branch.
The five inline requests in the owner's changes-requested review are addressed:

| Review request | Result |
| --- | --- |
| Enum states | Explicit `SubmissionState`/`SubmissionAction` decisions, scalar integer storage codes, unknown-code rejection |
| Actions outside conditions | Flow operations execute before branching; queue parsing results are named; project rule added to AGENTS.md |
| Enum outcomes | `SubmissionOutcome` used across domain, provider and tests |
| Reuse options | `NotificationOptions:EnableDurableSubmission`, false by default; separate delivery options removed |
| Rich domain and framework data access | `NotificationSubmissionDomain` owns registration, claims, retry eligibility and outcomes through `GenericDomain`/`IRepository`; the custom Table SDK store is removed |

The installed repository already supports conditional create/update. Real-adapter
verification reproduced its missing write-response ETag; the domain now verifies
its unique write identity and complete snapshot on read-back before using the
returned ETag. No framework runtime change or package upgrade is required for this
refactor. The [domain/data-access guide](domain-data-access.md) records the inspected
source versions, examples and framework follow-ups. Equivalent guides and agent
entry points were added at the user's request in
[Niobium PR #1](https://github.com/niobium-nz/Niobium/pull/1),
[Store PR #2](https://github.com/niobium-nz/Niobium.Store/pull/2) and
[Invoicing PR #17](https://github.com/niobium-nz/Niobium.Invoicing/pull/17).

Internal schema version 2 rejects the earlier unpublished string-state version.
The provider idempotency key stays stable. The old draft option does not enable
the new path. Generic repositories do not create tables, so `infra/main.bicep`
declares `NotificationSubmission` in the existing data account. Deploy that table
and satisfy the [cutover prerequisites](durable-submission.md#activation-and-remaining-dependencies)
before enabling the feature. No deployment or data migration was performed.

Validation used .NET SDK 10.0.403 after sourcing `/workspace/.niobium-tools/env.sh`:

| Exact command | Result |
| --- | --- |
| `dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo --disable-build-servers -m:1` | Passed, zero warnings/errors |
| `dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory /workspace/scratch/notification-domain-tests-final` | 96 passed, zero failed/skipped |
| `python scripts/check-coverage.py /workspace/scratch/notification-domain-tests-final/coverage.cobertura.xml` | Passed: 530/540 lines, 98.15%; Contracts 100%, Core 98.99%, Server 96.82% |
| `NIOBIUM_NOTIFICATION_TEST_TABLE_ENDPOINT=http://127.0.0.1:18492/devstoreaccount1 NIOBIUM_NOTIFICATION_TEST_BLOB_ENDPOINT=http://127.0.0.1:18490/devstoreaccount1 dotnet test --project src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj --configuration Release --no-restore --report-trx --results-directory /workspace/scratch/notification-domain-storage-final` | Four passed, zero failed/skipped; real pinned Table repository and Blob adapter |
| `/workspace/scratch/bicep-v0.49.2 format infra/main.bicep --outfile /workspace/scratch/notification-domain-main.formatted.bicep` | Syntax accepted; repository formatting unchanged |
| `/workspace/scratch/bicep-v0.49.2 build infra/main.bicep --outfile /workspace/scratch/notification-domain-main.json` | Blocked: proxy 403 while restoring existing AVM modules from MCR, BCP192 and dependent errors |
| `git -c core.whitespace=cr-at-eol diff --check` | Passed; relative documentation targets also checked |

The baseline Bicep files also cannot compile without those modules (BCP190 with
`--no-restore`). Full infrastructure type validation remains unverified, not a
successful check. The local suites cover ownership read-back, competing/stale
claims, uncertain writes, frozen payloads, missing data, corrupt state and retry
boundaries. Queue/provider replies are simulated. Fixtures removed their owned
local tables/containers and both in-memory Azurite processes were stopped.

The no-deployment/no-merge/no-publication boundary still applies; commits use
`[skip ci]`. These results do not satisfy the live deployment readiness gate or
formal current-head review. The feature remains disabled. The dated sections below
record earlier stages and their then-current implementation and test evidence.

## Historical automatic-review authorization checkpoint: 2026-11-06

The owner's instruction, "approve auto review", supersedes the earlier manual-only
Notification choice recorded below. `AGENTS.md` and the delivery guide now authorize
native code review for All PRs On every push, with security review for All PRs
Whenever code review runs, matching Invoicing's recorded configuration. Formal
reviewer approval remains separate; this is not an approval of the implementation.

At that checkpoint, activation was unverified: the GitHub connector did not expose
these settings and no browser-control tool was available. No native review comment was
present on Notification draft PR #2 or Invoicing draft PR #17 when checked. The
delivery guide links the exact settings page and distinguishes authorization,
saved configuration and execution evidence. The no-deployment/no-merge boundary
persists. This documentation-only update passed `git diff --check`; application
tests were not rerun, and that checkpoint claimed no automatic-review execution.
The 2026-11-07 review-policy follow-up above records later observed execution.

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

## Historical fixture bindings and manual-review choice (2026-11-06)

The owner configured `TEST_NOTIFICATION_SENDER` as a GitHub `test` environment variable and `TEST_NOTIFICATION_RECIPIENT` as an environment secret. All three workflow projections now read the sender from `vars` and the recipient from `secrets`. No address value is committed, no additional stored setting is requested, and the fixture identity remains independent of Invoicing. Application code, packages, public contracts and Bicep are unchanged.

At this earlier checkpoint, the connector verified `5he11` had Write permission, and the owner had chosen manual-only review. The later automatic-review authorization superseded that choice; the README, AGENTS.md and delivery guide now require native review alongside formal approval. Main protection and the initial trusted-harness installation were pending at this historical checkpoint, before PR #1 installed the safeguards. Current feature work still requires verification for its actual head.

Local verification passed: `python -m unittest discover -s scripts -p 'test_*.py'` (34), `node --test scripts/delivery-policy.test.cjs` (20), YAML parsing with all three sender-variable/recipient-secret projections checked, and `git diff --check`. The next feature run must establish build/coverage and actual deployment results on the pushed head.
