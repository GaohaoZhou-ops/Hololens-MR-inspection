"""Exercise the standalone launch/configuration workflow without Unity or shared tools."""
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
import uuid

from test_server import JPEG


PROJECT = Path(__file__).resolve().parents[1]


class PortableServerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "portable demo"
        self.root.mkdir()
        self.cwd = Path(self.temporary.name) / "another working directory"
        self.cwd.mkdir()
        # Deliberately omit Assets, Unity, the original config/data, and HoloLens1Environment.
        shutil.copytree(PROJECT / "server", self.root / "server",
                        ignore=shutil.ignore_patterns("data", "__pycache__"))
        for name in ("RunServer.cmd", "RunServer.sh"):
            shutil.copyfile(PROJECT / name, self.root / name)
        self.config_path = self.root / "config" / "server.local.json"
        self.environment = dict(os.environ, HOLOLENS_PYTHON=sys.executable, PYTHONUTF8="1")
        self.process = None
        self.output = None

    def tearDown(self):
        if self.process is not None:
            if self.process.poll() is None:
                if os.name == "nt":
                    self.process.terminate()
                else:
                    self.process.send_signal(signal.SIGINT)
                try:
                    self.process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=5)
            self.process = None
        if self.output is not None:
            self.output.close()
        self.temporary.cleanup()

    def launch_script(self, *arguments):
        if os.name == "nt":
            # cmd.exe /s requires an outer quote pair around the entire batch command.
            command = subprocess.list2cmdline([str(self.root / "RunServer.cmd"), *arguments])
            launcher = subprocess.list2cmdline([os.environ.get("COMSPEC", "cmd.exe")])
            command = f'{launcher} /d /s /c "{command}"'
        else:
            command = ["sh", str(self.root / "RunServer.sh"), *arguments]
        return subprocess.run(command, cwd=self.cwd, env=self.environment, capture_output=True,
                              text=True, encoding="utf-8", timeout=15)

    def test_platform_launcher_creates_config_once_without_printing_or_rotating_token(self):
        result = self.launch_script("--init-config")
        self.assertEqual(result.returncode, 0, result.stderr)
        original = self.config_path.read_bytes()
        config = json.loads(original)
        self.assertGreaterEqual(len(config["apiToken"]), 24)
        self.assertNotIn(config["apiToken"], result.stdout + result.stderr)
        self.assertFalse((self.root / "server" / "data").exists())
        if os.name != "nt":
            self.assertEqual(self.config_path.stat().st_mode & 0o077, 0)
        again = self.launch_script("--init-config")
        self.assertNotEqual(again.returncode, 0)
        self.assertIn("kept unchanged", again.stderr)
        self.assertEqual(self.config_path.read_bytes(), original)

    def test_missing_config_and_example_token_fail_without_creating_data(self):
        missing = self.launch_script()
        self.assertNotEqual(missing.returncode, 0)
        self.assertIn("--init-config", missing.stderr)
        self.config_path.parent.mkdir()
        shutil.copyfile(PROJECT / "config" / "server.example.json", self.config_path)
        placeholder = self.launch_script()
        self.assertNotEqual(placeholder.returncode, 0)
        self.assertIn("apiToken must match", placeholder.stderr)
        self.assertFalse((self.root / "server" / "data").exists())

    def test_standalone_receiver_uses_config_relative_storage_and_serves_web_photo_and_pose(self):
        # Exercise --config with spaces and a BOM produced by Windows editors.
        configured = self.launch_script("--config", str(self.config_path), "--init-config")
        self.assertEqual(configured.returncode, 0, configured.stderr)
        config = json.loads(self.config_path.read_text(encoding="utf-8"))
        config.update(host="127.0.0.1", port=0, delaySeconds=0)
        self.config_path.write_text(json.dumps(config), encoding="utf-8-sig")
        log_path = self.cwd / "receiver.log"
        self.output = log_path.open("wb")
        self.process = subprocess.Popen(
            [sys.executable, "-u", str(self.root / "server" / "server.py"),
             "--config", str(self.config_path)], cwd=self.cwd, env=self.environment,
            stdout=self.output, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 10
        port = None
        while time.monotonic() < deadline:
            output = log_path.read_text(encoding="utf-8")
            match = re.search(r"Browser monitor: http://127\.0\.0\.1:(\d+)/", output)
            if match:
                port = int(match[1])
                break
            if self.process.poll() is not None:
                self.fail(output)
            time.sleep(.05)
        self.assertIsNotNone(port, log_path.read_text(encoding="utf-8"))

        def request(method, path, body=None, headers=None):
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
            try:
                connection.request(method, path, body=body, headers=headers or {})
                response = connection.getresponse()
                return response.status, response.read()
            finally:
                connection.close()

        for path in ("/", "/app.js", "/trajectory.js", "/style.css", "/favicon.svg"):
            status, body = request("GET", path)
            self.assertEqual(status, 200, path)
            self.assertNotIn(config["apiToken"].encode(), body)
        headers = {"Content-Type": "image/jpeg", "X-Capture-Id": uuid.uuid4().hex,
                   "Authorization": "Bearer " + config["apiToken"]}
        self.assertEqual(request("POST", "/api/captures", JPEG, {**headers, "Authorization": "wrong"})[0], 401)
        status, body = request("POST", "/api/captures", JPEG, headers)
        self.assertEqual(status, 200)
        result = json.loads(body)
        self.assertEqual(result["serial"], "HL1-000001")
        self.assertEqual((result["centerX"], result["centerY"]), (.5, .5))
        self.assertEqual(request("GET", f"/api/captures/{result['captureId']}/image"), (200, JPEG))
        from test_poses import batch
        poses = batch(1)
        status, body = request("POST", "/api/poses", json.dumps(poses).encode(),
                               {**headers, "Content-Type": "application/json"})
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body)["acceptedThrough"], 0)
        self.assertEqual(request("GET", "/api/pose-sessions")[0], 200)
        directory = self.root / "server" / "data"
        self.assertTrue((directory / "captures.sqlite3").exists())
        self.assertTrue((directory / "poses.sqlite3").exists())
        self.assertEqual(next((directory / "images").glob("*.jpg")).read_bytes(), JPEG)
        self.assertFalse((self.cwd / "server").exists())
        self.assertNotIn(config["apiToken"], log_path.read_text(encoding="utf-8"))
