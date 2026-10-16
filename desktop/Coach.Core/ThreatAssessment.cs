using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Deterministic, conservative risk hints from visible numbers only. These are
/// NOT a legality engine and never claim guaranteed lethal or trades.
/// </summary>
public static class ThreatAssessment
{
    public sealed record TheoreticalTrade(string Attacker, string Target,
        int AttackerAttack, int TargetHealth, bool SurvivesVisibleDamage);
    private sealed record Minion(string Name, int? Attack, int? Health,
        bool Taunt, bool Frozen, bool? Exhausted);

    public static object ForPrompt(GameSnapshot snapshot)
    {
        if (!snapshot.InGame) return new { available = false };
        using var document = JsonDocument.Parse(snapshot.TacticalJson);
        var root = document.RootElement;
        var player = root.GetProperty("player");
        var opponent = root.GetProperty("opponent");
        var ours = Board(player);
        var theirs = Board(opponent);

        int? ownHp = EffectiveHealth(player);
        int? theirHp = EffectiveHealth(opponent);
        int knownEnemyAttack = theirs.Where(x => x.Attack is > 0).Sum(x => x.Attack ?? 0);
        int ourPotentialAttack = ours.Where(x =>
            x.Attack is > 0 && !x.Frozen && x.Exhausted == false)
            .Sum(x => x.Attack ?? 0);

        bool taunt = theirs.Any(x => x.Taunt);
        var candidateTrades = new List<TheoreticalTrade>();
        foreach (var attacker in ours.Where(x =>
            x.Attack is > 0 && x.Health is > 0 && !x.Frozen &&
            x.Exhausted == false))
        {
            foreach (var target in theirs.Where(x => x.Health is > 0 && x.Attack is >= 0))
            {
                if (attacker.Attack < target.Health) continue;
                // This is an arithmetic possibility only. Special mechanics,
                // shields, stealth, attack restrictions etc can invalidate it.
                candidateTrades.Add(new TheoreticalTrade(attacker.Name, target.Name,
                    attacker.Attack!.Value, target.Health!.Value,
                    attacker.Health > target.Attack));
            }
        }
        var topTrades = candidateTrades
            .OrderByDescending(x => x.SurvivesVisibleDamage)
            .ThenBy(x => x.AttackerAttack - x.TargetHealth)
            .Take(4).ToArray();

        var problems = new List<string>();
        if (ownHp is null || theirHp is null)
            problems.Add("Brak kompletnych statystyk jednego z bohaterów.");
        if (taunt)
            problems.Add("Wrogi Prowokujący stronnik zwykle blokuje atak na bohatera.");
        if (theirs.Any(x => x.Exhausted is null))
            problems.Add("Nie znamy gotowości ataku części przeciwnych jednostek.");
        if (ours.Any(x => x.Exhausted is null))
            problems.Add("Nie znamy gotowości ataku części własnych jednostek.");
        if (theirs.Any(x => x.Frozen))
            problems.Add("Część wrogich jednostek jest zamrożona.");
        if (theirHp is > 0 && ourPotentialAttack >= theirHp && !taunt)
            problems.Add("Atak z planszy może osiągać lethal liczbami, ale efekty i ograniczenia nie są zweryfikowane.");
        if (ownHp is > 0 && knownEnemyAttack >= ownHp)
            problems.Add("Sama widoczna siła ataku wrogiej planszy osiąga wartość naszego zdrowia+zbroi; to ostrzeżenie, nie pewny lethal.");

        return new
        {
            available = true,
            own_hero_effective_health = ownHp,
            enemy_hero_effective_health = theirHp,
            visible_enemy_board_attack_sum = knownEnemyAttack,
            visibly_ready_friendly_board_attack_sum = ourPotentialAttack,
            enemy_taunt_present = taunt,
            board_only_enemy_lethal_warning = ownHp is > 0 && knownEnemyAttack >= ownHp,
            unverified_friendly_lethal_possibility = theirHp is > 0 && ourPotentialAttack >= theirHp && !taunt,
            theoretical_trades = topTrades,
            caution = problems.Take(5).ToArray(),
            note = "To tylko liczby z JAWNEJ planszy, nie symulacja. Brak kart z ręki przeciwnika, sekretów, efektów zaklęć, ograniczeń celów, uzdrowień i pełnej legalności ataków. NIE stwierdzaj pewnego lethal i NIE obiecuj opłacalnych wymian bez uwzględnienia efektów, Prowokacji i Boskiej Tarczy."
        };
    }

    private static IReadOnlyList<Minion> Board(JsonElement side)
    {
        if (!side.TryGetProperty("board", out var board) || board.ValueKind != JsonValueKind.Array)
            return [];
        var minions = new List<Minion>();
        foreach (var card in board.EnumerateArray())
        {
            if (card.ValueKind != JsonValueKind.Object) continue;
            // HDT board can also contain LOCATION. It must not contribute
            // attack strength or be offered as an attacking minion.
            var kind = Str(card, "card_type");
            if (!string.IsNullOrWhiteSpace(kind) &&
                !string.Equals(kind, "MINION", StringComparison.OrdinalIgnoreCase))
                continue;
            minions.Add(new Minion(
                Str(card, "name") ?? "nieznana jednostka",
                Int(card, "attack"), Int(card, "health"),
                Flag(card, "taunt") == true, Flag(card, "frozen") == true,
                Flag(card, "exhausted")));
        }
        return minions;
    }

    private static int? EffectiveHealth(JsonElement side)
    {
        if (!side.TryGetProperty("hero", out var hero) ||
            hero.ValueKind != JsonValueKind.Object) return null;
        var health = Int(hero, "health");
        if (health is null) return null;
        return Math.Max(0, health.Value) + Math.Max(0, Int(hero, "armor") ?? 0);
    }

    private static int? Int(JsonElement card, string key) =>
        card.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var n) ? n : null;
    private static string? Str(JsonElement card, string key) =>
        card.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static bool? Flag(JsonElement card, string key)
    {
        if (!card.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.True ? true :
            value.ValueKind == JsonValueKind.False ? false : null;
    }
}
