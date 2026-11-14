"""Mandatory real test-service checks. Never runs with production or unscoped recipients."""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, build_opener
import uuid


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


readonly = load("read-only-deployment")
telemetry = load("test-telemetry")
live = load("live-recovery")
budgets = load("verification-budget")
Failure = readonly.VerificationFailure


def fixture_settings(environ):
    if environ.get("AZURE_ENV_NAME") != "test":
        raise Failure("test_environment_required")
    recipient = environ.get("TEST_NOTIFICATION_RECIPIENT", "")
    if not re.fullmatch(r"[^\s,;<>@]+@[^\s,;<>@]+\.[^\s,;<>@]+", recipient):
        raise Failure("approved_test_recipient_required")
    sender = environ.get("TEST_NOTIFICATION_SENDER", "")
    if not re.fullmatch(r"[^\s,;<>@]+@[^\s,;<>@]+\.[^\s,;<>@]+", sender):
        raise Failure("approved_test_sender_required")
    return recipient, sender


def configuration(environ):
    recipient, sender = fixture_settings(environ)
    if environ.get("TEST_ENVIRONMENT") != "test":
        raise Failure("test_environment_required")
    base = readonly.origin(environ.get("TEST_API_URL", ""))
    host = urlsplit(base).hostname
    if not host.startswith("niobiumnotify-func-test.") or not host.endswith(".azurewebsites.net"):
        raise Failure("test_host_required")
    account = environ.get("TEST_STORAGE_ACCOUNT")
    if account != "niobiumnotifystdtest":
        raise Failure("test_storage_required")
    key = environ.get("TEST_STORAGE_KEY", "")
    if not key or "\n" in key or "\r" in key:
        raise Failure("test_storage_credential_required")
    return {"base": base, "account": account, "key": key, "recipient": recipient, "sender": sender}


class Storage:
    def __init__(self, config, runner=subprocess.run, budget=None):
        self.runner = runner
        self.budget = budget
        self.environ = dict(os.environ, AZURE_STORAGE_ACCOUNT=config["account"], AZURE_STORAGE_KEY=config["key"])

    def call(self, *args):
        timeout = self.budget.timeout(30) if self.budget else 30
        try:
            result = self.runner(["az", "storage", *args, "--only-show-errors", "--output", "json"],
                                 env=self.environ, capture_output=True, text=True, check=False, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired):
            raise Failure("test_storage_operation_failed") from None
        if result.returncode:
            raise Failure("test_storage_operation_failed")
        try:
            return json.loads(result.stdout or "null")
        except ValueError:
            raise Failure("invalid_storage_metadata") from None

    def entity(self, tenant, channel="ContactUs"):
        return self.call("entity", "show", "--table-name", "Template", "--partition-key", tenant, "--row-key", channel)


def post(base, payload, opener=None, timeout=45):
    # Guard the side-effecting boundary independently as well. No retry of unknown side effects.
    base = readonly.origin(base)
    host = urlsplit(base).hostname
    if not host.startswith("niobiumnotify-func-test.") or not host.endswith(".azurewebsites.net"):
        raise Failure("test_host_required")
    request = Request(base + "/ContactUs", data=json.dumps(payload).encode("utf-8"), method="POST",
                      headers={"Content-Type": "application/json"})
    try:
        with (opener or build_opener(readonly.NoRedirect())).open(request, timeout=timeout) as response:
            if response.geturl() != request.full_url:
                raise Failure("test_redirect_rejected")
            return response.status
    except HTTPError as error:
        return error.code
    except (URLError, TimeoutError, OSError):
        raise Failure("test_http_outcome_unknown_no_retry") from None


def entity_etag(entity):
    value = entity.get("etag") or entity.get("odata.etag")
    if not value:
        raise Failure("entity_etag_required")
    return value


