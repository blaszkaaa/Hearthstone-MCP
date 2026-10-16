using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Coach.Core;

namespace Coach.App;

/// <summary>Official ChatGPT plan usage: OAuth token + public Responses API, streamed, store=false.</summary>
public sealed class ChatGptInference
{
    private readonly ChatGptAuth _auth;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(95) };
    public ChatGptInference(ChatGptAuth auth) => _auth = auth;
    public sealed record ModelChoice(string Slug, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public async Task<IReadOnlyList<ModelChoice>> ModelsAsync(CancellationToken ct)
    {
        string token = await _auth.AccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Nie udało się pobrać modeli: HTTP {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Brak katalogu modeli dla tego konta.");
        var choices = new List<ModelChoice>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("visibility", out var v) && v.GetString() != "list") continue;
            if (!item.TryGetProperty("slug", out var slugEl) || slugEl.ValueKind != JsonValueKind.String) continue;
            string? slug = slugEl.GetString();
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var display = item.TryGetProperty("display_name", out var d) ? d.GetString() : null;
            choices.Add(new ModelChoice(slug, string.IsNullOrWhiteSpace(display) ? slug : display));
        }
        return choices;
    }

    public Task<string> AnalyzeAsync(GameSnapshot snapshot, string model, string prompt, CancellationToken ct)
    {
        if (!snapshot.Actionable)
            throw new InvalidOperationException("Czekam na Twoją turę lub aktywny wybór kart.");
        return RequestAsync(snapshot, model, prompt, false, ct);
    }

    public Task<string> AnalyzeOpponentAsync(GameSnapshot snapshot, string model, string prompt, CancellationToken ct)
    {
        if (!snapshot.InGame || snapshot.MyTurn || snapshot.MulliganActive || snapshot.ChoiceActive)
            throw new InvalidOperationException("Analiza rywala jest dostępna wyłącznie podczas jego tury.");
        return RequestAsync(snapshot, model, prompt, true, ct);
    }

    public Task<string> AnalyzeDeckAsync(GameSnapshot snapshot, string model, string prompt, CancellationToken ct)
    {
        if (!DeckStrategy.TryGetUsableDeck(snapshot, out _, out _))
            throw new InvalidOperationException("Brak aktywnej talii HDT do analizy.");
        return RequestAsync(snapshot, model, prompt, false, ct, deckAnalysis: true);
    }

    private async Task<string> RequestAsync(GameSnapshot snapshot, string model, string prompt, bool opponentReview, CancellationToken ct, bool deckAnalysis = false)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Wybierz model ChatGPT.");
        string token = await _auth.AccessTokenAsync(ct);
        // Spend extra reasoning only on complex normal turns. Mulligan / Discover
        // remain low-latency and opponent checks never compete with player's move.
        var (effort, limit) = DecisionReasoningBudget.Choose(snapshot, opponentReview, deckAnalysis);
        var body = new
        {
            model,
            input = new[] { new { role = "user", content = prompt } },
            store = false,
            stream = true,
            reasoning = new { effort },
            text = new { verbosity = deckAnalysis ? "medium" : "low" },
            max_output_tokens = limit
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        DiagnosticLog.Write("api.response_status", new { status = (int)response.StatusCode,
            phase = deckAnalysis ? "deck_strategy" : opponentReview ? "opponent_review" : snapshot.Phase,
            model, reasoning_effort = effort, output_token_limit = limit });
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("Osiągnięto limit ChatGPT. Sprawdź ustawienia użycia planu.");
            throw new InvalidOperationException($"Odpowiedź AI niedostępna: HTTP {(int)response.StatusCode}");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        bool completed = false;
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string data = line[6..];
            if (data == "[DONE]") break;
            using var ev = JsonDocument.Parse(data);
            if (!ev.RootElement.TryGetProperty("type", out var typ)) continue;
            string type = typ.GetString() ?? "";
            if (type == "response.output_text.delta" &&
                ev.RootElement.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                sb.Append(delta.GetString());
            else if (type == "response.failed")
            {
                var details = ev.RootElement.GetRawText();
                if (details.Contains("subscription_sharing_usage", StringComparison.Ordinal))
                    throw new InvalidOperationException("Limit użycia planu ChatGPT został osiągnięty lub jest niedostępny.");
                throw new InvalidOperationException("Model zgłosił błąd podczas analizy.");
            }
            else if (type == "response.incomplete") throw new InvalidOperationException("Analiza AI nie została ukończona.");
            else if (type == "response.completed") { completed = true; break; }
        }
        if (!completed || sb.Length == 0) throw new InvalidOperationException("Nie otrzymano kompletnej odpowiedzi AI.");
        return sb.ToString().Trim();
    }
}
