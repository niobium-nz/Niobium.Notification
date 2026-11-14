"""Invoke the owned live queue fixture; never expose child process output or credentials."""
import json
from pathlib import Path
import subprocess


CHECKS = (
    "live_queue_provider_acceptance", "live_scheduled_recovery_observed",
    "live_owned_lost_outcome_snapshot", "live_same_provider_submission_recovered",
    "live_frozen_payload_and_window_preserved",
)
ERRORS = {
    "live_recovery_deadline_exceeded", "live_recovery_failed_details_suppressed",
    "live_test_environment_required", "live_test_commit_required", "live_test_host_required",
    "live_test_queue_required", "live_test_recipient_required", "live_owned_tenant_required",
    "live_template_ownership_required", "live_operation_must_be_new", "live_first_attempt_required",
    "live_acceptance_evidence_required", "live_recovery_window_too_short",
    "live_uncertain_snapshot_not_verified", "live_recovery_identity_changed",
    "live_frozen_payload_changed", "live_submission_identity_changed",
    "live_submission_not_recoverable", "live_scheduled_recovery_missing",
    "live_storage_access_denied", "live_storage_resource_missing", "live_storage_condition_failed",
    "live_queue_access_denied", "live_queue_quota_exceeded", "live_queue_resource_missing",
}
ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src/Niobium.Notification.Deployment.Tests/Niobium.Notification.Deployment.Tests.csproj"
ASSEMBLY = PROJECT.parent / "bin/Release/net10.0/Niobium.Notification.Deployment.Tests.dll"


def execute(arguments, environ, timeout, failure, runner):
    try:
        return runner(arguments, env=dict(environ), cwd=ROOT, capture_output=True,
                      text=True, check=False, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        raise failure("live_recovery_process_failed_output_suppressed") from None


def prepare(environ, failure, runner=subprocess.run):
    if environ.get("AZURE_ENV_NAME") != "test" or environ.get("TEST_ENVIRONMENT") != "test":
        raise failure("live_test_environment_required")
    if not environ.get("TEST_SERVICEBUS_CONNECTION"):
        raise failure("live_test_queue_credential_required")
    result = execute(["dotnet", "build", str(PROJECT), "--configuration", "Release", "--nologo"],
                     environ, 240, failure, runner)
    if result.returncode:
        raise failure("live_recovery_build_failed_output_suppressed")


def verify(environ, tenant, reporter, failure, runner=subprocess.run, report_path=None):
    # A unique report path prevents an earlier run's success from being reused.
    report = Path(report_path) if report_path else ROOT / "artifacts/deployed" / ("live-recovery-" + tenant + ".json")
    if report.exists():
        raise failure("live_recovery_report_already_exists")
    result = execute(["dotnet", str(ASSEMBLY), str(report)],
                     dict(environ, TEST_NOTIFICATION_TENANT=tenant), 510, failure, runner)
    try:
        evidence = json.loads(report.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        raise failure("live_recovery_report_missing_or_invalid") from None
    if result.returncode or evidence.get("success") is not True:
        code = evidence.get("error")
        raise failure(code if code in ERRORS else "live_recovery_failed_details_suppressed")
    if (evidence.get("commit") != environ.get("TEST_EXPECTED_COMMIT")
            or evidence.get("fixtureTenant") != tenant
            or evidence.get("checks") != [{"check": check, "passed": True} for check in CHECKS]):
        raise failure("live_recovery_report_evidence_mismatch")
    for check in CHECKS:
        reporter(check)
