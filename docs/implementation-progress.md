# Notification reliability implementation ledger

This repository is the downstream dependency for the supplied Niobium.Invoicing research plan. Preserve the original plan IDs and acceptance criteria. The owner's accepted recommendations are recorded in [Invoicing Q28](https://github.com/niobium-nz/Niobium.Invoicing/blob/50df240f8212af33a152a13bcce11c848d6b51a8/docs/decisions-and-risks.md#q28-business-decision-follow-up); that decision-only change completed production verification as PR #14. This milestone does not claim those business behaviors are implemented.

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

## Invoice terminology correction

The owner requires Invoicing terminology for invoice-related configuration, documentation and tests even when another service is under test. The source biller setting is now `TEST_BILLER_ID` throughout the workflow, preflight, deployed harness and test fixtures. It remains the existing test biller GUID whose `Invoice` template supplies the configured sender. The Notification API's established `Tenant` field is mapped explicitly at the HTTP boundary; its wire/storage contract is unchanged. This rule is recorded in the repository and workspace agent memory.

## Existing environment settings reused (2026-11-06)

The owner reconfirmed the existing Invoicing test variable `TEST_INVOICE_BILLER` and secret `TEST_NOTIFICATION_RECIPIENT`. All three Notification workflow projections now read that stored biller name into the invoice-oriented runner input `TEST_BILLER_ID`; the recipient secret name is unchanged. The delivery guide and agent memory distinguish stored names, runner names and repository scope. The earlier naming correction did not justify requesting a new biller or mailbox. Notification still needs its own binding to the same approved values because GitHub environments are repository scoped.

This correction changes no application, wire/storage, Bicep or package contract. Local checks passed: YAML parsing and all three stored-to-runner projections, 32 Python preflight/tooling tests, 20 Node policy tests and `git diff --check`. Exact-head CI and real deployed results must still be recorded before PR creation. The earlier missing-configuration failure is not evidence that the owner's Invoicing settings are absent.
