#!/usr/bin/env python3
"""Cross-platform photo receiver and web monitor for HoloLensCaptureDemo."""
import argparse
import hashlib
import hmac
import ipaddress
import json
import math
import os
import re
import secrets
import shutil
import socket
import sqlite3
import sys
import threading
import time
import webbrowser
from contextlib import closing
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_UPLOADS = 4
MAX_POSE_BYTES = 96 * 1024
CAPTURE_ID = re.compile(r"^[a-f0-9]{32}$")
WEB_ROOT = Path(__file__).resolve().parent / "web"
DEFAULT_CONFIG_PATH = Path(__file__).resolve().parents[1] / "config" / "server.local.json"
TOKEN_PLACEHOLDER = "replace-with-the-paired-client-token"


def initialize_config(path):
    """Create local credentials once; never rotate an existing device's token."""
    path = Path(path).resolve()
    config = {"host": "0.0.0.0", "port": 8765, "apiToken": secrets.token_urlsafe(32),
              "delaySeconds": 1.0, "dataDirectory": "../server/data"}
    path.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation also protects an existing config from concurrent initializers.
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as output:
        json.dump(config, output, indent=2)
        output.write("\n")
    return path


def load_config(path):
    path = Path(path).resolve()
    config = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(config, dict):
        raise ValueError("The receiver configuration must be a JSON object.")
    token = config.get("apiToken")
    if not isinstance(token, str) or len(token) < 24 or token == TOKEN_PLACEHOLDER:
        raise ValueError("apiToken must match the HoloLens client and contain at least 24 characters; "
                         "use --init-config to generate a new configuration.")
    host = config.get("host", "0.0.0.0")
    if not isinstance(host, str) or not host:
        raise ValueError("host must be a nonempty IPv4 address or hostname.")
    port = config.get("port", 8765)
    if isinstance(port, bool) or not isinstance(port, int) or not 0 <= port <= 65535:
        raise ValueError("port must be an integer from 0 to 65535 (0 selects a temporary port).")
    delay = config.get("delaySeconds", 1.0)
    if isinstance(delay, bool) or not isinstance(delay, (int, float)) or not 0 <= delay <= 30:
        raise ValueError("delaySeconds must be a number from 0 to 30.")
    directory = config.get("dataDirectory", "../server/data")
    if not isinstance(directory, str) or not directory:
        raise ValueError("dataDirectory must be a nonempty path, relative to the config file or absolute.")
    config.update(host=host, port=port, delaySeconds=delay,
                  dataDirectory=(path.parent / directory).resolve())
    return config


def utc_now():
    return datetime.now(timezone.utc).isoformat()


