using System.IO;
using System.Text;
using System.Text.Json;

namespace Coach.App;

/// <summary>
/// Local troubleshooting log. Do NOT pass auth tokens, authorization headers,
/// cookies, refresh tokens or ID tokens to this logger.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HearthstoneCoach", "logs");
    private static readonly string Session = Guid.NewGuid().ToString("N")[..10];
    private static readonly string FilePath = Path.Combine(DirectoryPath, "coach-" +
        DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Session + ".jsonl");
    private static DateTimeOffset _lastCleanup;
    private const long MaxFileBytes = 12 * 1024 * 1024;

    public static void Write(string eventType, object? details = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var record = JsonSerializer.Serialize(new
                {
                    at_utc = DateTimeOffset.UtcNow.ToString("O"),
                    session = Session,
                    @event = eventType,
                    details
                }, JsonOptions);
                string path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(record) > MaxFileBytes)
                    path = Path.Combine(DirectoryPath, "coach-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") +
                        "-" + Session + "-part.jsonl");
                File.AppendAllText(path, record + Environment.NewLine, new UTF8Encoding(false));
                if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromMinutes(10))
                {
                    _lastCleanup = DateTimeOffset.UtcNow;
                    foreach (var item in Directory.EnumerateFiles(DirectoryPath, "coach-*.jsonl"))
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(item) < DateTime.UtcNow.AddDays(-7))
                                File.Delete(item);
                        }
                        catch (IOException) { }
                    }
                }
            }
        }
        catch (Exception) { /* Diagnostics cannot crash a match. */ }
    }

    public static void Error(string eventType, Exception exception) =>
        Write(eventType, new { exception = exception.GetType().FullName,
            message = exception.Message, stack_trace = exception.StackTrace });
}
