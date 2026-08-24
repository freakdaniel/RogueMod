using System.Text.RegularExpressions;
using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed partial class RogueModRuntimeInstaller
{
    public RogueModRuntimeInstallResult Install(GameProfile profile, string gameRoot, string packageDirectory, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var packageRoot = Path.GetFullPath(packageDirectory);
        ValidatePackage(packageRoot);

        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var gameModsRoot = Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        var modsRoot = Resolve(normalizedGameRoot, profile.Ue4ss.RootRelativePath, "Mods");
        Directory.CreateDirectory(gameModsRoot);
        Directory.CreateDirectory(modsRoot);
        var destination = Path.Combine(modsRoot, RogueModLayout.LoaderModName);
        var legacyDestination = Path.Combine(modsRoot, RogueModLayout.LegacyLoaderModName);
        var destinationExists = Directory.Exists(destination);
        var legacyExists = Directory.Exists(legacyDestination);
        if (destinationExists && legacyExists)
        {
            throw new IOException(
                $"Both '{RogueModLayout.LoaderModName}' and legacy '{RogueModLayout.LegacyLoaderModName}' runtime directories exist. Resolve the duplicate before updating.");
        }
        var existingDestination = destinationExists ? destination : legacyExists ? legacyDestination : null;
        var replacing = existingDestination is not null;
        var migrating = legacyExists;
        if (replacing && !replace)
        {
            throw new IOException("RogueMod runtime is already installed. Pass --replace to replace it.");
        }

        var transaction = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(modsRoot, $".stage-RogueMod-{transaction}");
        var backup = Path.Combine(modsRoot, $".backup-RogueMod-{transaction}");
        var modsFile = Path.Combine(modsRoot, "mods.txt");
        var oldModsFile = File.Exists(modsFile) ? File.ReadAllBytes(modsFile) : null;
        var migratedManagedMods = new List<(string LegacyPath, string Destination)>();
        try
        {
            CopyTree(packageRoot, staging);
            if (replacing)
            {
                Directory.Move(existingDestination!, backup);
            }

            try
            {
                Directory.Move(staging, destination);
                if (replacing)
                {
                    MigrateLegacyManagedMods(backup, gameModsRoot, migratedManagedMods);
                }
                EnableRuntime(modsFile);
            }
            catch
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }
                foreach (var migration in migratedManagedMods.AsEnumerable().Reverse())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(migration.LegacyPath)!);
                    Directory.Move(migration.Destination, migration.LegacyPath);
                }
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, existingDestination!);
                }
                RestoreFile(modsFile, oldModsFile);
                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return new(destination, modsFile, replacing, migrating, migratedManagedMods.Count);
    }

    private static void MigrateLegacyManagedMods(
        string runtimeBackup,
        string gameModsRoot,
        ICollection<(string LegacyPath, string Destination)> migrations)
    {
        var legacyRoot = Path.Combine(runtimeBackup, RogueModLayout.LegacyManagedModsDirectoryName);
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        foreach (var legacyDirectory in Directory.EnumerateDirectories(legacyRoot).Order(StringComparer.Ordinal))
        {
            var manifest = ModManifestLoader.Load(Path.Combine(legacyDirectory, "mod.json"));
            if (manifest.Kind != ModKind.Managed)
            {
                throw new InvalidDataException(
                    $"Legacy managed mod directory contains non-managed package '{manifest.Id}'.");
            }
            var destination = Path.Combine(gameModsRoot, manifest.Id);
            if (Directory.Exists(destination))
            {
                var currentManifest = ModManifestLoader.Load(Path.Combine(destination, "mod.json"));
                if (!currentManifest.Id.Equals(manifest.Id, StringComparison.Ordinal))
                {
                    throw new IOException($"Cannot migrate '{manifest.Id}': destination is owned by '{currentManifest.Id}'.");
                }
                continue;
            }
            Directory.Move(legacyDirectory, destination);
            migrations.Add((legacyDirectory, destination));
        }
    }

    private static void ValidatePackage(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Runtime package does not exist: {root}");
        }
        RejectLink(root);
        RequireFile(root, "dlls/main.dll");
        RequireFile(root, "runtime/managed/RogueMod.Runtime.dll");
        RequireFile(root, "runtime/managed/RogueMod.Runtime.runtimeconfig.json");
        var fxrRoot = Resolve(root, "runtime", "dotnet", "host", "fxr");
        if (!Directory.Exists(fxrRoot) || !Directory.EnumerateFiles(fxrRoot, "hostfxr.dll", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("Runtime package does not contain Windows hostfxr.dll.");
        }
    }

    private static void RequireFile(string root, string relativePath)
    {
        var path = Resolve(root, relativePath.Split('/'));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Runtime package file is missing: {path}", path);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLink(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Runtime packages cannot contain links: {path}");
        }
    }

    private static void EnableRuntime(string modsFile)
    {
        var lines = File.Exists(modsFile) ? File.ReadAllLines(modsFile).ToList() : [];
        lines.RemoveAll(line => RogueModBridgeLine().IsMatch(line) || LegacyRogueModLine().IsMatch(line));
        var insertion = lines.FindIndex(line => KeybindsLine().IsMatch(line));
        lines.Insert(
            insertion < 0 ? lines.Count : insertion,
            $"{RogueModLayout.LoaderModName} : 1");

        Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
        var temporary = modsFile + $".roguemod-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, modsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RestoreFile(string path, byte[]? content)
    {
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, content);
        }
    }

    private static string Resolve(string root, params string[] parts)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(parts.Aggregate(normalizedRoot, Path.Combine));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Path escapes its root.");
        }
        return candidate;
    }

    [GeneratedRegex("^\\s*RogueModBridge\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RogueModBridgeLine();

    [GeneratedRegex("^\\s*RogueMod\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRogueModLine();

    [GeneratedRegex("^\\s*Keybinds\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeybindsLine();
}

public sealed record RogueModRuntimeInstallResult(
    string Destination,
    string ModsFile,
    bool Replaced,
    bool MigratedFromLegacyLayout,
    int MigratedManagedModCount);
