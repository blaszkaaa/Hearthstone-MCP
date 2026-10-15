using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Coach.Core;

namespace Coach.App;

public partial class MainWindow : Window
{
    private readonly ChatGptAuth _auth = new();
    private readonly ChatGptInference _inference;
    private readonly AnalysisScheduler _scheduler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GameSnapshot _snapshot = new();
    private CancellationTokenSource? _requestCts;
    private bool _busy;
    private int _analysisNumber;
    private string? _lastShownDecisionKey;
    private int _lastShownTurn = -1;
    private string? _pendingChange;
    private DateTimeOffset _pendingSince = DateTimeOffset.MinValue;
    private bool _adviceMarkedOutdated;
    private static readonly TimeSpan ConfirmChangeAfter = TimeSpan.FromSeconds(4);

    public MainWindow()
    {
        InitializeComponent();
        _inference = new ChatGptInference(_auth);
        _timer.Tick += Poll;
        UpdateAccount();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        RefreshState();
        if (_auth.SignedIn) await LoadModelsAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _requestCts?.Cancel();
        _requestCts?.Dispose();
    }

    private void Poll(object? sender, EventArgs e)
    {
        RefreshState();
        var now = DateTimeOffset.UtcNow;
        bool actionable = _snapshot.InGame && (_snapshot.MyTurn || _snapshot.ChoiceActive);
        var observed = actionable
            ? _snapshot.Turn + ":" + _snapshot.DecisionKey
            : (_snapshot.InGame ? "NOT_MY_TURN" : "NO_GAME");
        bool matches = actionable && _lastShownDecisionKey == _snapshot.DecisionKey &&
            _lastShownTurn == _snapshot.Turn;

        // Do not drop an answer because HDT briefly reports a different card,
        // an incomplete frame or no game. Confirm sustained change first.
        if (_lastShownDecisionKey is not null)
        {
            if (matches)
            {
                _pendingChange = null;
                if (_adviceMarkedOutdated)
                {
                    _adviceMarkedOutdated = false;
                    AiStatus.Text = "Gotowa rekomendacja";
                    AdviceMeta.Text = "Dane aktualne • tura " + _snapshot.Turn;
                }
            }
            else
            {
                if (_pendingChange != observed)
                {
                    _pendingChange = observed;
                    _pendingSince = now;
                }
                else if (now - _pendingSince >= ConfirmChangeAfter && !_adviceMarkedOutdated)
                {
                    _adviceMarkedOutdated = true;
                    if (!actionable)
                    {
                        // No actionable move for this phase. Hide the old action
                        // only after the state has remained stable for 4 seconds.
                        MoveText.Text = _snapshot.InGame ? "Czekam na Twoją turę" : "Czekam na mecz";
                        AdviceText.Text = "";
                        TargetPanel.Visibility = Visibility.Collapsed;
                        NextPanel.Visibility = Visibility.Collapsed;
                        WhyPanel.Visibility = Visibility.Collapsed;
                        AiStatus.Text = "Wstrzymano porady";
                        AdviceMeta.Text = "Nowa rekomendacja pojawi się w Twojej turze.";
                        _lastShownDecisionKey = null;
                        _pendingChange = null;
                        _adviceMarkedOutdated = false;
                    }
                    else
                    {
                        // Keep the prior move visible while the new answer loads,
                        // but clearly identify it as no longer current.
                        AiStatus.Text = "Aktualizuję po zmianie gry";
                        AdviceMeta.Text = "Poprzednia wskazówka może być nieaktualna • trwa aktualizacja.";
                    }
                }
            }
        }

        // Only cancel a long-running request for a confirmed, substantive game
        // change. A single noisy HDT frame is never enough to cancel it.
        if (_busy && _pendingChange is not null &&
            now - _pendingSince >= ConfirmChangeAfter)
            _requestCts?.Cancel();

        if (_auth.SignedIn && ModelPicker.SelectedItem is ChatGptInference.ModelChoice model &&
            _scheduler.ShouldAnalyze(_snapshot, now, _busy, AutoCheck.IsChecked == true))
        {
            _ = AnalyzeAsync(_snapshot, model);
        }
    }

