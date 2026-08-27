# Reflection API status

RogueMod extends reflection support as complete vertical slices: JMAP import, generated public API, managed transport, native bridge, Unreal lifetime handling, and tests must all agree before a type is marked supported.

## Supported

| Family | Properties | UFunction input/return/out | Automated transport | Deadzone live test | Managed representation |
|---|---:|---:|---:|---:|---|
| bool, signed/unsigned integers, enums, float, double | Yes | Yes | Yes | Yes | matching C# scalar |
| strong object and class references | Yes | Yes | Yes | Yes, `TObjectPtr` swap/restore | generated wrapper / `UnrealObjectHandle` |
| interface references (`FScriptInterface`) | Yes | Yes | Yes | Yes, set/clear/restore | generated wrapper / `UnrealObjectHandle` |
| `FString`, `FName`, `FText` display value | Yes | Yes | Yes | Yes | `string` |
| POD, no-destructor script structs | Yes | Yes | Yes | Yes | generated immutable record struct |
| `TArray<T>` | Yes | Yes | Yes | Yes | `IReadOnlyList<T>` |
| nested `TArray<TArray<T>>` | Yes | Yes | Yes | No target in current JMAP | recursive `IReadOnlyList<T>` |
| `TMap<K,V>` (scalar keys) | Yes, read-only | No | Yes | Pending live probe | `IReadOnlyDictionary<K,V>` |
| `TSet<T>` | Yes, read-only | No | Yes | Pending live probe | `IReadOnlySet<T>` |
| `TOptional<T>` with a supported non-container value | Yes | Yes | Yes | Yes, property set/unset/restore | `UnrealOptional<T>` |
| `TWeakObjectPtr<T>` | Yes | Yes | Yes | Yes, property null/restore | generated wrapper / serial-validated `UnrealObjectHandle` |
| `TLazyObjectPtr<T>` / `FLazyObjectPtr` | Yes | Yes | Yes | Yes, pending/null/restore | `UnrealLazyObjectReference<T>` with persistent `UnrealGuid` |
| `TSoftObjectPtr<T>` / `FSoftObjectPath` | Yes | Yes | Yes | Yes, path write/restore | `UnrealSoftObjectReference<T>` with persistent asset path |
| object discovery | n/a | n/a | Yes | Yes | `FindFirst<T>` / `FindAll<T>` |

## UFunction hooks

ABI 13 advertises `UnrealReflectionCapabilities.FunctionHooks`. The generated SDK emits strongly typed `Register<Function>PreHook` and `Register<Function>PostHook` helpers beside every callable wrapper. Their translated values are `ref` parameters. Assigning a new value in a pre hook replaces an input/ref parameter before the original call; assigning in a post hook replaces the return or out/ref value before it reaches the caller. The low-level `UnrealHookContext` exposes the same operations as `SetArgument`, `SetReturnValue`, and `SetOutArgument`.

RogueMod only marks a parameter modified when its generated callback value changes. Replacement values are encoded through the same scalar, object, interface, struct, string, array, optional, weak, and lazy transport used by invocation. All normal type and allocator restrictions still apply. `UnrealHookOptions` supplies a signed priority and an optional exact `UnrealObjectHandle` instance filter. Higher priorities run first; equal priorities retain registration order. Each replacement is committed before the next callback is marshalled, so hook chains observe the previous callback's value. Instance filtering occurs in native code before managed dispatch. ABI 13 does not prevent the original UFunction call: the installed UE4SS legacy global `ProcessEvent` callback export provides observation and parameter-buffer access but no callback-chain control object.

The bridge owns one UE4SS `ProcessEvent` callback per phase and filters registered function pointers internally. Disposing the returned subscription removes one registration. Remaining registrations are removed automatically before the owning managed mod is unloaded.

Nested arrays are advertised through `UnrealReflectionCapabilities.NestedArrays`. ABI 10 packs the recursive kinds into the existing 32-bit kind field, allowing at most three `TArray` containers. An older bridge does not advertise the capability, so a newer runtime rejects the operation before crossing the unmanaged boundary.

Array writes retain allocator and element-lifetime safety rules per inner type. `TArray<FName>` uses the native mutation backend: it builds the complete replacement in game-allocated scratch storage, initializes every element through its live inner `FProperty`, then swaps the finished array into the property. The previous live value is destroyed through `FArrayProperty::DestroyValue`, so grow, shrink, and clear do not free an Unreal allocation through a guessed allocator. Other element families currently remain limited to equal-length updates; nested arrays follow that restriction independently at each depth.