class CaptureStore:
    def __init__(self, directory):
        self.directory = Path(directory)
        (self.directory / "images").mkdir(parents=True, exist_ok=True)
        self.database = self.directory / "captures.sqlite3"
        self.lock = threading.Lock()
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute("""CREATE TABLE IF NOT EXISTS captures (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                capture_id TEXT NOT NULL UNIQUE, sha256 TEXT NOT NULL,
                created_at TEXT NOT NULL, image_bytes INTEGER NOT NULL)""")
            # Add a separate audit table so existing photos and serials remain intact.
            db.execute("""CREATE TABLE IF NOT EXISTS capture_attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT, capture_id TEXT NOT NULL,
                started_at TEXT NOT NULL, completed_at TEXT, source_ip TEXT NOT NULL,
                state TEXT NOT NULL, cached INTEGER NOT NULL,
                response_status INTEGER, response_json TEXT, processing_ms INTEGER,
                error TEXT)""")
            db.execute("CREATE INDEX IF NOT EXISTS attempts_capture ON capture_attempts(capture_id,id)")
            db.execute("""CREATE TABLE IF NOT EXISTS monitor_state (
                id INTEGER PRIMARY KEY CHECK(id=1), history_revision INTEGER NOT NULL)""")
            db.execute("INSERT OR IGNORE INTO monitor_state VALUES(1,0)")
        self.recover_clear()

    def recover_clear(self):
        # A durable intent distinguishes a rollback from cleanup after a committed clear.
        pending = self.directory / ".clear-pending"
        if not pending.exists():
            return
        images = list(pending.glob("*.jpg"))
        if not images:
            # Also handles a failed/partial intent write before any image was moved.
            shutil.rmtree(pending)
            return
        intent = pending / "intent.json"
        if not intent.exists():
            pending.rmdir()  # No images move until the intent has been written.
            return
        target = json.loads(intent.read_text(encoding="utf-8"))["revision"]
        with closing(sqlite3.connect(self.database)) as db:
            revision = db.execute("SELECT history_revision FROM monitor_state WHERE id=1").fetchone()[0]
        if revision < target:
            for image in images:
                image.replace(self.directory / "images" / image.name)
        shutil.rmtree(pending)

    def clear_history(self):
        # The caller must hold every upload slot, including while the response is recorded.
        with self.lock:
            self.recover_clear()
            pending = self.directory / ".clear-pending"
            with closing(sqlite3.connect(self.database)) as db:
                try:
                    db.execute("BEGIN IMMEDIATE")
                    rows = db.execute("SELECT sequence,capture_id FROM captures").fetchall()
                    attempts = db.execute("SELECT COUNT(*) FROM capture_attempts").fetchone()[0]
                    revision = db.execute("SELECT history_revision FROM monitor_state WHERE id=1").fetchone()[0] + 1
                    pending.mkdir()
                    (pending / "intent.json").write_text(json.dumps({"revision": revision}), encoding="utf-8")
                    for sequence, capture_id in rows:
                        image = self.directory / "images" / f"HL1-{sequence:06d}-{capture_id}.jpg"
                        if image.exists():
                            image.replace(pending / image.name)
                    db.execute("DELETE FROM capture_attempts")
                    db.execute("DELETE FROM captures")
                    # Leave sqlite_sequence intact: serial numbers must never be reused.
                    db.execute("UPDATE monitor_state SET history_revision=? WHERE id=1", (revision,))
                    db.commit()
                except Exception:
                    db.rollback()
                    self.recover_clear()
                    raise
            cleanup_pending = False
            try:
                self.recover_clear()
            except OSError:
                # The records are already cleared; retry file cleanup at the next startup.
                cleanup_pending = True
            return {"deletedCaptures": len(rows), "deletedAttempts": attempts,
                    "historyRevision": revision, "cleanupPending": cleanup_pending}

    def save(self, capture_id, image):
        digest = hashlib.sha256(image).hexdigest()
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            row = db.execute("SELECT sequence,sha256,created_at,image_bytes FROM captures WHERE capture_id=?",
                             (capture_id,)).fetchone()
            cached = row is not None
            if cached:
                if row[1] != digest:
                    raise ValueError("capture_id already belongs to a different image")
            else:
                now = datetime.now(timezone.utc).isoformat()
                cursor = db.execute("INSERT INTO captures(capture_id,sha256,created_at,image_bytes) VALUES(?,?,?,?)",
                                    (capture_id, digest, now, len(image)))
                row = (cursor.lastrowid, digest, now, len(image))
                destination = self.directory / "images" / f"HL1-{row[0]:06d}-{capture_id}.jpg"
                temporary = destination.with_suffix(".tmp")
                temporary.write_bytes(image)
                temporary.replace(destination)
            return {
                "captureId": capture_id,
                "serial": f"HL1-{row[0]:06d}",
                "centerX": 0.5,
                "centerY": 0.5,
                "imageSha256": row[1],
                "receivedAt": row[2],
                "imageBytes": row[3],
                "cached": cached,
            }

    def begin_attempt(self, capture_id, source_ip, cached):
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            cursor = db.execute("""INSERT INTO capture_attempts
                (capture_id,started_at,source_ip,state,cached) VALUES(?,?,?,'processing',?)""",
                                (capture_id, utc_now(), source_ip, int(cached)))
            return cursor.lastrowid

    def prepare_response(self, attempt_id, status, response):
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            db.execute("""UPDATE capture_attempts SET response_status=?,response_json=?,
                processing_ms=? WHERE id=?""", (status, json.dumps(response, ensure_ascii=False),
                                                response.get("processingMs"), attempt_id))

    def finish_attempt(self, attempt_id, state, error=None):
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            db.execute("UPDATE capture_attempts SET state=?,completed_at=?,error=? WHERE id=?",
                       (state, utc_now(), error, attempt_id))

    def recover_pending(self):
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            db.execute("""UPDATE capture_attempts SET state='interrupted',completed_at=?,
                error='Receiver stopped before completion was recorded.' WHERE state='processing'""", (utc_now(),))

    @staticmethod
    def capture_record(row):
        return {
            "sequence": row["sequence"], "captureId": row["capture_id"],
            "serial": f"HL1-{row['sequence']:06d}", "receivedAt": row["created_at"],
            "imageBytes": row["image_bytes"], "imageSha256": row["sha256"],
            "imageUrl": f"/api/captures/{row['capture_id']}/image",
            "sourceIp": row["source_ip"], "state": row["state"] or "legacy",
            "processingMs": row["processing_ms"], "completedAt": row["completed_at"],
            "attemptCount": row["attempt_count"],
            "response": json.loads(row["response_json"]) if row["response_json"] else None,
        }

    CAPTURE_SELECT = """SELECT c.*, a.source_ip,a.state,a.processing_ms,a.completed_at,a.response_json,
        (SELECT COUNT(*) FROM capture_attempts WHERE capture_id=c.capture_id) AS attempt_count
        FROM captures c LEFT JOIN capture_attempts a ON a.id=
        (SELECT MAX(id) FROM capture_attempts WHERE capture_id=c.capture_id)"""

    def list_captures(self, limit=50, before=None, query=""):
        clauses, values = [], []
        if before is not None:
            clauses.append("c.sequence < ?")
            values.append(before)
        if query:
            clauses.append("(instr(lower(printf('HL1-%06d',c.sequence)),?) > 0 OR instr(c.capture_id,?) > 0)")
            values.extend([query.lower(), query.lower()])
        where = " WHERE " + " AND ".join(clauses) if clauses else ""
        with self.lock, closing(sqlite3.connect(self.database)) as db:
            db.row_factory = sqlite3.Row
            rows = db.execute(self.CAPTURE_SELECT + where + " ORDER BY c.sequence DESC LIMIT ?",
                              [*values, limit + 1]).fetchall()
            total = db.execute("SELECT COUNT(*) FROM captures").fetchone()[0]
            states = dict(db.execute("SELECT state,COUNT(*) FROM capture_attempts GROUP BY state"))
            revision = db.execute("SELECT history_revision FROM monitor_state WHERE id=1").fetchone()[0]
        return {"captures": [self.capture_record(row) for row in rows[:limit]],
                "historyRevision": revision,
                "nextBefore": rows[limit - 1]["sequence"] if len(rows) > limit else None,
                "stats": {"photos": total, "responded": states.get("responded", 0),
                          "processing": states.get("processing", 0),
                          "failed": sum(states.get(s, 0) for s in ("failed", "disconnected", "interrupted"))}}

    def get_capture(self, capture_id):
        with self.lock, closing(sqlite3.connect(self.database)) as db:
            db.row_factory = sqlite3.Row
            row = db.execute(self.CAPTURE_SELECT + " WHERE c.capture_id=?", (capture_id,)).fetchone()
            if row is None:
                return None
            attempts = db.execute("SELECT * FROM capture_attempts WHERE capture_id=? ORDER BY id DESC LIMIT 50",
                                  (capture_id,)).fetchall()
        record = self.capture_record(row)
        record["attempts"] = [{"id": a["id"], "startedAt": a["started_at"], "completedAt": a["completed_at"],
                               "sourceIp": a["source_ip"], "state": a["state"], "cached": bool(a["cached"]),
                               "httpStatus": a["response_status"], "processingMs": a["processing_ms"],
                               "response": json.loads(a["response_json"]) if a["response_json"] else None,
                               "error": a["error"]} for a in attempts]
        return record

    def image_path(self, record):
        return self.directory / "images" / f"{record['serial']}-{record['captureId']}.jpg"


