using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Arithmetic-only face-damage forecasts from visible HDT entities. Deliberately
/// never returns "guaranteed lethal": Hearthstone effects, attack restrictions,
/// secrets, armor changes and undisclosed cards are not fully available.
/// </summary>
public static class DamageForecast
{
    public sealed record Attacker(string Name, int Attack, string Side);
    public sealed record DefensiveCard(string Name, int Cost, string Evidence);
    public sealed record Summary(
        int? OwnEffectiveHealth,
        int? EnemyEffectiveHealth,
        int ReadyFriendlyBoardDamage,
        int EnemyNextTurnBoardDamage,
        int UnknownFriendlyReadinessCount,
        int UnknownEnemyAttackCount,
        bool EnemyTauntPresent,
        bool OwnTauntPresent,
        bool? PotentialFriendlyBoardLethal,
        bool? PotentialEnemyBoardLethal,
        bool? PotentialEnemyLethalIfTauntRemoved,
        IReadOnlyList<Attacker> ReadyAttackers,
        IReadOnlyList<Attacker> EnemyThreats,
        IReadOnlyList<DefensiveCard> AffordableConfirmedTaunts,
        IReadOnlyList<string> Limitations);

    public static Summary Calculate(GameSnapshot snapshot)
    {
        if (!snapshot.InGame)
            return new(null, null, 0, 0, 0, 0, false, false, null, null, null,
                [], [], [], ["Brak aktywnego meczu."]);

        using var document = JsonDocument.Parse(snapshot.TacticalJson);
        var root = document.RootElement;
        var own = Get(root, "player");
        var enemy = Get(root, "opponent");
        var ownBoard = Array(Get(own, "board")).ToArray();
        var enemyBoard = Array(Get(enemy, "board")).ToArray();
        int? ownHp = HeroHealth(own);
        int? enemyHp = HeroHealth(enemy);

        // No positive assertion if a visible Taunt or combat status is unknown.
        bool enemyTaunt = enemyBoard.Any(x =>
            IsMinion(x) && Bool(x, "taunt") == true && Number(x, "health") is > 0);
        bool ownTaunt = ownBoard.Any(x =>
            IsMinion(x) && Bool(x, "taunt") == true && Number(x, "health") is > 0);

        var friendly = new List<Attacker>();
        var enemyThreats = new List<Attacker>();
        // HDT can expose board entities before their card type resolves.
        // Such objects do not count as attack, but MUST count as uncertainty.
        int ownUnknown = ownBoard.Count(x => Text(x, "card_type") is null);
        int enemyUnknown = enemyBoard.Count(x => Text(x, "card_type") is null);

        foreach (var card in ownBoard.Where(IsMinion))
        {
            if (Number(card, "health") is <= 0 || Number(card, "attack") is <= 0)
                continue;
            if (Number(card, "attack") is null || Number(card, "health") is null ||
                Bool(card, "exhausted") is null || Bool(card, "frozen") is null)
            {
                ownUnknown++;
                continue;
            }
            if (Bool(card, "exhausted") == false && Bool(card, "frozen") == false)
                friendly.Add(new(Name(card), Number(card, "attack")!.Value, "friendly"));
        }

        // Forecast of the OPPONENT'S NEXT turn, not current readiness.
        // Their minions being EXHAUSTED on our turn is expected and does not
        // mean they cannot attack when their next turn begins.
        foreach (var card in enemyBoard.Where(IsMinion))
        {
            if (Number(card, "health") is <= 0 || Number(card, "attack") is <= 0)
                continue;
            if (Number(card, "attack") is null || Number(card, "health") is null ||
                Bool(card, "frozen") is null)
            {
                enemyUnknown++;
                continue;
            }
            if (Bool(card, "frozen") == false)
                enemyThreats.Add(new(Name(card), Number(card, "attack")!.Value, "enemy"));
        }

        int ownDamage = friendly.Sum(x => x.Attack);
        int enemyDamage = enemyThreats.Sum(x => x.Attack);

        // Confirmed by the INSTALLED card DB, not an unverified text keyword.
        var tauntDefenses = CardKnowledge.Collect(snapshot)
            .Where(c => c.Source == "own_hand" && c.MechanicsVerified &&
                        c.ConfirmedMechanics.Contains("TAUNT", StringComparer.OrdinalIgnoreCase) &&
                        string.Equals(c.CardType, "MINION", StringComparison.OrdinalIgnoreCase) &&
                        c.Cost is >= 0)
            .Where(c => Number(own, "mana_available") is int available &&
                        c.Cost <= available)
            .Select(c => new DefensiveCard(c.Name, c.Cost!.Value,
                "TAUNT potwierdzone w bazie HearthDb, aktualna legalność niezweryfikowana"))
            .Take(5).ToArray();

        var limits = new List<string>
        {
            "Prognoza warunkowa: to wyłącznie widoczne jednostki; nie jest symulacją pełnych legalnych ataków.",
            "Nie uwzględnia obrażeń zaklęć, broni ani mocy bohatera, ukrytej ręki rywala, sekretów, uzdrawiania i efektów walki.",
            "Prowokację trzeba ominąć lub usunąć; jej obecność nie dowodzi, że atak na bohatera jest legalny."
        };
        if (ownUnknown > 0 || enemyUnknown > 0)
            limits.Add("Niepełne statystyki/gotowość części widocznych jednostek: brakującą siłę wyłączono z sum.");
        if (ownHp is null || enemyHp is null)
            limits.Add("Brak potwierdzonego zdrowia+pancerza bohatera — nie można porównać progu lethal.");
        if (enemyTaunt || ownTaunt)
            limits.Add("Wykryto jednostki z Prowokacją; prognoza obrażeń na bohatera wymaga usunięcia blokady.");
        if (tauntDefenses.Length > 0)
            limits.Add("Zagranie stronnika z Prowokacją jest kandydatem obronnym, ale nie gwarantuje przeżycia.");

        bool? ownLethal = enemyHp is > 0 && !enemyTaunt
            ? ownDamage >= enemyHp : enemyHp is null ? null : false;
        bool? enemyLethal = ownHp is > 0 && !ownTaunt
            ? enemyDamage >= ownHp : ownHp is null ? null : false;
        bool? ifTauntRemoved = ownHp is > 0
            ? enemyDamage >= ownHp : ownHp is null ? null : false;
        return new(ownHp, enemyHp, ownDamage, enemyDamage, ownUnknown,
            enemyUnknown, enemyTaunt, ownTaunt, ownLethal, enemyLethal,
            ifTauntRemoved, friendly, enemyThreats, tauntDefenses, limits);
    }

