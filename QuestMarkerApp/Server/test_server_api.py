#!/usr/bin/env python3
"""Flask API contract tests (no Quest required)."""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest
from pathlib import Path

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


if __name__ == "__main__":
    unittest.main()