def run(environ, reporter):
    budget = budgets.Budget(Failure)
    config = configuration(environ)
    live_environment = live.prepare(environ, Failure, budget=budget)
    health = readonly.verify(config["base"], environ.get("TEST_EXPECTED_COMMIT", ""), budget=budget)
    reporter("deployed_commit")
    metrics = telemetry.ProviderMetrics(environ, budget=budget)
    reporter("test_telemetry_destination")
    storage = Storage(config, budget=budget)
    fixture_tenant = str(uuid.uuid4())
    blob = fixture_tenant + "/delivery-verification.html"
    owned = {"PartitionKey": fixture_tenant, "RowKey": "ContactUs", "From": config["sender"],
             "FromName": "Niobium deployment verification", "Subject": "Notification deployment verification",
             "FallbackTo": config["recipient"], "Blob": "delivery-verification.html", "VerificationRun": fixture_tenant}
    entity_attempted = False
    blob_attempted = False
    try:
        # A failed CLI response may still follow a committed write. Reconcile attempted
        # writes in finally, and delete only after verifying ownership and an ETag.
        entity_attempted = True
        storage.call("entity", "insert", "--table-name", "Template", "--if-exists", "fail", "--entity",
                     *(k + "=" + v for k, v in owned.items()))
        entity = storage.entity(fixture_tenant)
        if any(entity.get(k) != v for k, v in owned.items()):
            raise Failure("fixture_readback_mismatch")
        original_etag = entity_etag(entity)
        # The Notification wire contract calls the fixture namespace Tenant.
        request = {"ID": str(uuid.uuid4()), "Tenant": fixture_tenant, "Message": "Notification test environment verification",
                   "Name": "Deployment verification", "Contact": "Synthetic fixture", "Token": "test-captcha-disabled"}
        # Missing body must fail, proving this host actually reads our unique template instead of silently skipping it.
        if post(config["base"], request, timeout=budget.timeout(45)) != 500:
            raise Failure("missing_blob_not_rejected")
        reporter("real_template_blob_failure")
        with tempfile.TemporaryDirectory() as directory:
            body = Path(directory) / "body.html"
            body.write_text("<html><body><p>Notification deployment verification in the test environment.</p><p>{{MESSAGE}}</p></body></html>", encoding="utf-8")
            blob_attempted = True
            storage.call("blob", "upload", "--container-name", "templates", "--name", blob, "--file", str(body),
                         "--overwrite", "false", "--metadata", "verificationrun=" + fixture_tenant)
        request["ID"] = str(uuid.uuid4())
        started = datetime.now(timezone.utc)
        if post(config["base"], request, timeout=budget.timeout(45)) != 201:
            raise Failure("real_provider_send_failed")
        reporter("real_provider_submission")
        if entity_etag(storage.entity(fixture_tenant)) != original_etag:
            raise Failure("template_changed_by_send")
        reporter("template_unchanged")
        request["Message"] = ""
        if post(config["base"], request, timeout=budget.timeout(45)) != 400:
            raise Failure("invalid_request_not_rejected")
        reporter("invalid_request_rejected")
        live.verify(live_environment, fixture_tenant, reporter, Failure, budget=budget)
        metrics.verify(started)
        reporter("provider_metrics_exported")
        return health
    finally:
        budget.begin_cleanup()
        # Delete only this run's unique records, never existing templates or shared containers/tables.
        cleanup_errors = []
        if blob_attempted:
            try:
                info = storage.call("blob", "show", "--container-name", "templates", "--name", blob)
                if info.get("metadata", {}).get("verificationrun") != fixture_tenant:
                    raise Failure("blob_ownership_changed")
                storage.call("blob", "delete", "--container-name", "templates", "--name", blob,
                             "--if-match", info["properties"]["etag"])
            except (Failure, KeyError):
                cleanup_errors.append("blob")
        if entity_attempted:
            try:
                current = storage.entity(fixture_tenant)
                if current.get("VerificationRun") != fixture_tenant or any(current.get(k) != v for k, v in owned.items()):
                    raise Failure("entity_ownership_changed")
                storage.call("entity", "delete", "--table-name", "Template", "--partition-key", fixture_tenant,
                             "--row-key", "ContactUs", "--if-match", entity_etag(current))
            except Failure:
                cleanup_errors.append("entity")
        if cleanup_errors:
            raise Failure("fixture_cleanup_failed")
        reporter("owned_fixture_cleanup")


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--report")
    mode.add_argument("--check-configuration", action="store_true")
    args = parser.parse_args()
    if args.check_configuration:
        try:
            fixture_settings(os.environ)
            print("PASS test_fixture_configuration")
            return 0
        except Failure as error:
            print("FAIL " + str(error))
            return 1
    report = {"commit": os.environ.get("TEST_EXPECTED_COMMIT"), "checks": [], "success": False}
    def passed(name):
        report["checks"].append({"check": name, "passed": True})
        print("PASS " + name, flush=True)
    try:
        run(os.environ, passed)
        report["success"] = True
    except (Failure, telemetry.Failure) as error:
        report["error"] = str(error)
        print("FAIL " + str(error), flush=True)
    except Exception:
        report["error"] = "unexpected_test_failure_output_suppressed"
        print("FAIL unexpected_test_failure_output_suppressed", flush=True)
    finally:
        target = Path(args.report)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
