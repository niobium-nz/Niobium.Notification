"""Production verification: same-origin GET /health only, with no fixture credentials."""
import argparse
import json
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class VerificationFailure(Exception):
    """Contains a fixed diagnostic code, never a response body or credential."""


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def origin(value):
    if not isinstance(value, str) or any(ord(char) <= 32 or ord(char) == 127 for char in value):
        raise VerificationFailure("invalid_service_origin")
    try:
        parsed = urlsplit(value)
        port = parsed.port
    except ValueError:
        raise VerificationFailure("invalid_service_origin") from None
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
            or port not in (None, 443) or parsed.path not in ("", "/")
            or parsed.query or parsed.fragment):
        raise VerificationFailure("invalid_service_origin")
    return value.rstrip("/")


class ReadOnlyClient:
    def __init__(self, base_url, opener=None):
        self.base_url = origin(base_url)
        self.opener = opener or build_opener(NoRedirect())

    def request(self, method="GET", path="/health"):
        if method != "GET" or path != "/health":
            raise VerificationFailure("read_only_route_rejected")
        request = Request(self.base_url + path, method="GET", headers={"Accept": "application/json"})
        try:
            with self.opener.open(request, timeout=20) as response:
                if response.status != 200 or response.geturl() != request.full_url:
                    raise VerificationFailure("health_response_rejected")
                data = response.read(4097)
                if len(data) > 4096:
                    raise VerificationFailure("health_response_too_large")
                return json.loads(data)
        except (HTTPError, URLError, TimeoutError, ValueError, OSError):
            raise VerificationFailure("health_request_failed") from None


def verify(base_url, expected_commit, attempts=30, delay=5, client=None, sleeper=time.sleep):
    if not re.fullmatch(r"[0-9a-f]{40}", expected_commit or ""):
        raise VerificationFailure("invalid_expected_commit")
    client = client or ReadOnlyClient(base_url)
    for attempt in range(attempts):
        try:
            payload = client.request()
            if isinstance(payload, dict) and payload.get("status") == "ready" and payload.get("commit") == expected_commit:
                return {"commit": expected_commit, "checks": [{"check": "deployed_commit", "passed": True}], "success": True}
        except VerificationFailure:
            pass
        if attempt + 1 < attempts:
            sleeper(delay)
    raise VerificationFailure("deployed_commit_not_ready")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    report = {"commit": os.environ.get("READONLY_EXPECTED_COMMIT"), "checks": [], "success": False}
    result = 1
    try:
        report = verify(os.environ.get("READONLY_BASE_URL", ""), os.environ.get("READONLY_EXPECTED_COMMIT", ""))
        print("PASS deployed_commit")
        result = 0
    except VerificationFailure as error:
        report["error"] = str(error)
        print("FAIL " + str(error))
    finally:
        target = Path(args.report)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    raise SystemExit(main())
