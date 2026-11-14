import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("webhook_config", Path(__file__).with_name("check-webhook-configuration.py"))
config = importlib.util.module_from_spec(spec)
spec.loader.exec_module(config)


class WebhookConfigurationTests(unittest.TestCase):
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
