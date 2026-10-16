using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Coach.Core;

namespace Coach.App;

internal sealed class HistoryMatchEntry
{
    public HistoryMatchEntry(HistoryMatch match)
    {
        Match = match;
        Date = match.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy  HH:mm");
        Opponents = match.PlayerClass + "  vs  " + match.OpponentClass;
        Summary = match.Plays.Count + " kart • do tury " + match.LastObservedTurn;
        State = match.FinishedUtc is null ? "W TRAKCIE" : "ZAKOŃCZONY";
    }

    public HistoryMatch Match { get; }
    public string Id => Match.Id;
    public string Date { get; }
    public string Opponents { get; }
    public string Summary { get; }
    public string State { get; }
}

internal sealed class HistoryCardEntry : INotifyPropertyChanged
{
    private ImageSource? _art;

    public HistoryCardEntry(HistoryPlay play)
    {
        Name = play.Name;
        CardId = play.CardId;
    }

    public string Name { get; }
    public string? CardId { get; }
    public ImageSource? Art
    {
        get => _art;
        set
        {
            if (ReferenceEquals(_art, value)) return;
            _art = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class HistoryTurnEntry
{
    public HistoryTurnEntry(IGrouping<int, HistoryPlay> turn)
    {
        Heading = "TURA " + turn.Key;
        Mine = turn.Where(p => p.Side == "player").OrderBy(p => p.Order)
            .Select(p => new HistoryCardEntry(p)).ToArray();
        Theirs = turn.Where(p => p.Side == "opponent").OrderBy(p => p.Order)
            .Select(p => new HistoryCardEntry(p)).ToArray();
    }

    public string Heading { get; }
    public IReadOnlyList<HistoryCardEntry> Mine { get; }
    public IReadOnlyList<HistoryCardEntry> Theirs { get; }
}
