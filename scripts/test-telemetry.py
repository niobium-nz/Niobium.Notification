"""Read current test deployment metrics through the existing workflow identity."""
import json
import re
import subprocess
import time
from urllib.parse import quote, urlsplit
import uuid


class Failure(RuntimeError):
    def __init__(self, message, retryable=False):
        super().__init__(message)
        self.retryable = retryable


class ProviderMetrics:
    def __init__(self, environ, runner=None, clock=time.monotonic, sleeper=time.sleep, budget=None):
        self.runner = runner or subprocess.run
        self.clock = clock
        self.sleeper = sleeper
        self.budget = budget
        self.group = environ.get("AZURE_RESOURCE_GROUP", "")
        build = environ.get("buildVersion", "")
        base = environ.get("TEST_API_URL", "")
        if (environ.get("AZURE_ENV_NAME") != "test" or environ.get("TEST_ENVIRONMENT") != "test"
                or not self.group or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", build)
                or not re.fullmatch(r"https://niobiumnotify-func-test(?:\.[a-z0-9-]+)*\.azurewebsites\.net", base)):
            raise Failure("test_telemetry_configuration_required")
        self.version = build + "-prerelease"
        self.service = "Niobium.Notification.Server.Test"
        app = "niobiumnotify-func-test"
        host = self.azure("resource", "show", "--resource-group", self.group, "--name", app,
                          "--resource-type", "Microsoft.Web/sites", "--api-version", "2024-11-01",
                          "--query", "properties.defaultHostName")
        if host != urlsplit(base).hostname:
            raise Failure("test_telemetry_host_mismatch")

        ids = self.azure("resource", "list", "--resource-group", self.group,
                         "--resource-type", "Microsoft.Insights/components", "--query", "[].id").splitlines()
        if len(ids) != 1 or not ids[0].lower().endswith("test"):
            raise Failure("single_test_telemetry_component_required")
        component = ids[0]
        self.application = self.guid(self.azure("resource", "show", "--ids", component,
                                                "--api-version", "2020-02-02", "--query", "properties.AppId"))
        expected_key = self.guid(self.azure("resource", "show", "--ids", component,
                                           "--api-version", "2020-02-02", "--query", "properties.InstrumentationKey"))

        def setting(name):
            return self.azure("functionapp", "config", "appsettings", "list", "--resource-group", self.group,
                              "--name", app, "--query", "[?name=='" + name + "'].value | [0]")

        connection = setting("APPLICATIONINSIGHTS_CONNECTION_STRING")
        keys = [part.split("=", 1)[1].strip() for part in connection.split(";")
                if "=" in part and part.split("=", 1)[0].strip().lower() == "instrumentationkey"]
        if len(keys) != 1 or self.guid(keys[0]) != expected_key:
            raise Failure("test_telemetry_destination_mismatch")
        configured_version = setting("SERVICE_VERSION")
        configured_service = setting("SERVICE_NAME")
        if configured_version != self.version or configured_service != self.service:
            raise Failure("test_telemetry_service_identity_mismatch")

    @staticmethod
    def guid(value):
        try:
            parsed = uuid.UUID(value)
            if parsed.int:
                return str(parsed)
        except (ValueError, AttributeError):
            pass
        raise Failure("test_telemetry_identifier_invalid")

    def azure(self, *args, timeout=60):
        if self.budget:
            timeout = self.budget.timeout(timeout)
        query = args[0] == "rest"
        try:
            result = self.runner(["az", *args, "--only-show-errors", "--output", "tsv"],
                                 text=True, capture_output=True, check=False, timeout=timeout)
        except (OSError, subprocess.TimeoutExpired):
            raise Failure("test_telemetry_read_failed", retryable=query) from None
        if result.returncode:
            # Only allowlisted error classes leave this process. Never include raw
            # CLI output, request URLs, tokens, query responses or application data.
            detail = (result.stderr or "").lower()
            codes = ("insufficientaccesserror", "authorizationfailed", "forbidden", "unauthorized",
                     "semanticerror", "badargumenterror", "pathnotfounderror", "throttled", "toomanyrequests",
                     "serviceunavailable", "gatewaytimeout", "internalservererror")
            code = next((code for code in codes if code in detail), None)
            missing_table = code == "semanticerror" and re.search(
                r"failed to resolve [^\r\n]{0,80} named ['\"]custommetrics['\"]", detail)
            transient = code in ("throttled", "toomanyrequests", "serviceunavailable", "gatewaytimeout", "internalservererror")
            message = "test_telemetry_read_failed" + ("_" + code if query and code else "")
            raise Failure(message, retryable=query and (missing_table or transient))
        return result.stdout.strip()

    def verify(self, started):
        # This is aggregate evidence for the current deployment/window, not a receipt
        # attributed to one email. Never retry the provider submission while polling.
        query = f'''customMetrics
| where timestamp >= datetime({started.isoformat()})
| where application_Version == {json.dumps(self.version)} and cloud_RoleName == {json.dumps(self.service)}
| where name in ("niobium.notification.provider.attempts", "niobium.notification.provider.duration")
| extend mode = tostring(customDimensions.mode), outcome = tostring(customDimensions.outcome), total = coalesce(valueSum, value), samples = coalesce(valueCount, 1)
| where mode == "legacy" and outcome == "accepted" and samples > 0 and total >= 0
| where name == "niobium.notification.provider.duration" or total >= 1
| summarize by name
| count'''
        url = "https://api.applicationinsights.io/v1/apps/" + self.application + "/query?query=" + quote(query, safe="")
        deadline = self.clock() + 360
        last_failure = None
        while (remaining := deadline - self.clock()) > 0:
            try:
                observed = self.azure("rest", "--method", "get", "--resource", "https://api.applicationinsights.io",
                                      "--url", url, "--query", "tables[0].rows[0][0]", timeout=min(60, remaining))
                last_failure = None
            except Failure as error:
                if not error.retryable:
                    raise
                last_failure = error
                self.sleep(max(0, min(10, deadline - self.clock())))
                continue
            if observed not in ("0", "1", "2"):
                raise Failure("test_telemetry_count_invalid")
            if observed == "2":
                return
            self.sleep(max(0, min(10, deadline - self.clock())))
        if last_failure:
            raise Failure(str(last_failure) + "_after_six_minutes")
        raise Failure("test_provider_metrics_not_exported_within_six_minutes")

    def sleep(self, seconds):
        if self.budget:
            self.budget.sleep(seconds, self.sleeper)
        else:
            self.sleeper(seconds)
