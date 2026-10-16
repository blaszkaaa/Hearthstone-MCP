using System.Security.Cryptography;

namespace Coach.Core;

/// <summary>
/// Installs a verified DLL after HDT has exited. Replacement is staged on the
/// same volume and uses File.Replace to keep a one-step rollback file.
/// Does not shut down processes or touch the Hearthstone game.
/// </summary>
public static class PluginFileSwap
{
    public static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static bool ValidDigest(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    public static bool Install(string downloadedDll, string destinationDll, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedDll);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDll);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        var expected = expectedSha256.Trim().ToUpperInvariant();
        if (!ValidDigest(expected))
            throw new InvalidDataException("Nieprawidłowa suma SHA-256 wydania.");
        if (!File.Exists(downloadedDll) ||
            !string.Equals(Sha256Of(downloadedDll), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Pobrana DLL ma niezgodny skrót SHA-256.");

        string? folder = Path.GetDirectoryName(Path.GetFullPath(destinationDll));
        if (string.IsNullOrWhiteSpace(folder))
            throw new IOException("Nie udało się określić folderu wtyczek HDT.");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "HearthstoneMcpPlugin.dll");
        if (!string.Equals(Path.GetFullPath(destinationDll), destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nie można zastąpić innego pliku niż HearthstoneMcpPlugin.dll.");
        string staged = Path.Combine(folder, ".HearthstoneMcpPlugin-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = Path.Combine(folder, "HearthstoneMcpPlugin.dll.bak");

        // No-op if the installed DLL is already identical.
        if (File.Exists(destination) &&
            string.Equals(Sha256Of(destination), expected, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            File.Copy(downloadedDll, staged, overwrite: false);
            if (!string.Equals(Sha256Of(staged), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Błąd kontroli spójności przed instalacją.");

            if (File.Exists(destination))
            {
                // File.Replace is atomic on the destination volume; keeps a
                // backup whose extension cannot be loaded as an HDT plugin.
                File.Replace(staged, destination, backup, ignoreMetadataErrors: true);
            }
            else File.Move(staged, destination);

            if (!string.Equals(Sha256Of(destination), expected, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(backup))
                    File.Copy(backup, destination, overwrite: true);
                else File.Delete(destination);
                throw new IOException("Kontrola po instalacji nie powiodła się. Cofnięto zmianę.");
            }
            return true;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
