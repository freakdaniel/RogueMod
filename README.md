# RogueMod

RogueMod is a C#-first mod loader and manager for Deadzone: Rogue, built on RE-UE4SS. The package model is designed for managed C#, native C++, Lua, and Unreal Pak mods.

## Current status

Deadzone: Rogue 1.4.2.0 / Unreal Engine 5.6.1 is confirmed working through Proton with the game-specific `VTableLayout.ini`. UE4SS loads the native sample and the small `RogueModBridge`; the bridge starts a private Windows CoreCLR 10.0.10 and the managed runtime loads each C# mod in its own collectible `AssemblyLoadContext`.

The reflection path has been tested in the main menu with real `PlayerController` instances. ABI 5 added zero-parameter calls, scalar input and return marshalling, and primitive property reads and writes. ABI 6 moved the shared package source to `<GameRoot>/Mods`. ABI 7 added allocator-safe `FString` and `FName` marshalling. ABI 8 added field-wise POD script structs and typed generated adapters; both are confirmed in game. ABI 9 adds `FText` and one-dimensional `TArray` marshalling and is covered by managed integration tests, full generated-SDK compilation, and the Windows cross-build; its in-game smoke test is pending.

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

## Build and test

```bash
dotnet build RogueMod.slnx -c Release
dotnet tests/RogueMod.Tests/bin/Release/net10.0/RogueMod.Tests.dll
```

Build and package the managed sample on Linux or Windows:

```bash
dotnet build src/RogueMod.Sample.Managed/RogueMod.Sample.Managed.csproj \
  -c Release -t:PackageRogueMod
```

The package is written to `.artifacts/packages/managed/Release/sample.hello-managed`.

## Build native components on Linux

Native mods remain Windows x64 DLLs when the game runs through Proton. RogueMod uses `clang-cl` and the Microsoft CRT/Windows SDK supplied by xwin, rather than the incompatible MinGW C++ ABI.

```bash
sudo apt install cmake ninja-build clang lld llvm
rustup target add x86_64-pc-windows-msvc
scripts/native-toolchain.sh bootstrap-xwin --accept-microsoft-license
scripts/native-toolchain.sh build
```

The bridge is written to `.build/native/Game__Shipping__Win64/RogueMod.Bridge.dll`. The native sample package is written to `.artifacts/packages/native/Game__Shipping__Win64/sample.hello-native`. See [Linux development](docs/linux-development.md) for details.

## Package and install the runtime

```bash
scripts/package-runtime.sh Release

dotnet run --project src/RogueMod.Cli -c Release -- install-runtime \
  --game "/path/to/Deadzone Rogue" \
  --package ".artifacts/runtime/RogueMod" \
  --replace
```

The runtime installer preserves the internal UE4SS bridge deployment and migrates legacy managed packages from `RogueModBridge/managed-mods` into the shared game-root directory.

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

## Generate the typed game SDK

```bash
dotnet run --project src/RogueMod.Cli -c Release -- generate-sdk \
  --game "/path/to/Deadzone Rogue" \
  --output ".artifacts/sdk/deadzone-rogue" \
  --namespace "DeadzoneRogue.Sdk"
```

The CLI imports the newest UE4SS `.jmap` dump unless `--jmap <file>` is supplied explicitly. It emits `RogueMod.GameSdk.g.cs` and a source manifest containing the dump SHA-256. See [Generated SDK](docs/generated-sdk.md).

## Diagnostics

```bash
dotnet run --project src/RogueMod.Cli -c Release -- diagnose \
  --game "/path/to/Deadzone Rogue"
```

On Linux, Steam must launch the game with `WINEDLLOVERRIDES=dwmapi=n,b`. RogueMod reports the required value but does not modify Steam launch options automatically.

Further details are available in [Architecture](docs/architecture.md) and [Managed API](docs/managed-api.md).
