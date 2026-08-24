using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed class NativeModInstaller
{
    public NativeModInstallResult Install(
        GameProfile profile,
        string gameRoot,
        string packageDirectory,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);

        var packageRoot = Path.GetFullPath(packageDirectory);
        if (!Directory.Exists(packageRoot))
        {
            throw new DirectoryNotFoundException($"Mod package directory does not exist: {packageRoot}");
        }
        RejectLink(packageRoot);

        var manifest = ModManifestLoader.Load(Path.Combine(packageRoot, "mod.json"));
        if (manifest.Kind != ModKind.Native)
        {
            throw new InvalidDataException($"Package '{manifest.Id}' is {manifest.Kind}, not Native.");
        }
        var loaderId = manifest.LoaderId!;
        if (loaderId.Equals(RogueModLayout.LoaderModName, StringComparison.OrdinalIgnoreCase)
            || loaderId.Equals(RogueModLayout.LegacyLoaderModName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The native loaderId '{loaderId}' is reserved by the runtime.");
        }

        var normalizedEntryPoint = manifest.EntryPoint.Replace('\\', '/');
        if (!normalizedEntryPoint.Equals("dlls/main.dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native entryPoint must be 'dlls/main.dll' for UE4SS.");
        }
        var entryPoint = ResolveInside(packageRoot, manifest.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException($"Native mod entry DLL does not exist: {entryPoint}", entryPoint);
        }

        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var packageStoreRoot = Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        var ue4ssModsRoot = Resolve(normalizedGameRoot, profile.Ue4ss.RootRelativePath, "Mods");
        Directory.CreateDirectory(packageStoreRoot);
        Directory.CreateDirectory(ue4ssModsRoot);
        var destination = Path.Combine(packageStoreRoot, manifest.Id);
        var deployment = Path.Combine(ue4ssModsRoot, loaderId);
        if (PathsEqual(packageRoot, destination))
        {
            throw new IOException("Package is already located at its installation destination.");
        }
        var destinationExists = Directory.Exists(destination);
        var deploymentExists = Directory.Exists(deployment);
        var replacing = destinationExists || deploymentExists;
        if (replacing && !replace)
        {
            throw new IOException($"Native mod '{manifest.Id}' ({loaderId}) is already installed. Pass --replace to replace it.");
        }
        ValidateExistingOwner(destination, manifest.Id, $"package id '{manifest.Id}'");
        ValidateExistingOwner(deployment, manifest.Id, $"UE4SS loaderId '{loaderId}'");

        var transaction = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(packageStoreRoot, $".stage-{manifest.Id}-{transaction}");
        var backup = Path.Combine(packageStoreRoot, $".backup-{manifest.Id}-{transaction}");
        var deploymentStaging = Path.Combine(ue4ssModsRoot, $".stage-{loaderId}-{transaction}");
        var deploymentBackup = Path.Combine(ue4ssModsRoot, $".backup-{loaderId}-{transaction}");
        var modsFile = Path.Combine(ue4ssModsRoot, "mods.txt");
        var oldModsFile = File.Exists(modsFile) ? File.ReadAllBytes(modsFile) : null;

        try
        {
            CopyPackage(packageRoot, staging);
            CopyPackage(packageRoot, deploymentStaging);
            try
            {
                if (destinationExists)
                {
                    Directory.Move(destination, backup);
                }
                if (deploymentExists)
                {
                    Directory.Move(deployment, deploymentBackup);
                }
                Directory.Move(staging, destination);
                Directory.Move(deploymentStaging, deployment);
                EnableMod(modsFile, loaderId);
            }
            catch
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }
                if (Directory.Exists(deployment))
                {
                    Directory.Delete(deployment, recursive: true);
                }
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }
                if (Directory.Exists(deploymentBackup))
                {
                    Directory.Move(deploymentBackup, deployment);
                }
                RestoreFile(modsFile, oldModsFile);
                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            if (Directory.Exists(deploymentBackup))
            {
                Directory.Delete(deploymentBackup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            if (Directory.Exists(deploymentStaging))
            {
                Directory.Delete(deploymentStaging, recursive: true);
            }
        }

        return new(manifest, destination, deployment, modsFile, replacing);
    }

    private static void ValidateExistingOwner(string directory, string expectedId, string identity)
    {
        var manifestPath = Path.Combine(directory, "mod.json");
        if (!Directory.Exists(directory) || !File.Exists(manifestPath))
        {
            return;
        }
        var installedManifest = ModManifestLoader.Load(manifestPath);
        if (!installedManifest.Id.Equals(expectedId, StringComparison.Ordinal))
        {
            throw new IOException($"The {identity} is already owned by package '{installedManifest.Id}'.");
        }
    }

    private static void EnableMod(string modsFile, string modId)
    {
        var lines = File.Exists(modsFile) ? File.ReadAllLines(modsFile).ToList() : [];
        var matches = lines.Select((line, index) => (line, index))
            .Where(value => IsModLine(value.line, modId))
            .Select(value => value.index)
            .ToArray();

        var enabledLine = $"{modId} : 1";
        if (matches.Length > 0)
        {
            lines[matches[0]] = enabledLine;
            for (var index = matches.Length - 1; index > 0; index--)
            {
                lines.RemoveAt(matches[index]);
            }
        }
        else
        {
            var insertion = lines.FindIndex(line => IsModLine(line, "Keybinds"));
            lines.Insert(insertion < 0 ? lines.Count : insertion, enabledLine);
        }

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

    private static bool IsModLine(string line, string modId)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith(';'))
        {
            return false;
        }
        var separator = trimmed.IndexOf(':');
        return separator > 0
            && trimmed[..separator].Trim().Equals(modId, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyPackage(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            RejectLink(sourceEntry);
            var destination = ResolveInside(destinationRoot, Path.GetFileName(sourceEntry));
            if (Directory.Exists(sourceEntry))
            {
                CopyPackage(sourceEntry, destination);
            }
            else
            {
                File.Copy(sourceEntry, destination, overwrite: false);
            }
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Mod packages cannot contain symbolic links or reparse points: {path}");
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

    private static string Resolve(string root, params string[] relativeParts) =>
        ResolveInside(root, Path.Combine(relativeParts));

    private static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Package path must be relative: {relativePath}");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Package path escapes its root: {relativePath}");
        }
        return candidate;
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record NativeModInstallResult(
    ModManifest Manifest,
    string Destination,
    string Deployment,
    string ModsFile,
    bool Replaced);
