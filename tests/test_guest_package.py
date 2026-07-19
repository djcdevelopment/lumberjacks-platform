import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest


ROOT = Path(__file__).resolve().parents[1]
PS = "powershell"
GENERATOR = ROOT / "tools" / "guest-package" / "build-guest-package.ps1"
PREFLIGHT = ROOT / "tools" / "guest-package" / "Invoke-GuestPreflight.ps1"
INSTALLER = ROOT / "tools" / "guest-package" / "Install-ComfyGuest.ps1"
UNINSTALLER = ROOT / "tools" / "guest-package" / "Uninstall-ComfyGuest.ps1"
EXPECTED_DLL = "94a3843ef8042adceaca6bc4d5c0c38c7c8dc5a1aa05b5f2a3019879840ba3a8"


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    state = {"bootstrap_status": 200, "bootstrap_body": "lumberjacksGatewayUrl=http://127.0.0.1\nlumberjacksAuthoritativeWindowId=w\nlumberjacksEnrollmentId=e\nlumberjacksClientAccessKey=fixture-access\n", "health_status": 200}

    def do_GET(self):
        if self.path.startswith("/join/bootstrap"):
            status = self.state["bootstrap_status"]
            body = self.state["bootstrap_body"]
        else:
            status = self.state["health_status"]
            body = "ok\n"
        self.send_response(status)
        self.end_headers()
        self.wfile.write(body.encode("utf-8"))

    def log_message(self, *_args):
        pass


