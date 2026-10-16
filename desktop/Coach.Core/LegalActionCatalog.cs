using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Visible-action candidates from HDT. These are EVIDENCE, not a complete
/// Hearthstone move generator: dynamic restrictions and effects may be missing.
/// </summary>
public static class LegalActionCatalog
{
    public sealed record HandOption(string Name, string? CardId, string? Type,
        int? Cost, bool? FitsAvailableMana);
    public sealed record BoardOption(string Name, string? CardId, string? Type,
        int? Attack, int? Health, bool? Exhausted, bool? Frozen,
        bool? PotentialAttacker, string Reason);
    public sealed record Summary(int? AvailableMana, bool? EnemyTauntPresent,
        IReadOnlyList<HandOption> Hand,
        IReadOnlyList<BoardOption> OwnBoard,
        IReadOnlyList<BoardOption> EnemyBoard,
        string Note);

    public static Summary Build(GameSnapshot snapshot)
    {
        const string caveat = "To kandydaci z jawnych danych HDT, nie pełny silnik zasad. "
            + "Brak informacji o kosztach alternatywnych, celach zaklęć, aurach, "
            + "Rush/Charge, blokadach ataku, sekretach i efektach specjalnych. "
            + "NIE nazywaj ruchu legalnym wyłącznie dlatego, że jest na tej liście.";
        if (!snapshot.InGame) return new(null, null, [], [], [], caveat);

        using var document = JsonDocument.Parse(snapshot.TacticalJson);
        var root = document.RootElement;
        var player = Property(root, "player");
        var enemy = Property(root, "opponent");
        var mana = Number(player, "mana_available");
        var hand = Enumerate(Property(player, "hand")).Select(x =>
            new HandOption(Name(x), Text(x, "card_id"), Text(x, "card_type"),
                Number(x, "cost"),
                mana.HasValue && Number(x, "cost").HasValue
                    ? Number(x, "cost")!.Value <= mana.Value : null))
            .Where(x => x.Name.Length > 0).Take(12).ToArray();

        BoardOption[] Board(JsonElement side)
        {
            return Enumerate(Property(side, "board")).Select(card =>
            {
                string? type = Text(card, "card_type");
                var attack = Number(card, "attack");
                var health = Number(card, "health");
                var exhausted = Flag(card, "exhausted");
                var frozen = Flag(card, "frozen");
                bool? possible = null;
                string why = "Niepełne dane o zdolności ataku";
                if (!string.IsNullOrWhiteSpace(type) &&
                    !string.Equals(type, "MINION", StringComparison.OrdinalIgnoreCase))
                {
                    possible = false;
                    why = "Nie jest stronnikiem";
                }
                else if (attack is <= 0 || health is <= 0)
                {
                    possible = false;
                    why = "Brak dodatniego ataku lub zdrowia";
                }
                else if (exhausted == true)
                {
                    possible = false;
                    why = "Wyczerpany";
                }
                else if (frozen == true)
                {
                    possible = false;
                    why = "Zamrożony";
                }
                else if (type == "MINION" && attack is > 0 && health is > 0 &&
                         exhausted == false && frozen == false)
                {
                    possible = true;
                    why = "Widoczne warunki spełnione; pozostałe zasady niezweryfikowane";
                }
                return new BoardOption(Name(card), Text(card, "card_id"), type,
                    attack, health, exhausted, frozen, possible, why);
            }).Where(x => x.Name.Length > 0).Take(14).ToArray();
        }

        var own = Board(player);
        var opponents = Board(enemy);
        bool? taunt = null;
        var enemyCards = Enumerate(Property(enemy, "board")).ToArray();
        if (enemyCards.Length > 0)
            taunt = enemyCards.Any(x => Flag(x, "taunt") == true);
        return new(mana, taunt, hand, own, opponents, caveat);
    }

    public static object ForPrompt(GameSnapshot snapshot)
    {
        var evidence = Build(snapshot);
        return new
        {
            mana = evidence.AvailableMana,
            hand_candidates = evidence.Hand,
            board_entities = evidence.OwnBoard,
            enemy_board_entities = evidence.EnemyBoard,
            enemy_taunt_present = evidence.EnemyTauntPresent,
            limits = evidence.Note
        };
    }

    private static JsonElement Property(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var v) ? v : default;
    private static IEnumerable<JsonElement> Enumerate(JsonElement item) =>
        item.ValueKind == JsonValueKind.Array ? item.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    private static string Name(JsonElement item) => Text(item, "name") ?? "";
    private static string? Text(JsonElement item, string key)
    {
        var value = Property(item, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? Number(JsonElement item, string key)
    {
        var value = Property(item, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : null;
    }
    private static bool? Flag(JsonElement item, string key)
    {
        var value = Property(item, key);
        return value.ValueKind == JsonValueKind.True ? true :
            value.ValueKind == JsonValueKind.False ? false : null;
    }
}
