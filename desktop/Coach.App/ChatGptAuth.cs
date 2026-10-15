using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coach.App;

/// <summary>
/// Official Sign in with ChatGPT OSS dynamic-registration flow (PKCE + OIDC).
/// OAuth credentials are encrypted using Windows DPAPI for the current user.
/// </summary>
public sealed class ChatGptAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private const string Issuer = "https://auth.openai.com";
    private const string ApiResource = "https://api.openai.com/v1";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "HearthstoneCoach");
    private static readonly string TokenPath = Path.Combine(Folder, "session.dat");
    private static readonly string HostPath = Path.Combine(Folder, "host.id");
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Session? _session;

    public bool SignedIn => _session is not null &&
        _session.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct");
    public string Email => _session?.Email ?? "Nie zalogowano";

    public ChatGptAuth()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var data = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
                _session = JsonSerializer.Deserialize<Session>(data);
            }
        }
        catch (Exception) { _session = null; } // Login again when DPAPI credential is invalid
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        string hostId;
        if (File.Exists(HostPath)) hostId = File.ReadAllText(HostPath).Trim();
        else { hostId = "urn:uuid:" + Guid.NewGuid(); File.WriteAllText(HostPath, hostId); }
        var codeVerifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(24));
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        string redirect = $"http://127.0.0.1:{port}/auth/callback";
        string clientId = _session?.ClientId ?? "dynamic_agent_client";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = ApiResource, ["state"] = state, ["nonce"] = nonce,
            ["code_challenge_method"] = "S256", ["code_challenge"] = challenge,
        };
        if (_session is null) parameters["agent_name_hint"] = "Hearthstone AI Coach";
        else if (!string.IsNullOrWhiteSpace(_session.IdToken))
            parameters["id_token_hint"] = _session.IdToken;
        string qs = string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key)
            + "=" + Uri.EscapeDataString(p.Value)));
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/auth/");
        server.Start();
        Process.Start(new ProcessStartInfo("https://auth.openai.com/api/accounts/authorize?" + qs)
            { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var ctx = await server.GetContextAsync().WaitAsync(timeout.Token);
        var query = ParseQuery(ctx.Request.Url?.Query ?? "");
        try
        {
            if (query.GetValueOrDefault("state") != state) throw new InvalidOperationException("Niepoprawny OAuth state.");
            if (query.TryGetValue("error", out var error)) throw new InvalidOperationException("Autoryzacja: " + error);
            var code = query.GetValueOrDefault("code");
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Brak kodu OAuth.");
            if (query.TryGetValue("client_id", out var returned) && clientId != "dynamic_agent_client" && returned != clientId)
                throw new InvalidOperationException("Niezgodny client_id.");
            string issued = clientId == "dynamic_agent_client" ? query.GetValueOrDefault("client_id") ?? "" : clientId;
            if (string.IsNullOrWhiteSpace(issued) || issued == "dynamic_agent_client")
                throw new InvalidOperationException("Rejestracja klienta nie została zakończona.");
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = issued,
                ["code"] = code, ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = redirect, ["resource"] = ApiResource
            });
            using var response = await Http.PostAsync(Issuer + "/api/accounts/oauth/token", body, timeout.Token);
            string raw = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Logowanie odrzucone: HTTP {(int)response.StatusCode}");
            using var token = JsonDocument.Parse(raw);
            var root = token.RootElement;
            string idToken = Required(root, "id_token");
            var identity = await ValidateIdTokenAsync(idToken, issued, nonce, timeout.Token);
            if (_session is not null && identity.Subject != _session.Subject)
                throw new InvalidOperationException("Wybrano inne konto. Wyloguj się, aby je zmienić.");
            var scopes = Get(root, "scope");
            if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct"))
                throw new InvalidOperationException("Nie przyznano uprawnienia do użycia planu ChatGPT.");
            _session = new Session(issued, identity.Subject, identity.Email, idToken,
                Required(root, "access_token"), Required(root, "refresh_token"), scopes,
                DateTimeOffset.UtcNow.AddSeconds(ReadExpires(root)));
            Save();
            await ReplyAsync(ctx, "Połączono z ChatGPT. Możesz wrócić do Hearthstone AI Coach.");
        }
        catch
        {
            await ReplyAsync(ctx, "Nie udało się połączyć. Wróć do aplikacji.");
            throw;
        }
    }

    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        if (!SignedIn) throw new InvalidOperationException("Najpierw zaloguj się przez ChatGPT.");
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_session is null) throw new InvalidOperationException("Brak sesji.");
            if (_session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3)) return _session.AccessToken;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = _session.ClientId,
                ["refresh_token"] = _session.RefreshToken, ["resource"] = ApiResource
            });
            using var result = await Http.PostAsync(Issuer + "/api/accounts/oauth/token", form, ct);
            if (!result.IsSuccessStatusCode) throw new InvalidOperationException("Sesja ChatGPT wygasła. Zaloguj się ponownie.");
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
            var value = json.RootElement;
            var scopes = Get(value, "scope");
            if (!string.IsNullOrEmpty(scopes) &&
                !scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("chatgpt.tokens.use.direct"))
                throw new InvalidOperationException("Uprawnienie do planu ChatGPT zostało cofnięte.");
            _session = _session with
            {
                AccessToken = Required(value, "access_token"),
                RefreshToken = Required(value, "refresh_token"),
                IdToken = Get(value, "id_token") is { Length: > 0 } updated ? updated : _session.IdToken,
                Scopes = string.IsNullOrEmpty(scopes) ? _session.Scopes : scopes,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ReadExpires(value))
            };
            Save();
            return _session.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task SignOutAsync()
    {
        var session = _session;
        _session = null;
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch (IOException) { }
        if (session is null) return;
        // Best effort remote revocation; tokens already removed locally.
        try
        {
            using var discovery = await Http.GetAsync(Issuer + "/.well-known/openid-configuration");
            discovery.EnsureSuccessStatusCode();
            using var meta = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
            var url = Required(meta.RootElement, "revocation_endpoint");
            if (!url.StartsWith(Issuer + "/", StringComparison.Ordinal)) return;
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = session.RefreshToken, ["token_type_hint"] = "refresh_token",
                ["client_id"] = session.ClientId
            });
            await Http.PostAsync(url, body);
        }
        catch (Exception) { /* account can also revoke from ChatGPT settings */ }
    }

    private void Save()
    {
        Directory.CreateDirectory(Folder);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_session), null, DataProtectionScope.CurrentUser);
        var temp = TokenPath + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, TokenPath, true);
    }
    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
        new string('=', (4 - value.Length % 4) % 4));
    static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2).ToDictionary(p => Uri.UnescapeDataString(p[0]),
            p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static async Task<(string Subject, string Email)> ValidateIdTokenAsync(
        string token, string audience, string nonce, CancellationToken ct)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("Niepoprawny ID token.");
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        if (Get(header.RootElement, "alg") != "RS256") throw new InvalidOperationException("Nieobsługiwany podpis ID token.");
        var claims = payload.RootElement;
        if (Get(claims, "iss").TrimEnd('/') != Issuer) throw new InvalidOperationException("Niepoprawny issuer.");
        if (Get(claims, "nonce") != nonce) throw new InvalidOperationException("Niepoprawny nonce.");
        var aud = claims.GetProperty("aud");
        bool matches = aud.ValueKind == JsonValueKind.String && aud.GetString() == audience ||
            aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.GetString() == audience);
        if (!matches) throw new InvalidOperationException("Niepoprawny audience.");
        if (!claims.TryGetProperty("exp", out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("ID token wygasł.");
        using var metadataResponse = await Http.GetAsync(Issuer + "/.well-known/openid-configuration", ct);
        metadataResponse.EnsureSuccessStatusCode();
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(ct));
        var jwksUri = Required(metadata.RootElement, "jwks_uri");
        if (!jwksUri.StartsWith(Issuer + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Nieznane JWKS.");
        using var jwksResponse = await Http.GetAsync(jwksUri, ct);
        jwksResponse.EnsureSuccessStatusCode();
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync(ct));
        string kid = Required(header.RootElement, "kid");
        bool verified = false;
        foreach (var key in jwks.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (Get(key, "kid") != kid || Get(key, "kty") != "RSA") continue;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Decode(Required(key, "n")),
                Exponent = Decode(Required(key, "e")) });
            verified = rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (verified) break;
        }
        if (!verified) throw new InvalidOperationException("ID token ma niepoprawny podpis.");
        return (Required(claims, "sub"), Get(claims, "email"));
    }

    static string Get(JsonElement o, string key) => o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    static string Required(JsonElement o, string key) => Get(o, key) is { Length: > 0 } s ? s : throw new InvalidOperationException("Niekompletna odpowiedź OpenAI: " + key);
    static int ReadExpires(JsonElement o) => o.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
    static async Task ReplyAsync(HttpListenerContext ctx, string message)
    {
        var html = "<html><meta charset='utf-8'><body style='background:#101820;color:#e5f6ee;font:20px Segoe UI;padding:40px'>" +
            WebUtility.HtmlEncode(message) + "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private sealed record Session(string ClientId, string Subject, string Email, string IdToken,
        string AccessToken, string RefreshToken, string Scopes, DateTimeOffset ExpiresAt);
}
