using System.Text;
using System.Text.RegularExpressions;

namespace RogueMod.Core.Authoring;

public sealed record ManagedModScaffoldOptions
{
    public required string ModId { get; init; }

    public required string ProjectName { get; init; }

    public required string DisplayName { get; init; }

    public required string OutputDirectory { get; init; }

    public string RogueModSdkVersion { get; init; } = "0.1.0";

    public string GameSdkVersion { get; init; } = "0.1.0";
}

public sealed record ManagedModScaffoldResult
{
    public required string OutputDirectory { get; init; }

    public required string SolutionPath { get; init; }

    public required string ProjectPath { get; init; }

    public required string PackageDirectory { get; init; }
}

public sealed partial class ManagedModScaffolder
{
    private static readonly TemplateFile[] TemplateFiles =
    [
        new("RogueMod.Templates.Managed.Solution", "RogueMod.ManagedMod.slnx"),
        new("RogueMod.Templates.Managed.DirectoryBuildProps", "Directory.Build.props"),
        new("RogueMod.Templates.Managed.DirectoryPackagesProps", "Directory.Packages.props"),
        new("RogueMod.Templates.Managed.GlobalJson", "global.json"),
        new("RogueMod.Templates.Managed.GitIgnore", ".gitignore"),
        new("RogueMod.Templates.Managed.Readme", "README.md"),
        new("RogueMod.Templates.Managed.Project", "src/RogueMod.ManagedMod/RogueMod.ManagedMod.csproj"),
        new("RogueMod.Templates.Managed.ModSource", "src/RogueMod.ManagedMod/Mod.cs"),
        new("RogueMod.Templates.Managed.Manifest", "src/RogueMod.ManagedMod/mod.json")
    ];

    public ManagedModScaffoldResult Create(ManagedModScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException($"Output path already exists: {outputDirectory}");
        }

        var parentDirectory = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new ArgumentException("The output directory must have a parent directory.", nameof(options));
        Directory.CreateDirectory(parentDirectory);

        var stagingDirectory = Path.Combine(parentDirectory, $".roguemod-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var templateFile in TemplateFiles)
            {
                var relativePath = ReplaceTokens(templateFile.RelativePath, options);
                var destination = Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(
                    destination,
                    ReplaceTokens(ReadTemplate(templateFile.ResourceName), options),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            Directory.Move(stagingDirectory, outputDirectory);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            throw;
        }

        var solutionPath = Path.Combine(outputDirectory, $"{options.ProjectName}.slnx");
        var projectPath = Path.Combine(outputDirectory, "src", options.ProjectName, $"{options.ProjectName}.csproj");
        return new ManagedModScaffoldResult
        {
            OutputDirectory = outputDirectory,
            SolutionPath = solutionPath,
            ProjectPath = projectPath,
            PackageDirectory = Path.Combine(outputDirectory, ".artifacts", "packages", "managed", "Release", options.ModId)
        };
    }

    public static string CreateDefaultProjectName(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var parts = Regex.Split(modId, "[^A-Za-z0-9]+")
            .Where(part => part.Length > 0)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..])
            .ToArray();
        var result = string.Concat(parts);
        return result.Length > 0 && !char.IsDigit(result[0]) ? result : $"Mod{result}";
    }

    private static void Validate(ManagedModScaffoldOptions options)
    {
        var manifest = new Mods.ModManifest(
            options.ModId,
            options.DisplayName,
            "0.1.0",
            Mods.ModKind.Managed,
            $"dlls/{options.ProjectName}.dll::{options.ProjectName}.Mod");
        var manifestErrors = manifest.Validate();
        if (manifestErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', manifestErrors), nameof(options));
        }

        if (!ProjectNamePattern().IsMatch(options.ProjectName)
            || options.ProjectName.Split('.').Any(segment => segment.Length == 0))
        {
            throw new ArgumentException(
                "ProjectName must be a valid dotted C# identifier using ASCII letters, digits and underscores.",
                nameof(options));
        }

        if (options.DisplayName.IndexOfAny(['\r', '\n', '"', '\\', '<', '>', '&']) >= 0)
        {
            throw new ArgumentException(
                "DisplayName cannot contain line breaks, quotes, backslashes, angle brackets or ampersands.",
                nameof(options));
        }

        ValidatePackageVersion(options.RogueModSdkVersion, nameof(options.RogueModSdkVersion));
        ValidatePackageVersion(options.GameSdkVersion, nameof(options.GameSdkVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
    }

    private static void ValidatePackageVersion(string version, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.Length > 64
            || !PackageVersionPattern().IsMatch(version))
        {
            throw new ArgumentException($"'{version}' is not a supported package version.", parameterName);
        }
    }

    private static string ReadTemplate(string resourceName)
    {
        using var stream = typeof(ManagedModScaffolder).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded managed mod template is missing: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReplaceTokens(string value, ManagedModScaffoldOptions options) => value
        .Replace("RogueMod.ManagedMod", options.ProjectName, StringComparison.Ordinal)
        .Replace("sample.managed-mod", options.ModId, StringComparison.Ordinal)
        .Replace("Sample managed mod", options.DisplayName, StringComparison.Ordinal)
        .Replace("ROGUEMOD_SDK_VERSION", options.RogueModSdkVersion, StringComparison.Ordinal)
        .Replace("DEADZONE_ROGUE_SDK_VERSION", options.GameSdkVersion, StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNamePattern();

    [GeneratedRegex("^[0-9]+(?:\\.[0-9]+){1,3}(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionPattern();

    private sealed record TemplateFile(string ResourceName, string RelativePath);
}