class GuestPackageTests(unittest.TestCase):
    def setUp(self):
        FixtureHandler.state = {"bootstrap_status": 200, "bootstrap_body": "lumberjacksGatewayUrl=http://127.0.0.1\nlumberjacksAuthoritativeWindowId=w\nlumberjacksEnrollmentId=e\nlumberjacksClientAccessKey=fixture-access\n", "health_status": 200}
        self.tmp = Path(tempfile.mkdtemp(prefix="comfy-guest-test-"))
        self.package = self.tmp / "package"
        self._ps(GENERATOR, "-OutputRoot", str(self.package))
        self.valheim = self.tmp / "Valheim"
        (self.valheim / "BepInEx" / "plugins").mkdir(parents=True)
        (self.valheim / "BepInEx" / "config").mkdir(parents=True)
        (self.valheim / "BepInEx" / "plugins" / "ThirdParty.dll").write_bytes(b"third-party")
        (self.valheim / "BepInEx" / "config" / "djcdevelopment.valheim.comfynetworksense.cfg").write_text(
            "[General]\nfoo=bar\n\n[Lumberjacks]\nold=value\n\n[Automation]\nsentinel=true\n", encoding="utf-8"
        )
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_port}"

    def tearDown(self):
        self.server.shutdown()
        self.thread.join(timeout=3)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _ps(self, script, *args):
        result = subprocess.run([PS, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), *map(str, args)], capture_output=True, text=True)
        if result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        return result

    def _preflight(self, *args):
        result = self._ps(PREFLIGHT, "-PackageRoot", self.package, "-ValheimPath", self.valheim, *args)
        return json.loads(result.stdout)

    def _bootstrap(self):
        return self.base + "/join/bootstrap"

    def test_generator_is_deterministic_and_dll_is_sealed_hash(self):
        second = self.tmp / "second"
        self._ps(GENERATOR, "-OutputRoot", second)
        self.assertEqual((self.package / "guest-index.json").read_bytes(), (second / "guest-index.json").read_bytes())
        import hashlib
        self.assertEqual(hashlib.sha256((self.package / "ComfyNetworkSense.dll").read_bytes()).hexdigest(), EXPECTED_DLL)
        self.assertFalse((self.package / "gateway" / "gateway.oci.tar").exists())
        self.assertFalse((self.package / "source").exists())

    def test_install_and_receipt_uninstall_restore_unrelated_files(self):
        config = self.valheim / "BepInEx" / "config" / "djcdevelopment.valheim.comfynetworksense.cfg"
        third = self.valheim / "BepInEx" / "plugins" / "ThirdParty.dll"
        before_config = config.read_bytes()
        before_third = third.read_bytes()
        self._ps(INSTALLER, "-BootstrapUrl", self._bootstrap(), "-ValheimPath", self.valheim, "-PackageRoot", self.package)
        text = config.read_text(encoding="utf-8")
        self.assertIn("zdoAuthoritativeConsumerEnabled=true", text)
        self.assertIn("[General]\nfoo=bar", text)
        self.assertIn("[Automation]\nsentinel=true", text)
        self.assertTrue((self.valheim / "BepInEx" / "comfy-guest-install.json").exists())
        self._ps(UNINSTALLER, "-ValheimPath", self.valheim)
        self.assertEqual(config.read_bytes(), before_config)
        self.assertEqual(third.read_bytes(), before_third)
        self.assertFalse((self.valheim / "BepInEx" / "comfy-guest-install.json").exists())
        self._ps(INSTALLER, "-BootstrapUrl", self._bootstrap(), "-ValheimPath", self.valheim, "-PackageRoot", self.package)
        with config.open("a", encoding="utf-8") as handle:
            handle.write("\n[General]\nuserEdited=true\n\n[Lumberjacks]\nuserOwnedKey=keep\n")
        self._ps(UNINSTALLER, "-ValheimPath", self.valheim)
        edited = config.read_text(encoding="utf-8")
        self.assertIn("userEdited=true", edited)
        self.assertIn("userOwnedKey=keep", edited)
        self.assertNotIn("lumberjacksGatewayUrl=", edited)

    def test_preflight_ready_fixture(self):
        data = json.loads((self.package / "guest-package-inputs.json").read_text(encoding="utf-8-sig"))
        data["gateway_base_url"] = self.base
        (self.package / "guest-package-inputs.json").write_text(json.dumps(data), encoding="utf-8")
        report = self._preflight("-NoBootstrap")
        self.assertEqual(report["verdict"], "READY", report)

    def test_guide_check_mutation_and_diagnostics_redaction(self):
        renderer = ROOT / "tools" / "render_guest_guide.py"
        ok = subprocess.run([r"C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe", str(renderer), "--manifest", str(self.package / "manifest.json"), "--inputs", str(self.package / "guest-package-inputs.json"), "--output", str(self.package / "GUEST-GUIDE.md"), "--check"], capture_output=True, text=True)
        self.assertEqual(ok.returncode, 0, ok.stderr)
        drift = subprocess.run([r"C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe", str(renderer), "--manifest", str(self.package / "manifest.json"), "--inputs", str(self.package / "guest-package-inputs.json"), "--output", str(self.package / "GUEST-GUIDE.md"), "--drift-scan"], capture_output=True, text=True)
        self.assertEqual(drift.returncode, 0, drift.stderr)
        scratch = self.tmp / "scratch-manifest.json"
        manifest = json.loads((self.package / "manifest.json").read_text(encoding="utf-8-sig"))
        manifest["mod"]["version"] = "0.5.99.0"
        scratch.write_text(json.dumps(manifest), encoding="utf-8")
        bad = subprocess.run([r"C:\Users\derek\AppData\Local\Programs\Python\Python312\python.exe", str(renderer), "--manifest", str(scratch), "--inputs", str(self.package / "guest-package-inputs.json"), "--output", str(self.package / "GUEST-GUIDE.md"), "--check"], capture_output=True, text=True)
        self.assertNotEqual(bad.returncode, 0)
        self._ps(INSTALLER, "-BootstrapUrl", self._bootstrap(), "-ValheimPath", self.valheim, "-PackageRoot", self.package)
        diagnostics = self.tmp / "diagnostics"
        self._ps(ROOT / "tools" / "guest-package" / "Collect-ComfyGuestDiagnostics.ps1", "-ValheimPath", self.valheim, "-OutputRoot", diagnostics)
        joined = "\n".join(p.read_text(encoding="utf-8") for p in diagnostics.rglob("*" ) if p.is_file())
        self.assertNotIn("fixture-access", joined)

    def test_fault_matrix_has_eight_distinct_non_ready_checks_with_remedies(self):
        cases = {}
        FixtureHandler.state["bootstrap_status"] = 200
        FixtureHandler.state["bootstrap_body"] = "lumberjacksGatewayUrl=http://127.0.0.1\nlumberjacksAuthoritativeWindowId=w\nlumberjacksEnrollmentId=e\nlumberjacksClientAccessKey=fixture-access\n"
        self.assertEqual(self._preflight("-NoBootstrap")["verdict"], "NOT_READY")  # production health is intentionally unreachable here
        original = (self.package / "ComfyNetworkSense.dll").read_bytes()
        (self.package / "ComfyNetworkSense.dll").write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
        cases["wrong DLL hash"] = self._preflight("-NoBootstrap")
        (self.package / "ComfyNetworkSense.dll").write_bytes(original)
        shutil.rmtree(self.valheim / "BepInEx")
        cases["missing BepInEx"] = self._preflight("-NoBootstrap")
        (self.valheim / "BepInEx").mkdir()
        (self.valheim / "BepInEx" / "config").write_text("not-a-directory", encoding="utf-8")
        cases["read-only config dir"] = self._preflight("-NoBootstrap")
        (self.valheim / "BepInEx" / "config").unlink()
        (self.valheim / "BepInEx" / "config").mkdir()
        fake = self.tmp / "valheim.exe"
        shutil.copy2(Path(os.environ.get("SystemRoot", "C:\\Windows")) / "System32" / "timeout.exe", fake)
        proc = subprocess.Popen([str(fake), "/t", "15"], creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        cases["game running"] = self._preflight("-NoBootstrap")
        proc.terminate()
        proc.wait(timeout=5)
        data = json.loads((self.package / "guest-package-inputs.json").read_text(encoding="utf-8"))
        data["gateway_base_url"] = "http://127.0.0.1:1"
        (self.package / "guest-package-inputs.json").write_text(json.dumps(data), encoding="utf-8")
        cases["unreachable Gateway"] = self._preflight("-NoBootstrap")
        data["gateway_base_url"] = "https://127.0.0.1:1"
        (self.package / "guest-package-inputs.json").write_text(json.dumps(data), encoding="utf-8")
        cases["failing TLS cert"] = self._preflight("-NoBootstrap")
        data["gateway_base_url"] = self.base
        (self.package / "guest-package-inputs.json").write_text(json.dumps(data), encoding="utf-8")
        FixtureHandler.state["bootstrap_body"] = "lumberjacksGatewayUrl=http://127.0.0.1\nlumberjacksAuthoritativeWindowId=w\nlumberjacksEnrollmentId=\nlumberjacksClientAccessKey=fixture-access\n"
        cases["empty enrollment id"] = self._preflight("-BootstrapUrl", self._bootstrap())
        FixtureHandler.state["bootstrap_status"] = 400
        cases["already-consumed bootstrap token"] = self._preflight("-BootstrapUrl", self._bootstrap())
        expected = {
            "wrong DLL hash": "dll_hash", "missing BepInEx": "bepinex_present", "read-only config dir": "config_writable",
            "game running": "valheim_not_running", "unreachable Gateway": "gateway_health", "failing TLS cert": "gateway_tls",
            "empty enrollment id": "enrollment_id", "already-consumed bootstrap token": "bootstrap_token",
        }
        for name, report in cases.items():
            failed = [c for c in report["checks"] if c["status"] == "FAIL"]
            self.assertTrue(failed, name)
            self.assertTrue(all(c["remedy"] for c in failed), name)
            self.assertIn(expected[name], [c["id"] for c in failed], name)
        self.assertEqual(len(cases), 8)


if __name__ == "__main__":
    unittest.main()
