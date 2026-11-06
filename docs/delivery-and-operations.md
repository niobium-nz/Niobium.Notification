# Delivery and operations

The owner approved applying Niobium.Invoicing's delivery safeguards to this dependency in Q28 #5. Workflows and external repository configuration must both enforce the policy.

## Sequence

1. Implement and validate a feature branch based on current `main`. Slash-containing names and documentation changes also trigger verification.
2. `Azure Deployment` runs Release build, application tests, coverage, Node policy tests and Python tooling tests. Coverage must be at least 90% combined and 80% per authored application module. Preserve package versions.
3. Deploy the exact feature SHA to the existing `test` environment and run the candidate harness. On a fresh runner, resolve immutable current main, check out only that commit and run its deployed harness against the same feature SHA. The workflow-wide test concurrency group protects both suites from another feature deployment; `queue: max` and `cancel-in-progress: false` preserve pending work.
4. Verify the terminal result and artifacts for this head. A prior green commit, skipped suite or running job is not success. The trusted main listener authenticates pipeline Git blobs and the exact main-harness step before publishing `Test deployment verified`; feature code cannot publish that status or create PRs.
5. After success the branch author creates/reuses the PR and requests the designated reviewer. Codex uses `niobium-nz` for implementation, requests `5he11`, rereads `/workspace/AGENTS.md` and delegates independent approval to `pr_approver`. Resolve feedback and obtain one formal approval of the current head. Native review is separate from formal approval. Verify configured background review actually ran for the current head; do not create polling reviewers or spam manual requests.
6. With all rules satisfied and main up to date, verify the proposed squash tree equals the tested tree, then merge through the GitHub connector with the expected head SHA. No direct pushes to main or bypasses.
7. The merged-PR event immediately deploys the actual merge SHA to production and runs read-only verification. There is no independent production gate. Stay active until production and package workflows finish; investigate any failure immediately and fix through the same process.

| Trigger | Effect |
| --- | --- |
| Feature push | Build, coverage, test deployment, candidate and trusted integration |
| PR opened or updated | Main-owned readiness verification, native review and author feedback handling |
| PR closed with `merged: true` into main | Production deployment, read-only check, conditional Contracts publication |
| Closed unmerged PR | No production deployment or publication |
| Direct main push | Prohibited by required protection; not a deployment trigger |

Package publication compares the actual merge commit's Contracts tree with its first parent. Missing history fails. Unchanged Contracts skips restore/build/NuGet login/publication. Feature packages are no longer published. Production and package concurrency queues retain pending runs.

## Required repository administration and installation

Source changes cannot configure repository permissions/rules. The inspected baseline `989b7c4b36154c5da2cdd3391ec297e1f453ea14` had unprotected main, no rulesets and read-only access for `5he11`. Recheck current settings before acting.

Normal protection requires PR-only main updates, one approval, dismissal of stale approvals, resolved threads, up-to-date main, required successful `test` deployment, squash-only merges and the GitHub Actions `Test deployment verified` status, with no bypass actors. `5he11` needs permission for an approval to satisfy protection. Disable Actions permission to create/approve PRs. Native code/security review settings are external; Invoicing's configuration does not prove Notification's configuration.

This first installation has no listener or deployed harness on main. The candidate must still pass every applicable check and actual test deployment, but the trusted step explicitly skips and the custom status remains unavailable. An owner must explicitly decide the one-time installation policy. A possible reviewed installation is to temporarily require both native checks `Build and local tests` and `Deploy test and run integration`, preserving the test-deployment, approval, thread, strict-up-to-date, squash and no-bypass rules, then restore the custom status immediately after the approved merge. This is a proposal, not an authorized exception. Do not infer approval from prior Invoicing installations, unchanged configuration confirmations or elapsed time. Do not open a PR while the baseline's unsafe PR-triggered production workflow would still apply without first confirming the candidate workflow replaces that trigger.

The connector cannot administer rulesets, Actions settings, environment configuration or collaborator roles. Prepare concrete changes and actual candidate evidence before asking for an installation decision. A skipped trusted step must remain explicit in the handoff and PR.

## Real test integration

Required GitHub `test` environment configuration in `niobium-nz/Niobium.Notification`:

| Name | Kind | Consumer |
| --- | --- | --- |
| `TEST_NOTIFICATION_RECIPIENT` | Secret | One owner-authorized mailbox; runner-only fixture destination |
| `TEST_INVOICE_BILLER` | Variable | Existing approved test biller GUID, mapped by the workflow into runner input `TEST_BILLER_ID`; its existing `Invoice` template supplies only the verified `From` |
| Existing Azure OIDC IDs, resource group and deployment settings | Existing secrets/variables | Same environment resources and runtime settings as baseline |

