import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("webhook_config", Path(__file__).with_name("check-webhook-configuration.py"))
config = importlib.util.module_from_spec(spec)
spec.loader.exec_module(config)


class WebhookConfigurationTests(unittest.TestCase):
    def test_isolated_preflight_ignores_feature_local_modules_and_pythonpath(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            script = directory / "check-webhook-configuration.py"
            script.write_bytes(Path(config.__file__).read_bytes())
            for name in ("base64.py", "json.py", "sitecustomize.py"):
                (directory / name).write_text("raise RuntimeError('feature-local module executed')\n")
            valid = json.dumps(["whsec_plJ3nmyCDGBKInavdOK15jsl"])
            for value, expected in ((valid, 0), ("private-invalid-setting", 1)):
                with self.subTest(valid=expected == 0):
                    result = subprocess.run([sys.executable, "-I", str(script)], cwd=directory,
                        env={"PYTHONPATH": str(directory), config.SETTING: value},
                        capture_output=True, text=True, timeout=10)
                    self.assertEqual(expected, result.returncode)
                    self.assertEqual("", result.stdout)
                    self.assertEqual("" if expected == 0 else "provider_webhook_signing_configuration_required\n", result.stderr)

    def test_valid_key_rotation_and_multiple_accounts_use_one_opaque_secret(self):
        for count in range(1, 5):
            with self.subTest(count=count):
                value = json.dumps(["whsec_plJ3nmyCDGBKInavdOK15jsl"] * count)
                self.assertEqual(0, config.main({config.SETTING: value}))

    def test_missing_or_invalid_configuration_has_only_a_fixed_diagnostic(self):
        for value in ("", "private-not-json", "null", "{}", "[]", "[null]", "[1]",
                      '["whsec_invalid"]', '["whsec_YWJj"]', json.dumps(["whsec_plJ3nmyCDGBKInavdOK15jsl"] * 5)):
            with self.subTest(value=value), contextlib.redirect_stderr(io.StringIO()) as errors:
                self.assertEqual(1, config.main({config.SETTING: value}))
                self.assertEqual("provider_webhook_signing_configuration_required\n", errors.getvalue())
