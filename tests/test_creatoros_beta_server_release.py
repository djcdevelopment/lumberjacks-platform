from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VERIFIER = ROOT / "infra" / "gcp" / "p7" / "scripts" / "verify-creatoros-beta-server-release.py"


def load_verifier():
    spec = importlib.util.spec_from_file_location("verify_creatoros_beta_server", VERIFIER)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


class CreatorOsBetaServerReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.verifier = load_verifier()

    def fixture(self, root: Path) -> Path:
        release = root / "release"
        release.mkdir()
        uid = "-123456789"
        content_hash = "a" * 64
        composition_hash = "b" * 64
        files: list[tuple[str, str, bytes]] = [
            ("server/worlds_local/CreatorOSBeta1.db", "world_db", b"MZ-world-db"),
            ("server/worlds_local/CreatorOSBeta1.fwl", "world_fwl", b"world-fwl"),
            ("server/BepInEx/plugins/ComfyNetworkSense.dll", "server_networksense_dll", b"MZnetwork"),
            (
                "server/BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg",
                "networksense_server_config",
                (
                    "[Lumberjacks]\n"
                    "lumberjacksGatewayUrl = http://gateway:4000\n"
                    "lumberjacksCutoverMode = native\n"
                    "zdoAuthoritativeConsumerEnabled = false\n"
                    "lumberjacksMotionEnabled = false\n"
                    "[LumberjacksGameSession]\n"
                    "lumberjacksGameSessionEnabled = false\n"
                    "[Gameplay]\n"
                    "gameplayEventProducerEnabled = true\n"
                    "questEvaluatorEnabled = true\n"
                    "[Netcode]\n"
                    "zdoRedirectEnabled = false\n"
                    "handshakeResponderEnabled = true\n"
                    "handshakeResponderEndpoint = http://gateway:4000\n"
                    "handshakeResponderStrictMode = true\n"
                    "handshakeResponderWindowId = creatoros-beta1\n"
                    "handshakeResponderActiveSeconds = 0\n"
                ).encode(),
            ),
            (
                "server/BepInEx/config/comfy-network-sense/quest-view.json",
                "server_quest_view",
                json.dumps(
                    {
                        "release_lineage": {
                            "release_id": "creatoros-beta1",
                            "campaign_id": "slayers-signature-hunt",
                            "composition_hash": composition_hash,
                            "pack_content_hash": content_hash,
                        }
                    }
                ).encode(),
            ),
            ("server/creatoros/venue.json", "server_venue", b"{}\n"),
            (
                "server/creatoros/campaign.json",
                "server_campaign",
                json.dumps(
                    {
                        "campaign_id": "slayers-signature-hunt",
                        "composition_hash": composition_hash,
                        "pack": {"content_hash": content_hash},
                    }
                ).encode(),
            ),
        ]
        for relative, _, payload in files:
            path = release / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        db = release / "server/worlds_local/CreatorOSBeta1.db"
        fwl = release / "server/worlds_local/CreatorOSBeta1.fwl"
        pair_hash = self.verifier.named_pair_hash(db, fwl)
        platform = {
            "schema": "creatoros-beta-platform-manifest/v1",
            "release_id": "creatoros-beta1",
            "server_mode": "native-valheim",
            "world": {
                "name": "CreatorOSBeta1",
                "uid": uid,
                "pair_hash": pair_hash,
                "db_sha256": hashlib.sha256(db.read_bytes()).hexdigest(),
                "fwl_sha256": hashlib.sha256(fwl.read_bytes()).hexdigest(),
            },
            "plugins": {
                "comfy_network_sense_sha256": hashlib.sha256(b"MZnetwork").hexdigest()
            },
            "controls": {
                "lumberjacks_custom_transport": "off",
                "native_valheim_networking": "on",
                "enrollment_admission": "on-fail-closed",
                "eventlog": "on",
                "dedicated_personal_progression": "client-only-message-actions",
            },
        }
        files.append(
            (
                "server/platform-manifest.json",
                "platform_manifest",
                (json.dumps(platform, indent=2) + "\n").encode(),
            )
        )
        (release / "server/platform-manifest.json").write_bytes(files[-1][2])
        artifacts = []
        for relative, role, payload in files:
            artifacts.append(
                {
                    "path": relative,
                    "role": role,
                    "sha256": hashlib.sha256(payload).hexdigest(),
                    "bytes": len(payload),
                }
            )
        manifest = {
            "schema": "creatoros-beta-release/v1",
            "release_id": "creatoros-beta1",
            "release_state": "frozen",
            "world": {"name": "CreatorOSBeta1", "uid": uid, "pair_hash": pair_hash},
            "artifacts": artifacts,
        }
        (release / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        return release

    def test_exact_server_slice_is_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            result = self.verifier.verify(self.fixture(Path(temporary)))
        self.assertEqual("valid", result["status"])
        self.assertEqual(8, len(result["server_files"]))

    def test_world_tamper_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            release = self.fixture(Path(temporary))
            with (release / "server/worlds_local/CreatorOSBeta1.db").open("ab") as stream:
                stream.write(b"tamper")
            with self.assertRaisesRegex(self.verifier.VerificationError, "artifact hash/size mismatch"):
                self.verifier.verify(release)

    def test_fail_open_config_is_rejected_even_if_rehashed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            release = self.fixture(Path(temporary))
            config = release / "server/BepInEx/config/djcdevelopment.valheim.comfynetworksense.cfg"
            payload = config.read_bytes().replace(b"handshakeResponderStrictMode = true", b"handshakeResponderStrictMode = false")
            config.write_bytes(payload)
            manifest_path = release / "release-manifest.json"
            manifest = json.loads(manifest_path.read_text())
            row = next(item for item in manifest["artifacts"] if item["role"] == "networksense_server_config")
            row["sha256"] = hashlib.sha256(payload).hexdigest()
            row["bytes"] = len(payload)
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
            with self.assertRaisesRegex(self.verifier.VerificationError, "StrictMode"):
                self.verifier.verify(release)


class CreatorOsBetaDeploymentContractTests(unittest.TestCase):
    def setUp(self) -> None:
        scripts = ROOT / "infra" / "gcp" / "p7" / "scripts"
        self.driver = (scripts / "Install-CreatorOsBetaServer.ps1").read_text(encoding="utf-8")
        self.installer = (scripts / "install-creatoros-beta-server.sh").read_text(encoding="utf-8")
        self.stop_driver = (scripts / "Stop-P7Safely.ps1").read_text(encoding="utf-8")
        self.stop_script = (scripts / "stop-p7-stack.sh").read_text(encoding="utf-8")
        self.compose = (ROOT / "infra" / "gcp" / "p7" / "docker-compose.yml").read_text(
            encoding="utf-8"
        )

    def test_driver_hashes_and_uploads_the_exact_platform_controls(self) -> None:
        self.assertIn("comfy-p7-creatoros-deploy/v2", self.driver)
        self.assertIn("controls_archive_sha256", self.driver)
        self.assertIn("installer_sha256", self.driver)
        self.assertIn("controls/docker-compose.yml", self.driver)
        self.assertIn("controls/comfy-lumberjacks-p7.service", self.driver)
        self.assertIn("controls/scripts/stop-p7-stack.sh", self.driver)
        self.assertIn("$remoteArchive' '$remoteControls' '$remoteManifest'", self.driver)
        self.assertIn("[string]$receipt.controls_archive_sha256 -ne $controlsArchiveHash", self.driver)
        self.assertIn("[string]$receipt.installer_sha256 -ne $remoteInstallerHash", self.driver)

    def test_remote_installer_verifies_controls_before_installing_them(self) -> None:
        verify = self.installer.index("verify_control controls/docker-compose.yml")
        install = self.installer.index("install_atomic \"$controls_root/controls/docker-compose.yml\"")
        self.assertLess(verify, install)
        self.assertIn("platform-controls archive file set drifted", self.installer)
        self.assertIn("systemd-analyze verify \"$unit_target\"", self.installer)
        self.assertIn("uploaded remote installer hash mismatch", self.installer)

    def test_remote_installer_has_a_bounded_rollback_transaction(self) -> None:
        self.assertIn("trap rollback_on_error ERR", self.installer)
        self.assertIn("transaction_committed=true", self.installer)
        self.assertIn("comfy-p7-creatoros-rollback/v1", self.installer)
        self.assertIn("Rollback blocked: Valheim is still running", self.installer)
        stop = self.installer.index("systemctl stop \"$service_name\"")
        world_backup = self.installer.index(
            "backup_if_present \"$world_root/$name\" \"worlds_local/$name\""
        )
        self.assertLess(stop, world_backup)

    def test_activation_waits_for_service_and_world_readiness(self) -> None:
        self.assertIn("activation_deadline=$((SECONDS + 240))", self.installer)
        self.assertIn("wait_for_activation 'CreatorOSBeta1 world load'", self.installer)
        self.assertIn("wait_for_activation 'durable strict roster window'", self.installer)
        self.assertIn("wait_for_activation 'public TLS health'", self.installer)

    def test_vm_stop_requires_the_entire_stack_to_stop(self) -> None:
        self.assertIn("detail=remaining_stack_stop_failed", self.stop_script)
        self.assertIn('--argjson stack_stop_exit "$stack_stop_exit"', self.stop_script)
        self.assertIn("[int]$receipt.stack_stop_exit -ne 0", self.stop_driver)

    def test_vm_stop_receipt_pins_the_post_save_world_pair(self) -> None:
        self.assertIn('db_sha256="$(sha256sum "$db"', self.stop_script)
        self.assertIn('fwl_sha256="$(sha256sum "$fwl"', self.stop_script)
        self.assertIn('--arg db_sha256 "$db_sha256"', self.stop_script)
        self.assertIn("[string]$receipt.db_sha256 -notmatch '^[0-9a-f]{64}$'", self.stop_driver)
        self.assertIn("[string]$receipt.fwl_sha256 -notmatch '^[0-9a-f]{64}$'", self.stop_driver)

    def test_compose_reconciles_the_durable_p7_project(self) -> None:
        self.assertIn('name: "${P7_COMPOSE_PROJECT_NAME:-comfy-lumberjacks-p7}"', self.compose)
        self.assertIn(
            "set_environment P7_COMPOSE_PROJECT_NAME comfy-lumberjacks-p7", self.installer
        )

    def test_activation_arms_baked_release_compatibility(self) -> None:
        self.assertIn("set_environment LUMBERJACKS_STRICT_RELEASE_ENABLED true", self.installer)
        self.assertIn(".strict_release_enabled == true", self.installer)
        self.assertIn("[bool]$receipt.strict_release -ne $true", self.driver)

    def test_activation_injects_and_proves_private_telemetry_auth(self) -> None:
        self.assertIn("sed -n 's/^VALHEIM_TELEMETRY_KEY=//p'", self.installer)
        self.assertIn("chmod 0600 \"$telemetry_config_temp\"", self.installer)
        self.assertIn("telemetry_secret_injected:true", self.installer)
        self.assertIn(
            "set_environment COMFY_LUMBERJACKS_ENROLLMENT_MANIFEST_ID creatoros-beta1",
            self.installer,
        )
        self.assertIn(
            "wait_for_activation 'authenticated NetworkSense heartbeat'", self.installer
        )
        self.assertIn(".telemetry_heartbeat_ready=true", self.installer)
        self.assertIn("[bool]$receipt.telemetry_secret_injected -ne $true", self.driver)
        self.assertIn("[bool]$receipt.telemetry_heartbeat_ready -ne $true", self.driver)


if __name__ == "__main__":
    unittest.main()
