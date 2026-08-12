#!/usr/bin/env python3
"""Fail closed around one exact NuGet.org publication and poll its availability."""

from __future__ import annotations

import argparse
import json
import time
import urllib.error
import urllib.request
from pathlib import Path


PACKAGE_ID = "Comfy.Transport.Contracts"
USER_AGENT = "lumberjacks-platform-publication-gate/1"


def index_url(package_id: str) -> str:
    return f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"


def package_url(package_id: str, version: str) -> str:
    lowered_id = package_id.lower()
    lowered_version = version.lower()
    return (
        "https://api.nuget.org/v3-flatcontainer/"
        f"{lowered_id}/{lowered_version}/{lowered_id}.{lowered_version}.nupkg"
    )


def request_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise RuntimeError(f"unexpected HTTP {response.status} from {url}")
        return response.read()


def public_versions(package_id: str) -> list[str]:
    try:
        payload = request_bytes(index_url(package_id))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return []
        raise RuntimeError(f"NuGet.org index returned HTTP {exc.code}") from exc
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RuntimeError("NuGet.org index did not return valid JSON") from exc
    versions = document.get("versions")
    if not isinstance(versions, list) or not all(isinstance(item, str) for item in versions):
        raise RuntimeError("NuGet.org index has no valid versions array")
    return versions


def assert_absent(package_id: str, version: str) -> None:
    versions = public_versions(package_id)
    if version.lower() in {item.lower() for item in versions}:
        raise RuntimeError(
            f"refusing to publish: NuGet.org already contains {package_id} {version}"
        )
    print(f"ABSENT id={package_id} version={version} checked_versions={len(versions)}")


def wait_for_package(
    package_id: str, version: str, output: Path, timeout: int
) -> None:
    url = package_url(package_id, version)
    deadline = time.monotonic() + timeout
    attempts = 0
    last_error = "not attempted"
    while time.monotonic() <= deadline:
        attempts += 1
        try:
            payload = request_bytes(url)
            if len(payload) < 512 or not payload.startswith(b"PK"):
                raise RuntimeError("downloaded package is not a plausible nupkg")
            output.parent.mkdir(parents=True, exist_ok=True)
            temporary = output.with_name(output.name + ".downloading")
            temporary.write_bytes(payload)
            temporary.replace(output)
            print(
                f"AVAILABLE id={package_id} version={version} "
                f"bytes={len(payload)} attempts={attempts}"
            )
            return
        except (OSError, RuntimeError, urllib.error.URLError) as exc:
            last_error = str(exc)
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            print(
                f"waiting for {package_id} {version}: attempt {attempts}: {last_error}",
                flush=True,
            )
            time.sleep(min(15, max(1, remaining)))
    raise RuntimeError(
        f"NuGet.org did not serve {package_id} {version} within {timeout}s; "
        f"last error: {last_error}"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    absent = subparsers.add_parser("assert-absent")
    absent.add_argument("--id", default=PACKAGE_ID, choices=(PACKAGE_ID,))
    absent.add_argument("--version", required=True)
    wait = subparsers.add_parser("wait")
    wait.add_argument("--id", default=PACKAGE_ID, choices=(PACKAGE_ID,))
    wait.add_argument("--version", required=True)
    wait.add_argument("--out", required=True, type=Path)
    wait.add_argument("--timeout", type=int, default=900)
    args = parser.parse_args()
    if args.command == "assert-absent":
        assert_absent(args.id, args.version)
    else:
        if args.timeout < 1 or args.timeout > 3600:
            parser.error("--timeout must be between 1 and 3600 seconds")
        wait_for_package(args.id, args.version, args.out, args.timeout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
