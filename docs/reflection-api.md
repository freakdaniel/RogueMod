# Reflection API status

RogueMod extends reflection support as complete vertical slices: JMAP import, generated public API, managed transport, native bridge, Unreal lifetime handling, and tests must all agree before a type is marked supported.

## Supported

| Family | Properties | UFunction input/return/out | Automated transport | Deadzone live test | Managed representation |
|---|---:|---:|---:|---:|---|
| bool, signed/unsigned integers, enums, float, double | Yes | Yes | Yes | Yes | matching C# scalar |
| strong object and class references | Yes | Yes | Yes | Yes | generated wrapper / `UnrealObjectHandle` |
| `FString`, `FName`, `FText` display value | Yes | Yes | Yes | Yes | `string` |
| POD, no-destructor script structs | Yes | Yes | Yes | Yes | generated immutable record struct |
| `TArray<T>` | Yes | Yes | Yes | Yes | `IReadOnlyList<T>` |
| nested `TArray<TArray<T>>` | Yes | Yes | Yes | No target in current JMAP | recursive `IReadOnlyList<T>` |
| `TOptional<T>` with a supported non-container value | Yes | Yes | Yes | Yes, property set/unset/restore | `UnrealOptional<T>` |
| `TWeakObjectPtr<T>` | Yes | Yes | Yes | Yes, property null/restore | generated wrapper / serial-validated `UnrealObjectHandle` |
| `TLazyObjectPtr<T>` / `FLazyObjectPtr` | Yes | Yes | Yes | Yes, pending/null/restore | `UnrealLazyObjectReference<T>` with persistent `UnrealGuid` |
| object discovery | n/a | n/a | Yes | Yes | `FindFirst<T>` / `FindAll<T>` |

## UFunction hooks

ABI 13 advertises `UnrealReflectionCapabilities.FunctionHooks`. The generated SDK emits strongly typed `Register<Function>PreHook` and `Register<Function>PostHook` helpers beside every callable wrapper. Their translated values are `ref` parameters. Assigning a new value in a pre hook replaces an input/ref parameter before the original call; assigning in a post hook replaces the return or out/ref value before it reaches the caller. The low-level `UnrealHookContext` exposes the same operations as `SetArgument`, `SetReturnValue`, and `SetOutArgument`.

RogueMod only marks a parameter modified when its generated callback value changes. Replacement values are encoded through the same scalar, object, struct, string, array, optional, weak, and lazy transport used by invocation. All normal type and allocator restrictions still apply. `UnrealHookOptions` supplies a signed priority and an optional exact `UnrealObjectHandle` instance filter. Higher priorities run first; equal priorities retain registration order. Each replacement is committed before the next callback is marshalled, so hook chains observe the previous callback's value. Instance filtering occurs in native code before managed dispatch. ABI 13 does not prevent the original UFunction call: the installed UE4SS legacy global `ProcessEvent` callback export provides observation and parameter-buffer access but no callback-chain control object.

The bridge owns one UE4SS `ProcessEvent` callback per phase and filters registered function pointers internally. Disposing the returned subscription removes one registration. Remaining registrations are removed automatically before the owning managed mod is unloaded.

Nested arrays are advertised through `UnrealReflectionCapabilities.NestedArrays`. ABI 10 packs the recursive kinds into the existing 32-bit kind field, allowing at most three `TArray` containers. An older bridge does not advertise the capability, so a newer runtime rejects the operation before crossing the unmanaged boundary.

Array writes retain allocator and element-lifetime safety rules per inner type. `TArray<FName>` uses the native mutation backend: it builds the complete replacement in game-allocated scratch storage, initializes every element through its live inner `FProperty`, then swaps the finished array into the property. The previous live value is destroyed through `FArrayProperty::DestroyValue`, so grow, shrink, and clear do not free an Unreal allocation through a guessed allocator. Other element families currently remain limited to equal-length updates; nested arrays follow that restriction independently at each depth.

`TOptional<T>` is advertised through `UnrealReflectionCapabilities.OptionalValues`. Generated wrappers use `UnrealOptional<T>` instead of C# nullable annotations so the Unreal set/unset state remains distinct from a value that is itself null. ABI 10 reuses the existing 16-byte wire value: `reserved` carries the set state and `data` points to one recursively marshalled inner value. The bridge calls `FOptionalProperty` exports for state, initialization, access, and destruction; it does not assume the optional's private memory layout. Scalar, enum, string-like, strong object/class, and generated POD struct inner values are supported. Nested array/optional containers inside an optional remain explicitly rejected.

