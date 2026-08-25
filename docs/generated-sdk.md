# Generated game SDK

The SDK layer hides raw `UObject` lookup, `FProperty` traversal, field offsets, and manual `ProcessEvent` buffer construction from mod authors.

## Pipeline

1. UE4SS writes a `.jmap` containing classes, structs, enums, `UFunction` objects, and properties.
2. `RogueMod.Sdk.JMapImporter` converts that external schema into a versioned internal model.
3. `CSharpSdkGenerator` emits typed wrappers with private runtime descriptors and a buildable NuGet project.
4. `RogueMod.Runtime` validates capabilities and sends operations to the native bridge.
5. The bridge validates live buffer metadata and performs the Unreal call.

JMAP is an importer input, not a public mod API. Changes to its external schema remain isolated in `RogueMod.Sdk`; mod authors consume only the maintained `DeadzoneRogue.Sdk` NuGet package.

## Mod author workflow

Add the authoring targets and typed game SDK to the mod project:

```xml
<ItemGroup>
  <PackageReference Include="RogueMod.Sdk" Version="0.1.0" />
  <PackageReference Include="DeadzoneRogue.Sdk" Version="0.1.0" />
</ItemGroup>
```

No local game installation, UE4SS developer build, dump hotkey, or JMAP file is required. `PackageRogueMod` excludes `DeadzoneRogue.Sdk.dll` from the mod output. The matching assembly is installed once in `<GameRoot>/RogueMod/runtime/shared` and resolved into every collectible mod context by the runtime. Exact assembly-version matching prevents a mod from silently running against an incompatible generated SDK.

## Maintainer capture

The pinned asset and game compatibility are recorded in `config/GameSdk/deadzone-rogue.json`. `prepare-game-sdk.ps1` verifies the installed executable version, downloads the exact official UE4SS archive, validates its SHA-256, installs the game-specific `VTableLayout.ini`, disables unrelated bundled mods, and enables the maintainer dumper. The dumper schedules one automatic snapshot after GameState initialization; `Ctrl+F5` remains only as a fallback.

```powershell
./scripts/prepare-game-sdk.ps1 `
  -GamePath 'E:\Steam\steamapps\common\Deadzone Rogue' `
  -InstallRuntime

./scripts/build-game-sdk.ps1 `
  -GamePath 'E:\Steam\steamapps\common\Deadzone Rogue' `
  -WaitForDumpSeconds 900
```

The build script waits until UE4SS has closed the snapshot file, generates a standalone CPM-enabled project, builds `.nupkg`/`.snupkg`, installs the game SDK into the shared runtime directory, updates the runtime archive metadata, and disables the dumper. Raw JMAP and generated source remain under ignored artifact paths.

For generator debugging, invoke the lower-level command directly:

```bash
dotnet run --project src/RogueMod.Cli -c Release -- generate-sdk \
  --game "/path/to/Deadzone Rogue" \
  --output ".artifacts/sdk/deadzone-rogue"
```

Use `--jmap <file>` to select an exact dump and `--namespace <name>` to override the generated namespace.

The output directory is a standalone generated project:

```bash
dotnet build .artifacts/sdk/deadzone-rogue/DeadzoneRogue.Sdk.csproj -c Release
dotnet pack src/RogueMod.Abstractions/RogueMod.Abstractions.csproj \
  -c Release --no-build -o .artifacts/sdk/packages
dotnet pack .artifacts/sdk/deadzone-rogue/DeadzoneRogue.Sdk.csproj \
  -c Release --no-build -o .artifacts/sdk/packages
```

Inside this repository the generated project can reference `src/RogueMod.Abstractions`. A standalone release uses its own `Directory.Packages.props` and the `RogueMod.Abstractions` package dependency. Generated sources and packages belong under `.artifacts`; reusable generator and authoring code stays under `src/RogueMod.Sdk`.

## Generated API

Each reflected class receives:

- inheritance matching Unreal reflection;
- `FindFirst(IUnrealReflection)`, `FindAll(IUnrealReflection)`, and object-handle construction;
- typed C# properties for supported primitive, enum, POD struct, and object-wrapper types;
- typed methods with named inputs and direct return values;
- generated result records for `out/ref` parameters;
- private property and function descriptors containing paths and buffer layout metadata.

Example target API:

```csharp
var player = context.Unreal.FindFirst<BP_Player>();
if (player is not null)
{
    player.Health = 250.0f;
    player.SetPauseMenuVisible(true);
    var pawnHealth = player.Pawn?.Health;
}

IReadOnlyList<BP_Player> allPlayers = context.Unreal.FindAll<BP_Player>();
```

ABI 10 supports single and multi-object discovery; bool, integer, enum, float, double, strong, weak, and lazy object references, `FString`, `FName`, `FText`, POD script structs, and capability-gated `TArray` and `TOptional` values in properties and `UFunction` input/return/out parameters. Arrays may contain other arrays up to three `TArray` containers deep. `FindAll<T>` uses UE4SS class matching and returns generated wrappers around serial-validated handles, never raw pointers. String-like values are exposed as C# `string`; arrays are exposed recursively as `IReadOnlyList<T>`; optionals use `UnrealOptional<T>` to preserve set/unset. Weak references use the same nullable generated wrapper shape as strong references. Lazy references use `UnrealLazyObjectReference<T>` so their persistent `UnrealGuid` survives while `Target` is unloaded; neither form roots the target. Descriptors include offsets, element sizes, bool masks, nested struct layout, recursive array inner metadata, and optional inner metadata from JMAP. Before `ProcessEvent`, the bridge verifies the live parameter count, total buffer size, return offset, live parameter offsets and sizes, and descriptor bounds.

The generator emits an immutable C# record struct plus `Descriptor`, `ToUnrealValue`, and `FromUnrealValue` adapters for JMAP structs marked both `STRUCT_IsPlainOldData` and `STRUCT_NoDestructor`. The struct must have no reflected superclass, all fields must be supported scalar or nested POD values, and every field must fit the declared native size. Marshalling is field-wise and therefore does not rely on CLR layout.

The generator emits typed array adapters only when the leaf element is a supported scalar, enum, strong object/class reference, string-like value, generated POD struct, or another supported array within the depth limit. It emits `UnrealOptional<T>` adapters for supported scalar, enum, strong object/class, string-like, and POD struct values. Standalone weak object properties and function parameters use typed nullable wrappers; standalone lazy properties and parameters use identity-preserving `UnrealLazyObjectReference<T>`. Nested containers inside an optional, unsupported structs, weak/lazy-object arrays, soft references, UTF-8/ANSI string properties, maps, and sets still produce explicit runtime failures rather than unsafe buffer writes. See [Reflection API status](reflection-api.md) for the remaining type families.
