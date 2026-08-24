using RogueMod.Abstractions;
using RogueMod.Core.Diagnostics;
using RogueMod.Core.Mods;
using RogueMod.Core.Profiles;
using RogueMod.Sdk;
using RogueMod.Runtime;
using RogueMod.Tests.Fixtures;
using RogueMod.Tests.Native;

var tests = new (string Name, Action Body)[]
{
    ("profile loads", ProfileLoads),
    ("normalized fingerprints ignore trailing whitespace", FingerprintsAreNormalized),
    ("inspector accepts complete installation", InspectorAcceptsCompleteInstallation),
    ("manifest rejects unsafe entry point", ManifestRejectsUnsafeEntryPoint),
    ("managed package manifest loads", ManagedPackageManifestLoads),
    ("managed package installs transactionally", ManagedPackageInstallsTransactionally),
    ("native package installs and activates transactionally", NativePackageInstallsAndActivatesTransactionally),
    ("runtime installs and activates transactionally", RuntimeInstallsAndActivatesTransactionally),
    ("native bootstrap validates ABI", NativeBootstrapValidatesAbi),
    ("managed mod loads and unloads", () => ManagedModLoadsAndUnloads().AsTask().GetAwaiter().GetResult()),
    ("jmap imports and generates typed sdk", JMapImportsAndGeneratesTypedSdk)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ProfileLoads()
{
    var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
    Assert(profile.SteamAppId == 3228590, "Unexpected Steam app id.");
    Assert(profile.Ue4ss.CompatibilityFiles.Count == 1, "Compatibility file is missing.");
}

static void FingerprintsAreNormalized()
{
    using var directory = new TemporaryDirectory();
    var first = Path.Combine(directory.Path, "first.ini");
    var second = Path.Combine(directory.Path, "second.ini");
    File.WriteAllText(first, "[Section]\r\nValue=1\r\n");
    File.WriteAllText(second, "[Section]\nValue=1\n\n  \n");
    Assert(FileFingerprint.ComputeNormalizedTextSha256(first) == FileFingerprint.ComputeNormalizedTextSha256(second), "Fingerprints differ.");
}

static void InspectorAcceptsCompleteInstallation()
{
    using var directory = new TemporaryDirectory();
    var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
    Touch(directory.Path, profile.ExecutableRelativePath);
    Touch(directory.Path, profile.Ue4ss.ProxyRelativePath);
    Touch(directory.Path, profile.Ue4ss.LibraryRelativePath);

    var compatibility = profile.Ue4ss.CompatibilityFiles.Single();
    var source = FindRepositoryFile(compatibility.SourceRelativePath);
    var destination = Combine(directory.Path, compatibility.DestinationRelativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(source, destination);

    var modsFile = Combine(directory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
    File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nRogueModBridge : 1\nHelloNativeMod : 1\n");

    var report = new InstallationInspector().Inspect(profile, directory.Path);
    Assert(report.IsCompatible, string.Join(Environment.NewLine, report.Checks.Where(check => check.Status == DiagnosticStatus.Fail)));
    Assert(report.Checks.Single(check => check.Id == "built-in-mods").Status == DiagnosticStatus.Pass,
        "Enabled RogueMod components were mistaken for bundled UE4SS mods.");
}

static void ManifestRejectsUnsafeEntryPoint()
{
    var manifest = new ModManifest("sample.mod", "Sample", "1.0.0", ModKind.Managed, "/absolute/mod.dll");
    Assert(manifest.Validate().Count > 0, "Unsafe manifest was accepted.");

    var selfDependent = new ModManifest(
        "sample.mod",
        "Sample",
        "1.0.0",
        ModKind.Managed,
        "mod.dll::Sample.Mod",
        ["sample.mod"]);
    Assert(selfDependent.Validate().Any(error => error.Contains("itself", StringComparison.Ordinal)), "Self dependency was accepted.");

    var nativeWithoutLoaderId = new ModManifest(
        "sample.native",
        "Sample native",
        "1.0.0",
        ModKind.Native,
        "dlls/main.dll");
    Assert(nativeWithoutLoaderId.Validate().Any(error => error.Contains("loaderId", StringComparison.Ordinal)),
        "Native manifest without loaderId was accepted.");
}

static void ManagedPackageManifestLoads()
{
    using var directory = new TemporaryDirectory();
    var path = Path.Combine(directory.Path, "mod.json");
    File.WriteAllText(path, """
        {"id":"sample.hello-managed","name":"Hello","version":"0.1.0","kind":"managed","entryPoint":"dlls/Hello.dll::Hello.Mod"}
        """);
    var manifest = ModManifestLoader.Load(path);
    Assert(manifest.Kind == ModKind.Managed, "Managed kind was not parsed.");
    Assert(manifest.EntryPoint == "dlls/Hello.dll::Hello.Mod", "Managed entry point changed.");
}

static void ManagedPackageInstallsTransactionally()
{
    using var gameDirectory = new TemporaryDirectory();
    using var packageDirectory = new TemporaryDirectory();
    var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
    var assemblyPath = typeof(TestManagedMod).Assembly.Location;
    var dllDirectory = Path.Combine(packageDirectory.Path, "dlls");
    Directory.CreateDirectory(dllDirectory);
    File.Copy(assemblyPath, Path.Combine(dllDirectory, Path.GetFileName(assemblyPath)));
    File.WriteAllText(Path.Combine(packageDirectory.Path, "mod.json"), $$"""
        {"id":"sample.mod","name":"Sample","version":"1.0.0","kind":"managed","entryPoint":"dlls/{{Path.GetFileName(assemblyPath)}}::{{typeof(TestManagedMod).FullName}}"}
        """);

    var installer = new ManagedModInstaller();
    var result = installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
    Assert(result.Destination == Path.Combine(gameDirectory.Path, "Mods", "sample.mod"),
        "Managed mod was not installed in the game-root Mods directory.");
    Assert(File.Exists(Path.Combine(result.Destination, "mod.json")), "Installed manifest is missing.");
    Assert(File.Exists(Path.Combine(result.Destination, "dlls", Path.GetFileName(assemblyPath))), "Installed assembly is missing.");

    var refusedReplacement = false;
    try
    {
        installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
    }
    catch (IOException)
    {
        refusedReplacement = true;
    }
    Assert(refusedReplacement, "Existing mod was replaced without explicit permission.");

    var replaced = installer.Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
    Assert(replaced.Replaced, "Explicit replacement was not reported.");
    var parent = Directory.GetParent(replaced.Destination)!;
    Assert(!parent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Staging directory was left behind.");
    Assert(!parent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Backup directory was left behind.");
}

static void RuntimeInstallsAndActivatesTransactionally()
{
    using var gameDirectory = new TemporaryDirectory();
    using var packageDirectory = new TemporaryDirectory();
    var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
    Touch(packageDirectory.Path, "dlls/main.dll");
    Touch(packageDirectory.Path, "runtime/managed/RogueMod.Runtime.dll");
    Touch(packageDirectory.Path, "runtime/managed/RogueMod.Runtime.runtimeconfig.json");
    Touch(packageDirectory.Path, "runtime/dotnet/host/fxr/10.0.10/hostfxr.dll");
    var modsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
    File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nKeybinds : 1\n");

    var result = new RogueModRuntimeInstaller().Install(profile, gameDirectory.Path, packageDirectory.Path);
    Assert(File.Exists(Path.Combine(result.Destination, "dlls", "main.dll")), "Runtime bridge was not installed.");
    var lines = File.ReadAllLines(modsFile);
    Assert(lines.Count(line => line == "RogueModBridge : 1") == 1, "Runtime was not activated exactly once.");
    Assert(Array.IndexOf(lines, "RogueModBridge : 1") < Array.IndexOf(lines, "Keybinds : 1"), "Runtime was inserted below Keybinds.");

    var legacyMod = Path.Combine(result.Destination, "managed-mods", "user.mod");
    Directory.CreateDirectory(Path.Combine(legacyMod, "dlls"));
    File.WriteAllText(Path.Combine(legacyMod, "mod.json"),
        """{"id":"user.mod","name":"User mod","version":"1.0.0","kind":"managed","entryPoint":"dlls/UserMod.dll::UserMod.Entry"}""");
    Touch(legacyMod, "dlls/UserMod.dll");
    var legacyDestination = Path.Combine(Path.GetDirectoryName(result.Destination)!, RogueModLayout.LegacyLoaderModName);
    Directory.Move(result.Destination, legacyDestination);
    File.WriteAllText(modsFile, "RogueMod : 1\nKeybinds : 1\n");
    var replaced = new RogueModRuntimeInstaller().Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
    Assert(replaced.MigratedFromLegacyLayout, "Legacy runtime layout migration was not reported.");
    Assert(replaced.MigratedManagedModCount == 1, "Legacy managed mod migration count is incorrect.");
    Assert(!Directory.Exists(legacyDestination), "Legacy runtime directory was left behind.");
    Assert(File.Exists(Path.Combine(gameDirectory.Path, "Mods", "user.mod", "mod.json")),
        "Runtime update did not migrate the managed mod to the game-root Mods directory.");
    Assert(!Directory.Exists(Path.Combine(replaced.Destination, "managed-mods")),
        "The new runtime still contains the legacy managed-mods directory.");
    Assert(File.ReadAllLines(modsFile).Contains("RogueModBridge : 1"), "Migrated runtime was not activated under its bridge name.");
    Assert(!File.ReadAllLines(modsFile).Any(line => line.StartsWith("RogueMod :", StringComparison.Ordinal)), "Legacy runtime activation was left behind.");
}

static void NativePackageInstallsAndActivatesTransactionally()
{
    using var gameDirectory = new TemporaryDirectory();
    using var packageDirectory = new TemporaryDirectory();
    var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
    Touch(packageDirectory.Path, "dlls/main.dll");
    File.WriteAllText(Path.Combine(packageDirectory.Path, "mod.json"), """
        {"id":"sample.native","name":"Sample native","version":"1.0.0","kind":"native","entryPoint":"dlls/main.dll","loaderId":"SampleNativeMod"}
        """);
    var modsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
    File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nSampleNativeMod : 0\nSampleNativeMod : 0\nKeybinds : 1\n");

    var installer = new NativeModInstaller();
    var result = installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
    Assert(File.Exists(Path.Combine(result.Destination, "dlls", "main.dll")), "Native entry DLL was not installed.");
    Assert(File.Exists(Path.Combine(result.Deployment, "dlls", "main.dll")), "Native entry DLL was not deployed to UE4SS.");
    var lines = File.ReadAllLines(modsFile);
    Assert(Path.GetFileName(result.Destination) == "sample.native", "Native package was not stored under its package id.");
    Assert(Path.GetFileName(result.Deployment) == "SampleNativeMod", "Native mod was not deployed under its UE4SS loaderId.");
    Assert(lines.Count(line => line == "SampleNativeMod : 1") == 1, "Native mod was not activated exactly once.");
    Assert(Array.IndexOf(lines, "SampleNativeMod : 1") < Array.IndexOf(lines, "Keybinds : 1"), "Native mod was activated below Keybinds.");

    var refusedReplacement = false;
    try
    {
        installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
    }
    catch (IOException)
    {
        refusedReplacement = true;
    }
    Assert(refusedReplacement, "Existing native mod was replaced without explicit permission.");

    var replaced = installer.Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
    Assert(replaced.Replaced, "Explicit native replacement was not reported.");
    var parent = Directory.GetParent(replaced.Destination)!;
    Assert(!parent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Native staging directory was left behind.");
    Assert(!parent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Native backup directory was left behind.");
    var deploymentParent = Directory.GetParent(replaced.Deployment)!;
    Assert(!deploymentParent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Native deployment staging directory was left behind.");
    Assert(!deploymentParent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Native deployment backup directory was left behind.");
}

static unsafe void NativeBootstrapValidatesAbi()
{
    using var directory = new TemporaryDirectory();
    Assert(sizeof(NativeBootstrapTestCallbacks.HostApi) == 120, "Managed ABI 9 host table has an unexpected size.");
    Assert(sizeof(NativeBootstrapTestCallbacks.NativeUnrealParameter) == 40, "Managed ABI 9 parameter has an unexpected size.");
    NativeBootstrapTestCallbacks.Messages.Clear();
    NativeBootstrapTestCallbacks.PropertyWritten = false;
    NativeBootstrapTestCallbacks.StringPropertyWritten = false;

    var assemblyPath = typeof(TestManagedMod).Assembly.Location;
    var modsRoot = Path.Combine(directory.Path, "Mods");
    var modDirectory = Path.Combine(modsRoot, "sample.mod");
    var dllDirectory = Path.Combine(modDirectory, "dlls");
    Directory.CreateDirectory(dllDirectory);
    File.Copy(assemblyPath, Path.Combine(dllDirectory, Path.GetFileName(assemblyPath)));
    File.WriteAllText(Path.Combine(modDirectory, "mod.json"), $$"""
        {"id":"sample.mod","name":"Sample","version":"1.0.0","kind":"managed","entryPoint":"dlls/{{Path.GetFileName(assemblyPath)}}::{{typeof(TestManagedMod).FullName}}"}
        """);

    var modRoot = directory.Path;
    var profileId = "deadzone-rogue-steam";
    fixed (char* modRootPointer = modRoot)
    fixed (char* profileIdPointer = profileId)
    fixed (char* modsRootPointer = modsRoot)
    {
        var api = new NativeBootstrapTestCallbacks.HostApi
        {
            Size = (uint)sizeof(NativeBootstrapTestCallbacks.HostApi),
            AbiVersion = 9,
            Log = &NativeBootstrapTestCallbacks.CaptureLog,
            ModRoot = modRootPointer,
            GameProfileId = profileIdPointer,
            UnrealIsAvailable = &NativeBootstrapTestCallbacks.UnrealIsAvailable,
            UnrealFindFirstOf = &NativeBootstrapTestCallbacks.UnrealFindFirstOf,
            UnrealIsValid = &NativeBootstrapTestCallbacks.UnrealIsValid,
            UnrealGetClass = &NativeBootstrapTestCallbacks.UnrealGetClass,
            UnrealGetPathName = &NativeBootstrapTestCallbacks.UnrealGetPathName,
            UnrealGetCapabilities = &NativeBootstrapTestCallbacks.UnrealGetCapabilities,
            UnrealInvokeZeroParameter = &NativeBootstrapTestCallbacks.UnrealInvokeZeroParameter,
            UnrealReadProperty = &NativeBootstrapTestCallbacks.UnrealReadProperty,
            UnrealWriteProperty = &NativeBootstrapTestCallbacks.UnrealWriteProperty,
            UnrealInvoke = &NativeBootstrapTestCallbacks.UnrealInvoke,
            GameModsRoot = modsRootPointer
        };

        delegate* unmanaged[Cdecl]<nint, int> initialize = &NativeBootstrap.Initialize;
        delegate* unmanaged[Cdecl]<int, int> dispatchGameEvent = &NativeBootstrap.DispatchGameEvent;
        delegate* unmanaged[Cdecl]<int> shutdown = &NativeBootstrap.Shutdown;
        Assert(initialize((nint)(&api)) == 0, "Native bootstrap rejected ABI version 9.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] loaded:sample.mod"), "Installed managed mod was not loaded.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] reflection:/Test/PlayerController"), "Native reflection ABI was not exposed to the managed mod.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] invoked:Pause"), "Generated-style zero-parameter UFunction wrapper was not invoked.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] marshalled:True:42"), "UFunction input/return/out values were not marshalled.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] strings:ReturnName:Output String"), "FString/FName input, return, and out values were not marshalled.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] text:Output Text"), "FText input and return values were not marshalled.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] array:4:5:6"), "TArray input and return values were not marshalled.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] struct:4:5:6"), "POD struct input and return values were not marshalled.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:SpawnLocation=7:8:9"), "Generated-style POD struct property was not read.");
        Assert(NativeBootstrapTestCallbacks.StructPropertyWritten, "Generated-style POD struct property was not written.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:bShouldPerformFullTickWhenPaused=True"), "Generated-style bool property was not read.");
        Assert(NativeBootstrapTestCallbacks.PropertyWritten, "Generated-style bool property was not written.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:PlayerName=Rogue"), "Generated-style FString property was not read.");
        Assert(NativeBootstrapTestCallbacks.StringPropertyWritten, "Generated-style FString property was not written.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:DisplayText=Display Text"), "Generated-style FText property was not read.");
        Assert(NativeBootstrapTestCallbacks.TextPropertyWritten, "Generated-style FText property was not written.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:Scores=7:8:9"), "Generated-style TArray property was not read.");
        Assert(NativeBootstrapTestCallbacks.ArrayPropertyWritten, "Generated-style TArray property was not written.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[ManagedRuntime] Managed runtime initialized. Loaded 1 mod(s)."), "Initialization was not logged.");
        Assert(dispatchGameEvent((int)ModGameEventKind.ProgramStarted) == 0, "Game event was rejected.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] event:ProgramStarted"), "Game event did not reach the managed mod.");
        Assert(dispatchGameEvent(999) == -2, "Unknown game event was accepted.");
        Assert(shutdown() == 0, "Native bootstrap shutdown failed.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] unloaded"), "Installed managed mod was not unloaded.");
        Assert(NativeBootstrapTestCallbacks.Messages.Contains("[ManagedRuntime] Managed runtime shut down."), "Shutdown was not logged.");

        api.AbiVersion = 999;
        Assert(initialize((nint)(&api)) == -2, "Unsupported ABI version was accepted.");
    }
}

