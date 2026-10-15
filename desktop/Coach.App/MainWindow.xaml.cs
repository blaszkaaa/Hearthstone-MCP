using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
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

        if (!_snapshot.InGame || (!_snapshot.MyTurn && !_snapshot.ChoiceActive))
        {
            if (_lastShownFingerprint is not null)
            {
                AdviceText.Text = "Czekam na Twoją turę lub wybór kart...";
                AdviceMeta.Text = "W poprzedniej turze analiza mogła już stracić aktualność.";
                _lastShownFingerprint = null;
            }
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
        ConnectionText.Text = _snapshot.Connected ? "●  Połączono z HDT" : "○  Czekam na HDT";
        ConnectionText.Foreground = _snapshot.Connected ?
            (System.Windows.Media.Brush)FindResource("Accent") :
            (System.Windows.Media.Brush)FindResource("Muted");
        GamePhaseText.Text = _snapshot.Status;
        TurnText.Text = _snapshot.InGame ? _snapshot.Turn.ToString() : "—";
        ManaText.Text = _snapshot.InGame ? _snapshot.Mana?.ToString() ?? "?" : "—";
        HandText.Text = _snapshot.InGame ? $"Ręka: {_snapshot.HandCount} kart" : "Ręka: —";
        ChoiceText.Text = !_snapshot.ChoiceSupported && _snapshot.InGame ? "Wybory: zaktualizuj DLL HDT" :
            _snapshot.ChoiceActive ? $"Wybory: {_snapshot.ChoicesCount} opcji" :
            "Wybory: brak aktywnego";
    }

    private async void SignInClick(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        AiStatus.Text = "Otwieram logowanie ChatGPT...";
        try
        {
            await _auth.SignInAsync();
            UpdateAccount();
            AiStatus.Text = "Połączono z ChatGPT";
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
        AccountText.Text = _auth.SignedIn ? _auth.Email + "\nUżywasz planu ChatGPT" : "Nie połączono";
        SignInButton.Content = _auth.SignedIn ? "Zaloguj ponownie" : "Połącz przez ChatGPT";
        SignOutButton.Visibility = _auth.SignedIn ? Visibility.Visible : Visibility.Collapsed;
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
        AiStatus.Text = original.ChoiceActive ? "Analizuję dostępne opcje..." : "Analizuję Twoją turę...";
        AdviceMeta.Text = $"Model: {model.DisplayName} • Prośba #{number} • Wysyłane są tylko dane aktualnej partii.";
        try
        {
            var result = await _inference.AnalyzeAsync(original, model.Slug, myRequest.Token);
            RefreshState();
            if (_snapshot.InGame && _snapshot.Fingerprint == original.Fingerprint &&
                (_snapshot.MyTurn || _snapshot.ChoiceActive))
            {
                AiStatus.Text = original.ChoiceActive ? "Wybierz wskazaną kartę" : "Proponowany ruch";
                AdviceText.Text = result;
                _lastShownFingerprint = original.Fingerprint;
                AdviceMeta.Text = "Aktualizacja: " + DateTime.Now.ToString("HH:mm:ss") +
                    " • Tura " + original.Turn + " • Model: " + model.DisplayName;
            }
            else { AiStatus.Text = "Plansza się zmieniła"; AdviceMeta.Text = "Pominięto nieaktualną odpowiedź AI."; }
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
