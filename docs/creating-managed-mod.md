# Create a managed mod on Windows

This is the shortest supported path from an empty directory to an installable Deadzone: Rogue mod. Mod authors do not install UE4SS developer tooling, capture JMAP files, or generate the game SDK.

## Requirements

- Windows 10 or newer;
- .NET 10 SDK;
- Deadzone: Rogue with the RogueMod runtime installed;
- a NuGet source containing matching versions of `RogueMod.Sdk`, `RogueMod.Abstractions`, and `DeadzoneRogue.Sdk`.

Until the packages are published, the NuGet source can be a normal directory supplied by the RogueMod maintainer. Keep the `.nupkg` files in that directory; do not unpack them or copy SDK DLLs into a mod.

## Option 1: RogueMod CLI

Create a managed project:

```powershell
roguemod new managed `
  --id example.hello-deadzone `
  --name Example.HelloDeadzone `
  --display-name 'Hello Deadzone'
```

The CLI creates `Example.HelloDeadzone` in the current directory. Pass `--output <directory>` to choose another new location. Existing files are never overwritten.

## Option 2: dotnet new

Install the template package from the maintainer feed or from an exact `.nupkg` path:

```powershell
dotnet new install E:\RogueModFeed\RogueMod.Templates.0.1.0.nupkg
```

Create the same project:

```powershell
dotnet new roguemod-managed `
  --name Example.HelloDeadzone `
  --mod-id example.hello-deadzone `
  --mod-name 'Hello Deadzone'
```

Both entry points produce the same starter layout:

```text
Example.HelloDeadzone/
  Example.HelloDeadzone.slnx
  Directory.Build.props
  Directory.Packages.props
  global.json
  src/
    Example.HelloDeadzone/
      Example.HelloDeadzone.csproj
      Mod.cs
      mod.json
```

Package versions are centralized in `Directory.Packages.props`. Update them there when the runtime or game SDK version changes.
Edit `mod.json` to set the description, icon, gallery, supported languages, and localized descriptions. Put referenced files under a sibling `media/` directory; see the [manifest reference](mod-manifest.md).

## Restore and package

Enter the generated directory and restore from the supplied package directory:

```powershell
cd Example.HelloDeadzone
dotnet restore --source E:\RogueModFeed
dotnet build -c Release -t:PackageRogueMod --no-restore
```

The ready package is written to:

```text
.artifacts\packages\managed\Release\example.hello-deadzone
```

It contains the mod assembly, its private dependencies, `mod.json`, and referenced `media/` files. It does not contain `RogueMod.Sdk.dll`, `RogueMod.Abstractions.dll`, or the large `DeadzoneRogue.Sdk.dll`; those shared assemblies are supplied once by the installed runtime.

## Install and test

Close the game before replacing an installed mod, then run:

```powershell
roguemod install `
  --game 'E:\Steam\steamapps\common\Deadzone Rogue' `
  --package '.artifacts\packages\managed\Release\example.hello-deadzone' `
  --replace
```

Start the game normally through Steam. The starter writes lifecycle messages through `IModLogger`. Edit `src\Example.HelloDeadzone\Mod.cs` to add behavior. Typed game access is available by importing `DeadzoneRogue.Sdk` and calling helpers such as `context.Unreal.FindFirst<T>()`.

## Command reference

```text
roguemod new managed --id <package-id> [options]

--name <project-name>         C# project and namespace name
--display-name <name>         Human-readable mod name
--output <directory>          New output directory
--sdk-version <version>       RogueMod.Sdk package version
--game-sdk-version <version>  DeadzoneRogue.Sdk package version
```

The package id must be 3-64 lowercase letters, digits, dots, underscores, or hyphens. Treat it as permanent: it is also the installed directory name and update identity.
