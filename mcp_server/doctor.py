"""Local diagnostics for the HDT snapshot bridge (never prints private card data)."""
from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path
from typing import Any

from .server import choice_payload, read_snapshot, snapshot_path


def build_report(path: Path | None = None, now: datetime | None = None) -> dict[str, Any]:
    target = path if path is not None else snapshot_path()
    snapshot = read_snapshot(target, now=now)
    state = snapshot["state"]
    result: dict[str, Any] = {
        "status": "ready" if snapshot["available"]
                  else "waiting_for_match" if snapshot["code"] == "NOT_IN_GAME"
                  else "error",
        "code": snapshot["code"],
        "tracker_connected": snapshot["tracker_connected"],
        "game_available": snapshot["available"],
        "age_seconds": snapshot["age_seconds"],
        "state_path": str(target),
        "reason": snapshot["reason"],
        "next_step": "",
        "warnings": [],
    }
    steps = {
        "MISSING_STATE": "Uruchom Hearthstone Deck Tracker, wlacz plugin Hearthstone MCP i sprawdz jego instalacje.",
        "NOT_IN_GAME": "Wtyczka wysyla dane. Rozpocznij mecz Hearthstone i ponow diagnostyke.",
        "STALE_STATE": "Dane sa stare. Uruchom ponownie HDT i sprawdz, czy plugin jest wlaczony.",
        "INVALID_SCHEMA": "Format danych jest niezgodny. Zaktualizuj razem wtyczke i serwer MCP.",
        "INVALID_TIMESTAMP": "Sprawdz date i godzine systemowa oraz wersje wtyczki.",
        "CLOCK_SKEW": "Sprawdz zegar i strefe czasowa Windows.",
        "INCOMPLETE_STATE": "HDT nie podal pelnego stanu. Sprawdz logi HDT i wznow mecz.",
        "OVERSIZED_STATE": "Plik danych przekracza limit bezpieczenstwa. Sprawdz wtyczke.",
        "READ_ERROR": "Sprawdz uprawnienia do pliku stanu i logi HDT.",
    }
    if state is None:
        result["next_step"] = steps.get(snapshot["code"], "Sprawdz status HDT i pluginu.")
        return result
    player = state["player"]
    opponent = state["opponent"]
    hand = player.get("hand")
    my_board = player.get("board")
    enemy_board = opponent.get("board")
    choices = choice_payload(state)
    result.update({
        "choice_tracking_supported": choices["choice_tracking_supported"],
        "choice_active": choices["choice_active"],
        "choice_count": choices["choice_count"],
    })
    result.update({
        "turn": state.get("turn"),
        "my_turn": state.get("my_turn"),
        "mana_available": player.get("mana_available"),
        "hand_count": len(hand) if isinstance(hand, list) else None,
        "my_board_count": len(my_board) if isinstance(my_board, list) else None,
        "opponent_board_count": len(enemy_board) if isinstance(enemy_board, list) else None,
    })
    if not isinstance(hand, list):
        result["warnings"].append("Brak listy kart na rece.")
    if not isinstance(my_board, list) or not isinstance(enemy_board, list):
        result["warnings"].append("Brak danych o jednostkach na stole.")
    if player.get("mana_available") is None:
        result["warnings"].append("Brak danych o dostepnej manie.")
    if not choices["choice_tracking_supported"]:
        result["next_step"] = "Odczyt meczu dziala, ale wykrywanie Odkryj wymaga aktualizacji DLL wtyczki HDT."
    elif choices["choice_active"]:
        result["next_step"] = "Widoczny wybor kart. Zapytaj MCP: Co wybrac? (get_hearthstone_choices)."
    elif state.get("my_turn") is not True:
        result["next_step"] = "Odczyt dziala. Poczekaj na swoja ture, by poprosic o zagranie."
    else:
        result["next_step"] = "Dane sa aktualne. Mozna zapytac klienta MCP: Co teraz zagrac?"
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diagnostyka odczytu Hearthstone przez HDT.")
    parser.add_argument("--state-path", type=Path, help="Niestandardowa sciezka pliku state.json")
    parser.add_argument("--json", action="store_true", help="Raport w formacie JSON")
    args = parser.parse_args(argv)
    report = build_report(args.state_path)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print("Hearthstone MCP — diagnostyka")
        print("Plik: " + report["state_path"])
        print("Status: " + report["status"] + " (" + report["code"] + ")")
        print("HDT / wtyczka: " + ("polaczone" if report["tracker_connected"] else "niepotwierdzone"))
        if report["age_seconds"] is not None:
            print("Wiek danych: " + str(report["age_seconds"]) + " s")
        if report["game_available"]:
            print("Tura: " + str(report["turn"]) + ", moja: " + str(report["my_turn"]))
            print("Mana: " + str(report["mana_available"]))
            print("Reka: " + str(report["hand_count"]) + " kart; stol: "
                  + str(report["my_board_count"]) + " / " + str(report["opponent_board_count"]))
            print("Odczyt wyborow: " + ("obslugiwany" if report["choice_tracking_supported"] else "wymaga nowej DLL"))
            if report["choice_tracking_supported"]:
                print("Aktywny wybor: " + str(report["choice_active"])
                      + "; opcji: " + str(report["choice_count"]))
        for warning in report["warnings"]:
            print("Uwaga: " + warning)
        print("Dalej: " + report["next_step"])
    return 0 if report["game_available"] else (2 if report["status"] == "waiting_for_match" else 1)


if __name__ == "__main__":
    raise SystemExit(main())
