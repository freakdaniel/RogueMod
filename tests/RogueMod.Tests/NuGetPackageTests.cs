using System.Diagnostics;
using System.IO.Compression;
using RogueMod.Abstractions;
using RogueMod.Core.Mods;
using RogueMod.Runtime;
using RogueMod.Sdk;
using Xunit;

namespace RogueMod.Tests;

public sealed class NuGetPackageTests
{
    [Fact]
    public async Task SdkPackageSupportsExternalManagedModAuthoring()
    {
        using var temporaryDirectory = new PackageTestDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var feed = Path.Combine(temporaryDirectory.Path, "feed");
        var consumer = Path.Combine(temporaryDirectory.Path, "consumer");
        var cliStarter = Path.Combine(temporaryDirectory.Path, "cli-starter");
        var templateStarter = Path.Combine(temporaryDirectory.Path, "template-starter");
        var packages = Path.Combine(temporaryDirectory.Path, "packages");
        var environment = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = packages
        };
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(consumer);

        await RunDotNet(
            repositoryRoot,
            null,
            "pack",
            Path.Combine(repositoryRoot, "src", "RogueMod.Abstractions", "RogueMod.Abstractions.csproj"),
            "--configuration",
            "Release",
            "--no-build",
            "--output",
            feed,
            "--nologo");
        await RunDotNet(
            repositoryRoot,
            null,
            "pack",
            Path.Combine(repositoryRoot, "src", "RogueMod.Sdk", "RogueMod.Sdk.csproj"),
            "--configuration",
            "Release",
            "--no-build",
            "--output",
            feed,
            "--nologo");

        var sdkPackage = Directory.GetFiles(feed, "RogueMod.Sdk.*.nupkg").Single();
        using (var archive = ZipFile.OpenRead(sdkPackage))
        {
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("README.md", entries);
            Assert.Contains("build/RogueMod.Sdk.props", entries);
            Assert.Contains("build/RogueMod.Sdk.targets", entries);
            Assert.Contains("build/native/include/RogueMod/NativeMod.hpp", entries);
            Assert.Contains("build/native/include/UE4SS/CppUserModBase.hpp", entries);
        }

        var gameSdkOutput = Path.Combine(temporaryDirectory.Path, "game-sdk");
        var gameSdkModel = new UnrealSdkModel(
            new UnrealSdkMetadata("package-test.jmap", new string('a', 64), 5, 6, "2026-08-25T00:00:00Z"),
            [
                new UnrealSdkType(
                    "/Script/PackageTest.TestActor",
                    "TestActor",
                    UnrealSdkTypeKind.Class,
                    null,
                    [],
                    [],
                    [])
            ]);
        var gameSdkMetadata = new CSharpSdkPackageMetadata(GameVersion: "1.4.2.0");
        var gameSdk = new CSharpSdkGenerator().Generate(
            gameSdkModel,
            gameSdkOutput,
            "DeadzoneRogue.Sdk",
            null,
            gameSdkMetadata);
        await RunDotNet(gameSdkOutput, environment, "restore", gameSdk.ProjectPath, "--source", feed, "--nologo");
        await RunDotNet(
            gameSdkOutput,
            environment,
            "pack",
            gameSdk.ProjectPath,
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            feed,
            "--nologo");

        var gameSdkPackage = Directory.GetFiles(feed, "DeadzoneRogue.Sdk.*.nupkg").Single();
        using (var archive = ZipFile.OpenRead(gameSdkPackage))
        {
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("README.md", entries);
            Assert.Contains("sdk/RogueMod.GameSdk.json", entries);
            Assert.Contains("lib/net10.0/DeadzoneRogue.Sdk.dll", entries);
        }

