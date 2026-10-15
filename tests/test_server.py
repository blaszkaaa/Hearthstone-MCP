"""Unit tests for the JSON-RPC MCP protocol and freshness guarantees."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from mcp_server.server import dispatch, read_snapshot, tool_payload

ROOT = Path(__file__).resolve().parents[1]


def game_state(captured=None, in_game=True):
    timestamp = (captured or datetime.now(timezone.utc)).isoformat()
    return {
        "schema_version": 1,
        "captured_at_utc": timestamp,
        "source": "hearthstone-deck-tracker-plugin",
        "in_game": in_game,
        "turn": 4,
        "my_turn": True,
        "player": {
            "mana_available": 3, "mana_max": 4,
            "hero": {"health": 25, "armor": 0},
            "hand": [{"entity_id": 19, "known": True, "name": "Fireball",
                      "card_id": "CS2_029", "cost": 4, "position": 1}],
            "board": [{"entity_id": 16, "name": "Friendly Minion", "attack": 2, "health": 3}],
            "secrets_count": 0, "played_this_turn": [],
        },
        "opponent": {
            "hero": {"health": 18, "armor": 0},
            "hand_count": 5,
            "board": [{"entity_id": 17, "name": "Enemy Minion", "attack": 4, "health": 2}],
            "secrets_count": 1,
            "played_this_turn": ["Known Spell"],
        },
    }


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.path = Path(self.dir.name) / "state.json"

    def put(self, state):
        self.path.write_text(json.dumps(state, ensure_ascii=False), encoding="utf-8")

    def test_missing_file_never_claims_availability(self):
        result = read_snapshot(self.path)
        self.assertFalse(result["available"])
        self.assertIsNone(result["state"])

    def test_live_game_state(self):
        self.put(game_state())
        result = read_snapshot(self.path)
        self.assertTrue(result["available"])
        self.assertLess(result["age_seconds"], 15)
        hand = tool_payload("get_hearthstone_hand", result)
        self.assertEqual(hand["hand"][0]["name"], "Fireball")
        self.assertEqual(hand["mana_available"], 3)
        board = tool_payload("get_hearthstone_board", result)
        self.assertEqual(board["opponent"]["secrets_count"], 1)

    def test_stale_game_not_exposed_as_live(self):
        self.put(game_state(datetime.now(timezone.utc) - timedelta(seconds=40)))
        result = read_snapshot(self.path)
        self.assertFalse(result["available"])
        self.assertIn("nieaktualne", result["reason"].lower())

    def test_future_clock_rejected(self):
        self.put(game_state(datetime.now(timezone.utc) + timedelta(minutes=1)))
        self.assertFalse(read_snapshot(self.path)["available"])

    def test_menu_is_not_a_match(self):
        self.put(game_state(in_game=False))
        self.assertFalse(read_snapshot(self.path)["available"])

    def test_invalid_schema_rejected(self):
        state = game_state()
        state["schema_version"] = 500
        self.put(state)
        self.assertFalse(read_snapshot(self.path)["available"])

    def test_bad_json_rejected(self):
        self.path.write_text("{", encoding="utf-8")
        self.assertFalse(read_snapshot(self.path)["available"])

    def test_oversized_file_rejected(self):
        self.path.write_bytes(b"x" * 512_001)
        self.assertFalse(read_snapshot(self.path)["available"])

    def test_no_private_opponent_hand_card_data(self):
        self.put(game_state())
        result = read_snapshot(self.path)
        data = tool_payload("get_hearthstone_hand", result)
        self.assertEqual(data["opponent_hand_count"], 5)
        self.assertNotIn("opponent_hand", data)

    def test_jsonrpc_initialize_and_notification(self):
        response = dispatch({"jsonrpc": "2.0", "id": 1,
                             "method": "initialize",
                             "params": {"protocolVersion": "2025-06-18"}})
        self.assertEqual(response["result"]["serverInfo"]["name"], "hearthstone-mcp")
        self.assertIsNone(dispatch({"jsonrpc": "2.0", "method": "notifications/initialized"}))

    def test_jsonrpc_tool_call(self):
        self.put(game_state())
        response = dispatch({"jsonrpc": "2.0", "id": 2,
                             "method": "tools/call",
                             "params": {"name": "read_hearthstone_state", "arguments": {}}}, self.path)
        result = json.loads(response["result"]["content"][0]["text"])
        self.assertTrue(result["available"])
        self.assertEqual(result["turn"], 4)

    def test_unknown_tool_is_error(self):
        response = dispatch({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                             "params": {"name": "execute_move"}})
        self.assertEqual(response["error"]["code"], -32602)

    def test_stdio_smoke(self):
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize",
             "params": {"protocolVersion": "2025-06-18"}},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call",
             "params": {"name": "hearthstone_status", "arguments": {}}},
        ]
        env = dict(os.environ)
        env["HEARTHSTONE_MCP_STATE_PATH"] = str(self.path)
        process = subprocess.run(
            [sys.executable, "-m", "mcp_server.server"],
            input="\n".join(json.dumps(x) for x in requests) + "\n",
            capture_output=True, text=True, cwd=ROOT, env=env, timeout=10,
        )
        self.assertEqual(process.returncode, 0, process.stderr)
        lines = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertEqual(len(lines), 3)
        self.assertEqual(lines[1]["id"], 2)
        self.assertEqual(len(lines[1]["result"]["tools"]), 5)
        self.assertEqual(lines[2]["id"], 3)


if __name__ == "__main__":
    unittest.main()
