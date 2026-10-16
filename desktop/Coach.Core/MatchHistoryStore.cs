using System.Text.Json;

namespace Coach.Core;

/// <summary>A played card actually reported by HDT (never an inferred or hidden card).</summary>
public sealed class HistoryPlay
{
    public int Turn { get; set; }
    public string Side { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CardId { get; set; }
    public int? EntityId { get; set; }
    public int Order { get; set; }
}

public sealed class HistoryMatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string PlayerClass { get; set; } = "?";
    public string OpponentClass { get; set; } = "?";
    public int FirstObservedTurn { get; set; }
    public int LastObservedTurn { get; set; }
    public bool Partial { get; set; }
    public List<HistoryPlay> Plays { get; set; } = [];
}

/// <summary>
/// Local, bounded match archive. A desktop poll observes public plays only;
/// it does not pretend to reconstruct missed turns or guess the winner.
/// </summary>
public sealed class MatchHistoryStore
{
    private const int MaxMatches = 100;
    private const int MaxPlaysPerMatch = 400;
    private readonly string _path;
    private readonly List<HistoryMatch> _matches = [];
    private HistoryMatch? _active;
    private DateTimeOffset? _outOfGameSince;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HearthstoneCoach", "match-history.json");

    public MatchHistoryStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    public IReadOnlyList<HistoryMatch> Matches => _matches;
    public int Revision { get; private set; }
    public string FilePath => _path;

    public void Observe(GameSnapshot snapshot, DateTimeOffset now)
    {
        // Stale or disconnected HDT is not evidence that a match ended.
        if (!snapshot.Connected) return;
        if (!snapshot.InGame)
        {
            if (_active == null) return;
            _outOfGameSince ??= now;
            if (now - _outOfGameSince >= TimeSpan.FromSeconds(4))
                Finish(now);
            return;
        }

        _outOfGameSince = null;
        if (_active is not null && snapshot.Turn < _active.LastObservedTurn)
            Finish(now);

        bool changed = false;
        if (_active is null)
        {
            _active = new HistoryMatch
            {
                StartedUtc = now,
                LastSeenUtc = now,
                FirstObservedTurn = snapshot.Turn,
                LastObservedTurn = snapshot.Turn,
                Partial = !snapshot.MulliganActive && snapshot.Turn > 1
            };
            _matches.Insert(0, _active);
            if (_matches.Count > MaxMatches)
                _matches.RemoveRange(MaxMatches, _matches.Count - MaxMatches);
            changed = true;
        }

        var match = _active;
        match.LastSeenUtc = now;
        if (snapshot.Turn > match.LastObservedTurn)
        {
            match.LastObservedTurn = snapshot.Turn;
            changed = true;
        }

        try
        {
            using var doc = JsonDocument.Parse(snapshot.PublicHistoryJson);
            var root = doc.RootElement;
            changed |= ObserveSide(root, match, "player", snapshot.Turn);
            changed |= ObserveSide(root, match, "opponent", snapshot.Turn);
        }
        catch (JsonException) { /* Incomplete HDT data: keep already observed events. */ }

        if (changed)
        {
            Revision++;
            Save();
        }
    }

    private static bool ObserveSide(JsonElement root, HistoryMatch match, string side, int turn)
    {
        if (!root.TryGetProperty(side, out var data) || data.ValueKind != JsonValueKind.Object)
            return false;

        bool changed = false;
        var cardClass = Text(data, "card_class");
        if (!string.IsNullOrWhiteSpace(cardClass))
        {
            if (side == "player" && match.PlayerClass != cardClass)
            {
                match.PlayerClass = cardClass;
                changed = true;
            }
            if (side == "opponent" && match.OpponentClass != cardClass)
            {
                match.OpponentClass = cardClass;
                changed = true;
            }
        }

        bool detailed = data.TryGetProperty("public_plays_this_turn", out var plays) &&
                        plays.ValueKind == JsonValueKind.Array;
        if (!detailed &&
            (!data.TryGetProperty("played_this_turn", out plays) ||
             plays.ValueKind != JsonValueKind.Array))
            return changed;

        int order = 0;
        foreach (var item in plays.EnumerateArray())
        {
            order++;
            if (order > 30) break;
            string? name = item.ValueKind == JsonValueKind.String ? item.GetString() :
                item.ValueKind == JsonValueKind.Object ? Text(item, "name") : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            int? entity = item.ValueKind == JsonValueKind.Object ? Number(item, "entity_id") : null;
            string? cardId = item.ValueKind == JsonValueKind.Object ? Text(item, "card_id") : null;
            if (!VisualTurnPlan.SafeCardId(cardId)) cardId = null;

            // Repeated polls must not duplicate events. Different entity IDs
            // represent different copies of a card played in the same turn.
            bool alreadySeen = match.Plays.Any(p => p.Turn == turn && p.Side == side &&
                (entity > 0 && p.EntityId > 0
                    ? p.EntityId == entity
                    : p.Order == order && p.Name == name));
            if (alreadySeen || match.Plays.Count >= MaxPlaysPerMatch) continue;
            match.Plays.Add(new HistoryPlay
            {
                Turn = turn, Side = side, Name = name.Length > 110 ? name[..110] : name,
                CardId = cardId, EntityId = entity > 0 ? entity : null, Order = order
            });
            changed = true;
        }
        return changed;
    }

    private static string? Text(JsonElement obj, string field) =>
        obj.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Number(JsonElement obj, string field) =>
        obj.TryGetProperty(field, out var value) && value.TryGetInt32(out var number)
            ? number : null;

    private void Finish(DateTimeOffset now)
    {
        if (_active == null) return;
        _active.FinishedUtc = now;
        _active.LastSeenUtc = now;
        _active = null;
        _outOfGameSince = null;
        Revision++;
        Save();
    }

    public void Flush() => Save();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 4_000_000) return;
            var saved = JsonSerializer.Deserialize<List<HistoryMatch>>(File.ReadAllText(_path));
            if (saved == null) return;
            foreach (var entry in saved.Take(MaxMatches))
            {
                if (entry is null || entry.Plays is null || entry.Plays.Count > MaxPlaysPerMatch ||
                    string.IsNullOrWhiteSpace(entry.Id)) continue;
                _matches.Add(entry);
            }

            var pending = _matches.FirstOrDefault(m => m.FinishedUtc is null);
            if (pending != null)
            {
                if (DateTimeOffset.UtcNow - pending.LastSeenUtc < TimeSpan.FromMinutes(2))
                    _active = pending;
                else
                {
                    pending.FinishedUtc = pending.LastSeenUtc;
                    Save();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep an unreadable archive untouched instead of overwriting it.
            _matches.Clear();
            _writeEnabled = false;
        }
    }

    private bool _writeEnabled = true;
    private void Save()
    {
        if (!_writeEnabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(_matches));
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
