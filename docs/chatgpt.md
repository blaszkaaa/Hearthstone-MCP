# Łączenie lokalnego Hearthstone MCP z ChatGPT

ChatGPT nie uruchamia serwerów stdio na Twoim komputerze ani nie odczytuje lokalnego JSON bez połączenia. Do pracy na żywo potrzebny jest most. Preferowana metoda to **Secure MCP Tunnel** firmy OpenAI — nie wymaga wystawienia publicznego serwera z danymi o rozgrywce.

## Secure MCP Tunnel (jeśli jest dostępny dla konta i organizacji)

1. Upewnij się, że wtyczka Hearthstone MCP działa w HDT. W katalogu repozytorium uruchom `python -m mcp_server.doctor`. W menu powinien pojawić się status `waiting_for_match`, a podczas partii `ready`. Jeśli test lokalny nie działa, najpierw napraw wtyczkę, nie konfiguruj tunelu.
2. Otwórz [dokumentację Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels). W ustawieniach Platform utwórz tunel i uzyskaj `tunnel_id` oraz klucz wymagany przez klienta tunelu. Dodatkowe uprawnienia organizacji mogą być wymagane.
3. Pobierz `tunnel-client` z linku w ustawieniach tuneli OpenAI. Wykonaj `tunnel-client help quickstart`, aby sprawdzić opcje właściwe dla pobranej wersji.
4. W PowerShell ustaw wymagany klucz w sesji, **bez zapisywania go w repozytorium**.
5. Skonfiguruj lokalny profil stdio z komendą uruchamiającą `python C:\pelna\sciezka\Hearthstone-MCP\mcp_server\server.py`. Sposób cytowania ścieżek z odstępami zależy od wersji klienta tunelu.
6. Uruchom diagnostykę `tunnel-client doctor --profile local-stdio --explain`, potem `tunnel-client run --profile local-stdio`.
7. W ChatGPT (jeśli posiadasz opcję dodania niestandardowego serwera MCP): `Plugins → + → Add custom MCP server → Tunnel`; wybierz utworzony tunel i zakończ łączenie. Uruchom nowy czat, wybierz tę wtyczkę i zapytaj: „Przeczytaj read_hearthstone_state i podpowiedz ruch.”

Przykładowa komenda inicjalizacji według oficjalnej dokumentacji (podstaw własny identyfikator i ścieżkę):

```powershell
tunnel-client init --sample sample_mcp_stdio_local --profile local-stdio --tunnel-id tunnel_TWOJ_ID --mcp-command "python C:\pelna\sciezka\Hearthstone-MCP\mcp_server\server.py"
```

**Uwaga:** konfiguracja tunelu wymaga dostępu do usługi OpenAI Platform i odpowiednich uprawnień. Samo posiadanie repozytorium nie tworzy połączenia z ChatGPT. Nie przesyłaj klucza API ani pliku stanu publicznie.

## Alternatywa

Zewnętrzny serwer MCP po HTTPS ze sprawdzaniem tożsamości może obsługiwać ChatGPT, ale **nie wystawiaj anonimowo** lokalnego pliku snapshot ani serwera na publiczny adres. W MVP nie dostarczamy publicznego endpointu.

Źródła: [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels), [Connecting custom MCP servers in ChatGPT](https://help.openai.com/en/articles/12584461-connecting-custom-mcp-servers-in-chatgpt).