class PoseStore:
    """Independent telemetry database; photo history deletion never erases a trajectory."""
    def __init__(self, directory):
        self.database = Path(directory) / "poses.sqlite3"
        self.lock = threading.Lock()
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute("""CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY, started_at TEXT NOT NULL, device_name TEXT NOT NULL,
                client_kind TEXT NOT NULL, last_received_at TEXT NOT NULL, source_ip TEXT NOT NULL)""")
            db.execute("""CREATE TABLE IF NOT EXISTS samples (
                session_id TEXT NOT NULL, sequence INTEGER NOT NULL, elapsed REAL NOT NULL,
                sampled_at TEXT NOT NULL, tracked INTEGER NOT NULL, tracking_state TEXT NOT NULL,
                x REAL, y REAL, z REAL, qx REAL, qy REAL, qz REAL, qw REAL,
                segment INTEGER NOT NULL, PRIMARY KEY(session_id,sequence))""")

    @staticmethod
    def validate(body):
        def number(value, low, high):
            return type(value) in (int, float) and math.isfinite(value) and low <= value <= high
        def timestamp(value):
            if not isinstance(value, str) or len(value) > 40:
                return False
            try:
                return datetime.fromisoformat(value.replace("Z", "+00:00")).utcoffset() is not None
            except ValueError:
                return False
        if not isinstance(body, dict):
            raise ValueError("invalid_pose_batch")
        session = body.get("session", {})
        samples = body.get("samples")
        if (not isinstance(session, dict) or not CAPTURE_ID.fullmatch(str(session.get("sessionId", ""))) or
                not timestamp(session.get("startedAt")) or
                not isinstance(session.get("deviceName"), str) or not 1 <= len(session["deviceName"]) <= 128 or
                session.get("clientKind") not in ("editor", "hololens-uwp") or
                not isinstance(samples, list) or not 1 <= len(samples) <= 60):
            raise ValueError("invalid_pose_session_or_batch")
        previous_sequence, previous_time = -1, -1
        rows = []
        for sample in samples:
            if not isinstance(sample, dict):
                raise ValueError("invalid_pose_sample")
            seq, elapsed, segment = sample.get("sequence"), sample.get("elapsed"), sample.get("segment")
            if (type(seq) is not int or not 0 <= seq <= 2147483647 or seq <= previous_sequence or
                    not number(elapsed, 0, 31536000) or elapsed < previous_time or
                    type(segment) is not int or not 0 <= segment <= 2147483647 or
                    type(sample.get("tracked")) is not bool or not timestamp(sample.get("sampledAt")) or
                    sample.get("trackingState") not in ("Active", "Editor", "Activating", "Inhibited", "OrientationOnly", "Unavailable", "AnchorUnavailable")):
                raise ValueError("invalid_pose_sample")
            pose = [None] * 7
            if sample["tracked"]:
                if sample["trackingState"] not in ("Active", "Editor"):
                    raise ValueError("invalid_tracking_state")
                pose = [sample.get(k) for k in ("x", "y", "z", "qx", "qy", "qz", "qw")]
                if (not all(number(v, -100000, 100000) for v in pose[:3]) or
                        not all(number(v, -1.001, 1.001) for v in pose[3:]) or
                        abs(sum(v*v for v in pose[3:]) - 1) > 0.02):
                    raise ValueError("invalid_position_or_quaternion")
            rows.append((session["sessionId"], seq, elapsed, sample["sampledAt"], int(sample["tracked"]),
                         sample["trackingState"], *pose, segment))
            previous_sequence, previous_time = seq, elapsed
        return session, rows

    def save(self, body, source_ip):
        session, rows = self.validate(body)
        with self.lock, closing(sqlite3.connect(self.database)) as db, db:
            existing = db.execute("SELECT started_at,device_name,client_kind FROM sessions WHERE session_id=?",
                                  (session["sessionId"],)).fetchone()
            identity = (session["startedAt"], session["deviceName"], session["clientKind"])
            if existing and existing != identity:
                raise ValueError("pose_identity_conflict")
            # Verify every duplicate before committing any part of the batch.
            added = 0
            for row in rows:
                prior = db.execute("SELECT * FROM samples WHERE session_id=? AND sequence=?", row[:2]).fetchone()
                if prior is not None and prior != row:
                    raise ValueError("pose_sample_conflict")
                if prior is None:
                    db.execute("INSERT INTO samples VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)", row)
                    added += 1
            db.execute("""INSERT INTO sessions VALUES(?,?,?,?,?,?) ON CONFLICT(session_id) DO UPDATE SET
                last_received_at=excluded.last_received_at,source_ip=excluded.source_ip""",
                       (session["sessionId"], *identity, utc_now(), source_ip))
        return {"sessionId": session["sessionId"], "acceptedThrough": rows[-1][1], "inserted": added}

    def list_sessions(self, limit=100):
        with self.lock, closing(sqlite3.connect(self.database)) as db:
            db.row_factory = sqlite3.Row
            rows = db.execute("""SELECT s.*,COUNT(p.sequence) AS samples,SUM(p.tracked) AS tracked,
                MAX(p.sequence) AS last_sequence,MAX(p.elapsed) AS duration
                FROM sessions s JOIN samples p ON p.session_id=s.session_id
                GROUP BY s.session_id ORDER BY s.started_at DESC,s.session_id DESC LIMIT ?""", (limit,)).fetchall()
        return {"sessions": [{"sessionId": r["session_id"], "startedAt": r["started_at"],
                              "deviceName": r["device_name"], "clientKind": r["client_kind"],
                              "lastReceivedAt": r["last_received_at"], "sourceIp": r["source_ip"],
                              "sampleCount": r["samples"], "trackedCount": r["tracked"],
                              "lastSequence": r["last_sequence"], "duration": r["duration"]} for r in rows]}

    def points(self, session_id, after=-1, limit=1000):
        with self.lock, closing(sqlite3.connect(self.database)) as db:
            if db.execute("SELECT 1 FROM sessions WHERE session_id=?", (session_id,)).fetchone() is None:
                return None
            db.row_factory = sqlite3.Row
            rows = db.execute("SELECT * FROM samples WHERE session_id=? AND sequence>? ORDER BY sequence LIMIT ?",
                              (session_id, after, limit + 1)).fetchall()
        samples = [{"sequence": r["sequence"], "elapsed": r["elapsed"], "sampledAt": r["sampled_at"],
                    "tracked": bool(r["tracked"]), "trackingState": r["tracking_state"], "segment": r["segment"],
                    **{key: r[key] for key in ("x", "y", "z", "qx", "qy", "qz", "qw")}} for r in rows[:limit]]
        return {"sessionId": session_id, "samples": samples,
                "nextAfter": samples[-1]["sequence"] if samples else after, "hasMore": len(rows) > limit}


