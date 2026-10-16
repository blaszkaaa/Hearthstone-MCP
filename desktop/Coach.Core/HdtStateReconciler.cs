namespace Coach.Core;

/// <summary>
/// Reconciles HDT's asynchronous snapshots before the scheduler and the UI read
/// them. Transient menu frames / disappearing Discover offers / partial animation
/// states must not silently discard valid advice or reset the analysis scheduler.
/// </summary>
public sealed class HdtStateReconciler
{
    private GameSnapshot? _accepted;
    private string? _candidate;
    private DateTimeOffset _candidateSince;
    private DateTimeOffset _acceptedAt;
    public string LastEvent { get; private set; } = "not_started";
    public GameSnapshot? Current => _accepted;

    public GameSnapshot Observe(GameSnapshot incoming, DateTimeOffset now)
    {
        if (_accepted is null)
            return Accept(incoming, now, "initial");

        // Same actionable decision may still contain newer printed card texts
        // or tracker metadata, which is safe to use in subsequent prompts.
        if (Equivalent(incoming, _accepted))
        {
            _candidate = null;
            _accepted = incoming;
            _acceptedAt = now;
            LastEvent = "same_decision";
            return _accepted;
        }

        bool urgent = incoming.InGame && incoming.Connected &&
            (incoming.ChoiceActive && !_accepted.ChoiceActive ||
             incoming.MulliganActive && !_accepted.MulliganActive ||
             _accepted.InGame && incoming.Turn > _accepted.Turn);
        if (urgent)
            return Accept(incoming, now, incoming.ChoiceActive
                ? "choice_entered" : incoming.MulliganActive ? "mulligan_entered" : "next_turn");

        // Keep a real recommendation visible through isolated HDT read errors
        // and animations; do not accidentally mark a new game as the old one.
        string stateKey = incoming.Connected && incoming.InGame
            ? incoming.Turn + ":" + incoming.Phase + ":" + incoming.DecisionKey
            : incoming.Connected ? "menu" : "disconnected";
        if (_candidate != stateKey)
        {
            _candidate = stateKey;
            _candidateSince = now;
            LastEvent = "pending_" + (incoming.Connected ? incoming.Phase : "offline");
            return _accepted;
        }

        // A menu/disconnect requires at least 2 seconds of consistent evidence;
        // leaving Discover or changing cards needs 0.8 seconds. This balances
        // HDT jitter with timely advice while the turn timer is running.
        var wait = !incoming.InGame || !incoming.Connected
            ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(800);
        if (now - _candidateSince < wait)
        {
            LastEvent = "pending_confirmation";
            return _accepted;
        }

        return Accept(incoming, now, !incoming.Connected ? "disconnected_confirmed" :
            !incoming.InGame ? "menu_confirmed" :
            incoming.Phase != _accepted.Phase ? "phase_changed" : "decision_changed");
    }

    private GameSnapshot Accept(GameSnapshot snapshot, DateTimeOffset now, string kind)
    {
        _accepted = snapshot;
        _acceptedAt = now;
        _candidate = null;
        LastEvent = kind;
        return snapshot;
    }

    private static bool Equivalent(GameSnapshot a, GameSnapshot b)
    {
        if (a.Connected != b.Connected || a.InGame != b.InGame)
            return false;
        if (!a.InGame) return true;
        return a.Turn == b.Turn && a.Phase == b.Phase &&
            a.MyTurn == b.MyTurn && a.DecisionKey == b.DecisionKey;
    }
}
