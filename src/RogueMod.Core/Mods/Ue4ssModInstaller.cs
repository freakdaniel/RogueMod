using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

internal sealed class Ue4ssModInstaller(ModKind kind, string requiredEntryPoint)
{
    public Ue4ssModInstallResult Install(GameProfile profile, string gameRoot, string packageDirectory, bool replace)
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
        if (manifest.Kind != kind)
        {
            throw new InvalidDataException($"Package '{manifest.Id}' is {manifest.Kind}, not {kind}.");
        }
        var loaderId = manifest.LoaderId!;
        if (loaderId.Equals(RogueModLayout.LoaderModName, StringComparison.OrdinalIgnoreCase)
            || loaderId.Equals(RogueModLayout.LegacyLoaderModName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {kind.ToString().ToLowerInvariant()} loaderId '{loaderId}' is reserved by the runtime.");
        }
        if (!manifest.EntryPoint.Replace('\\', '/').Equals(requiredEntryPoint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{kind} entryPoint must be '{requiredEntryPoint}' for UE4SS.");
        }
        var entryPoint = ModPackageFileSystem.ResolveInside(packageRoot, manifest.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException($"{kind} entry point does not exist: {entryPoint}", entryPoint);
        }

        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var packageStoreRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        var ue4ssModsRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, profile.Ue4ss.RootRelativePath, "Mods");
        Directory.CreateDirectory(packageStoreRoot);
        Directory.CreateDirectory(ue4ssModsRoot);
        var destination = Path.Combine(packageStoreRoot, manifest.Id);
        var deployment = Path.Combine(ue4ssModsRoot, loaderId);
        if (ModPackageFileSystem.PathsEqual(packageRoot, destination))
        {
            throw new IOException("Package is already located at its installation destination.");
        }
        var destinationExists = Directory.Exists(destination);
        var deploymentExists = Directory.Exists(deployment);
        if (destinationExists)
        {
            var previousManifest = ModManifestLoader.Load(Path.Combine(destination, "mod.json"));
            if (!string.Equals(previousManifest.LoaderId, loaderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{kind} mod '{manifest.Id}' cannot change loaderId from '{previousManifest.LoaderId}' to '{loaderId}' during replacement.");
            }
        }
        var replacing = destinationExists || deploymentExists;
        if (replacing && !replace)
        {
            throw new IOException($"{kind} mod '{manifest.Id}' ({loaderId}) is already installed. Pass --replace to replace it.");
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
            ModPackageFileSystem.CopyTree(packageRoot, staging);
            ModPackageFileSystem.CopyTree(packageRoot, deploymentStaging);
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
                Ue4ssModsFile.SetEnabled(modsFile, loaderId, enabled: true);
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
                ModPackageFileSystem.RestoreFile(modsFile, oldModsFile);
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
        if (!Directory.Exists(directory))
        {
            return;
        }
        if (!File.Exists(manifestPath))
        {
            throw new IOException($"The {identity} already exists but is not owned by a RogueMod package.");
        }
        var installedManifest = ModManifestLoader.Load(manifestPath);
        if (!installedManifest.Id.Equals(expectedId, StringComparison.Ordinal))
        {
            throw new IOException($"The {identity} is already owned by package '{installedManifest.Id}'.");
        }
    }
}

internal sealed record Ue4ssModInstallResult(
    ModManifest Manifest,
    string Destination,
    string Deployment,
    string ModsFile,
    bool Replaced);
