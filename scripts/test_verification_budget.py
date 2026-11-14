import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock, patch


def module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


budgets = module("verification-budget")
live = module("live-recovery")
deployed = module("deployed-integration")
fixtures = module("test_deployment_verification")


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.elapsed = 0
        self.budget = budgets.Budget(deployed.Failure, clock=lambda: self.elapsed)

    def advance(self, seconds):
        self.elapsed += seconds

    def test_build_credentials_child_and_telemetry_share_one_deadline(self):
        observed = []
        def run(args, **kwargs):
            observed.append(kwargs["timeout"])
            self.advance(kwargs["timeout"])
            return subprocess.CompletedProcess(args, 0, "value", "")
        # These are deliberately worst-case durations. Every phase consumes the
        # same clock; entering the child or telemetry cannot restart the allowance.
        for timeout in (240, 60, 60, 510):
            live.execute([], {}, timeout, deployed.Failure, run, self.budget)
        self.assertEqual([240, 60, 60, 240], observed)
        metrics = object.__new__(deployed.telemetry.ProviderMetrics)
        metrics.budget = self.budget
        metrics.runner = Mock()
        with self.assertRaisesRegex(deployed.Failure, "^test_verification_budget_exhausted$"):
            metrics.azure("rest")
        metrics.runner.assert_not_called()
        self.assertEqual(600, self.elapsed)

    def test_cleanup_reserve_is_bounded_and_cannot_be_extended(self):
        self.elapsed = 600
        with self.assertRaises(deployed.Failure):
            self.budget.check()
        self.budget.begin_cleanup()
        self.assertEqual(90, self.budget.timeout(300))
        self.advance(75)
        self.budget.begin_cleanup()
        self.assertEqual(15, self.budget.timeout(30))
        self.budget.sleep(30, self.advance)
        with self.assertRaisesRegex(deployed.Failure, "^test_cleanup_budget_exhausted$"):
            self.budget.check()
        self.assertEqual(690, self.elapsed)

    def test_child_timeout_still_cleans_owned_fixtures_and_writes_failed_report(self):
        import contextlib
        import io
        import json
        import os
        import sys
        import tempfile
        storage = fixtures.FixtureStorage()
        def exhaust(*args, **kwargs):
            self.assertIs(self.budget, kwargs["budget"])
            self.elapsed = 600
            self.budget.check()
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "integration.json"
            with patch.dict(os.environ, fixtures.ENV, clear=True), patch.object(sys, "argv", ["test", "--report", str(report)]), \
                    patch.object(deployed.budgets, "Budget", return_value=self.budget), \
                    patch.object(deployed.live, "prepare", return_value=fixtures.ENV), \
                    patch.object(deployed.readonly, "verify", return_value={"success": True}), \
                    patch.object(deployed.telemetry, "ProviderMetrics") as metrics, \
                    patch.object(deployed, "Storage", return_value=storage), \
                    patch.object(deployed, "post", side_effect=[500, 201, 400]), \
                    patch.object(deployed.live, "verify", side_effect=exhaust), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, deployed.main())
                metrics.return_value.verify.assert_not_called()
            evidence = json.loads(report.read_text())
        self.assertFalse(evidence["success"])
        self.assertEqual("test_verification_budget_exhausted", evidence["error"])
        self.assertIn({"check": "owned_fixture_cleanup", "passed": True}, evidence["checks"])
        self.assertTrue(self.budget.cleaning)
        self.assertEqual(2, len([call for call in storage.calls if call[1] == "delete"]))

    def test_storage_cleanup_calls_and_read_only_retries_use_remaining_time(self):
        self.elapsed = 595
        runner = Mock(return_value=subprocess.CompletedProcess([], 0, "{}", ""))
        storage = deployed.Storage({"account": "synthetic", "key": "synthetic"}, runner, self.budget)
        storage.call("entity", "show")
        self.assertEqual(5, runner.call_args.kwargs["timeout"])
        opener = Mock(open=Mock(return_value=fixtures.response({"status": "pending"})))
        client = deployed.readonly.ReadOnlyClient(fixtures.HOST, opener, self.budget)
        with self.assertRaisesRegex(deployed.Failure, "^test_verification_budget_exhausted$"):
            deployed.readonly.verify(fixtures.HOST, fixtures.SHA, client=client, sleeper=self.advance, budget=self.budget)
        self.assertEqual(1, opener.open.call_count)
        self.assertEqual(5, opener.open.call_args.kwargs["timeout"])
        self.budget.begin_cleanup()
        self.elapsed = 688
        storage.call("entity", "delete")
        self.assertEqual(2, runner.call_args.kwargs["timeout"])


if __name__ == "__main__":
    unittest.main()
