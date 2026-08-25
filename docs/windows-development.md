# Windows development

RogueMod's in-process components always target Windows x64. On Windows they can be built directly with Visual Studio Build Tools; the resulting runtime package is also the package used by Proton.

Mod authors who do not need to build RogueMod itself should use the shorter [managed-mod quick start](creating-managed-mod.md).

## Requirements

- the .NET SDK selected by `global.json`;
- Visual Studio Build Tools with the MSVC x64 and Windows SDK components;
- CMake 3.22 or newer.

Restore tools and validate the managed solution:

```powershell
dotnet tool restore
dotnet build RogueMod.slnx -c Release
dotnet test RogueMod.slnx -c Release
```

## Native bridge

Configure and build the bridge and native sample:

```powershell
cmake -S src/RogueMod.Native -B .build/native/windows-msvc -A x64
cmake --build .build/native/windows-msvc `
  --config Release `
  --target RogueModBridge PackageHelloNativeMod `
  --parallel
```

The bridge is written to `.build/native/windows-msvc/Release/RogueMod.Bridge.dll`. The native sample is packaged under `.artifacts/packages/native/Game__Shipping__Win64/sample.hello-native`.

## Ready runtime package

Package the native bridge, managed host, and pinned private Windows .NET runtime:

```powershell
./scripts/package-runtime.ps1 `
  -Configuration Release `
  -BridgePath .build/native/windows-msvc/Release/RogueMod.Bridge.dll
```

The command verifies the downloaded runtime checksum and requires the bridge, managed runtime, `hostfxr`, and `coreclr`. It produces:

```text
.artifacts/runtime/RogueMod/
.artifacts/runtime/RogueMod.Runtime-win-x64.zip
```

The ZIP has a top-level `RogueMod` directory. Extract it and pass that directory to `roguemod install-runtime`. Its `runtime-package.json` records the target, compatible hosts, .NET runtime version, and SHA-256 hashes of the native and managed entry assemblies.

## Continuous delivery

GitHub Actions builds and tests the managed solution on Windows and Linux, builds the native bridge with MSVC, and retains the ready runtime ZIP. A tag beginning with `v` also creates or updates the corresponding GitHub release with that ZIP.
