# Hearthstone AI Coach — 10 etapów

Stan aktualizuj wyłącznie po weryfikacji zmian na GitHubie. Kolejność jest obowiązkowa. Nie oznaczaj ukończenia etapu tylko dlatego, że powstał kod: wymagane są testy CI i sprawdzenie integracji; test rzeczywistej partii pozostaje osobną czynnością.

- [ ] **1. Silnik legalności ruchów** — W TRAKCIE. Podstawa: jawne dane HDT, koszt i mana, wyczerpanie, zamrożenie, atak 0, Prowokacja, jawne typy i strefy; lokalne odrzucanie sprzecznych porad. Do sprawdzenia: Windows CI, aktualność wskazówek podczas gry. Nie jest to pełny silnik zasad.
- [ ] **2. Aktualna baza typów/efektów kart i mechanik** — nie rozpoczęto.
- [ ] **3. Stabilne śledzenie stanu gry przez HDT** — nie rozpoczęto (istniejące poprawki nie są dowodem pełnego zakończenia).
- [ ] **4. Wykrywanie potencjalnego lethal i obrony przed lethal** — nie rozpoczęto.
- [ ] **5. Symulacja kilku sekwencji ruchów** — nie rozpoczęto.
- [ ] **6. Synergie i kombinacje kart** — nie rozpoczęto.
- [ ] **7. Zarządzanie czasem analiz** — nie rozpoczęto.
- [ ] **8. Probabilistyczne archetypy i ruchy rywala** — nie rozpoczęto; bez dostępu do ukrytych kart.
- [ ] **9. Plan strategiczny na cały mecz** — nie rozpoczęto.
- [ ] **10. Analiza błędów po partii** — nie rozpoczęto.

## Kryteria jakości

- Zero nowych porad sprzecznych z **potwierdzonymi** danymi; brak fałszywych deklaracji, że lista jest kompletnym zbiorem legalnych akcji.
- Nie zmieniaj ciemnego interfejsu, uwierzytelniania ani zachowania integracji HDT bez potrzeby.
- Dla każdej zmiany: regresje w Coach.Checks, build Windows, testy Python/MCP, przegląd statusu GitHub Actions.
- Nie przechowuj ani nie zgaduj ukrytej ręki przeciwnika.
- Kolejne uruchomienia kontynuują pierwszy nieukończony etap; PR/commit i wynik CI wpisuj przy nim po weryfikacji.

## Ograniczenia etapu 1

Koszt i podstawowe tagi HDT są tylko fragmentem zasad. Specjalne cele, aury, efekty chwilowe, odporność, szarża, tajemnice, blokady ataku i niestandardowe reguły kart mogą nie być dostępne. Odrzucamy tylko znane sprzeczności, zamiast błędnie certyfikować wszystkie ruchy jako legalne.
