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
        var result = new Ue4ssModInstaller(ModKind.Native, "dlls/main.dll")
            .Install(profile, gameRoot, packageDirectory, replace);
        return new(result.Manifest, result.Destination, result.Deployment, result.ModsFile, result.Replaced);
    }
}

public sealed record NativeModInstallResult(
    ModManifest Manifest,
    string Destination,
    string Deployment,
    string ModsFile,
    bool Replaced);
