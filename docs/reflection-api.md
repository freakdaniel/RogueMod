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

Array writes retain the existing allocator safety rules. Equal-length arrays are updated in place. A non-empty UE-owned allocation is not resized because the exact owning allocator cannot yet be proven from the installed UE4SS API. Nested arrays follow the same rule independently at each depth.

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
