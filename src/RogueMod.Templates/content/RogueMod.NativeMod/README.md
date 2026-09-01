# RogueMod.NativeMod

Sample native mod is a native UE4SS mod for Deadzone: Rogue.

## Layout

- `src/RogueMod.NativeMod.cpp` — mod source. Derives from the pinned `RC::CppUserModBase`, logs through `RogueMod::NativeLog::line`, and exports the UE4SS entry points via `ROGUEMOD_DEFINE_NATIVE_MOD`.
- `mod.json` — RogueMod package manifest (`kind: "native"`, fixed `entryPoint: "dlls/main.dll"`, immutable `loaderId: "SampleNative"`).
- `CMakeLists.txt` — builds `main.dll` and packs the ready mod package.

## Get the SDK headers

The build needs the pinned SDK headers (`RogueMod/NativeMod.hpp`, `RogueMod/NativeLog.hpp`, `UE4SS/CppUserModBase.hpp`). Obtain the `RogueMod.Sdk` NuGet package and point `ROGUEMOD_NATIVE_INCLUDE_DIR` at its `build/native/include` directory, for example:

```powershell
$env:ROGUEMOD_NATIVE_INCLUDE_DIR = "$env:USERPROFILE\.nuget\packages\roguemod.sdk\0.1.0\build\native\include"
```

These headers cover lifecycle callbacks, logging, and Windows APIs. Full Unreal reflection requires the complete UE4SS SDK from the same pinned commit.

## Build and package (Windows, MSVC)

```powershell
cmake -S . -B .build -A x64
cmake --build .build --config Release --target PackageRogueNativeMod
```

The package is written to `.artifacts/packages/native/Game__Shipping__Win64/sample.native-mod`. On Linux, cross-compile the same package with `clang-cl` and `xwin`; see the RogueMod repository documentation (`docs/linux-development.md`).

## Install

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.artifacts\packages\native\Game__Shipping__Win64\sample.native-mod' --replace
```

The package is stored canonically under `<GameRoot>/Mods/sample.native-mod` and deployed to `ue4ss/Mods/SampleNative`. `RogueMod::NativeLog::line` writes to `<GameRoot>/Valhalla/Binaries/Win64/ue4ss/Mods/SampleNative/SampleNative.log`.
