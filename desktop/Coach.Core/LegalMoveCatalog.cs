using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coach.Core;

/// <summary>
/// Conservative, evidence-based move constraints from the HDT snapshot.
/// An action not rejected here is NOT guaranteed legal: HDT lacks many
/// targeting rules, auras, once-per-turn tags and hidden effects.
/// </summary>
public static class LegalMoveCatalog
{
    private sealed record Card(string Name, string? Type, int? Cost, int? Attack,
        int? Health, bool? Exhausted, bool? Frozen, bool? Taunt);

    private static readonly Regex AttackVerb = new(
        @"\b(zaatakuj|atakuj|zaatakuje|zaatakowac|uderz|uderzyc|wykonaj atak|attack|hit with)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PlayVerb = new(
        @"^\s*(zagraj|rzuc|wystaw|wyloz|postaw|wyposaz|play|cast)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FaceTarget = new(
        @"\b(bohater[aem]? przeciwnika|wrogiego bohatera|w twarz|face|enemy hero)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeroPower = new(
        @"\b(moc bohatera|mocy bohatera|hero power)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static object ForPrompt(GameSnapshot snapshot)
    {
        if (!snapshot.InGame || snapshot.MulliganActive || snapshot.ChoiceActive)
            return new { available = false };
        using var doc = JsonDocument.Parse(snapshot.TacticalJson);
        var root = doc.RootElement;
        var player = Get(root, "player");
        var opponent = Get(root, "opponent");
        var hand = Cards(player, "hand");
        var board = Cards(player, "board");
        var taunts = Cards(opponent, "board")
            .Where(c => c.Taunt == true && c.Health != 0 && IsMinion(c))
            .Select(c => c.Name).Distinct().Take(7).ToArray();
        var mana = Int(player, "mana_available");
        return new
        {
            available = true,
            mana,
            mana_affordable_hand_cards_not_guaranteed_playable = hand
                .Where(c => mana.HasValue && c.Cost.HasValue && c.Cost <= mana)
                .Select(c => new { c.Name, c.Type, c.Cost }).Take(10).ToArray(),
            mana_blocked_hand_cards = hand
                .Where(c => mana.HasValue && c.Cost.HasValue && c.Cost > mana)
                .Select(c => new { c.Name, c.Type, c.Cost }).Take(10).ToArray(),
            visibly_ready_board_minions_not_guaranteed_attackers = board
                .Where(c => IsMinion(c) && c.Attack > 0 && c.Health != 0 &&
                            c.Frozen == false && c.Exhausted == false)
                .Select(c => new { c.Name, c.Attack }).Take(7).ToArray(),
            enemy_taunt_minions = taunts,
            unknown_attack_readiness = board.Count(c => IsMinion(c) &&
                (c.Exhausted is null || c.Frozen is null || c.Attack is null)),
            note = "Tylko obserwowane ograniczenia. Mana i EXHAUSTED nie potwierdzają pełnej legalności; możliwe dodatkowe reguły, efekty, cele, ukryte sekrety."
        };
    }

    public static AdviceLegality.Result Validate(GameSnapshot snapshot, AdviceCard advice)
    {
        if (!snapshot.InGame || !snapshot.MyTurn ||
            snapshot.MulliganActive || snapshot.ChoiceActive)
            return AdviceLegality.Result.Ok;

        using var doc = JsonDocument.Parse(snapshot.TacticalJson);
        var root = doc.RootElement;
        var player = Get(root, "player");
        var opponent = Get(root, "opponent");
        var hand = Cards(player, "hand");
        var ownBoard = Cards(player, "board");
        var enemyTaunts = Cards(opponent, "board")
            .Where(c => IsMinion(c) && c.Taunt == true && c.Health != 0).ToArray();
        var mana = Int(player, "mana_available");

        // Check each textual action independently, including the follow-up.
        foreach (var action in SplitActions(advice.Move).Concat(SplitActions(advice.Next)))
        {
            var normalized = Normalize(action);
            if (PlayVerb.IsMatch(normalized) && mana.HasValue)
            {
                foreach (var card in hand)
                {
                    if (card.Cost > mana && Mentions(action, card.Name))
                        return new(false, $"„{card.Name}” kosztuje {card.Cost} many, a dostępne jest {mana}.");
                }
            }
            if (HeroPower.IsMatch(normalized) && mana.HasValue)
            {
                var cost = Int(Get(player, "hero_power"), "cost");
                if (cost > mana)
                    return new(false, $"Moc bohatera kosztuje {cost} many, a dostępne jest {mana}.");
            }
            if (!AttackVerb.IsMatch(normalized)) continue;

            foreach (var attacker in ownBoard.Where(c => Mentions(action, c.Name)))
            {
                if (!IsMinion(attacker)) continue; // Older HDT may omit the type.
                if (attacker.Attack == 0)
                    return new(false, $"„{attacker.Name}” ma 0 ataku.");
                if (attacker.Health == 0)
                    return new(false, $"„{attacker.Name}” ma 0 zdrowia.");
                if (attacker.Frozen == true)
                    return new(false, $"„{attacker.Name}” jest zamrożony i nie może atakować.");
                if (attacker.Exhausted == true)
                    return new(false, $"„{attacker.Name}” jest wyczerpany i nie może teraz atakować.");
            }
            if (enemyTaunts.Length > 0 &&
                (FaceTarget.IsMatch(normalized) || FaceTarget.IsMatch(Normalize(advice.Target))))
                return new(false, "Wrogi stronnik z Prowokacją blokuje zwykły atak na bohatera: " +
                    string.Join(", ", enemyTaunts.Select(c => c.Name)) + ".");
        }
        return AdviceLegality.Result.Ok;
    }

    private static bool IsMinion(Card card) =>
        string.Equals(card.Type, "MINION", StringComparison.OrdinalIgnoreCase);

    private static List<Card> Cards(JsonElement parent, string key)
    {
        var array = Get(parent, key);
        if (array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(Str(x, "name")))
            .Select(x => new Card(Str(x, "name")!, Str(x, "card_type"),
                Int(x, "cost"), Int(x, "attack"), Int(x, "health"),
                Flag(x, "exhausted"), Flag(x, "frozen"), Flag(x, "taunt")))
            .ToList();
    }

    private static IEnumerable<string> SplitActions(string? text) =>
        Regex.Split(text ?? "", @"(?:;|\n|\bpotem\b|\bnast[eę]pnie\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Where(s => !string.IsNullOrWhiteSpace(s));

    private static bool Mentions(string sentence, string name)
    {
        var tokens = Words(Normalize(sentence));
        var words = Words(Normalize(name));
        if (words.Length == 0 || tokens.Length < words.Length) return false;
        for (var i = 0; i <= tokens.Length - words.Length; i++)
        {
            var found = true;
            for (var j = 0; j < words.Length; j++)
            {
                var length = Math.Min(5, words[j].Length);
                if (tokens[i + j].Length < length ||
                    !tokens[i + j].StartsWith(words[j][..length], StringComparison.Ordinal))
                {
                    found = false;
                    break;
                }
            }
            if (found) return true;
        }
        return false;
    }

    private static string[] Words(string text) =>
        Regex.Matches(text, @"[a-z0-9]+").Select(m => m.Value).ToArray();

    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant().Replace('ł', 'l')
                     .Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static JsonElement Get(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value)
            ? value : default;
    private static string? Str(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? Int(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    }
    private static bool? Flag(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.True ? true :
            value.ValueKind == JsonValueKind.False ? false : null;
    }
}
