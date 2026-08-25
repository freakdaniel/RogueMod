using RogueMod.Core.Profiles;

namespace RogueMod.Core.Mods;

public sealed class LuaModInstaller
{
    public LuaModInstallResult Install(GameProfile profile, string gameRoot, string packageDirectory, bool replace = false)
    {
        var result = new Ue4ssModInstaller(ModKind.Lua, "Scripts/main.lua")
            .Install(profile, gameRoot, packageDirectory, replace);
        return new(result.Manifest, result.Destination, result.Deployment, result.ModsFile, result.Replaced);
    }
}

public sealed record LuaModInstallResult(
    ModManifest Manifest,
    string Destination,
    string Deployment,
    string ModsFile,
    bool Replaced);
