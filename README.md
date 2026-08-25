# RogueMod

RogueMod is a C#-first mod loader and manager for Deadzone: Rogue, built on RE-UE4SS. The package model is designed for managed C#, native C++, Lua, and Unreal Pak mods.

## Current status

Deadzone: Rogue 1.4.2.0 / Unreal Engine 5.6.1 is confirmed working through Proton with the game-specific `VTableLayout.ini`. UE4SS loads the native sample and the small `RogueModBridge`; the bridge starts a private Windows CoreCLR 10.0.10 and the managed runtime loads each C# mod in its own collectible `AssemblyLoadContext`.

The reflection path has been tested in the main menu with real `PlayerController`, UMG, Niagara, and `ValGameInstance` instances. ABI 5 added zero-parameter calls, scalar input and return marshalling, and primitive property reads and writes. ABI 6 moved the shared package source to `<GameRoot>/Mods`. ABI 7 added allocator-safe `FString` and `FName` marshalling. ABI 8 added field-wise POD script structs and typed generated adapters. ABI 9 added `FText` and `TArray` marshalling. ABI 10 added safe multi-object discovery through UE4SS `FindAllOf`, generated `FindFirst<T>`/`FindAll<T>` wrappers, capability-gated nested `TArray` transport, `TOptional<T>` set/unset transport, serial-safe `TWeakObjectPtr<T>` reads/writes, and identity-preserving `FLazyObjectPtr` transport without changing the 16-byte wire value. ABI 11 adds ownership-safe, generated pre/post UFunction observation hooks that reuse the same descriptors and marshalling pipeline. Optional, weak, and lazy property mutation/restoration are verified in the installed game; nested arrays remain automated-transport-tested because the current JMAP has no live nested-array target.

## Repository layout

All code-bearing components live under `src/`:

```text
src/
  RogueMod.Abstractions/       Stable API referenced by C# mods
  RogueMod.Cli/                Command-line manager
  RogueMod.Core/               Profiles, manifests, installers, diagnostics
  RogueMod.Native/             UE4SS bridge and Windows C++ toolchain project
  RogueMod.Runtime/            In-process managed runtime
  RogueMod.Sdk/                JMAP generator and managed/native authoring SDK
  RogueMod.Templates/          Installable dotnet new templates
  RogueMod.Sample.Managed/     C# sample package
  RogueMod.Sample.Native/      C++ sample package
  RogueMod.Tooling.SdkDumper/  UE4SS Lua dump helper
tests/                         Automated tests and fixtures
config/                        Game profiles and compatibility data
docs/                          Design and development documentation
scripts/                       Reproducible build and packaging entry points
.artifacts/                    Generated SDKs and packages; ignored by Git
.build/                        Native build trees; ignored by Git
```

`RogueMod.Sample.*` projects are repository examples used to exercise the SDK. `RogueMod.Templates/content` is the parameterized starter source shipped by `dotnet new` and `roguemod new`; keeping it inside the template project avoids a second top-level source tree while preserving the two different product roles.

## Create a managed mod

The CLI creates a complete standalone solution without copying files from this repository:

```powershell
roguemod new managed `
  --id example.hello-deadzone `
  --name Example.HelloDeadzone `
  --display-name 'Hello Deadzone'

cd Example.HelloDeadzone
dotnet restore --source E:\RogueModFeed
dotnet build -c Release -t:PackageRogueMod --no-restore
```

The equivalent installable template is `RogueMod.Templates`:

```powershell
dotnet new install E:\RogueModFeed\RogueMod.Templates.0.1.0.nupkg
dotnet new roguemod-managed -n Example.HelloDeadzone `
  --mod-id example.hello-deadzone `
  --mod-name 'Hello Deadzone'
