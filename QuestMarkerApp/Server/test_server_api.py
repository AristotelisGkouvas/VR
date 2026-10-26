#!/usr/bin/env python3
"""Flask API contract tests (no Quest required)."""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

# Load coordinate_server as module
SERVER_PATH = Path(__file__).resolve().parent / "coordinate_server.py"
spec = importlib.util.spec_from_file_location("coordinate_server", SERVER_PATH)
mod = importlib.util.module_from_spec(spec)
sys.modules["coordinate_server"] = mod
# Prevent running main
os.environ.setdefault("QUEST_IP", "127.0.0.1")
spec.loader.exec_module(mod)

mod.API_KEY = ""
mod.RATE_LIMIT = 0  # disable for tests


class ApiTests(unittest.TestCase):
    def setUp(self):
        mod.app.config["TESTING"] = True
        self.client = mod.app.test_client()
        with mod._store_lock:
            mod._coordinates_store.clear()
            mod._quest_polls_in_flight = 0
            mod._quest_last_poll = 0.0

    def test_health(self):
        r = self.client.get("/api/health")
        self.assertEqual(r.status_code, 200)
        data = r.get_json()
        self.assertEqual(data["status"], "healthy")

    def test_post_and_get_coordinates(self):
        payload = {
            "coordinates": [
                {"id": "t1", "x": 1, "y": 0.5, "z": 2, "label": "A", "color": "#FF0000"}
            ],
            "sendToQuest": False,
        }
        r = self.client.post("/api/coordinates", json=payload)
        self.assertEqual(r.status_code, 200)
        self.assertTrue(r.get_json()["success"])

        r2 = self.client.get("/api/coordinates")
        data = r2.get_json()
        self.assertEqual(data["count"], 1)
        self.assertEqual(data["coordinates"][0]["id"], "t1")

    def test_clear(self):
        self.client.post(
            "/api/coordinates",
            json={"coordinates": [{"x": 0, "y": 0, "z": 1}], "sendToQuest": False},
        )
        r = self.client.post("/api/clear")
        self.assertEqual(r.status_code, 200)
        r2 = self.client.get("/api/coordinates")
        self.assertEqual(r2.get_json()["count"], 0)

    def test_delete(self):
        self.client.post(
            "/api/coordinates",
            json={
                "coordinates": [{"id": "delme", "x": 0, "y": 0, "z": 1}],
                "sendToQuest": False,
            },
        )
        r = self.client.delete("/api/coordinates/delme")
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.get_json()["removed_local"], 1)

    def test_auth_required(self):
        old = mod.API_KEY
        mod.API_KEY = "secret"
        try:
            r = self.client.post(
                "/api/coordinates",
                json={"coordinates": [{"x": 0, "y": 0, "z": 1}], "sendToQuest": False},
            )
            self.assertEqual(r.status_code, 401)
            r2 = self.client.post(
                "/api/coordinates",
                json={"coordinates": [{"x": 0, "y": 0, "z": 1}], "sendToQuest": False},
                headers={"X-Api-Key": "secret"},
            )
            self.assertEqual(r2.status_code, 200)
            # health stays open
            self.assertEqual(self.client.get("/api/health").status_code, 200)
        finally:
            mod.API_KEY = old

    def test_events_timeout(self):
        r = self.client.get("/api/events?since=9999999999999&timeout=0.2")
        self.assertEqual(r.status_code, 200)
        self.assertTrue(r.get_json().get("timeout"))

    # ---- robustness / contract fixes ------------------------------------------------------

    def test_non_object_bodies_are_400_not_500(self):
        for body in ([1, 2], "text", 5):
            r = self.client.post("/api/coordinates", json=body)
            self.assertEqual(r.status_code, 400, body)
        r = self.client.post("/api/coordinates", json={"coordinates": ["not-an-object"], "sendToQuest": False})
        self.assertEqual(r.status_code, 400)
        r = self.client.post("/api/calibrate", json=[1])
        self.assertEqual(r.status_code, 400)

    def test_non_finite_coordinate_rejected(self):
        r = self.client.post("/api/coordinates", data='{"coordinates":[{"x":NaN,"y":0,"z":0}],"sendToQuest":false}',
                             content_type="application/json")
        self.assertEqual(r.status_code, 400)
        r = self.client.post("/api/coordinates", json={"coordinates": [{"x": "1", "y": 0, "z": 0}], "sendToQuest": False})
        self.assertEqual(r.status_code, 400)

    def test_calibrate_rejects_nan(self):
        r = self.client.post("/api/calibrate", json={"realX": "nan", "realZ": 1})
        self.assertEqual(r.status_code, 400)

    def test_same_id_updates_instead_of_duplicating(self):
        for x in (1, 2):
            self.client.post("/api/coordinates",
                             json={"coordinates": [{"id": "dup", "x": x, "y": 0, "z": 0}], "sendToQuest": False})
        data = self.client.get("/api/coordinates").get_json()
        self.assertEqual(data["count"], 1)
        self.assertEqual(data["coordinates"][0]["x"], 2)

    def test_remove_missing_keeps_only_sent_ids(self):
        self.client.post("/api/coordinates", json={"coordinates": [{"id": "a", "x": 0, "y": 0, "z": 0},
                                                                    {"id": "b", "x": 0, "y": 0, "z": 0}],
                                                    "sendToQuest": False})
        self.client.post("/api/coordinates", json={"coordinates": [{"id": "b", "x": 1, "y": 0, "z": 0}],
                                                    "removeMissing": True, "sendToQuest": False})
        ids = [c["id"] for c in self.client.get("/api/coordinates").get_json()["coordinates"]]
        self.assertEqual(ids, ["b"])

    def test_events_bad_query_is_400(self):
        self.assertEqual(self.client.get("/api/events?since=abc").status_code, 400)
        self.assertEqual(self.client.get("/api/events?timeout=xyz").status_code, 400)
        self.assertEqual(self.client.get("/api/events?timeout=nan").status_code, 400)

    def test_event_stamps_strictly_increase(self):
        with mod._store_lock:
            stamps = []
            for _ in range(5):
                mod._notify_events()
                stamps.append(mod._last_event_ts)
        self.assertEqual(stamps, sorted(set(stamps)))

    def test_events_returns_snapshot_after_change(self):
        since = mod._last_event_ts
        self.client.post("/api/coordinates", json={"coordinates": [{"id": "e1", "x": 0, "y": 0, "z": 0}],
                                                    "sendToQuest": False})
        data = self.client.get(f"/api/events?since={since}&timeout=0.2").get_json()
        self.assertGreater(data["timestamp"], since)
        self.assertTrue(data["clearExisting"])
        self.assertEqual([c["id"] for c in data["coordinates"]], ["e1"])

    def test_delete_forwards_url_encoded_id(self):
        calls = []

        def fake(method, path, **kwargs):
            calls.append((method, path))
            return {"ok": True, "status_code": 200, "body": {}}

        with patch.object(mod, "_quest_request", side_effect=fake):
            r = self.client.delete("/api/coordinates/a%20b%2Fc")
        self.assertEqual(r.status_code, 200)
        self.assertEqual(calls, [("DELETE", "/api/coordinates/a%20b%2Fc")])

    def test_bearer_auth_and_wrong_key(self):
        old = mod.API_KEY
        mod.API_KEY = "secret"
        try:
            ok = self.client.get("/api/coordinates", headers={"Authorization": "bearer secret"})
            self.assertEqual(ok.status_code, 200)
            bad = self.client.get("/api/coordinates", headers={"X-Api-Key": "secreT"})
            self.assertEqual(bad.status_code, 401)
            bad2 = self.client.get("/api/coordinates", headers={"X-Api-Key": "secret-longer"})
            self.assertEqual(bad2.status_code, 401)
        finally:
            mod.API_KEY = old

    def test_unknown_route_and_method_are_json(self):
        r = self.client.get("/api/nope")
        self.assertEqual(r.status_code, 404)
        self.assertIn("error", r.get_json())
        r = self.client.put("/api/coordinates")
        self.assertEqual(r.status_code, 405)
        self.assertIn("error", r.get_json())

    def test_rate_limit_429(self):
        old = mod.RATE_LIMIT
        mod.RATE_LIMIT = 3
        mod._rate.clear()
        try:
            codes = [self.client.get("/api/health").status_code for _ in range(4)]
            self.assertEqual(codes, [200, 200, 200, 429])
        finally:
            mod.RATE_LIMIT = old
            mod._rate.clear()

    # ---- push delivery (long-poll) ------------------------------------------------------------

    def test_sync_event_uses_remove_missing_not_clear(self):
        since = mod._last_event_ts
        self.client.post("/api/coordinates", json={"coordinates": [{"id": "s1", "x": 0, "y": 0, "z": 0}],
                                                    "sendToQuest": False})
        data = self.client.get(f"/api/events?since={since}&timeout=0.2&removeMissing=1").get_json()
        self.assertTrue(data["removeMissing"])
        self.assertFalse(data["clearExisting"])
        self.assertEqual([c["id"] for c in data["coordinates"]], ["s1"])
        legacy = self.client.get(f"/api/events?since={since}&timeout=0.2").get_json()
        self.assertTrue(legacy["clearExisting"])  # older headset builds keep clear-and-replace
        self.assertFalse(legacy["removeMissing"])

    def test_no_double_delivery_while_quest_long_polls(self):
        calls = []

        def fake(method, path, **kwargs):
            calls.append((method, path))
            return {"ok": True, "status_code": 200, "body": {}}

        old_ip = mod.QUEST_IP
        mod.QUEST_IP = "127.0.0.1"  # the test client's address plays the headset
        try:
            with patch.object(mod, "_quest_request", side_effect=fake):
                # No poll yet: forwarded directly as before.
                r = self.client.post("/api/coordinates", json={"coordinates": [{"id": "a", "x": 0, "y": 0, "z": 0}]})
                self.assertEqual(len(calls), 1)
                self.assertNotIn("delivery", r.get_json())
                self.client.get("/api/events?since=0&timeout=0&removeMissing=1")  # headset polls
                self.assertTrue(mod._quest_is_polling())
                r = self.client.post("/api/coordinates", json={"coordinates": [{"id": "b", "x": 0, "y": 0, "z": 0}]})
                self.assertEqual(len(calls), 1)  # not forwarded a second time
                self.assertEqual(r.get_json()["delivery"], "events")
                self.assertTrue(r.get_json()["success"])
        finally:
            mod.QUEST_IP = old_ip

    def test_polls_from_other_clients_do_not_stop_forwarding(self):
        old_ip = mod.QUEST_IP
        mod.QUEST_IP = "10.9.9.9"
        try:
            self.client.get("/api/events?since=0&timeout=0&removeMissing=1")  # a dashboard, not the headset
            self.assertFalse(mod._quest_is_polling())
        finally:
            mod.QUEST_IP = old_ip

    # ---- browser / DNS-rebinding gate (keyless bridge) -------------------------------------

    def test_keyless_bridge_refuses_foreign_host_header(self):
        # DNS rebinding: evil.example re-resolved to 127.0.0.1 still sends Host: evil.example.
        for host in ("evil.example:5000", "192.168.1.50:5000", "127.0.0.1.evil.example"):
            r = self.client.get("/api/session/quest", headers={"Host": host})
            self.assertEqual(r.status_code, 403, host)
        for host in ("127.0.0.1:5000", "localhost:5000", "[::1]:5000", "LOCALHOST"):
            r = self.client.get("/api/coordinates", headers={"Host": host})
            self.assertEqual(r.status_code, 200, host)

    def test_keyless_bridge_refuses_cross_site_post(self):
        calls = []
        with patch.object(mod, "_quest_request", side_effect=lambda *a, **k: calls.append(a) or
                          {"ok": True, "status_code": 200, "body": {}}):
            r = self.client.post("/api/calibrate/reset", headers={"Origin": "https://evil.example"})
            self.assertEqual(r.status_code, 403)
            r = self.client.post("/api/clear", headers={"Origin": "null"})  # sandboxed iframe / file://
            self.assertEqual(r.status_code, 403)
            self.assertEqual(calls, [])  # nothing reached the headset
            r = self.client.post("/api/clear", headers={"Origin": "http://localhost:5000"})
            self.assertEqual(r.status_code, 200)
        old = mod.CORS_ENABLED
        mod.CORS_ENABLED = True
        try:
            with patch.object(mod, "_quest_request", return_value={"ok": True, "status_code": 200, "body": {}}):
                r = self.client.post("/api/clear", headers={"Origin": "https://dashboard.example"})
            self.assertEqual(r.status_code, 200)  # --cors is the explicit opt-in
        finally:
            mod.CORS_ENABLED = old

    def test_keyed_bridge_skips_host_check(self):
        # Listening on the LAN needs --api-key; then any Host is fine because the key is required.
        old = mod.API_KEY
        mod.API_KEY = "secret"
        try:
            r = self.client.get("/api/coordinates", headers={"Host": "192.168.1.20:5000", "X-Api-Key": "secret"})
            self.assertEqual(r.status_code, 200)
            r = self.client.get("/api/coordinates", headers={"Host": "192.168.1.20:5000",
                                                             "Origin": "https://evil.example"})
            self.assertEqual(r.status_code, 401)
        finally:
            mod.API_KEY = old

    def test_gps_origin_validation(self):
        r = self.client.post("/api/gps-origin", json={"latitude": 123, "longitude": 0})
        self.assertEqual(r.status_code, 400)
        r = self.client.post("/api/gps-origin", json={"latitude": "x", "longitude": 0})
        self.assertEqual(r.status_code, 400)


if __name__ == "__main__":
    unittest.main()
