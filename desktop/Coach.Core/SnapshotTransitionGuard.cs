namespace Coach.Core;

/// <summary>
/// Compares the game against the input for ONE active AI request or ONE displayed
/// recommendation. Never compare an active request with a previous displayed move:
/// that made every new turn cancel itself after four seconds.
/// </summary>
public sealed class SnapshotTransitionGuard
{
    private string? _candidate;
    private DateTimeOffset _since;

    public void Reset()
    {
        _candidate = null;
        _since = DateTimeOffset.MinValue;
    }

    public static bool MatchesActionableDecision(GameSnapshot reference, GameSnapshot current) =>
        reference.InGame && current.InGame && current.Actionable &&
        current.Turn == reference.Turn &&
        current.Phase == reference.Phase &&
        current.DecisionKey == reference.DecisionKey;

    public bool HasChanged(GameSnapshot reference, GameSnapshot current, DateTimeOffset now)
    {
        if (current.InGame && reference.InGame &&
            (current.Turn != reference.Turn || current.Phase != reference.Phase))
        {
            Reset();
            return true;
        }

        if (current.Actionable && current.DecisionKey == reference.DecisionKey &&
            current.Phase == reference.Phase)
        {
            Reset();
            return false;
        }

        // One noisy HDT snapshot or brief flicker to menu should not invalidate
        // work already underway. Require 2s of the SAME new observed state.
        string observed = current.InGame
            ? current.Phase + ":" + current.Turn + ":" + current.DecisionKey
            : "NO_GAME";
        if (_candidate != observed)
        {
            _candidate = observed;
            _since = now;
            return false;
        }
        return now - _since >= TimeSpan.FromSeconds(2);
    }
}
