"""Read existing test resource credentials; no creation, rotation or runtime settings writes."""
import json
import os
from pathlib import Path
import re
import subprocess


def azure(*args):
    result = subprocess.run(["az", *args, "--only-show-errors", "--output", "tsv"],
                            text=True, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError("Test resource metadata/credential lookup failed; Azure output suppressed")
    return result.stdout.strip()


def export(name, value, secret=False):
    if not value or "\n" in value or "\r" in value:
        raise ValueError("Missing or invalid " + name)
    if secret:
        print("::add-mask::" + value, flush=True)
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as stream:
        stream.write(name + "=" + value + "\n")


def main():
    if os.environ.get("AZURE_ENV_NAME") != "test":
        raise ValueError("Fixture credentials are allowed only in test")
    if os.environ.get("APP_SHORT_NAME") != "niobiumnotify":
        raise ValueError("Unexpected application")
    sha = os.environ.get("GITHUB_SHA", "")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Missing immutable feature commit")
    abbr = json.loads(Path("infra/abbreviations.json").read_text())
    function = "niobiumnotify-" + abbr["webSitesFunctions"] + "test"
    storage = ("niobiumnotify-" + abbr["storageStorageAccounts"] + "dtest").replace("-", "")
    bus = "niobiumnotify-" + abbr["serviceBusNamespaces"] + "test"
    group = os.environ["AZURE_RESOURCE_GROUP"]
    hostname = azure("resource", "show", "--resource-group", group, "--name", function,
                     "--resource-type", "Microsoft.Web/sites", "--api-version", "2024-11-01",
                     "--query", "properties.defaultHostName")
    if not hostname.startswith(function + ".") or not hostname.endswith(".azurewebsites.net"):
        raise ValueError("Unexpected test Function App hostname")
    export("TEST_ENVIRONMENT", "test")
    export("TEST_API_URL", "https://" + hostname)
    export("TEST_STORAGE_ACCOUNT", storage)
    export("TEST_STORAGE_KEY", azure("storage", "account", "keys", "list", "--resource-group", group,
                                    "--account-name", storage, "--query", "[0].value"), secret=True)
    export("TEST_EXPECTED_COMMIT", sha)
    export("TEST_SERVICEBUS_CONNECTION", azure("servicebus", "namespace", "authorization-rule", "keys", "list",
        "--resource-group", group, "--namespace-name", bus, "--name", "RootManageSharedAccessKey",
        "--query", "primaryConnectionString"), secret=True)


if __name__ == "__main__":
    main()
