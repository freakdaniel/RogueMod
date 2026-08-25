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
    [JsonConstructor]
    public ModManifest(
        string id,
        string name,
        string version,
        ModKind kind,
        string entryPoint,
        IReadOnlyList<string>? dependencies,
        string? loaderId,
        string? description,
        string? icon,
        IReadOnlyList<string>? images,
        string? defaultLanguage,
        IReadOnlyList<string>? supportedLanguages,
        IReadOnlyDictionary<string, ModLocalization>? localizations)
        : this(id, name, version, kind, entryPoint, dependencies, loaderId)
    {
        Description = description;
        Icon = icon;
        Images = images;
        DefaultLanguage = defaultLanguage ?? ModLanguages.English;
        SupportedLanguages = supportedLanguages;
        Localizations = localizations;
    }

    public string? Description { get; init; }

    public string? Icon { get; init; }

    public IReadOnlyList<string>? Images { get; init; }

    public string DefaultLanguage { get; init; } = ModLanguages.English;

    public IReadOnlyList<string>? SupportedLanguages { get; init; }

    public IReadOnlyDictionary<string, ModLocalization>? Localizations { get; init; }

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

        if (Kind is ModKind.Native or ModKind.Lua)
        {
            if (string.IsNullOrWhiteSpace(LoaderId) || !NativeLoaderIdPattern().IsMatch(LoaderId))
            {
                errors.Add($"{Kind.ToString().ToLowerInvariant()} loaderId must contain 3-64 ASCII letters, digits or '_' and start with a letter.");
            }
        }
        else if (LoaderId is not null)
        {
            errors.Add("loaderId is only valid for native and lua mods.");
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

        ValidateDescription(Description, "description", errors);
        ValidateImagePath(Icon, "icon", errors);
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in Images ?? [])
        {
            ValidateImagePath(image, "images entry", errors);
            if (!string.IsNullOrWhiteSpace(image) && !seenImages.Add(image.Replace('\\', '/')))
            {
                errors.Add($"image '{image}' is duplicated.");
            }
        }

        if (!ModLanguages.IsKnown(DefaultLanguage))
        {
            errors.Add($"defaultLanguage '{DefaultLanguage}' is not supported by Deadzone: Rogue.");
        }

        var seenLanguages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in SupportedLanguages ?? [])
        {
            if (!ModLanguages.IsKnown(language))
            {
                errors.Add($"supported language '{language}' is not supported by Deadzone: Rogue.");
            }
            else if (!seenLanguages.Add(language))
            {
                errors.Add($"supported language '{language}' is duplicated.");
            }
        }
        if (SupportedLanguages is { Count: 0 })
        {
            errors.Add("supportedLanguages cannot be empty when specified.");
        }
        if (SupportedLanguages is not null && !seenLanguages.Contains(DefaultLanguage))
        {
            errors.Add("supportedLanguages must include defaultLanguage.");
        }

        if (Localizations is not null && SupportedLanguages is null)
        {
            errors.Add("supportedLanguages is required when localizations are specified.");
        }
        foreach (var (language, localization) in Localizations
                     ?? new Dictionary<string, ModLocalization>(StringComparer.Ordinal))
        {
            if (!ModLanguages.IsKnown(language))
            {
                errors.Add($"localization language '{language}' is not supported by Deadzone: Rogue.");
            }
            else if (SupportedLanguages is not null && !seenLanguages.Contains(language))
            {
                errors.Add($"localization language '{language}' is missing from supportedLanguages.");
            }
            if (localization is null)
            {
                errors.Add($"localization '{language}' is required.");
            }
            else
            {
                ValidateDescription(localization.Description, $"localizations.{language}.description", errors);
            }
        }

        return errors;
    }

    private static void ValidateDescription(string? description, string field, ICollection<string> errors)
    {
        if (description is not null && (string.IsNullOrWhiteSpace(description) || description.Length > 8_000))
        {
            errors.Add($"{field} must contain 1-8000 characters when specified.");
        }
    }

    private static void ValidateImagePath(string? path, string field, ICollection<string> errors)
    {
        if (path is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            errors.Add($"{field} must be a non-empty relative image path.");
            return;
        }
        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            errors.Add($"{field} must stay inside the mod package.");
            return;
        }
        if (!ImageExtensions.Contains(Path.GetExtension(path)))
        {
            errors.Add($"{field} must use PNG, JPEG, WebP, GIF or SVG.");
        }
    }

    private static readonly HashSet<string> ImageExtensions =
        new([".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NativeLoaderIdPattern();
}

public sealed record ModLocalization(string Description);

public sealed record ModLanguage(string Id, string DisplayName);

public static class ModLanguages
{
    public const string English = "en";

    public static IReadOnlyList<ModLanguage> All { get; } = Array.AsReadOnly<ModLanguage>(
    [
        new("en", "English"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("pl", "Polish"),
        new("pt-BR", "Portuguese (Brazil)"),
        new("ru", "Russian"),
        new("zh-Hans", "Simplified Chinese"),
        new("es-419", "Spanish (Latin America)"),
        new("es-ES", "Spanish (Spain)"),
        new("zh-Hant", "Traditional Chinese"),
        new("uk", "Ukrainian")
    ]);

    public static bool IsKnown(string? id) =>
        id is not null && All.Any(language => language.Id.Equals(id, StringComparison.Ordinal));
}

public static class ModManifestLoader
{
    public static ModManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize(stream, ModManifestJsonContext.Default.ModManifest)
            ?? throw new InvalidDataException($"Manifest '{path}' is empty.");
        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Invalid manifest '{path}': {string.Join(" ", errors)}");
        }

        return manifest;
    }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModManifest))]
internal sealed partial class ModManifestJsonContext : JsonSerializerContext;

public enum ModKind
{
    Managed,
    Native,
    Lua,
    Pak
}
