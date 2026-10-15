namespace Coach.Core;

/// <summary>Per-phase debounced automatic analysis. Never analyzes an inactive or stale snapshot.</summary>
public sealed class AnalysisScheduler
{
    private string? _seenKey, _analyzedKey;
    private string _seenPhase = "";
    private DateTimeOffset _stableSince = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _turn = -1, _turnRequests;
    public string LastDecision { get; private set; } = "init";
    public static readonly TimeSpan TurnDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ChoiceDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MulliganDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TurnGap = TimeSpan.FromSeconds(9);
    public const int MaxTurnRequests = 8;

    public bool ShouldAnalyze(GameSnapshot snapshot, DateTimeOffset now, bool analyzing, bool enabled)
    {
        if (!snapshot.Actionable)
        {
            // Important: reset when opponent's turn or HDT temporarily disconnects.
            // Otherwise previously analyzed state might prevent the next turn.
            _seenKey = null;
            _seenPhase = "";
            LastDecision = "inactive";
            return false;
        }
        if (_turn != snapshot.Turn)
        {
            _turn = snapshot.Turn;
            _turnRequests = 0;
            _analyzedKey = null;
        }
        if (_seenKey != snapshot.DecisionKey || _seenPhase != snapshot.Phase)
        {
            _seenKey = snapshot.DecisionKey;
            _seenPhase = snapshot.Phase;
            _stableSince = now;
            LastDecision = "settling_new_" + snapshot.Phase;
            return false;
        }
        if (!enabled) { LastDecision = "disabled"; return false; }
        if (analyzing) { LastDecision = "busy"; return false; }
        var delay = snapshot.MulliganActive ? MulliganDelay :
            snapshot.ChoiceActive ? ChoiceDelay : TurnDelay;
        if (now - _stableSince < delay) { LastDecision = "settling"; return false; }
        if (_analyzedKey == snapshot.DecisionKey) { LastDecision = "already_analyzed"; return false; }
        if (!snapshot.MulliganActive && !snapshot.ChoiceActive && _turnRequests >= MaxTurnRequests)
        { LastDecision = "turn_budget"; return false; }
        // Critical choice prompts are time-sensitive and may appear right after
        // a regular turn analysis. Don't block them with the full cooldown.
        var gap = snapshot.ChoiceActive ? TimeSpan.FromSeconds(1) :
            snapshot.MulliganActive ? TimeSpan.FromSeconds(2) : TurnGap;
        if (now - _lastRequest < gap) { LastDecision = "cooldown"; return false; }
        MarkAnalyzed(snapshot, now);
        LastDecision = "triggered_" + snapshot.Phase;
        return true;
    }

    public void MarkAnalyzed(GameSnapshot snapshot, DateTimeOffset now)
    {
        _seenKey = snapshot.DecisionKey;
        _seenPhase = snapshot.Phase;
        _analyzedKey = snapshot.DecisionKey;
        _lastRequest = now;
        _turnRequests++;
    }

    public void RecordFailure(GameSnapshot snapshot)
    {
        // Retrying same state is allowed after cooldown if AI failed, but remains bounded.
        if (_analyzedKey == snapshot.DecisionKey)
            _analyzedKey = null;
    }
}
