using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Conservative, cheap tactical pre-analysis. Enumerates mana-cost bundles
/// as hypothetical starting points, NOT legal action sequences. It does not
/// simulate Battlecries, effects, restrictions or enemy hidden information.
/// </summary>
public static class TurnDecisionEngine
{
    public sealed record ResourceBundle(int ManaSpent, int ManaRemaining,
        IReadOnlyList<string> Cards, int CardCount);
    public sealed record Summary(
        string Focus,
        string FocusReason,
        int? AvailableMana,
        int? OwnEffectiveHealth,
        int? EnemyEffectiveHealth,
        int VisibleReadyBoardDamage,
        int PossibleEnemyNextTurnDamage,
        bool EnemyTaunt,
        bool OwnTaunt,
        IReadOnlyList<ResourceBundle> ManaPackages,
        int CardsWithUnknownCost,
        IReadOnlyList<string> Checks,
        string Limitations);

    public static Summary Analyze(GameSnapshot snapshot)
    {
        const string limits = "Pakiety many są WYŁĄCZNIE hipotetycznymi zbiorami kart po obecnym koszcie: "
            + "nie potwierdzają legalności, kolejności, możliwości celowania, pola na planszy, "
            + "warunków combo ani efektywnego wyniku. Mogą zawierać karty, których razem "
            + "nie należy zagrywać. Efekty zmieniające koszty/manę i nowe karty wymagają "
            + "ponownej analizy po zagrywce. Rywal ma nieznaną rękę/sekrety.";
        if (!snapshot.InGame || snapshot.MulliganActive || snapshot.ChoiceActive)
            return new("brak planowania sekwencji", "Oczekiwanie na zwykłą turę.",
                null, null, null, 0, 0, false, false, [], 0, [], limits);

        var forecast = DamageForecast.Calculate(snapshot);
        var candidates = LegalActionCatalog.Build(snapshot);
        var possibleEnemyLethal = forecast.PotentialEnemyBoardLethal == true;
        var possibleOwnLethal = forecast.PotentialFriendlyBoardLethal == true;
        string focus, reason;
        if (possibleOwnLethal)
        {
            focus = "zweryfikuj lethal";
            reason = "Widoczny atak planszy osiąga próg HP rywala, ale wynik zależy od legalności ataków i efektów.";
        }
        else if (possibleEnemyLethal)
        {
            focus = "obrona przed potencjalnym lethal";
            reason = "Wrogi atak z planszy w kolejnej turze może osiągnąć nasze HP; nie ignoruj nawet wyczerpanych teraz wrogich jednostek.";
        }
        else if (forecast.OwnEffectiveHealth is <= 10 &&
            forecast.EnemyNextTurnBoardDamage > 0)
        {
            focus = "bezpieczne tempo";
            reason = "Mało zdrowia; porównaj wymiany i oszczędzanie punktów życia.";
        }
        else
        {
            focus = "tempo i synergie";
            reason = "Brak potwierdzonej pilnej sytuacji na podstawie widocznej planszy; nadal uwzględnij ukryte możliwości przeciwnika.";
        }

        var checks = new List<string>
        {
            "Czy istnieje lethal? Zweryfikuj Prowokacje, osłony, gotowość ataku i pozostałą manę.",
            "Czy rywal może zagrozić lethal w następnej turze? Przygotuj konkretną obronę, nie zakładaj nieznanych kart.",
            "Porównaj co najmniej dwie REALNE kolejności działań i wartość zachowania kluczowych kart lub efektów combo.",
            "Zanim zagrasz usuwanie / dobieranie / Odkryj, oceń konsekwencje dla reszty tury.",
            "Nie marnuj many za wszelką cenę; nie zagrywaj zasobów potrzebnych do lepszej synergii.",
            "Dla każdej akcji sprawdź typ, aktualny koszt, możliwość ataku, cel i zmiany planszy; po nieprzewidywalnym efekcie zatrzymaj plan."
        };

        var bundles = BuildBundles(candidates.Hand, candidates.AvailableMana, out int unknown);
        return new(focus, reason, candidates.AvailableMana,
            forecast.OwnEffectiveHealth, forecast.EnemyEffectiveHealth,
            forecast.ReadyFriendlyBoardDamage, forecast.EnemyNextTurnBoardDamage,
            forecast.EnemyTauntPresent, forecast.OwnTauntPresent,
            bundles, unknown, checks, limits);
    }

