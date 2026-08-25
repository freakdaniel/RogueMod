using System.Security.Cryptography;
using System.Text;
using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed class PakModInstaller
{
    private static readonly string[] CompanionExtensions = [".utoc", ".ucas", ".sig"];

    public PakModInstallResult Install(
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
        ModPackageFileSystem.RejectLink(packageRoot);
        var manifest = ModManifestLoader.Load(Path.Combine(packageRoot, "mod.json"));
        ModPackageMetadataValidator.ValidateAssets(packageRoot, manifest);
        ValidatePackage(packageRoot, manifest);

        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var storeRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        var pakRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, profile.PakRootRelativePath);
        Directory.CreateDirectory(storeRoot);
        Directory.CreateDirectory(pakRoot);
        var destination = Path.Combine(storeRoot, manifest.Id);
        if (ModPackageFileSystem.PathsEqual(packageRoot, destination))
        {
            throw new IOException("Package is already located at its installation destination.");
        }

        ModManifest? previousManifest = null;
        if (Directory.Exists(destination))
        {
            previousManifest = ModManifestLoader.Load(Path.Combine(destination, "mod.json"));
            if (!previousManifest.Id.Equals(manifest.Id, StringComparison.Ordinal))
            {
                throw new IOException($"Package destination is owned by '{previousManifest.Id}'.");
            }
        }
        var newDeployments = GetDeploymentFiles(profile, normalizedGameRoot, packageRoot, manifest);
        var oldDeploymentPaths = previousManifest is null
            ? []
            : GetDeploymentFiles(profile, normalizedGameRoot, destination, previousManifest)
                .Select(file => file.Destination)
                .ToArray();
        var replacing = Directory.Exists(destination)
            || newDeployments.Any(file => File.Exists(file.Destination))
            || oldDeploymentPaths.Any(File.Exists);
        if (replacing && !replace)
        {
            throw new IOException($"Pak mod '{manifest.Id}' is already installed. Pass --replace to replace it.");
        }

        var transaction = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(storeRoot, $".stage-{manifest.Id}-{transaction}");
        var backup = Path.Combine(storeRoot, $".backup-{manifest.Id}-{transaction}");
        var touchedDeployments = oldDeploymentPaths.Concat(newDeployments.Select(file => file.Destination))
            .Distinct(PathComparer)
            .ToArray();
        var deploymentBackups = touchedDeployments.ToDictionary(
            path => path,
            path => path + $".backup-{transaction}",
            PathComparer);
        var deploymentStaging = newDeployments.ToDictionary(
            file => file.Destination,
            file => file.Destination + $".stage-{transaction}",
            PathComparer);
        try
        {
            ModPackageFileSystem.CopyTree(packageRoot, staging);
            foreach (var deployment in newDeployments)
            {
                File.Copy(deployment.Source, deploymentStaging[deployment.Destination], overwrite: false);
            }
            try
            {
                if (Directory.Exists(destination))
                {
                    Directory.Move(destination, backup);
                }
                foreach (var path in touchedDeployments.Where(File.Exists))
                {
                    File.Move(path, deploymentBackups[path]);
                }
                Directory.Move(staging, destination);
                foreach (var deployment in newDeployments)
                {
                    File.Move(deploymentStaging[deployment.Destination], deployment.Destination);
                }
            }
            catch
            {
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, recursive: true);
                }
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }
                foreach (var path in touchedDeployments)
                {
                    File.Delete(path);
                    if (File.Exists(deploymentBackups[path]))
                    {
                        File.Move(deploymentBackups[path], path);
                    }
                }
                throw;
            }
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
            foreach (var backupPath in deploymentBackups.Values)
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            foreach (var stagingPath in deploymentStaging.Values)
            {
                File.Delete(stagingPath);
            }
        }
        return new(manifest, destination, newDeployments.Select(file => file.Destination).ToArray(), replacing);
    }

    internal static bool IsEnabled(GameProfile profile, string gameRoot, string packageRoot, ModManifest manifest)
    {
        var deployments = GetDeploymentFiles(profile, Path.GetFullPath(gameRoot), packageRoot, manifest);
        return deployments.Count > 0 && deployments.All(file => File.Exists(file.Destination));
    }

    internal static IReadOnlyList<string> SetEnabled(
        GameProfile profile,
        string gameRoot,
        string packageRoot,
        ModManifest manifest,
        bool enabled)
    {
        var deployments = GetDeploymentFiles(profile, Path.GetFullPath(gameRoot), packageRoot, manifest);
        var transaction = Guid.NewGuid().ToString("N");
        if (!enabled)
        {
            var disabledBackups = deployments.ToDictionary(
                file => file.Destination,
                file => file.Destination + $".disable-{transaction}",
                PathComparer);
            try
            {
                foreach (var deployment in deployments.Where(file => File.Exists(file.Destination)))
                {
                    File.Move(deployment.Destination, disabledBackups[deployment.Destination]);
                }
            }
            catch
            {
                foreach (var pair in disabledBackups.Where(pair => File.Exists(pair.Value)))
                {
                    File.Move(pair.Value, pair.Key);
                }
                throw;
            }
            foreach (var backup in disabledBackups.Values)
            {
                File.Delete(backup);
            }
            return deployments.Select(file => file.Destination).ToArray();
        }

        var staging = deployments.ToDictionary(
            file => file.Destination,
            file => file.Destination + $".stage-{transaction}",
            PathComparer);
        var backups = deployments.ToDictionary(
            file => file.Destination,
            file => file.Destination + $".backup-{transaction}",
            PathComparer);
        try
        {
            foreach (var deployment in deployments)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(deployment.Destination)!);
                File.Copy(deployment.Source, staging[deployment.Destination], overwrite: false);
            }
            try
            {
                foreach (var deployment in deployments)
                {
                    if (File.Exists(deployment.Destination))
                    {
                        File.Move(deployment.Destination, backups[deployment.Destination]);
                    }
                    File.Move(staging[deployment.Destination], deployment.Destination);
                }
            }
            catch
            {
                foreach (var deployment in deployments)
                {
                    File.Delete(deployment.Destination);
                    if (File.Exists(backups[deployment.Destination]))
                    {
                        File.Move(backups[deployment.Destination], deployment.Destination);
                    }
                }
                throw;
            }
            foreach (var backupPath in backups.Values)
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            foreach (var stagingPath in staging.Values)
            {
                File.Delete(stagingPath);
            }
        }
        return deployments.Select(file => file.Destination).ToArray();
    }

    internal static IReadOnlyList<PakDeploymentFile> GetDeploymentFiles(
        GameProfile profile,
        string gameRoot,
        string packageRoot,
        ModManifest manifest)
    {
        ValidatePackage(packageRoot, manifest);
        var primary = ModPackageFileSystem.ResolveInside(packageRoot, manifest.EntryPoint);
        var payload = new List<string> { primary };
        var directory = Path.GetDirectoryName(primary)!;
        var baseName = Path.GetFileNameWithoutExtension(primary);
        foreach (var extension in CompanionExtensions)
        {
            var companion = Path.Combine(directory, baseName + extension);
            if (File.Exists(companion))
            {
                payload.Add(companion);
            }
        }
        var pakRoot = ModPackageFileSystem.Resolve(gameRoot, profile.PakRootRelativePath);
        var prefix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Id)))[..16];
        return payload.Select(path => new PakDeploymentFile(
            path,
            Path.Combine(pakRoot, $"roguemod-{prefix}-{Path.GetFileName(path)}"))).ToArray();
    }

    private static void ValidatePackage(string packageRoot, ModManifest manifest)
    {
        if (manifest.Kind != ModKind.Pak)
        {
            throw new InvalidDataException($"Package '{manifest.Id}' is {manifest.Kind}, not Pak.");
        }
        if (!Path.GetExtension(manifest.EntryPoint).Equals(".pak", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Pak entryPoint must reference a .pak file.");
        }
        var entryPoint = ModPackageFileSystem.ResolveInside(packageRoot, manifest.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException($"Pak entry point does not exist: {entryPoint}", entryPoint);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record PakDeploymentFile(string Source, string Destination);

public sealed record PakModInstallResult(
    ModManifest Manifest,
    string Destination,
    IReadOnlyList<string> Deployments,
    bool Replaced);