`TOptional<T>` is advertised through `UnrealReflectionCapabilities.OptionalValues`. Generated wrappers use `UnrealOptional<T>` instead of C# nullable annotations so the Unreal set/unset state remains distinct from a value that is itself null. ABI 10 reuses the existing 16-byte wire value: `reserved` carries the set state and `data` points to one recursively marshalled inner value. The bridge calls `FOptionalProperty` exports for state, initialization, access, and destruction; it does not assume the optional's private memory layout. Scalar, enum, string-like, strong object/class, and generated POD struct inner values are supported. Nested array/optional containers inside an optional remain explicitly rejected.

Weak object references are advertised through `UnrealReflectionCapabilities.WeakObjectReferences`. The bridge resolves reads with `FWeakObjectPtr::Get`, assigns live targets through `FWeakObjectPtr::operator=(UObject*)`, and clears them through `Reset`; it never copies the private two-index layout. Generated properties remain convenient nullable object wrappers. Those wrappers carry RogueMod's index-and-serial handle, do not keep the UObject alive, and become invalid after Unreal GC destroys the target.

Lazy object references are advertised through `UnrealReflectionCapabilities.LazyObjectReferences`. They use `UnrealLazyObjectReference<T>` rather than a nullable object wrapper because an unloaded target still has a meaningful persistent `UnrealGuid`. The native wire preserves the complete 24-byte `FLazyObjectPtr` value and separately reports its serial-safe weak cache; writing a value uses UE4SS's exported `SetPropertyValue`, so a pending identity is not collapsed into that cache. `CachedTarget` is null for null, uncached/pending, or stale references and does not load, resolve through the GUID registry, or root the object. The pinned UE4SS build exports neither GUID registry resolution nor `FUniqueObjectGuid::GetOrCreateIDForObject`, so RogueMod intentionally does not offer misleading resolution or `FromObject` operations; mods may inspect and round-trip engine-provided identities or write `Null`.

## Remaining type families

| Family | Current boundary | Required safe implementation |
|---|---|---|
| `TMap<K,V>` writes | reads only; writes rejected | construction, destruction, and hash reindexing through Unreal APIs |
| `TSet<T>` writes | reads only; writes rejected | live element descriptor plus Unreal-owned set allocation and hash maintenance |
| non-POD structs | rejected | per-field construction/destruction using live `FProperty` operations |
| fixed native arrays (`array_dim > 1`) | rejected | descriptor-aware element addressing distinct from dynamic `TArray` |
| UTF-8/ANSI string property variants | rejected | explicit encoding and Unreal-owned lifetime functions |

## Deadzone: Rogue snapshot priorities

The captured 1.4 JMAP currently contains 1,163 map properties, 586 soft-object properties, 380 interface properties, 369 weak-object properties, 194 sets, 26 optionals, and 5 lazy-object properties. No nested array was present in this snapshot, so nested arrays are transport-tested but not game-confirmed.

