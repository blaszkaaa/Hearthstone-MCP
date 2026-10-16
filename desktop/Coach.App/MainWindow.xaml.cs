using System.Collections.ObjectModel;
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
    private readonly MatchHistoryStore _history = new();
    private readonly ObservableCollection<HistoryMatchEntry> _historyRows = new();
    private int _shownHistoryRevision = -1;
    private readonly CardArtCache _cardImages = new();
    private readonly ObservableCollection<CardVisualItem> _planRows = new();
    private readonly ObservableCollection<CardVisualItem> _choiceRows = new();
    private readonly ObservableCollection<CardVisualItem> _mulliganRows = new();
    private int _turnPlanTurn = -1;
    private string? _shownChoiceKey;
    private string? _latestChoiceKey;
    private readonly HdtStateReconciler _stateReconciler = new();
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
    private string? _rejectedActionKey;
    private string? _rejectedActionReason;


    public MainWindow()
    {
        InitializeComponent();
        PlanItems.ItemsSource = _planRows;
        ChoiceItems.ItemsSource = _choiceRows;
        MulliganItems.ItemsSource = _mulliganRows;
        HistoryMatchList.ItemsSource = _historyRows;
        _inference = new ChatGptInference(_auth);
        _timer.Tick += Poll;
        UpdateAccount();
        DiagnosticLog.Write("app.start", new { version = "1.0", state_path = GameSnapshot.DefaultPath, log_dir = DiagnosticLog.DirectoryPath });
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
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        RefreshState();
        _history.Observe(_snapshot, DateTimeOffset.UtcNow);
        RefreshHistory();
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
    }

    private void Poll(object? sender, EventArgs e)
    {
        RefreshState();
        var now = DateTimeOffset.UtcNow;
        _intelligence.Observe(_snapshot, now);
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
        _turnPlanTurn = -1;
        _planRows.Clear();
        TurnPlanPanel.Visibility = Visibility.Collapsed;
        MoveText.Visibility = Visibility.Visible;
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
        ChoiceHeadline.Text = "Wybieram najlepszą kartę...";
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
                    ShowVisualPlan(original, card);
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
                    original.ChoiceActive ? "Wybór Odkryj / kart" : "Gotowa rekomendacja";
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
            "Automatyczna analiza włączona • szybki wybór kart i ponawianie przy zmianach." :
            "Automatyczna analiza wyłączona • możesz kliknąć „Analizuj teraz”.";
    }
    private void TopmostChanged(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;
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
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
