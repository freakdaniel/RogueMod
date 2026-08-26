# Managed mod API

A managed mod implements `IRogueMod`. Event delivery is optional through `IRogueModGameEvents`:

```csharp
public sealed class ExampleMod : IRogueMod, IRogueModGameEvents
{
    public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Log(ModLogLevel.Information, "Loaded.");
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
```

Available event identifiers are `ProgramStarted`, `UnrealInitialized`, `UiInitialized`, `Update`, and `CppModsLoaded`. Not every game invokes every UE4SS callback. `ProgramStarted` and `Update` are confirmed in Deadzone: Rogue.

Event handlers execute on the game thread and must remain short and non-blocking. The first handler exception disables later events for that mod for the rest of the session; it does not unload or disable other packages.

## Logging

`RogueMod.log` uses explicit sources:

- `[Bridge]` for native host startup;
- `[ManagedRuntime]` for discovery and lifecycle;
- `[C#:<mod-id>]` for managed mod output.

UE4SS correctly reports `RogueModBridge` as a C++ mod because the bridge DLL is native. Managed packages are reported only through the C# source prefix.

## Unreal reflection

`IModContext.Unreal` exposes the stable reflection boundary:

```csharp
if (context.Unreal.IsAvailable)
{
    var controller = context.Unreal.FindFirstOf("PlayerController");
    if (!controller.IsNull && context.Unreal.IsValid(controller))
    {
        var objectPath = context.Unreal.GetPathName(controller);
        var classPath = context.Unreal.GetPathName(context.Unreal.GetClass(controller));
    }
}
```

`UnrealObjectHandle` is not a raw pointer. It encodes the index and serial of a `GUObjectArray` slot. `IsValid` rejects the handle after Unreal GC destroys the object, even if the slot is later reused.

Reflection becomes available on the first `Update` and must be used on the game thread. Check `IUnrealReflection.Capabilities` before optional operations.

ABI 13 supports `FindFirstOf`, `FindAllOf`, `IsValid`, `GetClass`, `GetPathName`, property reads and writes, input, return, and out/ref `UFunction` parameters, mutable pre/post UFunction hooks, object creation through the engine's `StaticConstructObject` path, and actor spawning through `UWorld::SpawnActor`. `CreateObject(classHandle, outerHandle, name)` constructs a new `UObject` of the requested class, optionally owned by an outer and optionally named; it requires the `ObjectCreation` capability and resolves nothing internally, so callers obtain the class object first (for example `FindFirstOf("/Script/Engine.SceneComponent")` resolves the class object for a native class path). `SpawnActor(contextObject, classHandle, location, rotation)` resolves the world through `UObject::GetWorld` on the context object and spawns an actor of the given class at the supplied `UnrealVector`/`UnrealRotator`; it requires the `ActorSpawning` capability. Soft object references (`TSoftObjectPtr` / `FSoftObjectPath`) are exposed as `UnrealSoftObjectReference<T>` and require `SoftObjectReferences`; they carry the persistent asset path and are assigned by rebuilding the `PackageName`/`AssetName` `FName`s and the sub-path `FString` directly in the live value (layout verified from the game binary), bypassing the unreliable UE4SS `FSoftObjectPath` member-layout exports. Generated wrappers implement `IUnrealObjectType<T>`, so mod code can use `context.Unreal.FindFirst<MyType>()` and `context.Unreal.FindAll<MyType>()` without class-name strings or manual wrapper construction. Supported scalar kinds are bool, signed and unsigned integers, enums, float, double, strong, weak, and lazy object/class handles, `FString`, `FName`, and `FText`. `TArray` values use recursive `IReadOnlyList<T>` wrappers for up to three array containers and require the `NestedArrays` capability when nested. `TOptional<T>` uses `UnrealOptional<T>` and requires `OptionalValues`; this preserves set/unset separately from null. Weak object properties require `WeakObjectReferences`; their generated wrappers do not root the UObject and retain the normal serial validation. Lazy properties require `LazyObjectReferences` and use `UnrealLazyObjectReference<T>` so `ObjectId` remains available while `CachedTarget` is pending, uncached, or unloaded. Unreal strings and text are exposed as C# `string`; embedded null characters and values longer than 1,048,576 UTF-16 code units are rejected. Writing an `FText` creates display text from the supplied string and therefore does not preserve the original localization history or namespace/key identity.

Generated classes expose `Register<Function>PreHook` and `Register<Function>PostHook`. Their callback delegates contain the generated owner wrapper and translated parameter types, including structs, arrays, optionals, and object wrappers. Keep the returned `IDisposable` when a hook should be removed early. The runtime also removes every remaining subscription automatically when its owning mod unloads. Check `UnrealReflectionCapabilities.FunctionHooks` before optional registration when supporting older bridge installations.

```csharp
var subscription = PlayerController.RegisterAddYawInputPreHook(
    context.Unreal,
    (PlayerController controller, ref float value) =>
    {
        context.Logger.Log(ModLogLevel.Information, $"{controller.PathName}: AddYawInput({value})");
        value *= 0.5f;
    },
    new UnrealHookOptions(Priority: 100, Instance: playerController.Handle));
```

Generated post hooks use the same shape for return and out/ref values. Assigning the `ref` value requests replacement; leaving it unchanged avoids a native write. The optional `UnrealHookOptions` argument defaults to priority zero and all instances. Higher values run first, equal priorities are stable, and a non-null `Instance` is validated during registration and filtered before entering managed code. Hook cancellation is not exposed because the installed UE4SS callback ABI does not provide safe control over the original call.

Plain-old-data script structs are transported field by field through generated adapters. This includes nested POD structs whose JMAP metadata marks them `STRUCT_IsPlainOldData` and `STRUCT_NoDestructor`. The runtime validates the struct path, declared size, field bounds, scalar widths, bool masks, and nested descriptors before crossing the native boundary. It never copies CLR object layout into an Unreal buffer.

One-dimensional `TArray` values are exposed as `IReadOnlyList<T>`. Supported element kinds are numeric and bool scalars, enums, object/class handles, `FString`, `FName`, `FText`, and generated POD structs. Array descriptors retain the exact inner type, native element size, bool layout, and optional struct descriptor. The bridge validates live inner-property metadata, allocates Unreal storage through `FMemory`, applies type-specific construction and destruction, and caps transported arrays at 1,048,576 elements and 64 MiB of native element storage.

Deadzone: Rogue 1.4.2.0 has a build-specific `TObjectPtr` representation that cannot be safely replaced through the currently exported UE4SS property setter. Array reads and `UFunction` outputs are supported, and equal-size writes that do not replace `TObjectPtr` elements are supported. Attempts to change elements of a non-empty object-pointer array fail explicitly rather than performing a raw pointer write. A build-specific setter is required before that mutation can be enabled.

Structs with inheritance, destructors, unsupported fields, or fixed arrays remain `UnrealValue` and fail explicitly when used. Nested containers inside `TOptional`, weak/lazy-object arrays, soft object references, maps, sets, `FText` arrays with localization-history preservation, and UTF-8/ANSI string properties are not yet marshalled. Nested `TArray` transport exists, but the current Deadzone JMAP has no live property with that shape, so it is not yet game-confirmed.
