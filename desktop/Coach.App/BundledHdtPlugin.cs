using System.Reflection;
using Coach.Core;

namespace Coach.App;

/// <summary>
/// Holds the plugin compiled from the SAME source revision as this Windows
/// application. Embedded into the EXE at publish-time by GitHub Actions;
/// no unpacked auxiliary files or DNS are required after installation.
/// </summary>
internal static class BundledHdtPlugin
{
    internal const string ResourceName = "Coach.App.BundledHdtPlugin";
    private const long MaxBytes = 20_000_000;
    private static readonly Assembly OwnAssembly = typeof(BundledHdtPlugin).Assembly;

    public static bool Available =>
        OwnAssembly.GetManifestResourceInfo(ResourceName) is not null;

    public static string Extract(string destination)
    {
        using Stream? input = OwnAssembly.GetManifestResourceStream(ResourceName);
        if (input is null)
            throw new FileNotFoundException(
                "W aplikacji nie ma dołączonej DLL. Pobierz pełne wydanie Windows z GitHub Actions.");
        if (input.Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("Nieprawidłowy rozmiar DLL w aplikacji.");
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
            input.CopyTo(output);
        var dll = AssemblyName.GetAssemblyName(destination);
        if (!string.Equals(dll.Name, "HearthstoneMcpPlugin", StringComparison.Ordinal))
            throw new InvalidDataException("Dołączona biblioteka ma nieprawidłową tożsamość.");
        return PluginFileSwap.Sha256Of(destination);
    }
}
