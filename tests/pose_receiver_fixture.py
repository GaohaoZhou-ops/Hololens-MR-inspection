"""Temporary receiver for Unity PoseSmoke and browser checks; never uses real photos."""
import json
import sys
import tempfile
import threading
import time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "server"))
from server import CaptureStore, CaptureServer, CaptureHandler
CONTROL = ROOT / "logs/pose-smoke-control.json"
EVENTS = ROOT / "logs/pose-smoke-events.jsonl"
EVENTS.write_text("")
LOCK = threading.Lock()


def mode():
    try:
        return json.loads(CONTROL.read_text(encoding="utf-8-sig"))["mode"]
    except (OSError, ValueError):
        return "offline"


def event(kind, **extra):
    with LOCK, EVENTS.open("a") as output:
        output.write(json.dumps({"kind":kind, "time":time.monotonic(), **extra})+"\n")


class Handler(CaptureHandler):
    def do_GET(self):
        if self.path == "/health":
            event("health")
            if mode() == "offline":
                self.send_json(503, {"error":"test_offline"}); return
        super().do_GET()

    def send_json(self, status, body):
        if self.command == "POST" and self.path == "/api/poses" and status == 200:
            event("pose_ack", **body)
            if mode() == "online-drop-first" and not self.server.dropped:
                self.server.dropped = True
                self.close_connection = True
                event("dropped_response")
                return False
        return super().send_json(status, body)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="pose-test-", dir=ROOT.parent / "HoloLens1Environment/cache/temp") as directory:
        server = CaptureServer(("127.0.0.1", 8768), CaptureStore(directory), "pose-test-token-not-production", 1)
        server.RequestHandlerClass = Handler
        server.dropped = False
        def stop():
            while mode() != "stop": time.sleep(.1)
            server.shutdown()
        threading.Thread(target=stop, daemon=True).start()
        print("POSE_FIXTURE_READY", flush=True)
        try: server.serve_forever()
        finally: server.server_close()
