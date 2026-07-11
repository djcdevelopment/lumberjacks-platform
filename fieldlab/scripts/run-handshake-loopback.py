#!/usr/bin/env python3
"""P6/I5 handshake loopback driver (no game).

Drives a scripted Valheim client hello through the live Lumberjacks handshake responder
over HTTP, exercising Funnel 5's happy path AND the full ordered failure battery, then
reads /status so the MCP gate (valheim_handshake_gate) can return handshake_satisfied.

The emulated dedicated-server context is reconfigured between cases (as a real server's
ban list / player count / password would differ), so a single window ends up carrying one
accepted+steady-state connect plus reject rows for codes 3, 8 (blacklist), 8 (ticket), 9,
6, 7. Contract: fieldlab/NETCODE-HANDSHAKE-CONTRACT.md.

Usage: python run-handshake-loopback.py [--base http://127.0.0.1:4000] [--window i5-loopback]
Exit 0 = handshake_satisfied; 1 = anything else.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone

HOST = "steam_76561198000000000"
UID = 5_497_853_135_698


def _req(method, url, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8", "replace"))
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode("utf-8", "replace"))


def peerinfo(window, conn, **over):
    # The gateway serializes/deserializes snake_case JSON.
    body = {"window_id": window, "connection_id": conn, "uid": UID, "version": "0.221.12",
            "net_version": 36, "ref_pos": [9376, 105, 544], "player_name": "floooooobcakes",
            "host_name": HOST, "password_hash": "", "ticket_valid": True}
    body.update(over)
    return body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:4000")
    ap.add_argument("--window", default="i5-loopback")
    ap.add_argument("--evidence", default="")
    args = ap.parse_args()
    base = args.base.rstrip("/")
    w = args.window
    hs = base + "/valheim/handshake"
    log = []

    def step(label, method, path, body=None):
        status, payload = _req(method, hs + path, body)
        log.append({"step": label, "http": status, "response": payload})
        print(f"[{status}] {label}: {json.dumps(payload)[:160]}")
        return payload

    # Fresh window.
    step("reset", "POST", f"/reset/{w}")

    # 1. Happy path — no-password Steam-only server → accept + steady state.
    step("config:default", "POST", "/config", {"window_id": w, "context": {}})
    step("begin:happy", "POST", "/begin", {"window_id": w, "connection_id": "conn-happy"})
    step("peerinfo:happy", "POST", "/peerinfo", peerinfo(w, "conn-happy"))

    # 2. Wrong network version → ErrorVersion(3).
    step("peerinfo:bad-version", "POST", "/peerinfo", peerinfo(w, "c-ver", net_version=35))

    # 3. Blacklisted host → ErrorBanned(8).
    step("config:banned", "POST", "/config", {"window_id": w, "context": {"banned_hosts": [HOST]}})
    step("peerinfo:banned", "POST", "/peerinfo", peerinfo(w, "c-ban"))

    # 4. Bad Steam ticket → ErrorBanned(8) (same code, distinct check).
    step("config:default2", "POST", "/config", {"window_id": w, "context": {}})
    step("peerinfo:bad-ticket", "POST", "/peerinfo", peerinfo(w, "c-tkt", ticket_valid=False))

    # 5. Server full (>=10) → ErrorFull(9).
    step("config:full", "POST", "/config", {"window_id": w, "context": {"current_players": 10}})
    step("peerinfo:full", "POST", "/peerinfo", peerinfo(w, "c-full"))

    # 6. Wrong password → ErrorPassword(6).
    step("config:password", "POST", "/config",
         {"window_id": w, "context": {"password": "expected-hash", "salt": "salt16"}})
    step("peerinfo:bad-password", "POST", "/peerinfo", peerinfo(w, "c-pw", password_hash="wrong"))

    # 7. Duplicate uid already connected → ErrorAlreadyConnected(7).
    step("config:dup", "POST", "/config", {"window_id": w, "context": {"connected_uids": [UID]}})
    step("peerinfo:duplicate", "POST", "/peerinfo", peerinfo(w, "c-dup"))

    # Read the window trace + evaluate locally.
    status = step("status", "GET", f"/status/{w}")
    steady = int(status.get("steady_state_reached", 0))
    by_code = status.get("by_code", {}) or {}
    present = {str(k) for k in by_code}
    battery = {"3", "6", "7", "8", "9"}
    satisfied = steady >= 1 and battery.issubset(present)
    verdict = "handshake_satisfied" if satisfied else "NOT_satisfied"
    print(f"\nverdict={verdict} steady={steady} codes_present={sorted(present)} "
          f"missing={sorted(battery - present)}")

    if args.evidence:
        rec = {"utc": datetime.now(timezone.utc).isoformat(), "window": w, "base": base,
               "verdict": verdict, "steady_state": steady, "by_code": by_code,
               "battery_present": sorted(present), "log": log}
        with open(args.evidence, "w", encoding="utf-8") as f:
            json.dump(rec, f, indent=2)
        print(f"evidence -> {args.evidence}")

    return 0 if satisfied else 1


if __name__ == "__main__":
    sys.exit(main())
