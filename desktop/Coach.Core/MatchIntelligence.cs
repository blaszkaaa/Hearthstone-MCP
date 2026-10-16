using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Match-scoped evidence: own selected HDT deck, public opponent plays, and
/// one short (fallible) AI opponent-turn review. Never remembers across games.
/// </summary>
public sealed class MatchIntelligence
{
    private readonly MatchMemory _memory = new();
    private int _sessionId;
    private OpponentReview? _opponentReview;
    private sealed record OpponentReview(int SessionId, int Turn, string Note);

    public MatchMemory Memory => _memory;

    public void Observe(GameSnapshot snapshot, DateTimeOffset now)
    {
        _memory.Observe(snapshot, now);
        if (_sessionId != _memory.SessionId)
        {
            _sessionId = _memory.SessionId;
            _opponentReview = null;
        }
    }

    public bool RecordOpponentReview(GameSnapshot observed, string review)
    {
        if (!observed.InGame || observed.MyTurn || observed.MulliganActive ||
            observed.ChoiceActive || string.IsNullOrWhiteSpace(review) ||
            _memory.SessionId != _sessionId)
            return false;

        var note = review.Trim();
        if (note.Length > 700) note = note[..700];
        // Never accept an earlier in-flight request over a newer opponent review.
        if (_opponentReview is not null && _opponentReview.SessionId == _sessionId &&
            _opponentReview.Turn > observed.Turn)
            return false;
        _opponentReview = new OpponentReview(_sessionId, observed.Turn, note);
        return true;
    }

    public string BuildContext(GameSnapshot snapshot, object? deckStrategy = null) =>
        JsonSerializer.Serialize(new
        {
            card_knowledge = CardKnowledge.ForPrompt(snapshot),
            deck_strategy = deckStrategy,
            public_match_memory = _memory.ForPrompt(),
            // A review is a hypothesis, NOT an HDT fact; always re-check
            // current board / heroes / legal actions before acting on it.
            opponent_turn_review = _opponentReview is not null &&
                _opponentReview.SessionId == _sessionId &&
                snapshot.InGame && snapshot.Turn >= _opponentReview.Turn &&
                snapshot.Turn <= _opponentReview.Turn + 3
                    ? new
                    {
                        observed_on_turn = (int?)_opponentReview.Turn,
                        summary = _opponentReview.Note,
                        status = "AI_hypothesis_verify_against_current_state"
                    }
                    : null,
            visible_board_threats = ThreatAssessment.ForPrompt(snapshot),
            conditional_lethal_forecast = DamageForecast.ForPrompt(snapshot),
            candidate_actions = LegalActionCatalog.ForPrompt(snapshot),
            tactical_decision_explorer = TurnDecisionEngine.ForPrompt(snapshot)
        });
}
