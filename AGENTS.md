# Working in Niobium.Notification

Read [rich domains and data access](docs/domain-data-access.md) for domain, flow and
repository changes. Keep business transitions on rich domains and backend SDK
factories in framework adapters. Use enums for finite states/outcomes with explicit
storage compatibility. Execute I/O and mutation actions before an `if`, then test
a named result; do not hide actions inside conditions.

Read [HTTP client configuration](docs/http-client-configuration.md) before provider
transport changes. Use shared framework configuration when its released package is
available, preserve the existing transport, and keep redirect and retry policies
separate. The pending framework release is an explicit adoption dependency.

Read this file and [delivery and operations](docs/delivery-and-operations.md) before changes. Record the current branch, SHA and working-tree state. Preserve other work, use feature branches from current `main`, and keep the Niobium framework and package versions unchanged unless explicitly authorized.

Notification is an independent shared service that renders and sends generic notifications. It must not depend on Invoicing configuration, biller IDs or invoice templates. Invoicing owns its invoice-specific end-to-end tests and maps its biller to the existing Notification `Tenant` contract at the caller boundary. Follow the owner's accepted Q28 milestone order: Notification delivery safeguards, B02 durable retry recovery, then B06 delivery confirmation and lifecycle. The [ledger](docs/implementation-progress.md) distinguishes implemented controls from requirements. Do not silently interpret enqueue acceptance as delivery.

## Delivery and review

- Push feature work, verify terminal CI/CD on its exact SHA, then author-create or reuse one PR to `main`. Neither feature jobs nor the trusted readiness controller creates PRs.
- Request `niobium-nz` as reviewer, or the owner-authorized `5he11` when `niobium-nz` is the author. Verify identities and the recorded request.
- The owner approved native automatic review on 2026-11-06 (Pacific/Auckland), superseding the earlier manual-only choice. Use Invoicing's recorded settings: code review for All PRs On every push, and security review for All PRs Whenever code review runs. This is authorization, not evidence that the external settings have been saved; verify configuration and an automatic receipt for the current head separately. The connector cannot inspect or change these settings. See the delivery guide for the activation checkpoint.
- Request `5he11` and keep implementation (`niobium-nz`) and approval (`5he11`) roles separate. The implementation agent must not submit its own reviewer decision or delegate an approval as a substitute. Native comments/reactions are not formal approval. This choice does not change Invoicing's standing `pr_approver` handoff.
- Address actionable review feedback, rerun deployment/tests for changed heads and obtain one valid formal current-head approval from any approver GitHub permits. Do not invent an additional author exclusion. Check all comments/threads and verify native review results separately.
- Require up-to-date main, resolved threads, successful test deployment and the trusted custom status. Verify the proposed merge tree equals the tested tree. Squash merge through the connector with an expected-head SHA, without a second permission request once the rules are satisfied.
- Monitor the resulting production deployment, read-only tests and package workflow to terminal success. A failure requires immediate investigation and a fix through the same feature/review process. Never direct-push main, weaken protections or fabricate status.
- The initial trusted listener/harness cannot certify itself before it exists on main. A skipped trusted step is not success. Obtain an explicit administrative installation decision for this repository; earlier Invoicing exceptions do not carry over.

## Test and data boundaries

- Require 90% combined authored coverage and 80% per Contracts/Core/Server module. Do not exclude authored paths or weaken thresholds. Add boundary, failure and integration tests that demonstrate behavior.
- Use existing real test resources. The deployed harness may send only to the approved test environment `TEST_NOTIFICATION_RECIPIENT` secret and clean up only its own marked fixtures with ETags. Do not use the legacy contact-form integration scripts with their unscoped recipients.
- Production verification may only GET `/health`. It has no fixture setup, credentials for writes, queues, notifications, subscriptions, unsubscribe or cleanup. Do not infer that a GET endpoint is read-only.
- Do not print credentials, recipient addresses, provider response bodies or raw operational payloads. Use synthetic local test data and fixed diagnostics.
- Service-owned configuration, documentation and tests use Notification terminology (`Tenant`, `Channel`, sender, template and recipient). Consumer-specific vocabulary belongs in the consuming service. Preserve established wire/storage contracts.
- The deployed harness reads runner-only `TEST_NOTIFICATION_SENDER` from the GitHub `test` environment variable (`vars`) and `TEST_NOTIFICATION_RECIPIENT` from its environment secret (`secrets`). The owner confirmed these bindings on 2026-11-06; do not request a second sender secret. The sender must be explicitly approved and permitted by the existing test provider configuration; the recipient must be an approved test mailbox. Do not infer either from an arbitrary tenant/template or require any Invoicing setting. Create only this run's generic template and blob in existing test resources, with ownership-checked cleanup.

## Infrastructure composition

The owner requires environment-agnostic Bicep and reusable Function App modules. `infra/function-app.bicep` owns host-bound resources and unions generic caller `appSettings` with internal host settings. `infra/main.bicep` owns application-specific resources. Environment suffixes and supplied values select the actual resources.

Business resource names belong in `infra/main.bicepparam` defaults or deployment inputs. Bicep takes generic lists and creates resources; do not hardcode table or queue names in resource declarations. Follow `serviceBusQueueNames`/`SERVICE_BUS_QUEUE_NAMES` and `storageTableNames`/`STORAGE_TABLE_NAMES`. Include existing service-owned tables in the default list, and match the entity type names used by the repository. See [resource-name configuration](docs/delivery-and-operations.md#resource-name-configuration).

Workflow/environment variables and secrets reach the runner only through explicit workflow `env` projection or a reviewed export. The `infra/scripts` hooks convert `APPSETTING_` values into `appSettings`; `APPSETTING_KV_` values go directly from the runner to Key Vault, with only names and empty placeholders in Bicep parameters. Refresh the host's Key Vault references after successful writes. Do not add test-only credentials or application-specific secret parameters to the host module.

The new `TEST_STORAGE_KEY` is a masked runner-only fixture credential read from the existing test account. It is not a Function App setting, a new SAS, a new secret resource or a provider credential. The fixture credential does not require a separate Azure stack; application-owned tables are declared in `infra/main.bicep`.
