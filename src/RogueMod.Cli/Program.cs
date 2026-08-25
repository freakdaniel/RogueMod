using RogueMod.Core.Diagnostics;
using RogueMod.Core.Mods;
using RogueMod.Core.Profiles;
using RogueMod.Sdk;

return await RogueModCli.RunAsync(args);

internal static class RogueModCli
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return Task.FromResult(0);
        }

        try
        {
            var exitCode = args[0].ToLowerInvariant() switch
            {
                "diagnose" => RunDiagnose(args[1..]),
                "install-runtime" => RunInstallRuntime(args[1..]),
                "install-managed" => RunInstallManaged(args[1..]),
                "install-native" => RunInstallNative(args[1..]),
                "generate-sdk" => RunGenerateSdk(args[1..]),
                _ => UnknownCommand(args[0])
            };
            return Task.FromResult(exitCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return Task.FromResult(1);
        }
    }

    private static int RunInstallRuntime(string[] args)
    {
        var gameRoot = ReadOption(args, "--game")
            ?? throw new ArgumentException("install-runtime requires --game <directory>.");
        var packageDirectory = ReadOption(args, "--package")
            ?? throw new ArgumentException("install-runtime requires --package <directory>.");
        var profilePath = ReadOption(args, "--profile")
            ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");
        var result = new RogueModRuntimeInstaller().Install(
            GameProfileLoader.Load(profilePath), gameRoot, packageDirectory, HasFlag(args, "--replace"));
        Console.WriteLine($"Installed RogueMod runtime: {result.Destination}");
        if (result.MigratedFromLegacyLayout)
        {
            Console.WriteLine($"Migrated legacy runtime to: {RogueModLayout.LoaderModName}");
        }
        if (result.MigratedManagedModCount > 0)
        {
            Console.WriteLine($"Migrated managed mods to <game>/{RogueModLayout.GameModsDirectoryName}: {result.MigratedManagedModCount}");
        }
        Console.WriteLine($"Activated in: {result.ModsFile}");
        return 0;
    }

    private static int RunInstallManaged(string[] args)
    {
        var gameRoot = ReadOption(args, "--game")
            ?? throw new ArgumentException("install-managed requires --game <directory>.");
        var packageDirectory = ReadOption(args, "--package")
            ?? throw new ArgumentException("install-managed requires --package <directory>.");
        var profilePath = ReadOption(args, "--profile")
            ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");

        var profile = GameProfileLoader.Load(profilePath);
        var result = new ManagedModInstaller().Install(
            profile,
            gameRoot,
            packageDirectory,
            replace: HasFlag(args, "--replace"));
        Console.WriteLine($"Installed managed mod: {result.Manifest.Id} {result.Manifest.Version}");
        Console.WriteLine($"Destination: {result.Destination}");
        return 0;
    }

    private static int RunInstallNative(string[] args)
    {
        var gameRoot = ReadOption(args, "--game")
            ?? throw new ArgumentException("install-native requires --game <directory>.");
        var packageDirectory = ReadOption(args, "--package")
            ?? throw new ArgumentException("install-native requires --package <directory>.");
        var profilePath = ReadOption(args, "--profile")
            ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");

        var profile = GameProfileLoader.Load(profilePath);
        var result = new NativeModInstaller().Install(
            profile,
            gameRoot,
            packageDirectory,
            replace: HasFlag(args, "--replace"));
        Console.WriteLine($"Installed native mod: {result.Manifest.Id} {result.Manifest.Version}");
        Console.WriteLine($"Package store: {result.Destination}");
        Console.WriteLine($"UE4SS deployment: {result.Deployment}");
        Console.WriteLine($"Activated in: {result.ModsFile}");
        return 0;
    }

    private static int RunDiagnose(string[] args)
    {
        var gameRoot = ReadOption(args, "--game")
            ?? throw new ArgumentException("diagnose requires --game <directory>.");
        var profilePath = ReadOption(args, "--profile")
            ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");

        var profile = GameProfileLoader.Load(profilePath);
        var report = new InstallationInspector().Inspect(profile, gameRoot);

        Console.WriteLine($"{report.Profile.DisplayName} ({report.Profile.Id})");
        Console.WriteLine($"Game root: {report.GameRoot}");
        Console.WriteLine($"Unreal Engine: {report.Profile.UnrealEngineVersion}");
        Console.WriteLine();

        foreach (var check in report.Checks)
        {
            Console.WriteLine($"[{FormatStatus(check.Status)}] {check.Id}: {check.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(report.IsCompatible ? "Result: compatible" : "Result: incompatible");
        if (OperatingSystem.IsLinux())
        {
            Console.WriteLine($"Proton activation: WINEDLLOVERRIDES={profile.Ue4ss.ProtonDllOverride}");
        }

        return report.IsCompatible ? 0 : 2;
    }

    private static int RunGenerateSdk(string[] args)
    {
        var output = ReadOption(args, "--output")
            ?? throw new ArgumentException("generate-sdk requires --output <directory>.");
        var rootNamespace = ReadOption(args, "--namespace") ?? "DeadzoneRogue.Sdk";
        var jmap = ReadOption(args, "--jmap");
        if (jmap is null)
        {
            var gameRoot = ReadOption(args, "--game")
                ?? throw new ArgumentException("generate-sdk requires either --jmap <file> or --game <directory>.");
            var profilePath = ReadOption(args, "--profile")
                ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");
            var profile = GameProfileLoader.Load(profilePath);
            var ue4ssRoot = Path.GetFullPath(Path.Combine(gameRoot, profile.Ue4ss.RootRelativePath));
            jmap = Directory.Exists(ue4ssRoot)
                ? Directory.EnumerateFiles(ue4ssRoot, "*.jmap", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (jmap is null)
            {
                throw new FileNotFoundException(
                    $"No .jmap dump was found in '{ue4ssRoot}'. Generate one in UE4SS first (Ctrl+Numpad5).",
                    ue4ssRoot);
            }
        }

        var abstractionsProject = ReadOption(args, "--abstractions-project")
            ?? FindRepositoryFile("src/RogueMod.Abstractions/RogueMod.Abstractions.csproj");
        var model = new JMapImporter().Import(jmap);
        var result = new CSharpSdkGenerator().Generate(model, output, rootNamespace, abstractionsProject);
        Console.WriteLine($"Generated C# SDK from: {Path.GetFullPath(jmap)}");
        Console.WriteLine($"Types: {result.TypeCount}");
        Console.WriteLine($"Source: {result.SourcePath}");
        Console.WriteLine($"Manifest: {result.ManifestPath}");
        Console.WriteLine($"Project: {result.ProjectPath}");
        return 0;
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 64;
    }

    private static string FormatStatus(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pass => "PASS",
        DiagnosticStatus.Warning => "WARN",
        DiagnosticStatus.Fail => "FAIL",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static void PrintUsage()
    {
        Console.WriteLine("RogueMod CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  roguemod diagnose --game <directory> [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-runtime --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-managed --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-native --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod generate-sdk (--jmap <file> | --game <directory>) --output <directory> [--namespace <name>] [--abstractions-project <file>] [--profile <profile.json>]");
    }
}