        var cliAssembly = Path.Combine(
            repositoryRoot,
            "src",
            "RogueMod.Cli",
            "bin",
            "Release",
            "net10.0",
            "roguemod.dll");
        Assert.True(File.Exists(cliAssembly), "The Release CLI assembly was not built before integration tests.");
        await RunDotNet(
            repositoryRoot,
            environment,
            cliAssembly,
            "new",
            "managed",
            "--id",
            "external.cli-starter",
            "--name",
            "External.CliStarter",
            "--display-name",
            "External CLI starter",
            "--output",
            cliStarter);
        var cliStarterProject = Path.Combine(cliStarter, "src", "External.CliStarter", "External.CliStarter.csproj");
        await RunDotNet(cliStarter, environment, "restore", cliStarterProject, "--source", feed, "--nologo");
        await RunDotNet(
            cliStarter,
            environment,
            "build",
            cliStarterProject,
            "--configuration",
            "Release",
            "--target",
            "PackageRogueMod",
            "--no-restore",
            "--nologo");
        var cliStarterPackage = Path.Combine(
            cliStarter,
            ".artifacts",
            "packages",
            "managed",
            "Release",
            "external.cli-starter");
        Assert.True(
            File.Exists(Path.Combine(cliStarterPackage, "mod.json")),
            "The CLI starter did not build a ready mod package.");
        var cliGame = Path.Combine(temporaryDirectory.Path, "cli-game");
        Directory.CreateDirectory(cliGame);
        await RunDotNet(repositoryRoot, environment, cliAssembly, "install", "--game", cliGame, "--package", cliStarterPackage);
        var cliList = await RunDotNet(repositoryRoot, environment, cliAssembly, "list", "--game", cliGame);
        Assert.Contains("external.cli-starter", cliList.StandardOutput);
        Assert.Contains("Enabled", cliList.StandardOutput);
        await RunDotNet(repositoryRoot, environment, cliAssembly, "disable", "--game", cliGame, "--id", "external.cli-starter");
        var cliDisabledList = await RunDotNet(repositoryRoot, environment, cliAssembly, "list", "--game", cliGame);
        Assert.Contains("Disabled", cliDisabledList.StandardOutput);
        await RunDotNet(repositoryRoot, environment, cliAssembly, "update", "--game", cliGame, "--package", cliStarterPackage);
        var cliUpdatedList = await RunDotNet(repositoryRoot, environment, cliAssembly, "list", "--game", cliGame);
        Assert.Contains("Disabled", cliUpdatedList.StandardOutput);
        await RunDotNet(repositoryRoot, environment, cliAssembly, "enable", "--game", cliGame, "--id", "external.cli-starter");
        await RunDotNet(repositoryRoot, environment, cliAssembly, "uninstall", "--game", cliGame, "--id", "external.cli-starter");
        var cliEmptyList = await RunDotNet(repositoryRoot, environment, cliAssembly, "list", "--game", cliGame);
        Assert.Contains("No RogueMod packages are installed.", cliEmptyList.StandardOutput);

        await RunDotNet(
            repositoryRoot,
            environment,
            "pack",
            Path.Combine(repositoryRoot, "src", "RogueMod.Templates", "RogueMod.Templates.csproj"),
            "--configuration",
            "Release",
            "--output",
            feed,
            "--nologo");
        var templatePackage = Directory.GetFiles(feed, "RogueMod.Templates.*.nupkg").Single();
        using (var archive = ZipFile.OpenRead(templatePackage))
        {
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("content/RogueMod.ManagedMod/.template.config/template.json", entries);
            Assert.Contains("content/RogueMod.ManagedMod/gitignore.txt", entries);
        }

