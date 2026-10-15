"""Local, dependency-free MCP stdio server for Hearthstone Deck Tracker snapshots.

The HDT plugin writes a JSON snapshot; this process provides only the observed
(public-to-player) state. It never reads game memory or controls Hearthstone.
"""
from __future__ import annotations

import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SERVER_NAME = "hearthstone-mcp"
SERVER_VERSION = "0.1.0"
PROTOCOL_VERSION = "2025-06-18"
MAX_SNAPSHOT_BYTES = 512_000
MAX_AGE_SECONDS = 15.0


def snapshot_path() -> Path:
    explicit = os.environ.get("HEARTHSTONE_MCP_STATE_PATH")
    if explicit:
        return Path(explicit).expanduser()
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "HearthstoneMCP" / "state.json"
    return Path.home() / ".hearthstone-mcp" / "state.json"


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _unavailable(
    reason: str, age: float | None = None, *,
    code: str = "UNAVAILABLE", tracker_connected: bool = False,
) -> dict[str, Any]:
    return {
        "available": False,
        "tracker_connected": tracker_connected,
        "code": code,
        "age_seconds": age,
        "reason": reason,
        "state": None,
    }


def read_snapshot(path: Path | None = None, now: datetime | None = None) -> dict[str, Any]:
    target = path or snapshot_path()
    reference = now or utc_now()
    try:
        if not target.is_file():
            return _unavailable("Brak danych. Uruchom HDT, wlacz wtyczke i rozpocznij mecz.", code="MISSING_STATE")
        if target.stat().st_size > MAX_SNAPSHOT_BYTES:
            return _unavailable("Plik danych jest zbyt duzy; odczyt zablokowany.", code="OVERSIZED_STATE")
        with target.open("r", encoding="utf-8-sig") as handle:
            state = json.load(handle)
        if not isinstance(state, dict) or state.get("schema_version") != 1:
            return _unavailable("Nieobslugiwany format danych wtyczki.", code="INVALID_SCHEMA")
        when = state.get("captured_at_utc")
        if not isinstance(when, str):
            return _unavailable("Brak poprawnego znacznika czasu.", code="INVALID_TIMESTAMP")
        captured = datetime.fromisoformat(when.replace("Z", "+00:00"))
        if captured.tzinfo is None:
            return _unavailable("Znacznik czasu nie ma strefy UTC.", code="INVALID_TIMESTAMP")
        age = round((reference - captured).total_seconds(), 2)
        if age < -5:
            return _unavailable("Nieprawidlowy zegar systemowy: dane sa z przyszlosci.", age, code="CLOCK_SKEW")
        if age > MAX_AGE_SECONDS:
            return _unavailable("Dane nieaktualne. Sprawdz czy Hearthstone Deck Tracker dziala.", age, code="STALE_STATE")
        if state.get("in_game") is not True:
            return _unavailable("Hearthstone jest w menu lub nie ma aktywnego meczu.", age, code="NOT_IN_GAME", tracker_connected=True)
        # Defensive: refuse a malformed game state rather than hallucinate cards.
        if not isinstance(state.get("player"), dict) or not isinstance(state.get("opponent"), dict):
            return _unavailable("Niekompletny stan rozgrywki.", age, code="INCOMPLETE_STATE", tracker_connected=True)
        return {"available": True, "tracker_connected": True, "code": "IN_GAME", "age_seconds": age, "reason": None, "state": state}
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
        return _unavailable("Nie udalo sie odczytac danych: " + type(exc).__name__, code="READ_ERROR")


