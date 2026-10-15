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
    private string? _lastShownFingerprint;

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
        var previous = _snapshot;
        RefreshState();

        // Superseded game states cannot produce advice for the next situation.
        if (_busy && (!(_snapshot.MyTurn || _snapshot.ChoiceActive) ||
            !_snapshot.InGame || previous.Fingerprint != _snapshot.Fingerprint))
            _requestCts?.Cancel();

        // Do not leave an old recommendation on screen after the board changes.
        if (_lastShownFingerprint is not null &&
            (!_snapshot.InGame || (!_snapshot.MyTurn && !_snapshot.ChoiceActive) ||
             _snapshot.Fingerprint != _lastShownFingerprint))
        {
            MoveText.Text = _snapshot.InGame && _snapshot.ChoiceActive
                ? "Nowy wybór kart" : _snapshot.InGame && _snapshot.MyTurn
                    ? "Sytuacja się zmieniła" : "Czekam na Twoją turę";
            AdviceText.Text = "Poprzednia wskazówka jest już nieaktualna. Czekam na aktualną analizę.";
            AdviceMeta.Text = "Rekomendacje odświeżają się po zmianach w grze.";
            _lastShownFingerprint = null;
        }

        if (_auth.SignedIn && ModelPicker.SelectedItem is ChatGptInference.ModelChoice model &&
            _scheduler.ShouldAnalyze(_snapshot, DateTimeOffset.UtcNow, _busy, AutoCheck.IsChecked == true))
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
        AiStatus.Text = original.ChoiceActive ? "Analizuję wybór kart..." : "Analizuję Twoją turę...";
        if (_lastShownFingerprint != original.Fingerprint)
        {
            MoveText.Text = original.ChoiceActive ? "Którą kartę wybrać?" : "Szukam najlepszego ruchu...";
            AdviceText.Text = "Sprawdzam dostępne zagrania i aktualną sytuację na planszy.";
        }
        AdviceMeta.Text = $"Model: {model.DisplayName} • Analiza #{number} • Dane aktualnej partii.";
        try
        {
            var result = await _inference.AnalyzeAsync(original, model.Slug, myRequest.Token);
            RefreshState();
            if (_snapshot.InGame && _snapshot.Fingerprint == original.Fingerprint &&
                (_snapshot.MyTurn || _snapshot.ChoiceActive))
            {
                AiStatus.Text = original.ChoiceActive ? "Wybierz wskazaną kartę" : "Gotowa rekomendacja";
                var (move, details) = FormatAdvice(result);
                MoveText.Text = move;
                AdviceText.Text = details;
                _lastShownFingerprint = original.Fingerprint;
                AdviceMeta.Text = "Aktualizacja: " + DateTime.Now.ToString("HH:mm:ss") +
                    " • Tura " + original.Turn + " • Model: " + model.DisplayName;
            }
            else
            {
                AiStatus.Text = "Plansza się zmieniła";
                MoveText.Text = "Oczekuję na aktualny stan";
                AdviceText.Text = "Nowe zagrania lub wybory wymagają ponownej analizy.";
                AdviceMeta.Text = "Pominięto nieaktualną odpowiedź AI.";
            }
        }
        catch (OperationCanceledException)
        {
            AiStatus.Text = "Nowy stan rozgrywki";
            AdviceMeta.Text = "Poprzednia analiza została anulowana.";
        }
        catch (Exception ex)
        {
            AiStatus.Text = "Analiza niedostępna";
            AdviceMeta.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            AnalyzeButton.IsEnabled = true;
            if (ReferenceEquals(_requestCts, myRequest)) _requestCts = null;
            myRequest.Dispose();
        }
    }

    private static (string Move, string Details) FormatAdvice(string raw)
    {
        var lines = raw.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0) return ("Brak rekomendacji", "Model nie zwrócił opisu ruchu.");

        // The AI prompt requests RUCH / KOLEJNOŚĆ / DLACZEGO / UWAGA.
        // Keep fallback for models that answer conversationally.
        var moveLine = lines.FirstOrDefault(line =>
            line.StartsWith("RUCH:", StringComparison.OrdinalIgnoreCase));
        if (moveLine is not null)
        {
            var move = moveLine["RUCH:".Length..].Trim();
            var details = lines.Where(line => !ReferenceEquals(line, moveLine)).ToArray();
            return (move.Length == 0 ? "Proponowany ruch" : move,
                details.Length == 0 ? "Brak dodatkowego uzasadnienia." : string.Join("\n\n", details));
        }
        return (lines[0], lines.Length == 1 ? "Brak dodatkowego uzasadnienia." :
            string.Join("\n\n", lines.Skip(1)));
    }

    private void AutoChanged(object sender, RoutedEventArgs e)
    {
        AdviceMeta.Text = AutoCheck.IsChecked == true ?
            "Automatyczna analiza włączona • co najwyżej 6 zapytań na turę, minimum 12 s odstępu." :
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
