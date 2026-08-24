using System.Security.Cryptography;
using System.Text;
using RogueMod.Core.Profiles;

namespace RogueMod.Core.Diagnostics;

public sealed class InstallationInspector
{
    private static readonly HashSet<string> BundledUe4ssMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "CheatManagerEnablerMod",
        "ConsoleCommandsMod",
        "ConsoleEnablerMod",
        "SplitScreenMod",
        "LineTraceMod",
        "BPML_GenericFunctions",
        "BPModLoaderMod",
        "Keybinds"
    };

    public DiagnosticReport Inspect(GameProfile profile, string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);

        var root = Path.GetFullPath(gameRoot);
        var checks = new List<DiagnosticCheck>();

        if (!Directory.Exists(root))
        {
            checks.Add(new("game-root", DiagnosticStatus.Fail, $"Game directory does not exist: {root}"));
            return new(profile, root, checks);
        }

        CheckFile(checks, "game-executable", Resolve(root, profile.ExecutableRelativePath), required: true);
        CheckFile(checks, "ue4ss-proxy", Resolve(root, profile.Ue4ss.ProxyRelativePath), required: false);
        CheckFile(checks, "ue4ss-library", Resolve(root, profile.Ue4ss.LibraryRelativePath), required: false);

        foreach (var compatibilityFile in profile.Ue4ss.CompatibilityFiles)
        {
            CheckCompatibilityFile(checks, root, compatibilityFile);
        }

        CheckEnabledBuiltInMods(checks, Resolve(root, profile.Ue4ss.RootRelativePath, "Mods", "mods.txt"));
        return new(profile, root, checks);
    }

    private static void CheckFile(List<DiagnosticCheck> checks, string id, string path, bool required)
    {
        if (File.Exists(path))
        {
            checks.Add(new(id, DiagnosticStatus.Pass, path));
            return;
        }

        checks.Add(new(id, required ? DiagnosticStatus.Fail : DiagnosticStatus.Warning, $"Missing: {path}"));
    }

    private static void CheckCompatibilityFile(
        List<DiagnosticCheck> checks,
        string root,
        CompatibilityFile compatibilityFile)
    {
        var path = Resolve(root, compatibilityFile.DestinationRelativePath);
        if (!File.Exists(path))
        {
            checks.Add(new("compatibility-file", DiagnosticStatus.Warning, $"Missing: {path}"));
            return;
        }

        var actual = FileFingerprint.ComputeNormalizedTextSha256(path);
        var status = actual.Equals(compatibilityFile.NormalizedSha256, StringComparison.OrdinalIgnoreCase)
            ? DiagnosticStatus.Pass
            : DiagnosticStatus.Fail;
        var message = status == DiagnosticStatus.Pass
            ? path
            : $"Unexpected content: {path} (SHA-256 {actual})";
        checks.Add(new("compatibility-file", status, message));
    }

    private static void CheckEnabledBuiltInMods(List<DiagnosticCheck> checks, string modsFile)
    {
        if (!File.Exists(modsFile))
        {
            checks.Add(new("built-in-mods", DiagnosticStatus.Warning, $"Missing: {modsFile}"));
            return;
        }

        var enabled = File.ReadLines(modsFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(';') && line.EndsWith(": 1", StringComparison.Ordinal))
            .Select(line => line[..line.LastIndexOf(':')].Trim())
            .Where(BundledUe4ssMods.Contains)
            .ToArray();

        checks.Add(enabled.Length == 0
            ? new("built-in-mods", DiagnosticStatus.Pass, "All bundled UE4SS mods are disabled.")
            : new("built-in-mods", DiagnosticStatus.Warning, $"Enabled: {string.Join(", ", enabled)}"));
    }

    private static string Resolve(string root, params string[] relativeParts)
    {
        var normalized = relativeParts
            .SelectMany(part => part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        return Path.Combine([root, .. normalized]);
    }
}

public static class FileFingerprint
{
    public static string ComputeNormalizedTextSha256(string path)
    {
        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

public sealed record DiagnosticReport(GameProfile Profile, string GameRoot, IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool IsCompatible => Checks.All(check => check.Status != DiagnosticStatus.Fail);
}

public sealed record DiagnosticCheck(string Id, DiagnosticStatus Status, string Message);

public enum DiagnosticStatus
{
    Pass,
    Warning,
    Fail
}
