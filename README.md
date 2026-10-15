# Hearthstone MCP

**Polski, lokalny most Hearthstone Deck Tracker → MCP → asystent.**

Ten projekt udostępnia ChatGPT i innym klientom MCP jawny stan bieżącej rozgrywki, aby można było zapytać: **„Co teraz zagrać i dlaczego?”**. Nie steruje grą, nie wstrzykuje bibliotek DLL do procesu Hearthstone i nie ujawnia ukrytej ręki przeciwnika.

## Status projektu

**MVP v0.1 — kod źródłowy.** Dostępna jest wtyczka HDT w C# zapisująca snapshot JSON i lokalny serwer MCP po stdio w Pythonie. Python i protokół MCP mają automatyczne testy. GitHub Actions dodatkowo próbuje skompilować wtyczkę na Windows względem opublikowanego HDT; działanie z aktywną grą trzeba zweryfikować lokalnie.

**Ważne:** lokalny serwer stdio można podłączyć do klienta MCP uruchamianego na tym samym komputerze. **ChatGPT w przeglądarce nie może sam odczytać lokalnego procesu stdio**. Można użyć [Secure MCP Tunnel](docs/chatgpt.md), jeżeli jest dostępny na koncie, albo wdrożyć uwierzytelniony HTTPS MCP. Na tym etapie nie twierdzimy, że wtyczka jest już połączona z tą rozmową.

## Architektura

1. **Hearthstone Deck Tracker (HDT)** zbiera dane udostępnione podczas rozgrywki.
2. **HearthstoneMcpPlugin.dll** (wtyczka HDT, nie gry) zapisuje jawne dane do pliku JSON, co 0,5 sekundy.
3. **mcp_server/server.py** czyta plik i udostępnia pięć narzędzi MCP przez standardowe wejście/wyjście (stdio).
4. **Asystent MCP** odczytuje stan na żądanie i na jego podstawie formułuje rekomendację; nie ma funkcji zagrywania kart.

## Wymagania

- Windows i [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases).
- Python 3.10 lub nowszy (serwer działa bez pakietów pip).
- Do samodzielnej kompilacji wtyczki: .NET SDK, Visual Studio/Build Tools oraz **.NET Framework 4.7.2 Targeting Pack**.

## Instalacja na Windows

1. Sklonuj repozytorium i uruchom HDT co najmniej raz.
2. Znajdź katalog **plików programu HDT** — zawierający jednocześnie `Hearthstone Deck Tracker.exe`, `HearthDb.dll` i `Newtonsoft.Json.dll`. Nie chodzi o katalog `Plugins`.
3. W PowerShell, w katalogu repozytorium, uruchom:

   ```powershell
   .\scripts\build-plugin.ps1 -HdtPath "C:\sciezka\do\HDT"
   ```

4. Otwórz w HDT `Options → Tracker → Plugins → Plugins Folder`. Przekopiuj tam `plugin\bin\Release\net472\HearthstoneMcpPlugin.dll` i ponownie uruchom HDT.
5. W `Options → Tracker → Plugins` włącz **Hearthstone MCP**.
6. Rozpocznij partię i sprawdź plik stanu:

   ```powershell
   Get-Content "$env:LOCALAPPDATA\HearthstoneMCP\state.json"
   ```

   W czasie partii powinno być `"in_game":true`. W menu: `false`. Brak pliku oznacza, że wtyczka nie działa albo nie ma uprawnień do zapisu.

## Serwer MCP

Nie trzeba go uruchamiać ręcznie, jeśli klient MCP wspiera konfigurację lokalnych serwerów stdio; klient sam startuje proces Pythona.

Przykładowa konfiguracja dla **dowolnego klienta MCP obsługującego stdio** (dostosuj pełną ścieżkę):

```json
{
  "mcpServers": {
    "hearthstone": {
      "command": "python",
      "args": ["C:\\sciezka\\do\\Hearthstone-MCP\\mcp_server\\server.py"]
    }
  }
}
```

Dokładny format konfiguracji zależy od klienta. Sam serwer nie otwiera okna ani nie wypisuje statusu na stdout, ponieważ kanał ten służy komunikatom MCP.

### Dostępne narzędzia

| Narzędzie | Zastosowanie |
| --- | --- |
| `hearthstone_status` | Wykrycie, czy mecz trwa i jak świeże są dane |
| `read_hearthstone_state` | Cały widoczny stan meczu |
| `get_hearthstone_hand` | Nasza ręka oraz mana |
| `get_hearthstone_board` | Bohaterowie, stół i liczba sekretów |
| `get_hearthstone_plays` | Karty zagrane w aktualnej turze |

Każdy odczyt wymaga aktualizacji z ostatnich 15 sekund. Jeśli dane są stare, narzędzia zwracają `available: false` i **nie przedstawiają starej planszy jako aktualnej**.

## Testy

```powershell
python -m unittest discover -s tests -v
```

## Ograniczenia i prywatność

- Odczyt opiera się na stanie **HDT**, którego kompletność zależy od poprawnego rozpoznawania rozgrywki.
- Nie ma dostępu do ukrytych kart przeciwnika, losowych przyszłych efektów ani gwarancji optymalnego zagrania.
- Wtyczka **nie odczytuje pamięci procesu Hearthstone**, nie wstrzykuje DLL do gry i nie wysyła danych do żadnego zewnętrznego serwera.
- Snapshot lokalny może zawierać informacje o Twojej rozgrywce; jego udostępnianie klientowi AI będzie zależeć od konfiguracji tego klienta.
- W turniejach należy dodatkowo przestrzegać regulaminu organizatora i wymagań Blizzard; projekt nie gwarantuje dopuszczalności automatycznego doradztwa w każdym trybie.

## Kolejne kroki

1. Skompilować wtyczkę na Windows z faktyczną wersją HDT, a następnie zweryfikować pierwszy odczyt z gry.
2. Rozbudować dane o rozpoznawanie mechanik i kontekst talii.
3. Podłączyć lokalny serwer stdio do ChatGPT za pomocą [Secure MCP Tunnel](docs/chatgpt.md) (jeśli dostępny na koncie) lub osobnego uwierzytelnionego HTTPS MCP.

Źródła: [tworzenie wtyczek HDT](https://github.com/HearthSim/Hearthstone-Deck-Tracker/wiki/Creating-Plugins), [SDK MCP](https://github.com/modelcontextprotocol/csharp-sdk), [zasady dot. trackerów turniejowych](https://assets.blz-contentstack.com/v3/assets/bltc965041283bac56c/blt9947def28df308cb/60f784a52d26525ef67acd49/Hearthstone_Tournament_Player_Handbook_v_2.7.pdf).
