# RogueMod.Abstractions API guide

`RogueMod.Abstractions` is the stable API referenced by every managed mod. This guide walks the surface by task; the machine-generated member reference lives under [API reference](/api/RogueMod.Abstractions) (and in your IDE through the XML docs shipped in the NuGet package).

> [!IMPORTANT]
> Everything in this assembly is a compatibility surface. Additions are cheap; renames and behavior changes are breaking and require a version bump.

## Mod lifecycle

A managed mod implements `IRogueMod` and optionally `IRogueModGameEvents`:

```csharp
public sealed class ExampleMod : IRogueMod, IRogueModGameEvents
{
    private IModContext? context;

    public ValueTask LoadAsync(IModContext modContext, CancellationToken cancellationToken = default)
    {
        context = modContext;
        context.Logger.Log(ModLogLevel.Information, "Loaded.");
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        context = null;
        return ValueTask.CompletedTask;
    }
}
```

- `IModContext` provides `ModId`, `GameProfileId`, `Logger`, and `Unreal`.
- `ModGameEventKind` values: `ProgramStarted`, `UnrealInitialized`, `UiInitialized`, `Update`, `CppModsLoaded`. Only `ProgramStarted` and `Update` are game-confirmed in Deadzone: Rogue.
- Event handlers run on the game thread; the first thrown exception disables later events for that mod.

> [!NOTE]
> Reflection becomes available around the first `Update`. Retry or defer until `IUnrealReflection.IsAvailable` returns true.

## Logging

```csharp
context.Logger.Log(ModLogLevel.Warning, "Unexpected state");
```

`IModLogger` writes to `RogueMod.log` under the `[C#:<mod-id>]` prefix. Levels range from `Trace` to `Critical`.

## Object discovery

Handles, not pointers: `UnrealObjectHandle` encodes a `GUObjectArray` slot index and serial.

```csharp
var unreal = context.Unreal;
if (!unreal.IsAvailable) return;

var handle = unreal.FindFirstOf("PlayerController");
if (!handle.IsNull && unreal.IsValid(handle))
{
    var path = unreal.GetPathName(handle);
    var classHandle = unreal.GetClass(handle);
}
```

With generated wrappers, prefer type-safe discovery:

```csharp
var controller = unreal.FindFirst<ValPlayerController>();
foreach (var character in unreal.FindAll<ValCharacter>()) { }
```

`FindAllOf` requires the `UnrealReflectionCapabilities.ObjectEnumeration` capability.

## Capabilities

`IUnrealReflection.Capabilities` reports what the installed bridge supports. Check `UnrealReflectionCapabilities.FunctionHooks`, `ObjectCreation`, `ActorSpawning`, `MapSetWrites`, and friends before optional operations; unsupported members throw `NotSupportedException` rather than failing silently.

## Properties

Property access takes descriptors emitted by the generated SDK:

```csharp
var value = unreal.ReadProperty(handle, descriptor);
var amount = value.As<UnrealStructValue>().GetField("Amount").As<float>();

unreal.WriteProperty(handle, descriptor, UnrealValue.From(100f));
```

`UnrealValue` is the transport envelope: `UnrealValue.From<T>` wraps, `As<T>` unwraps (including enum conversion), `AsObjectHandle` unwraps references.

## Invoking UFunctions

```csharp
var result = unreal.Invoke(handle, functionDescriptor,
    new[] { new UnrealArgument("Amount", UnrealValue.From(10f)) });
float output = result.ReturnValue.As<float>();
float outValue = result.GetOut<float>("OutDamage");
```

`UnrealInvocationResult.GetOut<T>` reads out/ref arguments by reflected name.

## Hooks

```csharp
var subscription = unreal.RegisterHook(
    functionDescriptor,
    UnrealHookPhase.Pre,
    new UnrealHookOptions(Priority: 100, Instance: controllerHandle),
    hook =>
    {
        var damage = hook.Arguments["DamageData"].As<UnrealStructValue>();
        hook.SetArgument("DamageData", UnrealValue.From(damage));
    });
```

- Pre hooks may `SetArgument`; post hooks may `SetReturnValue` and `SetOutArgument`.
- Higher `Priority` runs first; `Instance` filters to one exact object.
- Dispose the subscription to remove the hook early; the runtime removes the rest at unload.

> [!NOTE]
> Generated wrappers expose strongly typed `Register<Function>PreHook`/`PostHook` methods with `ref` parameters — prefer them over raw descriptors. See [Managed mod API guide](managed-api.md).

## Transport types

| Unreal concept | Managed type | Notes |
| --- | --- | --- |
| Script struct | `UnrealStructValue` | Field-wise; `GetField(name)` |
| TArray | `UnrealArrayValue` | Up to three nested containers |
| TMap / TSet | `UnrealMapValue` / `UnrealSetValue` | Scalar keys; write support gated |
| TOptional | `UnrealOptional<T>` / `UnrealOptionalValue` | Preserves set/unset state |
| Soft reference | `UnrealSoftObjectReference<T>` | `FromPath(path)` builds an unloaded reference |
| Lazy reference | `UnrealLazyObjectReference<T>` | Persistent GUID preserved |
| Vector / Rotator / Guid | `UnrealVector` / `UnrealRotator` / `UnrealGuid` | Direct value transport |

Descriptor records (`UnrealStructDescriptor`, `UnrealArrayDescriptor`, `UnrealMapDescriptor`, `UnrealSetDescriptor`, `UnrealOptionalDescriptor`, `UnrealParameterDescriptor`, `UnrealFunctionDescriptor`, `UnrealPropertyDescriptor`) carry the exact native layout. Mod authors rarely construct them by hand — the generated SDK emits them — but every write is validated against them before crossing the boundary.

## Generated wrapper base

`UnrealObject` is the base class of every generated wrapper: it pairs a handle with `IUnrealReflection` and exposes `IsValid`, `PathName`, and protected read/write/call helpers. `IUnrealObjectType<TSelf>` provides the static contract used by `FindFirst<T>`/`FindAll<T>`.
