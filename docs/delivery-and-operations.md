# Delivery and operations

The owner approved applying Niobium.Invoicing's delivery safeguards to this dependency in Q28 #5. Workflows and external repository configuration must both enforce the policy.

## Sequence

1. Implement and validate a feature branch based on current `main`. Slash-containing names and documentation changes also trigger verification.
2. `Azure Deployment` runs Release build, application tests, coverage, Node policy tests and Python tooling tests. Coverage must be at least 90% combined and 80% per authored application module. Preserve package versions unless their upgrade is authorized.
3. Deploy the exact feature SHA to the existing `test` environment and run the candidate harness. On a fresh runner, resolve immutable current main, check out only that commit and run its deployed harness against the same feature SHA. The workflow-wide test concurrency group protects both suites from another feature deployment; `queue: max` and `cancel-in-progress: false` preserve pending work.
4. Verify the terminal result and artifacts for this head. A prior green commit, skipped suite or running job is not success. The trusted main listener authenticates pipeline Git blobs and the exact main-harness step before publishing `Test deployment verified`; feature code cannot publish that status or create PRs.
5. After success the branch author creates/reuses the PR and requests the designated reviewer. Codex uses `niobium-nz` for implementation and requests `5he11`. On 2026-11-06 (Pacific/Auckland), the owner approved native automatic review, superseding the earlier manual-only choice. Verify its external configuration and current-head execution separately, resolve feedback and obtain a valid formal approval of the current head. The implementation agent must not submit its own reviewer decision or delegate approval as a substitute. This choice does not change Invoicing's standing `pr_approver` handoff.
6. With all rules satisfied and main up to date, verify the proposed squash tree equals the tested tree, then merge through the GitHub connector with the expected head SHA. No direct pushes to main or bypasses.
7. The merged-PR event immediately deploys the actual merge SHA to production and runs read-only verification. There is no independent production gate. Stay active until production and package workflows finish; investigate any failure immediately and fix through the same process.

| Trigger | Effect |
| --- | --- |
| Feature push | Build, coverage, test deployment, candidate and trusted integration |
| PR opened or updated | Main-owned readiness verification, native review once configured, designated reviewer and author feedback handling |
| PR closed with `merged: true` into main | Production deployment, read-only check, conditional Contracts publication |
| Closed unmerged PR | No production deployment or publication |
| Direct main push | Prohibited by required protection; not a deployment trigger |

Package publication compares the actual merge commit's Contracts tree with its first parent. Missing history fails. Unchanged Contracts skips restore/build/NuGet login/publication. Feature packages are no longer published. Production and package concurrency queues retain pending runs.

## Automatic review authorization and activation

The owner approved automatic review on 2026-11-06 (Pacific/Auckland). The intended
repository settings match those already recorded for Invoicing:

| Setting | Code review | Security review |
| --- | --- | --- |
| Automatic review | Enabled | Enabled |
| Review | All PRs | All PRs |
| Trigger | On every push | Whenever code review runs |

