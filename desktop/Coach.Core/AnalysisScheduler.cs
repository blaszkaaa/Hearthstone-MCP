namespace Coach.Core;

/// <summary>
/// Analyze stable decisions, not every HDT snapshot. Avoid prompt spam at the start of a
/// turn while animations, draw and start-of-turn effects are still resolving.
/// </summary>
public sealed class AnalysisScheduler
{
    private string? _observed, _lastAnalyzed;
    private DateTimeOffset _changedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _turn = -1, _requestsThisTurn;
    private bool _wasActionable;

    public static readonly TimeSpan TurnSettling = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan ChoiceSettling = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinimumRequestGap = TimeSpan.FromSeconds(20);
    public const int MaxAutomaticRequestsPerTurn = 3;

    public bool ShouldAnalyze(GameSnapshot snapshot, DateTimeOffset now, bool analyzing, bool enabled)
    {
        bool actionable = snapshot.InGame && (snapshot.MyTurn || snapshot.ChoiceActive);
        if (!actionable)
        {
            _observed = null;
            _wasActionable = false;
            return false;
        }

        if (_turn != snapshot.Turn)
        {
            _turn = snapshot.Turn;
            _requestsThisTurn = 0;
            _lastAnalyzed = null;
            // Preserve rate limits across turns to avoid repeated requests at turn boundaries.
        }

        if (_observed != snapshot.DecisionKey || !_wasActionable)
        {
            _observed = snapshot.DecisionKey;
            _changedAt = now;
        }
        _wasActionable = true;

        if (!enabled || analyzing || _requestsThisTurn >= MaxAutomaticRequestsPerTurn)
            return false;
        if (now - _changedAt < (snapshot.ChoiceActive ? ChoiceSettling : TurnSettling))
            return false;
        if (now - _lastRequest < MinimumRequestGap)
            return false;
        if (_lastAnalyzed == snapshot.DecisionKey)
            return false;

        MarkAnalyzed(snapshot, now);
        return true;
    }

    public void MarkAnalyzed(GameSnapshot snapshot, DateTimeOffset now)
    {
        _lastAnalyzed = snapshot.DecisionKey;
        _lastRequest = now;
        _requestsThisTurn++;
    }
}