The owner reconfirmed that Invoicing already has the valid biller in `TEST_INVOICE_BILLER` and the authorized mailbox in secret `TEST_NOTIFICATION_RECIPIENT`. Reuse those same approved values and stored names; no new fixture identity or separate `TEST_BILLER_ID` GitHub variable is required. The latter is a harness input name, not a second setting. GitHub environments are repository scoped: Notification does not inherit Invoicing's settings merely because both environments are called `test`. An empty input here requires checking this repository's binding and workflow projection, not asking the owner to choose another biller or recipient. The connector cannot inspect or administer these environment settings.

The three workflow projections (preflight, candidate and trusted suites) use `TEST_BILLER_ID: ${{ vars.TEST_INVOICE_BILLER }}` and `TEST_NOTIFICATION_RECIPIENT: ${{ secrets.TEST_NOTIFICATION_RECIPIENT }}`. These are runner-only inputs, not Function App settings. Keep Notification's existing `Tenant` wire/storage contract and map the biller at that boundary. Do not copy private values into source, logs or review comments.

`scripts/test-resources.py` uses the workflow's Azure identity to read the existing test Function App's ARM hostname and data-account key. It validates application/environment, masks the key and exports runner-only test settings. It creates no key, app setting, table, queue or Azure resource and never runs in production.

The candidate and trusted harnesses each perform six checks:

- Exact deployed commit and readiness through `GET /health`.
- A request for an owned template without its blob must fail, proving the host reads the fixture rather than silently skipping it.
- A unique marked template and blob in existing `Template`/`templates` resources route a synthetic invoice-verification notification only to `TEST_NOTIFICATION_RECIPIENT`. `POST /ContactUs` must await the real Notification flow/provider and return 201. Test captcha remains disabled by the existing test configuration.
- Sending must leave the template ETag unchanged.
- Invalid input must be rejected before another send.
- Cleanup must verify ownership and use conditional ETags before deleting only this run's records/blob.

The source `Invoice` template and its recipient are never modified or used as the destination. The fixture uses an isolated template partition and the existing HTTP entry point to observe the real provider result synchronously. No separate sink, provider stub or Azure stack is introduced. A configuration-only preflight checks the required recipient and biller ID before cloud login/provisioning. Missing configuration is failure, not skip. A POST with an unknown outcome is never retried by the harness. Cleanup reconciles attempted writes, including lost responses; if ownership or outcome cannot be established it fails without deleting unowned data. Resolve any reported test-fixture cleanup failure before claiming completion.

The six checks demonstrate provider submission on this path, not recipient mail-server delivery, queue deduplication or B02 recovery. Future B02/B06 cases must verify those contracts separately. The old `src/Niobium.Notification.IntegrationTests/test/` scripts are not invoked because their fallback recipients are not scoped to this authorization.

## Production verification

Only `scripts/read-only-deployment.py` runs after production deployment. It permits same-origin `GET /health`, rejects other methods/routes and redirects, limits the response size and checks the actual merge SHA. No template, subscription or queue credentials enter this client. `GET /Unsubscribe` is side-effecting and forbidden. It reports sanitized status and commit, never response bodies or secrets.

`/health` returns compiled metadata, not complete dependency health. Provider receipts and storage recovery remain unverified by this production check. Check the report commit and success plus the package workflow's actual merge checkout before reporting delivery complete.

## Provisioning and credentials

Keep Bicep environment agnostic. `infra/main.bicep` owns application resources and passes their environment-specific settings to the generic Function App module. `infra/function-app.bicep` owns host resources and combines caller settings with internal host settings. No Bicep resource declarations change in this milestone.

The workflow explicitly projects GitHub settings into runner `env`. `SetAppSettings.ps1` turns ordinary `APPSETTING_` entries into environment references in generated `appSettings`; `APPSETTING_KV_` entries carry only the setting name and an empty placeholder. The reusable module converts them to Key Vault references. `SetKeyVaultSecrets.ps1` reads actual values from runner environment, writes the existing vault, then refreshes Function App references after all writes succeed. `ClearAppSettings.ps1` restores the original parameter file. The hook behavior matches the already reviewed Invoicing correction and does not introduce an environment-specific module path.

The runner's `TEST_STORAGE_KEY` is used only for owned integration fixtures. Runtime Table/Blob access retains existing identity configuration; Resend uses existing runtime provider secrets. Do not publish credential values, mailbox values or provider responses in source, logs, reports or review comments.