These settings belong to [Codex Code review settings](https://chatgpt.com/settings/code-review),
not a repository workflow. The connector cannot inspect/change the settings, and
saving repository documentation does not enable the native service.

The owner has since confirmed that automatic review is enabled on all PRs and
every push. Continue the authorized PR/test deployment process without requesting
that configuration again. A completed receipt for each actual head is still
required evidence, and formal current-head approval remains a separate merge rule.

On 2026-11-07, PR #2's native summary recorded completed code and security reviews
for `eea196e` after the draft was marked ready, and for `3933e1c` and `9132976` after
new commits. The [review of 9132976](https://github.com/niobium-nz/Niobium.Notification/pull/2#pullrequestreview-5810246112)
identified the stale README policy corrected in this follow-up. These receipts
establish execution for those heads; they do not certify every external setting
or approve the implementation.

Verify an automatic receipt for each new head and read all findings. A reviewer
assignment or a manual `@codex review` request does not prove automatic execution;
a bot comment or reaction does not supply formal owner approval. Keep formal review
separate from the implementation conversation. On 2026-11-07, the owner requested
successful test deployment and PR readiness for B02. That request supersedes the
earlier draft-only restriction for feature validation; use the ordinary test
pipeline and verify its terminal result before reporting the PR ready.

## Required repository administration and installation

Source changes cannot configure repository permissions/rules. The inspected baseline `989b7c4b36154c5da2cdd3391ec297e1f453ea14` had unprotected main, no rulesets and read-only access for `5he11`. On 2026-11-06, the owner granted `5he11` Write access and the connector verified it; main still had no protection. Recheck current settings before acting.

Normal protection requires PR-only main updates, one approval, dismissal of stale approvals, resolved threads, up-to-date main, required successful `test` deployment, squash-only merges and the GitHub Actions `Test deployment verified` status, with no bypass actors. `5he11` needs permission for an approval to satisfy protection. Disable Actions permission to create/approve PRs. Native code/security review is authorized and has executed on the heads recorded above. Verify external settings and the receipt for the actual head separately; automatic review does not replace formal approval.

The listener and reviewed harness are now present on main at
`4a93385be1b09a9d97d0c2384e1085f254c7d84c` from PR #1. The earlier installation gap
is historical; ordinary feature work uses that installed protection. During first
installation, a missing main harness caused the trusted step to skip, which could
not certify the candidate. Future changes to protected pipeline definitions still
need a concrete reviewed activation decision when they cannot be authenticated
against current main. A skipped trusted step is never a successful verification.
Do not infer an exception from an earlier installation or fabricate status.

The connector cannot administer rulesets, Actions settings, environment configuration
or collaborator roles. Prepare concrete changes and candidate evidence before
requesting any necessary installation decision; verify current rules separately.

## Real test integration

Required GitHub `test` environment configuration in `niobium-nz/Niobium.Notification`:

| Name | Kind | Consumer |
| --- | --- | --- |
| `TEST_NOTIFICATION_RECIPIENT` | Secret | One owner-authorized mailbox; runner-only fixture destination |
| `TEST_NOTIFICATION_SENDER` | Variable | Explicitly approved sender address allowed by the existing test provider; runner-only `From` for the owned generic template |
| Existing Azure OIDC IDs, resource group and deployment settings | Existing secrets/variables | Same environment resources and runtime settings as baseline |

Notification owns these generic service-test fixtures independently of its consumers. It needs no Invoicing variable, biller ID, invoice record, token or existing `Invoice` template. Consumer-specific end-to-end tests belong in the consumer repository, where they exercise Notification through its established public contract. The earlier request to copy `TEST_INVOICE_BILLER` into this repository is withdrawn.

All three workflow projections (preflight, candidate and trusted suites) map `vars.TEST_NOTIFICATION_SENDER` and `secrets.TEST_NOTIFICATION_RECIPIENT` to the corresponding runner inputs. The owner confirmed both settings on 2026-11-06; do not ask for a duplicate sender secret. They are runner-only inputs, not Function App settings. GitHub environment settings are repository scoped; any use of an already approved mailbox here is an explicit generic Notification fixture configuration, not inheritance of an Invoicing dependency. Do not choose a sender or recipient from an arbitrary existing template, invent a provider-permitted sender, or print addresses from test code. The sender is non-secret configuration and can appear in GitHub step metadata; the recipient remains masked, and reports contain only fixed diagnostics. The connector cannot administer these settings. A missing approved generic fixture must fail before resource access.

`scripts/test-resources.py` uses the workflow's Azure identity to read the existing test Function App's ARM hostname and data-account key. It validates application/environment, masks the key and exports runner-only test settings. It creates no key, app setting, table, queue or Azure resource and never runs in production.

The candidate and trusted harnesses each perform six checks:

- Exact deployed commit and readiness through `GET /health`.
- A request for an owned template without its blob must fail, proving the host reads the fixture rather than silently skipping it.
- A unique marked template and blob in existing `Template`/`templates` resources route a generic synthetic deployment-verification notification only to `TEST_NOTIFICATION_RECIPIENT`. `POST /ContactUs` must await the real Notification flow/provider and return 201. Test captcha remains disabled by the existing test configuration.
- Sending must leave the template ETag unchanged.
- Invalid input must be rejected before another send.
- Cleanup must verify ownership and use conditional ETags before deleting only this run's records/blob.

The harness never reads a pre-existing consumer template. Its sender and recipient come only from the explicit service-test settings; the tenant UUID is unique to this run. The fixture uses an isolated template partition and the existing HTTP entry point to observe the real provider result synchronously. No separate sink, provider stub or Azure stack is introduced. A configuration-only preflight checks the required sender and recipient before cloud login/provisioning. Missing configuration is failure, not skip. A POST with an unknown outcome is never retried by the harness. Cleanup reconciles attempted writes, including lost responses; if ownership or outcome cannot be established it fails without deleting unowned data. Resolve any reported test-fixture cleanup failure before claiming completion.

The six checks demonstrate provider submission on this path, not recipient mail-server delivery, queue deduplication or B02 recovery. Future B02/B06 cases must verify those contracts separately. The old `src/Niobium.Notification.IntegrationTests/test/` scripts are not invoked because their fallback recipients are not scoped to this authorization.

## Production verification

Only `scripts/read-only-deployment.py` runs after production deployment. It permits same-origin `GET /health`, rejects other methods/routes and redirects, limits the response size and checks the actual merge SHA. No template, subscription or queue credentials enter this client. `GET /Unsubscribe` is side-effecting and forbidden. It reports sanitized status and commit, never response bodies or secrets.

`/health` returns compiled metadata, not complete dependency health. Provider receipts and storage recovery remain unverified by this production check. Check the report commit and success plus the package workflow's actual merge checkout before reporting delivery complete.

## Provisioning and credentials

Keep Bicep environment agnostic. `infra/main.bicep` owns application resources and passes their environment-specific settings to the generic Function App module. `infra/function-app.bicep` owns host resources and combines caller settings with internal host settings. Resource-name defaults belong in the parameter file rather than resource declarations.

### Resource-name configuration

`main.bicep` accepts comma-separated `serviceBusQueueNames` and `storageTableNames`.
It converts each nonempty input to the resource list; it contains no service queue
or table names. `main.bicepparam` owns the application defaults and reads deployment
environment variables, using the same pattern for queues and tables:

| Parameter | Runner variable | Default |
| --- | --- | --- |
| `serviceBusQueueNames` | `SERVICE_BUS_QUEUE_NAMES` | `subscribecommand,notifycommand,subscribedevent` |
| `storageTableNames` | `STORAGE_TABLE_NAMES` | `Subscription,Template,NotificationSubmission` |

The table list includes historical service-owned tables as well as the new inbox.
Table names use singular PascalCase entity names: the Niobium repository defaults
to `typeof(TEntity).Name`. `NotificationSubmission` therefore matches its persisted
entity just as `Subscription` and `Template` do. A provisioning override does not
rename a table or change the runtime repository mapping. Keep every required table
in an override, comma-separated without surrounding spaces. An explicit empty
string produces an empty provisioning list; an absent variable uses the default.

Current workflows use the parameter defaults. A local deployment or a workflow
may supply `STORAGE_TABLE_NAMES`. GitHub environment variables are not automatically
runner environment variables: when adding a GitHub override, explicitly project
it in the deployment job's `env`, for example:

```yaml
env:
  STORAGE_TABLE_NAMES: ${{ vars.STORAGE_TABLE_NAMES || 'Subscription,Template,NotificationSubmission' }}
```

The fallback matters because an undefined GitHub variable expands to an empty
string, and `readEnvironmentVariable` uses its default only when the runner variable
is absent. Keep defaults in step with the parameter file when adding a projection.
These are infrastructure inputs, not `APPSETTING_` host settings. The application
settings hook must preserve the resource-name parameter bindings. No generic host
module or environment-specific Bicep branch is needed for a new table or queue.

The workflow explicitly projects GitHub settings into runner `env`. `SetAppSettings.ps1` turns ordinary `APPSETTING_` entries into environment references in generated `appSettings`; `APPSETTING_KV_` entries carry only the setting name and an empty placeholder. The reusable module converts them to Key Vault references. `SetKeyVaultSecrets.ps1` reads actual values from runner environment, writes the existing vault, then refreshes Function App references after all writes succeed. `ClearAppSettings.ps1` restores the original parameter file. The hook behavior matches the already reviewed Invoicing correction and does not introduce an environment-specific module path.

The runner's `TEST_STORAGE_KEY` is used only for owned integration fixtures. Runtime Table/Blob access retains existing identity configuration; Resend uses existing runtime provider secrets. Do not publish credential values, mailbox values or provider responses in source, logs, reports or review comments.

## B02 validation and activation

The [durable queued submission path](durable-submission.md) is disabled by default.
The owner authorized test deployment and readiness verification on 2026-11-07.
Feature validation uses the existing test environment and approved Notification
fixtures, with durable delivery still disabled. Follow the delivery sequence and
current-head approval requirements before merging. Enabling durable delivery
requires its concrete cutover and queue/provider verification prerequisites.
The existing deployed ContactUs harness does not certify the enabled queue path.

## Submission measurements

See [the instrument definitions and interpretation limits](submission-observability.md).
The candidate harness verifies the existing test Function App, its current workflow
service version and its configured Application Insights destination, then requires
the active legacy adapter's counter and duration to appear in Azure Monitor.
It observes the existing approved ContactUs send and polls only read operations
for up to six minutes. It never resends a notification to obtain telemetry.
An export or query failure fails the deployment check and still runs ownership-
checked fixture cleanup. The immutable main harness remains independently required.

This uses the current workflow identity and existing resources/settings. No new
secret, telemetry resource, durable-path activation or production fixture is added.
The Functions detector's other resource metadata is retained while explicit
`SERVICE_NAME` and `SERVICE_VERSION` values are reapplied after it. All OpenTelemetry
signals using that resource now use the configured service identity; update queries
that previously relied on the detector's fallback name/version.
