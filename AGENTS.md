# Working in Niobium.Notification

Read this file and [delivery and operations](docs/delivery-and-operations.md) before changes. Record the current branch, SHA and working-tree state. Preserve other work, use feature branches from current `main`, and keep the Niobium framework and package versions unchanged unless explicitly authorized.

This service is a dependency of Niobium.Invoicing. Follow the owner's accepted Q28 milestone order: Notification delivery safeguards, B02 durable retry recovery, then B06 delivery confirmation and lifecycle. The [ledger](docs/implementation-progress.md) distinguishes implemented controls from requirements. Do not silently interpret enqueue acceptance as delivery.

## Delivery and review

- Push feature work, verify terminal CI/CD on its exact SHA, then author-create or reuse one PR to `main`. Neither feature jobs nor the trusted readiness controller creates PRs.
- Request `niobium-nz` as reviewer, or the owner-authorized `5he11` when `niobium-nz` is the author. Verify identities and the recorded request.
- After creating a PR, reread `/workspace/AGENTS.md` and delegate independent review/approval to the existing `pr_approver`; create that named agent only if none exists. Provide current head/base and actual test evidence. Keep implementation (`niobium-nz`) and approval (`5he11`) roles separate. The implementation agent must not submit its own reviewer decision. Native Codex comments/reactions are not formal approval.
- Address actionable review feedback, rerun deployment/tests for changed heads and obtain one valid formal current-head approval from any approver GitHub permits. Do not invent an additional author exclusion. Check all comments/threads and actual native review results.
- Require up-to-date main, resolved threads, successful test deployment and the trusted custom status. Verify the proposed merge tree equals the tested tree. Squash merge through the connector with an expected-head SHA, without a second permission request once the rules are satisfied.
- Monitor the resulting production deployment, read-only tests and package workflow to terminal success. A failure requires immediate investigation and a fix through the same feature/review process. Never direct-push main, weaken protections or fabricate status.
- The initial trusted listener/harness cannot certify itself before it exists on main. A skipped trusted step is not success. Obtain an explicit administrative installation decision for this repository; earlier Invoicing exceptions do not carry over.

## Test and data boundaries

- Require 90% combined authored coverage and 80% per Contracts/Core/Server module. Do not exclude authored paths or weaken thresholds. Add boundary, failure and integration tests that demonstrate behavior.
- Use existing real test resources. The deployed harness may send only to the approved test environment `TEST_NOTIFICATION_RECIPIENT` secret and clean up only its own marked fixtures with ETags. Do not use the legacy contact-form integration scripts with their unscoped recipients.
- Production verification may only GET `/health`. It has no fixture setup, credentials for writes, queues, notifications, subscriptions, unsubscribe or cleanup. Do not infer that a GET endpoint is read-only.
- Do not print credentials, recipient addresses, provider response bodies or raw operational payloads. Use synthetic local test data and fixed diagnostics.
- Notification tenant is the sender/biller. Do not confuse it with Invoicing's management-group tenant.

## Infrastructure composition

The owner requires environment-agnostic Bicep and reusable Function App modules. `infra/function-app.bicep` owns host-bound resources and unions generic caller `appSettings` with internal host settings. `infra/main.bicep` owns application-specific resources. Environment suffixes and supplied values select the actual resources.

Workflow/environment variables and secrets reach the runner only through explicit workflow `env` projection or a reviewed export. The `infra/scripts` hooks convert `APPSETTING_` values into `appSettings`; `APPSETTING_KV_` values go directly from the runner to Key Vault, with only names and empty placeholders in Bicep parameters. Refresh the host's Key Vault references after successful writes. Do not add test-only credentials or application-specific secret parameters to the host module.

The new `TEST_STORAGE_KEY` is a masked runner-only fixture credential read from the existing test account. It is not a Function App setting, a new SAS, a new secret resource or a provider credential. No new Azure infrastructure is needed.