Weak object references are advertised through `UnrealReflectionCapabilities.WeakObjectReferences`. The bridge resolves reads with `FWeakObjectPtr::Get`, assigns live targets through `FWeakObjectPtr::operator=(UObject*)`, and clears them through `Reset`; it never copies the private two-index layout. Generated properties remain convenient nullable object wrappers. Those wrappers carry RogueMod's index-and-serial handle, do not keep the UObject alive, and become invalid after Unreal GC destroys the target.

Lazy object references are advertised through `UnrealReflectionCapabilities.LazyObjectReferences`. They use `UnrealLazyObjectReference<T>` rather than a nullable object wrapper because an unloaded target still has a meaningful persistent `UnrealGuid`. The native wire preserves the complete 24-byte `FLazyObjectPtr` value and separately reports its serial-safe weak cache; writing a value uses UE4SS's exported `SetPropertyValue`, so a pending identity is not collapsed into that cache. `CachedTarget` is null for null, uncached/pending, or stale references and does not load, resolve through the GUID registry, or root the object. The pinned UE4SS build exports neither GUID registry resolution nor `FUniqueObjectGuid::GetOrCreateIDForObject`, so RogueMod intentionally does not offer misleading resolution or `FromObject` operations; mods may inspect and round-trip engine-provided identities or write `Null`.

## Remaining type families

| Family | Current boundary | Required safe implementation |
|---|---|---|
| `TMap<K,V>` | JMAP imports key/value metadata; transport is absent | live key/value descriptors, sparse storage iteration, construction, destruction, and hash reindexing through Unreal APIs |
| `TSet<T>` | JMAP imports element metadata; transport is absent | live element descriptor plus Unreal-owned set allocation and hash maintenance |
| soft object/class references | generator recognizes names; runtime rejects the property kind | path-preserving value type and loaded/unloaded resolution semantics |
| interface references | generator recognizes the target class; runtime rejects the property kind | transport both object and interface identity according to `FScriptInterface` layout/helpers |
| non-POD structs | rejected | per-field construction/destruction using live `FProperty` operations |
| fixed native arrays (`array_dim > 1`) | rejected | descriptor-aware element addressing distinct from dynamic `TArray` |
| UTF-8/ANSI string property variants | rejected | explicit encoding and Unreal-owned lifetime functions |

## Deadzone: Rogue snapshot priorities

The captured 1.4 JMAP currently contains 1,163 map properties, 586 soft-object properties, 380 interface properties, 369 weak-object properties, 194 sets, 26 optionals, and 5 lazy-object properties. No nested array was present in this snapshot, so nested arrays are transport-tested but not game-confirmed.

On 2026-08-25 the live probe verified `UNiagaraSystem.LargeWorldCoordinateTileUpdateMode` against the installed Deadzone Rogue build. It read an unset value, round-tripped unset, wrote and read a set enum value, restored the original unset state, and confirmed the restoration. Generated property and UFunction optional adapters are also covered by automated native-ABI tests; the current JMAP contains no real UFunction with optional parameters, so the UFunction path cannot yet be game-confirmed.

The same probe verified `UValGameInstance.TickCallbackHelper`, a non-null `TWeakObjectPtr<UValTickCallbackHelper>`. It read the target, cleared and reread null, restored the original target, and confirmed both its path and serial-valid handle. Weak UFunction input/return is automated-transport-tested; the current JMAP has only a delegate signature with a weak parameter and no safely invokable real UFunction target.

Lazy transport was verified on the real `Default__NiagaraDataInterfaceActorComponent.SourceActor` property. The probe saved the original 24 bytes, wrote and reread a non-zero pending GUID (`524F4755-454D4F44-4C495645-54455354`), cleared and reread null, restored the original value, and compared its complete storage. This specifically verifies the unloaded/pending state that a weak-only representation would lose. Lazy UFunction input/return is automated-transport-tested; the current JMAP contains no lazy-reference UFunction. `TMap` and `TSet` remain later because their sparse hash storage additionally requires safe mutation and reindexing operations.

On 2026-08-26 the mutation probe verified grow and clear/restore for the live `BP_MainMenuPlayerController_C.Tags` `TArray<FName>`. It grew the initially empty array with a `RogueModLiveProbe` marker, reread the marker, then restored and reread the empty original. The first attempted `FProperty::CopyCompleteValue` commit was deliberately rejected after the live read still returned the original value; the confirmed backend instead commits the fully built scratch array by swapping its game-owned storage and destroys the displaced value through the array property's lifecycle.

## Object property writes