    private void RefreshState()
    {
        _snapshot = GameSnapshot.Read();
        var connected = _snapshot.Connected;
        var indicator = (Brush)FindResource(connected ? "Accent" : "Muted");
        ConnectionDot.Fill = indicator;
        HeaderIndicator.Fill = indicator;
        ConnectionCard.Background = (Brush)FindResource(connected ? "AccentTint" : "NeutralTint");
        HeaderStateBadge.Background = (Brush)FindResource(connected ? "AccentTint" : "NeutralTint");
        ConnectionTitle.Text = connected ? "HDT połączony" : "Brak połączenia";
        ConnectionTitle.Foreground = (Brush)FindResource(connected ? "Accent" : "White");
        ConnectionText.Text = _snapshot.Status;
        HeaderStatusText.Text = !connected ? "HDT rozłączony" :
            !_snapshot.InGame ? "HDT gotowy" :
            _snapshot.ChoiceActive ? "Wybór kart" :
            _snapshot.MyTurn ? "Twoja tura" : "Partia trwa";
        HeaderStatusText.Foreground = indicator;
        GamePhaseText.Text = _snapshot.Status;
        TurnText.Text = _snapshot.InGame ? _snapshot.Turn.ToString() : "—";
        ManaText.Text = _snapshot.InGame ? _snapshot.Mana?.ToString() ?? "?" : "—";
        HandText.Text = _snapshot.InGame ? $"Ręka: {_snapshot.HandCount} kart" : "Ręka: —";
        ChoiceText.Text = !_snapshot.InGame ? "Wybory: —" :
            !_snapshot.ChoiceSupported ? "Wybory: brak obsługi" :
            _snapshot.ChoiceActive ? $"Wybory: {_snapshot.ChoicesCount}" :
            "Wybory: brak";
    }

    private async void SignInClick(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        AiStatus.Text = "Otwieram logowanie ChatGPT...";
        try
        {
            await _auth.SignInAsync();
            UpdateAccount();
            AiStatus.Text = "Gotowy do analizy";
            await LoadModelsAsync();
        }
        catch (OperationCanceledException) { AiStatus.Text = "Przerwano logowanie"; }
        catch (Exception ex) { AiStatus.Text = "Nie udało się zalogować"; AdviceMeta.Text = ex.Message; }
        finally { SignInButton.IsEnabled = true; }
    }

    private async Task LoadModelsAsync()
    {
        ModelPicker.IsEnabled = false;
        try
        {
            var choices = await _inference.ModelsAsync(CancellationToken.None);
            ModelPicker.ItemsSource = choices;
            var suggested = choices.FirstOrDefault(x =>
                x.Slug.Contains("mini", StringComparison.OrdinalIgnoreCase) ||
                x.Slug.Contains("instant", StringComparison.OrdinalIgnoreCase));
            ModelPicker.SelectedItem = suggested ?? choices.FirstOrDefault();
            if (choices.Count == 0) AdviceMeta.Text = "Konto nie udostępnia modeli dla tego połączenia.";
        }
        catch (Exception ex) { AdviceMeta.Text = "Modele niedostępne: " + ex.Message; }
        finally { ModelPicker.IsEnabled = true; }
    }

    private void UpdateAccount()
    {
        var signedIn = _auth.SignedIn;
        AccountText.Text = signedIn ? _auth.Email : "Zaloguj się, aby uruchomić analizę.";
        AccountStateText.Text = signedIn ? "Plan ChatGPT aktywny" : "Nie połączono";
        AccountStateText.Foreground = (Brush)FindResource(signedIn ? "Accent" : "White");
        AccountDot.Fill = (Brush)FindResource(signedIn ? "Accent" : "Muted");
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SignOutClick(object sender, RoutedEventArgs e)
    {
        _requestCts?.Cancel();
        SignOutButton.IsEnabled = false;
        try { await _auth.SignOutAsync(); }
        finally
        {
            ModelPicker.ItemsSource = null;
            UpdateAccount();
            AiStatus.Text = "Wylogowano z ChatGPT";
            SignOutButton.IsEnabled = true;
        }
    }

    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        RefreshState();
        if (!_snapshot.InGame || (!_snapshot.MyTurn && !_snapshot.ChoiceActive))
        {
            AiStatus.Text = "Poczekaj na swoją turę";
            return;
        }
        if (!_auth.SignedIn || ModelPicker.SelectedItem is not ChatGptInference.ModelChoice model)
        {
            AiStatus.Text = "Najpierw połącz konto i wybierz model";
            return;
        }
        if (_busy) return;
        _scheduler.MarkAnalyzed(_snapshot, DateTimeOffset.UtcNow);
        await AnalyzeAsync(_snapshot, model);
    }