def tool_payload(name: str, snapshot: dict[str, Any]) -> dict[str, Any]:
    if name == "hearthstone_status":
        return {
            "available": snapshot["available"],
            "tracker_connected": snapshot["tracker_connected"],
            "code": snapshot["code"],
            "age_seconds": snapshot["age_seconds"],
            "reason": snapshot["reason"],
            "state_path": str(snapshot_path()),
        }
    if not snapshot["available"]:
        return {k: snapshot[k] for k in ("available", "tracker_connected", "code", "age_seconds", "reason")}
    state = snapshot["state"]
    shared = {
        "available": True,
        "age_seconds": snapshot["age_seconds"],
        "turn": state.get("turn"),
        "my_turn": state.get("my_turn"),
    }
    player = state["player"]
    opponent = state["opponent"]
    if name == "read_hearthstone_state":
        return {**shared, "state": state}
    if name == "get_hearthstone_advice_context":
        mana = player.get("mana_available")
        hand = player.get("hand", [])
        known_affordable = []
        unknown_cost = []
        too_expensive = []
        for card in hand if isinstance(hand, list) else []:
            if not isinstance(card, dict):
                continue
            cost = card.get("cost")
            if isinstance(cost, bool) or not isinstance(cost, int) or cost < 0:
                unknown_cost.append(card)
            elif isinstance(mana, int) and not isinstance(mana, bool):
                (known_affordable if cost <= mana else too_expensive).append(card)
            else:
                unknown_cost.append(card)
        return {
            **shared,
            "mana_available": mana,
            "mana_max": player.get("mana_max"),
            "my_hero": player.get("hero"),
            "opponent_hero": opponent.get("hero"),
            "my_hero_power": player.get("hero_power"),
            "opponent_hero_power": opponent.get("hero_power"),
            "my_weapon": player.get("weapon"),
            "opponent_weapon": opponent.get("weapon"),
            "my_deck_count": player.get("deck_count"),
            "opponent_deck_count": opponent.get("deck_count"),
            "my_board": player.get("board", []),
            "opponent_board": opponent.get("board", []),
            "opponent_hand_count": opponent.get("hand_count"),
            "opponent_secrets_count": opponent.get("secrets_count"),
            "my_secrets_count": player.get("secrets_count"),
            "played_this_turn": player.get("played_this_turn", []),
            "opponent_played_this_turn": opponent.get("played_this_turn", []),
            "hand": hand,
            "cards_with_affordable_mana_cost": known_affordable,
            "cards_with_unknown_cost": unknown_cost,
            "cards_above_available_mana": too_expensive,
            "caveats": [
                "Koszt karty nie gwarantuje, ze mozna ja zagrac (cele, warunki, efekty).",
                "Nie znamy ukrytej reki przeciwnika ani wszystkich mozliwych efektow.",
                "Jesli my_turn=false, nie proponuj natychmiastowego zagrania.",
                "Widoczna moc bohatera i bron nie gwarantuja legalnosci ich uzycia w tej turze."
            ],
        }
    if name == "get_hearthstone_hand":
        return {
            **shared,
            "mana_available": player.get("mana_available"),
            "mana_max": player.get("mana_max"),
            "hand": player.get("hand", []),
            "opponent_hand_count": opponent.get("hand_count"),
        }
    if name == "get_hearthstone_board":
        return {
            **shared,
            "player": {
                "hero": player.get("hero"),
                "hero_power": player.get("hero_power"),
                "weapon": player.get("weapon"),
                "deck_count": player.get("deck_count"),
                "board": player.get("board", []),
                "secrets_count": player.get("secrets_count"),
            },
            "opponent": {
                "hero": opponent.get("hero"),
                "hero_power": opponent.get("hero_power"),
                "weapon": opponent.get("weapon"),
                "deck_count": opponent.get("deck_count"),
                "board": opponent.get("board", []),
                "secrets_count": opponent.get("secrets_count"),
            },
        }
    if name == "get_hearthstone_plays":
        return {
            **shared,
            "player_played_this_turn": player.get("played_this_turn", []),
            "opponent_played_this_turn": opponent.get("played_this_turn", []),
        }
    raise KeyError(name)


TOOLS = [
    {
        "name": "hearthstone_status",
        "description": "Sprawdz polaczenie z Hearthstone, status meczu i swiezosc danych.",
    },
    {
        "name": "read_hearthstone_state",
        "description": "Odczytaj dostepny stan aktywnego meczu Hearthstone: tura, mana, reka, stol, bohaterowie, jawne zagrania. Nigdy nie zgaduj ukrytych kart. Jesli available=false, nie polecaj ruchu jako pewnego.",
    },
    {
        "name": "get_hearthstone_advice_context",
        "description": "Najwazniejsze aktualne informacje do odpowiedzi 'co zagrac teraz?': reka, mana, stol i szacunkowa dostepnosc kart wg kosztu. Koszt nie gwarantuje legalnosci ruchu. Podaj uzasadnienie i zaznacz brakujace informacje.",
    },
    {
        "name": "get_hearthstone_hand",
        "description": "Wyswietl karty na mojej rece i pozostala mane; identyfikatory kart i pozycje do rekomendacji.",
    },
    {
        "name": "get_hearthstone_board",
        "description": "Pokaz aktualny stol obu graczy, statystyki stronnikow i bohaterow.",
    },
    {
        "name": "get_hearthstone_plays",
        "description": "Pokaz znane karty zagrane w obecnej turze przez obu graczy.",
    },
]
for tool in TOOLS:
    tool["inputSchema"] = {"type": "object", "properties": {}, "additionalProperties": False}


def dispatch(message: dict[str, Any], path: Path | None = None) -> dict[str, Any] | None:
    """Handle one MCP JSON-RPC request; notifications must not get responses."""
    if not isinstance(message, dict):
        return {"jsonrpc": "2.0", "id": None, "error": {"code": -32600, "message": "Invalid Request"}}
    if "id" not in message:
        return None
    request_id = message["id"]
    method = message.get("method")
    if method == "initialize":
        result = {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        }
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": TOOLS}
    elif method == "tools/call":
        params = message.get("params")
        if not isinstance(params, dict) or params.get("name") not in {t["name"] for t in TOOLS}:
            return {
                "jsonrpc": "2.0", "id": request_id,
                "error": {"code": -32602, "message": "Unknown or missing tool name"},
            }
        if params.get("arguments", {}) not in ({}, None):
            return {
                "jsonrpc": "2.0", "id": request_id,
                "error": {"code": -32602, "message": "This tool does not accept arguments"},
            }
        snapshot = read_snapshot(path)
        data = tool_payload(params["name"], snapshot)
        result = {"content": [{"type": "text", "text": json.dumps(data, ensure_ascii=False)}], "isError": False}
    else:
        return {
            "jsonrpc": "2.0", "id": request_id,
            "error": {"code": -32601, "message": "Method not found"},
        }
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def main() -> int:
    # stdout belongs exclusively to newline-delimited MCP messages.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            incoming = json.loads(line)
            response = dispatch(incoming)
        except (ValueError, TypeError) as exc:
            response = {
                "jsonrpc": "2.0", "id": None,
                "error": {"code": -32700, "message": "Parse error: " + type(exc).__name__},
            }
        if response is not None:
            sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
