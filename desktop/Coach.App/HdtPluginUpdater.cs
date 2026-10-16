using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Coach.Core;

namespace Coach.App;

/// <summary>
/// One-click update ONLY of this repository's HDT plugin, never the Hearthstone game.
/// Downloads a release DLL + published SHA256 before requesting a graceful HDT exit.
/// </summary>
internal static class HdtPluginUpdater
{
    private const string ReleaseBase =
        "https://github.com/blaszkaaa/Hearthstone-MCP/releases/download/plugin-latest/";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    public sealed record Result(bool Changed, string Destination, bool Restarted);

    public static async Task<Result> UpdateAsync(Action<string> progress, CancellationToken ct)
    {
        string folder = Path.Combine(Path.GetTempPath(), "HearthstoneCoach-update-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string tempDll = Path.Combine(folder, "HearthstoneMcpPlugin.dll");
        try
        {
            progress("Pobieram oficjalną DLL i sumę kontrolną...");
            string sha = (await Http.GetStringAsync(ReleaseBase +
                "HearthstoneMcpPlugin.dll.sha256", ct)).Trim().Split(' ', '\t', '\r', '\n')[0];
            if (!PluginFileSwap.ValidDigest(sha))
                throw new InvalidDataException("Wydanie GitHub ma nieprawidłową sumę kontrolną.");

            using (var response = await Http.GetAsync(ReleaseBase + "HearthstoneMcpPlugin.dll",
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 20_000_000)
                    throw new InvalidDataException("Plik DLL jest zbyt duży.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(tempDll, FileMode.CreateNew, FileAccess.Write);
                var bytes = new byte[65536];
                long count = 0;
                int n;
                while ((n = await stream.ReadAsync(bytes, ct)) != 0)
                {
                    count += n;
                    if (count > 20_000_000) throw new InvalidDataException("Plik DLL jest zbyt duży.");
                    await file.WriteAsync(bytes.AsMemory(0, n), ct);
                }
            }
            if (!string.Equals(PluginFileSwap.Sha256Of(tempDll), sha,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Suma SHA-256 DLL nie zgadza się z wydaniem.");
            var assembly = AssemblyName.GetAssemblyName(tempDll);
            if (!string.Equals(assembly.Name, "HearthstoneMcpPlugin", StringComparison.Ordinal))
                throw new InvalidDataException("Pobrano nieprawidłową bibliotekę DLL.");

            string destination = ResolveInstalledPluginPath();
            if (File.Exists(destination) && string.Equals(
                PluginFileSwap.Sha256Of(destination), sha, StringComparison.OrdinalIgnoreCase))
            {
                progress("Wtyczka jest już aktualna. Nie trzeba zamykać HDT.");
                return new Result(false, destination, false);
            }

            var processes = Process.GetProcesses()
                .Where(p => string.Equals(p.ProcessName, "Hearthstone Deck Tracker",
                    StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.ProcessName, "HearthstoneDeckTracker",
                    StringComparison.OrdinalIgnoreCase)).ToList();
            string? restartExe = null;
            bool wasRunning = processes.Count > 0;
            bool trackerStopped = false;
            try
            {
                if (wasRunning)
                {
                    // Determine the original executable BEFORE shutting it down.
                    // If it cannot be determined, abort safely and leave HDT running.
                    restartExe = processes.Select(p =>
                    {
                        try { return p.MainModule?.FileName; }
                        catch { return null; }
                    }).FirstOrDefault(File.Exists);
                    if (restartExe is null)
                        throw new IOException("Nie znam ścieżki HDT. Nie zamykam go, bo nie mógłbym uruchomić ponownie.");

                    progress("Proszę HDT o bezpieczne zamknięcie...");
                    foreach (var process in processes)
                    {
                        if (process.HasExited) continue;
                        if (!process.CloseMainWindow())
                            throw new IOException("HDT nie przyjął polecenia zamknięcia. Zamknij go ręcznie i spróbuj ponownie.");
                    }
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        await Task.WhenAll(processes.Select(async p =>
                        {
                            if (!p.HasExited) await p.WaitForExitAsync(wait.Token);
                        }));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("HDT nie zamknął się w ciągu 20 sekund. Nie wymuszam zamknięcia.");
                    }
                    trackerStopped = true;
                }

                progress("Podmieniam DLL i weryfikuję zapis...");
                bool changed = PluginFileSwap.Install(tempDll, destination, sha);
                progress("Wtyczka zaktualizowana.");
                return new Result(changed, destination, wasRunning);
            }
            finally
            {
                // Restart only if we successfully stopped HDT. Never restart the
                // game client or other applications, even on installation failure.
                if (wasRunning && trackerStopped && restartExe is not null)
                {
                    progress("Uruchamiam ponownie Hearthstone Deck Tracker...");
                    Process.Start(new ProcessStartInfo(restartExe) {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(restartExe) ?? ""
                    });
                }
                foreach (var p in processes) p.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    public static string ResolveInstalledPluginPath()
    {
        var dataRoots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        var hdtRoots = dataRoots.SelectMany(root => new[] {
            Path.Combine(root, "HearthstoneDeckTracker"),
            Path.Combine(root, "Hearthstone Deck Tracker")
        }).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = new List<string>();
        foreach (var root in hdtRoots)
        {
            var pluginFolder = Path.Combine(root, "Plugins");
            if (!Directory.Exists(pluginFolder)) continue;
            var direct = Path.Combine(pluginFolder, "HearthstoneMcpPlugin.dll");
            if (File.Exists(direct)) existing.Add(direct);
            foreach (var sub in Directory.EnumerateDirectories(pluginFolder))
            {
                var candidate = Path.Combine(sub, "HearthstoneMcpPlugin.dll");
                if (File.Exists(candidate)) existing.Add(candidate);
            }
        }
        if (existing.Count > 1)
            throw new IOException("Znaleziono kilka kopii DLL HDT. Pozostaw jedną aktywną wtyczkę i spróbuj ponownie.");
        if (existing.Count == 1) return existing[0];
        if (hdtRoots.Length != 1)
            throw new DirectoryNotFoundException("Nie znaleziono jednoznacznego folderu HDT w AppData. Uruchom tracker co najmniej raz.");
        return Path.Combine(hdtRoots[0], "Plugins", "HearthstoneMcpPlugin.dll");
    }
}
