using System.Text;
using System.Text.RegularExpressions;
using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed partial class ModManager
{
    public ModInstallResult Install(
        GameProfile profile,
        string gameRoot,
        string packageDirectory,
        bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var packageRoot = Path.GetFullPath(packageDirectory);
        var manifest = ModManifestLoader.Load(Path.Combine(packageRoot, "mod.json"));
        return manifest.Kind switch
        {
            ModKind.Managed => FromManaged(new ManagedModInstaller().Install(profile, gameRoot, packageRoot, replace)),
            ModKind.Native => FromNative(new NativeModInstaller().Install(profile, gameRoot, packageRoot, replace)),
            ModKind.Lua => FromLua(new LuaModInstaller().Install(profile, gameRoot, packageRoot, replace)),
            ModKind.Pak => FromPak(new PakModInstaller().Install(profile, gameRoot, packageRoot, replace)),
            _ => throw new ArgumentOutOfRangeException(nameof(manifest.Kind), manifest.Kind, null)
        };
    }

    public ModUpdateResult Update(GameProfile profile, string gameRoot, string packageDirectory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var packageRoot = Path.GetFullPath(packageDirectory);
        var incoming = ModManifestLoader.Load(Path.Combine(packageRoot, "mod.json"));
        var installed = GetRequired(profile, gameRoot, incoming.Id);
        if (installed.Manifest.Kind != incoming.Kind)
        {
            throw new InvalidDataException(
                $"Mod '{incoming.Id}' cannot change kind from {installed.Manifest.Kind} to {incoming.Kind} during update.");
        }

        var preserveDisabled = installed.State == ModActivationState.Disabled;
        var result = Install(profile, gameRoot, packageRoot, replace: true);
        if (preserveDisabled)
        {
            SetEnabled(profile, gameRoot, incoming.Id, enabled: false);
        }
        return new(installed.Manifest.Version, result.Manifest.Version, result, preserveDisabled);
    }

    public IReadOnlyList<InstalledModInfo> List(GameProfile profile, string gameRoot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var storeRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, RogueModLayout.GameModsDirectoryName);
        if (!Directory.Exists(storeRoot))
        {
            return [];
        }

        var result = new List<InstalledModInfo>();
        foreach (var directory in Directory.EnumerateDirectories(storeRoot).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }
            var manifestPath = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }
            var manifest = ModManifestLoader.Load(manifestPath);
            if (!Path.GetFileName(directory).Equals(
                    manifest.Id,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Installed package directory '{directory}' does not match manifest id '{manifest.Id}'.");
            }
            result.Add(new InstalledModInfo
            {
                Manifest = manifest,
                Directory = directory,
                State = GetState(profile, normalizedGameRoot, directory, manifest)
            });
        }
        return result.OrderBy(mod => mod.Manifest.Id, StringComparer.Ordinal).ToArray();
    }

    public InstalledModInfo SetEnabled(GameProfile profile, string gameRoot, string modId, bool enabled)
    {
        var installed = GetRequired(profile, gameRoot, modId);
        switch (installed.Manifest.Kind)
        {
            case ModKind.Managed:
                SetManagedEnabled(installed.Directory, enabled);
                break;
            case ModKind.Native:
            case ModKind.Lua:
                SetUe4ssEnabled(profile, gameRoot, installed, enabled);
                break;
            case ModKind.Pak:
                PakModInstaller.SetEnabled(profile, gameRoot, installed.Directory, installed.Manifest, enabled);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(installed.Manifest.Kind), installed.Manifest.Kind, null);
        }
        return GetRequired(profile, gameRoot, modId);
    }

    public ModUninstallResult Uninstall(GameProfile profile, string gameRoot, string modId)
    {
        var installed = GetRequired(profile, gameRoot, modId);
        var normalizedGameRoot = Path.GetFullPath(gameRoot);
        var transaction = Guid.NewGuid().ToString("N");
        var packageBackup = installed.Directory + $".uninstall-{transaction}";
        string? deployment = null;
        string? deploymentBackup = null;
        string? modsFile = null;
        byte[]? oldModsFile = null;
        IReadOnlyList<string> pakDeployments = [];
        var pakBackups = new Dictionary<string, string>(PathComparer);

        if (installed.Manifest.Kind is ModKind.Native or ModKind.Lua)
        {
            var ue4ssModsRoot = ModPackageFileSystem.Resolve(normalizedGameRoot, profile.Ue4ss.RootRelativePath, "Mods");
            deployment = Path.Combine(ue4ssModsRoot, installed.Manifest.LoaderId!);
            deploymentBackup = deployment + $".uninstall-{transaction}";
            modsFile = Path.Combine(ue4ssModsRoot, "mods.txt");
            oldModsFile = File.Exists(modsFile) ? File.ReadAllBytes(modsFile) : null;
        }
        else if (installed.Manifest.Kind == ModKind.Pak)
        {
            pakDeployments = PakModInstaller.GetDeploymentFiles(
                    profile,
                    normalizedGameRoot,
                    installed.Directory,
                    installed.Manifest)
                .Select(file => file.Destination)
                .ToArray();
            foreach (var path in pakDeployments)
            {
                pakBackups[path] = path + $".uninstall-{transaction}";
            }
        }

        try
        {
            Directory.Move(installed.Directory, packageBackup);
            if (deployment is not null && Directory.Exists(deployment))
            {
                Directory.Move(deployment, deploymentBackup!);
            }
            foreach (var path in pakDeployments.Where(File.Exists))
            {
                File.Move(path, pakBackups[path]);
            }
            if (modsFile is not null)
            {
                Ue4ssModsFile.Remove(modsFile, installed.Manifest.LoaderId!);
            }
        }
        catch
        {
            if (modsFile is not null)
            {
                ModPackageFileSystem.RestoreFile(modsFile, oldModsFile);
            }
            if (deploymentBackup is not null && Directory.Exists(deploymentBackup) && deployment is not null)
            {
                Directory.Move(deploymentBackup, deployment);
            }
            foreach (var pair in pakBackups.Where(pair => File.Exists(pair.Value)))
            {
                File.Move(pair.Value, pair.Key);
            }
            if (Directory.Exists(packageBackup) && !Directory.Exists(installed.Directory))
            {
                Directory.Move(packageBackup, installed.Directory);
            }
            throw;
        }

        Directory.Delete(packageBackup, recursive: true);
        if (deploymentBackup is not null && Directory.Exists(deploymentBackup))
        {
            Directory.Delete(deploymentBackup, recursive: true);
        }
        foreach (var backup in pakBackups.Values)
        {
            File.Delete(backup);
        }
        return new(installed.Manifest, installed.Directory, deployment, pakDeployments);
    }

    private InstalledModInfo GetRequired(GameProfile profile, string gameRoot, string modId)
    {
        ValidateModId(modId);
        return List(profile, gameRoot).SingleOrDefault(mod => mod.Manifest.Id.Equals(modId, StringComparison.Ordinal))
            ?? throw new DirectoryNotFoundException($"Mod '{modId}' is not installed.");
    }

    private static ModActivationState GetState(
        GameProfile profile,
        string gameRoot,
        string packageDirectory,
        ModManifest manifest) => manifest.Kind switch
        {
            ModKind.Managed => File.Exists(Path.Combine(packageDirectory, RogueModLayout.DisabledMarkerFileName))
                ? ModActivationState.Disabled
                : ModActivationState.Enabled,
            ModKind.Native or ModKind.Lua => GetUe4ssState(profile, gameRoot, manifest),
            ModKind.Pak => GetPakState(profile, gameRoot, packageDirectory, manifest),
            _ => throw new ArgumentOutOfRangeException(nameof(manifest.Kind), manifest.Kind, null)
        };

    private static ModActivationState GetUe4ssState(GameProfile profile, string gameRoot, ModManifest manifest)
    {
        var modsRoot = ModPackageFileSystem.Resolve(gameRoot, profile.Ue4ss.RootRelativePath, "Mods");
        var enabled = Ue4ssModsFile.IsEnabled(Path.Combine(modsRoot, "mods.txt"), manifest.LoaderId!);
        var deployed = Directory.Exists(Path.Combine(modsRoot, manifest.LoaderId!));
        return enabled
            ? deployed ? ModActivationState.Enabled : ModActivationState.Broken
            : ModActivationState.Disabled;
    }

    private static ModActivationState GetPakState(
        GameProfile profile,
        string gameRoot,
        string packageDirectory,
        ModManifest manifest)
    {
        var deployments = PakModInstaller.GetDeploymentFiles(profile, gameRoot, packageDirectory, manifest);
        var deployedCount = deployments.Count(file => File.Exists(file.Destination));
        return deployedCount == 0
            ? ModActivationState.Disabled
            : deployedCount == deployments.Count ? ModActivationState.Enabled : ModActivationState.Broken;
    }

    private static void SetManagedEnabled(string packageDirectory, bool enabled)
    {
        var marker = Path.Combine(packageDirectory, RogueModLayout.DisabledMarkerFileName);
        if (enabled)
        {
            File.Delete(marker);
            return;
        }
        var temporary = marker + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, "Disabled by RogueMod." + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, marker, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void SetUe4ssEnabled(
        GameProfile profile,
        string gameRoot,
        InstalledModInfo installed,
        bool enabled)
    {
        var modsRoot = ModPackageFileSystem.Resolve(Path.GetFullPath(gameRoot), profile.Ue4ss.RootRelativePath, "Mods");
        Directory.CreateDirectory(modsRoot);
        var deployment = Path.Combine(modsRoot, installed.Manifest.LoaderId!);
        var createdDeployment = false;
        if (Directory.Exists(deployment))
        {
            var deploymentManifest = ModManifestLoader.Load(Path.Combine(deployment, "mod.json"));
            if (!deploymentManifest.Id.Equals(installed.Manifest.Id, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"UE4SS deployment '{deployment}' belongs to '{deploymentManifest.Id}', not '{installed.Manifest.Id}'.");
            }
        }
        if (enabled && !Directory.Exists(deployment))
        {
            var staging = deployment + $".stage-{Guid.NewGuid():N}";
            try
            {
                ModPackageFileSystem.CopyTree(installed.Directory, staging);
                Directory.Move(staging, deployment);
                createdDeployment = true;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        try
        {
            Ue4ssModsFile.SetEnabled(Path.Combine(modsRoot, "mods.txt"), installed.Manifest.LoaderId!, enabled);
        }
        catch
        {
            if (createdDeployment && Directory.Exists(deployment))
            {
                Directory.Delete(deployment, recursive: true);
            }
            throw;
        }
    }

    private static ModInstallResult FromManaged(ManagedModInstallResult result) =>
        new(result.Manifest, result.Destination, [], result.Replaced);

    private static ModInstallResult FromNative(NativeModInstallResult result) =>
        new(result.Manifest, result.Destination, [result.Deployment], result.Replaced);

    private static ModInstallResult FromLua(LuaModInstallResult result) =>
        new(result.Manifest, result.Destination, [result.Deployment], result.Replaced);

    private static ModInstallResult FromPak(PakModInstallResult result) =>
        new(result.Manifest, result.Destination, result.Deployments, result.Replaced);

    private static void ValidateModId(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId) || !ModIdPattern().IsMatch(modId))
        {
            throw new ArgumentException("Mod id must contain 3-64 lowercase letters, digits, '.', '_' or '-'.", nameof(modId));
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdPattern();
}

public enum ModActivationState
{
    Enabled,
    Disabled,
    Broken
}

public sealed record InstalledModInfo
{
    public required ModManifest Manifest { get; init; }

    public required string Directory { get; init; }

    public required ModActivationState State { get; init; }
}

public sealed record ModInstallResult(
    ModManifest Manifest,
    string Destination,
    IReadOnlyList<string> Deployments,
    bool Replaced);

public sealed record ModUpdateResult(
    string PreviousVersion,
    string CurrentVersion,
    ModInstallResult Installation,
    bool PreservedDisabledState);

public sealed record ModUninstallResult(
    ModManifest Manifest,
    string RemovedPackageDirectory,
    string? RemovedUe4ssDeployment,
    IReadOnlyList<string> RemovedPakDeployments);