    public static object ForPrompt(GameSnapshot snapshot)
    {
        var x = Analyze(snapshot);
        return new
        {
            focus = x.Focus,
            reason = x.FocusReason,
            visible_board = new
            {
                own_health_plus_armor = x.OwnEffectiveHealth,
                enemy_health_plus_armor = x.EnemyEffectiveHealth,
                known_friendly_ready_attack = x.VisibleReadyBoardDamage,
                enemy_board_next_turn_possible_attack = x.PossibleEnemyNextTurnDamage,
                enemy_taunt = x.EnemyTaunt,
                own_taunt = x.OwnTaunt
            },
            available_mana = x.AvailableMana,
            possible_card_cost_packages = x.ManaPackages.Select(y => new
            {
                mana_spent = y.ManaSpent,
                mana_left = y.ManaRemaining,
                cards = y.Cards,
                card_count = y.CardCount,
                certainty = "cost_only_hypothetical_NOT_legal_sequence"
            }).ToArray(),
            cards_with_unknown_cost = x.CardsWithUnknownCost,
            decision_checklist = x.Checks,
            limits = x.Limitations
        };
    }

    private static IReadOnlyList<ResourceBundle> BuildBundles(
        IReadOnlyList<LegalActionCatalog.HandOption> hand, int? mana, out int unknown)
    {
        unknown = hand.Count(x => x.Cost is null);
        if (mana is null || mana < 0) return [];
        // Keep search bounded even for a glitched or modified HDT hand.
        var playable = hand.Select((card, index) => new { card, index })
            .Where(x => x.card.Cost is >= 0 && x.card.Cost <= mana)
            .Take(12).ToArray();
        if (playable.Length == 0) return [];

        var found = new List<(int Used, List<string> Cards, int Key)>();
        int subsetCount = 1 << playable.Length; // <= 4096
        for (int mask = 1; mask < subsetCount; mask++)
        {
            int cost = 0;
            var names = new List<string>();
            for (int k = 0; k < playable.Length; k++)
            {
                if ((mask & (1 << k)) == 0) continue;
                cost += playable[k].card.Cost!.Value;
                if (cost > mana) break;
                // Preserve duplicates (different physical cards in hand).
                names.Add(playable[k].card.Name + " [pozycja " +
                    (playable[k].index + 1) + "]");
            }
            if (cost <= mana && names.Count == System.Numerics.BitOperations.PopCount((uint)mask))
                found.Add((cost, names, mask));
        }
        // Suggest resource bundles as exploration anchors, NOT "best plays".
        // Include both 1-card and multi-card branches, not only full mana spending.
        var selected = new List<(int Used, List<string> Cards, int Key)>();
        foreach (var count in new[] { 1, 2, 3, 4 })
        {
            var option = found.Where(x => x.Cards.Count == count)
                .OrderByDescending(x => x.Used)
                .ThenBy(x => x.Key)
                .FirstOrDefault();
            if (option.Cards is not null) selected.Add(option);
        }
        var flexible = found.OrderByDescending(x => x.Used)
            .ThenByDescending(x => x.Cards.Count).ThenBy(x => x.Key)
            .FirstOrDefault();
        if (flexible.Cards is not null) selected.Add(flexible);
        return selected.GroupBy(x => x.Key).Select(x => x.First())
            .Take(5)
            .Select(x => new ResourceBundle(x.Used, mana.Value - x.Used, x.Cards, x.Cards.Count))
            .ToArray();
    }
}
