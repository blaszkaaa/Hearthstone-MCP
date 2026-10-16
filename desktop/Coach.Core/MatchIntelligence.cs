using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Owns match-scoped context. A single instance must live across desktop polling
/// ticks; creating one for every AI request would forget previous turns.
/// </summary>
public sealed class MatchIntelligence
{
    private readonly MatchMemory _memory = new();
    public MatchMemory Memory => _memory;

    public void Observe(GameSnapshot snapshot, DateTimeOffset now) =>
        _memory.Observe(snapshot, now);

    public string BuildContext(GameSnapshot snapshot) =>
        JsonSerializer.Serialize(new
        {
            card_knowledge = CardKnowledge.ForPrompt(snapshot),
            public_match_memory = _memory.ForPrompt(),
            visible_board_threats = ThreatAssessment.ForPrompt(snapshot),
            candidate_actions = LegalActionCatalog.ForPrompt(snapshot)
        });
}
