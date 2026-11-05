"""Verify secret-hook ordering and failure behavior with a process-local fake Azure CLI."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
RESOURCE_ID = "/subscriptions/synthetic/resourceGroups/synthetic-test/providers/Microsoft.Web/sites/synthetic-app"
REFRESH_URL = "https://management.azure.com" + RESOURCE_ID + "/config/configreferences/appsettings/refresh?api-version=2022-03-01"

# A PowerShell function takes precedence over the installed CLI on every platform. These
# tests cannot contact Azure, even if the machine running them has real CLI credentials.
FAKE_CLI = r"""
$ErrorActionPreference = 'Stop'
function global:az {
    Add-Content -LiteralPath $env:FAKE_AZ_LOG -Value (ConvertTo-Json -InputObject @{ arguments = @($args) } -Compress)
    $global:LASTEXITCODE = 0
    if (($args[0] -eq 'resource' -and $env:FAKE_AZ_FAILURE -eq 'lookup') -or
        ($args[0] -eq 'keyvault' -and $env:FAKE_AZ_FAILURE -eq 'write') -or
        ($args[0] -eq 'rest' -and $env:FAKE_AZ_FAILURE -eq 'refresh')) {
        $global:LASTEXITCODE = 1
        return
    }
    if ($args[0] -eq 'resource' -and $env:FAKE_AZ_FAILURE -ne 'empty') {
        Write-Output $env:FAKE_RESOURCE_ID
    }
}
& $env:HOOK_SCRIPT
"""


class KeyVaultHookTests(unittest.TestCase):
    def run_hook(self, settings, failure="", missing=()):
        with tempfile.TemporaryDirectory() as directory:
            wrapper = Path(directory) / "invoke.ps1"
            log = Path(directory) / "calls.jsonl"
            wrapper.write_text(FAKE_CLI)
            environment = {name: value for name, value in os.environ.items()
                           if not name.upper().startswith("APPSETTING_")}
            environment.update({
                "keyVaultName": "synthetic-vault",
                "functionAppName": "synthetic-app",
                "AZURE_RESOURCE_GROUP": "synthetic-test",
                "HOOK_SCRIPT": str(ROOT / "infra/scripts/SetKeyVaultSecrets.ps1"),
                "FAKE_AZ_LOG": str(log), "FAKE_AZ_FAILURE": failure,
                "FAKE_RESOURCE_ID": RESOURCE_ID,
            })
            environment.update(settings)
            for name in missing:
                environment.pop(name, None)
            result = subprocess.run(
                ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(wrapper)],
                env=environment, text=True, capture_output=True)
            calls = [json.loads(line)["arguments"] for line in log.read_text().splitlines()] if log.exists() else []
            for value in settings.values():
                self.assertNotIn(value, result.stdout + result.stderr)
            return result, calls

    def test_refresh_occurs_after_all_secret_writes_with_runner_values(self):
        result, calls = self.run_hook({
            "APPSETTING_KV_STORAGETABLEOPTIONS__KEY": "synthetic-storage-value",
            "AppSetting_kV_OTHER__SECRET": "synthetic-other-value",
            "APPSETTING_SERVICE_NAME": "synthetic-service",
        })
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls[0][:2], ["resource", "show"])
        writes = calls[1:-1]
        self.assertEqual(len(writes), 2)
        values = {args[args.index("--name") + 1].upper(): args[args.index("--value") + 1] for args in writes}
        self.assertEqual(values, {
            "STORAGETABLEOPTIONS--KEY": "synthetic-storage-value", "OTHER--SECRET": "synthetic-other-value"})
        for args in writes:
            self.assertEqual(args[:3], ["keyvault", "secret", "set"])
            self.assertEqual(args[args.index("--vault-name") + 1], "synthetic-vault")
            self.assertEqual(args[args.index("--output") + 1], "none")
        self.assertEqual(calls[-1], ["rest", "--method", "post", "--uri", REFRESH_URL, "--only-show-errors", "--output", "none"])

    def test_failed_secret_write_stops_before_reference_refresh(self):
        result, calls = self.run_hook({"APPSETTING_KV_SOME__SECRET": "synthetic-value"}, failure="write")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[-1][:3], ["keyvault", "secret", "set"])
        self.assertFalse(any(args[0] == "rest" for args in calls))

    def test_reference_refresh_failure_fails_the_hook(self):
        result, calls = self.run_hook({"APPSETTING_KV_SOME__SECRET": "synthetic-value"}, failure="refresh")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls[-1][0], "rest")
        self.assertIn("Failed to refresh Key Vault references", result.stderr)

    def test_missing_app_metadata_fails_before_any_cloud_call(self):
        for name in ["functionAppName", "AZURE_RESOURCE_GROUP"]:
            with self.subTest(name=name):
                result, calls = self.run_hook({"APPSETTING_KV_SOME__SECRET": "synthetic-value"}, missing=[name])
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_failed_or_empty_app_lookup_stops_before_secret_writes(self):
        for failure in ["lookup", "empty"]:
            with self.subTest(failure=failure):
                result, calls = self.run_hook({"APPSETTING_KV_SOME__SECRET": "synthetic-value"}, failure=failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(len(calls), 1)
                self.assertEqual(calls[0][:2], ["resource", "show"])

    def test_no_vault_settings_performs_no_cloud_calls(self):
        result, calls = self.run_hook({"APPSETTING_SERVICE_NAME": "synthetic-service"},
                                      missing=["functionAppName", "AZURE_RESOURCE_GROUP"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(calls, [])


if __name__ == "__main__":
    unittest.main()
