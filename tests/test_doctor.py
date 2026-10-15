"""Tests for the user-facing HDT connection diagnostics."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from mcp_server.doctor import build_report

ROOT = Path(__file__).resolve().parents[1]


def sample_state(*, in_game=True, offset_seconds=0, mana=3):
    return {
        "schema_version": 1,
        "source": "hearthstone-deck-tracker-plugin",
        "captured_at_utc": (datetime.now(timezone.utc) + timedelta(seconds=offset_seconds)).isoformat(),
        "in_game": in_game,
        "turn": 7,
        "my_turn": True,
        "player": {
            "mana_available": mana, "mana_max": 7,
            "hand": [{"entity_id": 1, "name": "SECRET TEST CARD", "cost": 2}],
            "board": [{"entity_id": 2, "name": "FRIENDLY TEST MINION"}],
            "hero": {"health": 20},
        },
        "opponent": {
            "hand_count": 3, "board": [],
            "hero": {"health": 14},
        },
    }


class DoctorTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.path = Path(tmp.name) / "state.json"

    def put(self, state):
        self.path.write_text(json.dumps(state), encoding="utf-8")

    def test_missing_snapshot_has_actionable_code(self):
        report = build_report(self.path)
        self.assertEqual(report["code"], "MISSING_STATE")
        self.assertEqual(report["status"], "error")
        self.assertFalse(report["tracker_connected"])
        self.assertFalse(report["game_available"])
        self.assertIn("Hearthstone Deck Tracker", report["next_step"])

    def test_fresh_menu_snapshot_means_plugin_connected(self):
        self.put(sample_state(in_game=False))
        report = build_report(self.path)
        self.assertEqual(report["status"], "waiting_for_match")
        self.assertEqual(report["code"], "NOT_IN_GAME")
        self.assertTrue(report["tracker_connected"])
        self.assertFalse(report["game_available"])

    def test_fresh_game_summary_does_not_leak_card_names(self):
        self.put(sample_state())
        report = build_report(self.path)
        self.assertEqual(report["status"], "ready")
        self.assertTrue(report["tracker_connected"])
        self.assertEqual(report["mana_available"], 3)
        self.assertEqual(report["hand_count"], 1)
        self.assertEqual(report["my_board_count"], 1)
        self.assertNotIn("SECRET TEST CARD", json.dumps(report))

    def test_stale_file_is_not_marked_connected(self):
        self.put(sample_state(offset_seconds=-60))
        report = build_report(self.path)
        self.assertEqual(report["code"], "STALE_STATE")
        self.assertFalse(report["tracker_connected"])

    def test_unknown_mana_is_reported_not_guessed(self):
        self.put(sample_state(mana=None))
        report = build_report(self.path)
        self.assertIsNone(report["mana_available"])
        self.assertTrue(any("manie" in item for item in report["warnings"]))

    def test_cli_json_exit_codes(self):
        env = dict(os.environ)
        def run():
            return subprocess.run(
                [sys.executable, "-m", "mcp_server.doctor", "--state-path", str(self.path), "--json"],
                cwd=ROOT, env=env, text=True, capture_output=True, timeout=10,
            )
        missing = run()
        self.assertEqual(missing.returncode, 1, missing.stderr)
        self.assertEqual(json.loads(missing.stdout)["code"], "MISSING_STATE")
        self.put(sample_state(in_game=False))
        menu = run()
        self.assertEqual(menu.returncode, 2, menu.stderr)
        self.put(sample_state())
        live = run()
        self.assertEqual(live.returncode, 0, live.stderr)
        self.assertEqual(json.loads(live.stdout)["status"], "ready")


if __name__ == "__main__":
    unittest.main()
