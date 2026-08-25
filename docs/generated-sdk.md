# Generated game SDK

The SDK layer hides raw `UObject` lookup, `FProperty` traversal, field offsets, and manual `ProcessEvent` buffer construction from mod authors.

## Pipeline

1. UE4SS writes a `.jmap` containing classes, structs, enums, `UFunction` objects, and properties.
2. `RogueMod.Sdk.JMapImporter` converts that external schema into a versioned internal model.
3. `CSharpSdkGenerator` emits typed wrappers with private runtime descriptors and a buildable NuGet project.
4. `RogueMod.Runtime` validates capabilities and sends operations to the native bridge.
5. The bridge validates live buffer metadata and performs the Unreal call.

JMAP is an importer input, not a public mod API. Changes to its external schema remain isolated in `RogueMod.Sdk`.

## Create a dump

The bundled UE4SS dump key uses a numpad key. RogueMod includes `src/RogueMod.Tooling.SdkDumper`, which binds `Ctrl+F5` and calls `DumpJMAP(true, true)`. Trigger it after the main menu has loaded. Blueprint types are included while unnecessary CDO SDK values are omitted to reduce dump size and memory pressure.

Generate source from the newest dump in the game directory:

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

Inside this repository the generated project references `src/RogueMod.Abstractions`. Outside the repository it falls back to the `RogueMod.Abstractions` package dependency. Generated sources and packages belong under `.artifacts`; reusable generator and authoring code stays under `src/RogueMod.Sdk`.

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

ABI 10 supports single and multi-object discovery; bool, integer, enum, float, double, object, `FString`, `FName`, `FText`, POD script structs, and one-dimensional `TArray` values in properties and `UFunction` input/return/out parameters. `FindAll<T>` uses UE4SS class matching and returns generated wrappers around serial-validated handles, never raw pointers. String-like values are exposed as C# `string`; arrays are exposed as `IReadOnlyList<T>`. Descriptors include offsets, element sizes, bool masks, nested struct layout, and array inner metadata from JMAP. Before `ProcessEvent`, the bridge verifies the live parameter count, total buffer size, return offset, live parameter offsets and sizes, and descriptor bounds.

The generator emits an immutable C# record struct plus `Descriptor`, `ToUnrealValue`, and `FromUnrealValue` adapters for JMAP structs marked both `STRUCT_IsPlainOldData` and `STRUCT_NoDestructor`. The struct must have no reflected superclass, all fields must be supported scalar or nested POD values, and every field must fit the declared native size. Marshalling is field-wise and therefore does not rely on CLR layout.

The generator emits typed array adapters only when the element is a supported scalar, enum, object/class reference, string-like value, or generated POD struct. Unsupported structs, nested arrays, weak/soft object arrays, UTF-8/ANSI string properties, maps, sets, and optionals are represented as `UnrealValue`. Calling such a member produces an explicit `NotSupportedException` rather than an unsafe buffer write.