`TMap` and `TSet` reads are advertised through `UnrealReflectionCapabilities.MapSetProperties`. The bridge does not guess container layout from process memory. For the pinned Deadzone: Rogue 1.4.2.0 build, every live `FMapProperty`/`FSetProperty` reports an 80-byte `FScriptMap`/`FScriptSet` footprint (the Valhalla fork adds eight bytes over vanilla UE 5.6.1's 72), and every map/set read is gated on that footprint plus the runtime `FScriptMapLayout`/`FScriptSetLayout` returned by the pinned UE4SS exports. Sparse iteration reads only the vanilla-layout `TScriptSparseArray` `Data`/`AllocationFlags` fields at offsets 0/16 and derives the element count from the allocation bits, so the fork's trailing eight bytes do not affect reads. A game update that changes the footprint or the runtime layout disables the family instead of dereferencing bad offsets. Values cross the ABI as a bounded array of alternating key/value wire values (maps) or element wire values (sets); keys are scalar-only, and map values and set elements may contain one nested `TArray`. Writes are rejected before touching game memory until the game's own `FScriptSet`/`FScriptMap` construction and rehash operations are available through a compiled adapter.

Interface reads, UFunction parameters, and hook replacement are covered by the automated native-ABI transport test through kind 22 (`FScriptInterface`). On 2026-08-27 the live probe verified interface reads and persistent writes on a real `ChooserColumnBool.InputValue` interface property: set (`SetInterfacePropertyByName`, the self-referential value implements the interface), clear (direct null write), and restore all round-tripped, every probe feature passed, and the game exited without a crash report, so interface references are game-confirmed including persistent writes.

On 2026-08-25 the live probe verified `UNiagaraSystem.LargeWorldCoordinateTileUpdateMode` against the installed Deadzone Rogue build. It read an unset value, round-tripped unset, wrote and read a set enum value, restored the original unset state, and confirmed the restoration. Generated property and UFunction optional adapters are also covered by automated native-ABI tests; the current JMAP contains no real UFunction with optional parameters, so the UFunction path cannot yet be game-confirmed.

The same probe verified `UValGameInstance.TickCallbackHelper`, a non-null `TWeakObjectPtr<UValTickCallbackHelper>`. It read the target, cleared and reread null, restored the original target, and confirmed both its path and serial-valid handle. Weak UFunction input/return is automated-transport-tested; the current JMAP has only a delegate signature with a weak parameter and no safely invokable real UFunction target.

Lazy transport was verified on the real `Default__NiagaraDataInterfaceActorComponent.SourceActor` property. The probe saved the original 24 bytes, wrote and reread a non-zero pending GUID (`524F4755-454D4F44-4C495645-54455354`), cleared and reread null, restored the original value, and compared its complete storage. This specifically verifies the unloaded/pending state that a weak-only representation would lose. Lazy UFunction input/return is automated-transport-tested; the current JMAP contains no lazy-reference UFunction. `TMap` and `TSet` remain later because their sparse hash storage additionally requires safe mutation and reindexing operations.

On 2026-08-26 the mutation probe verified grow and clear/restore for the live `BP_MainMenuPlayerController_C.Tags` `TArray<FName>`. It grew the initially empty array with a `RogueModLiveProbe` marker, reread the marker, then restored and reread the empty original. The first attempted `FProperty::CopyCompleteValue` commit was deliberately rejected after the live read still returned the original value; the confirmed backend instead commits the fully built scratch array by swapping its game-owned storage and destroys the displaced value through the array property's lifecycle.

## Object property writes

The installed UE4SS `VTableLayout.ini` maps the object getter and setters to the wrong virtual slots for Deadzone: Rogue 1.4.2.0. Calling those entries, writing the eight-byte slot directly, or routing the assignment through `KismetSystemLibrary.SetObjectPropertyByName` can appear to pass an immediate read-back test and still terminate the game later. RogueMod does not use any of those paths.

For the pinned game build, RogueMod resolves the actual `FObjectPtrProperty` getter and `SetObjectPtrPropertyValueUnchecked` implementation from the live property vtable. Before enabling object access, it validates structural machine-code signatures for both functions. The setter receives the required pointer-to-temporary argument and executes the engine's incremental-GC write barrier before committing the reference. A write is reread through the validated engine getter; if verification fails, the original value is restored through the same setter. A game update that changes either signature disables object access instead of calling an unrecognized virtual function.

Generated strong object properties are writable. The same backend handles strong object/class values in temporary `ProcessEvent` parameter buffers, hook replacements, and equal-length object-array element replacement. Reads are converted immediately to serial-validated `UnrealObjectHandle` values. On 2026-08-26 the live probe swapped `AActor.Owner` between two transient actors, reread the replacement, restored null, destroyed both actors, ran every other reflection probe, and then exited the game without a crash report or a new crash artifact.

## Interface references

`FScriptInterface` is advertised through `UnrealReflectionCapabilities.InterfaceReferences`. The generated SDK emits a wrapper class for the target `UInterface` (a `UObject` subclass, so the generated class inherits the usual `UnrealObject` chain), and interface-typed properties and parameters use that wrapper. The transported value is the object implementing the interface; reads and writes go through the same object-handle rules as strong object references.

A `FScriptInterface` is a 16-byte pair: a raw `UObject*` object pointer at +0 and an `IInterface*` interface pointer at +8. All 380 interface properties in the captured Deadzone JMAP are 16 bytes with `CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor` and no `CPF_TObjectPtr`, so the object pointer is a plain pointer rather than a `TObjectPtr`. Reads copy the object pointer at +0 into a serial-validated handle. Writes into temporary `ProcessEvent` parameter buffers and hook replacement slots store the object pointer at +0 and zero the interface pointer at +8, because the engine lazily re-resolves the interface pointer from the object; this mirrors the temporary-slot rule that makes strong object parameters safe.

Persistent interface-property writes are supported through the engine's own `KismetSystemLibrary.SetInterfacePropertyByName`. The engine finds the property by name and validates `UClass::ImplementsInterface` before assigning, so a value whose object does not implement the target interface is rejected rather than silently written; the bridge verifies the object pointer landed and returns a failure status otherwise. Null values are written directly by zeroing the `FScriptInterface` slot, because `SetInterfacePropertyByName` skips null objects in UE5; removing a reference is safe without a write barrier. On 2026-08-27 the live probe created an inert `ChooserColumnBool` and confirmed set, clear, and restore on its real `InputValue` interface property: the self-referential value (the object implements `ChooserParameterBool`) survived a write/read round-trip, null cleared, and the original was restored, with every probe feature completing and the game exiting cleanly.

## Object creation

`IUnrealReflection.CreateObject(classHandle, outerHandle, name)` constructs a new `UObject` through the engine's `UObjectGlobals::StaticConstructObject` path, gated by `UnrealReflectionCapabilities.ObjectCreation`. The native side builds an `FStaticConstructObjectParameters` (0x40 bytes) using the exported `FStaticConstructObjectParameters` constructor, whose layout was verified from the UE 5.6.1 constructor disassembly: `Class` at 0, `Outer` at 8, `FName Name` at 0x10, `SetFlags`/`InternalSetFlags` at 0x18, two defaulted bools at 0x20, `Template` at 0x28, `InstanceGraph` at 0x30, and a trailing bool at 0x38. An optional object name is written as an `FName` through the exported `FName` constructor. On 2026-08-26 the live probe created a `USceneComponent` named `RogueModLiveProbe` owned by `Default__AIController`, confirmed the returned handle, class, and path name, and destroyed the component through `K2_DestroyComponent`, so object creation is game-confirmed without leaving test state alive during shutdown.

## Actor spawning

`IUnrealReflection.SpawnActor(contextObject, classHandle, location, rotation)` spawns an actor through `UWorld::SpawnActor(UClass*, const FVector*, const FRotator*)`, resolving the world from the context object through `UObject::GetWorld`. It is gated by `UnrealReflectionCapabilities.ActorSpawning`. On 2026-08-26 the live probe spawned an empty `/Script/Engine.Actor` into the main-menu persistent level at the origin, confirmed the returned handle, class, and path, and immediately destroyed the probe actor through `K2_DestroyActor`, so actor spawning is game-confirmed without leaving test state alive during shutdown.

## Soft object references

`TSoftObjectPtr` values are transported as `UnrealSoftObjectReference<T>` and gated by `UnrealReflectionCapabilities.SoftObjectReferences`. Generated properties and function parameters use this typed wrapper directly; `FromPath` creates an unloaded reference without resolving or loading the asset. The 40-byte value is intentionally treated as opaque. Reads call the game's reflected `KismetSystemLibrary.Conv_SoftObjectReferenceToString` with a borrowed const-reference copy. Writes call `MakeSoftObjectPath`, `Conv_SoftObjPathToSoftObjRef`, and `SetSoftObjectPropertyByName`, moving the game-created temporary values between validated `ProcessEvent` buffers and releasing them through the non-virtual type-specific destructor export. `UFunction` inputs and hook replacements use the same game-built temporary in their initialized parameter slot; outputs are converted back to a path before that slot is destroyed.

This backend exists because the pinned UE4SS build resolves the relevant property/member-layout operations incorrectly for Deadzone: Rogue. The rejected raw backend inferred `FName`/`FString` offsets inside the value: its immediate write/read round-trip appeared correct, but every shutdown later crashed with `EXCEPTION_ACCESS_VIOLATION` at `0x0000000800000003`, consistent with corrupted `FString` lifetime state. Isolated live tests proved the crash occurred with only the soft round-trip enabled; the Kismet backend then wrote the same marker into `Default__LevelStreaming.WorldAsset`, re-read the complete `/Game/Test/RogueModLiveProbe.RogueModLiveProbe` path, restored the empty original, and exited cleanly without a new crash report. Soft references are therefore game-confirmed without relying on the broken UE4SS setter or private layout guesses.