class CaptureServer(ThreadingHTTPServer):
    # Finish audit writes before a graceful server_close returns (also important on Windows).
    daemon_threads = False

    def __init__(self, address, store, api_token, delay_seconds=1.0):
        if len(api_token) < 24:
            raise ValueError("Configure a local API token with at least 24 characters")
        if not 0 <= delay_seconds <= 30:
            raise ValueError("delay_seconds must be between 0 and 30")
        self.store = store
        self.poses = PoseStore(store.directory)
        self.api_token = api_token
        self.delay_seconds = delay_seconds
        self.upload_slots = threading.BoundedSemaphore(MAX_UPLOADS)
        self.pose_slots = threading.BoundedSemaphore(2)
        super().__init__(address, CaptureHandler)


class CaptureHandler(BaseHTTPRequestHandler):
    server_version = "HoloLensCaptureDemo/1.0"

    def setup(self):
        super().setup()
        self.connection.settimeout(15)

    def send_json(self, status, body):
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        return self.send_bytes(status, payload, "application/json; charset=utf-8")

    def reject_upload(self, status, body):
        # Closing with unread upload bytes can discard the HTTP error via a TCP reset
        # on Windows. Drain an ordinary body within a bounded budget before replying.
        # Never wait for oversized/chunked uploads or for an unbounded slow sender.
        try:
            remaining = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            remaining = 0
        if 0 < remaining <= MAX_IMAGE_BYTES and not self.headers.get("Transfer-Encoding"):
            deadline = time.monotonic() + .25
            original_timeout = self.connection.gettimeout()
            try:
                while remaining > 0:
                    wait = deadline - time.monotonic()
                    if wait <= 0:
                        break
                    self.connection.settimeout(wait)
                    chunk = self.rfile.read1(min(remaining, 64 * 1024))
                    if not chunk:
                        break
                    remaining -= len(chunk)
            except OSError:
                pass
            finally:
                self.connection.settimeout(original_timeout)
        return self.send_json(status, body)

    def send_bytes(self, status, payload, content_type, download=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; object-src 'none'")
        if download:
            self.send_header("Content-Disposition", f'attachment; filename="{download}"')
        self.send_header("Connection", "close")
        try:
            self.end_headers()
            self.wfile.write(payload)
            self.wfile.flush()
            return True
        except OSError:
            self.close_connection = True
            return False

    def viewer_allowed(self):
        # Local browser access needs no upload token. Photos remain unavailable to LAN clients.
        try:
            host = urlsplit("http://" + self.headers.get("Host", "")).hostname
            origin = self.headers.get("Origin")
            return (ipaddress.ip_address(self.client_address[0]).is_loopback and
                    host in ("localhost", "127.0.0.1", "::1") and
                    (not origin or urlsplit(origin).hostname in ("localhost", "127.0.0.1", "::1")))
        except ValueError:
            return False

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/health":
            self.send_json(200, {"service": "HoloLensCaptureDemo", "status": "ok",
                                 "delaySeconds": self.server.delay_seconds, "dashboardAvailable": True})
            return
        if not self.viewer_allowed():
            self.send_json(403, {"error": "viewer_local_only", "message": "Open http://127.0.0.1:8765 on the receiving PC."})
            return
        static = {"/": ("index.html", "text/html; charset=utf-8"),
                  "/app.js": ("app.js", "text/javascript; charset=utf-8"),
                  "/trajectory.js": ("trajectory.js", "text/javascript; charset=utf-8"),
                  "/style.css": ("style.css", "text/css; charset=utf-8"),
                  "/favicon.svg": ("favicon.svg", "image/svg+xml")}
        try:
            if path in static:
                name, mime = static[path]
                self.send_bytes(200, (WEB_ROOT / name).read_bytes(), mime)
                return
            query = parse_qs(urlsplit(self.path).query)
            if path == "/api/pose-sessions":
                self.send_json(200, self.server.poses.list_sessions())
                return
            pose_match = re.fullmatch(r"/api/pose-sessions/([a-f0-9]{32})", path)
            if pose_match:
                try:
                    after = int(query.get("after", ["-1"])[0])
                    limit = int(query.get("limit", ["1000"])[0])
                    if after < -1 or not 1 <= limit <= 2000:
                        raise ValueError()
                except ValueError:
                    self.send_json(400, {"error": "invalid_query"})
                    return
                result = self.server.poses.points(pose_match[1], after, limit)
                self.send_json(200 if result is not None else 404, result or {"error": "session_not_found"})
                return
            if path == "/api/captures":
                try:
                    limit = int(query.get("limit", ["50"])[0])
                    before = int(query["before"][0]) if "before" in query else None
                    search = query.get("q", [""])[0].strip()
                    if not 1 <= limit <= 100 or (before is not None and before < 1) or len(search) > 128:
                        raise ValueError()
                except ValueError:
                    self.send_json(400, {"error": "invalid_query"})
                    return
                self.send_json(200, self.server.store.list_captures(limit, before, search))
                return
            match = re.fullmatch(r"/api/captures/([a-f0-9]{32})(/image)?", path)
            if match:
                record = self.server.store.get_capture(match[1])
                if record:
                    if match[2]:
                        filename = f"{record['serial']}-{record['captureId']}.jpg" if "download" in query else None
                        self.send_bytes(200, self.server.store.image_path(record).read_bytes(), "image/jpeg", filename)
                    else:
                        self.send_json(200, record)
                    return
            self.send_json(404, {"error": "not_found"})
        except FileNotFoundError:
            self.send_json(404, {"error": "file_not_found"})
        except (OSError, sqlite3.Error):
            self.send_json(500, {"error": "viewer_read_failed"})

    def log_message(self, format, *args):
        # One-second dashboard polling should not bury actual upload diagnostics.
        if self.command == "GET" and len(args) > 1 and str(args[1]) == "200":
            return
        if self.command == "POST" and self.path == "/api/poses" and len(args) > 1 and str(args[1]) == "200":
            return
        super().log_message(format, *args)

    def do_DELETE(self):
        if self.path != "/api/captures":
            self.send_json(404, {"error": "not_found"})
            return
        # A non-simple request header prevents cross-origin forms from clearing history.
        if not self.viewer_allowed() or self.headers.get("X-Demo-Action") != "clear-history":
            self.send_json(403, {"error": "clear_not_allowed"})
            return
        acquired = 0
        try:
            for _ in range(MAX_UPLOADS):
                if not self.server.upload_slots.acquire(blocking=False):
                    self.send_json(409, {"error": "uploads_in_progress"})
                    return
                acquired += 1
            self.send_json(200, self.server.store.clear_history())
        except (OSError, sqlite3.Error):
            self.send_json(500, {"error": "clear_failed"})
        finally:
            for _ in range(acquired):
                self.server.upload_slots.release()

    def do_POST(self):
        if self.path not in ("/api/captures", "/api/poses"):
            self.reject_upload(404, {"error": "not_found"})
            return
        authorization = self.headers.get("Authorization", "")
        if not hmac.compare_digest(authorization.encode("utf-8"),
                                   ("Bearer " + self.server.api_token).encode("utf-8")):
            self.reject_upload(401, {"error": "unauthorized"})
            return
        if self.path == "/api/poses":
            self.receive_poses()
            return
        capture_id = self.headers.get("X-Capture-Id", "")
        if not CAPTURE_ID.fullmatch(capture_id):
            self.reject_upload(400, {"error": "invalid_capture_id"})
            return
        if self.headers.get_content_type() != "image/jpeg":
            self.reject_upload(415, {"error": "expected_image_jpeg"})
            return
        if self.headers.get("Transfer-Encoding"):
            self.send_json(400, {"error": "use_content_length"})
            return
        try:
            size = int(self.headers["Content-Length"])
        except (TypeError, ValueError):
            self.send_json(411, {"error": "content_length_required"})
            return
        if not 4 <= size <= MAX_IMAGE_BYTES:
            self.send_json(413, {"error": "image_size_out_of_range"})
            return
        if not self.server.upload_slots.acquire(blocking=False):
            self.reject_upload(503, {"error": "busy_try_again"})
            return
        started = time.monotonic()
        attempt_id = None
        response_sent = False
        try:
            image = self.rfile.read(size)
            if len(image) != size:
                self.send_json(400, {"error": "incomplete_image"})
                return
            # This stub stores photos without decoding or running inference.
            if not (image.startswith(b"\xff\xd8\xff") and image.endswith(b"\xff\xd9")):
                self.send_json(400, {"error": "invalid_jpeg_envelope"})
                return
            try:
                response = self.server.store.save(capture_id, image)
            except ValueError as error:
                self.send_json(409, {"error": str(error)})
                return
            attempt_id = self.server.store.begin_attempt(capture_id, self.client_address[0], response["cached"])
            if not response["cached"]:
                time.sleep(self.server.delay_seconds)
            response["processingMs"] = round((time.monotonic() - started) * 1000)
            self.server.store.prepare_response(attempt_id, 200, response)
            response_sent = True
            sent = self.send_json(200, response)
            self.server.store.finish_attempt(attempt_id, "responded" if sent else "disconnected",
                                             None if sent else "Response connection closed during write.")
            print(f"Stored {response['serial']}: {size} bytes, {response['processingMs']} ms", flush=True)
        except (socket.timeout, TimeoutError):
            self.send_json(408, {"error": "upload_timeout"})
        except (OSError, sqlite3.Error) as error:
            print(f"Storage error: {type(error).__name__}", flush=True)
            if attempt_id is not None:
                try:
                    self.server.store.finish_attempt(attempt_id, "failed", type(error).__name__)
                except sqlite3.Error:
                    pass
            if not response_sent:
                self.send_json(500, {"error": "storage_failure"})
        finally:
            self.server.upload_slots.release()

    def receive_poses(self):
        if self.headers.get_content_type() != "application/json":
            self.reject_upload(415, {"error": "expected_json"})
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            size = 0
        if not 1 <= size <= MAX_POSE_BYTES or self.headers.get("Transfer-Encoding"):
            self.send_json(413, {"error": "pose_batch_size_out_of_range"})
            return
        if not self.server.pose_slots.acquire(blocking=False):
            self.reject_upload(503, {"error": "busy_try_again"})
            return
        try:
            raw = self.rfile.read(size)
            if len(raw) != size:
                raise ValueError("incomplete_pose_batch")
            result = self.server.poses.save(json.loads(raw), self.client_address[0])
            self.send_json(200, result)
        except (UnicodeDecodeError, ValueError) as error:
            conflict = str(error) in ("pose_identity_conflict", "pose_sample_conflict")
            self.send_json(409 if conflict else 400, {"error": str(error)[:128]})
        except (socket.timeout, TimeoutError):
            self.send_json(408, {"error": "pose_upload_timeout"})
        except (OSError, sqlite3.Error):
            self.send_json(500, {"error": "pose_storage_failure"})
        finally:
            self.server.pose_slots.release()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=DEFAULT_CONFIG_PATH,
                        help="receiver JSON file (default: config/server.local.json in this project)")
    parser.add_argument("--init-config", action="store_true",
                        help="create a local config with a random token, then exit; never overwrite")
    parser.add_argument("--open-browser", action="store_true",
                        help="open the local web monitor after starting the receiver")
    parser.add_argument("--no-browser", dest="open_browser", action="store_false",
                        help="disable automatic browser opening when using RunServer scripts")
    args = parser.parse_args(argv)
    if sys.version_info < (3, 11):
        parser.error("Python 3.11 or newer is required.")
    if args.init_config:
        try:
            path = initialize_config(args.config)
        except FileExistsError:
            parser.error("Configuration already exists; it was kept unchanged. Omit --init-config to run.")
        except OSError:
            parser.error("Cannot create configuration; check the destination and write permissions.")
        print(f"Created receiver configuration: {path}", flush=True)
        print("Pair the HoloLens client with the same apiToken and this computer's LAN address.", flush=True)
        print("The token was saved to the local file and is not printed. Run again without --init-config to start.", flush=True)
        return
    try:
        config = load_config(args.config)
    except FileNotFoundError:
        parser.error("Configuration not found. Copy the paired server.local.json here, "
                     "or run with --init-config for a new setup.")
    except json.JSONDecodeError:
        parser.error("Configuration is not valid JSON.")
    except (ValueError, UnicodeError) as error:
        parser.error(str(error))
    except OSError:
        parser.error("Cannot read configuration; check the file permissions.")
    data_directory = config["dataDirectory"]
    try:
        server = CaptureServer((config["host"], config["port"]), CaptureStore(data_directory),
                               config["apiToken"], config["delaySeconds"])
    except OSError:
        parser.error("Cannot start receiver; check the listen address, port availability and data-directory permissions.")
    server.store.recover_pending()
    print(f"Photo receiver listening on port {server.server_port}; delay={server.delay_seconds}s", flush=True)
    print(f"Photos and serial database: {data_directory}", flush=True)
    print(f"Browser monitor: http://127.0.0.1:{server.server_port}/", flush=True)
    print("Press Ctrl+C in this terminal to stop the receiver.", flush=True)
    if args.open_browser:
        def open_monitor():
            try:
                webbrowser.open(f"http://127.0.0.1:{server.server_port}/", new=2)
            except (OSError, webbrowser.Error):
                print("Open the browser monitor URL manually on this computer.", flush=True)
        threading.Thread(target=open_monitor, daemon=True).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
