using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    private readonly OpponentTurnScheduler _opponentScheduler = new();
    private readonly MatchIntelligence _intelligence = new();
    private readonly DeckStrategy _deckStrategy = new();
    private CancellationTokenSource? _deckRequestCts;
    private string? _deckObservedSignature;
    private string? _deckAttemptSignature;
    private DateTimeOffset _deckAttemptAt = DateTimeOffset.MinValue;
    private bool _deckBusy;
    private readonly MatchHistoryStore _history = new();
    private readonly ObservableCollection<HistoryMatchEntry> _historyRows = new();
    private int _shownHistoryRevision = -1;
    private readonly CardArtCache _cardImages = new();
    private readonly ObservableCollection<CardVisualItem> _planRows = new();
    private readonly ObservableCollection<CardVisualItem> _handRows = new();
    private string? _handSignature;
    private readonly ObservableCollection<CardVisualItem> _choiceRows = new();
    private readonly ObservableCollection<CardVisualItem> _mulliganRows = new();
    private int _turnPlanTurn = -1;
    private string? _shownChoiceKey;
    private string? _latestChoiceKey;
    private readonly HdtStateReconciler _stateReconciler = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GameSnapshot _snapshot = new();
    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _opponentRequestCts;
    private int _opponentRequestTurn = -1;
    private bool _opponentBusy;
    private bool _busy;
    private int _analysisNumber;
    private GameSnapshot? _shownSnapshot;
    private GameSnapshot? _activeRequestSnapshot;
    private readonly SnapshotTransitionGuard _shownGuard = new();
    private readonly SnapshotTransitionGuard _activeRequestGuard = new();
    private bool _cancelRequested;
    private string _cancelReason = "";
    private bool _closing;
    private bool _appUpdateBusy;
    private string? _lastLoggedSnapshot;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private string _lastSchedulerReason = "";
    private string? _rejectedActionKey;
    private string? _rejectedActionReason;


    public MainWindow()
    {
        InitializeComponent();
        PlanItems.ItemsSource = _planRows;
        HandItems.ItemsSource = _handRows;
        ChoiceItems.ItemsSource = _choiceRows;
        MulliganItems.ItemsSource = _mulliganRows;
        HistoryMatchList.ItemsSource = _historyRows;
        _inference = new ChatGptInference(_auth);
        _timer.Tick += Poll;
        UpdateAccount();
        AppVersionText.Text = "Zainstalowana wersja: " +
            (AppUpdater.InstalledVersion.Length > 7 ?
                AppUpdater.InstalledVersion[..7] : AppUpdater.InstalledVersion);
        DiagnosticLog.Write("app.start", new { version = AppUpdater.InstalledVersion, state_path = GameSnapshot.DefaultPath, log_dir = DiagnosticLog.DirectoryPath });
    }

    private void NavigateClick(object sender, RoutedEventArgs e)
    {
        var settings = ReferenceEquals(sender, SettingsNavButton);
        var history = ReferenceEquals(sender, HistoryNavButton);
        MainPage.Visibility = !settings && !history ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        MainNavButton.Background = (Brush)FindResource(!settings && !history ? "AccentTint" : "Panel2");
        MainNavButton.Foreground = (Brush)FindResource(!settings && !history ? "Accent" : "White");
        SettingsNavButton.Background = (Brush)FindResource(settings ? "AccentTint" : "Panel2");
        SettingsNavButton.Foreground = (Brush)FindResource(settings ? "Accent" : "White");
        HistoryNavButton.Background = (Brush)FindResource(history ? "AccentTint" : "Panel2");
        HistoryNavButton.Foreground = (Brush)FindResource(history ? "Accent" : "White");
        if (history)
        {
            _shownHistoryRevision = -1;
            RefreshHistory();
        }
        if (settings) SettingsPage.ScrollToTop();
        else if (!history) MainPage.ScrollToTop();
    }

    private void OpenSettingsFromDashboard(object sender, RoutedEventArgs e) =>
        NavigateClick(SettingsNavButton, e);

    private void WindowShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var target = e.Key switch
        {
            Key.D1 or Key.NumPad1 => MainNavButton,
            Key.D2 or Key.NumPad2 => SettingsNavButton,
            Key.D3 or Key.NumPad3 => HistoryNavButton,
            _ => null
        };
        if (target is null) return;
        NavigateClick(target, new RoutedEventArgs());
        e.Handled = true;
    }

    private bool? _historyNarrowLayout;

    private void HistoryLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        // At 860px window width the workspace has ~610px available. Never
        // force a 250px match list beside an unreadably narrow card timeline.
        var width = e.NewSize.Width;
        if (width <= 0) return;
        bool narrow = width < 760;
        if (_historyNarrowLayout == narrow) return;
        _historyNarrowLayout = narrow;

        HistoryListColumn.Width = narrow
            ? new GridLength(1, GridUnitType.Star) : new GridLength(250);
        HistoryGapColumn.Width = new GridLength(narrow ? 0 : 14);
        HistoryDetailsColumn.Width = narrow
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HistoryTopRow.Height = narrow ? GridLength.Auto :
            new GridLength(1, GridUnitType.Star);
        HistoryBottomRow.Height = narrow
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(HistoryDetailsCard, narrow ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(HistoryDetailsCard, narrow ? 1 : 0);
        HistoryListPanel.MaxHeight = narrow ? 190 : double.PositiveInfinity;
        HistoryListPanel.Margin = narrow ? new Thickness(0, 0, 0, 12) :
            new Thickness(0);
        DiagnosticLog.Write("ui.history.layout", new { width, narrow });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        RefreshState();
        _history.Observe(_snapshot, DateTimeOffset.UtcNow);
        RefreshHistory();
        _ = CheckForAppUpdateOnStartupAsync();
        if (_auth.SignedIn) await LoadModelsAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _history.Flush();
        DiagnosticLog.Write("app.close");
        _closing = true;
        _timer.Stop();
        _cancelReason = "application_closed";
        _requestCts?.Cancel();
        _opponentRequestCts?.Cancel();
        _deckRequestCts?.Cancel();
    }

    private void Poll(object? sender, EventArgs e)
    {
        RefreshState();
        var now = DateTimeOffset.UtcNow;
        _intelligence.Observe(_snapshot, now);
        ObserveDeckStrategy(now);
        // Opponent inference must never compete with an actionable player turn.
        // Public played-card memory keeps collecting evidence even if canceled.
        if (_opponentBusy && (!_snapshot.InGame || _snapshot.MyTurn ||
            _snapshot.MulliganActive || _snapshot.ChoiceActive ||
            _snapshot.Turn != _opponentRequestTurn))
            _opponentRequestCts?.Cancel();
        _history.Observe(_snapshot, now);
        if (HistoryPage.Visibility == Visibility.Visible)
            RefreshHistory();
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
        // A Discover popup is an interruption, not a new turn plan:
        // never erase its visible instructions just to display a choice.
        bool preservePlan = VisualTurnPlan.ShouldPreserveTurnPlan(_turnPlanTurn, _snapshot);
        if (_shownSnapshot is not null && !preservePlan &&
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
        if (_auth.SignedIn && ModelPicker.SelectedItem is ChatGptInference.ModelChoice reviewModel &&
            _opponentScheduler.ShouldAnalyze(_snapshot, now, AutoCheck.IsChecked == true,
                _opponentBusy || _busy || _deckBusy))
        {
            DiagnosticLog.Write("ai.opponent.scheduled", new { turn = _snapshot.Turn, key = _snapshot.DecisionKey });
            _ = ReviewOpponentAsync(_snapshot, reviewModel);
        }
        if (_lastSchedulerReason != _scheduler.LastDecision)
        {
            _lastSchedulerReason = _scheduler.LastDecision;
            DiagnosticLog.Write("scheduler.status", new { reason = _lastSchedulerReason, turn = _snapshot.Turn, phase = _snapshot.Phase });
        }
    }

    private void ObserveDeckStrategy(DateTimeOffset now)
    {
        if (!DeckStrategy.TryGetUsableDeck(_snapshot, out var signature, out var deckJson))
        {
            if (_deckBusy && (_snapshot.InGame || _snapshot.Connected))
                _deckRequestCts?.Cancel();
            DeckStrategyName.Text = "Aktywna talia HDT";
            DeckStrategyStatus.Text = "Brak danych";
            DeckStrategyText.Text = !_snapshot.Connected
                ? "Uruchom HDT z wtyczką MCP."
                : "Wybierz aktywną talię w HDT lub zaktualizuj wtyczkę. " +
                  "Nie analizuję talii, której zgodności nie mogę potwierdzić.";
            return;
        }

        if (_deckObservedSignature != signature)
        {
            _deckRequestCts?.Cancel();
            _deckObservedSignature = signature;
            _deckAttemptSignature = null;
            DeckStrategyText.Text = "Rozpoznano nowy skład talii. Przygotowuję analizę...";
            DiagnosticLog.Write("deck.detected", new {
                signature, status = _snapshot.ActiveDeckStatus
            });
        }
        try
        {
            using var doc = JsonDocument.Parse(deckJson);
            if (doc.RootElement.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
                DeckStrategyName.Text = name.GetString() ?? "Aktywna talia HDT";
        }
        catch (JsonException) { DeckStrategyName.Text = "Aktywna talia HDT"; }

        if (_deckStrategy.IsCurrent(_snapshot))
        {
            DeckStrategyStatus.Text = "Strategia gotowa";
            DeckStrategyText.Text = _deckStrategy.Analysis;
            return;
        }
        if (_snapshot.Actionable && _deckBusy)
            _deckRequestCts?.Cancel();
        if (_deckBusy)
        {
            DeckStrategyStatus.Text = "Analizuję...";
            return;
        }
        if (_snapshot.InGame)
        {
            DeckStrategyStatus.Text = "Brak analizy wstępnej";
            DeckStrategyText.Text = "Przed meczem zabrakło czasu na osobną analizę. " +
                "AI nadal otrzymuje skład aktywnej talii podczas zagrywania.";
            return;
        }
        if (!_auth.SignedIn || ModelPicker.SelectedItem is not ChatGptInference.ModelChoice model)
        {
            DeckStrategyStatus.Text = "Oczekuję na ChatGPT";
            DeckStrategyText.Text = "Połącz konto i wybierz model, aby przeanalizować kombosy przed grą.";
            return;
        }
        if (_busy || _opponentBusy) return;
        if (_deckAttemptSignature == signature &&
            now - _deckAttemptAt < TimeSpan.FromSeconds(45))
        {
            DeckStrategyStatus.Text = "Ponowię analizę";
            return;
        }
        _ = AnalyzeDeckBeforeMatchAsync(_snapshot, signature, deckJson, model, now);
    }

    private async Task AnalyzeDeckBeforeMatchAsync(GameSnapshot startingState,
        string signature, string deckJson, ChatGptInference.ModelChoice model, DateTimeOffset now)
    {
        if (_deckBusy || _closing || startingState.InGame) return;
        _deckBusy = true;
        _deckAttemptSignature = signature;
        _deckAttemptAt = now;
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        _deckRequestCts = request;
        DeckStrategyStatus.Text = "Analizuję...";
        DeckStrategyText.Text = "Badam koszty kart, interakcje, możliwe kombosy i mulligan...";
        try
        {
            var prompt = DeckStrategyPrompt.Build(deckJson);
            DiagnosticLog.Write("deck.analysis.request", new {
                signature, model = model.Slug, prompt
            });
            var analysis = await _inference.AnalyzeDeckAsync(startingState, model.Slug,
                prompt, request.Token);
            if (!_closing &&
                DeckStrategy.TryGetUsableDeck(_snapshot, out var current, out _) &&
                string.Equals(signature, current, StringComparison.OrdinalIgnoreCase) &&
                _deckStrategy.Store(_snapshot, analysis))
            {
                DeckStrategyText.Text = _deckStrategy.Analysis;
                DeckStrategyStatus.Text = "Strategia gotowa";
                DiagnosticLog.Write("deck.analysis.completed", new { signature,
                    model = model.Slug, analysis = _deckStrategy.Analysis });
            }
            else
                DiagnosticLog.Write("deck.analysis.discarded", new { signature,
                    reason = "deck_changed_or_app_closed" });
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("deck.analysis.canceled", new { signature,
                in_game = _snapshot.InGame, actionable = _snapshot.Actionable });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("deck.analysis.failure", ex);
            if (!_closing && _deckObservedSignature == signature)
                DeckStrategyText.Text = "Analiza talii nie powiodła się: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_deckRequestCts, request)) _deckRequestCts = null;
            request.Dispose();
            _deckBusy = false;
            if (!_closing && _deckObservedSignature == signature && !_deckStrategy.IsCurrent(_snapshot))
                DeckStrategyStatus.Text = "Oczekuję na ponowienie";
        }
    }

    private async Task ReviewOpponentAsync(GameSnapshot original, ChatGptInference.ModelChoice model)
    {
        if (_opponentBusy || _closing) return;
        _opponentBusy = true;
        _opponentRequestTurn = original.Turn;
        var session = _intelligence.Memory.SessionId;
        var started = DateTimeOffset.UtcNow;
        var request = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _opponentRequestCts = request;
        try
        {
            // Freeze all evidence at the instant this request starts.
            var context = _intelligence.BuildContext(original, _deckStrategy.ForPrompt(original));
            var prompt = OpponentTurnPrompt.Build(original, context);
            DiagnosticLog.Write("ai.opponent.request", new {
                turn = original.Turn, session, model = model.Slug,
                fingerprint = original.Fingerprint, prompt
            });
            var review = await _inference.AnalyzeOpponentAsync(original, model.Slug, prompt, request.Token);
            if (!_closing && _snapshot.InGame &&
                _snapshot.Turn >= original.Turn &&
                _snapshot.Turn <= original.Turn + 1 &&
                session == _intelligence.Memory.SessionId &&
                _intelligence.RecordOpponentReview(original, review))
                DiagnosticLog.Write("ai.opponent.completed", new {
                    turn = original.Turn,
                    elapsed_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    review
                });
            else
                DiagnosticLog.Write("ai.opponent.discarded", new { turn = original.Turn, session });
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("ai.opponent.canceled", new {
                turn = original.Turn,
                elapsed_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            // Background review errors must not replace the visible coach advice.
            DiagnosticLog.Error("ai.opponent.failure", ex);
        }
        finally
        {
            if (ReferenceEquals(_opponentRequestCts, request)) _opponentRequestCts = null;
            request.Dispose();
            _opponentBusy = false;
            _opponentRequestTurn = -1;
        }
    }

    private void ClearDisplayedAdvice()
    {
        _shownSnapshot = null;
        _shownGuard.Reset();
        _turnPlanTurn = -1;
        _planRows.Clear();
        TurnPlanPanel.Visibility = Visibility.Collapsed;
        MoveText.Visibility = Visibility.Visible;
        MoveText.FontSize = 28;
        MoveText.Text = !_snapshot.InGame ? "Czekam na mecz" :
            _snapshot.MulliganActive ? "Co zostawić na ręce?" :
            _snapshot.ChoiceActive ? "Wybieram najlepszą kartę..." :
            _snapshot.MyTurn ? "Przygotowuję plan na tę turę..." : "Czekam na Twoją turę";
        AdviceText.Text = "";
        TargetPanel.Visibility = Visibility.Collapsed;
        NextPanel.Visibility = Visibility.Collapsed;
        WhyPanel.Visibility = Visibility.Collapsed;
        AiStatus.Text = _snapshot.Actionable ? "Aktualizuję poradę..." : "Oczekiwanie";
        AdviceMeta.Text = "Wskazówka z poprzedniej fazy lub ruchu została ukryta.";
    }

    private void RefreshState()
    {
        var raw = GameSnapshot.Read();
        _snapshot = _stateReconciler.Observe(raw, DateTimeOffset.UtcNow);
        if (_stateReconciler.LastEvent != "same_decision" &&
            _stateReconciler.LastEvent != "pending_confirmation")
            DiagnosticLog.Write("hdt.transition", new {
                kind = _stateReconciler.LastEvent,
                accepted_phase = _snapshot.Phase, accepted_turn = _snapshot.Turn,
                accepted_key = _snapshot.DecisionKey,
                raw_status = raw.Status, raw_phase = raw.Phase,
                raw_connected = raw.Connected
            });
        var connected = _snapshot.Connected;
        var indicator = (Brush)FindResource(connected ? "Accent" : "Muted");
        ConnectionDot.Fill = indicator;
        HeaderIndicator.Fill = indicator;
        ConnectionCard.Background = (Brush)FindResource(connected ? "AccentTint" : "NeutralTint");
        HeaderStateBadge.Background = (Brush)FindResource(connected ? "AccentTint" : "NeutralTint");
        ConnectionTitle.Text = connected ? "HDT połączony" : "Brak połączenia";
        ConnectionTitle.Foreground = (Brush)FindResource(connected ? "Accent" : "White");
        ConnectionText.Text = _snapshot.Status;
        ConnectionHelpPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
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
        UpdateChoiceVisuals(_snapshot);
        UpdateDashboard();
        UpdateVisibleHand(_snapshot);
    }

    private void UpdateDashboard()
    {
        DashboardHeading.Text = !_snapshot.InGame ? "Asystent rozgrywki" :
            _snapshot.MulliganActive ? "Ręka początkowa" :
            _snapshot.ChoiceActive ? "Wybór karty" :
            _snapshot.MyTurn ? "Twoja tura" : "Tura przeciwnika";
        DashboardSubheading.Text = _snapshot.MulliganActive
            ? "Zostaw lub odrzuć karty według planu talii."
            : _snapshot.ChoiceActive
            ? "Odkryj nie zmienia ustalonego planu. Nieznane efekty są warunkowe."
            : _snapshot.MyTurn
            ? "Jeden plan na całą turę. Bez automatycznych zmian po każdym zagraniu."
            : _snapshot.InGame
            ? "Obserwuję widoczne zagrania rywala. Nowy plan powstanie w Twojej turze."
            : "Uruchom Hearthstone i połącz HDT, aby zobaczyć analizę.";
        DashboardGameState.Text = !_snapshot.Connected ? "Oczekiwanie na HDT" :
            !_snapshot.InGame ? "Gotowy do gry" :
            _snapshot.MulliganActive ? "Wybór ręki" :
            _snapshot.ChoiceActive ? "Odkryj" :
            _snapshot.MyTurn ? "Możesz zagrywać" : "Ruch rywala";
        DashboardTurn.Text = _snapshot.InGame ? _snapshot.Turn.ToString() : "—";
        DashboardMana.Text = _snapshot.InGame ? _snapshot.Mana?.ToString() ?? "?" : "—";
        DashboardHandCount.Text = _snapshot.InGame ? _snapshot.HandCount.ToString() : "—";
        bool ready = _snapshot.InGame && _turnPlanTurn == _snapshot.Turn &&
            _scheduler.CompletedPlanTurn == _snapshot.Turn;
        PlanStateText.Text = ready ? "Plan gotowy" :
            _busy && !_snapshot.MulliganActive ? "Trwa analiza..." :
            _snapshot.MulliganActive ? "Wybór ręki" :
            _snapshot.MyTurn ? "Oczekuję na plan" :
            _snapshot.InGame ? "Obserwuję przeciwnika" : "Czekam na mecz";
        PlanStateDetail.Text = ready
            ? "Cała sekwencja jest widoczna. Nie zmieni się automatycznie podczas tej tury."
            : _snapshot.MyTurn
            ? "AI układa kolejność wszystkich możliwych do przewidzenia zagrań."
            : _snapshot.InGame
            ? "W następnej Twojej turze pojawi się jeden nowy plan."
            : "Plan zostanie przygotowany po rozpoczęciu Twojej tury.";
        AnalyzeButton.Content = ready ? "Przelicz ręcznie" : "Przygotuj plan";
    }

    private void UpdateVisibleHand(GameSnapshot state)
    {
        HandPanel.Visibility = state.InGame && !state.MulliganActive
            ? Visibility.Visible : Visibility.Collapsed;
        if (!state.InGame || state.MulliganActive)
        {
            _handSignature = null;
            _handRows.Clear();
            return;
        }
        var cards = VisualTurnPlan.BuildHand(state);
        HandPanelCount.Text = cards.Count + (cards.Count == 1 ? " karta" :
            cards.Count is >= 2 and <= 4 ? " karty" : " kart");
        // Only rebuild WPF elements when the hand itself changes, not when
        // HDT updates the board, opponent metadata or the turn history.
        var signature = string.Join("|", cards.Select(c =>
            c.Index + ":" + c.CardId + ":" + c.Name));
        if (_handSignature == signature) return;
        _handSignature = signature;
        _handRows.Clear();
        foreach (var card in cards)
        {
            var row = new CardVisualItem(card.Index, card.Name, card.CardId);
            _handRows.Add(row);
            _ = LoadCardArtAsync(row);
        }
    }

    private void UpdateChoiceVisuals(GameSnapshot state)
    {
        bool mulligan = state.InGame && state.MulliganActive;
        bool discover = state.InGame && state.ChoiceActive && !mulligan;
        MulliganPanel.Visibility = mulligan ? Visibility.Visible : Visibility.Collapsed;
        ChoicePanel.Visibility = discover ? Visibility.Visible : Visibility.Collapsed;

        if (!mulligan && !discover)
        {
            _shownChoiceKey = null;
            _latestChoiceKey = null;
            _choiceRows.Clear();
            _mulliganRows.Clear();
            return;
        }
        if (_shownChoiceKey == state.DecisionKey) return;
        _shownChoiceKey = state.DecisionKey;
        _latestChoiceKey = null;
        _choiceRows.Clear();
        _mulliganRows.Clear();
        ChoiceHeadline.Text = _scheduler.CompletedPlanTurn == state.Turn
            ? "Wybierz zgodnie z warunkiem w planie"
            : "Sprawdzam dostępne opcje...";
        foreach (var offered in VisualTurnPlan.BuildChoices(state, null))
        {
            var row = new CardVisualItem(offered.Index, offered.Name, offered.CardId);
            if (mulligan) _mulliganRows.Add(row);
            else _choiceRows.Add(row);
            _ = LoadCardArtAsync(row);
        }
    }

    private void ShowVisualMulligan(GameSnapshot state, AdviceCard advice)
    {
        UpdateChoiceVisuals(state);
        var decisions = VisualTurnPlan.ParseMulliganDecisions(advice.Move);
        foreach (var item in _mulliganRows)
            item.MulliganDecision = decisions.TryGetValue(item.Index, out var decision)
                ? decision : "BRAK DECYZJI";
        DiagnosticLog.Write("ui.mulligan.visualized", new
        {
            turn = state.Turn, cards = _mulliganRows.Count,
            accepted_decisions = _mulliganRows.Count(x => decisions.ContainsKey(x.Index)),
            decisions = decisions
        });
    }

    private void ShowVisualPlan(GameSnapshot state, AdviceCard advice)
    {
        var plan = VisualTurnPlan.BuildSteps(state, advice);
        _planRows.Clear();
        foreach (var step in plan)
        {
            var row = new CardVisualItem(step.Number, step.Instruction,
                step.CardId, step.Target is null ? "" : "CEL: " + step.Target);
            _planRows.Add(row);
            _ = LoadCardArtAsync(row);
        }
        _turnPlanTurn = plan.Count > 0 ? state.Turn : -1;
        bool hasPlan = plan.Count > 0;
        TurnPlanPanel.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        MoveText.Visibility = hasPlan ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticLog.Write("ui.plan.visualized", new {
            turn = state.Turn, steps = plan.Count,
            art_known = plan.Count(x => x.CardId is not null)
        });
    }

    private void ShowVisualChoice(GameSnapshot state, AdviceCard advice)
    {
        UpdateChoiceVisuals(state);
        var selected = VisualTurnPlan.TryChoiceNumber(advice.Move);
        foreach (var item in _choiceRows)
            item.Recommended = selected.HasValue && item.Index == selected.Value;
        if (state.ChoiceActive)
            ChoiceHeadline.Text = selected is int index ?
                "WYBIERZ " + index : "Wybierz wskazaną kartę";
        _latestChoiceKey = state.DecisionKey;
        DiagnosticLog.Write("ui.choice.visualized", new {
            turn = state.Turn, selection = selected,
            offers = _choiceRows.Count, plan_preserved = _turnPlanTurn == state.Turn
        });
    }

    private async Task LoadCardArtAsync(CardVisualItem item)
    {
        if (item.CardId is null) return;
        try
        {
            var image = await _cardImages.GetAsync(item.CardId);
            if (!_closing && image is not null)
                item.Art = image;
        }
        catch (Exception ex)
        {
            // Image failures MUST never interrupt card advice or plan updates.
            DiagnosticLog.Write("ui.card_art.missing", new {
                card_id = item.CardId, error = ex.GetType().Name
            });
        }
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
            AiStatus.Text = "Połącz konto i wybierz model w Ustawieniach";
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
            original.ChoiceActive ? "Sprawdzam wybór kart..." : "Układam plan na całą turę...";
        // During a refresh keep the existing suggestion on screen; do not clear
        // it when starting or failing another request.
        if (_shownSnapshot is null)
        {
            MoveText.Text = original.MulliganActive ? "Co zostawić?" :
                original.ChoiceActive ? "Wybieram najlepszą opcję..." : "Przygotowuję kolejność zagrań...";
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
        var tacticalContext = _intelligence.BuildContext(original, _deckStrategy.ForPrompt(original));
        var prompt = AdvicePrompt.Build(original, tacticalContext);
        if (_rejectedActionKey == original.DecisionKey &&
            !string.IsNullOrWhiteSpace(_rejectedActionReason))
            prompt += "\n\nKOREKTA WYMAGANA: Twoja poprzednia porada była sprzeczna z typem/strefą karty: " +
                _rejectedActionReason +
                "\nWybierz INNĄ legalną akcję. Nie sugeruj atakowania kartami z ręki ani wskazywania celu przy efekcie losowym.";
        if (!original.CardTypesSupported)
            DiagnosticLog.Write("hdt.card_types_missing", new { request = number,
                hint = "Zaktualizuj DLL HDT do v0.4, aby walidować typy kart." });
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
            // HDT briefly changes entity metadata during animations. The AI
            // response may be correct for a decision that comes back on the next
            // read. Wait briefly instead of losing the only useful answer.
            if (!SnapshotTransitionGuard.MatchesActionableDecision(original, _snapshot) &&
                _snapshot.InGame && _snapshot.Turn == original.Turn &&
                _snapshot.Phase == original.Phase)
            {
                DiagnosticLog.Write("ai.response.grace_recheck", new
                {
                    request = number, old_key = original.DecisionKey,
                    first_key = _snapshot.DecisionKey
                });
                await Task.Delay(TimeSpan.FromMilliseconds(1400));
                if (_closing) return;
                RefreshState();
            }
            if (SnapshotTransitionGuard.MatchesActionableDecision(original, _snapshot))
            {
                var card = AdviceCard.Parse(result, original.Phase);
                var legality = AdviceLegality.Validate(original, card);
                if (!legality.Allowed)
                {
                    _rejectedActionKey = original.DecisionKey;
                    _rejectedActionReason = legality.Reason;
                    _scheduler.RecordFailure(original);
                    DiagnosticLog.Write("ai.advice.rejected_illegal", new {
                        request = number, turn = original.Turn, decision_key = original.DecisionKey,
                        move = card.Move, target = card.Target, next = card.Next,
                        problem = legality.Reason
                    });
                    AiStatus.Text = "Zablokowano niemożliwą poradę";
                    MoveText.Text = "Sprawdzam zasady kart...";
                    AdviceMeta.Text = legality.Reason + " Ponowię analizę.";
                    TargetPanel.Visibility = Visibility.Collapsed;
                    NextPanel.Visibility = Visibility.Collapsed;
                    WhyPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                _rejectedActionKey = null;
                _rejectedActionReason = null;
                if (original.ChoiceActive)
                {
                    // A side choice never overwrites the previously accepted
                    // plan. It only highlights the selected option beside it.
                    ShowVisualChoice(original, card);
                    if (_turnPlanTurn != original.Turn)
                    {
                        MoveText.Visibility = Visibility.Visible;
                        MoveText.Text = card.Move;
                    }
                    AiStatus.Text = "Odkryj: wybierz wskazaną kartę";
                    AdviceMeta.Text = $"Wybór w turze {original.Turn} • {DateTime.Now:HH:mm:ss}";
                    DiagnosticLog.Write("ai.displayed.choice", new {
                        request = number, index = VisualTurnPlan.TryChoiceNumber(card.Move)
                    });
                    return;
                }
                if (original.MulliganActive)
                    ShowVisualMulligan(original, card);
                else
                {
                    ShowVisualPlan(original, card);
                    // Lock the completed plan only AFTER it passed validation
                    // and was actually rendered in the interface.
                    _scheduler.MarkTurnPlanned(original);
                    DiagnosticLog.Write("ai.turn_plan.locked", new {
                        turn = original.Turn, steps = _planRows.Count,
                        note = "no_auto_refresh_until_next_turn"
                    });
                }
                MoveText.FontSize = original.MulliganActive ? 20 : 28;
                MoveText.Text = card.Move;
                if (original.MulliganActive) MoveText.Visibility = Visibility.Collapsed;
                TargetText.Text = card.Target;
                TargetPanel.Visibility = _turnPlanTurn == original.Turn
                    ? Visibility.Collapsed :
                    card.Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                AdviceText.Text = card.Next;
                NextPanel.Visibility = _turnPlanTurn == original.Turn
                    ? Visibility.Collapsed :
                    card.Next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                WhyText.Text = card.Reason;
                WhyPanel.Visibility = card.Reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _shownSnapshot = original;
                _shownGuard.Reset();
                if (original.MulliganActive) ChoiceHeadline.Text = "ZOSTAW / ODRZUĆ";
                AiStatus.Text = original.MulliganActive ? "Zostaw / odrzuć karty" :
                    "Plan gotowy · bez automatycznych zmian";
                DiagnosticLog.Write("ai.displayed", new { request = number, decision_key = original.DecisionKey, turn = original.Turn });
                AdviceMeta.Text = $"Tura {original.Turn} • {DateTime.Now:HH:mm:ss} • {model.DisplayName}";
            }
            else
            {
                // An outdated result must ALWAYS re-arm the scheduler. The old
                // behavior marked the decision as analyzed forever, leaving the
                // visible "Czekam na aktualizację..." state with no retry.
                _scheduler.RecordFailure(original);
                AiStatus.Text = "Przygotowuję aktualną poradę";
                AdviceMeta.Text = _snapshot.Actionable
                    ? "Stan gry zmienił się podczas analizy. Nowa próba uruchomi się automatycznie."
                    : "Poczekam na kolejną Twoją turę.";
                if (_shownSnapshot is null)
                    MoveText.Text = _snapshot.Actionable ? "Ponawiam analizę..." : "Czekam na Twoją turę";
                DiagnosticLog.Write("ai.discarded.retry_armed", new
                {
                    request = number, cause = "state_changed",
                    old_key = original.DecisionKey, new_key = _snapshot.DecisionKey,
                    old_turn = original.Turn, new_turn = _snapshot.Turn,
                    phase = _snapshot.Phase, scheduler = _scheduler.LastDecision
                });
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
            "Automatyczna analiza włączona • jeden plan na początek każdej Twojej tury." :
            "Automatyczna analiza wyłączona • plan możesz przygotować przyciskiem ręcznym.";
    }
    private void TopmostChanged(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;
    private async Task CheckForAppUpdateOnStartupAsync()
    {
        try
        {
            var release = await AppUpdater.CheckAsync(CancellationToken.None);
            if (_closing) return;
            if (release.IsNewerThan(AppUpdater.InstalledVersion))
            {
                AppUpdateStatus.Text = "Dostępna nowa wersja: " + release.Version[..7] +
                    ". Kliknij, aby pobrać i uruchomić aktualizację.";
                UpdateAppButton.Content = "Zainstaluj aktualizację ↻";
            }
            else
                AppUpdateStatus.Text = "Masz aktualną wersję aplikacji.";
        }
        catch (Exception ex)
        {
            if (_closing) return;
            AppUpdateStatus.Text = "Błąd sprawdzania aktualizacji: " + ex.Message +
                ". Szczegóły znajdziesz w logach aplikacji.";
            DiagnosticLog.Write("app.update.check_failed", new {
                type = ex.GetType().Name, error = ex.Message
            });
        }
    }

    private async void UpdateAppClick(object sender, RoutedEventArgs e)
    {
        if (_appUpdateBusy) return;
        RefreshState();
        if (_snapshot.InGame)
        {
            MessageBox.Show(this, "Zakończ mecz przed aktualizacją aplikacji.",
                "Aktualizacja aplikacji", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _appUpdateBusy = true;
        UpdateAppButton.IsEnabled = false;
        UpdatePluginButton.IsEnabled = false;
        try
        {
            AppUpdateStatus.Text = "Sprawdzanie najnowszego wydania...";
            var release = await AppUpdater.CheckAsync(CancellationToken.None);
            if (_closing) return;
            if (!release.IsNewerThan(AppUpdater.InstalledVersion))
            {
                AppUpdateStatus.Text = "Masz już najnowszą wersję.";
                UpdateAppButton.Content = "Sprawdź aktualizacje ↻";
                return;
            }
            var reply = MessageBox.Show(this,
                "Dostępna jest aktualizacja " + release.Version[..7] +
                ". Aplikacja pobierze ją, sprawdzi SHA-256, zamknie się, " +
                "zachowa kopię starych plików i uruchomi się ponownie. " +
                "Historia meczów i ustawienia pozostaną na komputerze. Kontynuować?",
                "Aktualizuj Hearthstone AI Coach",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reply != MessageBoxResult.Yes)
            {
                AppUpdateStatus.Text = "Aktualizacja została anulowana.";
                return;
            }

            DiagnosticLog.Write("app.update.begin", new { current = AppUpdater.InstalledVersion,
                target = release.Version });
            await AppUpdater.PrepareAndStartAsync(release,
                message => Dispatcher.Invoke(() => AppUpdateStatus.Text = message),
                () =>
                {
                    RefreshState();
                    return !_closing && !_snapshot.InGame;
                },
                CancellationToken.None);
            if (_closing) return;
            DiagnosticLog.Write("app.update.restart_requested", new { target = release.Version });
            Close();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            DiagnosticLog.Error("app.update.failure", ex);
            AppUpdateStatus.Text = "Aktualizacja nieudana: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Aktualizacja aplikacji",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _appUpdateBusy = false;
            UpdateAppButton.IsEnabled = true;
            UpdatePluginButton.IsEnabled = true;
        }
    }

    private async void UpdatePluginClick(object sender, RoutedEventArgs e)
    {
        RefreshState();
        if (_snapshot.InGame)
        {
            MessageBox.Show(this, "Poczekaj na zakończenie partii, zanim zaktualizujesz HDT.",
                "Aktualizacja wtyczki", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            "Aplikacja pobierze wtyczkę ze sprawdzonego wydania GitHub, " +
            "poprosi HDT o zamknięcie, zachowa kopię starej DLL i ponownie uruchomi tracker. " +
            "Nie będzie wymuszać zamknięcia HDT. Kontynuować?",
            "Aktualizuj wtyczkę HDT", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        UpdatePluginButton.IsEnabled = false;
        _timer.Stop();
        _cancelReason = "plugin_update";
        _requestCts?.Cancel();
        void Report(string message) =>
            Dispatcher.Invoke(() => PluginUpdateStatus.Text = message);
        try
        {
            DiagnosticLog.Write("plugin.update.begin");
            var updated = await HdtPluginUpdater.UpdateAsync(Report, CancellationToken.None);
            var versionLabel = updated.Source == "bundled-offline"
                ? " (wersja dołączona do aplikacji — bez internetu)"
                : " (wydanie GitHub)";
            var status = updated.Changed
                ? "DLL zaktualizowana" + versionLabel + ". " + (updated.Restarted ?
                    "HDT został uruchomiony ponownie." : "Uruchom HDT, aby użyć nowej DLL.")
                : "Zainstalowana DLL jest identyczna z dostępną" + versionLabel + ".";
            PluginUpdateStatus.Text = status;
            DiagnosticLog.Write("plugin.update.success", new
            {
                updated.Changed, updated.Destination, updated.Restarted, updated.Source
            });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("plugin.update.failure", ex);
            PluginUpdateStatus.Text = "Aktualizacja nieudana: " + ex.Message;
            MessageBox.Show(this, ex.Message + "\n\nSprawdź logi diagnostyczne aplikacji.",
                "Aktualizacja HDT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdatePluginButton.IsEnabled = true;
            _timer.Start();
            RefreshState();
        }
    }

    private void RefreshHistory()
    {
        if (_shownHistoryRevision == _history.Revision) return;
        var previouslySelected = (HistoryMatchList.SelectedItem as HistoryMatchEntry)?.Id;
        _shownHistoryRevision = _history.Revision;
        _historyRows.Clear();
        foreach (var match in _history.Matches)
            _historyRows.Add(new HistoryMatchEntry(match));
        HistoryEmptyText.Visibility = _historyRows.Count == 0 ?
            Visibility.Visible : Visibility.Collapsed;
        HistoryMatchList.SelectedItem = _historyRows.FirstOrDefault(m => m.Id == previouslySelected)
            ?? _historyRows.FirstOrDefault();
        // ListBox selection can remain unchanged when an item was rebuilt.
        ShowSelectedHistory();
    }

    private void HistorySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ShowSelectedHistory();

    private void ShowSelectedHistory()
    {
        if (HistoryMatchList.SelectedItem is not HistoryMatchEntry selected)
        {
            HistoryDetailPanel.Visibility = Visibility.Collapsed;
            HistoryNoSelectionText.Visibility = Visibility.Visible;
            return;
        }
        var match = selected.Match;
        HistoryDetailPanel.Visibility = Visibility.Visible;
        HistoryNoSelectionText.Visibility = Visibility.Collapsed;
        HistoryTitle.Text = match.PlayerClass + "  vs  " + match.OpponentClass;
        HistoryStats.Text = match.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy, HH:mm") +
            "  •  " + (match.FinishedUtc.HasValue ? "Mecz zakończony" : "Mecz w toku") +
            "  •  " + match.Plays.Count + " zagranych kart";
        HistoryCoverageText.Text = match.Partial
            ? "Zapis częściowy: aplikacja dołączyła w trakcie meczu. Brakujących ruchów nie odtwarzamy."
            : "Zapis obejmuje tylko widoczne zagrania wykryte przez HDT podczas pracy aplikacji.";
        var turns = match.Plays.GroupBy(play => play.Turn).OrderBy(g => g.Key)
            .Select(g => new HistoryTurnEntry(g)).ToArray();
        HistoryNoEventsText.Visibility = turns.Length == 0 ?
            Visibility.Visible : Visibility.Collapsed;
        HistoryTurnList.ItemsSource = turns;
        foreach (var turn in turns)
        {
            foreach (var card in turn.Mine.Concat(turn.Theirs))
                _ = LoadHistoryArtAsync(card);
        }
    }

    private async Task LoadHistoryArtAsync(HistoryCardEntry item)
    {
        if (item.CardId is null) return;
        try
        {
            var art = await _cardImages.GetAsync(item.CardId);
            if (!_closing && art is not null)
                item.Art = art;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("ui.history.card_art.failure", new {
                card_id = item.CardId, error = ex.GetType().Name
            });
        }
    }

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
    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    private void WindowStateChanged(object sender, EventArgs e)
    {
        if (MaximizeButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Przywróć rozmiar" : "Maksymalizuj";
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }
}
