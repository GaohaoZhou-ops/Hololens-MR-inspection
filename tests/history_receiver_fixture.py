"""Isolated receiver for Unity work-resume and browser cache controls."""
import json
from pathlib import Path
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "server"))
from server import CaptureStore, CaptureServer, CaptureHandler

CONTROL = ROOT / "logs/history-smoke-control.json"
EVENTS = ROOT / "logs/history-smoke-events.jsonl"
EVENTS.write_text("")
LOCK = threading.Lock()


def mode():
    try:
        return json.loads(CONTROL.read_text(encoding="utf-8-sig"))["mode"]
    except (OSError, ValueError):
        return "online"


class Handler(CaptureHandler):
    def do_GET(self):
        if self.path == "/health" and mode() == "offline-data":
            self.send_json(503, {"error": "test_data_offline"})
            return
        if self.path.startswith("/api/device-anchor/"):
            with LOCK, EVENTS.open("a") as output:
                output.write(json.dumps({"kind": "download", "captureId": self.path.rsplit("/", 1)[1]}) + "\n")
        super().do_GET()

    def do_POST(self):
        if self.path in ("/api/captures", "/api/poses") and mode() == "offline-data":
            self.reject_upload(503, {"error": "test_data_offline"})
            return
        super().do_POST()


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="work-history-test-", dir=ROOT.parent / "HoloLens1Environment/cache/temp") as directory:
        server = CaptureServer(("127.0.0.1", 8769), CaptureStore(directory), "history-test-token-not-production", 0.1)
        server.RequestHandlerClass = Handler

        def stop():
            while mode() != "stop":
                time.sleep(.1)
            server.shutdown()

        threading.Thread(target=stop, daemon=True).start()
        print("HISTORY_FIXTURE_READY", flush=True)
        try:
            server.serve_forever()
        finally:
            server.server_close()
