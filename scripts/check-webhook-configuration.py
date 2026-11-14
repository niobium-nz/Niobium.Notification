"""Fail before provisioning if the required webhook secret bundle is missing or malformed."""
import base64
import binascii
import json
import os
import sys

SETTING = "APPSETTING_KV_RESENDWEBHOOKOPTIONS__SIGNINGSECRETSJSON"


def valid_configuration(environ):
    try:
        values = json.loads(environ.get(SETTING, ""))
        if not isinstance(values, list) or not 1 <= len(values) <= 4:
            return False
        return all(isinstance(value, str) and value.startswith("whsec_")
                   and len(base64.b64decode(value[6:], validate=True)) >= 16 for value in values)
    except (ValueError, TypeError, binascii.Error):
        return False


def main(environ):
    if valid_configuration(environ):
        return 0
    # Fixed diagnostic only: never print environment values or decoder exceptions.
    print("provider_webhook_signing_configuration_required", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main(os.environ))
