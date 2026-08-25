using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed class ManagedModInstaller
{
    public ManagedModInstallResult Install(
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
        ModPackageMetadataValidator.ValidateAssets(packageRoot, manifest);
        if (manifest.Kind != ModKind.Managed)
        {
            throw new InvalidDataException($"Package '{manifest.Id}' is {manifest.Kind}, not Managed.");
        }
        ValidateManagedEntryPoint(packageRoot, manifest);

        var destinationRoot = Resolve(Path.GetFullPath(gameRoot), RogueModLayout.GameModsDirectoryName);
        Directory.CreateDirectory(destinationRoot);

        var destination = Path.Combine(destinationRoot, manifest.Id);
        if (PathsEqual(packageRoot, destination))
        {
            throw new IOException("Package is already located at its installation destination.");
        }
        var replacingExisting = Directory.Exists(destination);
        if (replacingExisting && !replace)
        {
            throw new IOException($"Managed mod '{manifest.Id}' is already installed. Pass --replace to replace it.");
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(destinationRoot, $".stage-{manifest.Id}-{transactionId}");
        var backup = Path.Combine(destinationRoot, $".backup-{manifest.Id}-{transactionId}");
        try
        {
            CopyPackage(packageRoot, staging);
            File.Delete(Path.Combine(staging, RogueModLayout.DisabledMarkerFileName));
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                }
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

        return new(manifest, destination, replacingExisting);
    }

    private static void ValidateManagedEntryPoint(string packageRoot, ModManifest manifest)
    {
        var separator = manifest.EntryPoint.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0
            || separator == manifest.EntryPoint.Length - 2
            || manifest.EntryPoint.IndexOf("::", separator + 2, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException("Managed entryPoint must use '<assembly.dll>::<namespace.type>'.");
        }

        var assemblyRelativePath = manifest.EntryPoint[..separator].Trim();
        var assemblyPath = ResolveInside(packageRoot, assemblyRelativePath);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Managed entry assembly does not exist: {assemblyPath}", assemblyPath);
        }
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
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Mod packages cannot contain symbolic links or reparse points: {path}");
        }
    }

    private static string Resolve(string root, params string[] relativeParts)
    {
        var path = relativeParts.Aggregate(root, Path.Combine);
        return ResolveInside(root, Path.GetRelativePath(root, path));
    }

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

public sealed record ManagedModInstallResult(ModManifest Manifest, string Destination, bool Replaced);