        var templateEnvironment = new Dictionary<string, string>(environment)
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(temporaryDirectory.Path, "dotnet-home"),
            ["DOTNET_NOLOGO"] = "1"
        };
        await RunDotNet(repositoryRoot, templateEnvironment, "new", "install", templatePackage);
        await RunDotNet(
            repositoryRoot,
            templateEnvironment,
            "new",
            "roguemod-managed",
            "--name",
            "External.TemplateStarter",
            "--mod-id",
            "external.template-starter",
            "--mod-name",
            "External template starter",
            "--output",
            templateStarter);
        Assert.True(File.Exists(Path.Combine(templateStarter, ".gitignore")), "dotnet new did not rename the gitignore template file.");
        var templateStarterProject = Path.Combine(
            templateStarter,
            "src",
            "External.TemplateStarter",
            "External.TemplateStarter.csproj");
        await RunDotNet(templateStarter, templateEnvironment, "restore", templateStarterProject, "--source", feed, "--nologo");
        await RunDotNet(
            templateStarter,
            templateEnvironment,
            "build",
            templateStarterProject,
            "--configuration",
            "Release",
            "--target",
            "PackageRogueMod",
            "--no-restore",
            "--nologo");
        Assert.True(
            File.Exists(Path.Combine(
                templateStarter,
                ".artifacts",
                "packages",
                "managed",
                "Release",
                "external.template-starter",
                "mod.json")),
            "The dotnet new starter did not build a ready mod package.");

        var runtimeDependency = Path.Combine(temporaryDirectory.Path, "runtime-dependency");
        Directory.CreateDirectory(runtimeDependency);
        await File.WriteAllTextAsync(Path.Combine(runtimeDependency, "External.RuntimeDependency.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>External.RuntimeDependency</PackageId>
                <Version>1.0.0</Version>
              </PropertyGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(runtimeDependency, "RuntimeMarker.cs"), """
            namespace External.RuntimeDependency;

            public static class RuntimeMarker
            {
                public static string Value => "runtime dependency loaded";
            }
            """, TestContext.Current.CancellationToken);
        await RunDotNet(
            runtimeDependency,
            environment,
            "pack",
            Path.Combine(runtimeDependency, "External.RuntimeDependency.csproj"),
            "--configuration",
            "Release",
            "--output",
            feed,
            "--nologo");

        var projectPath = Path.Combine(consumer, "ExternalMod.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RogueModModId>external.package-test</RogueModModId>
                <RogueModModName>External package test</RogueModModName>
                <RogueModEntryPoint>ExternalPackageTest.Mod</RogueModEntryPoint>
              </PropertyGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(consumer, "Mod.cs"), """
            using System.Threading;
            using System.Threading.Tasks;
            using DeadzoneRogue.Sdk;
            using External.RuntimeDependency;
            using RogueMod.Abstractions;

            namespace ExternalPackageTest;

            public sealed class Mod : IRogueMod
            {
                public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
                {
                    _ = context.Unreal.FindFirst<TestActor>();
                    _ = RuntimeMarker.Value;
                    return ValueTask.CompletedTask;
                }

                public ValueTask UnloadAsync(CancellationToken cancellationToken = default) =>
                    ValueTask.CompletedTask;
            }
            """, TestContext.Current.CancellationToken);

        await RunDotNet(
            consumer,
            environment,
            "add",
            projectPath,
            "package",
            "RogueMod.Sdk",
            "--version",
            "0.1.0",
            "--source",
            feed,
            "--no-restore");
        await RunDotNet(
            consumer,
            environment,
            "add",
            projectPath,
            "package",
            "DeadzoneRogue.Sdk",
            "--version",
            "0.1.0",
            "--source",
            feed,
            "--no-restore");
        await RunDotNet(
            consumer,
            environment,
            "add",
            projectPath,
            "package",
            "External.RuntimeDependency",
            "--version",
            "1.0.0",
            "--source",
            feed,
            "--no-restore");
        await RunDotNet(consumer, environment, "restore", projectPath, "--source", feed, "--nologo");
        await RunDotNet(
            consumer,
            environment,
            "build",
            projectPath,
            "--configuration",
            "Release",
            "--target",
            "PackageRogueMod",
            "--no-restore",
            "--nologo");

        var modPackage = Path.Combine(consumer, ".artifacts", "packages", "managed", "Release", "external.package-test");
        Assert.True(File.Exists(Path.Combine(modPackage, "mod.json")), "The imported PackageRogueMod target did not emit a manifest.");
        Assert.True(File.Exists(Path.Combine(modPackage, "dlls", "ExternalMod.dll")), "The imported PackageRogueMod target did not emit the mod assembly.");
        var buildOutput = Path.Combine(consumer, "bin", "Release", "net10.0");
        var buildAssemblies = Directory.GetFiles(buildOutput, "*.dll").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.DoesNotContain("DeadzoneRogue.Sdk.dll", buildAssemblies);
        var packagedAssemblies = Directory.GetFiles(Path.Combine(modPackage, "dlls"), "*.dll").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.DoesNotContain("DeadzoneRogue.Sdk.dll", packagedAssemblies);
        Assert.Contains("External.RuntimeDependency.dll", packagedAssemblies);

        var sharedAssemblyDirectory = Path.Combine(temporaryDirectory.Path, "runtime", "shared");
        Directory.CreateDirectory(sharedAssemblyDirectory);
        File.Copy(
            Path.Combine(gameSdkOutput, "bin", "Release", "net10.0", "DeadzoneRogue.Sdk.dll"),
            Path.Combine(sharedAssemblyDirectory, "DeadzoneRogue.Sdk.dll"));
        var manifest = new ModManifest(
            "external.package-test",
            "External package test",
            "0.1.0",
            ModKind.Managed,
            "dlls/ExternalMod.dll::ExternalPackageTest.Mod");
        await LoadAndUnloadExternalMod(
            manifest,
            modPackage,
            sharedAssemblyDirectory,
            TestContext.Current.CancellationToken);
        ForceCollectibleContextsToUnload();

        var propertyOutput = await RunDotNet(
            consumer,
            environment,
            "msbuild",
            projectPath,
            "-getProperty:RogueModNativeIncludeDir",
            "--nologo");
        var includeDirectory = propertyOutput.StandardOutput.Trim();
        Assert.True(Directory.Exists(includeDirectory), $"The SDK native include property does not resolve to a directory: {includeDirectory}");
        Assert.True(File.Exists(Path.Combine(includeDirectory, "RogueMod", "NativeMod.hpp")), "The SDK native include property does not resolve to the packaged headers.");
    }

    private static async Task<ProcessResult> RunDotNet(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        Assert.True(process.Start(), $"Could not start dotnet {string.Join(' ', arguments)}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var result = new ProcessResult(await standardOutput, await standardError);
        Assert.True(
            process.ExitCode == 0,
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}:{Environment.NewLine}{result.StandardOutput}{result.StandardError}");
        return result;
    }

    private static async Task LoadAndUnloadExternalMod(
        ModManifest manifest,
        string modPackage,
        string sharedAssemblyDirectory,
        CancellationToken cancellationToken)
    {
        await using var host = await ManagedModHost.LoadAsync(
            manifest,
            modPackage,
            new PackageTestContext(),
            sharedAssemblyDirectory,
            cancellationToken);
        Assert.True(host.IsLoaded, "The mod did not resolve the centrally installed typed game SDK.");
    }

    private static void ForceCollectibleContextsToUnload()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RogueMod.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RogueMod repository root.");
    }

    private sealed record ProcessResult(string StandardOutput, string StandardError);

    private sealed class PackageTestContext : IModContext
    {
        public string ModId => "external.package-test";

        public string GameProfileId => "deadzone-rogue-steam";

        public IModLogger Logger { get; } = new PackageTestLogger();

        public IUnrealReflection Unreal { get; } = new PackageTestReflection();
    }

    private sealed class PackageTestLogger : IModLogger
    {
        public void Log(ModLogLevel level, string message)
        {
        }
    }

    private sealed class PackageTestReflection : IUnrealReflection
    {
        public bool IsAvailable => true;

        public UnrealObjectHandle FindFirstOf(string className) => UnrealObjectHandle.Null;

        public bool IsValid(UnrealObjectHandle handle) => false;

        public UnrealObjectHandle GetClass(UnrealObjectHandle handle) => UnrealObjectHandle.Null;

        public string? GetPathName(UnrealObjectHandle handle) => null;
    }

    private sealed class PackageTestDirectory : IDisposable
    {
        public PackageTestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RogueMod.PackageTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
