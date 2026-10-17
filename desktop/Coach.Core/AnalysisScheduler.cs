namespace Coach.Core;

/// <summary>Per-phase debounced automatic analysis. Never analyzes an inactive or stale snapshot.</summary>
public sealed class AnalysisScheduler
{
    private string? _seenKey, _analyzedKey;
    private string _seenPhase = "";
    private DateTimeOffset _stableSince = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _turn = -1, _turnRequests;
    private int _completedPlanTurn = -1;
    public int CompletedPlanTurn => _completedPlanTurn;
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    public string LastDecision { get; private set; } = "init";
    public static readonly TimeSpan TurnDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ChoiceDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MulliganDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TurnGap = TimeSpan.FromSeconds(9);
    // Prevent endless paid-plan requests but do not lock up after a few stale replies.
    public const int MaxTurnRequests = 24;
    public const int MaxAttemptsPerDecision = 3;

    public bool ShouldAnalyze(GameSnapshot snapshot, DateTimeOffset now, bool analyzing, bool enabled)
    {
        if (!snapshot.InGame)
        {
            _completedPlanTurn = -1;
            _turn = -1;
            _analyzedKey = null;
            _attempts.Clear();
        }
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
            _completedPlanTurn = -1;
            _turn = snapshot.Turn;
            _turnRequests = 0;
            _analyzedKey = null;
            _attempts.Clear();
        }
        // A completed plan is immutable for the remainder of this turn, even
        // after drawing a card, spending mana or opening a Discover popup.
        if (_completedPlanTurn == snapshot.Turn && !snapshot.MulliganActive)
        { LastDecision = "plan_ready_for_turn"; return false; }
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
        if (_attempts.GetValueOrDefault(snapshot.DecisionKey) >= MaxAttemptsPerDecision)
        { LastDecision = "retry_budget_for_decision"; return false; }
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

    public void MarkTurnPlanned(GameSnapshot snapshot)
    {
        if (!snapshot.InGame || snapshot.MulliganActive || snapshot.ChoiceActive)
            return;
        _completedPlanTurn = snapshot.Turn;
        _analyzedKey = snapshot.DecisionKey;
        LastDecision = "turn_plan_completed";
    }

    public void MarkAnalyzed(GameSnapshot snapshot, DateTimeOffset now)
    {
        _seenKey = snapshot.DecisionKey;
        _seenPhase = snapshot.Phase;
        _analyzedKey = snapshot.DecisionKey;
        _lastRequest = now;
        _turnRequests++;
        _attempts[snapshot.DecisionKey] = _attempts.GetValueOrDefault(snapshot.DecisionKey) + 1;
    }

    public void RecordFailure(GameSnapshot snapshot)
    {
        // Re-arm a rejected / failed reply. Subsequent polling triggers a retry
        // of the SAME stable decision after cooldown (up to 3 attempts).
        if (_analyzedKey == snapshot.DecisionKey)
            _analyzedKey = null;
    }
}
