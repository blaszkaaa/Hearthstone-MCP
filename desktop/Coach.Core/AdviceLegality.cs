using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coach.Core;

/// <summary>
/// Fail-closed checks for obvious impossible actions. This is intentionally NOT
/// a complete Hearthstone simulation; it refuses only assertions contradicted
/// by explicit observed type/zone data.
/// </summary>
public static class AdviceLegality
{
    public sealed record Result(bool Allowed, string Reason)
    {
        public static readonly Result Ok = new(true, "");
    }
    private sealed record ObservedCard(string Name, string? Type, string? Text,
        int? Attack, int? Health, bool? Exhausted, bool? Frozen);
    private static readonly Regex Attacking = new(
        @"\b(zaatakuj|atakuj|zaatakuje|zaatakowac|uderz|uderzyc|wykonaj atak|attack|hit with)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Playing = new(
        @"\b(zagraj|zagrać|wystaw|wystawiaj|rzuć|rzuc|play|cast)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RandomResolution = new(
        @"\b(rozdziel losowo|rozloz losowo|losowo rozdziel|randomly split|split randomly|random among|random enemies)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static Result Validate(GameSnapshot snapshot, AdviceCard advice)
    {
        if (!snapshot.InGame) return Result.Ok;
        using var doc = JsonDocument.Parse(snapshot.TacticalJson);
        var root = doc.RootElement;
        if (snapshot.MulliganActive) return ValidateMulligan(root, advice);
        if (snapshot.ChoiceActive) return ValidateChoice(root, advice);
        if (!root.TryGetProperty("player", out var player) ||
            !root.TryGetProperty("opponent", out var enemy)) return Result.Ok;

        var hand = Cards(player, "hand");
        var ownBoard = Cards(player, "board");
        var enemyBoard = Cards(enemy, "board");
        var mana = Int(player, "mana_available");

        // One-card actions with a known current cost greater than available mana
        // must never be advertised as immediately playable. We intentionally do
        // not validate later "POTEM" actions: preceding effects can change mana.
        if (Playing.IsMatch(Normalize(advice.Move)) && mana.HasValue)
        {
            foreach (var card in LegalActionCatalog.Build(snapshot).Hand)
            {
                if (card.Cost > mana.Value && ContainsName(advice.Move, card.Name))
                    return new(false, "„" + card.Name + "” kosztuje " +
                        card.Cost + " many, a dostępne jest tylko " + mana.Value + ".");
            }
        }

        foreach (var action in SplitActions(advice.Move).Concat(SplitActions(advice.Next)))
        {
            if (!Attacking.IsMatch(Normalize(action))) continue;
            foreach (var candidate in hand.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            {
                if (ContainsName(action, candidate.Name) &&
                    !ownBoard.Any(x => ContainsName(x.Name, candidate.Name)))
                    return new(false, "Karta „" + candidate.Name +
                        "” jest w ręce — nie może wykonywać ataku. " +
                        "Najpierw sprawdź typ i legalne zagranie.");
            }
            foreach (var candidate in ownBoard.Where(c => ContainsName(action, c.Name)))
            {
                if (!string.IsNullOrWhiteSpace(candidate.Type) &&
                    !string.Equals(candidate.Type, "MINION", StringComparison.OrdinalIgnoreCase))
                    return new(false, "„" + candidate.Name + "” ma typ " +
                        candidate.Type + " i nie wykonuje ataku stronnika.");
                if (candidate.Attack is <= 0 || candidate.Health is <= 0)
                    return new(false, "„" + candidate.Name +
                        "” nie ma dodatniego ataku/zdrowia potrzebnego do ataku.");
                if (candidate.Exhausted == true)
                    return new(false, "„" + candidate.Name +
                        "” jest wyczerpany i nie może teraz zaatakować.");
                if (candidate.Frozen == true)
                    return new(false, "„" + candidate.Name +
                        "” jest zamrożony i nie może teraz zaatakować.");
            }
            if (!string.IsNullOrWhiteSpace(advice.Target))
            {
                foreach (var candidate in hand)
                    if (ContainsName(advice.Target, candidate.Name) &&
                        !ownBoard.Concat(enemyBoard).Any(x => ContainsName(x.Name, candidate.Name)))
                        return new(false, "Cel „" + candidate.Name +
                            "” jest kartą w ręce, nie jednostką na planszy.");
            }
        }

        // Certain spells explicitly split damage randomly among enemies.
        // A hand-picked target would be an impossible instruction.
        if (!string.IsNullOrWhiteSpace(advice.Target))
        {
            foreach (var candidate in hand.Where(c =>
                string.Equals(c.Type, "SPELL", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.Text) &&
                RandomResolution.IsMatch(Normalize(c.Text))))
            {
                if (ContainsName(advice.Move, candidate.Name))
                    return new(false, "Zaklęcie „" + candidate.Name +
                        "” rozdziela obrażenia losowo — nie wybieraj ręcznie celu.");
            }
        }

        return Result.Ok;
    }

    private static Result ValidateChoice(JsonElement root, AdviceCard advice)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return Result.Ok; // No reliable choice IDs: avoid guessing.
        var match = Regex.Match(Normalize(advice.Move), @"\bwybierz\s+(\d+)\b");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var selected))
            return new(false, "Wybór Odkryj wymaga numeru opcji: «Wybierz 1/2/3».");
        var offered = choices.EnumerateArray().Select((x, index) =>
            Int(x, "index") ?? index + 1).ToArray();
        if (!offered.Contains(selected))
            return new(false, "Opcja " + selected +
                " nie występuje w aktualnym wyborze kart.");
        return Result.Ok;
    }

    private static Result ValidateMulligan(JsonElement root, AdviceCard advice)
    {
        if (!root.TryGetProperty("mulligan_cards", out var offered) ||
            offered.ValueKind != JsonValueKind.Array || offered.GetArrayLength() == 0)
            return Result.Ok;
        var lines = advice.Move.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var decisions = lines.Select(x => Regex.Match(Normalize(x.Trim()),
            @"^(\d+)[.)]?\s+(zostaw|odrzuc)\b"))
            .Where(m => m.Success).ToArray();
        int count = offered.GetArrayLength();
        if (decisions.Length != count)
            return new(false, "Mulligan wymaga ZOSTAW/ODRZUĆ osobno dla każdej z " +
                count + " kart.");
        var indices = decisions.Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        if (!indices.OrderBy(x => x).SequenceEqual(Enumerable.Range(1, count)))
            return new(false, "Mulligan ma nieprawidłowe lub powtórzone numery kart.");
        return Result.Ok;
    }

    private static IEnumerable<string> SplitActions(string text) =>
        Regex.Split(text ?? "", @"(?:;|\n|\bpotem\b|\bnast[eę]pnie\b|\ba następnie\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Where(segment => !string.IsNullOrWhiteSpace(segment));

    private static List<ObservedCard> Cards(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new ObservedCard(
                Str(x, "name") ?? "",
                Str(x, "card_type"),
                Str(x, "card_text") ?? Str(x, "card_text_en"),
                Int(x, "attack"), Int(x, "health"),
                Flag(x, "exhausted"), Flag(x, "frozen")))
            .Where(x => x.Name.Length > 0).ToList();
    }

    private static string? Str(JsonElement value, string name) =>
        value.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
    private static bool? Flag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.True ? true :
            p.ValueKind == JsonValueKind.False ? false : null;
    }

    private static bool ContainsName(string sentence, string cardName)
    {
        var tokens = Words(Normalize(sentence));
        var names = Words(Normalize(cardName));
        if (names.Length == 0 || tokens.Length < names.Length) return false;
        for (int i = 0; i <= tokens.Length - names.Length; i++)
        {
            bool all = true;
            for (int j = 0; j < names.Length; j++)
            {
                var expected = names[j];
                var received = tokens[i + j];
                int length = Math.Min(expected.Length, 5);
                if (received.Length < length ||
                    !received.StartsWith(expected[..length], StringComparison.Ordinal))
                { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    private static string[] Words(string normalized) =>
        Regex.Matches(normalized, @"[a-z0-9]+").Select(x => x.Value).ToArray();

    private static string Normalize(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant().Replace('ł', 'l')
                     .Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
