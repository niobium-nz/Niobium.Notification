import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), Path(__file__).with_name(name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


readonly = module("read-only-deployment")
deployed = module("deployed-integration")
resources = module("test-resources")
SHA = "a" * 40
HOST = "https://niobiumnotify-func-test.synthetic.azurewebsites.net"
TENANT = "00000000-0000-0000-0000-000000000001"
ENV = {"AZURE_ENV_NAME": "test", "TEST_ENVIRONMENT": "test", "TEST_API_URL": HOST,
       "TEST_STORAGE_ACCOUNT": "niobiumnotifystdtest", "TEST_STORAGE_KEY": "synthetic-key",
       "TEST_NOTIFICATION_SENDER": "verified@example.invalid", "TEST_NOTIFICATION_RECIPIENT": "approved@example.invalid",
       "TEST_EXPECTED_COMMIT": SHA}


def response(payload, url=HOST + "/health", status=200):
    result = Mock()
    result.status = status
    result.geturl.return_value = url
    result.read.return_value = json.dumps(payload).encode()
    result.__enter__ = Mock(return_value=result)
    result.__exit__ = Mock(return_value=False)
    return result


class ReadOnlyTests(unittest.TestCase):
    def test_only_get_health_reaches_network(self):
        opener = Mock()
        client = readonly.ReadOnlyClient(HOST, opener)
        for method, path in [("POST", "/health"), ("GET", "/Unsubscribe"), ("GET", "/ContactUs"),
                             ("GET", "/health?token=secret"), ("GET", "//elsewhere/health")]:
            with self.subTest(method=method, path=path), self.assertRaises(readonly.VerificationFailure):
                client.request(method, path)
        opener.open.assert_not_called()
        opener.open.return_value = response({"status": "ready", "commit": SHA})
        self.assertEqual(SHA, client.request()["commit"])
        request = opener.open.call_args.args[0]
        self.assertEqual("GET", request.method)
        self.assertEqual(HOST + "/health", request.full_url)
        self.assertIsNone(request.data)

    def test_invalid_origins_rejected(self):
        for value in ["http://host", "https://u:p@host", "https://host/path", "https://host?token=x",
                      "https://host#fragment", "https://host:123", "https://host:bad", "", "https://[broken", "https://host\n", "https://ho st"]:
            with self.subTest(value=value), self.assertRaises(readonly.VerificationFailure):
                readonly.ReadOnlyClient(value)

    def test_redirect_and_large_or_invalid_response_rejected(self):
        opener = Mock()
        for received in [response({}, url="https://elsewhere/health"), response({}, status=302), response({"data": "x" * 4097})]:
            opener.open.return_value = received
            with self.assertRaises(readonly.VerificationFailure):
                readonly.ReadOnlyClient(HOST, opener).request()
        self.assertIsNone(readonly.NoRedirect().redirect_request(None, None, 302, "", {}, "https://elsewhere"))
        opener.open.return_value = response({})
        opener.open.return_value.read.return_value = b"not JSON with sensitive body"
        with self.assertRaisesRegex(readonly.VerificationFailure, "^health_request_failed$"):
            readonly.ReadOnlyClient(HOST, opener).request()

    def test_transport_errors_are_sanitized(self):
        for error in [URLError("sensitive"), TimeoutError("sensitive"), HTTPError(HOST, 500, "sensitive", {}, None)]:
            with self.subTest(error=type(error)), self.assertRaisesRegex(readonly.VerificationFailure, "^health_request_failed$"):
                readonly.ReadOnlyClient(HOST, Mock(open=Mock(side_effect=error))).request()

    def test_commit_readiness_retries_then_verifies_exact_sha(self):
        client = Mock()
        client.request.side_effect = [readonly.VerificationFailure("transient"), {"status": "ready", "commit": "b" * 40},
                                      {"status": "ready", "commit": SHA}]
        sleeper = Mock()
        report = readonly.verify(HOST, SHA, attempts=3, client=client, sleeper=sleeper)
        self.assertTrue(report["success"])
        self.assertEqual(SHA, report["commit"])
        self.assertEqual(2, sleeper.call_count)
        self.assertEqual(3, client.request.call_count)

    def test_commit_mismatch_exhausts_bounded_read_only_retries(self):
        client = Mock(request=Mock(return_value={"status": "ready", "commit": "b" * 40}))
        sleeper = Mock()
        with self.assertRaisesRegex(readonly.VerificationFailure, "deployed_commit_not_ready"):
            readonly.verify(HOST, SHA, attempts=2, client=client, sleeper=sleeper)
        self.assertEqual(2, client.request.call_count)
        sleeper.assert_called_once_with(5)
        with self.assertRaisesRegex(readonly.VerificationFailure, "invalid_expected_commit"):
            readonly.verify(HOST, "main", client=client)
        self.assertEqual(2, client.request.call_count)


class FixtureStorage:
    def __init__(self, mode=None):
        self.mode = mode
        self.calls = []
        self.owned = None
        self.blob = None

    def entity(self, tenant, channel="ContactUs"):
        if self.owned is None or tenant != self.owned["PartitionKey"] or channel != "ContactUs":
            raise AssertionError("Only this run's owned template may be read")
        value = dict(self.owned, etag="entity-etag")
        if self.mode == "entity-owner-changed" and any(c[:2] == ("blob", "upload") for c in self.calls):
            value["VerificationRun"] = "someone-else"
        return value

    def call(self, *args):
        self.calls.append(args)
        if args[:2] == ("entity", "insert"):
            self.owned = dict(v.split("=", 1) for v in args[args.index("--entity") + 1:])
            if self.mode == "insert-response-lost":
                raise deployed.Failure("test_storage_operation_failed")
        elif args[:2] == ("blob", "upload"):
            self.blob = args[args.index("--name") + 1]
            if self.mode == "upload-response-lost":
                raise deployed.Failure("test_storage_operation_failed")
        elif args[:2] == ("blob", "show"):
            return {"metadata": {"verificationrun": "someone-else" if self.mode == "blob-owner-changed" else self.owned["PartitionKey"]},
                    "properties": {"etag": "blob-etag"}}
        return None


class DeployedTests(unittest.TestCase):
    def setUp(self):
        for name in ("prepare", "verify"):
            patcher = patch.object(deployed.live, name)
            mocked = patcher.start()
            if name == "prepare":
                mocked.return_value = ENV
            self.addCleanup(patcher.stop)

    def test_preflight_requires_no_resource_credentials_or_network(self):
        settings = {k: ENV[k] for k in ("AZURE_ENV_NAME", "TEST_NOTIFICATION_SENDER", "TEST_NOTIFICATION_RECIPIENT")}
        with patch.dict(os.environ, settings, clear=True), patch.object(sys, "argv", ["deployed-integration.py", "--check-configuration"]), patch.object(deployed, "Storage") as storage, patch.object(deployed.readonly, "verify") as health, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, deployed.main())
        storage.assert_not_called()
        health.assert_not_called()
        self.assertEqual("PASS test_fixture_configuration\n", output.getvalue())
        self.assertNotIn(settings["TEST_NOTIFICATION_RECIPIENT"], output.getvalue())
        self.assertNotIn(settings["TEST_NOTIFICATION_SENDER"], output.getvalue())

    def test_preflight_fails_closed_for_missing_settings(self):
        with patch.dict(os.environ, {"AZURE_ENV_NAME": "test"}, clear=True), patch.object(sys, "argv", ["deployed-integration.py", "--check-configuration"]), patch.object(deployed, "Storage") as storage, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(1, deployed.main())
        storage.assert_not_called()
        self.assertEqual("FAIL approved_test_recipient_required\n", output.getvalue())

    def run_fixture(self, storage, statuses=(500, 201, 400), metrics=None):
        posted = []
        checks = []
        iterator = iter(statuses)

        def post(base, body, **kwargs):
            self.assertEqual(HOST, base)
            posted.append(dict(body))
            return next(iterator)

        with patch.object(deployed, "Storage", return_value=storage), patch.object(deployed.readonly, "verify", return_value={"success": True}), patch.object(deployed, "post", side_effect=post), patch.object(deployed.telemetry, "ProviderMetrics", return_value=metrics or Mock()):
            deployed.run(ENV, checks.append)
        return posted, checks

    def test_production_and_unscoped_configuration_fail_before_io(self):
        for key, value in [("AZURE_ENV_NAME", "prod"), ("TEST_ENVIRONMENT", "prod"),
                           ("TEST_API_URL", "https://niobiumnotify-func-prod.synthetic.azurewebsites.net"),
                           ("TEST_API_URL", "https://niobiumnotify-func-test.synthetic.azurewebsites.net.attacker.invalid"),
                           ("TEST_STORAGE_ACCOUNT", "niobiumnotifystdprod"), ("TEST_STORAGE_KEY", ""),
                           ("TEST_NOTIFICATION_SENDER", ""), ("TEST_NOTIFICATION_SENDER", "not-an-address"),
                           ("TEST_NOTIFICATION_SENDER", "a@example.invalid,b@example.invalid"),
                           ("TEST_NOTIFICATION_SENDER", "a@example.invalid\r\nBcc:other@example.invalid"),
                           ("TEST_NOTIFICATION_RECIPIENT", "a@example.invalid,b@example.invalid"),
                           ("TEST_NOTIFICATION_RECIPIENT", "")]:
            with self.subTest(key=key, value=value), patch.object(deployed, "Storage") as storage, self.assertRaises(deployed.Failure):
                deployed.run(dict(ENV, **{key: value}), Mock())
            storage.assert_not_called()

    def test_real_path_uses_only_approved_recipient_and_owned_fixture_cleanup(self):
        storage = FixtureStorage()
        posted, checks = self.run_fixture(storage)
        self.assertEqual(ENV["TEST_NOTIFICATION_RECIPIENT"], storage.owned["FallbackTo"])
        self.assertEqual(ENV["TEST_NOTIFICATION_SENDER"], storage.owned["From"])
        self.assertNotEqual(TENANT, storage.owned["PartitionKey"])
        self.assertEqual([storage.owned["PartitionKey"]] * 3, [p["Tenant"] for p in posted])
        self.assertNotEqual(posted[0]["ID"], posted[1]["ID"])
        self.assertEqual("", posted[2]["Message"])
        self.assertEqual(["deployed_commit", "test_telemetry_destination", "real_template_blob_failure", "real_provider_submission", "template_unchanged", "invalid_request_rejected", "provider_metrics_exported", "owned_fixture_cleanup"], checks)
        deletions = [c for c in storage.calls if c[1] == "delete"]
        self.assertEqual(2, len(deletions))
        self.assertTrue(all("--if-match" in c for c in deletions))
        self.assertEqual(storage.blob, deletions[0][deletions[0].index("--name") + 1])
        self.assertEqual(storage.owned["PartitionKey"], deletions[1][deletions[1].index("--partition-key") + 1])

    def test_sender_is_required_before_health_or_storage_access(self):
        settings = dict(ENV)
        settings.pop("TEST_NOTIFICATION_SENDER")
        with patch.object(deployed.readonly, "verify") as health, patch.object(deployed, "Storage") as storage, self.assertRaisesRegex(deployed.Failure, "^approved_test_sender_required$"):
            deployed.run(settings, Mock())
        health.assert_not_called()
        storage.assert_not_called()

    def test_generic_fixture_needs_no_existing_consumer_template(self):
        storage = FixtureStorage()
        posted, checks = self.run_fixture(storage)
        self.assertEqual(("entity", "insert"), storage.calls[0][:2])
        self.assertEqual("ContactUs", storage.owned["RowKey"])
        self.assertEqual("Notification deployment verification", storage.owned["Subject"])
        self.assertEqual("Notification test environment verification", posted[1]["Message"])
        self.assertIn("real_provider_submission", checks)

    def test_live_recovery_uses_owned_template_and_failure_still_cleans_it(self):
        storage = FixtureStorage()
        deployed.live.verify.side_effect = deployed.Failure("live_scheduled_recovery_missing")
        with self.assertRaisesRegex(deployed.Failure, "^live_scheduled_recovery_missing$"):
            self.run_fixture(storage)
        deployed.live.verify.assert_called_once()
        self.assertEqual(storage.owned["PartitionKey"], deployed.live.verify.call_args.args[1])
        self.assertEqual(2, len([c for c in storage.calls if c[1] == "delete"]))

    def test_live_build_failure_precedes_any_service_fixture(self):
        deployed.live.prepare.side_effect = deployed.Failure("live_recovery_build_failed_output_suppressed")
        with patch.object(deployed.readonly, "verify") as health, patch.object(deployed, "Storage") as storage:
            with self.assertRaisesRegex(deployed.Failure, "^live_recovery_build_failed_output_suppressed$"):
                deployed.run(ENV, Mock())
        health.assert_not_called()
        storage.assert_not_called()

    def test_uncertain_fixture_writes_are_reconciled(self):
        for mode, expected in [("insert-response-lost", 1), ("upload-response-lost", 2)]:
            with self.subTest(mode=mode):
                storage = FixtureStorage(mode)
                with self.assertRaisesRegex(deployed.Failure, "test_storage_operation_failed"):
                    self.run_fixture(storage)
                self.assertEqual(expected, len([c for c in storage.calls if c[1] == "delete"]))

    def test_changed_ownership_is_not_deleted(self):
        for mode, protected in [("blob-owner-changed", "blob"), ("entity-owner-changed", "entity")]:
            with self.subTest(mode=mode):
                storage = FixtureStorage(mode)
                with self.assertRaisesRegex(deployed.Failure, "fixture_cleanup_failed"):
                    self.run_fixture(storage)
                self.assertFalse(any(c[:2] == (protected, "delete") for c in storage.calls))

    def test_provider_failure_cleans_up_and_never_retries_send(self):
        storage = FixtureStorage()
        with self.assertRaisesRegex(deployed.Failure, "real_provider_send_failed"):
            self.run_fixture(storage, (500, 502))
        self.assertEqual(2, len([c for c in storage.calls if c[1] == "delete"]))

    def test_metrics_failure_cleans_up_without_another_send(self):
        storage = FixtureStorage()
        statuses = Mock(side_effect=[500, 201, 400])
        metrics = Mock()
        metrics.verify.side_effect = deployed.telemetry.Failure("test_telemetry_read_failed")
        with patch.object(deployed, "Storage", return_value=storage), patch.object(deployed.readonly, "verify"), patch.object(deployed, "post", statuses), patch.object(deployed.telemetry, "ProviderMetrics", return_value=metrics):
            with self.assertRaisesRegex(deployed.telemetry.Failure, "^test_telemetry_read_failed$"):
                deployed.run(ENV, Mock())
        self.assertEqual(3, statuses.call_count)
        self.assertEqual(2, len([c for c in storage.calls if c[1] == "delete"]))
        metrics.verify.assert_called_once()

    def test_metrics_destination_failure_precedes_fixture_writes_and_sends(self):
        with patch.object(deployed.readonly, "verify"), patch.object(deployed, "Storage") as storage, patch.object(deployed, "post") as post, patch.object(deployed.telemetry, "ProviderMetrics", side_effect=deployed.telemetry.Failure("test_telemetry_destination_mismatch")):
            with self.assertRaises(deployed.telemetry.Failure):
                deployed.run(ENV, Mock())
        storage.assert_not_called()
        post.assert_not_called()

    def test_missing_blob_success_does_not_count_as_provider_evidence(self):
        storage = FixtureStorage()
        with self.assertRaisesRegex(deployed.Failure, "missing_blob_not_rejected"):
            self.run_fixture(storage, (201,))
        self.assertFalse(any(c[:2] == ("blob", "upload") for c in storage.calls))

    def test_side_effect_http_boundary_guards_host_and_never_retries(self):
        opener = Mock()
        with self.assertRaisesRegex(deployed.Failure, "test_host_required"):
            deployed.post("https://production.example.invalid", {}, opener)
        opener.open.assert_not_called()
        opener.open.side_effect = TimeoutError("secret response")
        with self.assertRaisesRegex(deployed.Failure, "^test_http_outcome_unknown_no_retry$"):
            deployed.post(HOST, {}, opener)
        self.assertEqual(1, opener.open.call_count)

    def test_side_effect_http_preserves_status_and_rejects_redirect(self):
        opener = Mock(open=Mock(return_value=response({}, HOST + "/ContactUs", 201)))
        self.assertEqual(201, deployed.post(HOST, {}, opener))
        opener.open.return_value = response({}, "https://elsewhere/ContactUs", 201)
        with self.assertRaisesRegex(deployed.Failure, "test_redirect_rejected"):
            deployed.post(HOST, {}, opener)
        opener.open.side_effect = HTTPError(HOST, 500, "sensitive body", {}, None)
        self.assertEqual(500, deployed.post(HOST, {}, opener))

    def test_storage_credentials_stay_out_of_arguments_and_failure_output(self):
        runner = Mock(return_value=subprocess.CompletedProcess([], 1, "secret", "secret"))
        storage = deployed.Storage(deployed.configuration(ENV), runner)
        with self.assertRaisesRegex(deployed.Failure, "^test_storage_operation_failed$"):
            storage.entity(TENANT, "ContactUs")
        call = runner.call_args
        self.assertNotIn(ENV["TEST_STORAGE_KEY"], call.args[0])
        self.assertEqual(ENV["TEST_STORAGE_KEY"], call.kwargs["env"]["AZURE_STORAGE_KEY"])
        self.assertTrue(call.kwargs["capture_output"])


