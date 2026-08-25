using System.Text.RegularExpressions;
using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed partial class RogueModRuntimeInstaller
{
    public RogueModRuntimeInstallResult Install(
        GameProfile profile,
        string gameRoot,
        string packageDirectory,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var packageRoot = Path.GetFullPath(packageDirectory);
        ValidatePackage(packageRoot);

        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var gameModsRoot = Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        var ue4ssModsRoot = Resolve(normalizedGameRoot, profile.Ue4ss.RootRelativePath, "Mods");
        var runtimeRoot = Resolve(normalizedGameRoot, RogueModLayout.RuntimeDirectoryName);
        var bridgeDeployment = Path.Combine(ue4ssModsRoot, RogueModLayout.LoaderModName);
        var legacyBridgeDeployment = Path.Combine(ue4ssModsRoot, RogueModLayout.LegacyLoaderModName);
        Directory.CreateDirectory(gameModsRoot);
        Directory.CreateDirectory(ue4ssModsRoot);

        if (ModPackageFileSystem.PathsEqual(packageRoot, runtimeRoot))
        {
            throw new IOException("Runtime package is already located at its installation destination.");
        }
        var bridgeExists = Directory.Exists(bridgeDeployment);
        var legacyBridgeExists = Directory.Exists(legacyBridgeDeployment);
        if (bridgeExists && legacyBridgeExists)
        {
            throw new IOException(
                $"Both '{RogueModLayout.LoaderModName}' and legacy '{RogueModLayout.LegacyLoaderModName}' bridge directories exist.");
        }
        var existingBridge = bridgeExists ? bridgeDeployment : legacyBridgeExists ? legacyBridgeDeployment : null;
        var runtimeExists = Directory.Exists(runtimeRoot);
        var replacing = runtimeExists || existingBridge is not null;
        if (replacing && !replace)
        {
            throw new IOException("RogueMod runtime is already installed. Pass --replace to replace it.");
        }

        var legacyRuntimeLayout = legacyBridgeExists
            || existingBridge is not null && Directory.Exists(Path.Combine(existingBridge, "runtime"));
        var transaction = Guid.NewGuid().ToString("N");
        var runtimeStaging = Path.Combine(normalizedGameRoot, $".RogueMod.stage-{transaction}");
        var runtimeBackup = Path.Combine(normalizedGameRoot, $".RogueMod.backup-{transaction}");
        var bridgeStaging = Path.Combine(ue4ssModsRoot, $".stage-{RogueModLayout.LoaderModName}-{transaction}");
        var bridgeBackup = Path.Combine(ue4ssModsRoot, $".backup-{RogueModLayout.LoaderModName}-{transaction}");
        var modsFile = Path.Combine(ue4ssModsRoot, "mods.txt");
        var oldModsFile = File.Exists(modsFile) ? File.ReadAllBytes(modsFile) : null;
        var migratedManagedMods = new List<(string LegacyPath, string Destination)>();
        try
        {
            CopyTree(packageRoot, runtimeStaging);
            PreserveSharedRuntimeFiles(runtimeRoot, runtimeStaging);
            if (existingBridge is not null)
            {
                PreserveSharedRuntimeFiles(existingBridge, runtimeStaging);
            }
            Directory.CreateDirectory(Path.Combine(bridgeStaging, "dlls"));
            File.Copy(
                Resolve(packageRoot, "dlls", "main.dll"),
                Path.Combine(bridgeStaging, "dlls", "main.dll"),
                overwrite: false);

            try
            {
                if (runtimeExists)
                {
                    Directory.Move(runtimeRoot, runtimeBackup);
                }
                if (existingBridge is not null)
                {
                    Directory.Move(existingBridge, bridgeBackup);
                }
                Directory.Move(runtimeStaging, runtimeRoot);
                Directory.Move(bridgeStaging, bridgeDeployment);
                MigrateLegacyManagedMods(runtimeBackup, gameModsRoot, migratedManagedMods);
                MigrateLegacyManagedMods(bridgeBackup, gameModsRoot, migratedManagedMods);
                EnableRuntime(modsFile);
            }
            catch
            {
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, recursive: true);
                }
                if (Directory.Exists(bridgeDeployment))
                {
                    Directory.Delete(bridgeDeployment, recursive: true);
                }
                foreach (var migration in migratedManagedMods.AsEnumerable().Reverse())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(migration.LegacyPath)!);
                    Directory.Move(migration.Destination, migration.LegacyPath);
                }
                if (Directory.Exists(runtimeBackup))
                {
                    Directory.Move(runtimeBackup, runtimeRoot);
                }
                if (Directory.Exists(bridgeBackup) && existingBridge is not null)
                {
                    Directory.Move(bridgeBackup, existingBridge);
                }
                ModPackageFileSystem.RestoreFile(modsFile, oldModsFile);
                throw;
            }

            if (Directory.Exists(runtimeBackup))
            {
                Directory.Delete(runtimeBackup, recursive: true);
            }
            if (Directory.Exists(bridgeBackup))
            {
                Directory.Delete(bridgeBackup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(runtimeStaging))
            {
                Directory.Delete(runtimeStaging, recursive: true);
            }
            if (Directory.Exists(bridgeStaging))
            {
                Directory.Delete(bridgeStaging, recursive: true);
            }
        }

        return new(runtimeRoot, modsFile, replacing, legacyRuntimeLayout, migratedManagedMods.Count)
        {
            BridgeDeployment = bridgeDeployment
        };
    }

    private static void MigrateLegacyManagedMods(
        string sourceRoot,
        string gameModsRoot,
        ICollection<(string LegacyPath, string Destination)> migrations)
    {
        var legacyRoot = Path.Combine(sourceRoot, RogueModLayout.LegacyManagedModsDirectoryName);
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

    private static void PreserveSharedRuntimeFiles(string sourceRoot, string stagingRoot)
    {
        var source = Path.Combine(sourceRoot, "runtime", "shared");
        if (!Directory.Exists(source))
        {
            return;
        }
        var destination = Path.Combine(stagingRoot, "runtime", "shared");
        CopyMissingTree(source, destination);
    }

    private static void CopyMissingTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLink(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyMissingTree(entry, target);
            }
            else if (!File.Exists(target))
            {
                File.Copy(entry, target);
            }
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
        lines.Insert(insertion < 0 ? lines.Count : insertion, $"{RogueModLayout.LoaderModName} : 1");
        Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
        var temporary = modsFile + $".roguemod-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, modsFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string Resolve(string root, params string[] parts) =>
        ModPackageFileSystem.Resolve(root, parts);

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
    int MigratedManagedModCount)
{
    public string? BridgeDeployment { get; init; }
}