static async ValueTask ManagedModLoadsAndUnloads()
{
    var logger = new TestLogger();
    var context = new TestModContext("sample.mod", "deadzone-rogue-steam", logger, new UnavailableUnrealReflection());
    var assemblyPath = typeof(TestManagedMod).Assembly.Location;
    var manifest = new ModManifest(
        "sample.mod",
        "Sample",
        "1.0.0",
        ModKind.Managed,
        $"{Path.GetFileName(assemblyPath)}::{typeof(TestManagedMod).FullName}");

    await using var host = await ManagedModHost.LoadAsync(manifest, Path.GetDirectoryName(assemblyPath)!, context);
    Assert(host.IsLoaded, "Managed mod was not loaded.");
    Assert(logger.Messages.Contains("loaded:sample.mod"), "Managed load callback was not invoked.");
    host.DispatchGameEvent(ModGameEventKind.UnrealInitialized);
    Assert(logger.Messages.Contains("event:UnrealInitialized"), "Managed game-event callback was not invoked.");

    await host.UnloadAsync();
    Assert(!host.IsLoaded, "Managed mod was not unloaded.");
    Assert(logger.Messages.Contains("unloaded"), "Managed unload callback was not invoked.");
}

static void JMapImportsAndGeneratesTypedSdk()
{
    using var directory = new TemporaryDirectory();
    var jmapPath = Path.Combine(directory.Path, "fixture.jmap");
    File.WriteAllText(jmapPath, """
        {
          "metadata": {
            "timestamp": "2026-08-24T00:00:00Z",
            "engine_version": { "major": 5, "minor": 6 }
          },
          "objects": {
            "/Script/CoreUObject.Vector": {
              "type": "ScriptStruct",
              "super_struct": null,
              "properties_size": 24,
              "min_alignment": 8,
              "struct_flags": "STRUCT_IsPlainOldData | STRUCT_NoDestructor | STRUCT_ZeroConstructor",
              "children": [],
              "properties": [
                { "name": "X", "type": "DoubleProperty", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Y", "type": "DoubleProperty", "offset": 8, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Z", "type": "DoubleProperty", "offset": 16, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Script/Engine.Actor": {
              "type": "Class",
              "super_struct": null,
              "children": [],
              "properties": []
            },
            "/Game/Test.BP_Player_C": {
              "type": "Class",
              "super_struct": "/Script/Engine.Actor",
              "children": ["/Game/Test.BP_Player_C:SetHealth", "/Game/Test.BP_Player_C:GetHealth", "/Game/Test.BP_Player_C:SetPlayerName", "/Game/Test.BP_Player_C:SetLocation", "/Game/Test.BP_Player_C:GetLocation", "/Game/Test.BP_Player_C:EchoText", "/Game/Test.BP_Player_C:EchoNumbers"],
              "properties": [
                { "name": "Health", "type": "FloatProperty", "offset": 256, "array_dim": 1, "size": 4, "flags": "CPF_Edit | CPF_BlueprintVisible" },
                { "name": "Target", "type": "ObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 264, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" },
                { "name": "PlayerName", "type": "StrProperty", "offset": 272, "array_dim": 1, "size": 16, "flags": "CPF_BlueprintVisible" },
                { "name": "Mode", "type": "NameProperty", "offset": 288, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" },
                { "name": "DisplayText", "type": "TextProperty", "offset": 296, "array_dim": 1, "size": 16, "flags": "CPF_BlueprintVisible" },
                { "name": "Location", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 320, "array_dim": 1, "size": 24, "flags": "CPF_BlueprintVisible | CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Scores", "type": "ArrayProperty", "offset": 344, "array_dim": 1, "size": 16, "inner": { "name": "Scores", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_BlueprintVisible | CPF_ZeroConstructor" }
              ]
            },
            "/Game/Test.BP_Player_C:SetHealth": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewHealth", "type": "FloatProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_Parm" }
              ]
            },
            "/Game/Test.BP_Player_C:GetHealth": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "ReturnValue", "type": "FloatProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_Parm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:SetPlayerName": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewName", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_Parm" }
              ]
            },
            "/Game/Test.BP_Player_C:SetLocation": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewLocation", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 0, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Game/Test.BP_Player_C:GetLocation": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "ReturnValue", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 0, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoText": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "TextProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_Parm" },
                { "name": "ReturnValue", "type": "TextProperty", "offset": 16, "array_dim": 1, "size": 16, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoNumbers": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "Input", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_ZeroConstructor" },
                { "name": "ReturnValue", "type": "ArrayProperty", "offset": 16, "array_dim": 1, "size": 16, "inner": { "name": "ReturnValue", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_ZeroConstructor" }
              ]
            }
          },
          "vtables": {}
        }
        """);

    var model = new JMapImporter().Import(jmapPath);
    Assert(model.Metadata.EngineMajor == 5 && model.Metadata.EngineMinor == 6, "Engine version was not imported.");
    var player = model.Types.Single(type => type.Path == "/Game/Test.BP_Player_C");
    Assert(player.Functions.Count == 7, "UFunctions were not attached to their class.");

    var output = Path.Combine(directory.Path, "sdk");
    var result = new CSharpSdkGenerator().Generate(model, output, "DeadzoneRogue.Sdk");
    var source = File.ReadAllText(result.SourcePath);
    Assert(source.Contains("public class BP_Player : Actor", StringComparison.Ordinal), "Generated class inheritance is missing.");
    Assert(source.Contains("public float Health", StringComparison.Ordinal), "Generated typed property is missing.");
    Assert(source.Contains("public Actor? Target", StringComparison.Ordinal), "Generated object wrapper property is missing.");
    Assert(source.Contains("public string PlayerName", StringComparison.Ordinal), "Generated FString property is missing.");
    Assert(source.Contains("public string Mode", StringComparison.Ordinal), "Generated FName property is missing.");
    Assert(source.Contains("public string DisplayText", StringComparison.Ordinal), "Generated FText property is missing.");
    Assert(source.Contains("public Vector Location", StringComparison.Ordinal), "Generated POD struct property is missing.");
    Assert(source.Contains("public IReadOnlyList<int> Scores", StringComparison.Ordinal), "Generated TArray property is missing.");
    Assert(source.Contains("public void SetHealth(float newHealth)", StringComparison.Ordinal), "Generated void UFunction wrapper is missing.");
    Assert(source.Contains("public float GetHealth()", StringComparison.Ordinal), "Generated return value wrapper is missing.");
    Assert(source.Contains("public void SetPlayerName(string newName)", StringComparison.Ordinal), "Generated FString UFunction wrapper is missing.");
    Assert(source.Contains("public void SetLocation(Vector newLocation)", StringComparison.Ordinal), "Generated POD struct input wrapper is missing.");
    Assert(source.Contains("public Vector GetLocation()", StringComparison.Ordinal), "Generated POD struct return wrapper is missing.");
    Assert(source.Contains("public string EchoText(string input)", StringComparison.Ordinal), "Generated FText UFunction wrapper is missing.");
    Assert(source.Contains("public IReadOnlyList<int> EchoNumbers(IReadOnlyList<int> input)", StringComparison.Ordinal), "Generated TArray UFunction wrapper is missing.");
    Assert(source.Contains("Array: new(\"IntProperty\", 4", StringComparison.Ordinal), "Generated TArray element descriptor is missing.");
    Assert(source.Contains("public static UnrealStructDescriptor Descriptor", StringComparison.Ordinal), "Generated POD struct descriptor is missing.");
    Assert(source.Contains("new(\"NewHealth\", \"FloatProperty\", 0, 1, \"CPF_Parm\", 4", StringComparison.Ordinal),
        "Generated UFunction runtime layout metadata is missing.");
    Assert(File.Exists(result.ManifestPath), "SDK manifest was not generated.");

    var abstractionsProject = FindRepositoryFile("src/RogueMod.Abstractions/RogueMod.Abstractions.csproj");
    var escapedProjectPath = System.Security.SecurityElement.Escape(abstractionsProject);
    var generatedProject = Path.Combine(output, "GeneratedSdk.csproj");
    File.WriteAllText(generatedProject, $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="{{escapedProjectPath}}" />
          </ItemGroup>
        </Project>
        """);
    var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"build \"{generatedProject}\" -c Release --nologo",
        WorkingDirectory = output,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException("Could not start generated SDK compilation.");
    var standardOutput = build.StandardOutput.ReadToEnd();
    var standardError = build.StandardError.ReadToEnd();
    build.WaitForExit();
    Assert(build.ExitCode == 0, $"Generated SDK did not compile:{Environment.NewLine}{standardOutput}{standardError}");
}

static void Touch(string root, string relativePath)
{
    var path = Combine(root, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, []);
}

static string Combine(string root, params string[] relativeParts)
{
    var parts = relativeParts.SelectMany(part => part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
    return Path.Combine([root, .. parts]);
}

static string FindRepositoryFile(string relativePath)
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"roguemod-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

file sealed record TestModContext(
    string ModId,
    string GameProfileId,
    IModLogger Logger,
    IUnrealReflection Unreal) : IModContext;

file sealed class UnavailableUnrealReflection : IUnrealReflection
{
    public bool IsAvailable => false;
    public UnrealObjectHandle FindFirstOf(string className) => UnrealObjectHandle.Null;
    public bool IsValid(UnrealObjectHandle handle) => false;
    public UnrealObjectHandle GetClass(UnrealObjectHandle handle) => UnrealObjectHandle.Null;
    public string? GetPathName(UnrealObjectHandle handle) => null;
}

file sealed class TestLogger : IModLogger
{
    public List<string> Messages { get; } = [];

    public void Log(ModLogLevel level, string message) => Messages.Add(message);
}
