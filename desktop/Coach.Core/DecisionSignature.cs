using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Canonical, decision-relevant fields only. HDT emits transient zone positions,
/// localized card text, unimportant entity metadata and refresh ordering; none
/// should invalidate a recommendation while the actual decision is unchanged.
/// </summary>
public static class DecisionSignature
{
    public static string Compute(JsonElement root)
    {
        var player = Prop(root, "player");
        var opponent = Prop(root, "opponent");
        bool mulligan = Flag(root, "mulligan_active") == true ||
            String(root, "phase") == "mulligan";
        bool choice = !mulligan && Flag(root, "choice_active") == true;
        var canonical = new
        {
            turn = Integer(root, "turn"),
            my_turn = Flag(root, "my_turn"),
            phase = mulligan ? "mulligan" : choice ? "choice" : "turn",
            choice_cards = choice ? Options(Prop(root, "choices")) : Array.Empty<object>(),
            mulligan_cards = mulligan ? Cards(Prop(root, "mulligan_cards")) : Array.Empty<object>(),
            player = new
            {
                mana = Integer(player, "mana_available"),
                max_mana = Integer(player, "mana_max"),
                hand = Cards(Prop(player, "hand")),
                board = Cards(Prop(player, "board")),
                hero = Hero(Prop(player, "hero")),
                weapon = Weapon(Prop(player, "weapon")),
                hero_power = Power(Prop(player, "hero_power")),
                secrets_count = Integer(player, "secrets_count")
            },
            enemy = new
            {
                board = Cards(Prop(opponent, "board")),
                hero = Hero(Prop(opponent, "hero")),
                weapon = Weapon(Prop(opponent, "weapon")),
                secrets_count = Integer(opponent, "secrets_count")
            }
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    private static object[] Options(JsonElement choices)
    {
        if (choices.ValueKind != JsonValueKind.Array) return [];
        return choices.EnumerateArray().Select((c, i) => (object)new
        {
            index = Integer(c, "index") ?? i + 1,
            card = Card(Prop(c, "card"))
        }).ToArray();
    }

    private static object[] Cards(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Select(Card).ToArray();
    }

    private static object Card(JsonElement item) => new
    {
        card_id = String(item, "card_id"),
        card_type = String(item, "card_type"),
        name = String(item, "name"),
        known = Flag(item, "known"),
        entity_id = Integer(item, "entity_id"),
        cost = Integer(item, "cost"),
        attack = Integer(item, "attack"),
        health = Integer(item, "health"),
        taunt = Flag(item, "taunt"),
        divine_shield = Flag(item, "divine_shield"),
        exhausted = Flag(item, "exhausted"),
        frozen = Flag(item, "frozen"),
    };

    private static object Hero(JsonElement h) => new
    {
        card_id = String(h, "card_id"),
        health = Integer(h, "health"),
        armor = Integer(h, "armor"),
        attack = Integer(h, "attack"),
        frozen = Flag(h, "frozen")
    };

    private static object Weapon(JsonElement w) => new
    {
        card_id = String(w, "card_id"),
        attack = Integer(w, "attack"),
        durability_remaining = Integer(w, "durability_remaining")
    };

    private static object Power(JsonElement p) => new
    {
        card_id = String(p, "card_id"),
        cost = Integer(p, "cost")
    };

    private static JsonElement Prop(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static string? String(JsonElement e, string key)
    {
        var v = Prop(e, key);
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
    private static int? Integer(JsonElement e, string key)
    {
        var v = Prop(e, key);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }
    private static bool? Flag(JsonElement e, string key)
    {
        var v = Prop(e, key);
        return v.ValueKind == JsonValueKind.True ? true :
            v.ValueKind == JsonValueKind.False ? false : null;
    }
}
