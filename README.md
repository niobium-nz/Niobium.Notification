# Niobium.Notification

Read [rich domains and data access](docs/domain-data-access.md) before business or persistence changes.


.NET 10 Azure Functions service for templated email notifications, subscription events and contact requests. Consumers submit generic `NotifyCommand` messages; their business-specific data remains opaque to this shared service. Successful queue acceptance is not proof of provider submission or recipient delivery.

Read [agent instructions](AGENTS.md), [delivery and operations](docs/delivery-and-operations.md), and the [implementation ledger](docs/implementation-progress.md) before changing delivery or recovery behavior.

The [durable queue submission path](docs/durable-submission.md) adds an opt-in inbox,
frozen provider requests and recovery scheduling for B02. It remains disabled
until its legacy-message cutover and live verification prerequisites are met.

[Submission measurements](docs/submission-observability.md) distinguish provider
adapter results and latency from verified durable decisions and saved outcomes.
The deployed test checks Azure Monitor export using its existing approved send.

## Local verification

Use the pinned SDK and packages in `global.json` and the project files. Python 3, Node.js 22 and PowerShell 7 are needed for deployment-tooling tests. These commands do not contact deployed services:

```sh
dotnet restore Niobium.Notification.slnx --nologo
dotnet build Niobium.Notification.slnx --configuration Release --no-restore --nologo
dotnet test --project src/Niobium.Notification.Core.Tests/Niobium.Notification.Core.Tests.csproj --configuration Release --no-build --no-restore --report-trx --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml --results-directory artifacts/unit
python scripts/check-coverage.py artifacts/unit/coverage.cobertura.xml
node --test scripts/delivery-policy.test.cjs
python -m unittest discover -s scripts -p 'test_*.py'
```

Application coverage includes authored Contracts, Core and Server files, including HTTP and queue entry points. The minimum is 90% combined and 80% per module. Generated SDK/build code is excluded.

## Deployment

Push a feature branch to run builds, coverage, test deployment and real integration tests. After success, the author creates the PR, requests the designated reviewer, handles feedback and merges only with current-head approval and repository protections satisfied. A merged PR deploys production and runs a GET-only health check. The merging agent must monitor both production and package workflows to terminal results.

Do not run the legacy `src/Niobium.Notification.IntegrationTests/test/` scripts against deployed services: their recipients are not scoped to the approved Notification test recipient. The new deployed harness uses the explicitly configured `TEST_NOTIFICATION_SENDER` test environment variable, `TEST_NOTIFICATION_RECIPIENT` test environment secret and run-owned fixtures in existing resources. Never run test fixture or notification operations in production. The owner authorized native automatic code and security review, superseding the earlier manual-only choice. Check the review receipt for each head and address its findings; formal owner approval remains a separate merge requirement. See [automatic review guidance](docs/delivery-and-operations.md#automatic-review-authorization-and-activation).

`GET /health` returns only readiness and the compiled commit SHA. It does not assert storage, messaging or provider health. The production checker is intentionally limited to this route; even `GET /Unsubscribe` changes data.
