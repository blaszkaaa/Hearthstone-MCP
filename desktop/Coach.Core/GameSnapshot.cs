using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coach.Core;

public sealed class GameSnapshot
{
    public bool Connected { get; private init; }
    public bool InGame { get; private init; }
    public bool MyTurn { get; private init; }
    public bool ChoiceActive { get; private init; }
    public bool ChoiceSupported { get; private init; }
    public int Turn { get; private init; }
    public int? Mana { get; private init; }
    public int HandCount { get; private init; }
    public int ChoicesCount { get; private init; }
    public string Fingerprint { get; private init; } = "";
    public string Status { get; private init; } = "Oczekiwanie na Hearthstone";
    public string TacticalJson { get; private init; } = "{}";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HearthstoneMCP", "state.json");

    public static GameSnapshot Read(string? path = null, DateTimeOffset? now = null)
    {
        var source = path ?? DefaultPath;
        if (!File.Exists(source)) return new() { Status = "Uruchom Hearthstone Deck Tracker i wtyczkę MCP" };
        try
        {
            var info = new FileInfo(source);
            if (info.Length > 512_000) return new() { Status = "Plik danych jest zbyt duży" };
            using var doc = JsonDocument.Parse(File.ReadAllText(source));
            var root = doc.RootElement;
            if (ReadInt(root, "schema_version") != 1)
                return new() { Status = "Nieobsługiwana wersja danych HDT" };
            var stamp = ReadString(root, "captured_at_utc");
            if (!DateTimeOffset.TryParse(stamp, out var captured))
                return new() { Status = "Brak poprawnej daty stanu HDT" };
            var age = ((now ?? DateTimeOffset.UtcNow) - captured).TotalSeconds;
            if (age > 15 || age < -5) return new() { Status = "Dane HDT nieaktualne" };
            if (!ReadBool(root, "in_game"))
                return new() { Connected = true, Status = "HDT połączony • czekam na mecz" };
            if (!root.TryGetProperty("player", out var player) ||
                !root.TryGetProperty("opponent", out var opponent) ||
                player.ValueKind != JsonValueKind.Object || opponent.ValueKind != JsonValueKind.Object)
                return new() { Connected = true, Status = "Niekompletny stan rozgrywki" };

            var choices = Get(root, "choices");
            bool supported = ReadBool(root, "choice_tracking_supported");
            bool active = supported && ReadBool(root, "choice_active");
            bool mine = ReadBool(root, "my_turn");
            var hand = Get(player, "hand");
            int count = hand.ValueKind == JsonValueKind.Array ? hand.GetArrayLength() : 0;
            int choiceCount = active && choices.ValueKind == JsonValueKind.Array ? choices.GetArrayLength() : 0;

            // Keep the prompt compact, avoiding HDT timing fields and hidden opponent data.
            var payload = new
            {
                turn = ReadInt(root, "turn"),
                my_turn = mine,
                player = new
                {
                    mana_available = ReadInt(player, "mana_available"),
                    mana_max = ReadInt(player, "mana_max"),
                    card_class = ReadString(player, "card_class"),
                    hero = GetRaw(player, "hero"),
                    weapon = GetRaw(player, "weapon"),
                    hero_power = GetRaw(player, "hero_power"),
                    deck_count = ReadInt(player, "deck_count"),
                    hand = GetRaw(player, "hand"),
                    board = GetRaw(player, "board"),
                    secrets_count = ReadInt(player, "secrets_count"),
                    played_this_turn = GetRaw(player, "played_this_turn")
                },
                opponent = new
                {
                    card_class = ReadString(opponent, "card_class"),
                    hero = GetRaw(opponent, "hero"),
                    weapon = GetRaw(opponent, "weapon"),
                    hero_power = GetRaw(opponent, "hero_power"),
                    deck_count = ReadInt(opponent, "deck_count"),
                    hand_count = ReadInt(opponent, "hand_count"),
                    board = GetRaw(opponent, "board"),
                    secrets_count = ReadInt(opponent, "secrets_count"),
                    played_this_turn = GetRaw(opponent, "played_this_turn")
                },
                choice_tracking_supported = supported,
                choice_active = active,
                choices = active ? GetRaw(root, "choices") : JsonSerializer.SerializeToElement(Array.Empty<object>())
            };
            var compact = JsonSerializer.Serialize(payload);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compact)));
            return new()
            {
                Connected = true, InGame = true, MyTurn = mine, ChoiceSupported = supported,
                ChoiceActive = active, Turn = ReadInt(root, "turn") ?? 0,
                Mana = ReadInt(player, "mana_available"), HandCount = count,
                ChoicesCount = choiceCount, TacticalJson = compact, Fingerprint = digest,
                Status = active ? "Wybór kart • potrzebna decyzja" :
                    mine ? "Twoja tura • analiza aktywna" : "Tura przeciwnika • obserwuję"
            };
        }
        catch (IOException) { return new() { Status = "HDT zapisuje dane • ponawiam odczyt" }; }
        catch (JsonException) { return new() { Status = "Niepoprawne dane HDT • ponawiam odczyt" }; }
        catch (UnauthorizedAccessException) { return new() { Status = "Brak dostępu do pliku HDT" }; }
    }

    static JsonElement Get(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var item) ? item : default;
    static JsonElement GetRaw(JsonElement obj, string key) { var v = Get(obj, key); return v.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : v; }
    static string? ReadString(JsonElement obj, string key) { var v = Get(obj, key); return v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
    static bool ReadBool(JsonElement obj, string key) { var v = Get(obj, key); return v.ValueKind == JsonValueKind.True; }
    static int? ReadInt(JsonElement obj, string key) { var v = Get(obj, key); return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null; }
}
