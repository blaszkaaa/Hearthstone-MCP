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

## Stabilne automatyczne rekomendacje (0.6)

Naprawa błędu, w którym po analizie widniał komunikat `Zmieniono stan rozgrywki — Czekam na aktualizację...` i nic więcej się nie pojawiało:

- Identyfikator decyzji obejmuje tylko **istotne pola gry** (koszt, ręka, statystyki, zdrowie, mana, talia w fazie mulligan, aktualne opcje wyboru). Zmiany technicznych opisów i pozycji HDT nie przerywają odpowiedzi.
- Gdy odpowiedź dotrze podczas chwilowego odczytu HDT, aplikacja czeka **1,4 sekundy** i sprawdza stan ponownie, zanim ją odrzuci.
- Jeśli odpowiedź nadal jest nieaktualna, harmonogram zostaje **odblokowany** i automatycznie spróbuje przeanalizować aktualną decyzję po ustaleniu się gry. Nie trzeba klikać „Analizuj teraz”.
- Maksymalnie 3 próby tej samej decyzji (żeby nie zużywać bez końca planu ChatGPT); nowa sytuacja na planszy otrzymuje osobny limit. Zwykła tura jest ograniczona do 24 automatycznych prób, także w długiej turze.
- Logi zawierają `ai.response.grace_recheck` oraz `ai.discarded.retry_armed` i stan harmonogramu `retry_budget_for_decision`.
- Widoczny numer wersji w nagłówku to teraz `BETA 0.6`. Zaktualizuj samą aplikację Windows; istniejąca DLL HDT v0.3 działa dalej.

Nie wolno używać poprzedniej decyzji, gdy nowy stan planszy został rzeczywiście potwierdzony. Poprawka nie zmienia wyglądu karty z poradą ani logowania przez ChatGPT.

## Poprawka wyświetlania po mulliganie (0.4.1)

Jeśli podczas normalnej tury na ekranie pozostawały decyzje `ZOSTAW/ODRZUĆ` z mulliganu, starsza wersja mogła anulować następne zapytanie AI, porównując **nowe zapytanie** z **poprzednio wyświetloną poradą**. Od tej wersji osobno śledzone są: aktualne zapytanie oraz wyświetlona rekomendacja. Zmiana tury/fazy usuwa poprzednią poradę, a nietrwała zmiana danych HDT nie przerywa żądania.

Model GPT-6.1 Sol może zużywać dużo czasu na rozumowanie. Wysyłamy więc żądanie z `reasoning.effort=low` oraz `text.verbosity=low`; limit awaryjny podniesiono z 23 do 45 sekund. Zmiana tury może anulować poprzednie, nieaktualne zapytanie wcześniej. **Nie oznacza to**, że każde zapytanie powinno trwać 45 sekund. Po błędzie harmonogram może ponowić analizę.

W logach `ai.cancel.superseded` oznacza nową turę lub decyzję, natomiast `ai.canceled_or_timeout` zawiera `reason` (np. `timeout_45s`). Będzie można rozróżnić wolny model od poprawnego odrzucenia starej odpowiedzi.

## Pełne logi do analizy błędów

W aplikacji kliknij **Otwórz logi diagnostyczne**. Pliki JSONL są zapisywane w:
`%LOCALAPPDATA%\HearthstoneCoach\logs\coach-*.jsonl`.

W logach znajdują się: czas, sesja, stan i faza gry, pełny widoczny stan z HDT (karty Twojej ręki, plansza, wybory), zmiany wykryte przez harmonogram, dane wysyłane do AI (prompt), surowe odpowiedzi AI, czasy analiz, błędy i wyjątkowe sytuacje. Dzięki temu można odtworzyć, dlaczego Discover lub mulligan nie zostały wykryte i kiedy podpowiedź przestała się aktualizować.

Wtyczka HDT zapisuje dodatkowo własną diagnostykę w:
`%LOCALAPPDATA%\HearthstoneMCP\plugin.log` (oraz `plugin.log.old` po rotacji).
Dzienniki aplikacji są przechowywane maksymalnie 7 dni i mogą zawierać szczegóły Twoich rozgrywek. **Nie zawierają haseł ani tokenów ChatGPT.** Przed udostępnieniem plików sprawdź, czy nie ma w nich informacji, których nie chcesz ujawniać.

Gdy problem wystąpi, wyślij plik `coach-*.jsonl` z odpowiedniej godziny oraz `plugin.log` — bez potrzeby przepisywania błędów ręcznie.

## Inteligencja taktyczna v0.5 — wiedza o kartach, pamięć i zagrożenia

Silnik aplikacji (bez zmiany wyglądu oraz bez nowych żądań API) tworzy trzy konteksty do **jednego** zapytania ChatGPT:

