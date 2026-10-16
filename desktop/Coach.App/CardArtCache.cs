using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Coach.Core;

namespace Coach.App;

/// <summary>
/// Lazy card images from HearthstoneJSON, keyed strictly by HDT CardId.
/// No image download runs on the UI thread. Bound the image cache to 160 MiB.
/// </summary>
internal sealed class CardArtCache
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private readonly SemaphoreSlim _downloads = new(3);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _inFlight = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HearthstoneCoach", "card-images");
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const long MaxCacheBytes = 160L * 1024 * 1024;

    public Task<ImageSource?> GetAsync(string? cardId)
    {
        if (!VisualTurnPlan.SafeCardId(cardId)) return Task.FromResult<ImageSource?>(null);
        return _inFlight.GetOrAdd(cardId!, FetchAsync);
    }

    private async Task<ImageSource?> FetchAsync(string cardId)
    {
        await _downloads.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string cached = Path.Combine(DirectoryPath, cardId + ".png");
            if (File.Exists(cached))
            {
                try { return ReadFrozen(File.ReadAllBytes(cached)); }
                catch (Exception ex) when (ex is IOException or NotSupportedException)
                {
                    try { File.Delete(cached); } catch (IOException) { }
                }
            }
            // Polish card render first, English only when Polish is unavailable.
            foreach (var locale in new[] { "plPL", "enUS" })
            {
                try
                {
                    var url = "https://art.hearthstonejson.com/v1/render/latest/" +
                        locale + "/256x/" + cardId + ".png";
                    using var response = await Client.GetAsync(url,
                        HttpCompletionOption.ResponseHeadersRead);
                    if (response.StatusCode == HttpStatusCode.NotFound) continue;
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > MaxImageBytes) continue;
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0 || bytes.Length > MaxImageBytes) continue;
                    var image = ReadFrozen(bytes);
                    string temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temp, bytes);
                        File.Move(temp, cached, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
                    }
                    PruneCache();
                    return image;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                    or IOException or NotSupportedException or InvalidOperationException)
                {
                    // Missing image or offline mode: never block or crash the coach.
                    continue;
                }
            }
            return null;
        }
        finally
        {
            _downloads.Release();
            _inFlight.TryRemove(cardId, out _);
        }
    }

    private static ImageSource ReadFrozen(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 360;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void PruneCache()
    {
        try
        {
            var files = new DirectoryInfo(DirectoryPath).GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long total = 0;
            foreach (var file in files)
            {
                total += file.Length;
                if (total <= MaxCacheBytes) continue;
                try { file.Delete(); } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
