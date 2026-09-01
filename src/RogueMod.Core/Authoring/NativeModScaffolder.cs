using System.Text;
using System.Text.RegularExpressions;

namespace RogueMod.Core.Authoring;

public sealed record NativeModScaffoldOptions
{
    public required string ModId { get; init; }

    public required string ProjectName { get; init; }

    public required string DisplayName { get; init; }

    public required string LoaderId { get; init; }

    public required string OutputDirectory { get; init; }
}

public sealed record NativeModScaffoldResult
{
    public required string OutputDirectory { get; init; }

    public required string ManifestPath { get; init; }

    public required string SourcePath { get; init; }

    public required string CMakeListsPath { get; init; }

    public required string PackageDirectory { get; init; }
}

public sealed partial class NativeModScaffolder
{
    private static readonly TemplateFile[] TemplateFiles =
    [
        new("RogueMod.Templates.Native.CMakeLists", "CMakeLists.txt"),
        new("RogueMod.Templates.Native.GitIgnore", ".gitignore"),
        new("RogueMod.Templates.Native.Readme", "README.md"),
        new("RogueMod.Templates.Native.Source", "src/RogueMod.NativeMod.cpp"),
        new("RogueMod.Templates.Native.Manifest", "mod.json")
    ];

    public NativeModScaffoldResult Create(NativeModScaffoldOptions options)
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

        return new NativeModScaffoldResult
        {
            OutputDirectory = outputDirectory,
            ManifestPath = Path.Combine(outputDirectory, "mod.json"),
            SourcePath = Path.Combine(outputDirectory, "src", $"{options.ProjectName}.cpp"),
            CMakeListsPath = Path.Combine(outputDirectory, "CMakeLists.txt"),
            PackageDirectory = Path.Combine(outputDirectory, ".artifacts", "packages", "native", "Game__Shipping__Win64", options.ModId)
        };
    }

    public static string CreateDefaultProjectName(string modId) =>
        ManagedModScaffolder.CreateDefaultProjectName(modId);

    public static string CreateDefaultLoaderId(string projectName) =>
        LuaModScaffolder.CreateDefaultLoaderId(projectName);

    private static void Validate(NativeModScaffoldOptions options)
    {
        var manifest = new Mods.ModManifest(
            options.ModId,
            options.DisplayName,
            "0.1.0",
            Mods.ModKind.Native,
            "dlls/main.dll",
            LoaderId: options.LoaderId);
        var manifestErrors = manifest.Validate();
        if (manifestErrors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', manifestErrors), nameof(options));
        }

        if (!ProjectNamePattern().IsMatch(options.ProjectName)
            || options.ProjectName.Split('.').Any(segment => segment.Length == 0))
        {
            throw new ArgumentException(
                "ProjectName must be a valid dotted identifier using ASCII letters, digits and underscores.",
                nameof(options));
        }

        if (options.DisplayName.IndexOfAny(['\r', '\n', '"', '\\', '<', '>', '&']) >= 0)
        {
            throw new ArgumentException(
                "DisplayName cannot contain line breaks, quotes, backslashes, angle brackets or ampersands.",
                nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
    }

    private static string ReadTemplate(string resourceName)
    {
        using var stream = typeof(NativeModScaffolder).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded native mod template is missing: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReplaceTokens(string value, NativeModScaffoldOptions options) => value
        .Replace("RogueMod.NativeMod", options.ProjectName, StringComparison.Ordinal)
        .Replace("sample.native-mod", options.ModId, StringComparison.Ordinal)
        .Replace("Sample native mod", options.DisplayName, StringComparison.Ordinal)
        .Replace("SampleNative", options.LoaderId, StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNamePattern();

    private sealed record TemplateFile(string ResourceName, string RelativePath);
}