1. **Znane efekty kart** — odczytuje `card_id`, koszt i oryginalny opis działania z HDT dla własnej ręki, mulliganu, Odkrycia, widocznych stronników i broni. Wydobywa potwierdzone słowa kluczowe (np. Battlecry / Taunt / Divine Shield), ale **nie zgaduje** efektu, jeśli nie ma go w danych. Wzmocnienia i przemiany w trakcie partii mogą zmienić rzeczywiste działanie karty.
2. **Pamięć meczu** — śledzi **publicznie zaobserwowane** karty zagrane przez obie strony, aktualizowane przez ciągłe odczyty HDT. Zapamiętuje maksymalnie 120 zdarzeń w RAM, a do AI przekazuje 32 ostatnie. Rozróżnia brak danych z wcześniejszych tur od pewnej wiedzy o historii. Zapis resetuje się po nowym meczu / dłuższym pobycie w menu. Pamięć jest procesowa (znika po zamknięciu aplikacji), natomiast diagnostyka rozgrywki nadal zapisuje się do lokalnych logów.
3. **Ocena zagrożeń** — oblicza jawną siłę ataku obu plansz, zdrowie + pancerz bohaterów, sprawdza obecność Prowokacji oraz ostrzega przed **potencjalnym** lethal i teoretycznymi wymianami. Nie traktuje tych ocen jako potwierdzonego, legalnego ruchu: mogą działać tarcze, zdolności, sekrety, zaklęcia i inne ograniczenia. Nie zna ukrytych kart przeciwnika.

AI ma najpierw uwzględnić ryzyko przegranej, możliwy lethal i wyraźne zagrożenia, następnie opłacalne wymiany oraz tempo. Nadal zwraca krótki, konkretny ruch, cel i powód. Odpowiedź jest generowana **jednym wywołaniem** dotychczasowego modelu w planie ChatGPT. Nie ma dodatkowego logowania ani opłat za osobne API.

W logach `game.intelligence` znajduje się przekazany kontekst strategiczny; `ai.request` zawiera kompletny prompt do łatwiejszej diagnostyki. **Do tej aktualizacji nie potrzeba podmieniać DLL HDT:** wystarczy nowy program `HearthstoneCoach.exe`, jeżeli używana wtyczka już poprawnie eksportuje tekst kart oraz stan gry.

## Poprawa zasad kart — Beta 0.7

**Ważne: zaktualizuj zarówno wtyczkę HDT do v0.4, jak i aplikację Windows do Beta 0.7.** Stara DLL v0.3 nie przekazuje typu kart.

Wtyczka publikuje teraz z bazy HearthDb pole `card_type` (np. `SPELL`, `MINION`, `WEAPON`, `LOCATION`) dla kart z ręki, planszy i aktywnych wyborów. Te typy trafiają do pojedynczego promptu AI i do lokalnych reguł kontroli.

Aplikacja **blokuje i loguje** jednoznacznie niemożliwe rady, np. „Zaatakuj Wybuchającym wulkanem” (zaklęcie w ręce), atak na własną kartę znajdującą się w ręce, próbę zaatakowania lokacją jak stronnikiem oraz wskazanie ręcznego celu dla zaklęcia, którego opis mówi o losowym rozdzielaniu obrażeń. Automatycznie ponawia analizę (z dotychczasowym limitem prób) i przekazuje modelowi przyczynę odrzucenia. Zdarzenia diagnostyczne: `ai.advice.rejected_illegal` i `hdt.card_types_missing`.

Nie jest to pełny silnik zasad Hearthstone: nie potwierdza wszystkich możliwych celów, ograniczeń ataku, efektów Aur i zmian mechanik w czasie meczu. Dlatego walidator skupia się na rażących błędach jednoznacznie sprzecznych z widocznymi typami i strefami kart.

## Etap 2 — wiarygodne dane kart i mechanik (DLL HDT v0.5)

W tej wersji wtyczka HDT udostępnia dla znanych kart:
`card_id`, `card_dbf_id`, `card_type`, `printed_mechanics`
(z tagów HearthDb) i `printed_data_source`. Stan gry zawiera
`card_evidence_schema=2`, `card_catalog_source` oraz
`card_catalog_assembly_version`: **wersję zainstalowanej biblioteki**,
a nie gwarancję posiadania najnowszego katalogu kart.

AI rozróżnia teraz trzy różne sytuacje: `catalog_confirmed` oznacza
mechaniki wyeksportowane przez bazę HearthDb (również pustą listę);
`not_provided` oznacza **brak danych o mechanikach**, a nie brak
mechanik na karcie; `unverified_text_hints` oznacza słowa z opisu
karty, których nie wolno traktować jak potwierdzonych tagów.
Żaden z tych elementów nie symuluje aktualnych efektów po buffach,
transformacjach ani zmienionych kosztach.

Zachowujemy też wielokrotne kopie tej samej karty w ręce, zamiast łączyć
je w pojedynczy wpis według samej nazwy lub ID. Dane trafiają do
tego samego wywołania modelu; wygląd programu jest bez zmian.

**Aktualizacja wymaga pobrania zarówno nowej wtyczki HDT v0.5,
jak i nowej aplikacji Windows.** Ze starszą DLL nowe pola pozostają
nieznane, a AI nie może twierdzić, że zna jej mechaniczną specyfikację.
Jest to nadal baza wydrukowanych informacji zainstalowanego HDT, nie
automatycznie synchronizowana globalna baza wszystkich kart.

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
