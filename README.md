# Hearthstone MCP

**Polski, lokalny most Hearthstone Deck Tracker → MCP → asystent.**

Ten projekt udostępnia ChatGPT i innym klientom MCP jawny stan bieżącej rozgrywki, aby można było zapytać: **„Co teraz zagrać i dlaczego?”**. Nie steruje grą, nie wstrzykuje bibliotek DLL do procesu Hearthstone i nie ujawnia ukrytej ręki przeciwnika.

## Status projektu

**MVP v0.2 — kod źródłowy.** Dostępna jest wtyczka HDT w C# zapisująca snapshot JSON i lokalny serwer MCP po stdio w Pythonie. Python i protokół MCP mają automatyczne testy. GitHub Actions dodatkowo próbuje skompilować wtyczkę na Windows względem opublikowanego HDT; działanie z aktywną grą trzeba zweryfikować lokalnie.

**Ważne:** lokalny serwer stdio można podłączyć do klienta MCP uruchamianego na tym samym komputerze. **ChatGPT w przeglądarce nie może sam odczytać lokalnego procesu stdio**. Można użyć [Secure MCP Tunnel](docs/chatgpt.md), jeżeli jest dostępny na koncie, albo wdrożyć uwierzytelniony HTTPS MCP. Na tym etapie nie twierdzimy, że wtyczka jest już połączona z tą rozmową.

## Architektura

1. **Hearthstone Deck Tracker (HDT)** zbiera dane udostępnione podczas rozgrywki.
2. **HearthstoneMcpPlugin.dll** (wtyczka HDT, nie gry) zapisuje jawne dane do pliku JSON, co 0,5 sekundy.
3. **mcp_server/server.py** czyta plik i udostępnia siedem narzędzi MCP przez standardowe wejście/wyjście (stdio).
4. **Asystent MCP** odczytuje stan na żądanie i na jego podstawie formułuje rekomendację; nie ma funkcji zagrywania kart.

## Wymagania

- Windows i [Hearthstone Deck Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases).
- Python 3.10 lub nowszy (serwer działa bez pakietów pip).
- Do samodzielnej kompilacji wtyczki: .NET SDK, Visual Studio/Build Tools oraz **.NET Framework 4.7.2 Targeting Pack**.

## Instalacja na Windows

