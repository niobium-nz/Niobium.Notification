"""Run the storage suite against owned, disposable loopback Azurite processes.

Requires Linux, Azurite on PATH and a restored/built Release storage-test project.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
PROJECT = "src/Niobium.Notification.Storage.Tests/Niobium.Notification.Storage.Tests.csproj"


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def wait_ready(process, port):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("An owned Azurite process exited before readiness.")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.1)
    raise RuntimeError("An owned Azurite process did not become ready.")


def stop(process):
    # A finished group leader can leave a test host or build worker behind.
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        process.wait()
        return
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    finally:
        # Kill remaining members even when the leader has already exited.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=5)


def verify_report(directory):
    reports = list(directory.glob("*.trx"))
    if len(reports) != 1:
        raise RuntimeError("Expected one storage test report from this invocation.")
    counters = ET.parse(reports[0]).find(".//{*}Counters")
    if counters is None:
        raise RuntimeError("The storage test report has no result counts.")
    counts = {name: int(counters.get(name, "0")) for name in ("total", "executed", "passed", "failed", "notExecuted")}
    if counts["total"] < 1 or counts["passed"] != counts["total"] or counts["executed"] != counts["total"] or counts["failed"] or counts["notExecuted"]:
        raise RuntimeError("Every storage test must execute and pass; missing or skipped tests fail verification.")
    return counts


def run(results):
    binaries = {name: shutil.which(name) for name in ("azurite-table", "azurite-blob", "dotnet")}
    if not all(binaries.values()):
        raise RuntimeError("Install Azurite and .NET and put their executables on PATH.")
    results.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="run-", dir=results))
    processes = []
    logs = []
    env = os.environ.copy()
    try:
        for kind in ("table", "blob"):
            port = free_port()
            log = (directory / f"azurite-{kind}.log").open("w")
            logs.append(log)
            process = subprocess.Popen([
                binaries[f"azurite-{kind}"], "--silent", "--inMemoryPersistence",
                "--disableTelemetry", "--skipApiVersionCheck",
                f"--{kind}Host", "127.0.0.1", f"--{kind}Port", str(port),
            ], cwd=directory, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            processes.append(process)
            wait_ready(process, port)
            env[f"NIOBIUM_NOTIFICATION_TEST_{kind.upper()}_ENDPOINT"] = f"http://127.0.0.1:{port}/devstoreaccount1"

        process = subprocess.Popen([
            binaries["dotnet"], "test", "--project", PROJECT,
            "--configuration", "Release", "--no-build", "--no-restore",
            "--report-trx", "--results-directory", str(directory),
        ], cwd=ROOT, env=env, start_new_session=True)
        processes.append(process)
        result = process.wait(timeout=180)
        if result:
            return result if result > 0 else 128 - result
        counts = verify_report(directory)
        print(json.dumps({"storage_tests": counts, "results_directory": str(directory)}), flush=True)
        return 0
    finally:
        # Preserve cancellation while ensuring a second signal cannot interrupt cleanup.
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        cleanup_errors = []
        for process in reversed(processes):
            try:
                stop(process)
            except (OSError, subprocess.TimeoutExpired) as error:
                cleanup_errors.append(error)
        for log in logs:
            log.close()
        if cleanup_errors:
            raise RuntimeError("An owned storage-test process could not be stopped.") from cleanup_errors[0]


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-directory", type=Path, default=ROOT / "artifacts/storage")
    args = parser.parse_args()
    signal.signal(signal.SIGINT, interrupted)
    signal.signal(signal.SIGTERM, interrupted)
    try:
        raise SystemExit(run(args.results_directory.resolve()))
    except (OSError, RuntimeError, subprocess.TimeoutExpired, ET.ParseError, ValueError) as error:
        print(f"Storage integration failed: {error}", flush=True)
        raise SystemExit(1)
