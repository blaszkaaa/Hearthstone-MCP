using System.Text.Json;
using Coach.Core;

// Desktop release manifest must never redirect updates to arbitrary files or accept bad digests.
var version = new string('a', 40);
var checksum = new string('b', 64);
var validUpdate = AppReleaseManifest.Parse(
    JsonSerializer.Serialize(new { version, sha256 = checksum, asset = AppReleaseManifest.PackageName }));
Check(validUpdate.IsNewerThan(new string('c', 40)), "new desktop release recognized");
Check(!validUpdate.IsNewerThan(version), "same desktop commit never reinstalls");
var immutableAsset = AppReleaseManifest.ImmutablePackageName(version);
var immutableManifest = AppReleaseManifest.Parse(JsonSerializer.Serialize(new {
    version, sha256 = checksum, asset = immutableAsset
}));
Check(immutableManifest.Asset == immutableAsset,
    "SHA-specific release ZIP is accepted by the new updater");
foreach (var invalidAsset in new[] {
    AppReleaseManifest.ImmutablePackageName(new string('c', 40)),
    "HearthstoneCoach-win-x64-" + version + ".exe",
    "../../HearthstoneCoach-win-x64.zip"
})
{
    bool rejected = false;
    try {
        AppReleaseManifest.Parse(JsonSerializer.Serialize(new {
            version, sha256 = checksum, asset = invalidAsset
        }));
    }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "immutable updater asset must match target commit and exact ZIP name");
}
foreach (var bad in new[] {
    JsonSerializer.Serialize(new { version = "v2", sha256 = checksum, asset = AppReleaseManifest.PackageName }),
    JsonSerializer.Serialize(new { version, sha256 = "not-a-digest", asset = AppReleaseManifest.PackageName }),
    JsonSerializer.Serialize(new { version, sha256 = checksum, asset = "../unauthorized.exe" })
})
{
    bool rejected = false;
    try { AppReleaseManifest.Parse(bad); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "invalid desktop release manifest rejected");
}

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

    // A card description / HDT presentation position is not a new decision.
    var decoratedHand = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 8, my_turn = true, choice_tracking_supported = true, choice_active = true,
        choices = sample.choices,
        player = new
        {
            mana_available = 4, mana_max = 8, card_class = "MAGE",
            hand = new[] { new { name = "Fireball", cost = 4,
                card_text = "Informacja z HDT", position = 1 } },
            board = Array.Empty<object>(), played_this_turn = Array.Empty<object>()
        },
        opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(decoratedHand));
    var metadataSnapshot = GameSnapshot.Read(path, now);
    Check(metadataSnapshot.DecisionKey == snapshot.DecisionKey,
        "cosmetic hand card text/position does not change the decision");
    Check(metadataSnapshot.Fingerprint != snapshot.Fingerprint,
        "debug state still reflects changed printed card text");
    Check(SnapshotTransitionGuard.MatchesActionableDecision(snapshot, metadataSnapshot),
        "cosmetic snapshot can still receive result");

    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 8, my_turn = true, choice_tracking_supported = true, choice_active = true,
        choices = sample.choices,
        player = new
        {
            mana_available = 4, mana_max = 8, card_class = "MAGE",
            hand = new[] { new { name = "Fireball", cost = 5 } },
            board = Array.Empty<object>(), played_this_turn = Array.Empty<object>()
        },
        opponent = sample.opponent
    }));
    var changedCostSnapshot = GameSnapshot.Read(path, now);
    Check(changedCostSnapshot.DecisionKey != snapshot.DecisionKey,
        "changed card mana cost really changes the decision");
    Check(!SnapshotTransitionGuard.MatchesActionableDecision(snapshot, changedCostSnapshot),
        "reject old advice for a changed card");

    // The bug from the screenshot: reply was discarded once, but never retried.
    var recovery = new AnalysisScheduler();
    Check(!recovery.ShouldAnalyze(snapshot, now, false, true), "retry scenario settles");
    Check(recovery.ShouldAnalyze(snapshot, now.AddSeconds(2), false, true),
        "initial choice inference");
    Check(!recovery.ShouldAnalyze(snapshot, now.AddSeconds(3), false, true),
        "successful decision is not continuously re-requested");
    recovery.RecordFailure(snapshot); // AI reply rejected for transient HDT jitter
    Check(recovery.ShouldAnalyze(snapshot, now.AddSeconds(4), false, true),
        "stale reply re-arms same decision");
    recovery.RecordFailure(snapshot);
    Check(recovery.ShouldAnalyze(snapshot, now.AddSeconds(6), false, true),
        "up to three bounded attempts for same decision");
    recovery.RecordFailure(snapshot);
    Check(!recovery.ShouldAnalyze(snapshot, now.AddSeconds(8), false, true) &&
          recovery.LastDecision == "retry_budget_for_decision",
        "network failure cannot cause unbounded AI calls");
    var nextDecisionRetry = new AnalysisScheduler();
    Check(!nextDecisionRetry.ShouldAnalyze(snapshot, now, false, true), "next decision setup");
    Check(nextDecisionRetry.ShouldAnalyze(snapshot, now.AddSeconds(2), false, true),
        "first decision runs");
    Check(!nextDecisionRetry.ShouldAnalyze(changedCostSnapshot, now.AddSeconds(3), false, true),
        "different decision debounce");
    Check(nextDecisionRetry.ShouldAnalyze(changedCostSnapshot, now.AddSeconds(5), false, true),
        "different decision triggers even without resetting app");

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

    // Regression: Wybuchający wulkan is a SPELL in HAND, NOT a minion.
    // The effect splits damage randomly and therefore has no manually chosen target.
    var volcano = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        card_types_supported = true, turn = 3, my_turn = true,
        phase = "turn", mulligan_active = false, choice_active = false,
        player = new
        {
            mana_available = 3, mana_max = 3,
            hand = new[] { new
            {
                name = "Wybuchający wulkan", card_id = "TEST_VOLCANO",
                known = true, card_type = "SPELL", spell_school = 3, cost = 3,
                card_text = "Rozdziel losowo 3 pkt. obrażeń między wrogów."
            } },
            board = new[] { new {
                name = "Własny stronnik", card_type = "MINION",
                attack = 2, health = 3, exhausted = false
            } }
        },
        opponent = new
        {
            board = new[] { new {
                name = "Wroga jednostka", card_type = "MINION",
                attack = 2, health = 2, exhausted = false
            } }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(volcano));
    var volcanoState = GameSnapshot.Read(path, now);
    Check(volcanoState.CardTypesSupported, "HDT announces type support");
    Check(CardKnowledge.Collect(volcanoState).Any(x =>
        x.Name == "Wybuchający wulkan" && x.CardType == "SPELL"),
        "card type reaches AI knowledge");
    Check(AdvicePrompt.Build(volcanoState).Contains("SPELL to ZAKLĘCIE"),
        "model cannot conflate spells and minions");
    var wrongAttack = AdviceCard.Parse("RUCH: Zaatakuj Wybuchającym wulkanem");
    Check(!AdviceLegality.Validate(volcanoState, wrongAttack).Allowed,
        "block attacking with a spell in hand in Polish inflection");
    var wrongTarget = AdviceCard.Parse(
        "RUCH: Zagraj Wybuchający wulkan\nCEL: Wroga jednostka");
    Check(!AdviceLegality.Validate(volcanoState, wrongTarget).Allowed,
        "block manually targeting a random-split spell");
    var wrongHandTarget = AdviceCard.Parse(
        "RUCH: Zaatakuj Wybuchający wulkan\nCEL: Wybuchający wulkan");
    Check(!AdviceLegality.Validate(volcanoState, wrongHandTarget).Allowed,
        "spell in own hand is not a board attack target");
    Check(AdviceLegality.Validate(volcanoState,
        AdviceCard.Parse("RUCH: Zagraj Wybuchający wulkan")).Allowed,
        "allow casting spell without invented target");
    Check(AdviceLegality.Validate(volcanoState,
        AdviceCard.Parse("RUCH: Zaatakuj Wrogą jednostkę")).Allowed,
        "do not block attacks when no hand card is mentioned");

    // A changed card type is decision-relevant, even if its name, costs and
    // visible stats match; we must not reuse an earlier type-based recommendation.
    var fakeMinion = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        card_types_supported = true, turn = 3, my_turn = true,
        phase = "turn", mulligan_active = false, choice_active = false,
        player = new
        {
            mana_available = 3, mana_max = 3,
            hand = new[] { new
            {
                name = "Wybuchający wulkan", card_id = "TEST_VOLCANO",
                known = true, card_type = "MINION", spell_school = 3, cost = 3,
                card_text = "Rozdziel losowo 3 pkt. obrażeń między wrogów."
            } },
            board = volcano.player.board
        },
        opponent = volcano.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(fakeMinion));
    var minionState = GameSnapshot.Read(path, now);
    Check(volcanoState.DecisionKey != minionState.DecisionKey,
        "card type changes the tactical fingerprint");

    // Stage 1: action candidates and conservative legality verification.
    var legalityFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, card_types_supported = true, turn = 5, my_turn = true,
        phase = "turn", mulligan_active = false, choice_active = false,
        player = new
        {
            mana_available = 2, mana_max = 5,
            hand = new[]
            {
                new { name = "Drogi Smok", card_type = "MINION", card_id = "T1", cost = 5 },
                new { name = "Tani Czar", card_type = "SPELL", card_id = "T2", cost = 1 },
            },
            board = new[]
            {
                new { name = "Zmrożony Sługa", card_type = "MINION", card_id = "B1",
                    attack = 5, health = 3, exhausted = false, frozen = true },
                new { name = "Wyczerpany Strażnik", card_type = "MINION", card_id = "B2",
                    attack = 3, health = 4, exhausted = true, frozen = false },
                new { name = "Pacyfista", card_type = "MINION", card_id = "B3",
                    attack = 0, health = 4, exhausted = false, frozen = false },
                new { name = "Gotowy Stronnik", card_type = "MINION", card_id = "B4",
                    attack = 2, health = 4, exhausted = false, frozen = false }
            }
        },
        opponent = new
        {
            board = new[]
            {
                new { name = "Wrogi Stronnik", card_type = "MINION", card_id = "E1",
                    attack = 1, health = 2, exhausted = false, frozen = false }
            }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(legalityFixture));
    var legalState = GameSnapshot.Read(path, now);
    var catalog = LegalActionCatalog.Build(legalState);
    Check(catalog.AvailableMana == 2 && catalog.Hand.Count == 2, "candidate catalog reads mana and hand");
    Check(catalog.Hand.First(x => x.Name == "Drogi Smok").FitsAvailableMana == false,
        "expensive card not payable with available mana");
    Check(catalog.Hand.First(x => x.Name == "Tani Czar").FitsAvailableMana == true,
        "affordable card passes visible mana check");
    Check(catalog.OwnBoard.First(x => x.Name == "Zmrożony Sługa").PotentialAttacker == false,
        "frozen minion excluded");
    Check(catalog.OwnBoard.First(x => x.Name == "Wyczerpany Strażnik").PotentialAttacker == false,
        "exhausted minion excluded");
    Check(catalog.OwnBoard.First(x => x.Name == "Pacyfista").PotentialAttacker == false,
        "zero-attack minion excluded");
    Check(catalog.OwnBoard.First(x => x.Name == "Gotowy Stronnik").PotentialAttacker == true,
        "ready minion is only a potential attacker");
    Check(!AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zagraj Drogiego Smoka")).Allowed,
        "block playing 5-cost card with only 2 mana");
    Check(AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zagraj Tani Czar")).Allowed,
        "allow visible affordable spell");
    Check(!AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zaatakuj Zmrożonym Sługą")).Allowed,
        "block frozen attacker");
    Check(!AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zaatakuj Wyczerpanym Strażnikiem")).Allowed,
        "block exhausted attacker");
    Check(!AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zaatakuj Pacyfistą")).Allowed,
        "block attacker with zero attack");
    Check(AdviceLegality.Validate(legalState, AdviceCard.Parse("RUCH: Zaatakuj Gotowym Stronnikiem")).Allowed,
        "do not block attack supported by visible tags");
    Check(!AdviceLegality.Validate(snapshot,
        AdviceCard.Parse("RUCH: Wybierz 9 — karta")).Allowed,
        "reject unoffered Discover option");
    Check(AdviceLegality.Validate(snapshot,
        AdviceCard.Parse("RUCH: Wybierz 2 — karta")).Allowed,
        "allow offered Discover option");
    Check(!AdviceLegality.Validate(snapshot,
        AdviceCard.Parse("RUCH: Zagraj Fireball")).Allowed,
        "Discover must not be replaced by normal card play");
    Check(AdviceLegality.Validate(openingState, mulliganAdvice).Allowed,
        "complete indexed mulligan stays accepted");
    Check(!AdviceLegality.Validate(openingState, AdviceCard.Parse(
        "WYBORY:\\n1 ZOSTAW — Karta A\\n2 ODRZUĆ — Karta B",
        "mulligan")).Allowed,
        "incomplete mulligan requires all choices");

    var enriched = new MatchIntelligence().BuildContext(legalState);
    using (var contextDoc = JsonDocument.Parse(enriched))
    {
        var candidates = contextDoc.RootElement.GetProperty("candidate_actions");
        Check(candidates.GetProperty("hand_candidates").GetArrayLength() == 2,
            "single-call prompt contains visible hand candidates");
        Check(candidates.GetProperty("board_entities").GetArrayLength() == 4,
            "single-call prompt contains per-minion action evidence");
    }

    // Stage 2: distinguish authoritative mechanic TAGS from text hints.
    // Two copies with the same card ID must not collapse into one hand option.
    var taggedCards = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        card_evidence_schema = 2,
        card_catalog_source = "HearthDb (installed HDT)",
        card_catalog_assembly_version = "1.2.3.4",
        turn = 4, my_turn = true, phase = "turn",
        player = new
        {
            hand = new[] {
                new { known = true, entity_id = 101, card_id = "TEST_A",
                    card_dbf_id = 12345, name = "Testowy Okrzyk",
                    card_type = "MINION", cost = 2,
                    card_text = "<b>Battlecry:</b> Deal damage.",
                    card_text_en = "Battlecry: Deal damage.",
                    printed_data_source = "HearthDb",
                    printed_mechanics = new[] { "BATTLECRY", "TAUNT" } },
                new { known = true, entity_id = 102, card_id = "TEST_A",
                    card_dbf_id = 12345, name = "Testowy Okrzyk",
                    card_type = "MINION", cost = 2,
                    card_text = "<b>Battlecry:</b> Deal damage.",
                    card_text_en = "Battlecry: Deal damage.",
                    printed_data_source = "HearthDb",
                    printed_mechanics = new[] { "BATTLECRY", "TAUNT" } }
            },
            board = Array.Empty<object>()
        },
        opponent = new { board = Array.Empty<object>() }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(taggedCards));
    var tagsSnapshot = GameSnapshot.Read(path, now);
    var exactCards = CardKnowledge.Collect(tagsSnapshot);
    Check(exactCards.Count(x => x.Source == "own_hand") == 2,
        "two identical hand cards retained by entity ID");
    Check(exactCards.All(x => x.DbfId == 12345 &&
        x.MechanicsVerified && x.ConfirmedMechanics.Contains("BATTLECRY")),
        "dbf ID and HearthDb mechanics are confirmed");
    using (var d = JsonDocument.Parse(JsonSerializer.Serialize(CardKnowledge.ForPrompt(tagsSnapshot))))
    {
        var data = d.RootElement;
        Check(data.GetProperty("card_evidence_schema").GetInt32() == 2,
            "new card-evidence schema version");
        Check(data.GetProperty("catalog_assembly_version").GetString() == "1.2.3.4",
            "provenance reports actual installed catalog assembly");
        var first = data.GetProperty("printed_card_effects")[0];
        Check(first.GetProperty("mechanic_tags_status").GetString() == "catalog_confirmed",
            "confirmed tags identified distinctly from text");
        Check(first.GetProperty("confirmed_printed_mechanics").GetArrayLength() == 2,
            "both HearthDb mechanics exported without guessing");
        Check(first.GetProperty("unverified_text_hints").GetArrayLength() > 0,
            "description words remain explicitly unverified");
    }

    // Legacy/partial HDT exports: even a keyword-looking description does NOT
    // imply that its mechanic was confirmed by HearthDb.
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 4, my_turn = true, phase = "turn",
        player = new
        {
            hand = new[] {
                new { name = "Nieznana karta", card_id = "OLD_B",
                    card_type = "MINION", card_text = "Taunt", cost = 1 },
                new { name = "Nieznana karta", card_id = "OLD_B",
                    card_type = "MINION", card_text = "Taunt", cost = 1 }
            },
            board = Array.Empty<object>()
        },
        opponent = new { board = Array.Empty<object>() }
    }));
    var olderSnapshot = GameSnapshot.Read(path, now);
    var olderKnowledge = CardKnowledge.Collect(olderSnapshot);
    Check(olderKnowledge.Count == 2, "duplicate legacy cards remain distinct");
    Check(olderKnowledge.All(x => !x.MechanicsVerified &&
        x.ConfirmedMechanics.Length == 0 && x.Keywords.Contains("Taunt")),
        "legacy description hint must NOT become authoritative mechanic");
    using (var d = JsonDocument.Parse(JsonSerializer.Serialize(CardKnowledge.ForPrompt(olderSnapshot))))
    {
        var info = d.RootElement.GetProperty("printed_card_effects")[0];
        Check(info.GetProperty("mechanic_tags_status").GetString() == "not_provided" &&
            info.GetProperty("confirmed_printed_mechanics").ValueKind == JsonValueKind.Null,
            "missing mechanic tags distinguish unknown from confirmed empty list");
    }
    var promptWithEvidence = AdvicePrompt.Build(tagsSnapshot,
        new MatchIntelligence().BuildContext(tagsSnapshot));
    Check(promptWithEvidence.Contains("catalog_confirmed") &&
        promptWithEvidence.Contains("unverified_text_hints"),
        "evidence status reaches model prompt");

    // One-click updater: atomic replacement, checksum verification and rollback
    // copy. These tests run on the Windows CI runner; they do not touch HDT.
    var installFixture = Path.Combine(Path.GetTempPath(), "hs-plugin-swap-" +
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(installFixture);
    try
    {
        var download = Path.Combine(installFixture, "downloaded.dll");
        var installed = Path.Combine(installFixture, "HearthstoneMcpPlugin.dll");
        File.WriteAllText(download, "NEW_VERSION_FROM_SIGNED_RELEASE");
        File.WriteAllText(installed, "OLD_WORKING_VERSION");
        var expected = PluginFileSwap.Sha256Of(download);
        Check(PluginFileSwap.ValidDigest(expected), "release SHA256 format");
        Check(PluginFileSwap.Install(download, installed, expected), "verified DLL replaced");
        Check(File.ReadAllText(installed) == "NEW_VERSION_FROM_SIGNED_RELEASE",
            "destination is new release");
        Check(File.ReadAllText(installed + ".bak") == "OLD_WORKING_VERSION",
            "old installed DLL backed up");
        Check(!PluginFileSwap.Install(download, installed, expected),
            "identical installed plugin never causes unnecessary HDT restart");
        File.WriteAllText(download, "TAMPERED");
        bool refused = false;
        try { PluginFileSwap.Install(download, installed, expected); }
        catch (InvalidDataException) { refused = true; }
        Check(refused && File.ReadAllText(installed) == "NEW_VERSION_FROM_SIGNED_RELEASE",
            "tampered download must not overwrite the installed plugin");
        Check(!Directory.EnumerateFiles(installFixture, "*.tmp").Any(),
            "temporary files always cleaned up");
    }
    finally
    {
        Directory.Delete(installFixture, recursive: true);
    }

    // Stage 3: HDT may briefly hide Discover offers, report "menu" during a
    // transition or reorder entity metadata. Confirm normal states without
    // delaying urgent Discover entries or real next-turn events.
    var transitions = new HdtStateReconciler();
    Check(ReferenceEquals(transitions.Observe(openingState, now), openingState),
        "initial mulligan is available immediately");
    Check(ReferenceEquals(transitions.Observe(standard, now.AddSeconds(1)), standard),
        "real next turn takes priority over mulligan");
    var choiceAtTurnNine = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 9, my_turn = true, choice_tracking_supported = true,
        mulligan_active = false, choice_active = true, phase = "choice",
        choices = sample.choices, player = sample.player, opponent = sample.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(choiceAtTurnNine));
    var urgentChoice = GameSnapshot.Read(path, now);
    Check(ReferenceEquals(transitions.Observe(urgentChoice, now.AddSeconds(2)), urgentChoice) &&
          transitions.LastEvent == "choice_entered", "Discover offer processed immediately");

    // If offers briefly disappear during an animation, never replace the
    // pending choice with a regular move. Confirm that the disappearance lasts.
    Check(ReferenceEquals(transitions.Observe(standard, now.AddSeconds(2.3)), urgentChoice),
        "one disappearing-choice frame ignored");
    Check(ReferenceEquals(transitions.Observe(urgentChoice, now.AddSeconds(2.5)), urgentChoice),
        "return of Discover resets disappearance candidate");
    Check(ReferenceEquals(transitions.Observe(standard, now.AddSeconds(3)), urgentChoice),
        "end of Discover is not prematurely accepted");
    Check(ReferenceEquals(transitions.Observe(standard, now.AddSeconds(4)), standard) &&
          transitions.LastEvent == "phase_changed", "confirmed Discover ending processed");

    // Same-phase tactical changes need confirmation to survive transient
    // animations, while a real new turn is accepted without extra waiting.
    Check(ReferenceEquals(transitions.Observe(differentMove, now.AddSeconds(4.2)), standard),
        "one transient same-turn change ignored");
    Check(ReferenceEquals(transitions.Observe(standard, now.AddSeconds(4.4)), standard),
        "transient move resets candidate");
    Check(ReferenceEquals(transitions.Observe(differentMove, now.AddSeconds(5)), standard),
        "new candidate stabilizes before acceptance");
    Check(ReferenceEquals(transitions.Observe(differentMove, now.AddSeconds(6)), differentMove),
        "stable mana/hand change eventually accepted");
    Check(ReferenceEquals(transitions.Observe(nextTurn, now.AddSeconds(7)), nextTurn) &&
          transitions.LastEvent == "next_turn", "following turn cannot remain stuck on previous advice");

    // Brief menu or inaccessible JSON must not interrupt an in-progress turn.
    Check(ReferenceEquals(transitions.Observe(intelMenu, now.AddSeconds(7.2)), nextTurn),
        "short HDT menu flicker suppressed");
    Check(ReferenceEquals(transitions.Observe(nextTurn, now.AddSeconds(8)), nextTurn),
        "recovered HDT state restores original transition");
    Check(ReferenceEquals(transitions.Observe(intelMenu, now.AddSeconds(9)), nextTurn),
        "menu begins confirmation window");
    Check(ReferenceEquals(transitions.Observe(intelMenu, now.AddSeconds(12)), intelMenu) &&
          transitions.LastEvent == "menu_confirmed", "actual match exit is reflected");
    Check(ReferenceEquals(transitions.Observe(openingState, now.AddSeconds(13)), openingState) &&
          transitions.LastEvent == "mulligan_entered", "next game's mulligan starts fresh");

    // Stage 4: lethal/defense must use explicit visible HDT state, NOT assume
    // opponent EXHAUSTED this turn implies they cannot attack NEXT turn.
    var forecastFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, card_types_supported = true, turn = 6, my_turn = true,
        phase = "turn", mulligan_active = false, choice_active = false,
        player = new
        {
            mana_available = 2, mana_max = 6,
            hero = new { health = 4, armor = 1 },
            hand = new object[] {
                new { name = "Obronny Mur", card_type = "MINION", card_id = "TAUNT_A",
                    known = true, cost = 2, printed_data_source = "HearthDb",
                    printed_mechanics = new[] { "TAUNT" } },
                new { name = "Drogie Wsparcie", card_type = "MINION", card_id = "TAUNT_B",
                    known = true, cost = 7, printed_data_source = "HearthDb",
                    printed_mechanics = new[] { "TAUNT" } },
                new { name = "Pozorna Prowokacja", card_type = "MINION", card_id = "TAUNT_C",
                    known = true, cost = 1, card_text = "Taunt" }
            },
            board = new object[] {
                new { name = "Gotowy A", card_type = "MINION",
                    attack = 3, health = 3, frozen = false, exhausted = false,
                    taunt = false },
                new { name = "Gotowy B", card_type = "MINION",
                    attack = 2, health = 3, frozen = false, exhausted = false,
                    taunt = true },
                new { name = "Zamrożony", card_type = "MINION",
                    attack = 8, health = 5, frozen = true, exhausted = false,
                    taunt = false },
                new { name = "Wyczerpany", card_type = "MINION",
                    attack = 7, health = 5, frozen = false, exhausted = true,
                    taunt = false },
                new { name = "Lokalizacja", card_type = "LOCATION",
                    attack = 20, health = 5, frozen = false, exhausted = false,
                    taunt = false }
            }
        },
        opponent = new {
            hero = new { health = 5, armor = 0 },
            board = new object[] {
                new { name = "Wróg A", card_type = "MINION",
                    attack = 4, health = 4, frozen = false, exhausted = true,
                    taunt = false },
                new { name = "Wrogi prowokator", card_type = "MINION",
                    attack = 1, health = 1, frozen = false, exhausted = true,
                    taunt = true },
                new { name = "Wrogi zamrożony", card_type = "MINION",
                    attack = 6, health = 2, frozen = true, exhausted = true,
                    taunt = false }
            }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(forecastFixture));
    var forecastState = GameSnapshot.Read(path, now);
    var forecast = DamageForecast.Calculate(forecastState);
    Check(forecast.OwnEffectiveHealth == 5 &&
          forecast.EnemyEffectiveHealth == 5, "lethal accounts for hero armor");
    Check(forecast.ReadyFriendlyBoardDamage == 5,
        "frozen exhausted and location excluded from ready damage");
    Check(forecast.EnemyNextTurnBoardDamage == 5,
        "enemy exhausted on our turn is still a NEXT turn threat");
    Check(forecast.EnemyTauntPresent && forecast.OwnTauntPresent,
        "both sides taunt restrictions detected");
    Check(forecast.PotentialFriendlyBoardLethal == false &&
          forecast.PotentialEnemyBoardLethal == false &&
          forecast.PotentialEnemyLethalIfTauntRemoved == true,
        "taunt prevents unqualified face lethal claim on both sides");
    Check(forecast.AffordableConfirmedTaunts.Count == 1 &&
          forecast.AffordableConfirmedTaunts[0].Name == "Obronny Mur",
        "defensive taunts require verified HearthDb tags and enough mana");
    Check(forecast.Limitations.Any(x => x.Contains("nie jest symulacją")),
        "lethal warning must contain explicit limitations");
    using (var structured = JsonDocument.Parse(
        JsonSerializer.Serialize(DamageForecast.ForPrompt(forecastState))))
    {
        var data = structured.RootElement;
        Check(data.GetProperty("candidate_board_lethal_if_unblocked").GetBoolean() == false,
            "blocked lethal exposed conservatively");
        Check(data.GetProperty("opponent_board_lethal_if_own_taunt_removed").GetBoolean(),
            "conditional enemy lethal warnings available to model");
        Check(data.GetProperty("affordable_confirmed_taunt_candidates").GetArrayLength() == 1,
            "affordable defensive option available to the AI");
    }
    var detailedPrompt = AdvicePrompt.Build(forecastState,
        new MatchIntelligence().BuildContext(forecastState));
    Check(detailedPrompt.Contains("conditional_lethal_forecast") &&
          detailedPrompt.Contains("Wrogi EXHAUSTED"),
        "model receives warning that exhaustion expires on enemy's next turn");

    // A case without Taunt can be lethal by board arithmetic, still conditional
    // because gameplay effects and secret cards have not been simulated.
    var unblockedScenario = new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true, phase = "turn",
        player = new {
            hero = new { health = 4, armor = 0 },
            board = new[] {
                new { name = "Atak", card_type = "MINION", attack = 5, health = 3,
                    frozen = false, exhausted = false, taunt = false }
            }, hand = Array.Empty<object>()
        },
        opponent = new {
            hero = new { health = 5, armor = 0 },
            board = new[] {
                new { name = "Wróg", card_type = "MINION", attack = 4, health = 2,
                    frozen = false, exhausted = true, taunt = false }
            }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(unblockedScenario));
    var unblockedState = GameSnapshot.Read(path, now);
    var unblockedForecast = DamageForecast.Calculate(unblockedState);
    Check(unblockedForecast.PotentialFriendlyBoardLethal == true,
        "potential friendly lethal requires no taunt and enough visible damage");
    Check(unblockedForecast.PotentialEnemyBoardLethal == true,
        "enemy next-turn lethal potential includes exhausted enemy attacker");

    // No health or card type => unknown, NOT an optimistic lethal calculation.
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true, phase = "turn",
        player = new { hero = new { armor = 5 }, hand = Array.Empty<object>(),
            board = new[] { new { name = "Niewiadoma", attack = 99, health = 4 } } },
        opponent = new { hero = new { armor = 0 }, board = Array.Empty<object>() }
    }));
    var unknownForecast = DamageForecast.Calculate(GameSnapshot.Read(path, now));
    Check(unknownForecast.PotentialFriendlyBoardLethal is null &&
          unknownForecast.PotentialEnemyBoardLethal is null &&
          unknownForecast.ReadyFriendlyBoardDamage == 0,
        "missing hero health and card types must not create imaginary lethal");

    // Visual plan: only actual HDT card IDs, never fabricate image links.
    var pictureFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true, phase = "turn",
        player = new
        {
            mana_available = 4, mana_max = 6,
            hand = new[] { new { name = "Ognista Kula", card_id = "CS2_029", cost = 4,
                card_type = "SPELL" } },
            board = new[] { new { name = "Własny Sługa", card_id = "EX1_001",
                card_type = "MINION", attack = 2, health = 3, exhausted = false } }
        },
        opponent = new { board = Array.Empty<object>() }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(pictureFixture));
    var pictureState = GameSnapshot.Read(path, now);
    var stepAdvice = AdviceCard.Parse(
        "RUCH: Zagraj Ognistą Kulę\nCEL: Wroga jednostka\n" +
        "POTEM: 2. Zaatakuj Własnym Sługą; 3. Zakończ turę\n" +
        "DLACZEGO: Testowy plan.");
    var visualSteps = VisualTurnPlan.BuildSteps(pictureState, stepAdvice);
    Check(visualSteps.Count == 3 &&
        visualSteps[0].CardId == "CS2_029" &&
        visualSteps[1].CardId == "EX1_001" &&
        visualSteps[2].CardId == null,
        "full turn sequence maps only genuinely visible cards to art");
    // Regression: a busy board can require more than five distinct actions.
    var longTurn = AdviceCard.Parse("RUCH: Zagraj Ognistą Kulę\\n".Replace("\\n", "\n") +
        "POTEM: " + string.Join("; ", Enumerable.Range(2, 9)
            .Select(i => i + ". Akcja " + i + " - sprawdz stan planszy " + new string('x', 45))) +
        "\nDLACZEGO: Test.");
    var longSteps = VisualTurnPlan.BuildSteps(pictureState, longTurn);
    Check(longSteps.Count == 10 && longSteps[^1].Number == 10 &&
        longSteps[^1].Instruction.Contains("Akcja 10"),
        "turn plan renders every model-provided step without a five-action cap");
    Check(longTurn.Next.Length > 420,
        "AI instructions after the first move are not truncated at 420 characters");
    Check(AdvicePrompt.Build(pictureState).Contains("NIE urywaj planu po 5 krokach"),
        "AI no longer asks for only four follow-up actions");
    Check(visualSteps[0].Target == "Wroga jednostka" &&
        visualSteps[1].Target == null,
        "target of first move does not incorrectly carry to later steps");
    Check(!VisualTurnPlan.SafeCardId("../secret") &&
        !VisualTurnPlan.SafeCardId("http://evil") &&
        VisualTurnPlan.SafeCardId("CS2_029"),
        "card images only accept safe validated HDT IDs");
    var pickFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true, phase = "choice",
        choice_active = true, choice_tracking_supported = true,
        choices = new[] {
            new { index = 1, card = new { name = "Opcja A", card_id = "EX1_001" } },
            new { index = 2, card = new { name = "Opcja B", card_id = "CS2_029" } },
            new { index = 3, card = new { name = "Opcja C", card_id = "../oops" } }
        },
        player = new { hand = Array.Empty<object>(), board = Array.Empty<object>() },
        opponent = new { board = Array.Empty<object>() }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(pickFixture));
    var pickPicture = GameSnapshot.Read(path, now);
    Check(VisualTurnPlan.ShouldPreserveTurnPlan(6, pickPicture),
        "existing turn plan persists when Discover temporarily interrupts play");
    Check(!VisualTurnPlan.ShouldPreserveTurnPlan(5, pickPicture) &&
        !VisualTurnPlan.ShouldPreserveTurnPlan(6, new GameSnapshot()),
        "previous-turn or finished-game plan must not linger");
    var selectedCard = AdviceCard.Parse("RUCH: Wybierz 2 — Opcja B", "choice");
    var visualChoices = VisualTurnPlan.BuildChoices(pickPicture, selectedCard);
    Check(VisualTurnPlan.BuildSteps(pickPicture, selectedCard).Count == 0,
        "a Discover decision is not a replacement turn plan");
    Check(visualChoices.Count == 3 &&
        visualChoices[1].Recommended && !visualChoices[0].Recommended &&
        visualChoices[1].CardId == "CS2_029" &&
        visualChoices[2].CardId == null,
        "Discover graphics preserve order and highlight the offered choice");
    Check(VisualTurnPlan.TryChoiceNumber("Wybierz opcję 3 — karta") == 3,
        "choice parser accepts Polish option numbering");

    // Mulligan layout receives one safe label per indexed physical card.
    var mulliganPictureCards = VisualTurnPlan.BuildChoices(openingState, null);
    Check(mulliganPictureCards.Count == 3 &&
        mulliganPictureCards[0].Name == "Karta A" &&
        mulliganPictureCards[1].Name == "Karta B",
        "mulligan card art strip preserves HDT order");
    var mulliganLabels = VisualTurnPlan.ParseMulliganDecisions(mulliganAdvice.Move);
    Check(mulliganLabels.Count == 3 &&
        mulliganLabels[1] == "ZOSTAW" &&
        mulliganLabels[2] == "ODRZUĆ" &&
        mulliganLabels[3] == "ZOSTAW",
        "each mulligan card gets a distinct visible keep/discard label");
    Check(VisualTurnPlan.ParseMulliganDecisions("1 ODRZUC — A\n4 ZOSTAW — D")[1] == "ODRZUĆ",
        "ASCII mulligan spelling supported");
    Check(!VisualTurnPlan.ParseMulliganDecisions("1 ZOSTAW — A\n1 ODRZUĆ — A")
        .ContainsKey(1), "conflicting duplicate mulligan answer is not displayed as certain");

    // Torch / Pochodnia has a mutable script tag: 8 printed damage is NOT
    // proof of its real value after returning to the player's hand.
    var torchFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 7, my_turn = true, phase = "turn",
        player = new
        {
            mana_available = 3, mana_max = 7,
            hand = new[] { new
            {
                known = true, card_id = "CATA_585", name = "Pochodnia",
                card_type = "SPELL", cost = 1, card_text = "Zadaj 8 obrażeń",
                live_damage = 2, live_damage_source = "TAG_SCRIPT_DATA_NUM_1"
            } },
            board = Array.Empty<object>(), played_this_turn = new[] { "Pochodnia" }
        },
        opponent = sample.opponent
    };
    var torchJson = JsonSerializer.Serialize(torchFixture);
    File.WriteAllText(path, torchJson);
    var torchState = GameSnapshot.Read(path, now);
    var torchCard = CardKnowledge.Collect(torchState).Single(x => x.CardId == "CATA_585");
    Check(torchCard.LiveDamage == 2 && torchCard.PrintedText!.Contains("8"),
        "live Torch damage overrides printed catalog damage in AI evidence");
    var intelTorch = new MatchIntelligence();
    intelTorch.Observe(torchState, now);
    intelTorch.Observe(torchState, now.AddSeconds(1));
    using (var context = JsonDocument.Parse(intelTorch.BuildContext(torchState)))
    {
        var root = context.RootElement;
        var printed = root.GetProperty("card_knowledge").GetProperty("printed_card_effects")[0];
        Check(printed.GetProperty("current_live_damage").GetInt32() == 2 &&
              printed.GetProperty("live_damage_status").GetString() == "verified_entity_tag",
              "prompt includes verified actual Torch damage");
        Check(root.GetProperty("public_match_memory")
                  .GetProperty("observed_own_torch_plays").GetInt32() == 1,
              "Torch history survives polling without duplicate plays");
    }
    Check(AdvicePrompt.Build(torchState).Contains("current_live_damage"),
        "AI must consider Torch remaining damage rather than printed amount");
    File.WriteAllText(path, torchJson.Replace("\"live_damage\":2", "\"live_damage\":1"));
    var weakerTorch = GameSnapshot.Read(path, now);
    Check(weakerTorch.DecisionKey != torchState.DecisionKey &&
          CardKnowledge.Collect(weakerTorch).Single(x => x.CardId == "CATA_585").LiveDamage == 1,
          "remaining damage change triggers fresh advice");
    File.WriteAllText(path, torchJson.Replace("\"live_damage\":2", "\"live_damage\":null"));
    var unknownTorch = GameSnapshot.Read(path, now);
    Check(CardKnowledge.Collect(unknownTorch).Single(x => x.CardId == "CATA_585").LiveDamage == null,
          "absent live tag must not be replaced with the printed base value");

    // Match history: visible cards, duplicate copies, persistence and boundaries.
    var historyFile = Path.Combine(Path.GetTempPath(),
        "hscoach-history-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var history = new MatchHistoryStore(historyFile);
        var recorded = new
        {
            schema_version = 1, captured_at_utc = now.ToString("O"),
            in_game = true, turn = 1, my_turn = true, phase = "turn",
            player = new
            {
                card_class = "MAGE", hand = Array.Empty<object>(),
                played_this_turn = new[] { "Fireball", "Fireball" },
                public_plays_this_turn = new[] {
                    new { entity_id = 7, card_id = "CS2_029", name = "Fireball" },
                    new { entity_id = 8, card_id = "CS2_029", name = "Fireball" }
                }
            },
            opponent = new
            {
                card_class = "PRIEST", played_this_turn = new[] { "Northshire Cleric" },
                public_plays_this_turn = new[] {
                    new { entity_id = 9, card_id = "CS2_235", name = "Northshire Cleric" }
                }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(recorded));
        var observed = GameSnapshot.Read(path, now);
        history.Observe(observed, now);
        history.Observe(observed, now.AddSeconds(1));
        Check(history.Matches.Count == 1 && history.Matches[0].Plays.Count == 3,
            "record unique instances without double counting polls");
        Check(history.Matches[0].Plays[0].CardId == "CS2_029" &&
              history.Matches[0].Plays[1].EntityId == 8 &&
              history.Matches[0].OpponentClass == "PRIEST",
            "persist IDs and public opponent plays");
        Check(new MatchHistoryStore(historyFile).Matches[0].Plays.Count == 3,
            "history survives process restart");

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            schema_version = 1, captured_at_utc = now.ToString("O"), in_game = false
        }));
        var finished = GameSnapshot.Read(path, now);
        history.Observe(finished, now.AddSeconds(2));
        history.Observe(finished, now.AddSeconds(7));
        Check(history.Matches[0].FinishedUtc.HasValue, "finished matches are archived");

        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema_version = 1, captured_at_utc = now.ToString("O"),
            in_game = true, turn = 6, my_turn = false, phase = "turn",
            player = new { card_class = "ROGUE", hand = Array.Empty<object>(),
                           played_this_turn = new[] { "Coin" } },
            opponent = new { card_class = "MAGE", played_this_turn = Array.Empty<string>() }
        }));
        history.Observe(GameSnapshot.Read(path, now), now.AddSeconds(8));
        Check(history.Matches.Count == 2 && history.Matches[0].Partial,
            "late join becomes a separate partially observed match");
        Check(history.Matches[0].Plays.Count == 1 &&
              history.Matches[0].Plays[0].Name == "Coin" &&
              history.Matches[0].Plays[0].CardId == null,
            "old HDT plugin still records card names without fabricated IDs");
    }
    finally { try { File.Delete(historyFile); } catch { } }

    // A deck is visible in the HDT MENU before the first mulligan.
    var deckA = new string('A', 64);
    var deckB = new string('B', 64);
    var deckFixture = new {
        source = "hdt_active_deck", status = "hdt_selected_not_game_verified",
        name = "Kombinacje ognia", card_class = "MAGE", signature = deckA,
        cards = new [] {
            new { card_id = "CS2_029", name = "Ognista Kula",
                count = 2, cost = 4, card_type = "SPELL", card_text = "Zadaj 6 obrażeń." }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = false, active_deck = deckFixture
    }));
    var menuDeck = GameSnapshot.Read(path, now);
    bool hasMenuDeck = DeckStrategy.TryGetUsableDeck(menuDeck, out var selectedSignature, out var selectedDeck);
    Check(menuDeck.Connected && !menuDeck.InGame && hasMenuDeck &&
        selectedSignature == deckA && selectedDeck.Contains("Ognista Kula"),
        "active HDT deck can be planned in the menu before a match");
    var strategy = new DeckStrategy();
    Check(strategy.Store(menuDeck, "ARCHETYP: Tempo\\nKOMBINACJE: Test".Replace("\\n", "\n")) &&
        strategy.IsCurrent(menuDeck) && strategy.ForPrompt(menuDeck) is not null,
        "pre-match strategy is cached against exact deck signature");
    Check(DeckStrategyPrompt.Build(selectedDeck).Contains("KOMBINACJE"),
        "deck planning prompt requests actionable card synergies");
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = false, active_deck = new {
            deckFixture.source, deckFixture.status, deckFixture.cards,
            deckFixture.name, deckFixture.card_class, signature = deckB
        }
    }));
    var differentDeck = GameSnapshot.Read(path, now);
    Check(!strategy.IsCurrent(differentDeck) && strategy.ForPrompt(differentDeck) is null,
        "changing the HDT deck invalidates previously prepared strategy");
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = false, active_deck = new {
            deckFixture.source, status = "class_mismatch", deckFixture.cards,
            deckFixture.name, deckFixture.card_class, signature = deckA
        }
    }));
    Check(!DeckStrategy.TryGetUsableDeck(GameSnapshot.Read(path, now), out _, out _),
        "unverified mismatched class must never receive a deck strategy");
    File.WriteAllText(path, JsonSerializer.Serialize(new {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, my_turn = false, turn = 1,
        player = new { active_deck = new {
            deckFixture.source, status = "class_matches_not_game_verified",
            deckFixture.cards, deckFixture.name, deckFixture.card_class, signature = deckA
        }, hand = Array.Empty<object>(), board = Array.Empty<object>() },
        opponent = new { board = Array.Empty<object>() }
    }));
    var matchingGame = GameSnapshot.Read(path, now);
    Check(strategy.IsCurrent(matchingGame) &&
        new MatchIntelligence().BuildContext(matchingGame, strategy.ForPrompt(matchingGame))
            .Contains("ARCHETYP: Tempo"),
        "only matching pre-match deck strategies enter live AI evidence");

    // The full currently active HDT deck is available to the player-turn AI,
    // without confusing the original list with cards still in the draw pile.
    var withDeck = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 12, my_turn = false, phase = "turn",
        player = new {
            card_class = "MAGE",
            active_deck = new {
                source = "hdt_active_deck", status = "class_matches_not_game_verified",
                name = "Testowa talia", signature = "DECK_A",
                cards = new[] { new { card_id = "CS2_029", name = "Ognista Kula",
                    count = 2, cost = 4, card_type = "SPELL" } }
            },
            hand = Array.Empty<object>(), board = Array.Empty<object>(),
            played_this_turn = Array.Empty<string>()
        },
        opponent = new {
            card_class = "WARRIOR", board = Array.Empty<object>(),
            played_this_turn = new[] { "Odkryta karta" }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(withDeck));
    var enemyTurn = GameSnapshot.Read(path, now);
    using (var deckData = JsonDocument.Parse(enemyTurn.TacticalJson))
    {
        var activeDeck = deckData.RootElement.GetProperty("player").GetProperty("active_deck");
        Check(activeDeck.GetProperty("cards")[0].GetProperty("card_id").GetString() == "CS2_029",
            "actual HDT active deck entries reach the AI snapshot");
    }
    var intelligence = new MatchIntelligence();
    intelligence.Observe(enemyTurn, now);
    Check(intelligence.RecordOpponentReview(enemyTurn,
            "WIDOCZNE: zagrano kartę. RYZYKO: plansza. NASTĘPNA TURA: obrona."),
        "opponent review saved during enemy turn");
    Check(AdvicePrompt.Build(enemyTurn, intelligence.BuildContext(enemyTurn))
        .Contains("class_matches_not_game_verified", StringComparison.Ordinal) &&
        OpponentTurnPrompt.Build(enemyTurn, intelligence.BuildContext(enemyTurn))
        .Contains("Odkryta karta", StringComparison.Ordinal),
        "AI prompts receive opponent play and deck evidence");
    var opponentScheduler = new OpponentTurnScheduler();
    Check(!opponentScheduler.ShouldAnalyze(enemyTurn, now, true, false),
        "enemy analysis waits for stable initial evidence");
    Check(!opponentScheduler.ShouldAnalyze(enemyTurn, now.AddSeconds(3), true, false),
        "enemy analysis does not start too early");
    Check(opponentScheduler.ShouldAnalyze(enemyTurn, now.AddSeconds(5), true, false),
        "opponent turn receives a proactive AI review");
    Check(!opponentScheduler.ShouldAnalyze(enemyTurn, now.AddSeconds(10), true, false),
        "same enemy state is not re-reviewed");
    var laterPlay = new {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 12, my_turn = false, phase = "turn", player = withDeck.player,
        opponent = new { card_class = "WARRIOR", board = Array.Empty<object>(),
            played_this_turn = new[] { "Odkryta karta", "Druga karta" } }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(laterPlay));
    var changedEnemy = GameSnapshot.Read(path, now);
    Check(!opponentScheduler.ShouldAnalyze(changedEnemy, now.AddSeconds(11), true, false),
        "new public enemy play must settle before inference");
    Check(opponentScheduler.ShouldAnalyze(changedEnemy, now.AddSeconds(16), true, false),
        "a second enemy review follows meaningful public activity");
    Check(!opponentScheduler.ShouldAnalyze(changedEnemy, now.AddSeconds(27), true, false),
        "maximum two enemy reviews per turn");
    var playerTurnFixture = new {
        schema_version = 1, captured_at_utc = now.ToString("O"), in_game = true,
        turn = 13, my_turn = true, phase = "turn", player = withDeck.player,
        opponent = laterPlay.opponent
    };
    File.WriteAllText(path, JsonSerializer.Serialize(playerTurnFixture));
    var ourNextTurn = GameSnapshot.Read(path, now);
    intelligence.Observe(ourNextTurn, now.AddSeconds(17));
    using (var afterEnemy = JsonDocument.Parse(intelligence.BuildContext(ourNextTurn)))
    {
        Check(afterEnemy.RootElement.GetProperty("opponent_turn_review")
            .GetProperty("summary").GetString()!.Contains("RYZYKO"),
            "opponent review informs the following player turn");
    }
    intelligence.Memory.Reset();
    intelligence.Observe(ourNextTurn, now.AddSeconds(18));
    using (var newMatch = JsonDocument.Parse(intelligence.BuildContext(ourNextTurn)))
    {
        Check(newMatch.RootElement.GetProperty("opponent_turn_review").ValueKind ==
            JsonValueKind.Null, "old opponent review never leaks into a new match");
    }

    // The tactical explorer must provide alternative mana plans without claiming
    // that spending all mana is the best or that an effect is guaranteed.
    var thinkingFixture = new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true, phase = "turn",
        player = new
        {
            mana_available = 5, mana_max = 6,
            hero = new { health = 5, armor = 0 },
            hand = new[] {
                new { name = "Karta A", card_id = "TEST_A", card_type = "SPELL", cost = (int?)2 },
                new { name = "Karta A", card_id = "TEST_A", card_type = "SPELL", cost = (int?)2 },
                new { name = "Karta B", card_id = "TEST_B", card_type = "SPELL", cost = (int?)1 },
                new { name = "Zbyt droga", card_id = "TEST_C", card_type = "SPELL", cost = (int?)8 },
                new { name = "Koszt nieznany", card_id = "TEST_D", card_type = "SPELL", cost = (int?)null }
            },
            board = new[] { new { name = "Obrońca", card_id = "TEST_O", card_type = "MINION",
                attack = 1, health = 2, taunt = false, exhausted = false, frozen = false } }
        },
        opponent = new
        {
            hero = new { health = 18, armor = 0 },
            board = new[] { new { name = "Zagrożenie", card_id = "TEST_E", card_type = "MINION",
                attack = 7, health = 5, taunt = false, exhausted = true, frozen = false } }
        }
    };
    File.WriteAllText(path, JsonSerializer.Serialize(thinkingFixture));
    var thinkingSnapshot = GameSnapshot.Read(path, now);
    var turnTactics = TurnDecisionEngine.Analyze(thinkingSnapshot);
    Check(turnTactics.Focus == "obrona przed potencjalnym lethal" &&
        turnTactics.PossibleEnemyNextTurnDamage == 7, "exhausted enemy remains a next-turn threat");
    Check(turnTactics.CardsWithUnknownCost == 1 &&
        turnTactics.ManaPackages.Any(x => x.ManaSpent == 5 &&
            x.CardCount == 3 && x.Cards.Count(c => c.StartsWith("Karta A")) == 2) &&
        turnTactics.ManaPackages.All(x => x.ManaSpent <= 5 &&
            x.Cards.All(card => !card.Contains("Zbyt droga") &&
                !card.Contains("Koszt nieznany"))),
        "bounded mana explorer preserves duplicate physical cards, budget, unknown costs");
    Check(turnTactics.ManaPackages.Any(x => x.CardCount == 1) &&
        turnTactics.ManaPackages.Any(x => x.CardCount >= 2),
        "planner keeps competing single-card and combo branches instead of one greedy choice");
    using (var planningJson = JsonDocument.Parse(new MatchIntelligence().BuildContext(thinkingSnapshot)))
    {
        var explorer = planningJson.RootElement.GetProperty("tactical_decision_explorer");
        Check(explorer.GetProperty("focus").GetString() == turnTactics.Focus &&
            explorer.GetProperty("possible_card_cost_packages")[0]
                .GetProperty("certainty").GetString() == "cost_only_hypothetical_NOT_legal_sequence",
            "tactical context marks cost-only packages as hypothetical");
    }
    Check(AdvicePrompt.Build(thinkingSnapshot).Contains("DWIE RÓŻNE sensowne linie") &&
        AdvicePrompt.Build(thinkingSnapshot).Contains("possible_card_cost_packages"),
        "AI explicitly compares different plans while respecting uncertainty");
    Check(DecisionReasoningBudget.Choose(thinkingSnapshot).Effort == "medium" &&
        DecisionReasoningBudget.Choose(thinkingSnapshot, opponentReview: true).Effort == "low" &&
        DecisionReasoningBudget.Choose(thinkingSnapshot, deckAnalysis: true).Effort == "medium",
        "model deliberation adapts to turn complexity, opponent and deck phases");

    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schema_version = 1, captured_at_utc = now.ToString("O"),
        in_game = true, turn = 6, my_turn = true,
        choice_tracking_supported = true, choice_active = true,
        choices = new[] { new { index = 1, card = new { name = "Opcja A" } } },
        player = thinkingFixture.player, opponent = thinkingFixture.opponent
    }));
    var urgentStrategicChoice = GameSnapshot.Read(path, now);
    Check(DecisionReasoningBudget.Choose(urgentStrategicChoice).Effort == "low" &&
        TurnDecisionEngine.Analyze(urgentStrategicChoice).ManaPackages.Count == 0,
        "Discover stays immediate; turn mana explorer does not override choice");

    Console.WriteLine("Hearthstone Coach core checks: PASS");
}
finally { try { File.Delete(path); } catch { } }

static void Check(bool condition, string what)
{
    if (!condition) throw new Exception("Check failed: " + what);
}