    private async Task AnalyzeAsync(GameSnapshot original, ChatGptInference.ModelChoice model)
    {
        if (_busy) return;
        _busy = true;
        AnalyzeButton.IsEnabled = false;
        _requestCts = new CancellationTokenSource();
        var myRequest = _requestCts;
        var number = ++_analysisNumber;
        bool previousAdviceOutdated = _lastShownDecisionKey is not null &&
            (_lastShownDecisionKey != original.DecisionKey || _lastShownTurn != original.Turn);
        AiStatus.Text = previousAdviceOutdated ? "Poprzednia wskazówka nieaktualna" :
            original.ChoiceActive ? "Sprawdzam wybór kart..." : "Analizuję dostępne zagrania...";
        // During a refresh keep the existing suggestion on screen; do not clear
        // it when starting or failing another request.
        if (_lastShownDecisionKey is null)
        {
            MoveText.Text = original.ChoiceActive ? "Wybieram najlepszą opcję..." : "Sprawdzam karty...";
            TargetPanel.Visibility = Visibility.Collapsed;
            NextPanel.Visibility = Visibility.Collapsed;
            WhyPanel.Visibility = Visibility.Collapsed;
        }
        AdviceMeta.Text = previousAdviceOutdated ?
            "Trwa nowa analiza • nie zagrywaj poprzednio wskazanej karty bez sprawdzenia." :
            $"Model: {model.DisplayName} • analiza #{number}";
        try
        {
            var result = await _inference.AnalyzeAsync(original, model.Slug, myRequest.Token);
            RefreshState();
            if (_snapshot.InGame &&
                (_snapshot.MyTurn || _snapshot.ChoiceActive) &&
                _snapshot.Turn == original.Turn &&
                _snapshot.DecisionKey == original.DecisionKey)
            {
                var card = AdviceCard.Parse(result);
                MoveText.Text = card.Move;
                TargetText.Text = card.Target;
                TargetPanel.Visibility = card.Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                AdviceText.Text = card.Next;
                NextPanel.Visibility = card.Next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                WhyText.Text = card.Reason;
                WhyPanel.Visibility = card.Reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _lastShownDecisionKey = original.DecisionKey;
                _lastShownTurn = original.Turn;
                _pendingChange = null;
                _adviceMarkedOutdated = false;
                AiStatus.Text = original.ChoiceActive ? "Wybór Odkryj / kart" : "Gotowa rekomendacja";
                AdviceMeta.Text = $"Tura {original.Turn} • {DateTime.Now:HH:mm:ss} • {model.DisplayName}";
            }
            else
            {
                // State changed while AI was responding: never overwrite a more
                // recent recommendation with an outdated one.
                AiStatus.Text = "Zmieniono stan rozgrywki";
                AdviceMeta.Text = "Pominięto nieaktualną odpowiedź. Czekam na stabilny stan.";
                if (_lastShownDecisionKey is null)
                    MoveText.Text = "Czekam na aktualizację...";
            }
        }
        catch (OperationCanceledException)
        {
            AiStatus.Text = "Ponawiam po zmianie planszy";
            AdviceMeta.Text = "Przerwano nieaktualną analizę • czekam na stabilny stan.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Błąd analizy";
            AdviceMeta.Text = ex.Message;
            if (_lastShownDecisionKey is null)
                MoveText.Text = "Nie udało się pobrać porady";
        }
        finally
        {
            _busy = false;
            AnalyzeButton.IsEnabled = true;
            if (ReferenceEquals(_requestCts, myRequest)) _requestCts = null;
            myRequest.Dispose();
        }
    }

    private void AutoChanged(object sender, RoutedEventArgs e)
    {
        AdviceMeta.Text = AutoCheck.IsChecked == true ?
            "Automatyczna analiza włączona • maks. 4 analizy na turę, co najmniej 14 s odstępu." :
            "Automatyczna analiza wyłączona • możesz kliknąć „Analizuj teraz”.";
    }
    private void TopmostChanged(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;
    private void UsageClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://chatgpt.com/#settings/Usage") { UseShellExecute = true });
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
