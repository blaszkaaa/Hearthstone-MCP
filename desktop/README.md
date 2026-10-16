# Hearthstone AI Coach — Windows

Natywna aplikacja **Windows 10/11, WPF/.NET 8**, ciemny interfejs. Odczytuje ten sam `%LOCALAPPDATA%\HearthstoneMCP\state.json`, który tworzy dotychczasowa wtyczka HDT. Nie potrzebuje Pythona do **interfejsu i silnika AI**; istniejący serwer MCP nadal może działać niezależnie dla ChatGPT.

## Aktualizacja całej aplikacji jednym przyciskiem

W **Ustawieniach → Aktualizacje aplikacji** kliknij **Sprawdź aktualizacje**.
Program automatycznie sprawdza nową wersję przy uruchomieniu, ale **nie pobiera jej
ani nie zamyka się bez kliknięcia użytkownika**. Jeśli na GitHubie jest nowa
wersja, wybierz **Zainstaluj aktualizację** i potwierdź restart.

- Wydanie aplikacji `app-latest` zawiera archiwum Windows x64 i manifest
  `HearthstoneCoach-update.json` z wersją (SHA commita) i sumą SHA-256.
- Aplikacja pobiera i **sprawdza SHA-256 całej paczki** przed podmianą plików.
  Instalator działa w osobnym procesie dopiero po zamknięciu HearthstoneCoach.exe,
  zachowuje poprzednie pliki do ewentualnego cofnięcia i uruchamia program ponownie.
- **Nie aktualizuje w trakcie meczu.** Nie rusza danych użytkownika w
  `%LOCALAPPDATA%`, historii gier, logowania do ChatGPT ani folderu HDT.
- Jeśli nie ma internetu, manifest jest uszkodzony lub katalog instalacji jest
  chroniony przed zapisem, pokazuje komunikat bez zmiany programu.
- W przypadku nieudanego kopiowania skrypt próbuje przywrócić stare pliki,
  a szczegóły zapisuje w `%LOCALAPPDATA%\HearthstoneMCP\desktop-update-error.log`.

