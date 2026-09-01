using RogueMod.Core.Authoring;
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
                "new" => RunNew(args[1..]),
                "install" => RunInstall(args[1..]),
                "list" => RunList(args[1..]),
                "uninstall" => RunUninstall(args[1..]),
                "enable" => RunSetEnabled(args[1..], enabled: true),
                "disable" => RunSetEnabled(args[1..], enabled: false),
                "update" => RunUpdate(args[1..]),
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

    private static int RunInstall(string[] args)
    {
        var gameRoot = RequireOption(args, "install", "--game", "directory");
        var packageDirectory = RequireOption(args, "install", "--package", "directory");
        var result = new ModManager().Install(
            LoadProfile(args),
            gameRoot,
            packageDirectory,
            replace: HasFlag(args, "--replace"));
        Console.WriteLine($"Installed {result.Manifest.Kind.ToString().ToLowerInvariant()} mod: {result.Manifest.Id} {result.Manifest.Version}");
        Console.WriteLine($"Package store: {result.Destination}");
        foreach (var deployment in result.Deployments)
        {
            Console.WriteLine($"Deployment: {deployment}");
        }
        return 0;
    }

    private static int RunList(string[] args)
    {
        var gameRoot = RequireOption(args, "list", "--game", "directory");
        var mods = new ModManager().List(LoadProfile(args), gameRoot);
        if (mods.Count == 0)
        {
            Console.WriteLine("No RogueMod packages are installed.");
            return 0;
        }
        Console.WriteLine("STATE     KIND     VERSION          ID");
        foreach (var mod in mods)
        {
            Console.WriteLine($"{mod.State,-9} {mod.Manifest.Kind,-8} {mod.Manifest.Version,-16} {mod.Manifest.Id}");
        }
        return 0;
    }

    private static int RunUninstall(string[] args)
    {
        var gameRoot = RequireOption(args, "uninstall", "--game", "directory");
        var modId = RequireOption(args, "uninstall", "--id", "package-id");
        var result = new ModManager().Uninstall(LoadProfile(args), gameRoot, modId);
        Console.WriteLine($"Uninstalled {result.Manifest.Kind.ToString().ToLowerInvariant()} mod: {result.Manifest.Id}");
        return 0;
    }

    private static int RunSetEnabled(string[] args, bool enabled)
    {
        var command = enabled ? "enable" : "disable";
        var gameRoot = RequireOption(args, command, "--game", "directory");
        var modId = RequireOption(args, command, "--id", "package-id");
        var result = new ModManager().SetEnabled(LoadProfile(args), gameRoot, modId, enabled);
        Console.WriteLine($"{(enabled ? "Enabled" : "Disabled")} {result.Manifest.Kind.ToString().ToLowerInvariant()} mod: {result.Manifest.Id}");
        return 0;
    }

    private static int RunUpdate(string[] args)
    {
        var gameRoot = RequireOption(args, "update", "--game", "directory");
        var packageDirectory = RequireOption(args, "update", "--package", "directory");
        var result = new ModManager().Update(LoadProfile(args), gameRoot, packageDirectory);
        Console.WriteLine($"Updated mod: {result.Installation.Manifest.Id} {result.PreviousVersion} -> {result.CurrentVersion}");
        if (result.PreservedDisabledState)
        {
            Console.WriteLine("State preserved: disabled");
        }
        return 0;
    }

    private static int RunNew(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintNewUsage();
            return 0;
        }

        var templateName = args[0].ToLowerInvariant();
        if (templateName is not ("managed" or "lua" or "native" or "pak"))
        {
            throw new ArgumentException($"Unsupported mod template '{args[0]}'. Available templates: managed, lua, native, pak.");
        }

        var templateArgs = args[1..];
        if (HasFlag(templateArgs, "-h") || HasFlag(templateArgs, "--help"))
        {
            PrintNewUsage();
            return 0;
        }

        var modId = ReadOption(templateArgs, "--id")
            ?? throw new ArgumentException($"new {templateName} requires --id <package-id>.");
        var projectName = ReadOption(templateArgs, "--name")
            ?? ManagedModScaffolder.CreateDefaultProjectName(modId);
        var displayName = ReadOption(templateArgs, "--display-name") ?? projectName;
        var output = ReadOption(templateArgs, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, projectName);

        if (templateName == "lua")
        {
            var luaResult = new LuaModScaffolder().Create(new LuaModScaffoldOptions
            {
                ModId = modId,
                ProjectName = projectName,
                DisplayName = displayName,
                LoaderId = ReadOption(templateArgs, "--loader-id")
                    ?? LuaModScaffolder.CreateDefaultLoaderId(projectName),
                OutputDirectory = output
            });
            Console.WriteLine($"Created Lua mod: {luaResult.OutputDirectory}");
            Console.WriteLine("Next:");
            Console.WriteLine($"  cd \"{luaResult.OutputDirectory}\"");
            Console.WriteLine("  roguemod install --game '<path to Deadzone Rogue>' --package . --replace");
            Console.WriteLine("Scripts/main.lua is the entry point; the UE4SS Lua API provides the reflection layer.");
            return 0;
        }

        if (templateName == "native")
        {
            var nativeResult = new NativeModScaffolder().Create(new NativeModScaffoldOptions
            {
                ModId = modId,
                ProjectName = projectName,
                DisplayName = displayName,
                LoaderId = ReadOption(templateArgs, "--loader-id")
                    ?? NativeModScaffolder.CreateDefaultLoaderId(projectName),
                OutputDirectory = output
            });
            Console.WriteLine($"Created native mod: {nativeResult.OutputDirectory}");
            Console.WriteLine("Next:");
            Console.WriteLine($"  cd \"{nativeResult.OutputDirectory}\"");
            Console.WriteLine("  Set ROGUEMOD_NATIVE_INCLUDE_DIR to the RogueMod.Sdk build/native/include directory.");
            Console.WriteLine("  cmake -S . -B .build -A x64");
            Console.WriteLine("  cmake --build .build --config Release --target PackageRogueNativeMod");
            Console.WriteLine($"  roguemod install --game '<path to Deadzone Rogue>' --package \"{nativeResult.PackageDirectory}\" --replace");
            return 0;
        }

        if (templateName == "pak")
        {
            var pakResult = new PakModScaffolder().Create(new PakModScaffoldOptions
            {
                ModId = modId,
                ProjectName = projectName,
                DisplayName = displayName,
                OutputDirectory = output
            });
            Console.WriteLine($"Created pak mod: {pakResult.OutputDirectory}");
            Console.WriteLine("Next:");
            Console.WriteLine($"  cd \"{pakResult.OutputDirectory}\"");
            Console.WriteLine($"  Pack the payload to {Path.GetFileName(pakResult.PakEntryPointPath)} with repak or UnrealPak (see README.md).");
            Console.WriteLine("  roguemod install --game '<path to Deadzone Rogue>' --package . --replace");
            return 0;
        }

        var result = new ManagedModScaffolder().Create(new ManagedModScaffoldOptions
        {
            ModId = modId,
            ProjectName = projectName,
            DisplayName = displayName,
            OutputDirectory = output,
            RogueModSdkVersion = ReadOption(templateArgs, "--sdk-version") ?? "0.1.0",
            GameSdkVersion = ReadOption(templateArgs, "--game-sdk-version") ?? "0.1.0"
        });

        Console.WriteLine($"Created managed mod: {result.OutputDirectory}");
        Console.WriteLine($"Solution: {result.SolutionPath}");
        Console.WriteLine("Next:");
        Console.WriteLine($"  cd \"{result.OutputDirectory}\"");
        Console.WriteLine("  dotnet restore");
        Console.WriteLine("  dotnet build -c Release -t:PackageRogueMod");
        Console.WriteLine($"Package: {result.PackageDirectory}");
        return 0;
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
        if (result.BridgeDeployment is not null)
        {
            Console.WriteLine($"UE4SS bridge deployment: {result.BridgeDeployment}");
        }
        if (result.MigratedFromLegacyLayout)
        {
            Console.WriteLine($"Migrated legacy runtime files to: {result.Destination}");
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
        var packageId = ReadOption(args, "--package-id") ?? "DeadzoneRogue.Sdk";
        var packageVersion = ReadOption(args, "--package-version") ?? "0.1.0";
        var rogueModVersion = ReadOption(args, "--roguemod-version") ?? "0.1.0";
        var gameVersion = ReadOption(args, "--game-version");
        var jmap = ReadOption(args, "--jmap");
        if (jmap is null)
        {
            var gameRoot = ReadOption(args, "--game")
                ?? throw new ArgumentException("generate-sdk requires either --jmap <file> or --game <directory>.");
            var profilePath = ReadOption(args, "--profile")
                ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");
            var profile = GameProfileLoader.Load(profilePath);
            if (gameVersion is null)
            {
                var executable = Path.GetFullPath(Path.Combine(gameRoot, profile.ExecutableRelativePath));
                gameVersion = File.Exists(executable)
                    ? System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion
                    : null;
            }
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

        var requestedAbstractionsProject = ReadOption(args, "--abstractions-project");
        if (HasFlag(args, "--standalone") && requestedAbstractionsProject is not null)
        {
            throw new ArgumentException("generate-sdk cannot combine --standalone with --abstractions-project.");
        }
        var abstractionsProject = HasFlag(args, "--standalone")
            ? null
            : requestedAbstractionsProject ?? FindRepositoryFile("src/RogueMod.Abstractions/RogueMod.Abstractions.csproj");
        var model = new JMapImporter().Import(jmap);
        var packageMetadata = new CSharpSdkPackageMetadata(packageId, packageVersion, rogueModVersion, gameVersion);
        var result = new CSharpSdkGenerator().Generate(model, output, rootNamespace, abstractionsProject, packageMetadata);
        Console.WriteLine($"Generated C# SDK from: {Path.GetFullPath(jmap)}");
        Console.WriteLine($"Package: {packageMetadata.PackageId} {packageMetadata.PackageVersion}");
        Console.WriteLine($"Game version: {packageMetadata.GameVersion ?? "not specified"}");
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

    private static string RequireOption(string[] args, string command, string option, string valueName) =>
        ReadOption(args, option)
        ?? throw new ArgumentException($"{command} requires {option} <{valueName}>.");

    private static GameProfile LoadProfile(string[] args)
    {
        var profilePath = ReadOption(args, "--profile")
            ?? Path.Combine(AppContext.BaseDirectory, "profiles", "deadzone-rogue.json");
        return GameProfileLoader.Load(profilePath);
    }

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
        Console.WriteLine("  roguemod new managed --id <package-id> [--name <project-name>] [--display-name <name>] [--output <directory>] [--sdk-version <version>] [--game-sdk-version <version>]");
        Console.WriteLine("  roguemod new lua --id <package-id> [--name <project-name>] [--display-name <name>] [--loader-id <name>] [--output <directory>]");
        Console.WriteLine("  roguemod new native --id <package-id> [--name <project-name>] [--display-name <name>] [--loader-id <name>] [--output <directory>]");
        Console.WriteLine("  roguemod new pak --id <package-id> [--name <project-name>] [--display-name <name>] [--output <directory>]");
        Console.WriteLine("  roguemod install --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod list --game <directory> [--profile <profile.json>]");
        Console.WriteLine("  roguemod uninstall --game <directory> --id <package-id> [--profile <profile.json>]");
        Console.WriteLine("  roguemod enable --game <directory> --id <package-id> [--profile <profile.json>]");
        Console.WriteLine("  roguemod disable --game <directory> --id <package-id> [--profile <profile.json>]");
        Console.WriteLine("  roguemod update --game <directory> --package <directory> [--profile <profile.json>]");
        Console.WriteLine("  roguemod diagnose --game <directory> [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-runtime --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-managed --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod install-native --game <directory> --package <directory> [--replace] [--profile <profile.json>]");
        Console.WriteLine("  roguemod generate-sdk (--jmap <file> | --game <directory>) --output <directory> [--namespace <name>] [--package-id <id>] [--package-version <version>] [--roguemod-version <version>] [--game-version <version>] [--standalone | --abstractions-project <file>] [--profile <profile.json>]");
    }

    private static void PrintNewUsage()
    {
        Console.WriteLine("Create a RogueMod project:");
        Console.WriteLine("  roguemod new managed --id <package-id> [options]");
        Console.WriteLine("  roguemod new lua --id <package-id> [options]");
        Console.WriteLine("  roguemod new native --id <package-id> [options]");
        Console.WriteLine("  roguemod new pak --id <package-id> [options]");
        Console.WriteLine();
        Console.WriteLine("Managed options:");
        Console.WriteLine("  --name <project-name>        C# project and namespace name; derived from the id by default");
        Console.WriteLine("  --display-name <name>        Human-readable mod name; defaults to the project name");
        Console.WriteLine("  --output <directory>         New output directory; defaults to ./<project-name>");
        Console.WriteLine("  --sdk-version <version>      RogueMod.Sdk version; defaults to 0.1.0");
        Console.WriteLine("  --game-sdk-version <version> DeadzoneRogue.Sdk version; defaults to 0.1.0");
        Console.WriteLine();
        Console.WriteLine("Lua options:");
        Console.WriteLine("  --name <project-name>        Project directory name; derived from the id by default");
        Console.WriteLine("  --display-name <name>        Human-readable mod name; defaults to the project name");
        Console.WriteLine("  --loader-id <name>           UE4SS loader directory name; derived from the project name by default");
        Console.WriteLine("  --output <directory>         New output directory; defaults to ./<project-name>");
        Console.WriteLine();
        Console.WriteLine("Native options:");
        Console.WriteLine("  --name <project-name>        Project directory name; derived from the id by default");
        Console.WriteLine("  --display-name <name>        Human-readable mod name; defaults to the project name");
        Console.WriteLine("  --loader-id <name>           UE4SS loader directory and C++ mod class name; derived from the project name by default");
        Console.WriteLine("  --output <directory>         New output directory; defaults to ./<project-name>");
        Console.WriteLine();
        Console.WriteLine("Pak options:");
        Console.WriteLine("  --name <project-name>        Project directory name; derived from the id by default");
        Console.WriteLine("  --display-name <name>        Human-readable mod name; defaults to the project name");
        Console.WriteLine("  --output <directory>         New output directory; defaults to ./<project-name>");
    }
}