class ResourceTests(unittest.TestCase):
    def test_credentials_are_test_only(self):
        for override in [{"AZURE_ENV_NAME": "prod"}, {"APP_SHORT_NAME": "other"}, {"GITHUB_SHA": "main"}]:
            with patch.dict(os.environ, AZURE_ENV_NAME="test", APP_SHORT_NAME="niobiumnotify", GITHUB_SHA=SHA), patch.object(resources, "azure") as azure:
                with patch.dict(os.environ, override), self.assertRaises(ValueError):
                    resources.main()
                azure.assert_not_called()

    def test_resource_lookup_masks_key_and_validates_arm_hostname(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "env"
            with patch.dict(os.environ, AZURE_ENV_NAME="test", APP_SHORT_NAME="niobiumnotify", GITHUB_SHA=SHA, AZURE_RESOURCE_GROUP="test-group", GITHUB_ENV=str(target)), patch.object(resources, "azure", side_effect=[HOST.removeprefix("https://"), "synthetic-key"]) as azure, contextlib.redirect_stdout(io.StringIO()) as output:
                resources.main()
            self.assertEqual("::add-mask::synthetic-key\n", output.getvalue())
            values = dict(line.split("=", 1) for line in target.read_text().splitlines())
            self.assertEqual(HOST, values["TEST_API_URL"])
            self.assertEqual(SHA, values["TEST_EXPECTED_COMMIT"])
            self.assertEqual("test", values["TEST_ENVIRONMENT"])
            self.assertTrue(all("create" not in c.args for c in azure.call_args_list))

    def test_wrong_arm_host_blocks_key_read(self):
        with patch.dict(os.environ, AZURE_ENV_NAME="test", APP_SHORT_NAME="niobiumnotify", GITHUB_SHA=SHA, AZURE_RESOURCE_GROUP="test-group"), patch.object(resources, "azure", return_value="niobiumnotify-func-prod.synthetic.azurewebsites.net") as azure:
            with self.assertRaisesRegex(ValueError, "Unexpected test Function App hostname"):
                resources.main()
            self.assertEqual(1, azure.call_count)

    def test_cli_failure_suppresses_provider_output(self):
        with patch.object(resources.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "secret", "secret")):
            with self.assertRaisesRegex(RuntimeError, "^Test resource metadata/credential lookup failed; Azure output suppressed$"):
                resources.azure("resource", "show")

    def test_environment_export_rejects_injection(self):
        for value in ["", "value\nOTHER=secret", "value\rOTHER=secret"]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                resources.export("TEST_STORAGE_KEY", value, secret=True)


if __name__ == "__main__":
    unittest.main()
