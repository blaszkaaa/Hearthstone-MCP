# Hearthstone AI Coach — Windows

Natywna aplikacja **Windows 10/11, WPF/.NET 8**, ciemny interfejs. Odczytuje ten sam `%LOCALAPPDATA%\HearthstoneMCP\state.json`, który tworzy dotychczasowa wtyczka HDT. Nie potrzebuje Pythona do **interfejsu i silnika AI**; istniejący serwer MCP nadal może działać niezależnie dla ChatGPT.

## Pobieranie i start

1. Zainstaluj i uruchom **Hearthstone Deck Tracker** wraz z wtyczką `HearthstoneMcpPlugin.dll` z tego repozytorium. Podczas meczu `state.json` musi się aktualizować. Wtyczka w wersji 0.2 lub nowszej obsługuje Discover.
2. Otwórz [Windows AI Coach build — Actions](https://github.com/blaszkaaa/Hearthstone-MCP/actions/workflows/desktop.yml), wybierz najnowszy **pomyślny** workflow gałęzi `main`, pobierz `Hearthstone-AI-Coach-win-x64` w sekcji *Artifacts*.
3. Rozpakuj całe archiwum i uruchom `HearthstoneCoach.exe`. Wydanie jest self-contained: nie wymaga osobnej instalacji .NET Runtime.
4. Kliknij **Połącz przez ChatGPT**. W systemowej przeglądarce wybierz konto, zaakceptuj zakres `ChatGPT plan usage`, wróć do aplikacji. Dostępność zależy od planu i przyznanych uprawnień konta.
5. Wybierz model z listy dostępnej na Twoim koncie. Włącz **Automatyczna analiza**. Opcjonalnie zaznacz **Przypnij nad grą**.
6. Rozpocznij partię. Po około 4 sekundach stabilnego stanu na Twojej turze model zaproponuje zagranie (mulligan po około 2 sekundach, Odkryj po około 1 sekundzie). Przy Discover/wspieranym wyborze przeanalizuje dostępne opcje.

**Koszty:** aplikacja korzysta z oficjalnego OSS `Sign in with ChatGPT` i `ChatGPT plan usage`, nie z klucza API. Zużycie jest wliczane do dostępnych limitów planu ChatGPT; limit może się wyczerpać. Zarządzaj dostępem i limitami w [ustawieniach ChatGPT](https://chatgpt.com/#settings/Usage). Nigdy nie wprowadzaj klucza API ani hasła do aplikacji — logowanie odbywa się w przeglądarce OpenAI.

## Jak działa automatyczna analiza

- Odczyt lokalnego snapshotu HDT co 1 s; plik musi być aktualny (<15 s).
- AI **nie jest wywoływane** w menu ani bez Twojej tury/aktywnego wyboru.
- AI czeka ok. 4 s na ustabilizowanie zwykłej tury, 2 s na mulligan i 1 s przy wyborze Odkryj. Zwykłe analizy co najmniej co 9 s, maks. 8 w turze (tylko po zmianie decyzji). Wybory otrzymują priorytet. Po błędzie możliwe jest ponowienie próby.
- Krótkie lub nieistotne wahania odczytu HDT **nie kasują rekomendacji**. Dopiero utrzymująca się przez 4 s istotna zmiana pozwala oznaczyć poradę jako nieaktualną i anulować stare zapytanie. Poprzednia rekomendacja pozostaje widoczna podczas odświeżania (z ostrzeżeniem), a spóźniona odpowiedź nie nadpisuje nowej.
- Odpowiedź eksponuje **kartę / ruch, cel i kolejność**, a bardzo krótkie uzasadnienie znajduje się na samym końcu. Odpowiedź jest rekomendacją, nie komendą. Bez automatyzacji kliknięć, bez DLL wstrzykiwanej w grę.
- Obsługuje scenariusze „Co zagrać?” oraz „Którą kartę Odkryj wybrać?”.
- Panel można przypiąć na wierzchu gry (tryb `Topmost`). Używa własnej ciemnej ramki okna Windows, bez jasnej belki u góry.

## Wybór ręki początkowej (mulligan) i Odkryj

Wtyczka HDT **v0.3** publikuje `phase`, `mulligan_active`, `mulligan_cards` oraz aktywne `choices`. Zaktualizuj **zarówno DLL wtyczki, jak i aplikację**: sama wymiana EXE nie umożliwi odczytu pierwszego wyboru ani poprawnego raportowania Odkrycia.

- **Mulligan:** dla każdej karty w ręce początkowej aplikacja pokazuje osobno `1 ZOSTAW`, `2 ODRZUĆ`, `3 ZOSTAW`, itd. Nawet jeśli nie jest to jeszcze Twoja tura.
- **Odkryj / inne aktywne wybory:** wybiera konkretną opcję z indeksem i nazwą (`Wybierz 2 — [karta]`). Odczyt nie opiera się na zwykłej ręce gracza.
- **Tury:** analiza uruchamia się po ok. 4 s stabilności; mulligan po 2 s, Odkryj po ok. 1 s. W miarę możliwości model otrzymuje limit czasu ok. 23 s na zakończenie analizy. Po niepowodzeniu może ponowić odczyt.
- **Po każdej nowej turze:** harmonogram umożliwia nową analizę; każda zmiana istotna dla decyzji dostaje nowy identyfikator. Starsze odpowiedzi nie nadpisują nowszych.

## Pełne logi do analizy błędów

W aplikacji kliknij **Otwórz logi diagnostyczne**. Pliki JSONL są zapisywane w:
`%LOCALAPPDATA%\HearthstoneCoach\logs\coach-*.jsonl`.

W logach znajdują się: czas, sesja, stan i faza gry, pełny widoczny stan z HDT (karty Twojej ręki, plansza, wybory), zmiany wykryte przez harmonogram, dane wysyłane do AI (prompt), surowe odpowiedzi AI, czasy analiz, błędy i wyjątkowe sytuacje. Dzięki temu można odtworzyć, dlaczego Discover lub mulligan nie zostały wykryte i kiedy podpowiedź przestała się aktualizować.

Wtyczka HDT zapisuje dodatkowo własną diagnostykę w:
`%LOCALAPPDATA%\HearthstoneMCP\plugin.log` (oraz `plugin.log.old` po rotacji).
Dzienniki aplikacji są przechowywane maksymalnie 7 dni i mogą zawierać szczegóły Twoich rozgrywek. **Nie zawierają haseł ani tokenów ChatGPT.** Przed udostępnieniem plików sprawdź, czy nie ma w nich informacji, których nie chcesz ujawniać.

Gdy problem wystąpi, wyślij plik `coach-*.jsonl` z odpowiedniej godziny oraz `plugin.log` — bez potrzeby przepisywania błędów ręcznie.

## Ochrona danych i logowanie

Rejestracja zgodna z [OpenAI Sign in with ChatGPT dla aplikacji OSS](https://developers.openai.com/siwc/token-sharing-open-source/sign-in): systemowa przeglądarka, OAuth PKCE i losowe state/nonce, callback na `127.0.0.1`, walidacja OIDC JWT przez JWKS; żądania AI do oficjalnego `https://api.openai.com/v1/responses` z `store=false` i `stream=true`. Wysłany tekst zawiera **stan widocznej rozgrywki**, m.in. Twoją rękę i opcje wyboru, ale nie ukrytą rękę przeciwnika.

Dane dostępu przechowywane są lokalnie w `%LOCALAPPDATA%\HearthstoneCoach\session.dat`, zaszyfrowane Windows DPAPI dla aktualnego użytkownika. Nie zapisujemy tokenów ani odpowiedzi AI w repozytorium, nie wysyłamy ich do serwera autorów aplikacji. Wylogowanie usuwa lokalną sesję i podejmuje próbę unieważnienia refresh tokena. Uprawnienie można też cofnąć w ustawieniach ChatGPT.

## Ręczne budowanie

Wymaga .NET SDK 8, Windows:

```powershell
dotnet run --project desktop/Coach.Checks/Coach.Checks.csproj -c Release
dotnet publish desktop/Coach.App/Coach.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\HearthstoneCoach
```

## Ograniczenia

- Kompilacja/testy automatyczne nie zastępują testu OAuth i analizy w uruchomionej grze na koncie użytkownika.
- HDT i jego logi nie udostępniają pełnego zbioru legalnych akcji ani ukrytych kart. AI może się mylić. Gra pozostaje wyłącznie pod kontrolą użytkownika.
- Aplikacja nie gwarantuje dopuszczalności porad AI w turniejach i określonych trybach. Sprawdź odpowiednie zasady.
- Początkowa wersja nie ma automatycznych aktualizacji, instalatora MSI, historii analiz, wyboru różnych kont ani podświetlania kart na ekranie gry.