**Bez kompilacji:** otwórz [ostatni pomyślny workflow GitHub Actions](https://github.com/blaszkaaa/Hearthstone-MCP/actions/workflows/tests.yml), w sekcji **Artifacts** pobierz archiwum `HearthstoneMcpPlugin-win` i wypakuj DLL. Potem przejdź od razu do kroku 4 instrukcji poniżej. GitHub może wymagać zalogowania.

**Z kompilacją ze źródeł:**

1. Sklonuj repozytorium i uruchom HDT co najmniej raz.
2. Znajdź katalog **plików programu HDT** — zawierający jednocześnie `Hearthstone Deck Tracker.exe`, `HearthDb.dll` i `Newtonsoft.Json.dll`. Nie chodzi o katalog `Plugins`.
3. W PowerShell, w katalogu repozytorium, uruchom:

   ```powershell
   .\scripts\build-plugin.ps1 -HdtPath "C:\sciezka\do\HDT"
   ```

4. Zamknij HDT i uruchom instalator w PowerShell:

   ```powershell
   .\scripts\install-plugin.ps1
   ```

   Jeśli masz gotowy plik DLL z GitHub Actions, uruchom ` .\scripts\install-plugin.ps1 -DllPath "C:\pobrane\HearthstoneMcpPlugin.dll" `. Możesz też skompilować i zainstalować wtyczkę jedną komendą: ` .\scripts\install-plugin.ps1 -Build -HdtPath "C:\sciezka\do\HDT" `.

   Instalator domyślnie instaluje do katalogu `Plugins` w katalogu danych HDT w `%APPDATA%`, weryfikuje sumę SHA-256 i nie ingeruje w proces gry. Możesz wskazać katalog przez `-PluginsPath`. Uruchom ponownie HDT.
5. W `Options → Tracker → Plugins` włącz **Hearthstone MCP**.
6. Rozpocznij partię i sprawdź plik stanu:

   ```powershell
   Get-Content "$env:LOCALAPPDATA\HearthstoneMCP\state.json"
   ```

   W czasie partii powinno być `"in_game":true`. W menu: `false`. Brak pliku oznacza, że wtyczka nie działa albo nie ma uprawnień do zapisu.

## Nowość: Odkryj (Discover), Wybierz jedno (Choose One) i inne wybory kart

Kiedy Hearthstone wyświetli 2, 3 lub więcej kart do wyboru, HDT udostępnia identyfikatory aktywnych ofert gracza. Wtyczka odczytuje je w kolejności wyboru (pole `choices[*].index`, indeksy zaczynają się od 1), zapisuje nazwy, identyfikatory, koszt i opis działania karty **o ile HDT zna te dane**.

Gdy pytasz ChatGPT **„Co wziąć?”**, klient MCP powinien najpierw uruchomić narzędzie `get_hearthstone_choices`. `get_hearthstone_advice_context` również uwzględnia aktywne wybory. Asystent powinien wskazać numer/nazwę wybranej opcji, podać krótkie uzasadnienie i **nie zalecać zakończenia tury, gdy `choice_active=true`**.

Pola:

- `choice_tracking_supported=true` — najnowsza DLL udostępnia wybory; `false` oznacza starą DLL i konieczność aktualizacji.
- `choice_active=true` — trwa wybór, `choices` zawiera dostępne opcje. `false` — HDT nie wykrywa obecnie wyboru. `null` — stara DLL, nie da się tego ocenić.
- `choices` — kolejne opcje z `index` (od 1) i obiektem `card`; gdy `known=false`, nie zgadujemy tożsamości karty.
- `choice_count` — liczba rozpoznanych ofert.

**Ważne:** HDT raportuje także inne rodzaje wyborów niż Discover. Projekt nie nazywa automatycznie każdego wyboru mechaniką Odkryj. Rozpoznawanie dostępnych opcji zależy od aktualnej wersji i danych logów HDT. Nie odczytuje kart ukrytych przeciwnika i nie steruje myszką.

### Aktualizacja wtyczki, jeśli masz już starszą DLL

1. Zamknij Hearthstone Deck Tracker.
2. Pobierz nową DLL `HearthstoneMcpPlugin.dll` z **najnowszego pomyślnego** [workflow na main](https://github.com/blaszkaaa/Hearthstone-MCP/actions/workflows/tests.yml), sekcja **Artifacts → HearthstoneMcpPlugin-win**.
3. Rozpakuj DLL, a w folderze projektu uruchom:

   ```powershell
   .\scripts\install-plugin.ps1 -DllPath "$env:USERPROFILE\Downloads\HearthstoneMcpPlugin.dll"
   ```

4. Uruchom ponownie HDT, wejdź w `Options → Tracker → Plugins`, upewnij się, że `Hearthstone MCP` jest aktywna.
5. Podczas okna wyboru odpal `python -m mcp_server.doctor`. Powinno być `Odczyt wyborow: obslugiwany`, `Aktywny wybor: True` i liczba opcji. Po dokonaniu wyboru `Aktywny wybor` powinien się zmienić na `False`.
6. Jeśli klient ChatGPT nadal pokazuje starą listę narzędzi, odśwież jego połączenie MCP. Nowe narzędzie to `get_hearthstone_choices`.

## Szybki test połączenia

W folderze repozytorium uruchom:

```powershell
python -m mcp_server.doctor
```

Raport odróżnia: brak pliku, nieaktualne dane, aktywną wtyczkę w menu i aktywny mecz. Podaje wiek danych, liczbę kart na ręce, manę i liczbę jednostek na stole. **Nie drukuje nazw kart ani ukrytych danych przeciwnika.**

Możesz sprawdzić inny plik danych komendą `python -m mcp_server.doctor --state-path "C:\sciezka\state.json" --json`. Kody wyjścia: `0` — mecz i świeże dane, `2` — plugin działa, lecz brak meczu, `1` — wymagana diagnoza błędu.

`hearthstone_status` w MCP zwraca też stabilne pole `code` oraz `tracker_connected`, dzięki czemu klient odróżnia poprawną pracę w menu od niedziałającej integracji.

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
| `get_hearthstone_advice_context` | Kontekst tury i aktywne opcje wyboru (jeśli są widoczne) |
| `get_hearthstone_choices` | Aktywne opcje Odkryj / Choose One / inne wybory; numer, nazwa i opis, jeśli dostępne |
| `get_hearthstone_hand` | Nasza ręka oraz mana |
| `get_hearthstone_board` | Bohaterowie, widoczne moce bohaterów, broń, stół, liczba kart w talii i sekretów |
| `get_hearthstone_plays` | Karty zagrane w aktualnej turze |

Każdy odczyt wymaga aktualizacji z ostatnich 15 sekund. Jeśli dane są stare, narzędzia zwracają `available: false` i **nie przedstawiają starej planszy jako aktualnej**.

## Testy

```powershell
python -m unittest discover -s tests -v
```

## Ograniczenia i prywatność

- Odczyt opiera się na stanie **HDT**, którego kompletność zależy od poprawnego rozpoznawania rozgrywki.
- Nie ma dostępu do ukrytych kart przeciwnika, losowych przyszłych efektów ani gwarancji optymalnego zagrania.
- Moc bohatera i broń są odczytywane tylko, jeśli HDT widzi odpowiadające im obiekty; samo ich wykrycie nie oznacza, że można ich użyć w tej turze.
- Znane karty mogą zawierać opis działania (`card_text`, `card_text_en`) z bazy HDT, aby asystent nie musiał zgadywać efektów nowych kart. Tekst karty opisuje bazową wersję i nie odzwierciedla wszystkich zmian w trakcie partii.
- Wtyczka **nie odczytuje pamięci procesu Hearthstone**, nie wstrzykuje DLL do gry i nie wysyła danych do żadnego zewnętrznego serwera.
- Snapshot lokalny może zawierać informacje o Twojej rozgrywce; jego udostępnianie klientowi AI będzie zależeć od konfiguracji tego klienta.
- W turniejach należy dodatkowo przestrzegać regulaminu organizatora i wymagań Blizzard; projekt nie gwarantuje dopuszczalności automatycznego doradztwa w każdym trybie.

## Kolejne kroki

1. Skompilować wtyczkę na Windows z faktyczną wersją HDT, a następnie zweryfikować pierwszy odczyt z gry.
2. Sprawdzić wybory Odkryj, Choose One i pozostałe na kilku rzeczywistych kartach; rozbudować kontekst mechanik i talii.
3. Podłączyć lokalny serwer stdio do ChatGPT za pomocą [Secure MCP Tunnel](docs/chatgpt.md) (jeśli dostępny na koncie) lub osobnego uwierzytelnionego HTTPS MCP.

Źródła: [tworzenie wtyczek HDT](https://github.com/HearthSim/Hearthstone-Deck-Tracker/wiki/Creating-Plugins), [SDK MCP](https://github.com/modelcontextprotocol/csharp-sdk), [zasady dot. trackerów turniejowych](https://assets.blz-contentstack.com/v3/assets/bltc965041283bac56c/blt9947def28df308cb/60f784a52d26525ef67acd49/Hearthstone_Tournament_Player_Handbook_v_2.7.pdf).
