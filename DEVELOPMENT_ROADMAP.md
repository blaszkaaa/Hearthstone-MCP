# Plan rozwoju Hearthstone AI Coach — 10 etapów

Zasady: zachowujemy ciemny wygląd, brak automatycznego sterowania grą i brak dostępu do ukrytej ręki przeciwnika. Każdy etap wymaga testów C#, kompilacji aplikacji Windows, a po zmianach wtyczki także kompilacji HDT. Ukończenie etapu oznaczamy dopiero po udanym CI i scaleniu PR do `main`.

| Etap | Status | Cel i kryterium akceptacji |
| --- | --- | --- |
| 1. Legalne ruchy | **Zakończony (PR #13)** | Widoczne kandydatury ruchów, koszt many, blokada ataków wyczerpanymi/zamrożonymi jednostkami, kontrola Discover/mulligan; testy regresyjne. To filtr znanych błędów, nie pełna symulacja Hearthstone. |
| 2. Baza kart i mechanik | **W trakcie** | Wtyczka v0.5 eksportuje identyfikatory DBF, mechaniki z tagów HearthDb i wersję jej assembly; model oddziela potwierdzone tagi od wskazówek z tekstu. Testy i CI wymagane. |
| 3. Śledzenie zmian HDT | Do zrobienia | Poprawna aktualizacja decyzji po mulliganie, Discover, turach, animacjach i resetach. |
| 4. Potencjalny lethal i obrona | Do zrobienia | Wiarygodne warunkowe wyliczenia z wyraźną informacją o brakujących efektach. |
| 5. Warianty kilku ruchów | Do zrobienia | Krótkie porównanie sekwencji bez wymyślania dostępnych akcji. |
| 6. Synergie i kombinacje | Do zrobienia | Wykrywanie opartych na danych interakcji własnych kart i kolejności. |
| 7. Zarządzanie czasem | Do zrobienia | Opóźnienie dopasowane do fazy i czasu pozostałego na turę, bez nadmiernych żądań. |
| 8. Archetyp przeciwnika | Do zrobienia | Ostrożne prawdopodobieństwa tylko na podstawie publicznych zagrań. |
| 9. Plan całego meczu | Do zrobienia | Aktualizowany plan kontroli planszy, tempa i zasobów. |
| 10. Analiza po partii | Do zrobienia | Porównanie wykonanych ruchów z rekomendacjami na podstawie zapisanej historii i ograniczeń danych. |

## Zasady wdrożeń

1. Sprawdź istniejące moduły, dane HDT i historię PR przed modyfikacją.
2. Zrób małą, testowalną zmianę na gałęzi `feat/stage-XX-...`.
3. Uruchom testy automatyczne i kompilację Windows.
4. Scal po weryfikacji. Wpisz link PR i datę do tego pliku.
5. Rozpocznij następny nieukończony etap. Nigdy nie ogłaszaj końca bez weryfikacji.

## Historia

- 2026-10-16 — etap 2 rozpoczęty: `feat/stage-02-card-evidence-mechanics`. Przed scaleniem wymagane CI Windows i HDT oraz test starszej DLL.

- 2026-10-16 — etap 1: [PR #13](https://github.com/blaszkaaa/Hearthstone-MCP/pull/13). [Windows CI](https://github.com/blaszkaaa/Hearthstone-MCP/actions/runs/38874279335) oraz [testy MCP/HDT](https://github.com/blaszkaaa/Hearthstone-MCP/actions/runs/38874279319) zakończone sukcesem. Walidacja ma charakter konserwatywny i nie gwarantuje kompletności zasad gry.
