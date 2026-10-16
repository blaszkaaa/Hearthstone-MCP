using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coach.Core;

/// <summary>Validated descriptor for the rolling, Windows x64 desktop release.</summary>
public sealed class AppReleaseManifest
{
    public const string PackageName = "HearthstoneCoach-win-x64.zip";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("asset")]
    public string Asset { get; init; } = "";

    public static AppReleaseManifest Parse(string json)
    {
        AppReleaseManifest? value;
        try { value = JsonSerializer.Deserialize<AppReleaseManifest>(json); }
        catch (JsonException ex) { throw new InvalidDataException("Nieprawidłowy manifest aktualizacji.", ex); }
        if (value is null ||
            value.Version.Length != 40 || !value.Version.All(Uri.IsHexDigit) ||
            value.Sha256.Length != 64 || !value.Sha256.All(Uri.IsHexDigit) ||
            !string.Equals(value.Asset, PackageName, StringComparison.Ordinal))
            throw new InvalidDataException("Manifest aktualizacji zawiera nieprawidłową wersję, plik lub sumę SHA-256.");
        return value;
    }

    public bool IsNewerThan(string installedVersion) =>
        !string.Equals(Version, installedVersion, StringComparison.OrdinalIgnoreCase);
}
