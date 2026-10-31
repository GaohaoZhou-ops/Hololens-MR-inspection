import concurrent.futures
import http.client
import importlib.util
import json
from email.message import Message
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import uuid

SOURCE = Path(__file__).resolve().parents[1] / "server" / "server.py"
spec = importlib.util.spec_from_file_location("capture_server", SOURCE)
receiver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receiver)
TOKEN = "test-token-for-local-receiver-validation"
# A JPEG envelope is sufficient for testing this storage-only stub.
JPEG = b"\xff\xd8\xff\xe0test-upload\xff\xd9"


class ReceiverTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = receiver.CaptureStore(self.directory.name)
        self.server = receiver.CaptureServer(("127.0.0.1", 0), self.store, TOKEN, 0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.directory.cleanup()

    def request(self, image=JPEG, capture_id=None, token=TOKEN, content_type="image/jpeg"):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.request("POST", "/api/captures", body=image, headers={
            "Authorization": "Bearer " + token,
            "X-Capture-Id": capture_id or uuid.uuid4().hex,
            "Content-Type": content_type,
        })
        response = connection.getresponse()
        result = (response.status, json.loads(response.read()))
        connection.close()
        return result

    def get(self, path, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.request("GET", path, headers=headers or {})
        response = connection.getresponse()
        result = (response.status, dict(response.getheaders()), response.read())
        connection.close()
        return result

    def json_get(self, path):
        status, _, body = self.get(path)
        self.assertEqual(status, 200)
        return json.loads(body)

    def clear(self, headers=None, path="/api/captures"):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.request("DELETE", path, headers={"X-Demo-Action": "clear-history"} if headers is None else headers)
        response = connection.getresponse()
        result = response.status, json.loads(response.read())
        connection.close()
        return result

    def completed(self, capture_id):
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            detail = self.json_get("/api/captures/" + capture_id)
            if detail["state"] != "processing":
                return detail
            time.sleep(0.01)
        self.fail("The audit record did not complete")

    def test_one_second_delay_and_saved_original_bytes(self):
        self.server.delay_seconds = 1
        started = time.monotonic()
        status, result = self.request()
        self.assertEqual(status, 200)
        self.assertGreaterEqual(time.monotonic() - started, 0.95)
        self.assertEqual(result["serial"], "HL1-000001")
        self.assertEqual((result["centerX"], result["centerY"]), (0.5, 0.5))
        files = list((Path(self.directory.name) / "images").glob("*.jpg"))
        self.assertEqual(files[0].read_bytes(), JPEG)

    def test_unauthorized_invalid_id_and_non_jpeg_are_rejected(self):
        self.assertEqual(self.request(token="wrong")[0], 401)
        self.assertEqual(self.request(capture_id="../../escape")[0], 400)
        self.assertEqual(self.request(content_type="text/plain")[0], 415)
        self.assertEqual(self.request(image=b"not a picture")[0], 400)
        self.assertEqual(list((Path(self.directory.name) / "images").iterdir()), [])

    def test_rejected_upload_with_body_after_headers_returns_http_error(self):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=3)
        try:
            connection.putrequest("POST", "/api/captures")
            connection.putheader("Authorization", "Bearer wrong")
            connection.putheader("Content-Length", str(len(JPEG)))
            connection.endheaders()
            time.sleep(.05)
            connection.send(JPEG)
            response = connection.getresponse()
            self.assertEqual(response.status, 401)
            self.assertEqual(json.loads(response.read()), {"error": "unauthorized"})
        finally:
            connection.close()
        self.assertEqual(list((Path(self.directory.name) / "images").iterdir()), [])

    def test_rejected_upload_does_not_wait_indefinitely_for_missing_body(self):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=2)
        try:
            connection.putrequest("POST", "/api/captures")
            connection.putheader("Authorization", "Bearer wrong")
            connection.putheader("Content-Length", str(len(JPEG)))
            connection.endheaders()
            started = time.monotonic()
            response = connection.getresponse()
            self.assertEqual(response.status, 401)
            response.read()
            self.assertLess(time.monotonic() - started, 1.5)
        finally:
            connection.close()

    def test_duplicate_request_is_idempotent_and_conflict_is_rejected(self):
        capture_id = uuid.uuid4().hex
        status, first = self.request(capture_id=capture_id)
        self.assertEqual(status, 200)
        status, second = self.request(capture_id=capture_id)
        self.assertEqual(second["serial"], first["serial"])
        self.assertTrue(second["cached"])
        self.assertEqual(self.request(capture_id=capture_id, image=b"\xff\xd8\xffchanged\xff\xd9")[0], 409)
        self.assertEqual(len(list((Path(self.directory.name) / "images").glob("*.jpg"))), 1)

    def test_parallel_uploads_have_distinct_serials(self):
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(lambda _: self.request(), range(4)))
        self.assertTrue(all(status == 200 for status, _ in results))
        self.assertEqual(len({result["serial"] for _, result in results}), 4)

    def test_serial_sequence_survives_store_recreation(self):
        self.request()
        self.server.store = receiver.CaptureStore(self.directory.name)
        self.assertEqual(self.request()[1]["serial"], "HL1-000002")

    def test_oversized_body_is_rejected_before_reading(self):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=5)
        connection.putrequest("POST", "/api/captures")
        for name, value in {"Authorization": "Bearer " + TOKEN, "X-Capture-Id": uuid.uuid4().hex,
                            "Content-Type": "image/jpeg", "Content-Length": str(receiver.MAX_IMAGE_BYTES + 1)}.items():
            connection.putheader(name, value)
        connection.endheaders()
        response = connection.getresponse()
        self.assertEqual(response.status, 413)
        response.read()
        connection.close()

    def test_dashboard_preserves_legacy_photos_without_inventing_a_response(self):
        capture_id = uuid.uuid4().hex
        saved = self.store.save(capture_id, JPEG)
        detail = self.json_get("/api/captures/" + capture_id)
        self.assertEqual(detail["serial"], saved["serial"])
        self.assertEqual(detail["state"], "legacy")
        self.assertIsNone(detail["response"])
        self.assertIsNone(detail["processingMs"])
        self.assertEqual(detail["attemptCount"], 0)
        status, headers, image = self.get(detail["imageUrl"] + "?download=1")
        self.assertEqual(status, 200)
        self.assertEqual(image, JPEG)
        self.assertEqual(headers["Content-Type"], "image/jpeg")
        self.assertIn(saved["serial"], headers["Content-Disposition"])

    def test_dashboard_records_exact_response_and_retries_after_reopening_store(self):
        capture_id = uuid.uuid4().hex
        _, first = self.request(capture_id=capture_id)
        self.completed(capture_id)
        _, second = self.request(capture_id=capture_id)
        detail = self.completed(capture_id)
        self.assertEqual(detail["attemptCount"], 2)
        self.assertEqual(detail["response"], second)
        self.assertEqual(detail["attempts"][1]["response"], first)
        self.assertTrue(detail["attempts"][0]["cached"])
        self.assertEqual(detail["sourceIp"], "127.0.0.1")
        self.assertEqual(detail["state"], "responded")
        self.assertEqual(detail["attempts"][0]["httpStatus"], 200)
        self.server.store = receiver.CaptureStore(self.directory.name)
        self.assertEqual(self.json_get("/api/captures/" + capture_id)["response"], second)
        page = self.json_get("/api/captures")
        self.assertEqual(page["stats"]["photos"], 1)
        self.assertEqual(page["stats"]["responded"], 2)

    def test_dashboard_exposes_processing_before_a_response_exists(self):
        self.server.delay_seconds = 0.4
        capture_id = uuid.uuid4().hex
        with concurrent.futures.ThreadPoolExecutor() as pool:
            future = pool.submit(self.request, capture_id=capture_id)
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                page = self.json_get("/api/captures")
                if page["stats"]["processing"]:
                    break
                time.sleep(0.01)
            self.assertEqual(page["stats"]["processing"], 1)
            detail = self.json_get("/api/captures/" + capture_id)
            self.assertEqual(detail["state"], "processing")
            self.assertIsNone(detail["response"])
            self.assertEqual(future.result()[0], 200)
        self.assertEqual(self.completed(capture_id)["state"], "responded")

    def test_dashboard_cursor_search_and_query_validation(self):
        for _ in range(7):
            self.request()
        page = self.json_get("/api/captures?limit=3")
        self.assertEqual([c["sequence"] for c in page["captures"]], [7, 6, 5])
        self.assertEqual(page["nextBefore"], 5)
        page = self.json_get("/api/captures?limit=3&before=5")
        self.assertEqual([c["sequence"] for c in page["captures"]], [4, 3, 2])
        self.assertEqual(self.json_get("/api/captures?limit=3&before=2")["nextBefore"], None)
        self.assertEqual(self.json_get("/api/captures?q=hl1-000003")["captures"][0]["sequence"], 3)
        self.assertEqual(self.json_get("/api/captures?q=no-match")["captures"], [])
        for query in ("limit=0", "limit=101", "before=-1", "limit=hello"):
            self.assertEqual(self.get("/api/captures?" + query)[0], 400)

    def test_dashboard_access_and_paths_do_not_expose_private_files_or_tokens(self):
        for path in ("/", "/app.js", "/style.css", "/favicon.svg", "/system-live", "/system-live.js", "/system-live.css"):
            status, headers, body = self.get(path)
            self.assertEqual(status, 200)
            self.assertNotIn(TOKEN.encode(), body)
            self.assertEqual("media-src 'self' https:" in headers["Content-Security-Policy"], path == "/system-live")
        self.assertEqual(self.get("/api/captures", {"Host": "untrusted.example"})[0], 403)
        self.assertEqual(self.get("/api/captures", {"Origin": "https://untrusted.example"})[0], 403)
        handler = object.__new__(receiver.CaptureHandler)
        handler.client_address = ("192.168.0.90", 1234)
        handler.headers = Message()
        handler.headers["Host"] = "127.0.0.1"
        self.assertFalse(handler.viewer_allowed())
        for path in ("/../../config/server.local.json", "/api/captures/../../config", "/api/captures/" + "a"*32):
            self.assertEqual(self.get(path)[0], 404)

    def test_dashboard_recovers_incomplete_attempts_without_marking_them_sent(self):
        capture_id = uuid.uuid4().hex
        response = self.store.save(capture_id, JPEG)
        attempt = self.store.begin_attempt(capture_id, "192.168.0.8", False)
        self.store.prepare_response(attempt, 200, response)
        self.store.recover_pending()
        detail = self.json_get("/api/captures/" + capture_id)
        self.assertEqual(detail["state"], "interrupted")
        self.assertEqual(detail["response"], response)
        self.assertEqual(self.json_get("/api/captures")["stats"]["responded"], 0)

    def test_dashboard_records_failed_response_write(self):
        original = receiver.CaptureHandler.send_bytes
        def fail_upload(handler, status, payload, content_type, download=None):
            if handler.command == "POST":
                handler.close_connection = True
                return False
            return original(handler, status, payload, content_type, download)
        capture_id = uuid.uuid4().hex
        with patch.object(receiver.CaptureHandler, "send_bytes", fail_upload):
            with self.assertRaises(http.client.RemoteDisconnected):
                self.request(capture_id=capture_id)
            detail = self.completed(capture_id)
        self.assertEqual(detail["state"], "disconnected")
        self.assertEqual(detail["response"]["captureId"], capture_id)
        self.assertEqual(self.json_get("/api/captures")["stats"]["failed"], 1)

    def test_clear_removes_photos_attempts_and_preserves_serial_sequence(self):
        legacy_id, capture_id = uuid.uuid4().hex, uuid.uuid4().hex
        self.store.save(legacy_id, JPEG)
        self.request(capture_id=capture_id)
        self.request(capture_id=capture_id)
        self.completed(capture_id)
        unrelated = Path(self.directory.name) / "images" / "keep.txt"
        unrelated.write_text("not a capture")
        status, result = self.clear()
        # The response/audit can be visible just before the upload handler's
        # finally block releases its slot. A busy clear is an expected response.
        deadline = time.monotonic() + 2
        while status == 409 and time.monotonic() < deadline:
            self.assertEqual(result["error"], "uploads_in_progress")
            time.sleep(.01)
            status, result = self.clear()
        self.assertEqual(status, 200)
        self.assertEqual(result["deletedCaptures"], 2)
        self.assertEqual(result["deletedAttempts"], 2)
        self.assertFalse(result["cleanupPending"])
        page = self.json_get("/api/captures")
        self.assertEqual(page["captures"], [])
        self.assertEqual(page["stats"], {"photos": 0, "responded": 0, "processing": 0, "failed": 0})
        self.assertEqual(page["historyRevision"], 1)
        self.assertEqual(self.get("/api/captures/" + capture_id)[0], 404)
        self.assertEqual(self.get("/api/captures/" + legacy_id + "/image")[0], 404)
        self.assertEqual(list((Path(self.directory.name) / "images").glob("*.jpg")), [])
        self.assertEqual(unrelated.read_text(), "not a capture")
        self.server.store = receiver.CaptureStore(self.directory.name)
        self.assertEqual(self.request()[1]["serial"], "HL1-000003")

    def test_clear_requires_local_viewer_and_explicit_action_header(self):
        self.store.save(uuid.uuid4().hex, JPEG)
        self.assertEqual(self.clear({})[0], 403)
        self.assertEqual(self.clear({"X-Demo-Action": "clear-history", "Origin": "https://untrusted.example"})[0], 403)
        self.assertEqual(self.clear({"X-Demo-Action": "clear-history", "Host": "untrusted.example"})[0], 403)
        self.assertEqual(self.clear(path="/api/captures?q=HL1")[0], 404)
        self.assertEqual(self.json_get("/api/captures")["stats"]["photos"], 1)

    def test_clear_refuses_inflight_upload_and_releases_every_slot(self):
        entered, release = threading.Event(), threading.Event()
        original = self.store.prepare_response
        def waiting(*args):
            entered.set()
            release.wait(5)
            return original(*args)
        with patch.object(self.store, "prepare_response", waiting), concurrent.futures.ThreadPoolExecutor() as pool:
            future = pool.submit(self.request)
            try:
                self.assertTrue(entered.wait(3))
                self.assertEqual(self.clear(), (409, {"error": "uploads_in_progress"}))
                self.assertEqual(self.json_get("/api/captures")["stats"]["photos"], 1)
            finally:
                release.set()
            self.assertEqual(future.result()[0], 200)
        self.completed(future.result()[1]["captureId"])
        self.assertEqual(self.clear()[0], 200)
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            self.assertTrue(all(status == 200 for status, _ in pool.map(lambda _: self.request(), range(4))))

    def test_clear_file_failure_rolls_back_records_and_moved_images(self):
        for _ in range(2):
            self.store.save(uuid.uuid4().hex, JPEG)
        files = sorted((Path(self.directory.name) / "images").glob("*.jpg"))
        original = Path.replace
        def deny_second(path, target):
            if path == files[1]:
                raise PermissionError("image is in use")
            return original(path, target)
        with patch.object(Path, "replace", deny_second):
            self.assertEqual(self.clear(), (500, {"error": "clear_failed"}))
        self.assertEqual(self.json_get("/api/captures")["stats"]["photos"], 2)
        self.assertEqual(self.json_get("/api/captures")["historyRevision"], 0)
        self.assertTrue(all(path.read_bytes() == JPEG for path in files))
        self.assertEqual(self.clear()[0], 200)

    def test_clear_interrupted_before_commit_restores_images_on_restart(self):
        saved = self.store.save(uuid.uuid4().hex, JPEG)
        original = Path.replace
        def move_then_interrupt(path, target):
            original(path, target)
            raise KeyboardInterrupt("simulated process interruption")
        with patch.object(Path, "replace", move_then_interrupt), self.assertRaises(KeyboardInterrupt):
            self.store.clear_history()
        self.server.store = receiver.CaptureStore(self.directory.name)
        self.assertEqual(self.get("/api/captures/" + saved["captureId"] + "/image")[2], JPEG)
        self.assertEqual(self.json_get("/api/captures")["historyRevision"], 0)

    def test_clear_pending_file_cleanup_finishes_after_restart(self):
        self.store.save(uuid.uuid4().hex, JPEG)
        with patch.object(receiver.shutil, "rmtree", side_effect=PermissionError("file temporarily locked")):
            status, result = self.clear()
        self.assertEqual(status, 200)
        self.assertTrue(result["cleanupPending"])
        self.assertEqual(self.json_get("/api/captures")["captures"], [])
        self.server.store = receiver.CaptureStore(self.directory.name)
        self.assertFalse((Path(self.directory.name) / ".clear-pending").exists())
        self.assertEqual(self.request()[1]["serial"], "HL1-000002")

    def test_clear_intent_write_failure_does_not_block_later_clears(self):
        self.store.save(uuid.uuid4().hex, JPEG)
        original = Path.write_text
        def fail_intent(path, data, *args, **kwargs):
            if path.name == "intent.json":
                original(path, "{", *args, **kwargs)
                raise OSError("simulated disk write failure")
            return original(path, data, *args, **kwargs)
        with patch.object(Path, "write_text", fail_intent):
            self.assertEqual(self.clear(), (500, {"error": "clear_failed"}))
        self.assertEqual(self.json_get("/api/captures")["stats"]["photos"], 1)
        self.assertEqual(self.clear()[0], 200)


if __name__ == "__main__":
    unittest.main(verbosity=2)
