"""Isolated PC receiver for the Unity OfflineSmoke stage; never uses production data."""
import json
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'server'))
from server import CaptureHandler, CaptureServer, CaptureStore

CONTROL = ROOT / 'logs/offline-smoke-control.json'
EVENTS = ROOT / 'logs/offline-smoke-events.jsonl'
EVENTS.write_text('')
LOCK = threading.Lock()


def mode():
    try:
        return json.loads(CONTROL.read_text())['mode']
    except (OSError, ValueError):
        return 'offline'


def event(kind, capture_id=None):
    with LOCK, EVENTS.open('a') as stream:
        stream.write(json.dumps({'kind': kind, 'time': time.monotonic(), 'captureId': capture_id}) + '\n')


class Handler(CaptureHandler):
    def do_GET(self):
        if self.path == '/health':
            event('health')
            if mode() == 'offline':
                self.send_json(503, {'error': 'test_offline'})
                return
        super().do_GET()

    def do_POST(self):
        event('upload', self.headers.get('X-Capture-Id'))
        super().do_POST()

    def send_bytes(self, status, payload, content_type, download=None):
        if self.command == 'POST' and status == 200 and mode() == 'online-drop-first' and not self.server.dropped:
            self.server.dropped = True
            self.close_connection = True
            event('response_dropped')
            return False
        return super().send_bytes(status, payload, content_type, download)


if __name__ == '__main__':
    import tempfile
    cache = ROOT.parent / 'HoloLens1Environment/cache/temp'
    with tempfile.TemporaryDirectory(prefix='offline-smoke-', dir=cache) as data:
        receiver = CaptureServer(('127.0.0.1', 8767), CaptureStore(data), 'offline-test-token-no-production-access', 0.15)
        receiver.RequestHandlerClass = Handler
        receiver.dropped = False
        def stop_when_requested():
            while mode() != 'stop':
                time.sleep(0.1)
            receiver.shutdown()
        threading.Thread(target=stop_when_requested, daemon=True).start()
        print('OFFLINE_FIXTURE_READY', flush=True)
        try:
            receiver.serve_forever()
        finally:
            receiver.server_close()