```

Both paths emit the same `.slnx` starter and ready package layout. While packages remain unpublished, `E:\RogueModFeed` is simply a maintainer-supplied directory containing the matching `.nupkg` files. See the [Windows managed-mod quick start](docs/creating-managed-mod.md).

## Build and test

```bash
dotnet build RogueMod.slnx -c Release
dotnet test RogueMod.slnx -c Release
```

Build and package the managed sample on Linux or Windows:

```bash
dotnet build src/RogueMod.Sample.Managed/RogueMod.Sample.Managed.csproj \
  -c Release -t:PackageRogueMod
```

The package is written to `.artifacts/packages/managed/Release/sample.hello-managed`.

External managed mods can consume the authoring packages instead of importing repository files:

```xml
<ItemGroup>
  <PackageReference Include="RogueMod.Sdk" Version="0.1.0" />
  <PackageReference Include="DeadzoneRogue.Sdk" Version="0.1.0" />
</ItemGroup>
```

`RogueMod.Sdk` brings the stable `RogueMod.Abstractions` API transitively and imports the `PackageRogueMod` build target. `DeadzoneRogue.Sdk` provides the generated typed game wrappers. Its assembly is deliberately excluded from every mod package and installed once under the RogueMod runtime shared directory. `RogueMod.Sdk` also carries the pinned minimal C++ lifecycle headers under `build/native/include` and exposes their absolute path as `$(RogueModNativeIncludeDir)` to MSBuild consumers.

## Build native components on Linux

Native mods remain Windows x64 DLLs when the game runs through Proton. RogueMod uses `clang-cl` and the Microsoft CRT/Windows SDK supplied by xwin, rather than the incompatible MinGW C++ ABI.

```bash
sudo apt install cmake ninja-build clang lld llvm zip unzip
rustup target add x86_64-pc-windows-msvc
scripts/native-toolchain.sh bootstrap-xwin --accept-microsoft-license
scripts/native-toolchain.sh build
```

The bridge is written to `.build/native/Game__Shipping__Win64/RogueMod.Bridge.dll`. The native sample package is written to `.artifacts/packages/native/Game__Shipping__Win64/sample.hello-native`. See [Linux development](docs/linux-development.md) for details.

On Windows, Visual Studio Build Tools can build the same bridge directly with MSVC:

```powershell
cmake -S src/RogueMod.Native -B .build/native/windows-msvc -A x64
cmake --build .build/native/windows-msvc --config Release --target RogueModBridge PackageHelloNativeMod --parallel
```

## Package and install the runtime

```bash
scripts/package-runtime.sh Release
```

On Windows:

```powershell
./scripts/package-runtime.ps1 `
  -Configuration Release `
  -BridgePath .build/native/windows-msvc/Release/RogueMod.Bridge.dll
```

Both scripts validate the bridge, managed host, `hostfxr`, and `coreclr`, then emit the same portable package and release archive:

```text
.artifacts/runtime/RogueMod/
.artifacts/runtime/RogueMod.Runtime-win-x64.zip
```

The archive contains a `win-x64` runtime because the game process is Windows x64 both on native Windows and under Proton. Tagged GitHub pushes matching `v*` publish this ZIP as a release asset; other CI runs retain it as a workflow artifact.

Install an unpacked runtime:

```bash
dotnet run --project src/RogueMod.Cli -c Release -- install-runtime \
  --game "/path/to/Deadzone Rogue" \
  --package ".artifacts/runtime/RogueMod" \
  --replace
```

The runtime installer places the technical payload under `<GameRoot>/RogueMod`, deploys only `dlls/main.dll` to the internal UE4SS bridge directory, and migrates legacy managed packages from `RogueModBridge/managed-mods` into the shared game-root directory.

## Game installation layout

`<GameRoot>/Mods` is the canonical package store read by RogueMod:

```text
Deadzone Rogue/
  Mods/
    sample.hello-managed/
      mod.json
      dlls/RogueMod.Sample.Managed.dll
    sample.hello-native/
      mod.json
      dlls/main.dll
