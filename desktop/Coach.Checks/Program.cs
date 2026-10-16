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
    Check(!startup.ShouldAnalyze(standard, now.AddSeconds(3), false, true), "not too fast at start");
    Check(startup.ShouldAnalyze(standard, now.AddSeconds(8), false, true), "stable normal turn");
    Check(!startup.ShouldAnalyze(standard, now.AddSeconds(9), false, true), "no spamming");

    var parsed = AdviceCard.Parse("RUCH: Zagraj Mrocznego jeźdźca\nCEL: przeciwnik\nPOTEM: użyj mocy bohatera\nDLACZEGO: Daje przewagę na planszy.\nUWAGA: Jeśli cel dostępny");
    Check(parsed.Move == "Zagraj Mrocznego jeźdźca", "short prominent move");
    Check(parsed.Target == "przeciwnik", "target separate");
    Check(parsed.Next == "użyj mocy bohatera", "steps before reason");
    Check(parsed.Reason.Contains("Daje przewagę") && parsed.Reason.Contains("Uwaga"), "reason last");
    Check(AdviceCard.Parse("Zagraj monetę").Move == "Zagraj monetę", "fallback model reply");

    // Opening mulligan is actionable even without CURRENT_PLAYER, and needs
    // an explicit one-by-one KEEP/DISCARD result.
    var opening = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        phase = "mulligan", mulligan_active = true, turn = 0, my_turn = false,
        choice_tracking_supported = true, choice_active = false,
        mulligan_cards = new[]
        {
            new { index = 1, name = "Karta A", cost = 1 },
            new { index = 2, name = "Karta B", cost = 6 },
            new { index = 3, name = "Karta C", cost = 2 }
        },
        choices = Array.Empty<object>(), player = sample.player, opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(opening));
    var openingState = GameSnapshot.Read(path, now);
    Check(openingState.MulliganActive && openingState.Actionable && openingState.Phase == "mulligan",
        "mulligan detected when not my turn");
    Check(AdvicePrompt.Build(openingState).Contains("ZOSTAW lub ODRZUĆ"), "mulligan-specific prompt");
    var mulliganAdvice = AdviceCard.Parse(
        "WYBORY:\n1 ZOSTAW — Karta A\n2 ODRZUĆ — Karta B\n3 ZOSTAW — Karta C\nDLACZEGO: Tańsze karty ułatwią start.",
        "mulligan");
    Check(mulliganAdvice.Move.Contains("1 ZOSTAW") &&
          mulliganAdvice.Move.Contains("2 ODRZUĆ") &&
          mulliganAdvice.Move.Contains("3 ZOSTAW"), "all individual mulligan decisions");
    Check(mulliganAdvice.Move.Split('\n').Length == 3, "numbered decisions have distinct lines");
    var mulliganScheduler = new AnalysisScheduler();
    Check(!mulliganScheduler.ShouldAnalyze(openingState, now, false, true), "mulligan settles");
    Check(mulliganScheduler.ShouldAnalyze(openingState, now.AddSeconds(3), false, true), "mulligan quick advice");

    var discovery = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        phase = "choice", mulligan_active = false, turn = 2, my_turn = true,
        choice_tracking_supported = true, choice_active = true, choices = sample.choices,
        player = sample.player, opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(discovery));
    var pick = GameSnapshot.Read(path, now);
    Check(pick.ChoiceActive && pick.Phase == "choice" && pick.Actionable, "Discover phase is actionable");
    Check(AdvicePrompt.Build(pick).Contains("jedną kartę", StringComparison.OrdinalIgnoreCase), "Discover-specific prompt");
    var chooseScheduler = new AnalysisScheduler();
    Check(!chooseScheduler.ShouldAnalyze(pick, now, false, true), "Discover settling");
    Check(chooseScheduler.ShouldAnalyze(pick, now.AddSeconds(2), false, true), "Discover triggers fast");
    chooseScheduler.RecordFailure(pick);
    Check(chooseScheduler.ShouldAnalyze(pick, now.AddSeconds(4), false, true), "retry failed Discover");

    // A fresh turn must never reuse the previous turn's advice key.
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        phase = "turn", mulligan_active = false, turn = 10, my_turn = true,
        choice_tracking_supported = true, choice_active = false, choices = Array.Empty<object>(),
        player = sample.player, opponent = sample.opponent
    }));
    var nextTurn = GameSnapshot.Read(path, now);
    Check(nextTurn.DecisionKey != snapshot.DecisionKey, "next turn gets different decision key");
    var turnScheduler = new AnalysisScheduler();
    Check(!turnScheduler.ShouldAnalyze(nextTurn, now, false, true), "next turn settles");
    Check(turnScheduler.ShouldAnalyze(nextTurn, now.AddSeconds(5), false, true), "next turn reanalyzes");

    // Regression: an old mulligan recommendation cannot cancel analysis of
    // turn two merely because its displayed decision key differs.
    var transition = new SnapshotTransitionGuard();
    Check(transition.HasChanged(openingState, standard, now),
        "mulligan to next turn invalidates old advice");
    transition.Reset();
    Check(!transition.HasChanged(standard, standard, now.AddSeconds(1)),
        "current request survives its own unchanged state");

    var changedWithinTurn = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        phase = "turn", mulligan_active = false, turn = 9, my_turn = true,
        choice_tracking_supported = true, choice_active = false,
        choices = Array.Empty<object>(),
        player = new
        {
            mana_available = 3, mana_max = 8, card_class = "MAGE",
            hand = new[] { new { name = "Fireball", cost = 4 } },
            board = Array.Empty<object>(), played_this_turn = Array.Empty<object>()
        },
        opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(changedWithinTurn));
    var differentMove = GameSnapshot.Read(path, now);
    Check(!transition.HasChanged(standard, differentMove, now.AddSeconds(2)),
        "transient HDT jitter does not abort request");
    Check(transition.HasChanged(standard, differentMove, now.AddSeconds(5)),
        "sustained changed decision cancels only old request");
    transition.Reset();
    Check(!transition.HasChanged(standard, standard, now.AddSeconds(6)),
        "restored original state works again");

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
    // New strategic intelligence: only printed HDT effects, visible threat
    // arithmetic and deduplicated public history observed across polling.
    var intelligenceSample = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 4, my_turn = true, choice_tracking_supported = true,
        choice_active = false, phase = "turn", mulligan_active = false,
        choices = Array.Empty<object>(),
        player = new
        {
            mana_available = 2, mana_max = 4, card_class = "MAGE",
            hand = new[] { new
            {
                known = true, card_id = "TEST_001", name = "Testowy okrzyk",
                cost = 2, card_text = "<b>Battlecry:</b> Deal 3 damage.",
                card_text_en = "Battlecry: Deal 3 damage."
            } },
            board = new[] { new
            {
                known = true, name = "Własny stronnik", card_id = "TEST_002",
                attack = 3, health = 4, exhausted = false, frozen = false, taunt = false
            } },
            hero = new { health = 4, armor = 1 },
            played_this_turn = new[] { "Znany czar" }
        },
        opponent = new
        {
            card_class = "PRIEST", hand_count = 5,
            board = new[] { new
            {
                known = true, name = "Wrogi prowokujący", card_id = "TEST_003",
                attack = 5, health = 3, exhausted = false, frozen = false,
                taunt = true, divine_shield = true
            } },
            hero = new { health = 3, armor = 0 },
            played_this_turn = new[] { "Znany stronnik" }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(intelligenceSample));
    var intelSnapshot = GameSnapshot.Read(path, now);
    var intellect = new MatchIntelligence();
    intellect.Observe(intelSnapshot, now);
    intellect.Observe(intelSnapshot, now.AddSeconds(1));
    Check(intellect.Memory.TotalObservedPlays == 2, "no repeated plays on every HDT poll");
    var knowledge = CardKnowledge.Collect(intelSnapshot);
    Check(knowledge.Any(x => x.Name == "Testowy okrzyk" &&
          x.PrintedText!.Contains("Battlecry: Deal 3 damage.") &&
          x.Keywords.Contains("Battlecry")), "printed text and keywords from HDT");
    Check(knowledge.All(x => x.Source != "enemy_hand"), "never synthesize enemy hand");

    using (var strategic = JsonDocument.Parse(intellect.BuildContext(intelSnapshot)))
    {
        var ctx = strategic.RootElement;
        var risk = ctx.GetProperty("visible_board_threats");
        Check(risk.GetProperty("visible_enemy_board_attack_sum").GetInt32() == 5, "visible pressure");
        Check(risk.GetProperty("board_only_enemy_lethal_warning").GetBoolean(), "enemy lethal warning");
        Check(risk.GetProperty("enemy_taunt_present").GetBoolean(), "enemy taunt");
        Check(!risk.GetProperty("unverified_friendly_lethal_possibility").GetBoolean(),
              "taunt blocks naive face lethal");
        Check(risk.GetProperty("theoretical_trades").GetArrayLength() > 0, "arithmetic-only trades");
        Check(ctx.GetProperty("public_match_memory")
                 .GetProperty("recent_public_plays").GetArrayLength() == 2, "public play memory");
        Check(ctx.GetProperty("public_match_memory")
                 .GetProperty("coverage").GetString() == "partial_from_app_start", "incomplete history");
    }
    var groundedPrompt = AdvicePrompt.Build(intelSnapshot, intellect.BuildContext(intelSnapshot));
    Check(groundedPrompt.Contains("DODATKOWE DOWODY") &&
          groundedPrompt.Contains("Battlecry") &&
          groundedPrompt.Contains("visible_board_threats"), "new evidence reaches one AI prompt");

    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 5, my_turn = true, phase = "turn",
        player = new { played_this_turn = new[] { "Kolejny czar" }, hand = Array.Empty<object>() },
        opponent = new { played_this_turn = Array.Empty<string>() }
    }));
    var intelTurn5 = GameSnapshot.Read(path, now);
    intellect.Observe(intelTurn5, now.AddSeconds(2));
    Check(intellect.Memory.TotalObservedPlays == 3, "history accumulates across turns");
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = false
    }));
    var intelMenu = GameSnapshot.Read(path, now);
    intellect.Observe(intelMenu, now.AddSeconds(3));
    Check(intellect.Memory.TotalObservedPlays == 3, "brief menu jitter preserves memory");
    intellect.Observe(intelMenu, now.AddSeconds(16));
    Check(intellect.Memory.TotalObservedPlays == 0, "long menu gap clears game memory");

    Console.WriteLine("Hearthstone Coach core checks: PASS");
}
finally { try { File.Delete(path); } catch { } }

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Check failed: " + what);
}
