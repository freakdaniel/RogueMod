using System.Text.Json;

namespace RogueMod.Core.Profiles;

public sealed record GameProfile(
    string Id,
    string DisplayName,
    uint SteamAppId,
    string UnrealEngineVersion,
    string ExecutableRelativePath,
    Ue4ssProfile Ue4ss)
{
    public string PakRootRelativePath { get; init; } = "Valhalla/Content/Paks/~mods";
}

public sealed record Ue4ssProfile(
    string RootRelativePath,
    string ProxyRelativePath,
    string LibraryRelativePath,
    string ProtonDllOverride,
    IReadOnlyList<CompatibilityFile> CompatibilityFiles)
{
    public Ue4ssEngineVersionOverride? EngineVersionOverride { get; init; }
}

public sealed record Ue4ssEngineVersionOverride(int MajorVersion, int MinorVersion);

public sealed record CompatibilityFile(
    string SourceRelativePath,
    string DestinationRelativePath,
    string NormalizedSha256);

public static class GameProfileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static GameProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        var profile = JsonSerializer.Deserialize<GameProfile>(stream, Options)
            ?? throw new InvalidDataException($"Profile '{path}' is empty.");

        Validate(profile, path);
        return profile;
    }

    private static void Validate(GameProfile profile, string path)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            throw new InvalidDataException($"Profile '{path}' must define id and displayName.");
        }

        if (Path.IsPathRooted(profile.ExecutableRelativePath))
        {
            throw new InvalidDataException("executableRelativePath must be relative to the game root.");
        }

        if (string.IsNullOrWhiteSpace(profile.PakRootRelativePath) || Path.IsPathRooted(profile.PakRootRelativePath))
        {
            throw new InvalidDataException("pakRootRelativePath must be a non-empty path relative to the game root.");
        }

        foreach (var file in profile.Ue4ss.CompatibilityFiles)
        {
            if (Path.IsPathRooted(file.DestinationRelativePath))
            {
                throw new InvalidDataException("Compatibility destinations must be relative to the game root.");
            }

            if (file.NormalizedSha256.Length != 64 || !file.NormalizedSha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"Invalid SHA-256 for '{file.DestinationRelativePath}'.");
            }
        }

        if (profile.Ue4ss.EngineVersionOverride is { } version
            && (version.MajorVersion <= 0 || version.MinorVersion < 0))
        {
            throw new InvalidDataException("UE4SS engineVersionOverride must contain a positive majorVersion and a non-negative minorVersion.");
        }
    }
}
