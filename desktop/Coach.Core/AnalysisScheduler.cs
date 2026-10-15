namespace Coach.Core;

/// <summary>
/// Debounced, rate-limited analysis trigger. No network calls while disconnected.
/// </summary>
public sealed class AnalysisScheduler
{
    private string? _observed, _lastAnalyzed;
    private DateTimeOffset _changedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _turn = -1, _requestsThisTurn = 0;
    private bool _wasActionable;

    public bool ShouldAnalyze(GameSnapshot snapshot, DateTimeOffset now, bool analyzing, bool enabled)
    {
        bool actionable = snapshot.InGame && (snapshot.MyTurn || snapshot.ChoiceActive);
        if (!actionable)
        {
            _observed = null; _wasActionable = false;
            return false;
        }
        if (_turn != snapshot.Turn)
        {
            _turn = snapshot.Turn; _requestsThisTurn = 0;
            _lastRequest = DateTimeOffset.MinValue; _lastAnalyzed = null;
        }
        if (_observed != snapshot.Fingerprint || !_wasActionable)
        {
            _observed = snapshot.Fingerprint;
            _changedAt = now;
        }
        _wasActionable = true;
        if (!enabled || analyzing || _requestsThisTurn >= 6) return false;
        if (now - _changedAt < TimeSpan.FromSeconds(2)) return false;
        if (now - _lastRequest < TimeSpan.FromSeconds(12)) return false;
        if (_lastAnalyzed == snapshot.Fingerprint) return false;
        MarkAnalyzed(snapshot, now);
        return true;
    }

    public void MarkAnalyzed(GameSnapshot snapshot, DateTimeOffset now)
    {
        _lastAnalyzed = snapshot.Fingerprint;
        _lastRequest = now;
        _requestsThisTurn++;
    }
}
