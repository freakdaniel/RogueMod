# RogueMod

RogueMod is a C#-first mod loader and manager for Deadzone: Rogue, built on RE-UE4SS. The package model is designed for managed C#, native C++, Lua, and Unreal Pak mods.

**[Documentation site](https://freakdaniel.github.io/RogueMod/)** — quick starts, guides, CLI reference, architecture, and the full API reference, built with Docusaurus.

## Current status

Deadzone: Rogue 1.4.2.0 / Unreal Engine 5.6.1 is confirmed working through Proton with the game-specific `VTableLayout.ini`. UE4SS loads the native `RogueModBridge`; the bridge starts a private Windows CoreCLR and the managed runtime loads each C# mod in its own collectible `AssemblyLoadContext`.

The reflection layer is tested in the game's main menu against real `PlayerController`, UMG, Niagara, and `ValGameInstance` objects. The generated SDK provides strongly typed wrappers, property transport (including `TMap`/`TSet` and reference writes through the game's validated `TObjectPtr` setter), object creation, actor spawning, and mutable pre/post hooks. See [Reflection status](docs/reflection-api.md) for the supported type matrix.

## Quick start: a managed mod

```powershell
roguemod new managed `
  --id example.hello-deadzone `
  --name Example.HelloDeadzone

cd Example.HelloDeadzone
dotnet restore --source E:\RogueModFeed
dotnet build -c Release -t:PackageRogueMod
```

Every package kind follows the same shape — `roguemod new managed|lua|native|pak` produces the matching standalone starter. Full walkthroughs: [managed](docs/creating-managed-mod.md), [Lua](docs/creating-lua-mod.md), [native](docs/creating-native-mod.md), and [pak](docs/creating-pak-mod.md) quick starts.

## Managing installed mods

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.\MyModPackage' --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
```

All commands accept Managed, Native, Lua, and Pak packages. See the [CLI reference](docs/cli-reference.md).

## Building and testing from source

```bash
dotnet tool restore
dotnet build RogueMod.slnx -c Release
dotnet test RogueMod.slnx -c Release
```

The native bridge builds with MSVC on Windows (`cmake -S src/RogueMod.Native -B .build/native/windows-msvc -A x64`) and with `clang-cl` + `xwin` on Linux; details in [Windows development](docs/windows-development.md) and [Linux development](docs/linux-development.md).

The runtime package is produced by `scripts/package-runtime.sh` / `scripts/package-runtime.ps1` and written to `.artifacts/runtime/RogueMod.Runtime-win-x64.zip`.

## Documentation

| Content | Where |
| --- | --- |
| Documentation site (guides + API reference) | https://freakdaniel.github.io/RogueMod/ |
| Guides source | `docs/` (Docusaurus docs content) |
| Site engine and configuration | `website/` (Docusaurus, `npm run dev` to preview) |
| API reference source | XML documentation comments in `RogueMod.Abstractions` and `RogueMod.Sdk`, emitted by the generator for the game SDK |
| API reference generation | `src/RogueMod.Tooling.ApiDocsGen` (run via `npm run gen-api`) |
| Build the site locally | `npm run build` in `website/` (`npm run dev` to preview) |

The API reference pages under `website/reference` are generated artifacts and are not committed. The site publishes to GitHub Pages automatically on changes to `docs/`, `website/`, or documented source.

## Repository layout

```text
src/
  RogueMod.Abstractions/       Stable API referenced by C# mods
  RogueMod.Cli/                Command-line manager
  RogueMod.Core/               Profiles, manifests, installers, diagnostics, scaffolders
  RogueMod.Native/             UE4SS bridge and Windows C++ toolchain project
  RogueMod.Runtime/            In-process managed runtime
  RogueMod.Sdk/                JMAP generator, packaging targets, native headers
  RogueMod.Templates/          dotnet new templates for all package kinds
  RogueMod.Sample.Managed/     C# sample package
  RogueMod.Sample.TypedHooks/  External-style generated SDK and hook sample
  RogueMod.Sample.Invulnerability/ Practical exact-instance gameplay hook
  RogueMod.Sample.Native/      C++ sample package
  RogueMod.Tooling.SdkDumper/  UE4SS Lua dump helper
  RogueMod.Tooling.ApiDocsGen/ API reference markdown generator for the docs site
tests/                         Automated tests and fixtures
docs/                          Documentation guides (Docusaurus docs content)
website/                       Documentation site engine (Docusaurus)
config/                        Game profiles and compatibility data
scripts/                       Reproducible build and packaging entry points
.artifacts/                    Generated SDKs and packages; ignored by Git
.build/                        Native build trees; ignored by Git
```

## Contributing

Read the [contributing guide](docs/contributing.md) and [code style](docs/code-style.md). In short: build and test with zero warnings (warnings are errors), cover behavior changes with tests, document public surface with XML documentation comments, and keep gameplay knowledge in documentation rather than gameplay frameworks.
