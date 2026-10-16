using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Coach.App;

/// <summary>A single card-backed row in the plan or side-choice panel.</summary>
internal sealed class CardVisualItem : INotifyPropertyChanged
{
    private ImageSource? _art;
    private bool _recommended;
    private string _mulliganDecision = "ANALIZUJĘ…";

    public CardVisualItem(int index, string caption, string? cardId, string description = "")
    {
        Index = index;
        Caption = caption;
        CardId = cardId;
        Description = description;
    }

    public int Index { get; }
    public string Caption { get; }
    public string Description { get; }
    public string? CardId { get; }
    public string IndexText => Index.ToString();
    public string Placeholder => CardId is null ? "?" : "HS";
    public bool Recommended
    {
        get => _recommended;
        set { if (_recommended != value) { _recommended = value; Changed(); } }
    }

    public string MulliganDecision
    {
        get => _mulliganDecision;
        set { if (_mulliganDecision != value) { _mulliganDecision = value; Changed(); } }
    }

    public ImageSource? Art
    {
        get => _art;
        set { if (_art != value) { _art = value; Changed(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
