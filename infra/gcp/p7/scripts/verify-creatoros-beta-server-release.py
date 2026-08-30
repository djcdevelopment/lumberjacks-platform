#!/usr/bin/env python3
"""Independently verify the CreatorOS server slice accepted by P7."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path, PurePosixPath


SCHEMA = "creatoros-beta-release/v1"
REQUIRED_ROLES = {
    "world_db",
    "world_fwl",
    "platform_manifest",
    "server_networksense_dll",
    "networksense_server_config",
    "server_quest_view",
    "server_venue",
    "server_campaign",
}
SHA256 = re.compile(r"[0-9a-f]{64}")


class VerificationError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def named_pair_hash(db: Path, fwl: Path) -> str:
    digest = hashlib.sha256()
    for name, path in sorted((("CreatorOSBeta1.db", db), ("CreatorOSBeta1.fwl", fwl))):
        digest.update((name + "\n").encode("utf-8"))
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


def safe_path(value: object) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        raise VerificationError("artifact path must be a POSIX relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or "." in path.parts or ".." in path.parts:
        raise VerificationError(f"unsafe artifact path: {value}")
    return value


def load_json(path: Path, label: str) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise VerificationError(f"{label} is invalid UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise VerificationError(f"{label} must be a JSON object")
    return value


def config_value(text: str, section: str, key: str) -> str | None:
    section_match = re.search(
        rf"(?ms)^\[{re.escape(section)}\]\s*(.*?)(?=^\[|\Z)", text
    )
    if not section_match:
        return None
    value = re.search(rf"(?m)^{re.escape(key)}\s*=\s*(.*?)\s*$", section_match.group(1))
    return value.group(1) if value else None


def verify(release: Path, allow_candidate: bool = False) -> dict[str, object]:
    manifest_path = release / "release-manifest.json"
    manifest = load_json(manifest_path, "release manifest")
    if manifest.get("schema") != SCHEMA or manifest.get("release_id") != "creatoros-beta1":
        raise VerificationError("release identity drifted")
    state = manifest.get("release_state")
    if state != "frozen" and not (allow_candidate and state == "candidate-dirty"):
        raise VerificationError("P7 accepts only a frozen release unless candidate mode is explicit")
    world = manifest.get("world")
    if not isinstance(world, dict) or world.get("name") != "CreatorOSBeta1":
        raise VerificationError("world name must be CreatorOSBeta1")
    uid = str(world.get("uid", ""))
    if not re.fullmatch(r"-?[1-9][0-9]*", uid):
        raise VerificationError("world UID is invalid")

    rows = manifest.get("artifacts")
    if not isinstance(rows, list) or not rows:
        raise VerificationError("release artifacts are missing")
    roles: dict[str, list[dict]] = {}
    artifacts: dict[str, dict] = {}
    for row in rows:
        if not isinstance(row, dict):
            raise VerificationError("artifact row must be an object")
        relative = safe_path(row.get("path"))
        if relative in artifacts:
            raise VerificationError(f"duplicate artifact path: {relative}")
        path = release / Path(*PurePosixPath(relative).parts)
        if not path.is_file():
            raise VerificationError(f"artifact is missing: {relative}")
        actual_hash = sha256_file(path)
        if row.get("sha256") != actual_hash or row.get("bytes") != path.stat().st_size:
            raise VerificationError(f"artifact hash/size mismatch: {relative}")
        role = row.get("role")
        if not isinstance(role, str) or not role:
            raise VerificationError(f"artifact role missing: {relative}")
        artifacts[relative] = row
        roles.setdefault(role, []).append(row)
    for role in REQUIRED_ROLES:
        if len(roles.get(role, [])) != 1:
            raise VerificationError(f"server role must occur exactly once: {role}")

    server_rows = [row for row in rows if str(row["path"]).startswith("server/")]
    actual_server = {
        path.relative_to(release).as_posix()
        for path in (release / "server").rglob("*")
        if path.is_file()
    }
    if actual_server != {row["path"] for row in server_rows}:
        raise VerificationError("server directory file set disagrees with the release manifest")

    def role_path(role: str) -> Path:
        relative = roles[role][0]["path"]
        return release / Path(*PurePosixPath(relative).parts)

    platform = load_json(role_path("platform_manifest"), "platform manifest")
    if platform.get("schema") != "creatoros-beta-platform-manifest/v1":
        raise VerificationError("platform manifest schema drifted")
    if platform.get("release_id") != "creatoros-beta1" or platform.get("server_mode") != "native-valheim":
        raise VerificationError("platform release/server mode drifted")
    platform_world = platform.get("world", {})
    if platform_world.get("name") != "CreatorOSBeta1" or str(platform_world.get("uid")) != uid:
        raise VerificationError("platform world identity disagrees with the release")
    if platform_world.get("pair_hash") != world.get("pair_hash"):
        raise VerificationError("platform world-pair hash disagrees with the release")
    db = role_path("world_db")
    fwl = role_path("world_fwl")
    if PurePosixPath(roles["world_db"][0]["path"]).name != "CreatorOSBeta1.db" or PurePosixPath(roles["world_fwl"][0]["path"]).name != "CreatorOSBeta1.fwl":
        raise VerificationError("world files must use the CreatorOSBeta1 basename")
    if named_pair_hash(db, fwl) != world.get("pair_hash"):
        raise VerificationError("world-pair bytes drifted")
    if platform_world.get("db_sha256") != sha256_file(db) or platform_world.get("fwl_sha256") != sha256_file(fwl):
        raise VerificationError("platform world member hashes drifted")
    controls = platform.get("controls", {})
    expected_controls = {
        "lumberjacks_custom_transport": "off",
        "native_valheim_networking": "on",
        "enrollment_admission": "on-fail-closed",
        "eventlog": "on",
        "dedicated_personal_progression": "client-only-message-actions",
    }
    if controls != expected_controls:
        raise VerificationError("platform controls drifted")
    server_dll = role_path("server_networksense_dll")
    if not server_dll.read_bytes().startswith(b"MZ"):
        raise VerificationError("server NetworkSense artifact is not a Windows PE")
    if platform.get("plugins", {}).get("comfy_network_sense_sha256") != sha256_file(server_dll):
        raise VerificationError("platform NetworkSense hash drifted")

    config = role_path("networksense_server_config").read_text(encoding="utf-8")
    pins = {
        ("Lumberjacks", "lumberjacksGatewayUrl"): "http://gateway:4000",
        ("Lumberjacks", "lumberjacksCutoverMode"): "native",
        ("Lumberjacks", "zdoAuthoritativeConsumerEnabled"): "false",
        ("Lumberjacks", "lumberjacksMotionEnabled"): "false",
        ("LumberjacksGameSession", "lumberjacksGameSessionEnabled"): "false",
        ("Gameplay", "gameplayEventProducerEnabled"): "true",
        ("Gameplay", "questEvaluatorEnabled"): "true",
        ("Netcode", "zdoRedirectEnabled"): "false",
        ("Netcode", "handshakeResponderEnabled"): "true",
        ("Netcode", "handshakeResponderEndpoint"): "http://gateway:4000",
        ("Netcode", "handshakeResponderStrictMode"): "true",
        ("Netcode", "handshakeResponderWindowId"): "creatoros-beta1",
        ("Netcode", "handshakeResponderActiveSeconds"): "0",
    }
    for (section, key), expected in pins.items():
        if config_value(config, section, key) != expected:
            raise VerificationError(f"server config pin drifted: {section}.{key}")

    campaign = load_json(role_path("server_campaign"), "server campaign")
    view = load_json(role_path("server_quest_view"), "server quest view")
    content_hash = campaign.get("pack", {}).get("content_hash")
    if not isinstance(content_hash, str) or not SHA256.fullmatch(content_hash):
        raise VerificationError("campaign content hash is invalid")
    lineage = view.get("release_lineage", {})
    if (
        lineage.get("release_id") != "creatoros-beta1"
        or lineage.get("campaign_id") != campaign.get("campaign_id")
        or lineage.get("composition_hash") != campaign.get("composition_hash")
        or lineage.get("pack_content_hash") != content_hash
    ):
        raise VerificationError("server quest-view lineage drifted")

    return {
        "schema": "comfy-p7-creatoros-server-verification/v1",
        "status": "valid",
        "release_state": state,
        "release_manifest_sha256": sha256_file(manifest_path),
        "world_name": "CreatorOSBeta1",
        "world_uid": uid,
        "world_pair_hash": world.get("pair_hash"),
        "pack_content_hash": content_hash,
        "server_files": [
            {
                "path": row["path"].removeprefix("server/"),
                "sha256": row["sha256"],
                "bytes": row["bytes"],
            }
            for row in sorted(server_rows, key=lambda value: value["path"])
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--release-dir", type=Path, required=True)
    parser.add_argument("--allow-candidate", action="store_true")
    args = parser.parse_args()
    try:
        result = verify(args.release_dir.resolve(), args.allow_candidate)
    except (OSError, KeyError, ValueError, VerificationError) as exc:
        print(f"CreatorOS P7 server release invalid: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
