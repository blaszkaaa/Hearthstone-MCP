using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Adaptive latency/quality tradeoff. Medium reasoning is only used on normal
/// turns with enough observable options to make tactical branching worthwhile.
/// Never delay Discover or the mulligan for deeper reasoning.
/// </summary>
public static class DecisionReasoningBudget
{
    public static (string Effort, int OutputTokens) Choose(
        GameSnapshot snapshot, bool opponentReview = false, bool deckAnalysis = false)
    {
        if (deckAnalysis) return ("medium", 1700);
        if (opponentReview) return ("low", 650);
        if (snapshot.MulliganActive) return ("low", 800);
        if (snapshot.ChoiceActive) return ("low", 650);
        if (!snapshot.InGame) return ("low", 650);

        using var doc = JsonDocument.Parse(snapshot.TacticalJson);
        var root = doc.RootElement;
        var player = Property(root, "player");
        var opponent = Property(root, "opponent");
        var options = Count(Property(player, "hand")) +
                      Count(Property(player, "board")) +
                      Count(Property(opponent, "board"));
        // A single clear action benefits from speed. Multiple hand/board
        // candidates merit more attention to order, tempo and trading.
        return options >= 4 ? ("medium", 2100) : ("low", 1600);
    }

    private static JsonElement Property(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(name, out var value) ? value : default;
    private static int Count(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
}
