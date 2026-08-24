using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RogueMod.Core.Mods;

public sealed partial record ModManifest(
    string Id,
    string Name,
    string Version,
    ModKind Kind,
    string EntryPoint,
    IReadOnlyList<string>? Dependencies = null,
    string? LoaderId = null)
{
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id) || !ModIdPattern().IsMatch(Id))
        {
            errors.Add("id must contain 3-64 lowercase letters, digits, '.', '_' or '-'.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("name is required.");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            errors.Add("version is required.");
        }

        if (string.IsNullOrWhiteSpace(EntryPoint) || Path.IsPathRooted(EntryPoint))
        {
            errors.Add("entryPoint must be a non-empty relative path or managed type name.");
        }

        if (Kind == ModKind.Native)
        {
            if (string.IsNullOrWhiteSpace(LoaderId) || !NativeLoaderIdPattern().IsMatch(LoaderId))
            {
                errors.Add("native loaderId must contain 3-64 ASCII letters, digits or '_' and start with a letter.");
            }
        }
        else if (LoaderId is not null)
        {
            errors.Add("loaderId is only valid for native mods.");
        }

        var seenDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in Dependencies ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency) || !ModIdPattern().IsMatch(dependency))
            {
                errors.Add($"dependency id '{dependency}' is invalid.");
            }
            else if (dependency.Equals(Id, StringComparison.Ordinal))
            {
                errors.Add("a mod cannot depend on itself.");
            }
            else if (!seenDependencies.Add(dependency))
            {
                errors.Add($"dependency '{dependency}' is duplicated.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NativeLoaderIdPattern();
}

public static class ModManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ModManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<ModManifest>(stream, Options)
            ?? throw new InvalidDataException($"Manifest '{path}' is empty.");
        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Invalid manifest '{path}': {string.Join(" ", errors)}");
        }

        return manifest;
    }
}

public enum ModKind
{
    Managed,
    Native,
    Lua,
    Pak
}
