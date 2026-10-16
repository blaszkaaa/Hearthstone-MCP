using System.Text.Json;

namespace Coach.Core;

/// <summary>
/// Short, evidence-only memory of public plays observed while THIS desktop app
/// was open. No opponent hand identities, no guesses, no persistence across games.
/// </summary>
public sealed class MatchMemory
{
    public sealed record ObservedPlay(int Turn, string Side, string Card);
    private readonly List<ObservedPlay> _plays = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _firstObservedTurn = -1;
    private int _lastObservedTurn = -1;
    private DateTimeOffset? _noGameSince;
    private bool _observedMulligan;

    public int FirstObservedTurn => _firstObservedTurn;
    public int TotalObservedPlays => _plays.Count;
    public IReadOnlyList<ObservedPlay> RecentPlays => _plays.TakeLast(32).ToArray();

    public void Reset()
    {
        _plays.Clear();
        _seen.Clear();
        _firstObservedTurn = -1;
        _lastObservedTurn = -1;
        _noGameSince = null;
        _observedMulligan = false;
    }

    public void Observe(GameSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.InGame)
        {
            _noGameSince ??= now;
            if (now - _noGameSince >= TimeSpan.FromSeconds(12)) Reset();
            return;
        }

        if (_noGameSince is not null && now - _noGameSince >= TimeSpan.FromSeconds(12))
            Reset();
        _noGameSince = null;

        if ((_lastObservedTurn >= 0 && snapshot.Turn < _lastObservedTurn) ||
            (snapshot.MulliganActive && _lastObservedTurn > 0))
            Reset();

        if (_firstObservedTurn < 0)
        {
            _firstObservedTurn = snapshot.Turn;
            _observedMulligan = snapshot.MulliganActive;
        }
        if (snapshot.MulliganActive) _observedMulligan = true;
        _lastObservedTurn = Math.Max(_lastObservedTurn, snapshot.Turn);

        using var doc = JsonDocument.Parse(snapshot.TacticalJson);
        var root = doc.RootElement;
        ObserveSide(root, "player", snapshot.Turn);
        ObserveSide(root, "opponent", snapshot.Turn);
    }

    private void ObserveSide(JsonElement root, string side, int turn)
    {
        if (!root.TryGetProperty(side, out var value) ||
            !value.TryGetProperty("played_this_turn", out var played) ||
            played.ValueKind != JsonValueKind.Array)
            return;

        // HDT's public list can hold identical names twice. Keep occurrences,
        // but do not count repeated polling as additional played cards.
        int index = 0;
        foreach (var item in played.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = item.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var key = turn + "|" + side + "|" + index + "|" + name;
            if (!_seen.Add(key)) continue;
            _plays.Add(new ObservedPlay(turn, side, name.Length <= 110 ? name : name[..110]));
        }
        // Never let a long game or a buggy tracker grow process memory endlessly.
        if (_plays.Count > 120)
        {
            _plays.RemoveRange(0, _plays.Count - 120);
            _seen.Clear();
            foreach (var group in _plays.GroupBy(x => (x.Turn, x.Side)))
            {
                var occurrence = 0;
                foreach (var play in group)
                    _seen.Add(play.Turn + "|" + play.Side + "|" + (++occurrence) + "|" + play.Card);
            }
        }
    }

    public object ForPrompt() => new
    {
        first_observed_turn = _firstObservedTurn < 0 ? (int?)null : _firstObservedTurn,
        observed_opening = _observedMulligan,
        coverage = _firstObservedTurn > 1 && !_observedMulligan ? "partial_from_app_start" : "observed_only",
        recent_public_plays = RecentPlays.Select(x => new
        {
            turn = x.Turn, side = x.Side, card = x.Card
        }).ToArray(),
        note = "Historia to wyłącznie publiczne zagrania ZAOBSERWOWANE podczas pracy aplikacji; nie jest pełną talią, historią wszystkich tur ani wiedzą o ręce przeciwnika."
    };
}
