import hashlib
import http.client
import json
from pathlib import Path
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
import uuid
from contextlib import closing

from test_server import receiver, TOKEN, JPEG
from test_poses import batch


class WorkHistoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = receiver.CaptureStore(self.directory.name)
        self.server = receiver.CaptureServer(("127.0.0.1", 0), self.store, TOKEN, 0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.device_id, self.work_id = uuid.uuid4().hex, uuid.uuid4().hex
        self.poll_body = {"deviceId": self.device_id, "workId": self.work_id, "deviceName": "History device",
                          "clientKind": "editor", "cache": {}, "status": "ready"}

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.thread.join(); self.directory.cleanup()

    def call(self, method, path, body=None, headers=None):
        defaults = {"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"}
        defaults.update(headers or {})
        if body is not None and not isinstance(body, bytes):
            body = json.dumps(body).encode()
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        try:
            connection.request(method, path, body=body, headers=defaults)
            response = connection.getresponse()
            content_type, data = response.getheader("Content-Type", ""), response.read()
            return response.status, json.loads(data) if "application/json" in content_type else data
        finally:
            connection.close()

    def poll(self):
        return self.call("POST", "/api/device-poll", self.poll_body)

    def archive(self, capture_id=None):
        self.assertEqual(self.poll()[0], 200)
        capture_id = capture_id or uuid.uuid4().hex
        saved = self.store.save(capture_id, JPEG)
        data = b'{"x":1.2,"y":0.5,"z":-2}'
        headers = {"Content-Type": "application/octet-stream", "X-Device-Id": self.device_id,
                   "X-Work-Id": self.work_id, "X-Anchor-Kind": "editor"}
        status, ack = self.call("POST", "/api/work-anchors/" + capture_id, data, headers)
        self.assertEqual(status, 200, ack)
        return capture_id, saved, data, headers, ack

    def command(self, kind="resume", **extra):
        body = {"commandId": uuid.uuid4().hex, "deviceId": self.device_id, "kind": kind,
                "workId": self.work_id, **extra}
        result = self.call("POST", "/api/device-commands", body, {"X-Demo-Action": "device-command"})
        return body, result

    def test_anchor_archive_manifest_exact_bytes_and_restart_persistence(self):
        capture_id, saved, data, headers, ack = self.archive()
        self.assertEqual(ack["anchorSha256"], hashlib.sha256(data).hexdigest())
        self.assertEqual(ack["anchorBytes"], len(data))
        self.assertEqual(self.call("POST", "/api/work-anchors/" + capture_id, data, headers), (200, ack))
        self.server.work = receiver.WorkStore(self.directory.name)
        status, manifest = self.call("GET", "/api/device-history/" + self.work_id)
        self.assertEqual(status, 200)
        self.assertEqual(manifest["records"][0]["serial"], saved["serial"])
        self.assertEqual(manifest["records"][0]["anchorKind"], "editor")
        self.assertEqual(self.call("GET", "/api/device-anchor/" + capture_id), (200, data))
        _, overview = self.call("GET", "/api/work-history")
        self.assertEqual(overview["works"][0]["restorableCount"], 1)

    def test_archive_identity_conflicts_and_unavailable_anchor_are_explicit(self):
        capture_id, _, data, headers, _ = self.archive()
        self.assertEqual(self.call("POST", "/api/work-anchors/" + capture_id, b'{"x":99,"y":0,"z":0}', headers)[0], 409)
        self.assertEqual(self.call("POST", "/api/work-anchors/" + capture_id, data, {**headers, "X-Anchor-Kind": "wsa"})[0], 400)
        missing_id = uuid.uuid4().hex
        self.assertEqual(self.call("POST", "/api/work-anchors/" + missing_id, data, headers)[0], 409)
        self.store.save(missing_id, JPEG)
        self.assertEqual(self.call("POST", "/api/work-anchors/" + missing_id, b"", {**headers, "X-Anchor-Kind": "none"})[0], 200)
        _, manifest = self.call("GET", "/api/device-history/" + self.work_id)
        unavailable = next(r for r in manifest["records"] if r["captureId"] == missing_id)
        self.assertEqual(unavailable["anchorBytes"], 0)
        self.assertEqual(unavailable["anchorKind"], "none")

    def test_commands_survive_restart_and_wait_for_idempotent_ack(self):
        self.poll()
        body, (status, result) = self.command()
        self.assertEqual(status, 200)
        self.assertEqual(result["status"], "queued")
        self.assertEqual(self.call("POST", "/api/device-commands", body, {"X-Demo-Action": "device-command"}), (200, result))
        self.server.work = receiver.WorkStore(self.directory.name)
        first = self.poll()[1]["command"]
        self.assertEqual(first["commandId"], body["commandId"])
        self.assertEqual(self.poll()[1]["command"], first)
        second, _ = self.command("new_work", title="第二次检查")
        self.assertEqual(self.poll()[1]["command"]["commandId"], body["commandId"])
        ack = {"deviceId": self.device_id, "commandId": body["commandId"], "status": "partial", "detail": "loaded, awaiting localization"}
        self.assertEqual(self.call("POST", "/api/device-acks", ack)[0], 200)
        self.assertEqual(self.call("POST", "/api/device-acks", ack)[0], 200)
        self.assertEqual(self.call("POST", "/api/device-acks", {**ack, "status": "completed"})[0], 409)
        self.assertEqual(self.poll()[1]["command"]["commandId"], second["commandId"])

    def test_device_cache_clear_requires_recent_heartbeat_and_expires(self):
        self.poll()
        with closing(sqlite3.connect(self.server.work.database)) as db, db:
            db.execute("UPDATE devices SET seen_at='2000-01-01T00:00:00+00:00'")
        _, response = self.command("clear_cache", categories=["pendingPhotos"])
        self.assertEqual(response[0], 409)
        self.poll()
        body, response = self.command("clear_cache", categories=["downloadedAnchors", "visibleLabels"])
        self.assertEqual(response[0], 200)
        with closing(sqlite3.connect(self.server.work.database)) as db, db:
            db.execute("UPDATE device_commands SET expires_at=0")
        self.assertIsNone(self.poll()[1]["command"])
        _, overview = self.call("GET", "/api/work-history")
        self.assertEqual(overview["devices"][0]["command"]["status"], "expired")

    def test_selective_photo_clear_preserves_records_anchor_and_trajectory(self):
        capture_id, saved, data, _, _ = self.archive()
        poses = batch(2)
        self.assertEqual(self.call("POST", "/api/poses", poses)[0], 200)
        unrelated = Path(self.directory.name) / "images" / "keep.txt"
        unrelated.write_text("not a photo")
        status, cleared = self.call("POST", "/api/cache/clear", {"categories": ["images"]}, {"X-Demo-Action": "cache-clear"})
        self.assertEqual(status, 200)
        self.assertEqual(cleared["images"]["count"], 1)
        _, record = self.call("GET", "/api/captures/" + capture_id)
        self.assertEqual(record["serial"], saved["serial"])
        self.assertFalse(record["imageAvailable"])
        self.assertEqual(self.call("GET", record["imageUrl"])[0], 404)
        self.assertEqual(self.call("GET", "/api/device-anchor/" + capture_id), (200, data))
        self.assertEqual(self.call("GET", "/api/pose-sessions")[1]["sessions"][0]["sampleCount"], 2)
        self.assertTrue(unrelated.exists())
        self.assertEqual(self.store.save(uuid.uuid4().hex, JPEG)["serial"], "HL1-000002")

    def test_selective_pose_clear_keeps_photo_archive_and_accepts_new_samples(self):
        capture_id, saved, data, _, _ = self.archive()
        poses = batch(3)
        self.call("POST", "/api/poses", poses)
        revision = self.call("GET", "/api/pose-sessions")[1]["historyRevision"]
        result = self.call("POST", "/api/cache/clear", {"categories": ["poses"]}, {"X-Demo-Action": "cache-clear"})
        self.assertEqual(result[0], 200)
        self.assertEqual(result[1]["poses"]["count"], 3)
        self.assertEqual(self.call("GET", "/api/pose-sessions")[1]["sessions"], [])
        self.assertEqual(self.call("GET", "/api/pose-sessions")[1]["historyRevision"], revision + 1)
        self.assertTrue(self.store.image_path(saved).exists())
        self.assertEqual(self.call("GET", "/api/device-anchor/" + capture_id), (200, data))
        self.assertEqual(self.call("POST", "/api/poses", poses)[0], 200)
        self.server.poses = receiver.PoseStore(self.directory.name)
        self.assertEqual(self.call("GET", "/api/pose-sessions/" + poses["session"]["sessionId"])[1]["historyRevision"], revision + 1)

    def test_cache_clear_never_deletes_inflight_uploads_and_releases_slots(self):
        self.archive()
        self.server.upload_slots.acquire()
        try:
            result = self.call("POST", "/api/cache/clear", {"categories": ["images"]}, {"X-Demo-Action": "cache-clear"})
            self.assertEqual(result[0], 409)
            self.assertEqual(len(list((Path(self.directory.name) / "images").glob("*.jpg"))), 1)
        finally:
            self.server.upload_slots.release()
        self.assertEqual(self.call("POST", "/api/cache/clear", {"categories": ["images"]}, {"X-Demo-Action": "cache-clear"})[0], 200)

    def test_private_history_and_clear_actions_require_their_authentication(self):
        capture_id, _, _, _, _ = self.archive()
        for path in ("/api/device-history/" + self.work_id, "/api/device-anchor/" + capture_id):
            self.assertEqual(self.call("GET", path, headers={"Authorization": "wrong"})[0], 401)
        self.assertEqual(self.call("GET", "/api/work-history", headers={"Host": "untrusted.example"})[0], 403)
        for route, body in (("/api/device-commands", {}), ("/api/cache/clear", {"categories": ["images"]})):
            self.assertEqual(self.call("POST", route, body)[0], 403)
        self.assertEqual(self.call("POST", "/api/cache/clear", {"categories": ["everything"]}, {"X-Demo-Action": "cache-clear"})[0], 400)
        _, response = self.command("clear_cache", categories=["privateKeys"])
        self.assertEqual(response[0], 400)
        for path in ("/", "/history.js"):
            status, content = self.call("GET", path)
            self.assertEqual(status, 200)
            self.assertNotIn(TOKEN.encode(), content)

    def test_full_photo_history_clear_does_not_remove_work_backup(self):
        capture_id, _, data, headers, ack = self.archive()
        self.assertEqual(self.call("DELETE", "/api/captures", headers={"X-Demo-Action": "clear-history"})[0], 200)
        self.assertEqual(self.call("GET", "/api/device-anchor/" + capture_id), (200, data))
        self.assertEqual(self.call("GET", "/api/device-history/" + self.work_id)[1]["total"], 1)
        self.assertEqual(self.call("POST", "/api/work-anchors/" + capture_id, data, headers), (200, ack))
