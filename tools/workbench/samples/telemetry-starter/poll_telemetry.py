#!/usr/bin/env python3
"""Poll the Lumberjacks aggregates-only telemetry API (v0) and print a summary.

Standard library only. The API is aggregates-only by tested design: no player IDs,
names, or positions appear in any response.

Usage:
  python poll_telemetry.py https://<site>          # one pass over every endpoint
  python poll_telemetry.py https://<site> server   # just one endpoint
"""
import json
import sys
import urllib.error
import urllib.request

ENDPOINTS = ["server", "tick", "sessions", "delivery", "regions", "events", "valheim", "cutover"]


def fetch(base, name):
    url = f"{base.rstrip('/')}/api/v0/telemetry/{name}"
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        return json.load(resp)


def summarize(name, data, indent="  "):
    print(f"{name}:")
    if not isinstance(data, dict):
        print(f"{indent}{str(data)[:120]}")
        return
    for key, value in data.items():
        text = json.dumps(value, ensure_ascii=False)
        if len(text) > 100:
            text = text[:97] + "..."
        print(f"{indent}{key}: {text}")


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    base = sys.argv[1]
    wanted = sys.argv[2:] or ENDPOINTS
    failures = 0
    for name in wanted:
        try:
            data = fetch(base, name)
        except urllib.error.HTTPError as e:
            print(f"{name}: HTTP {e.code} ({e.reason})")
            failures += 1
            continue
        except Exception as e:  # noqa: BLE001 - starter script, report and continue
            print(f"{name}: unreachable ({e})")
            failures += 1
            continue
        summarize(name, data)
        print()
    if failures == len(wanted):
        raise SystemExit("every endpoint failed - wrong base URL, or the server is down")


if __name__ == "__main__":
    main()
