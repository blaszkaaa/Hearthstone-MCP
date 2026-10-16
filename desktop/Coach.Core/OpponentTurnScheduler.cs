namespace Coach.Core;

/// <summary>
/// Reviews visible opponent activity while the player cannot act. Debounced,
/// at most twice per opponent turn, and only after a material state change.
/// </summary>
public sealed class OpponentTurnScheduler
{
    private int _turn = -1;
    private int _requests;
    private string? _seenFingerprint;
    private string? _analyzedFingerprint;
    private DateTimeOffset _stableSince;
    private DateTimeOffset _lastRequest;
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan RequestGap = TimeSpan.FromSeconds(9);

    public bool ShouldAnalyze(GameSnapshot snapshot, DateTimeOffset now, bool enabled, bool busy)
    {
        if (!snapshot.InGame)
        {
            Reset();
            return false;
        }
        if (snapshot.MyTurn || snapshot.MulliganActive || snapshot.ChoiceActive)
            return false;
        if (_turn != snapshot.Turn)
        {
            _turn = snapshot.Turn;
            _requests = 0;
            _seenFingerprint = null;
            _analyzedFingerprint = null;
        }
        if (_seenFingerprint != snapshot.Fingerprint)
        {
            _seenFingerprint = snapshot.Fingerprint;
            _stableSince = now;
            return false;
        }
        if (!enabled || busy || _requests >= 2 ||
            _analyzedFingerprint == snapshot.Fingerprint ||
            now - _stableSince < SettleDelay ||
            now - _lastRequest < RequestGap)
            return false;

        _requests++;
        _lastRequest = now;
        _analyzedFingerprint = snapshot.Fingerprint;
        return true;
    }

    public void Reset()
    {
        _turn = -1;
        _requests = 0;
        _seenFingerprint = null;
        _analyzedFingerprint = null;
        _lastRequest = default;
    }
}