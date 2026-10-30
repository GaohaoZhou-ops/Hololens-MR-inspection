import copy
import http.client
import json
import tempfile
import threading
import time
import unittest
import uuid
from test_server import receiver, TOKEN


def batch(count=3):
    return {"session": {"sessionId": uuid.uuid4().hex, "startedAt": "2026-10-30T10:00:00Z",
                        "deviceName": "Pose test", "clientKind": "editor"},
            "samples": [{"sequence": i, "elapsed": float(i), "sampledAt": f"2026-10-30T10:00:{i:02d}Z",
                         "tracked": True, "trackingState": "Editor", "segment": 0,
                         "x": i * .5, "y": i * .1, "z": -i * .2,
                         "qx": 0., "qy": 0., "qz": 0., "qw": 1.} for i in range(count)]}


class PoseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.server = receiver.CaptureServer(("127.0.0.1", 0), receiver.CaptureStore(self.directory.name), TOKEN, 1)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join(); self.directory.cleanup()

    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=4)
        defaults = {"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"}
        defaults.update(headers or {})
        conn.request(method, path, json.dumps(body).encode() if body is not None else None, headers=defaults)
        response = conn.getresponse(); result = response.status, json.loads(response.read()); conn.close()
        return result

    def test_positions_orientation_tracking_gap_and_no_inference_delay(self):
        data = batch(); data["samples"][1].update(tracked=False, trackingState="Unavailable")
        start = time.monotonic()
        status, ack = self.request("POST", "/api/poses", data)
        self.assertEqual(status, 200); self.assertLess(time.monotonic()-start, .8)
        self.assertEqual(ack["acceptedThrough"], 2)
        _, result = self.request("GET", "/api/pose-sessions/"+data["session"]["sessionId"])
        self.assertEqual(result["samples"][2]["x"], 1)
        self.assertEqual(result["samples"][2]["qw"], 1)
        self.assertIsNone(result["samples"][1]["x"])

    def test_duplicate_idempotency_conflict_and_atomic_rollback(self):
        data = batch(); self.assertEqual(self.request("POST", "/api/poses", data)[0], 200)
        self.assertEqual(self.request("POST", "/api/poses", data)[1]["inserted"], 0)
        changed = copy.deepcopy(data); changed["samples"][1]["x"] = 99
        self.assertEqual(self.request("POST", "/api/poses", changed)[0], 409)
        changed = copy.deepcopy(data); changed["session"]["deviceName"] = "another device"
        self.assertEqual(self.request("POST", "/api/poses", changed)[0], 409)
        _, sessions = self.request("GET", "/api/pose-sessions")
        self.assertEqual(sessions["sessions"][0]["sampleCount"], 3)

    def test_validation_auth_and_local_viewer(self):
        data = batch()
        self.assertEqual(self.request("POST", "/api/poses", data, {"Authorization": "wrong"})[0], 401)
        for key, value in [("x", float('nan')), ("qw", 0), ("sequence", True), ("elapsed", -1), ("trackingState", "bad")]:
            bad = copy.deepcopy(data); bad["samples"][0][key] = value
            self.assertEqual(self.request("POST", "/api/poses", bad)[0], 400, key)
        self.assertEqual(self.request("POST", "/api/poses", {"session":data["session"], "samples":[]})[0], 400)
        self.assertEqual(self.request("GET", "/api/pose-sessions", headers={"Host":"192.168.0.113:8765"})[0], 403)

    def test_sessions_pagination_persistence_and_photo_clear_scope(self):
        first, second = batch(5), batch(2)
        self.request("POST", "/api/poses", first); self.request("POST", "/api/poses", second)
        path = "/api/pose-sessions/"+first["session"]["sessionId"]
        _, page = self.request("GET", path+"?limit=2")
        self.assertTrue(page["hasMore"]); self.assertEqual(page["nextAfter"], 1)
        _, page = self.request("GET", path+"?after=1&limit=2")
        self.assertEqual([p["sequence"] for p in page["samples"]], [2,3])
        self.server.poses = receiver.PoseStore(self.directory.name)
        self.assertEqual(len(self.request("GET", path)[1]["samples"]), 5)
        self.request("DELETE", "/api/captures", headers={"X-Demo-Action":"clear-history"})
        self.assertEqual(len(self.request("GET", "/api/pose-sessions")[1]["sessions"]), 2)
        self.assertEqual(self.request("GET", path+"?after=-2")[0], 400)
        self.assertEqual(self.request("GET", "/api/pose-sessions/"+uuid.uuid4().hex)[0], 404)


if __name__ == '__main__':
    unittest.main()