```

Managed packages are loaded directly from this directory. Native packages are also stored here, then transactionally deployed to the internal `ue4ss/Mods/<loaderId>` directory because UE4SS requires that physical layout. The root package remains the source of truth.

RogueMod infrastructure never lives in this `Mods` directory. Private CoreCLR, managed runtime, logs, and the shared game SDK are stored under `<GameRoot>/RogueMod`; `ue4ss/Mods/RogueModBridge` contains only the native bootstrap DLL.

## Manage installed mods

The kind-neutral manager reads `mod.json` and supports Managed, Native, Lua, and Pak packages:

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.\MyModPackage'
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
roguemod enable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
roguemod update --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.\MyModPackage-v2'
roguemod uninstall --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.my-mod
```

`list` distinguishes `Enabled`, `Disabled`, and `Broken` deployments. Updates are supplied as local package directories and preserve disabled state. Existing `install-managed` and `install-native` commands remain supported. See [Package manager](docs/mod-manager.md) for manifest and deployment details.

Install samples:

```bash
dotnet run --project src/RogueMod.Cli -c Release -- install-managed \
  --game "/path/to/Deadzone Rogue" \
  --package ".artifacts/packages/managed/Release/sample.hello-managed" \
  --replace

dotnet run --project src/RogueMod.Cli -c Release -- install-native \
  --game "/path/to/Deadzone Rogue" \
  --package ".artifacts/packages/native/Game__Shipping__Win64/sample.hello-native" \
  --replace
```

## Use the typed game SDK

Mod authors consume the maintained package and never install UE4SS tooling or generate reflection dumps:

```csharp
using DeadzoneRogue.Sdk;

var actor = context.Unreal.FindFirst<Actor>();
```

JMAP capture is a maintainer-only release operation. For a verified local game build, prepare the pinned tooling once, launch the game, and build the package from the automatic snapshot:

```powershell
./scripts/prepare-game-sdk.ps1 `
  -GamePath 'E:\Steam\steamapps\common\Deadzone Rogue' `
  -InstallRuntime

# Launch the game once. No dump hotkey is required.
./scripts/build-game-sdk.ps1 `
  -GamePath 'E:\Steam\steamapps\common\Deadzone Rogue' `
  -WaitForDumpSeconds 900
```

The maintainer flow pins the game version, UE4SS asset URL, and SHA-256 in `config/GameSdk/deadzone-rogue.json`. It emits `DeadzoneRogue.Sdk.nupkg`, installs the compiled SDK once into `<GameRoot>/RogueMod/runtime/shared`, updates the ready runtime archive, and disables the dump mod after success.

The lower-level generator remains available for importer development:

```bash
dotnet run --project src/RogueMod.Cli -c Release -- generate-sdk \
  --game "/path/to/Deadzone Rogue" \
  --output ".artifacts/sdk/deadzone-rogue" \
  --namespace "DeadzoneRogue.Sdk"
```

The CLI imports the newest UE4SS `.jmap` dump unless `--jmap <file>` is supplied explicitly. It emits `RogueMod.GameSdk.g.cs`, a source manifest containing the dump SHA-256 and compatibility metadata, and a buildable/packable `DeadzoneRogue.Sdk.csproj`. Generated game code and raw dumps remain ignored maintainer artifacts.

```bash
dotnet build .artifacts/sdk/deadzone-rogue/DeadzoneRogue.Sdk.csproj -c Release
dotnet pack src/RogueMod.Abstractions/RogueMod.Abstractions.csproj \
  -c Release --no-build -o .artifacts/sdk/packages
dotnet pack .artifacts/sdk/deadzone-rogue/DeadzoneRogue.Sdk.csproj \
  -c Release --no-build -o .artifacts/sdk/packages
```

See [Generated SDK](docs/generated-sdk.md).

## Diagnostics

```bash
dotnet run --project src/RogueMod.Cli -c Release -- diagnose \
  --game "/path/to/Deadzone Rogue"
```

On Linux, Steam must launch the game with `WINEDLLOVERRIDES=dwmapi=n,b`. RogueMod reports the required value but does not modify Steam launch options automatically.

Further details are available in [Architecture](docs/architecture.md) and [Managed API](docs/managed-api.md).