`UnrealMutationBackend.try_assign_object` owns every object-property write, which covers plain `FObjectProperty` and `CPF_TObjectPtr` slots alike. This game's shipping build stores object slots as plain eight-byte `UObject*` pointers, which is exactly the store the engine's own unchecked setter performs, so the backend writes the pointer bytes directly and verifies by re-reading them. On a mismatch the saved original bytes are restored before the write is reported as failed. Write rejections surface as native status `-7`, and a value that changed but could not be restored is reported as `-8` so probes and mods can distinguish a clean rejection from a dangerous one.

The engine getter and setter exports (`FObjectPropertyBase::GetObjectPropertyValue` / `SetObjectPropertyValue`) are unreliable in the pinned UE4SS build for Deadzone 1.4.2.0: the getter ignores the supplied value address and returns the same property-class constant for every object property, so it cannot be used for read-back verification. Object reads therefore read the raw pointer bytes too. This is safe for the pinned game build; if a future game build stores `TObjectPtr` as a handle rather than a raw pointer, the raw path must be replaced by an engine-backed setter whose vtable slot is resolved from UE4SS's own startup layout (`SetObjectPtrPropertyValueUnchecked = 0x448` on this game, slot 137; naive ini counting is wrong because UVTD leaves the FProperty virtuals between FField and the named functions unnamed).

The 2026-08-26 crash analysis (`EXCEPTION_ACCESS_VIOLATION reading 0x318`, `CrashContext.runtime-xml` and minidump captured) is consistent with a concurrent worker thread observing the probe's transiently nulled live `NiagaraSystem.SystemSpawnScript` during startup. The live probe therefore verifies object writes with swap round-trips between two valid instances of the property's declared class on class default objects (`Default__AIController.BrainComponent` and siblings), never writing null-or-foreign values on hot objects. On 2026-08-26 that round-trip passed in the installed game: the CDO's null `BrainComponent` was swapped to `Default__BrainComponent`, re-read as the alternate, then restored to null and re-read as null. Object property writes are therefore game-confirmed for this build.

## Object creation

`IUnrealReflection.CreateObject(classHandle, outerHandle, name)` constructs a new `UObject` through the engine's `UObjectGlobals::StaticConstructObject` path, gated by `UnrealReflectionCapabilities.ObjectCreation`. The native side builds an `FStaticConstructObjectParameters` (0x40 bytes) using the exported `FStaticConstructObjectParameters` constructor, whose layout was verified from the UE 5.6.1 constructor disassembly: `Class` at 0, `Outer` at 8, `FName Name` at 0x10, `SetFlags`/`InternalSetFlags` at 0x18, two defaulted bools at 0x20, `Template` at 0x28, `InstanceGraph` at 0x30, and a trailing bool at 0x38. An optional object name is written as an `FName` through the exported `FName` constructor. On 2026-08-26 the live probe created a `USceneComponent` named `RogueModLiveProbe` owned by `Default__AIController` and confirmed the returned handle, its class, and its path name, so object creation is game-confirmed.

## Actor spawning

`IUnrealReflection.SpawnActor(contextObject, classHandle, location, rotation)` spawns an actor through `UWorld::SpawnActor(UClass*, const FVector*, const FRotator*)`, resolving the world from the context object through `UObject::GetWorld`. It is gated by `UnrealReflectionCapabilities.ActorSpawning`. On 2026-08-26 the live probe spawned an empty `/Script/Engine.Actor` into the main-menu persistent level at the origin and confirmed the returned handle, its class, and its path, so actor spawning is game-confirmed.

## Soft object references

`TSoftObjectPtr` values are transported as `UnrealSoftObjectReference<T>` (path + always-null cached target) and gated by `UnrealReflectionCapabilities.SoftObjectReferences`. In UE 5.6.1 `TSoftObjectPtr` is exactly `FSoftObjectPath` (40 bytes, no weak cache): `PackageName` `FName` at 0, `AssetName` `FName` at 8, `FString` sub-path at 16, and an eight-byte engine tag at 32. The layout was verified from the `FSoftObjectPath` constructor disassembly (it zeroes `0x28` bytes). Reads rebuild `Package.Asset:SubPath` from the raw `FName`s and sub-path `FString`; writes build the `FName`s, replace the sub-path through `FString::operator=` (the proven FString property-write path), and preserve the tag bytes. The earlier UE4SS `FSoftObjectPath::SetPath`/`ToString` exports were rejected: they rely on UE4SS member-layout resolution that is unreliable for this game build and crashed the managed runtime when a live round-trip wrote through layout-derived offsets. On 2026-08-26 the live probe wrote a marker path into `Default__LevelStreaming.WorldAsset` (initially empty), re-read it as the marker, and restored the empty original without destabilizing the game, so soft references are game-confirmed.