    public static object ForPrompt(GameSnapshot snapshot)
    {
        var x = Calculate(snapshot);
        return new
        {
            board_attack_this_turn_if_unblocked = x.ReadyFriendlyBoardDamage,
            enemy_board_attack_next_turn_if_unblocked = x.EnemyNextTurnBoardDamage,
            own_hero_health_plus_armor = x.OwnEffectiveHealth,
            enemy_hero_health_plus_armor = x.EnemyEffectiveHealth,
            enemy_taunt_blocks_face = x.EnemyTauntPresent,
            own_taunt_blocks_face = x.OwnTauntPresent,
            candidate_board_lethal_if_unblocked = x.PotentialFriendlyBoardLethal,
            opponent_board_lethal_if_unblocked = x.PotentialEnemyBoardLethal,
            opponent_board_lethal_if_own_taunt_removed = x.PotentialEnemyLethalIfTauntRemoved,
            friendly_attackers_with_visible_readiness = x.ReadyAttackers,
            visible_enemy_attackers_next_turn = x.EnemyThreats,
            unknown_friendly_readiness_count = x.UnknownFriendlyReadinessCount,
            unknown_enemy_attack_count = x.UnknownEnemyAttackCount,
            affordable_confirmed_taunt_candidates = x.AffordableConfirmedTaunts,
            limitations = x.Limitations,
            verdict = "NIGDY nie wnioskuj 'pewny lethal' z tych liczb. "
                + "Potencjalny lethal zależy od legalności ataków i nieznanych efektów; "
                + "stronniki przeciwnika z EXHAUSTED w naszej turze zwykle mogą atakować w swojej kolejnej turze."
        };
    }

    private static int? HeroHealth(JsonElement side)
    {
        var h = Get(side, "hero");
        var hp = Number(h, "health");
        return hp is null ? null :
            Math.Max(0, hp.Value) + Math.Max(0, Number(h, "armor") ?? 0);
    }

    private static bool IsMinion(JsonElement x)
    {
        var kind = Text(x, "card_type");
        // Older HDT versions did not export card types, so unknown stays
        // excluded rather than accidentally treating LOCATION as a minion.
        return string.Equals(kind, "MINION", StringComparison.OrdinalIgnoreCase);
    }

    private static string Name(JsonElement x) => Text(x, "name") ?? "nieznany stronnik";
    private static IEnumerable<JsonElement> Array(JsonElement x) =>
        x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().ToArray() : [];
    private static JsonElement Get(JsonElement x, string key) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement x, string key)
    {
        var v = Get(x, key);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
    private static int? Number(JsonElement x, string key)
    {
        var v = Get(x, key);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }
    private static bool? Bool(JsonElement x, string key)
    {
        var v = Get(x, key);
        return v.ValueKind == JsonValueKind.True ? true :
            v.ValueKind == JsonValueKind.False ? false : null;
    }
}
