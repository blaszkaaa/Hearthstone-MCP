using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coach.Core;

/// <summary>
/// Presentation models built only from visible cards and the current AI answer.
/// A card image is resolved by explicit HDT card_id, NEVER by a fabricated name.
/// This is a plan visualization, not a claim that all later actions stay legal.
/// </summary>
public static class VisualTurnPlan
{
    public sealed record Step(int Number, string Instruction, string? Target,
        string? CardId, string? CardName);
    public sealed record Choice(int Index, string Name, string? CardId, bool Recommended);
    private sealed record Known(string Name, string? CardId);

    private static readonly Regex ValidCardId = new(
        @"\A[A-Za-z0-9_]{2,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StepNumber = new(
        @"^\s*\d+[.)]\s*", RegexOptions.Compiled);
    private static readonly Regex ChoiceIndex = new(
        @"\bwybierz\s+(?:opcj[eę]\s+)?([1-9]\d?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool SafeCardId(string? id) => id != null && ValidCardId.IsMatch(id);

    /// <summary>Keep current turn plan during Discover, not after turn/game ends.</summary>
    public static bool ShouldPreserveTurnPlan(int planTurn, GameSnapshot current) =>
        planTurn >= 0 && current.InGame && current.Turn == planTurn &&
        !current.MulliganActive && (current.MyTurn || current.ChoiceActive);

    public static IReadOnlyList<Step> BuildSteps(GameSnapshot state, AdviceCard card)
    {
        if (!state.InGame || state.MulliganActive || state.ChoiceActive) return [];
        var chunks = new List<string>();
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in Regex.Split(text, @"\s*(?:\r?\n|;|\s+\|\s+)\s*"))
            {
                var clean = StepNumber.Replace(part, "").Trim();
                if (clean.Length > 0 && !chunks.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    chunks.Add(clean);
            }
        }
        Add(card.Move);
        Add(card.Next);
        var cards = VisibleOwnCards(state);
        return chunks.Take(5).Select((text, i) =>
        {
            var match = FindCard(text, cards);
            // A target applies only to the FIRST move unless separately encoded.
            string? target = i == 0 && !string.IsNullOrWhiteSpace(card.Target)
                ? card.Target : null;
            return new Step(i + 1, text, target, match?.CardId, match?.Name);
        }).ToArray();
    }

    public static IReadOnlyList<Choice> BuildChoices(GameSnapshot state, AdviceCard? advice)
    {
        if (!state.ChoiceActive && !state.MulliganActive) return [];
        using var doc = JsonDocument.Parse(state.TacticalJson);
        var root = doc.RootElement;
        var field = state.MulliganActive ? "mulligan_cards" : "choices";
        if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        int? recommended = advice is null ? null :
            TryChoiceNumber(advice.Move);
        var list = new List<Choice>();
        int ordinal = 0;
        foreach (var element in arr.EnumerateArray())
        {
            ordinal++;
            var card = element;
            int index = ordinal;
            if (!state.MulliganActive && element.ValueKind == JsonValueKind.Object)
            {
                index = Integer(element, "index") ?? ordinal;
                if (element.TryGetProperty("card", out var nested)) card = nested;
            }
            if (card.ValueKind != JsonValueKind.Object) continue;
            var name = String(card, "name") ?? "Karta " + index;
            var id = String(card, "card_id");
            list.Add(new Choice(index, name, SafeCardId(id) ? id : null,
                recommended == index));
        }
        return list.Take(6).ToArray();
    }

    public static int? TryChoiceNumber(string? move)
    {
        var match = ChoiceIndex.Match(Normalize(move ?? ""));
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    private static List<Known> VisibleOwnCards(GameSnapshot state)
    {
        using var doc = JsonDocument.Parse(state.TacticalJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("player", out var player) ||
            player.ValueKind != JsonValueKind.Object) return [];
        var result = new List<Known>();
        void Add(JsonElement entity)
        {
            if (entity.ValueKind != JsonValueKind.Object) return;
            var id = String(entity, "card_id");
            var name = String(entity, "name");
            if (!SafeCardId(id) || string.IsNullOrWhiteSpace(name)) return;
            result.Add(new Known(name, id));
        }
        foreach (var group in new[] { "hand", "board" })
        {
            if (player.TryGetProperty(group, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var card in arr.EnumerateArray()) Add(card);
        }
        foreach (var field in new[] { "weapon", "hero_power", "hero" })
            if (player.TryGetProperty(field, out var entity)) Add(entity);
        return result;
    }

    private static Known? FindCard(string instruction, IReadOnlyList<Known> cards)
    {
        var normalized = Normalize(instruction);
        // Only match known visible own cards; longest names first avoids matching
        // e.g. "Smok" instead of "Mały Smok".
        foreach (var card in cards.OrderByDescending(x => x.Name.Length))
        {
            var full = Normalize(card.Name);
            if (full.Length >= 3 && normalized.Contains(full, StringComparison.Ordinal))
                return card;
        }
        // Polish inflection: "Ognistą Kulą" vs printed "Ognista Kula".
        // Require two separate >=5-character stems; never invent a card ID.
        foreach (var card in cards.OrderByDescending(x => x.Name.Length))
        {
            var words = Words(Normalize(card.Name)).Where(w => w.Length >= 4).ToArray();
            if (words.Length < 2) continue;
            var visible = Words(normalized);
            if (words.All(w => visible.Any(v =>
            {
                int stem = Math.Min(5, w.Length - 1);
                return v.Length >= stem &&
                    v.StartsWith(w[..stem], StringComparison.Ordinal);
            })))
                return card;
        }
        return null;
    }
    private static string[] Words(string value) =>
        Regex.Matches(value, @"[a-z0-9]+").Select(m => m.Value).ToArray();
    private static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.ToLowerInvariant().Replace('ł', 'l')
                     .Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
    private static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    private static int? Integer(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var n) ? n : null;
}
