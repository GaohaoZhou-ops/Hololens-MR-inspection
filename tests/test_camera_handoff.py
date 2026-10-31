import http.client
import json
import tempfile
import threading
import time
import unittest
import uuid
from test_server import receiver, TOKEN


class CameraHandoffTests(unittest.TestCase):
    def setUp(self):
        self.now = 10.
        self.control = receiver.LivePreviewCoordinator(lambda: self.now)
        self.ip = "192.168.100.61"
        self.preview = uuid.uuid4().hex
        self.lease = uuid.uuid4().hex

    def browser(self, action="poll", **extra):
        return self.control.browser({"previewId": self.preview, "deviceAddress": self.ip, "action": action, **extra})

    def camera(self, action="acquire", **extra):
        return self.control.camera({"leaseId": self.lease, "action": action, **extra}, self.ip)

    def test_camera_waits_for_pause_then_release_resumes(self):
        self.assertEqual(self.browser("register")["action"], "play")
        self.assertFalse(self.camera()["ready"])
        self.assertEqual(self.browser()["leaseId"], self.lease)
        self.assertFalse(self.camera()["ready"])
        self.browser(pausedLeaseId=self.lease)
        self.assertTrue(self.camera()["ready"])
        self.assertEqual(self.camera()["settleMs"], 750)
        self.camera("release")
        self.assertEqual(self.browser()["action"], "play")

    def test_every_viewer_must_pause_and_other_device_is_not_interrupted(self):
        self.browser("register")
        second = uuid.uuid4().hex
        other = uuid.uuid4().hex
        self.control.browser({"action":"register", "previewId":second, "deviceAddress":self.ip})
        self.control.browser({"action":"register", "previewId":other, "deviceAddress":"192.168.100.62"})
        self.assertEqual(self.camera()["previewCount"], 2)
        self.browser(pausedLeaseId=self.lease)
        self.assertFalse(self.camera()["ready"])
        self.assertEqual(self.control.browser({"action":"poll", "previewId":other})["action"], "play")
        self.control.browser({"action":"stop", "previewId":second})
        self.assertTrue(self.camera()["ready"])

    def test_preview_started_during_capture_is_held_until_release(self):
        self.assertTrue(self.camera()["ready"])
        self.assertEqual(self.browser("register")["action"], "pause")
        self.camera("release")
        self.assertEqual(self.browser()["action"], "play")

    def test_deadman_and_stale_preview_do_not_fake_an_ack(self):
        self.browser("register")
        self.camera()
        self.now += 13
        self.assertFalse(self.camera()["ready"])
        self.browser("register", pausedLeaseId=self.lease)
        self.assertTrue(self.camera()["ready"])
        self.now += 120
        self.assertEqual(self.browser("register")["action"], "play")

    def test_repeated_release_and_late_acquire_cannot_repause(self):
        self.browser("register")
        self.camera()
        self.camera("release")
        self.assertTrue(self.camera("release")["released"])
        with self.assertRaisesRegex(ValueError, "ended"):
            self.camera()
        self.assertEqual(self.browser()["action"], "play")

    def test_lease_cannot_be_released_by_another_device(self):
        self.camera()
        with self.assertRaisesRegex(ValueError, "identity_conflict"):
            self.control.camera({"leaseId": self.lease, "action": "release"}, "192.168.100.62")
        with self.assertRaisesRegex(ValueError, "busy_conflict"):
            self.control.camera({"leaseId": uuid.uuid4().hex, "action": "acquire"}, self.ip)

    def test_long_poll_wakes_for_capture_without_browser_timer(self):
        self.browser("register")
        results = []
        thread = threading.Thread(target=lambda: results.append(self.browser(observed="play",waitSeconds=2)))
        thread.start()
        time.sleep(.05)
        started = time.monotonic()
        self.camera()
        thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertLess(time.monotonic() - started, 1)
        self.assertEqual(results[0]["action"], "pause")

    def test_http_authentication_and_local_browser_boundary(self):
        with tempfile.TemporaryDirectory() as directory:
            server = receiver.CaptureServer(("127.0.0.1",0),receiver.CaptureStore(directory),TOKEN,0)
            thread = threading.Thread(target=server.serve_forever); thread.start()
            def call(path, body, headers):
                connection = http.client.HTTPConnection("127.0.0.1",server.server_port,timeout=5)
                try:
                    connection.request("POST",path,json.dumps(body),{"Content-Type":"application/json",**headers})
                    response = connection.getresponse()
                    return response.status, json.loads(response.read())
                finally: connection.close()
            try:
                preview = {"action":"register","previewId":self.preview,"deviceAddress":"127.0.0.1"}
                capture = {"action":"acquire","leaseId":self.lease}
                self.assertEqual(call("/api/live-preview",preview,{})[0],403)
                self.assertEqual(call("/api/live-preview",preview,{"X-Demo-Action":"live-preview","Host":"evil.example"})[0],403)
                self.assertEqual(call("/api/live-preview",preview,{"X-Demo-Action":"live-preview"})[0],200)
                self.assertEqual(call("/api/camera-lease",capture,{})[0],401)
                status, state = call("/api/camera-lease",capture,{"Authorization":"Bearer "+TOKEN})
                self.assertEqual(status,200)
                self.assertFalse(state["ready"])
            finally:
                server.shutdown(); server.server_close(); thread.join()