**Pierwszy raz** pobierz i rozpakuj wydanie z
[GitHub Releases — app-latest](https://github.com/blaszkaaa/Hearthstone-MCP/releases/tag/app-latest).
Od tego momentu kolejne aktualizacje instalujesz w aplikacji.
Dla Windows używaj katalogu z prawem zapisu (np. w folderze użytkownika);
nie trzeba uruchamiać programu jako administrator.

Publikacja pakietu aktualizacyjnego odbywa się w
[desktop.yml](../.github/workflows/desktop.yml) po pomyślnym buildzie z gałęzi main.
Jest to **oddzielna funkcja** od aktualizacji biblioteki wtyczki HDT.

## Aktualizacja DLL jednym przyciskiem

W zakładce **Preferencje** kliknij **Aktualizuj wtyczkę HDT ↻**, gdy nie trwa mecz.
Program wykonuje następujące czynności:

1. Przez maksymalnie 6 sekund sprawdza oficjalne wydanie `plugin-latest` w
   [GitHub Releases](https://github.com/blaszkaaa/Hearthstone-MCP/releases/tag/plugin-latest).
   Gdy `github.com` jest niedostępny (np. `Nieznany host (github.com:443)`),
   **automatycznie przełącza się na sprawdzoną DLL wbudowaną w ten sam plik EXE**.
   Nowa aplikacja Windows publikuje DLL zbudowaną z tego samego commita.
   Sprawdza tożsamość biblioteki i sumę SHA-256 PRZED zamknięciem HDT.
   Nie trzeba pobierać pliku DLL osobno ani mieć działającego DNS.
2. Wyszukuje dotychczas zainstalowaną `HearthstoneMcpPlugin.dll`
   w typowych folderach danych HDT. Jeśli wykryje kilka instalacji albo
   nieznany folder, przerywa zamiast nadpisywać losowy plik.
3. Jeśli HDT jest otwarty, wysyła polecenie jego **normalnego zamknięcia**
   i czeka maksymalnie 20 sekund. **Nigdy nie używa Force/Kill.**
   Jeśli HDT nie chce się zamknąć, pokaże błąd i poprosi o ręczne zamknięcie.
4. Podmienia DLL w folderze Plugins i zostawia poprzednią kopię
   `HearthstoneMcpPlugin.dll.bak`. Sprawdza sumę po zapisaniu.
   Nie dotyka bibliotek gry Hearthstone i nie wstrzykuje DLL do gry.
5. Jeśli HDT był otwarty, uruchamia jego wcześniejszy plik EXE ponownie.
   Jeśli przed aktualizacją nie działał, pozostawia go wyłączonego.

Jeśli masz już dokładnie tę samą DLL, HDT nie zostanie zamknięty.
Aktualizacja zapisuje zdarzenia `plugin.update.*` w logach.
Jeśli GitHub nie działa, instalowana jest wersja **dołączona do pobranej aplikacji Windows**. Jest ona tak aktualna, jak sama aplikacja; dla nowszych wersji pobierz nowe wydanie programu. Jeśli wydanie jest uszkodzone lub nie można zweryfikować DLL, aktualizacja zostaje bezpiecznie zatrzymana. Podczas meczu przycisk odmawia aktualizacji.

Publikacją nowej DLL i sumy SHA-256 zarządza
[workflow plugin-release.yml](../.github/workflows/plugin-release.yml),
uruchamiany po zmianach wtyczki na `main`. Wymaga uprawnień
`contents:write` dla GitHub Actions.

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

## Stabilność odczytów HDT — etap 3

Silnik `HdtStateReconciler` łączy kolejno odebrane stany HDT.
Wejście w Odkryj, mulligan oraz potwierdzona następna tura mają
priorytet i nie są opóźniane; chwilowy zanik oferty Odkryj lub
jednorazowa zmiana planszy w animacji wymaga ponownego potwierdzenia
przez ok. 0,8 s. Chwilowy błąd odczytu/menu jest tolerowany maksymalnie
przez 2 s, potem rozłączenie lub wyjście z meczu jest pokazywane.
AI nie traci więc bieżącego stanu z powodu pojedynczej klatki odczytu.

Logi `hdt.transition` pokazują przyczyny zaakceptowanych zmian
i odrzuconych przejściowych odczytów, co ułatwia dalszą diagnostykę.
Przy usterkach prześlij logi JSONL z chwili awarii. Testy automatyczne
odtwarzają przejścia mulligan → tura → Odkryj → zakończenie wyboru,
przejście do następnej tury i rzeczywiste wyjście do menu.

Nie zmienia się interfejs ani DLL HDT — etap 3 wymaga tylko aktualizacji
aplikacji Windows.

## Etap 4 — prognoza lethal i obrony z jawnych danych

`DamageForecast` liczy **tylko warunkową** siłę ataku widocznych
stronników i oblicza zdrowie bohaterów z pancerzem. Dzieli:

- **Nasza tura:** atak własnych, niezamrożonych, niewyczerpanych
  jednostek o znanych statystykach.
- **Następna tura przeciwnika:** atak niezamrożonych wrogich jednostek.
  Ważne: `exhausted=true` na naszej turze nie oznacza, że wróg nie
  będzie mógł atakować po rozpoczęciu swojej następnej tury.
- **Prowokacja:** obecna Prowokacja blokuje proste założenie o
  bezpośrednim ataku w bohatera; oddzielnie pokazujemy zagrożenie
  po jej ewentualnym usunięciu.
- **Kandydaci obronni:** stronniki z ręki, którym zainstalowany HearthDb
  nadał tag `TAUNT` i których wydrukowany koszt mieści się w aktualnej
  manie. Nie jest to gwarancja ich legalności ani skuteczności w meczu.

Nie liczymy pewnego lethal z samych sum ataku. Nie przewidujemy
niewidocznych kart, sekretów, obrażeń zaklęć, broni, mocy bohatera,
aur, bonusów w czasie tury, zdolności jednostek, leczenia, zdolności
Rush/Charge ani wszystkich ograniczeń i zmian kosztu. Jeśli nie znamy
zdrowia czy typu karty, wynik pozostaje nieznany zamiast udawać zero.

AI otrzymuje `conditional_lethal_forecast` jako dodatkową
część **istniejącego pojedynczego** promptu. Nie wykonuje dodatkowych
zapytań do modelu i nie steruje grą. W razie sprzeczności lub braków
w danych opisuje warunki i niepewność.

Testy regresyjne: zdrowie + pancerz, Prowokacja obu stron, aktualna
gotowość jednostek, obrona przed potencjalnym atakiem następnej tury,
nieznane statystyki oraz weryfikacja tagów HearthDb.

## Wizualny plan tury i grafiki kart — Beta 1.0

Aplikacja Windows prezentuje teraz **kroki planu tury z miniaturami kart**.
Plan powstaje z rekomendacji AI (pierwszy krok `RUCH`, a do czterech
następnych, oddzielonych średnikami, w `POTEM`). Każda karta jest
dopasowywana **wyłącznie do identyfikatora `card_id` znanej, widocznej
karty z HDT**. Jeżeli AI poda niedopasowaną nazwę lub grafika nie jest
dostępna, pozostają numer kroku i opis — aplikacja nie przypisuje
losowego obrazu.

Podczas Odkrycia pojawia się **boczny panel** z grafikami i numerami
oferowanych kart. Rekomendowana opcja zostaje wyróżniona, np.
`WYBIERZ 2`. Plan tury **pozostaje na ekranie podczas wyboru**;
dopiero nowa tura lub istotna późniejsza analiza mogą go zmienić.
Lista kolejnych kroków jest warunkowa — skutki Odkrycia i losowych
efektów nie są przewidywane z góry.

Obraz pochodzi z
[HearthstoneJSON — Card renders](https://hearthstonejson.com/docs/images.html),
w wersji polskiej `plPL`, a gdy jest niedostępny — angielskiej
`enUS`. Po najechaniu myszą na miniaturę można zobaczyć większy
podgląd. Pobieranie odbywa się **asynchronicznie**, maksymalnie po trzy
obrazy jednocześnie, i nie spowalnia analizy modelu.
Pliki są zapisywane lokalnie w
`%LOCALAPPDATA%\\HearthstoneCoach\\card-images` (limit około 160 MiB).
Przy braku internetu wyświetlany jest czytelny tekstowy zamiennik.
Aplikacja pobiera obrazy kart z zewnętrznej domeny
`art.hearthstonejson.com` tylko na potrzeby ich wyświetlania; nie
przesyła tam danych konta ChatGPT ani pełnego stanu meczu.

Źródłowe obrazy kart Hearthstone są własnością Blizzard Entertainment.
HearthstoneJSON jest projektem HearthSim, niezależnym od Blizzard.
Nie zmieniamy gry ani nie pobieramy grafiki dla kart ukrytych.

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


## Rozpoznawanie talii i analiza tury rywala

- **Twoja talia:** nowa wtyczka HDT v0.6 udostępnia nazwę i pełny początkowy skład talii oznaczonej jako **ACTIVE** w Hearthstone Deck Tracker (identyfikator, nazwa, liczba kopii, koszt, rodzaj, opis karty). To nie są aktualne karty pozostałe w bibliotece. Gdy brak aktywnej talii lub nie zgadza się klasa, AI nie korzysta ze składu. Sama zgodność klasy **nie potwierdza**, że w partii faktycznie wybrano identyczny deck.
- **Tura przeciwnika:** po ok. 4 sekundach stabilności aplikacja wykonuje krótką analizę widocznej planszy i publicznych zagrań, pod warunkiem włączonej automatycznej analizy i zalogowanego ChatGPT. Po istotnych zmianach może wykonać jeszcze jedną analizę (maks. 2 na turę, min. 9 s między startami). Nie pokazuje podczas tego ruchów do zagrania.
- **Twoja następna tura:** wynik analizy rywala dołączany jest do kolejnego promptu jako **hipoteza**, wymagająca porównania z aktualnymi danymi z HDT; historia publicznych zagrań jest zbierana niezależnie od dostępności AI. Nie odczytujemy nieznanych kart z ręki ani talii przeciwnika.
- **Priorytet opóźnień:** tura gracza przerywa trwającą analizę przeciwnika, aby szybciej wygenerować poradę. Nieudana analiza w tle nie psuje obecnej rekomendacji. Dodatkowe wywołania mogą zużywać limit użycia modelu.
- **Aktualizacja:** aby AI znało pełny skład talii, zaktualizuj **zarówno EXE, jak i DLL wtyczki HDT**; sama aplikacja bez nowej wtyczki nadal analizuje wroga na podstawie widocznych kart.
- **Logi:** diagnostyka obejmuje `ai.opponent.scheduled`, `ai.opponent.request`, `ai.opponent.completed`, `ai.opponent.canceled`, `ai.opponent.discarded`, `ai.opponent.failure`.
