using System.Text.Json;
using Coach.Core;

var path = Path.Combine(Path.GetTempPath(), "hscoach-test-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var now = DateTimeOffset.UtcNow;
    var sample = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 8, my_turn = true, choice_tracking_supported = true, choice_active = true,
        choices = new[] { new { index = 1, card = new { known = true, name = "Dobra karta" } },
                          new { index = 2, card = new { known = true, name = "Słaba karta" } } },
        player = new
        {
            mana_available = 4, mana_max = 8, card_class = "MAGE",
            hand = new[] { new { name = "Fireball", cost = 4 } },
            board = Array.Empty<object>(), played_this_turn = Array.Empty<object>()
        },
        opponent = new
        {
            card_class = "PRIEST", hand_count = 3,
            board = Array.Empty<object>(), played_this_turn = Array.Empty<object>()
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(sample));
    var snapshot = GameSnapshot.Read(path, now);
    Check(snapshot.Connected && snapshot.InGame && snapshot.MyTurn, "live snapshot");
    Check(snapshot.ChoiceActive && snapshot.ChoicesCount == 2, "choices");
    Check(snapshot.Mana == 4 && snapshot.HandCount == 1, "mana and hand");
    using var tactical = JsonDocument.Parse(snapshot.TacticalJson);
    Check(tactical.RootElement.GetProperty("player").GetProperty("hand").ValueKind == JsonValueKind.Array,
        "hand must be an array, not string");
    Check(tactical.RootElement.GetProperty("choices").GetArrayLength() == 2, "choice payload");

    // Stable tactical decisions must not be invalidated by unrelated HDT
    // bookkeeping (opponent's estimated hand count / turn history).
    var noisy = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 8, my_turn = true, choice_tracking_supported = true, choice_active = true,
        choices = sample.choices, player = sample.player,
        opponent = new
        {
            card_class = "PRIEST", hand_count = 99,
            board = Array.Empty<object>(), played_this_turn = new[] { "Irrelevant event" }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(noisy));
    var noisySnapshot = GameSnapshot.Read(path, now);
    Check(noisySnapshot.DecisionKey == snapshot.DecisionKey, "ignore non-tactical metadata");
    Check(noisySnapshot.Fingerprint != snapshot.Fingerprint, "full prompt can change");

    // No analysis in the first seconds of a standard turn; Discover is faster.
    var standardTurn = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 9, my_turn = true, choice_tracking_supported = true, choice_active = false,
        choices = Array.Empty<object>(), player = sample.player, opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(standardTurn));
    var standard = GameSnapshot.Read(path, now);
    var startup = new AnalysisScheduler();
    Check(!startup.ShouldAnalyze(standard, now, false, true), "initial settling");
    Check(!startup.ShouldAnalyze(standard, now.AddSeconds(5), false, true), "not too fast at start");
    Check(startup.ShouldAnalyze(standard, now.AddSeconds(8), false, true), "stable normal turn");
    Check(!startup.ShouldAnalyze(standard, now.AddSeconds(9), false, true), "no spamming");

    var parsed = AdviceCard.Parse("RUCH: Zagraj Mrocznego jeźdźca\\nCEL: przeciwnik\\nPOTEM: użyj mocy bohatera\\nDLACZEGO: Daje przewagę na planszy.\\nUWAGA: Jeśli cel dostępny");
    Check(parsed.Move == "Zagraj Mrocznego jeźdźca", "short prominent move");
    Check(parsed.Target == "przeciwnik", "target separate");
    Check(parsed.Next == "użyj mocy bohatera", "steps before reason");
    Check(parsed.Reason.Contains("Daje przewagę") && parsed.Reason.Contains("Uwaga"), "reason last");
    Check(AdviceCard.Parse("Zagraj monetę").Move == "Zagraj monetę", "fallback model reply");

    var scheduler = new AnalysisScheduler();
    Check(!scheduler.ShouldAnalyze(snapshot, now, false, true), "debounce");
    Check(scheduler.ShouldAnalyze(snapshot, now.AddSeconds(3), false, true), "first request");
    Check(!scheduler.ShouldAnalyze(snapshot, now.AddSeconds(4), false, true), "no duplicate");
    Check(!scheduler.ShouldAnalyze(snapshot, now.AddSeconds(17), false, true), "same-state suppression");

    var modified = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(sample))!;
    // Change an observed, public field to trigger a new fingerprint.
    var changed = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 8, my_turn = true, choice_tracking_supported = true, choice_active = true,
        choices = new[] { new { index = 1, card = new { known = true, name = "Inna karta" } } },
        player = sample.player, opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(changed));
    var changedSnapshot = GameSnapshot.Read(path, now);
    Check(changedSnapshot.Fingerprint != snapshot.Fingerprint, "fingerprint on choice change");
    Check(!scheduler.ShouldAnalyze(changedSnapshot, now.AddSeconds(18), false, true), "changed state debounce");
    Check(scheduler.ShouldAnalyze(changedSnapshot, now.AddSeconds(21), false, true), "new meaningful state");

    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.AddSeconds(-45).ToString("O"), in_game = true
    }));
    Check(!GameSnapshot.Read(path, now).InGame, "stale snapshot");
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = false
    }));
    Check(GameSnapshot.Read(path, now).Connected && !GameSnapshot.Read(path, now).InGame, "menu status");
    Console.WriteLine("Hearthstone Coach core checks: PASS");
}
finally { try { File.Delete(path); } catch { } }

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Check failed: " + what);
}
