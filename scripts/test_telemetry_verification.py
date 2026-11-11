from datetime import datetime, timezone
import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock
from urllib.parse import parse_qs, urlsplit


spec = importlib.util.spec_from_file_location("test_telemetry", Path(__file__).with_name("test-telemetry.py"))
telemetry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(telemetry)

HOST = "niobiumnotify-func-test.synthetic.azurewebsites.net"
APP = "00000000-0000-0000-0000-000000000001"
KEY = "00000000-0000-0000-0000-000000000002"
COMPONENT = "/subscriptions/synthetic/resourceGroups/notification-test/providers/Microsoft.Insights/components/notification-test"
ENV = {"AZURE_ENV_NAME": "test", "TEST_ENVIRONMENT": "test", "AZURE_RESOURCE_GROUP": "notification-test",
       "TEST_API_URL": "https://" + HOST, "buildVersion": "1.2.345"}
METADATA = [HOST, COMPONENT, APP, KEY, "InstrumentationKey=" + KEY + ";IngestionEndpoint=https://synthetic.invalid/",
            "1.2.345-prerelease", "Niobium.Notification.Server.Test"]
STARTED = datetime(2026, 11, 11, 12, 0, tzinfo=timezone.utc)


def runner(values):
    return Mock(side_effect=[subprocess.CompletedProcess([], 0, value, "") for value in values])


class Clock:
    def __init__(self):
        self.elapsed = 0

    def __call__(self):
        return self.elapsed

    def sleep(self, seconds):
        self.elapsed += seconds


class TelemetryTests(unittest.TestCase):
    def test_current_test_destination_is_checked_before_read_only_ingestion_polling(self):
        azure = runner(METADATA + ["0", "1", "2"])
        clock = Clock()
        metrics = telemetry.ProviderMetrics(ENV, azure, clock, clock.sleep)
        metrics.verify(STARTED)
        self.assertEqual(20, clock.elapsed)
        calls = [call.args[0] for call in azure.call_args_list]
        self.assertTrue(all(call[1] in ("resource", "functionapp", "rest") for call in calls))
        self.assertTrue(all("create" not in call and "set" not in call and "post" not in call for call in calls))
        for call in calls[-3:]:
            self.assertEqual("get", call[call.index("--method") + 1])
            url = urlsplit(call[call.index("--url") + 1])
            self.assertEqual("api.applicationinsights.io", url.hostname)
            self.assertEqual("/v1/apps/" + APP + "/query", url.path)
            query = parse_qs(url.query)["query"][0]
            for expected in [STARTED.isoformat(), "1.2.345-prerelease", "Niobium.Notification.Server.Test",
                             'mode == "legacy"', 'outcome == "accepted"', "samples > 0", "total >= 0",
                             "provider.attempts", "provider.duration", "| count"]:
                self.assertIn(expected, query)
            self.assertNotIn(KEY, query)
        self.assertTrue(all(call.kwargs["capture_output"] and call.kwargs["timeout"] <= 60 for call in azure.call_args_list))

    def test_invalid_configuration_blocks_all_azure_reads(self):
        for key, value in [("AZURE_ENV_NAME", "prod"), ("TEST_ENVIRONMENT", "prod"),
                           ("TEST_API_URL", "https://niobiumnotify-func-prod.synthetic.azurewebsites.net"),
                           ("TEST_API_URL", "https://" + HOST + ".attacker.invalid"),
                           ("TEST_API_URL", "https://" + HOST + "/path"),
                           ("AZURE_RESOURCE_GROUP", ""), ("buildVersion", "1.2"), ("buildVersion", "1.2.3\n")]:
            with self.subTest(key=key, value=value):
                azure = Mock()
                with self.assertRaisesRegex(telemetry.Failure, "^test_telemetry_configuration_required$"):
                    telemetry.ProviderMetrics(dict(ENV, **{key: value}), azure)
                azure.assert_not_called()

    def test_wrong_destination_or_deployment_identity_fails_closed(self):
        for index, value in [(0, "niobiumnotify-func-prod.synthetic.azurewebsites.net"),
                             (1, COMPONENT + "\n" + COMPONENT), (1, COMPONENT + "-prod"),
                             (2, "not-a-guid-sensitive"), (3, "00000000-0000-0000-0000-000000000000"),
                             (4, "InstrumentationKey=" + APP), (4, "missing-sensitive"),
                             (4, "InstrumentationKey=" + KEY + ";InstrumentationKey=" + KEY),
                             (5, "old-version-sensitive"), (6, "Niobium.Notification.Server.Prod")]:
            values = list(METADATA)
            values[index] = value
            azure = runner(values)
            with self.subTest(index=index, value=value), self.assertRaises(telemetry.Failure) as error:
                telemetry.ProviderMetrics(ENV, azure)
            self.assertNotIn("sensitive", str(error.exception))
            self.assertTrue(all(call.args[0][1] != "rest" for call in azure.call_args_list))

    def test_cli_errors_never_expose_credentials_or_provider_output(self):
        for result in [subprocess.CompletedProcess([], 1, "sensitive", "sensitive"),
                       subprocess.TimeoutExpired(["sensitive"], 60, "sensitive"), OSError("sensitive")]:
            azure = Mock(side_effect=result) if isinstance(result, Exception) else Mock(return_value=result)
            with self.subTest(kind=type(result)), self.assertRaisesRegex(telemetry.Failure, "^test_telemetry_read_failed$"):
                telemetry.ProviderMetrics(ENV, azure)

    def test_missing_ingestion_expires_without_writes(self):
        azure = runner(METADATA + ["1"] * 36)
        clock = Clock()
        metrics = telemetry.ProviderMetrics(ENV, azure, clock, clock.sleep)
        with self.assertRaisesRegex(telemetry.Failure, "^test_provider_metrics_not_exported_within_six_minutes$"):
            metrics.verify(STARTED)
        self.assertEqual(360, clock.elapsed)
        self.assertEqual(36, sum(call.args[0][1] == "rest" for call in azure.call_args_list))

    def test_query_error_and_invalid_result_are_not_success_or_retry_triggers(self):
        for response in ["", "3", "sensitive-raw-telemetry"]:
            azure = runner(METADATA + [response])
            sleeper = Mock()
            metrics = telemetry.ProviderMetrics(ENV, azure, sleeper=sleeper)
            with self.assertRaisesRegex(telemetry.Failure, "^test_telemetry_count_invalid$"):
                metrics.verify(STARTED)
            sleeper.assert_not_called()
        azure = runner(METADATA + ["2"])
        metrics = telemetry.ProviderMetrics(ENV, azure)
        azure.side_effect = [subprocess.CompletedProcess([], 1, "sensitive", "sensitive")]
        with self.assertRaisesRegex(telemetry.Failure, "^test_telemetry_read_failed$"):
            metrics.verify(STARTED)


if __name__ == "__main__":
    unittest.main()
