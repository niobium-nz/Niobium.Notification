"""Exercise the generic deployment hooks with synthetic runner settings and no cloud calls."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


class AppSettingsTests(unittest.TestCase):
    def generate(self, path, settings):
        environment = {name: value for name, value in os.environ.items()
                       if not name.upper().startswith("APPSETTING_")}
        environment.update(settings)
        return subprocess.run(
            ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
             str(ROOT / "infra/scripts/SetAppSettings.ps1"), "-OutputPath", str(path)],
            env=environment, text=True, capture_output=True, check=True)

    def test_vault_settings_pass_only_names_while_plain_settings_keep_environment_references(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "main.bicepparam"
            original = "using 'main.bicep'\n"
            path.write_text(original)
            result = self.generate(path, {
                "APPSETTING_KV_STORAGETABLEOPTIONS__KEY": "synthetic-storage-secret",
                "APPSETTING_KV_ARBITRARY__SECRET": "synthetic-other-secret",
                "APPSETTING_SERVICE_NAME": "synthetic-service",
                "UNRELATED": "ignored",
            })
            generated = path.read_text()
            for name in ["KV_STORAGETABLEOPTIONS__KEY", "KV_ARBITRARY__SECRET"]:
                self.assertIn(f"name: '{name}'\n    value: ''", generated)
                self.assertNotIn(f"readEnvironmentVariable('APPSETTING_{name}')", generated)
            self.assertIn("value: readEnvironmentVariable('APPSETTING_SERVICE_NAME')", generated)
            self.assertNotIn("UNRELATED", generated)
            for value in ["synthetic-storage-secret", "synthetic-other-secret", "synthetic-service"]:
                self.assertNotIn(value, generated + result.stdout + result.stderr)
            self.assertEqual(Path(str(path) + ".backup").read_text(), original)

    def test_vault_prefixes_are_case_insensitive_throughout_parameter_generation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "main.bicepparam"
            path.write_text("using 'main.bicep'\n")
            settings = {
                "APPSETTING_kv_LOWER__SECRET": "synthetic-lowercase-secret",
                "AppSetting_kV_MIXED__SECRET": "synthetic-mixed-case-secret",
            }
            result = self.generate(path, settings)
            generated = path.read_text()
            for name, value in settings.items():
                setting_name = name[len("APPSETTING_"):]
                self.assertIn(f"name: '{setting_name}'\n    value: ''", generated)
                self.assertNotIn(f"readEnvironmentVariable('{name}')", generated)
                self.assertNotIn(value, generated + result.stdout + result.stderr)

    def test_regeneration_replaces_settings_and_keeps_original_backup(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "main.bicepparam"
            original = "using 'main.bicep'\n"
            path.write_text(original)
            self.generate(path, {"APPSETTING_KV_OLD__SECRET": "synthetic-old"})
            self.generate(path, {"APPSETTING_KV_NEW__SECRET": "synthetic-new"})
            generated = path.read_text()
            self.assertEqual(generated.count("param appSettings = ["), 1)
            self.assertNotIn("OLD__SECRET", generated)
            self.assertIn("name: 'KV_NEW__SECRET'\n    value: ''", generated)
            self.assertEqual(Path(str(path) + ".backup").read_text(), original)

    def test_clear_restores_original_parameter_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "main.bicepparam"
            original = "using 'main.bicep'\n"
            path.write_text(original)
            self.generate(path, {"APPSETTING_KV_SOME__SECRET": "synthetic-value"})
            subprocess.run(
                ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                 str(ROOT / "infra/scripts/ClearAppSettings.ps1"), "-OutputPath", str(path)],
                text=True, capture_output=True, check=True)
            self.assertEqual(path.read_text(), original)
            self.assertFalse(Path(str(path) + ".backup").exists())


if __name__ == "__main__":
    unittest.main()
