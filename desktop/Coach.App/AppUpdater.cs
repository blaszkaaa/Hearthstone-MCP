using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using Coach.Core;

namespace Coach.App;

/// <summary>
/// Updates the desktop EXE itself (independent of the HDT plugin updater).
/// Downloads and validates a rolling GitHub Release, stages it, then launches
/// an EMBEDDED out-of-process updater so no running executable is overwritten.
/// </summary>
internal static class AppUpdater
{
    private const string ReleaseBase =
        "https://github.com/blaszkaaa/Hearthstone-MCP/releases/download/app-latest/";
    private const long MaxZipBytes = 350L * 1024 * 1024;
    private const long MaxExtractedBytes = 900L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static string InstalledVersion =>
        typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "dev";

    public static async Task<AppReleaseManifest> CheckAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            ReleaseBase + "HearthstoneCoach-update.json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 32_768)
            throw new InvalidDataException("Manifest aktualizacji jest zbyt duży.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(input);
        var buffer = new char[32_769];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), deadline.Token);
        if (length > 32_768 || await reader.ReadAsync(buffer.AsMemory(0, 1), deadline.Token) != 0)
            throw new InvalidDataException("Manifest aktualizacji jest zbyt duży.");
        return AppReleaseManifest.Parse(new string(buffer, 0, length));
    }

    public static async Task PrepareAndStartAsync(
        AppReleaseManifest manifest, Action<string> progress, Func<bool> canRestart, CancellationToken ct)
    {
        var applicationDirectory = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(applicationDirectory, "HearthstoneCoach.exe")))
            throw new FileNotFoundException("Nie znaleziono zainstalowanego HearthstoneCoach.exe.");
        // Fail early for read-only or system-protected directories.
        var writeProbe = Path.Combine(applicationDirectory, ".coach-write-test-" +
            Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(writeProbe, "ok"); }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Nie można aktualizować aplikacji w folderze bez uprawnień zapisu. " +
                "Przenieś ją do folderu użytkownika.", ex);
        }
        finally { if (File.Exists(writeProbe)) File.Delete(writeProbe); }

        var updateDirectory = Path.Combine(Path.GetTempPath(), "HearthstoneCoach-update-" +
            Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(updateDirectory, AppReleaseManifest.PackageName);
        var stageDir = Path.Combine(updateDirectory, "staged");
        var scriptPath = Path.Combine(updateDirectory, "apply-update.ps1");
        Directory.CreateDirectory(updateDirectory);
        bool started = false;
        try
        {
            progress("Pobieranie aktualnej wersji aplikacji...");
            using (var response = await Http.GetAsync(ReleaseBase + AppReleaseManifest.PackageName,
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxZipBytes)
                    throw new InvalidDataException("Paczka aktualizacji jest zbyt duża.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(zipPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 65536, useAsync: true);
                byte[] buffer = new byte[65536];
                long bytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) != 0)
                {
                    bytes += read;
                    if (bytes > MaxZipBytes)
                        throw new InvalidDataException("Paczka aktualizacji jest zbyt duża.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            progress("Weryfikacja sumy SHA-256...");
            await using (var downloaded = File.OpenRead(zipPath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, ct));
                if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Paczka nie zgadza się z manifestem SHA-256. Nie instaluję jej.");
            }

            progress("Przygotowanie nowej wersji...");
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                if (zip.Entries.Count is < 1 or > 120 ||
                    zip.Entries.Sum(e => e.Length) > MaxExtractedBytes ||
                    zip.Entries.Any(e => e.Length > MaxExtractedBytes ||
                        e.FullName.Contains('/') || e.FullName.Contains('\\') ||
                        e.FullName is "." or ".." ||
                        string.IsNullOrWhiteSpace(e.Name)) ||
                    !zip.Entries.Any(e =>
                        string.Equals(e.FullName, "HearthstoneCoach.exe", StringComparison.Ordinal)))
                    throw new InvalidDataException("Nieprawidłowa struktura archiwum aktualizacji.");
                Directory.CreateDirectory(stageDir);
                zip.ExtractToDirectory(stageDir);
            }

            await using (var embedded = typeof(AppUpdater).Assembly.GetManifestResourceStream(
                "Coach.App.UpdaterScript") ??
                throw new InvalidDataException("Brakuje wbudowanego modułu aktualizacji."))
            await using (var output = File.Create(scriptPath))
                await embedded.CopyToAsync(output, ct);

            if (!canRestart())
                throw new InvalidOperationException(
                    "Podczas pobierania rozpoczął się mecz. Instalacja nie zostanie uruchomiona.");
            progress("Uruchamiam instalator. Aplikacja zostanie zamknięta i otwarta ponownie...");
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = applicationDirectory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-AppPid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("-InstallDir");
            start.ArgumentList.Add(applicationDirectory);
            start.ArgumentList.Add("-StageDir");
            start.ArgumentList.Add(stageDir);
            start.ArgumentList.Add("-UpdateDir");
            start.ArgumentList.Add(updateDirectory);
            using var updater = Process.Start(start) ??
                throw new IOException("Nie można uruchomić instalatora aktualizacji.");
            started = true;
        }
        finally
        {
            // After launching the helper it owns the staged files, even if the
            // app crashes while shutting down. Never delete them prematurely.
            if (!started)
            {
                try { Directory.Delete(updateDirectory, recursive: true); }
                catch (IOException) { }
            }
        }
    }
}
