# RogueMod architecture

RogueMod separates the external C# manager from the minimal native code that runs inside the game process.

## Components

1. `RogueMod.Core` owns game profiles, package manifests, transactional installation, migration, and diagnostics. It does not depend on Unreal Engine.
2. `RogueMod.Cli` is the automation-friendly manager interface. A future GUI can use Core without duplicating package logic.
3. `RogueMod.Abstractions` is the stable API referenced by managed mods. Mods do not reference runtime or manager implementations.
4. `RogueMod.Sdk` imports UE4SS JMAP data and emits typed C# wrappers. It also contains the managed packaging target and minimal native authoring headers.
5. `RogueMod.Native` builds `RogueModBridge`, a small UE4SS C++ mod. It loads private Windows CoreCLR through hostfxr and exposes a versioned C ABI to managed code.
6. `RogueMod.Runtime` discovers managed packages, resolves dependencies, and loads each package in a collectible `AssemblyLoadContext`.

## Package storage and deployment

The canonical package store is always relative to the game root:

```text
<GameRoot>/
  Mods/
    <package-id>/
      mod.json
      dlls/...
```

This `Mods` directory contains user-installed game mods only. RogueMod's private CoreCLR, managed runtime, logs, and shared generated SDK use a separate technical root:

```text
<GameRoot>/RogueMod/
  runtime/managed/...
  runtime/dotnet/...
  runtime/shared/DeadzoneRogue.Sdk.dll
  RogueMod.log
```

Managed packages are loaded directly from this directory. Native packages use the same canonical store, but UE4SS requires native DLLs under its own directory. The manager therefore maintains a transactional deployment copy:

```text
<GameRoot>/Valhalla/Binaries/Win64/ue4ss/Mods/
  RogueModBridge/       Minimal bootstrap: dlls/main.dll only
  <native-loader-id>/   Native package deployment copy
  <lua-loader-id>/      Lua package deployment copy
  mods.txt              UE4SS activation state
```

Pak payloads are deployed to the profile-specific Unreal Pak directory. For Deadzone: Rogue this is `Valhalla/Content/Paks/~mods`; stable package-ID hashes prevent deployment filename collisions. `package-id` is the stable RogueMod identity. Native and Lua manifests additionally define an immutable `loaderId`, which must satisfy UE4SS directory-name constraints. Moving the canonical store and technical runtime out of `RogueModBridge` prevents runtime upgrades from owning or hiding user content. UE4SS receives only the native bootstrap it requires.

The runtime installer migrates packages from the legacy `RogueModBridge/managed-mods` layout into `<GameRoot>/Mods`, preserves `runtime/shared`, and moves the technical payload to `<GameRoot>/RogueMod`. Runtime replacement, bridge deployment, and migration are rolled back together when activation fails.

## In-process lifecycle

The bridge passes the game-root Mods path and the selected game-profile id to managed code. The runtime filters the shared directory by manifest kind, resolves managed dependencies, and loads packages in deterministic id order. Missing dependencies or cycles disable only the affected packages.

Lifecycle events are delivered on the Unreal game thread. `ProgramStarted` and `Update` are confirmed in Deadzone: Rogue; the availability of other UE4SS callbacks depends on the game. An exception in a mod event handler disables later callbacks for that mod without unloading other packages.

## Reflection boundary

Host ABI 11 dynamically resolves exact decorated exports from the installed `UE4SS.dll` and does not link the bridge against private UEPseudo headers. Single and multi-object discovery use UE4SS `FindFirstOf` and `FindAllOf`; exact leading-slash paths used by diagnostics resolve through `StaticFindObject`. Managed mods receive index/serial handles backed by `GUObjectArray`, never raw object pointers. Handles become invalid after Unreal GC destroys or reuses a slot.

The current reflection contract supports object discovery; primitive, strong/weak/lazy object, `FString`, `FName`, `FText`, POD script-struct, and capability-gated nested `TArray` property access; zero-parameter calls; and input, return, and out/ref parameter buffers for `ProcessEvent`. The bridge constructs and destroys Unreal-owned string and text values with UE4SS exports and copies only UTF-16 display data across the managed boundary. POD structs cross the C ABI as bounded byte buffers, but managed code serializes them from validated field descriptors rather than copying CLR layout. Arrays cross as bounded recursive ABI values; their in-process storage uses Unreal `FMemory`, live inner-property metadata, recursive construction/destruction, and at most three encoded `TArray` containers. Lazy references carry their complete 24-byte native identity plus an optional serial-safe resolved handle and are assigned through the exported UE4SS property setter. Non-empty `TObjectPtr` array element replacement is rejected for the current game build because its safe setter is not exposed by the installed UE4SS version. The bridge validates handle serials, parameter counts, live offsets and sizes, buffer bounds, struct sizes, array counts, and string lengths before dispatch.

### Type translation pipeline

Reflected metadata has one translation policy per process boundary. `CSharpTypeTranslator` owns C# type selection, generated transport expressions, supported POD/container rules, and descriptor emission. `NativeReflectionTypeRegistry` owns Unreal metadata-to-ABI kind resolution, while `NativeScalarValueCodec` owns primitive wire conversions. Property access and `ProcessEvent` invocation consume these components instead of defining local type switches.

Hooks must reuse the same generated descriptors, registry, and codecs for their argument and return buffers. Hook registration and dispatch may add lifecycle and ownership rules, but must not introduce another Unreal-to-C# type table. This mirrors the useful separation in source-generator-based Unreal integrations while retaining RogueMod's shipping-game and UE4SS boundary.

ABI 11 installs one bridge-owned UE4SS `ProcessEvent` pre callback and one post callback, then filters calls by resolved `UFunction*`. Per-mod registrations do not install independent engine detours. Native registrations copy and validate generated parameter layouts before activation; managed subscriptions are owned by the mod context and are removed before its collectible assembly load context unloads. The initial contract exposes read-only call snapshots. Argument replacement, return replacement, and preventing the original call are deliberately outside ABI 11.

## Platform ABI

The external manager can be published independently for `linux-x64` and `win-x64`. UE4SS DLLs are always Windows x64 binaries; Proton runs the same PE process on Linux. Managed mods contain portable IL, but any native dependency loaded into the game must target `win-x64`.

C++ mods are cross-compiled on Linux with `clang-cl` and Microsoft CRT/Windows SDK files obtained through xwin. MinGW is unsupported because its C++ ABI is incompatible with the installed MSVC UE4SS build.

## Package kinds

- Managed: a C# assembly loaded by `RogueMod.Runtime`.
- Native: a UE4SS C++ mod deployed under its `loaderId`.
- Lua: a UE4SS Lua package with `Scripts/main.lua`, deployed and activated under its `loaderId`.
- Pak: a `.pak` payload with optional same-name `.utoc`, `.ucas`, and `.sig` companions.

## Safety boundary

RogueMod does not disable or bypass anti-cheat. A profile may prohibit activation in online modes. Managed and native mods execute with the game process permissions; manifests and hashes provide integrity metadata, not a security sandbox.

## Deadzone: Rogue compatibility detail

Deadzone: Rogue 1.4.2.0 uses an `FMalloc` vtable layout where `Malloc` is at `0x30` and `Free` is at `0x50`. The default UE4SS layout called `Malloc` in place of `Free`, producing a fatal error in `MallocBinned2.cpp`. The profile installs a game-specific `VTableLayout.ini` with four additional slots.
