# Create a native mod

Native mods are Windows x64 DLLs loaded by UE4SS. RogueMod owns the package around the binary: the manifest, canonical storage, transactional deployment under an immutable `loaderId`, and activation state. The authoring SDK is a pair of pinned headers that keep the lifecycle ABI stable without requiring the full UE4SS source tree.

## Scaffold a starter

```powershell
roguemod new native `
  --id example.hello-native `
  --name Example.HelloNative `
  --display-name 'Hello Native'
```

The equivalent installable template is `RogueMod.Templates`:

```powershell
dotnet new roguemod-native -n Example.HelloNative `
  --mod-id example.hello-native `
  --mod-name 'Hello Native'
```

Both paths emit the same standalone layout:

```text
Example.HelloNative/
  CMakeLists.txt                    Builds main.dll and packs the mod package
  mod.json                          Package manifest (kind "native")
  src/Example.HelloNative.cpp       Mod source
  README.md
```

## SDK headers

The starter compiles against three pinned headers shipped by the `RogueMod.Sdk` NuGet package under `build/native/include`:

- `UE4SS/CppUserModBase.hpp` — the lifecycle base class, pinned to RE-UE4SS build `a1e7f571` with a `static_assert` on the ABI size.
- `RogueMod/NativeMod.hpp` — `ROGUEMOD_DEFINE_NATIVE_MOD`, which exports the `start_mod`/`uninstall_mod` entry points UE4SS expects.
- `RogueMod/NativeLog.hpp` — `RogueMod::NativeLog::line`, an append-only UTF-8 logger that writes beside the mod deployment.

Point `ROGUEMOD_NATIVE_INCLUDE_DIR` at that directory (from the NuGet cache or an extracted package) and CMake resolves the rest. The pinned headers cover lifecycle callbacks, logging, and Windows APIs; full Unreal reflection requires the complete UE4SS SDK from the same pinned commit, which is compatible because only the lifecycle ABI is pinned.

## Build and package

Windows with Visual Studio Build Tools:

```powershell
$env:ROGUEMOD_NATIVE_INCLUDE_DIR = "$env:USERPROFILE\.nuget\packages\roguemod.sdk\0.1.0\build\native\include"
cmake -S . -B .build -A x64
cmake --build .build --config Release --target PackageRogueNativeMod
```

The package is written to `.artifacts/packages/native/Game__Shipping__Win64/<mod-id>` containing `mod.json` and `dlls/main.dll`. On Linux, cross-compile the same DLL with `clang-cl` and the Microsoft CRT/Windows SDK supplied by `xwin`; see [Linux development](linux-development.md). MinGW is unsupported because its C++ ABI is incompatible with the MSVC-built UE4SS.

## Manifest

The native manifest follows the shared [package manifest](mod-manifest.md) rules with two specifics:

- `entryPoint` is fixed to `dlls/main.dll`.
- `loaderId` is required and immutable after installation. It must contain 3-64 ASCII letters, digits or `_`, start with a letter, and satisfy UE4SS directory-name constraints. `roguemod new native` derives it from the project name and uses the same identifier as the C++ mod class and UE4SS `ModName`.

## Install and manage

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package '.artifacts\packages\native\Game__Shipping__Win64\example.hello-native' --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
roguemod disable --game 'E:\Steam\steamapps\common\Deadzone Rogue' --id example.hello-native
```

The package is stored canonically under `<GameRoot>/Mods/example.hello-native` and deployed transactionally to `ue4ss/Mods/<loaderId>`. `enable`, `disable`, `update`, and `uninstall` behave exactly as for every other package kind; see [Package manager](mod-manager.md).

## Lifecycle

Override `on_program_start`, `on_update`, `on_unreal_init`, or `on_ui_init` on the `CppUserModBase` subclass. Lifecycle events arrive on Unreal's game thread; see [Architecture](architecture.md) for the event model the bridge delivers to managed mods, which matches the UE4SS callbacks driving native mods.
