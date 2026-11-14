import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock


spec = importlib.util.spec_from_file_location("live_recovery", Path(__file__).with_name("live-recovery.py"))
live = importlib.util.module_from_spec(spec)
spec.loader.exec_module(live)
ENV = {"AZURE_ENV_NAME": "test", "TEST_ENVIRONMENT": "test", "TEST_EXPECTED_COMMIT": "a" * 40,
       "TEST_SERVICEBUS_CONNECTION": "sensitive-queue-credential", "TEST_NOTIFICATION_RECIPIENT": "approved@example.invalid"}


class LiveRecoveryTests(unittest.TestCase):
    def test_missing_or_production_settings_never_start_child(self):
        for changes in ({"AZURE_ENV_NAME": "prod"}, {"TEST_ENVIRONMENT": "prod"}, {"TEST_SERVICEBUS_CONNECTION": ""}):
            runner = Mock()
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                live.prepare(dict(ENV, **changes), RuntimeError, runner)
            runner.assert_not_called()

    def test_build_failure_and_timeout_suppress_process_details(self):
        runner = Mock(return_value=subprocess.CompletedProcess([], 1, "private stdout", "private stderr"))
        with self.assertRaisesRegex(RuntimeError, "^live_recovery_build_failed_output_suppressed$"):
            live.prepare(ENV, RuntimeError, runner)
        self.assertNotIn(ENV["TEST_SERVICEBUS_CONNECTION"], runner.call_args.args[0])
        self.assertTrue(runner.call_args.kwargs["capture_output"])
        runner.side_effect = subprocess.TimeoutExpired("private command", 1, output="private response")
        with self.assertRaisesRegex(RuntimeError, "^live_recovery_process_failed_output_suppressed$"):
            live.prepare(ENV, RuntimeError, runner)

    def verify_report(self, evidence, code=0):
        checks = []
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "evidence.json"
            def run(args, **kwargs):
                self.assertEqual("fixture", kwargs["env"]["TEST_NOTIFICATION_TENANT"])
                self.assertNotIn(ENV["TEST_NOTIFICATION_RECIPIENT"], args)
                target.write_text(json.dumps(evidence), encoding="utf-8")
                return subprocess.CompletedProcess(args, code, "private stdout", "private stderr")
            live.verify(ENV, "fixture", checks.append, RuntimeError, run, target)
        return checks

    def good_report(self):
        return {"success": True, "commit": ENV["TEST_EXPECTED_COMMIT"], "fixtureTenant": "fixture",
                "checks": [{"check": check, "passed": True} for check in live.CHECKS]}

    def test_success_requires_exact_commit_fixture_and_complete_passed_checks(self):
        self.assertEqual(list(live.CHECKS), self.verify_report(self.good_report()))
        for changes in ({"commit": "b" * 40}, {"fixtureTenant": "another"}, {"checks": []},
                        {"checks": [{"check": name, "passed": False} for name in live.CHECKS]}):
            with self.subTest(changes=changes), self.assertRaisesRegex(RuntimeError, "^live_recovery_report_evidence_mismatch$"):
                self.verify_report(dict(self.good_report(), **changes))

    def test_failure_exposes_only_known_diagnostic(self):
        for error, expected in [("live_scheduled_recovery_missing", "live_scheduled_recovery_missing"),
                                ("private provider payload", "live_recovery_failed_details_suppressed")]:
            with self.subTest(error=error), self.assertRaisesRegex(RuntimeError, "^" + expected + "$"):
                self.verify_report(dict(self.good_report(), success=False, error=error), code=1)

    def test_stale_report_is_never_reused(self):
        runner = Mock()
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "evidence.json"
            target.write_text(json.dumps(self.good_report()))
            with self.assertRaisesRegex(RuntimeError, "^live_recovery_report_already_exists$"):
                live.verify(ENV, "fixture", Mock(), RuntimeError, runner, target)
        runner.assert_not_called()


if __name__ == "__main__":
    unittest.main()
