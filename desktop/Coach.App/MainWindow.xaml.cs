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
    private readonly MatchIntelligence _intelligence = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GameSnapshot _snapshot = new();
    private CancellationTokenSource? _requestCts;
    private bool _busy;
    private int _analysisNumber;
    private GameSnapshot? _shownSnapshot;
    private GameSnapshot? _activeRequestSnapshot;
    private readonly SnapshotTransitionGuard _shownGuard = new();
    private readonly SnapshotTransitionGuard _activeRequestGuard = new();
    private bool _cancelRequested;
    private string _cancelReason = "";
    private bool _closing;
    private string? _lastLoggedSnapshot;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private string _lastSchedulerReason = "";


    public MainWindow()
    {
        InitializeComponent();
        _inference = new ChatGptInference(_auth);
        _timer.Tick += Poll;
        UpdateAccount();
        DiagnosticLog.Write("app.start", new { version = "0.5", state_path = GameSnapshot.DefaultPath, log_dir = DiagnosticLog.DirectoryPath });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        RefreshState();
        if (_auth.SignedIn) await LoadModelsAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        DiagnosticLog.Write("app.close");
        _closing = true;
        _timer.Stop();
        _cancelReason = "application_closed";
        _requestCts?.Cancel();
    }

    private void Poll(object? sender, EventArgs e)
    {
        RefreshState();
        var now = DateTimeOffset.UtcNow;
        _intelligence.Observe(_snapshot, now);
        if (_lastLoggedSnapshot != _snapshot.Fingerprint || now - _lastHeartbeat > TimeSpan.FromSeconds(10))
        {
            _lastLoggedSnapshot = _snapshot.Fingerprint;
            _lastHeartbeat = now;
            DiagnosticLog.Write("hdt.snapshot", new
            {
                connected = _snapshot.Connected,
                in_game = _snapshot.InGame,
                turn = _snapshot.Turn,
                my_turn = _snapshot.MyTurn,
                phase = _snapshot.Phase,
                mulligan = _snapshot.MulliganActive,
                choice = _snapshot.ChoiceActive,
                offers = _snapshot.ChoicesCount,
                hand = _snapshot.HandCount,
                mana = _snapshot.Mana,
                status = _snapshot.Status,
                decision_key = _snapshot.DecisionKey,
                fingerprint = _snapshot.Fingerprint,
                game_data = _snapshot.TacticalJson
            });
        }
        if (_shownSnapshot is not null &&
            _shownGuard.HasChanged(_shownSnapshot, _snapshot, now))
        {
            DiagnosticLog.Write("advice.expired", new
            {
                shown_turn = _shownSnapshot.Turn, shown_phase = _shownSnapshot.Phase,
                new_turn = _snapshot.Turn, new_phase = _snapshot.Phase,
                old_key = _shownSnapshot.DecisionKey, new_key = _snapshot.DecisionKey
            });
            ClearDisplayedAdvice();
        }

        // Cancellation MUST compare the snapshot for the CURRENT request, not
        // the already displayed recommendation from an earlier turn/mulligan.
        if (_busy && !_cancelRequested && _activeRequestSnapshot is not null &&
            _activeRequestGuard.HasChanged(_activeRequestSnapshot, _snapshot, now))
        {
            _cancelRequested = true;
            _cancelReason = "superseded_by_game_state";
            DiagnosticLog.Write("ai.cancel.superseded", new
            {
                request_turn = _activeRequestSnapshot.Turn,
                request_phase = _activeRequestSnapshot.Phase,
                current_turn = _snapshot.Turn,
                current_phase = _snapshot.Phase,
                request_key = _activeRequestSnapshot.DecisionKey,
                current_key = _snapshot.DecisionKey
            });
            _requestCts?.Cancel();
        }

        if (_auth.SignedIn && ModelPicker.SelectedItem is ChatGptInference.ModelChoice model &&
            _scheduler.ShouldAnalyze(_snapshot, now, _busy, AutoCheck.IsChecked == true))
        {
            DiagnosticLog.Write("ai.scheduled", new { phase = _snapshot.Phase, turn = _snapshot.Turn, key = _snapshot.DecisionKey });
            _ = AnalyzeAsync(_snapshot, model);
        }
        if (_lastSchedulerReason != _scheduler.LastDecision)
        {
            _lastSchedulerReason = _scheduler.LastDecision;
            DiagnosticLog.Write("scheduler.status", new { reason = _lastSchedulerReason, turn = _snapshot.Turn, phase = _snapshot.Phase });
        }
    }

    private void ClearDisplayedAdvice()
    {
        _shownSnapshot = null;
        _shownGuard.Reset();
        MoveText.FontSize = 28;
        MoveText.Text = !_snapshot.InGame ? "Czekam na mecz" :
            _snapshot.MulliganActive ? "Co zostawić na ręce?" :
            _snapshot.ChoiceActive ? "Wybieram najlepszą kartę..." :
            _snapshot.MyTurn ? "Sprawdzam następny ruch..." : "Czekam na Twoją turę";
        AdviceText.Text = "";
        TargetPanel.Visibility = Visibility.Collapsed;
        NextPanel.Visibility = Visibility.Collapsed;
        WhyPanel.Visibility = Visibility.Collapsed;
        AiStatus.Text = _snapshot.Actionable ? "Aktualizuję poradę..." : "Oczekiwanie";
        AdviceMeta.Text = "Wskazówka z poprzedniej fazy lub ruchu została ukryta.";
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
            _snapshot.MulliganActive ? "Wybór ręki" :
            _snapshot.ChoiceActive ? "Wybór kart" :
            _snapshot.MyTurn ? "Twoja tura" : "Partia trwa";
        HeaderStatusText.Foreground = indicator;
        GamePhaseText.Text = _snapshot.Status;
        TurnText.Text = _snapshot.InGame ? _snapshot.Turn.ToString() : "—";
        ManaText.Text = _snapshot.InGame ? _snapshot.Mana?.ToString() ?? "?" : "—";
        HandText.Text = _snapshot.InGame ? $"Ręka: {_snapshot.HandCount} kart" : "Ręka: —";
        ChoiceText.Text = !_snapshot.InGame ? "Wybory: —" :
            _snapshot.MulliganActive ? $"Ręka: {_snapshot.HandCount}" :
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
            DiagnosticLog.Write("auth.signin.success");
            await LoadModelsAsync();
        }
        catch (OperationCanceledException) { AiStatus.Text = "Przerwano logowanie"; }
        catch (Exception ex) { DiagnosticLog.Error("auth.signin.failure", ex); AiStatus.Text = "Nie udało się zalogować"; AdviceMeta.Text = ex.Message; }
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
            DiagnosticLog.Write("ai.models", new { count = choices.Count, models = choices.Select(x => x.Slug).ToArray() });
            if (choices.Count == 0) AdviceMeta.Text = "Konto nie udostępnia modeli dla tego połączenia.";
        }
        catch (Exception ex) { DiagnosticLog.Error("ai.models.failure", ex); AdviceMeta.Text = "Modele niedostępne: " + ex.Message; }
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
        _cancelReason = "signout";
        _requestCts?.Cancel();
        SignOutButton.IsEnabled = false;
        try { await _auth.SignOutAsync(); DiagnosticLog.Write("auth.signout"); }
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
        if (!_snapshot.Actionable)
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
        _intelligence.Observe(_snapshot, DateTimeOffset.UtcNow);
        _scheduler.MarkAnalyzed(_snapshot, DateTimeOffset.UtcNow);
        DiagnosticLog.Write("ai.manual", new { phase = _snapshot.Phase, turn = _snapshot.Turn, key = _snapshot.DecisionKey });
        await AnalyzeAsync(_snapshot, model);
    }

    private async Task AnalyzeAsync(GameSnapshot original, ChatGptInference.ModelChoice model)
    {
        if (_busy) return;
        _busy = true;
        AnalyzeButton.IsEnabled = false;
        _requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var myRequest = _requestCts;
        _activeRequestSnapshot = original;
        _activeRequestGuard.Reset();
        _cancelRequested = false;
        _cancelReason = "";
        var number = ++_analysisNumber;
        bool previousAdviceOutdated = _shownSnapshot is not null &&
            (_shownSnapshot.DecisionKey != original.DecisionKey ||
             _shownSnapshot.Turn != original.Turn ||
             _shownSnapshot.Phase != original.Phase);
        AiStatus.Text = previousAdviceOutdated ? "Poprzednia wskazówka nieaktualna" :
            original.MulliganActive ? "Analizuję rękę początkową..." :
            original.ChoiceActive ? "Sprawdzam wybór kart..." : "Analizuję dostępne zagrania...";
        // During a refresh keep the existing suggestion on screen; do not clear
        // it when starting or failing another request.
        if (_shownSnapshot is null)
        {
            MoveText.Text = original.MulliganActive ? "Co zostawić?" :
                original.ChoiceActive ? "Wybieram najlepszą opcję..." : "Sprawdzam karty...";
            TargetPanel.Visibility = Visibility.Collapsed;
            NextPanel.Visibility = Visibility.Collapsed;
            WhyPanel.Visibility = Visibility.Collapsed;
        }
        AdviceMeta.Text = previousAdviceOutdated ?
            "Trwa nowa analiza • nie zagrywaj poprzednio wskazanej karty bez sprawdzenia." :
            $"Model: {model.DisplayName} • analiza #{number}";
        var started = DateTimeOffset.UtcNow;
        // Snapshot memory at request start: future observations must never
        // change the meaning of an already running inference.
        var tacticalContext = _intelligence.BuildContext(original);
        var prompt = AdvicePrompt.Build(original, tacticalContext);
        DiagnosticLog.Write("ai.request", new
        {
            request = number, turn = original.Turn, phase = original.Phase, model = model.Slug,
            decision_key = original.DecisionKey, prompt = prompt
        });
        DiagnosticLog.Write("game.intelligence", new
        {
            request = number,
            turn = original.Turn,
            observed_public_plays = _intelligence.Memory.TotalObservedPlays,
            context = tacticalContext
        });
        try
        {
            var result = await _inference.AnalyzeAsync(original, model.Slug, prompt, myRequest.Token);
            DiagnosticLog.Write("ai.response", new
            {
                request = number, phase = original.Phase, turn = original.Turn,
                duration_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                response = result
            });
            RefreshState();
            if (_snapshot.InGame &&
                _snapshot.Actionable &&
                _snapshot.Turn == original.Turn &&
                _snapshot.DecisionKey == original.DecisionKey)
            {
                var card = AdviceCard.Parse(result, original.Phase);
                MoveText.FontSize = original.MulliganActive ? 20 : 28;
                MoveText.Text = card.Move;
                TargetText.Text = card.Target;
                TargetPanel.Visibility = card.Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                AdviceText.Text = card.Next;
                NextPanel.Visibility = card.Next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                WhyText.Text = card.Reason;
                WhyPanel.Visibility = card.Reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _shownSnapshot = original;
                _shownGuard.Reset();
                AiStatus.Text = original.MulliganActive ? "Zostaw / odrzuć karty" :
                    original.ChoiceActive ? "Wybór Odkryj / kart" : "Gotowa rekomendacja";
                DiagnosticLog.Write("ai.displayed", new { request = number, decision_key = original.DecisionKey, turn = original.Turn });
                AdviceMeta.Text = $"Tura {original.Turn} • {DateTime.Now:HH:mm:ss} • {model.DisplayName}";
            }
            else
            {
                // State changed while AI was responding: never overwrite a more
                // recent recommendation with an outdated one.
                AiStatus.Text = "Zmieniono stan rozgrywki";
                AdviceMeta.Text = "Pominięto nieaktualną odpowiedź. Czekam na stabilny stan.";
                if (_shownSnapshot is null)
                    MoveText.Text = "Czekam na aktualizację...";
                DiagnosticLog.Write("ai.discarded", new { request = number, cause = "state_changed", old_key = original.DecisionKey, new_key = _snapshot.DecisionKey, phase = _snapshot.Phase });
            }
        }
        catch (OperationCanceledException)
        {
            _scheduler.RecordFailure(original);
            bool timedOut = myRequest.IsCancellationRequested && _cancelReason.Length == 0;
            string reason = timedOut ? "timeout_45s" :
                _cancelReason.Length > 0 ? _cancelReason : "network_cancelled";
            DiagnosticLog.Write("ai.canceled_or_timeout", new
            {
                request = number, reason,
                request_turn = original.Turn, current_turn = _snapshot.Turn,
                elapsed_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                phase = original.Phase
            });
            if (!_closing)
            {
                AiStatus.Text = reason == "superseded_by_game_state"
                    ? "Zmieniła się sytuacja w grze" : "Nie udało się ukończyć analizy";
                AdviceMeta.Text = timedOut
                    ? "AI nie odpowiedziało w 45 s. Możesz wybrać szybszy model lub ponowić analizę."
                    : reason == "superseded_by_game_state"
                    ? "Poprzednie zapytanie anulowano. Nowa tura otrzyma osobną analizę."
                    : "Zapytanie przerwane: " + reason;
            }
        }
        catch (Exception ex)
        {
            _scheduler.RecordFailure(original);
            DiagnosticLog.Error("ai.failure", ex);
            AiStatus.Text = "Błąd analizy";
            AdviceMeta.Text = ex.Message;
            if (_shownSnapshot is null)
                MoveText.Text = "Nie udało się pobrać porady";
        }
        finally
        {
            _busy = false;
            _activeRequestSnapshot = null;
            _activeRequestGuard.Reset();
            AnalyzeButton.IsEnabled = true;
            if (ReferenceEquals(_requestCts, myRequest)) _requestCts = null;
            myRequest.Dispose();
            DiagnosticLog.Write("ai.complete", new { request = number, elapsed_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds, phase = original.Phase });
        }
    }

    private void AutoChanged(object sender, RoutedEventArgs e)
    {
        AdviceMeta.Text = AutoCheck.IsChecked == true ?
            "Automatyczna analiza włączona • szybki wybór kart i ponawianie przy zmianach." :
            "Automatyczna analiza wyłączona • możesz kliknąć „Analizuj teraz”.";
    }
    private void TopmostChanged(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;
    private void OpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DiagnosticLog.DirectoryPath);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DiagnosticLog.DirectoryPath + "\"") { UseShellExecute = true });
            DiagnosticLog.Write("logs.opened");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("logs.open.failure", ex);
            AdviceMeta.Text = "Folder logów: " + DiagnosticLog.DirectoryPath;
        }
    }
    private void UsageClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://chatgpt.com/#settings/Usage") { UseShellExecute = true });
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
