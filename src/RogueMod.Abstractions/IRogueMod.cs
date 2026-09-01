namespace RogueMod.Abstractions;

/// <summary>Stable managed entry point implemented by a C# mod.</summary>
public interface IRogueMod
{
    /// <summary>
    /// Loads the mod. Called once per session before any game event is delivered.
    /// Store the <see cref="IModContext"/> and subscribe hooks here; long work must be
    /// deferred to game events because loading runs on the game thread.
    /// </summary>
    /// <param name="context">The mod context owning this mod's subscriptions and logger.</param>
    /// <param name="cancellationToken">Signals that RogueMod is shutting down during load.</param>
    ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads the mod. RogueMod removes remaining hook subscriptions automatically after
    /// this returns; dispose manually only when a subscription must be released earlier.
    /// </summary>
    /// <param name="cancellationToken">Signals that the host is shutting down.</param>
    ValueTask UnloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional synchronous lifecycle callbacks dispatched on the Unreal game thread.
/// A throwing handler is disabled for the remainder of the session.
/// </summary>
public interface IRogueModGameEvents
{
    /// <summary>Receives one lifecycle event. Must stay short and non-blocking.</summary>
    /// <param name="eventKind">The lifecycle event being dispatched.</param>
    void OnGameEvent(ModGameEventKind eventKind);
}

/// <summary>Lifecycle events forwarded by the RogueMod runtime to <see cref="IRogueModGameEvents"/>.</summary>
public enum ModGameEventKind
{
    /// <summary>The game has finished its startup sequence.</summary>
    ProgramStarted = 1,

    /// <summary>The Unreal object system is initialized; reflection may begin on this event.</summary>
    UnrealInitialized = 2,

    /// <summary>The game's UI subsystem is initialized.</summary>
    UiInitialized = 3,

    /// <summary>The per-frame update tick. Reflection is normally available from here on.</summary>
    Update = 4,

    /// <summary>All UE4SS C++ mods, including the RogueMod bridge, have completed loading.</summary>
    CppModsLoaded = 5
}

/// <summary>Services RogueMod provides to a loaded mod.</summary>
public interface IModContext
{
    /// <summary>Gets the canonical package id from <c>mod.json</c>.</summary>
    string ModId { get; }

    /// <summary>Gets the id of the active game profile, for example <c>deadzone-rogue</c>.</summary>
    string GameProfileId { get; }

    /// <summary>Gets the logger writing to <c>RogueMod.log</c> under the mod's own source prefix.</summary>
    IModLogger Logger { get; }

    /// <summary>Gets the Unreal reflection boundary. Check <see cref="IUnrealReflection.IsAvailable"/> before use.</summary>
    IUnrealReflection Unreal { get; }
}

/// <summary>
/// The stable reflection boundary between managed mods and the installed game. Handles are
/// index/serial pairs into Unreal's live object array, never raw pointers. All members must be
/// called from the game thread. Feature availability is versioned through <see cref="Capabilities"/>;
/// members unsupported by the active bridge throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IUnrealReflection
{
    /// <summary>Gets a value indicating whether the native bridge and Unreal object system are live.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets the capability flags supported by the active bridge installation.</summary>
    UnrealReflectionCapabilities Capabilities =>
        IsAvailable ? UnrealReflectionCapabilities.Objects : UnrealReflectionCapabilities.None;

    /// <summary>Finds the first live object whose class matches <paramref name="className"/> and inherits from <c>UObject</c>.</summary>
    /// <param name="className">A class name, or a leading-slash object path for exact resolution.</param>
    /// <returns>The object handle, or <see cref="UnrealObjectHandle.Null"/> when nothing matched.</returns>
    UnrealObjectHandle FindFirstOf(string className);

    /// <summary>Finds every live object whose class matches <paramref name="className"/>.</summary>
    /// <param name="className">A class name, or a leading-slash object path for exact resolution.</param>
    /// <returns>The matching object handles; empty when nothing matched.</returns>
    IReadOnlyList<UnrealObjectHandle> FindAllOf(string className) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal object enumeration.");

    /// <summary>Indicates whether the handle still refers to a live object. Rejects handles after Unreal GC destroys or reuses the slot.</summary>
    /// <param name="handle">The handle to validate.</param>
    bool IsValid(UnrealObjectHandle handle);

    /// <summary>Returns the class object of the given object.</summary>
    /// <param name="handle">The object whose class is resolved.</param>
    /// <returns>The class object handle, or <see cref="UnrealObjectHandle.Null"/> when unavailable.</returns>
    UnrealObjectHandle GetClass(UnrealObjectHandle handle);

    /// <summary>Returns the full Unreal path name of the object, or null when unavailable.</summary>
    /// <param name="handle">The object whose path name is resolved.</param>
    string? GetPathName(UnrealObjectHandle handle);

    /// <summary>Reads one reflected property. Requires <see cref="UnrealReflectionCapabilities.PropertyRead"/> and a descriptor emitted by the generated SDK.</summary>
    /// <param name="handle">The object to read from.</param>
    /// <param name="property">The property descriptor describing layout and transport.</param>
    UnrealValue ReadProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal property reads.");

    /// <summary>Writes one reflected property. Requires <see cref="UnrealReflectionCapabilities.PropertyWrite"/>; container and reference writes are additionally gated by their capabilities.</summary>
    /// <param name="handle">The object to write to.</param>
    /// <param name="property">The property descriptor describing layout and transport.</param>
    /// <param name="value">The transported value; must match the descriptor shape.</param>
    void WriteProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property, UnrealValue value) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal property writes.");

    /// <summary>Invokes a reflected UFunction. Requires <see cref="UnrealReflectionCapabilities.FunctionInvocation"/>.</summary>
    /// <param name="handle">The object the UFunction is called on.</param>
    /// <param name="function">The function descriptor describing parameters and layout.</param>
    /// <param name="arguments">Input and reference arguments; omitted inputs are left zero-initialized.</param>
    /// <returns>The return value and out arguments.</returns>
    UnrealInvocationResult Invoke(
        UnrealObjectHandle handle,
        UnrealFunctionDescriptor function,
        IReadOnlyList<UnrealArgument> arguments) =>
        throw new NotSupportedException("The active RogueMod bridge does not support UFunction invocation.");

    /// <summary>Observes calls to a reflected UFunction until the returned subscription is disposed.</summary>
    /// <param name="function">The function descriptor to hook.</param>
    /// <param name="phase">Whether the callback runs before or after the original call.</param>
    /// <param name="callback">The callback receiving the call snapshot.</param>
    IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        Action<UnrealHookContext> callback) =>
        throw new NotSupportedException("The active RogueMod bridge does not support UFunction hooks.");

    /// <summary>
    /// Constructs a new UObject of the given class through the engine's StaticConstructObject
    /// path, optionally owned by an outer and optionally given a name. Requires
    /// <see cref="UnrealReflectionCapabilities.ObjectCreation"/>.
    /// </summary>
    /// <param name="classHandle">The class object to instantiate; resolve it with <see cref="FindFirstOf"/> first.</param>
    /// <param name="outerHandle">The owning object, or <see cref="UnrealObjectHandle.Null"/> for the transient package.</param>
    /// <param name="objectName">The new object's name, or null for an engine-generated name.</param>
    /// <returns>The handle of the constructed object.</returns>
    UnrealObjectHandle CreateObject(
        UnrealObjectHandle classHandle,
        UnrealObjectHandle outerHandle,
        string? objectName = null) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal object creation.");

    /// <summary>
    /// Spawns an actor of the given class into the world that owns the context object.
    /// Requires <see cref="UnrealReflectionCapabilities.ActorSpawning"/>.
    /// </summary>
    /// <param name="contextObject">Any object in the target world; the world is resolved through <c>UObject::GetWorld</c>.</param>
    /// <param name="classHandle">The actor class to spawn.</param>
    /// <param name="location">The spawn translation.</param>
    /// <param name="rotation">The spawn rotation.</param>
    /// <returns>The handle of the spawned actor.</returns>
    UnrealObjectHandle SpawnActor(
        UnrealObjectHandle contextObject,
        UnrealObjectHandle classHandle,
        UnrealVector location,
        UnrealRotator rotation) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal actor spawning.");

    /// <summary>Registers a UFunction hook with deterministic ordering and an optional exact-object filter.</summary>
    /// <param name="function">The function descriptor to hook.</param>
    /// <param name="phase">Whether the callback runs before or after the original call.</param>
    /// <param name="options">Priority, instance filter, and input-decoding policy.</param>
    /// <param name="callback">The callback receiving the call snapshot.</param>
    /// <returns>The subscription; dispose to remove the hook early.</returns>
    IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        UnrealHookOptions options,
        Action<UnrealHookContext> callback)
    {
        if (options != default)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support ordered or instance-filtered UFunction hooks.");
        }
        return RegisterHook(function, phase, callback);
    }
}

/// <summary>Feature flags describing what the active bridge installation supports. Check before optional operations.</summary>
[Flags]
public enum UnrealReflectionCapabilities
{
    /// <summary>No reflection is available.</summary>
    None = 0,

    /// <summary>Object discovery and validation are available.</summary>
    Objects = 1 << 0,

    /// <summary>Scalar and reference property reads are available.</summary>
    PropertyRead = 1 << 1,

    /// <summary>Property writes are available.</summary>
    PropertyWrite = 1 << 2,

    /// <summary>UFunction invocation is available.</summary>
    FunctionInvocation = 1 << 3,

    /// <summary>Multi-object enumeration is available.</summary>
    ObjectEnumeration = 1 << 4,

    /// <summary>Nested TArray transport is available.</summary>
    NestedArrays = 1 << 5,

    /// <summary>TOptional transport is available.</summary>
    OptionalValues = 1 << 6,

    /// <summary>Weak object reference transport is available.</summary>
    WeakObjectReferences = 1 << 7,

    /// <summary>Lazy object reference transport is available.</summary>
    LazyObjectReferences = 1 << 8,

    /// <summary>Mutable pre/post UFunction hooks are available.</summary>
    FunctionHooks = 1 << 9,

    /// <summary>Object construction through StaticConstructObject is available.</summary>
    ObjectCreation = 1 << 10,

    /// <summary>Actor spawning through UWorld::SpawnActor is available.</summary>
    ActorSpawning = 1 << 11,

    /// <summary>Soft object reference transport is available.</summary>
    SoftObjectReferences = 1 << 12,

    /// <summary>Interface reference transport is available.</summary>
    InterfaceReferences = 1 << 13,

    /// <summary>TMap/TSet property reads are available.</summary>
    MapSetProperties = 1 << 14,

    /// <summary>TMap/TSet property writes are available.</summary>
    MapSetWrites = 1 << 15
}

/// <summary>Phase of a UFunction hook relative to the original call.</summary>
public enum UnrealHookPhase
{
    /// <summary>The callback runs before the original UFunction and may replace input arguments.</summary>
    Pre = 1,

    /// <summary>The callback runs after the original UFunction and may replace the return value and out/ref arguments.</summary>
    Post = 2
}

/// <summary>
/// Registration policy for a UFunction hook. Higher priorities run first; equal priorities
/// retain registration order. A null instance handle matches every object. Post hooks may skip
/// decoding pure input parameters when their callback only consumes return and out/ref values.
/// </summary>
/// <param name="Priority">Descending execution priority; zero by default.</param>
/// <param name="Instance">Restricts the hook to one exact object, or null to match every instance.</param>
/// <param name="SkipInputDecoding">Skips decoding of pure input parameters; used by observational post hooks.</param>
public readonly record struct UnrealHookOptions(
    int Priority = 0,
    UnrealObjectHandle Instance = default,
    bool SkipInputDecoding = false);

/// <summary>An opaque reference to a live Unreal object. Encodes a <c>GUObjectArray</c> slot index and serial; never a raw pointer.</summary>
/// <param name="Value">The encoded index/serial pair; zero means null.</param>
public readonly record struct UnrealObjectHandle(ulong Value)
{
    /// <summary>Gets the null handle.</summary>
    public static UnrealObjectHandle Null => default;

    /// <summary>Gets a value indicating whether this handle refers to no object.</summary>
    public bool IsNull => Value == 0;
}

/// <summary>A stable property identity emitted by the generated game SDK.</summary>
/// <param name="OwnerPath">Full Unreal path of the owning script struct or class.</param>
/// <param name="Name">Reflected property name.</param>
/// <param name="UnrealType">Unreal property kind with type suffix, for example <c>StructProperty:/Script/Engine.Vector</c>.</param>
/// <param name="Offset">Property offset inside the owner layout.</param>
/// <param name="ArrayDimension">Fixed-array dimension; one for ordinary properties.</param>
/// <param name="Flags">Reflected property flags as a pipe-separated string.</param>
/// <param name="Size">Declared property size in bytes; zero when unknown.</param>
/// <param name="ByteOffset">Byte offset for boolean properties.</param>
/// <param name="ByteMask">Byte mask for boolean properties.</param>
/// <param name="FieldMask">Field mask for boolean properties.</param>
/// <param name="Struct">Field-wise metadata when the property is a script struct.</param>
/// <param name="Array">Element metadata when the property is a TArray.</param>
public sealed record UnrealPropertyDescriptor(
    string OwnerPath,
    string Name,
    string UnrealType,
    int Offset,
    int ArrayDimension,
    string Flags,
    int Size = 0,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0,
    UnrealStructDescriptor? Struct = null,
    UnrealArrayDescriptor? Array = null)
{
    /// <summary>Inner-value metadata when this property is a TOptional.</summary>
    public UnrealOptionalDescriptor? Optional { get; init; }

    /// <summary>Key/value metadata when this property is a TMap.</summary>
    public UnrealMapDescriptor? Map { get; init; }

    /// <summary>Element metadata when this property is a TSet.</summary>
    public UnrealSetDescriptor? Set { get; init; }
}

/// <summary>The four native 32-bit components of an Unreal FGuid.</summary>
/// <param name="A">First component.</param>
/// <param name="B">Second component.</param>
/// <param name="C">Third component.</param>
/// <param name="D">Fourth component.</param>
public readonly record struct UnrealGuid(uint A, uint B, uint C, uint D)
{
    /// <summary>Gets the empty GUID.</summary>
    public static UnrealGuid Empty => default;

    /// <summary>Gets a value indicating whether all components are zero.</summary>
    public bool IsEmpty => A == 0 && B == 0 && C == 0 && D == 0;

    /// <inheritdoc />
    public override string ToString() => $"{A:X8}-{B:X8}-{C:X8}-{D:X8}";
}

/// <summary>An Unreal FVector translation (three consecutive float components).</summary>
/// <param name="X">X component.</param>
/// <param name="Y">Y component.</param>
/// <param name="Z">Z component.</param>
public readonly record struct UnrealVector(float X, float Y, float Z)
{
    /// <summary>Gets the zero vector.</summary>
    public static UnrealVector Zero => default;
}

/// <summary>An Unreal FRotator (pitch, yaw, roll, three consecutive float components).</summary>
/// <param name="Pitch">Pitch component.</param>
/// <param name="Yaw">Yaw component.</param>
/// <param name="Roll">Roll component.</param>
public readonly record struct UnrealRotator(float Pitch, float Yaw, float Roll)
{
    /// <summary>Gets the zero rotator.</summary>
    public static UnrealRotator Zero => default;
}

/// <summary>Runtime layout metadata for one reflected UFunction parameter.</summary>
/// <param name="Name">Reflected parameter name.</param>
/// <param name="UnrealType">Unreal property kind with type suffix.</param>
/// <param name="Offset">Parameter offset inside the function parameter buffer.</param>
/// <param name="ArrayDimension">Fixed-array dimension; one for ordinary parameters.</param>
/// <param name="Flags">Reflected property flags as a pipe-separated string.</param>
/// <param name="Size">Declared parameter size in bytes.</param>
/// <param name="ByteOffset">Byte offset for boolean parameters.</param>
/// <param name="ByteMask">Byte mask for boolean parameters.</param>
/// <param name="FieldMask">Field mask for boolean parameters.</param>
/// <param name="Struct">Field-wise metadata when the parameter is a script struct.</param>
/// <param name="Array">Element metadata when the parameter is a TArray.</param>
public sealed record UnrealParameterDescriptor(
    string Name,
    string UnrealType,
    int Offset,
    int ArrayDimension,
    string Flags,
    int Size,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0,
    UnrealStructDescriptor? Struct = null,
    UnrealArrayDescriptor? Array = null)
{
    /// <summary>Inner-value metadata when this parameter is a TOptional.</summary>
    public UnrealOptionalDescriptor? Optional { get; init; }

    /// <summary>Key/value metadata when this parameter is a TMap.</summary>
    public UnrealMapDescriptor? Map { get; init; }

    /// <summary>Element metadata when this parameter is a TSet.</summary>
    public UnrealSetDescriptor? Set { get; init; }

    /// <summary>Gets a value indicating whether the parameter is declared as an out parameter.</summary>
    public bool IsOutput => HasFlag("CPF_OutParm");

    /// <summary>Gets a value indicating whether the parameter is the function return value.</summary>
    public bool IsReturn => HasFlag("CPF_ReturnParm");

    /// <summary>Gets a value indicating whether the parameter is passed by reference.</summary>
    public bool IsReference => HasFlag("CPF_ReferenceParm");

    /// <summary>Gets a value indicating whether the parameter is a pure input.</summary>
    public bool IsInput => !IsReturn && (!IsOutput || IsReference);

    private bool HasFlag(string flag) =>
        Flags.Split('|', StringSplitOptions.TrimEntries).Contains(flag, StringComparer.Ordinal);
}

/// <summary>A stable UFunction identity emitted by the generated game SDK.</summary>
/// <param name="OwnerPath">Full Unreal path of the owning class.</param>
/// <param name="Path">Full Unreal function path, for example <c>/Script/Engine.Actor:SetOwner</c>.</param>
/// <param name="Name">Reflected function name.</param>
/// <param name="Flags">Unreal function flags as a pipe-separated string.</param>
/// <param name="Parameters">Parameter descriptors, or null for a parameterless function.</param>
public sealed record UnrealFunctionDescriptor(
    string OwnerPath,
    string Path,
    string Name,
    string Flags,
    IReadOnlyList<UnrealParameterDescriptor>? Parameters = null)
{
    /// <summary>Gets the parameter descriptors; empty for a parameterless function.</summary>
    public IReadOnlyList<UnrealParameterDescriptor> ParameterList => Parameters ?? [];
}

/// <summary>A named input argument for a UFunction invocation.</summary>
/// <param name="Name">Reflected parameter name.</param>
/// <param name="Value">The transported argument value.</param>
public readonly record struct UnrealArgument(string Name, UnrealValue Value);

/// <summary>Field-wise layout metadata for a transportable Unreal script struct.</summary>
/// <param name="Path">Full Unreal struct path, for example <c>/Script/Valhalla.DamageData</c>.</param>
/// <param name="Size">Declared struct size in bytes.</param>
/// <param name="Alignment">Declared struct alignment in bytes.</param>
/// <param name="Fields">Transported field descriptors.</param>
/// <param name="RawLayout">When true, values cross the boundary as raw bytes built from the declared field offsets instead of per-field native values.</param>
public sealed record UnrealStructDescriptor(
    string Path,
    int Size,
    int Alignment,
    IReadOnlyList<UnrealStructFieldDescriptor> Fields,
    bool RawLayout = false);

/// <summary>Layout and nested-type metadata for one field in a transportable Unreal struct.</summary>
/// <param name="Name">Reflected field name.</param>
/// <param name="UnrealType">Unreal property kind with type suffix.</param>
/// <param name="Offset">Field offset inside the struct layout.</param>
/// <param name="Size">Declared field size in bytes.</param>
/// <param name="ArrayDimension">Fixed-array dimension; one for ordinary fields.</param>
/// <param name="ByteOffset">Byte offset for boolean fields.</param>
/// <param name="ByteMask">Byte mask for boolean fields.</param>
/// <param name="FieldMask">Field mask for boolean fields.</param>
/// <param name="Struct">Nested metadata when the field is itself a script struct.</param>
/// <param name="Array">Element metadata when the field is a TArray.</param>
/// <param name="Optional">Inner metadata when the field is a TOptional.</param>
/// <param name="Map">Key/value metadata when the field is a TMap.</param>
/// <param name="Set">Element metadata when the field is a TSet.</param>
public sealed record UnrealStructFieldDescriptor(
    string Name,
    string UnrealType,
    int Offset,
    int Size,
    int ArrayDimension = 1,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0,
    UnrealStructDescriptor? Struct = null,
    UnrealArrayDescriptor? Array = null,
    UnrealOptionalDescriptor? Optional = null,
    UnrealMapDescriptor? Map = null,
    UnrealSetDescriptor? Set = null);

/// <summary>A field-wise struct value used by generated SDK adapters.</summary>
/// <param name="Descriptor">The struct layout the fields belong to.</param>
/// <param name="Fields">The transported field values by reflected name.</param>
public sealed record UnrealStructValue(
    UnrealStructDescriptor Descriptor,
    IReadOnlyDictionary<string, UnrealValue> Fields)
{
    /// <summary>Reads one transported field value.</summary>
    /// <param name="name">The reflected field name.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the struct has no transported field with that name.</exception>
    public UnrealValue GetField(string name) => Fields.TryGetValue(name, out var value)
        ? value
        : throw new KeyNotFoundException($"Unreal struct '{Descriptor.Path}' has no transported field named '{name}'.");
}

/// <summary>Element metadata for a one-dimensional Unreal TArray.</summary>
/// <param name="ElementUnrealType">Unreal property kind with type suffix of the element.</param>
/// <param name="ElementSize">Native element size in bytes.</param>
/// <param name="ElementByteOffset">Byte offset for boolean elements.</param>
/// <param name="ElementByteMask">Byte mask for boolean elements.</param>
/// <param name="ElementFieldMask">Field mask for boolean elements.</param>
/// <param name="ElementStruct">Field-wise metadata when elements are script structs.</param>
public sealed record UnrealArrayDescriptor(
    string ElementUnrealType,
    int ElementSize,
    int ElementByteOffset = 0,
    int ElementByteMask = 0,
    int ElementFieldMask = 0,
    UnrealStructDescriptor? ElementStruct = null)
{
    /// <summary>Inner-value metadata when this parameter is a TOptional.</summary>
    public UnrealOptionalDescriptor? Optional { get; init; }

    /// <summary>Element metadata when this array contains another TArray.</summary>
    public UnrealArrayDescriptor? ElementArray { get; init; }
}

/// <summary>A managed list transported through a generated TArray adapter.</summary>
public sealed record UnrealArrayValue(
    UnrealArrayDescriptor Descriptor,
    IReadOnlyList<UnrealValue> Elements)
{
    /// <summary>Encodes a managed list into a transported TArray value.</summary>
    /// <typeparam name="T">The managed element type.</typeparam>
    /// <param name="descriptor">The array descriptor describing the native element layout.</param>
    /// <param name="values">The managed values.</param>
    /// <param name="encode">Converts one managed value into its transported representation.</param>
    /// <returns>The transported value.</returns>
    public static UnrealValue From<T>(
        UnrealArrayDescriptor descriptor,
        IReadOnlyList<T> values,
        Func<T, UnrealValue> encode)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(encode);
        return UnrealValue.From(new UnrealArrayValue(descriptor, values.Select(encode).ToArray()));
    }

    /// <summary>Decodes a transported TArray value into a managed list.</summary>
    /// <typeparam name="T">The managed element type.</typeparam>
    /// <param name="value">The transported value.</param>
    /// <param name="decode">Converts one transported element into its managed representation.</param>
    /// <returns>The decoded list.</returns>
    public static IReadOnlyList<T> ToList<T>(UnrealValue value, Func<UnrealValue, T> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var transported = value.As<UnrealArrayValue>();
        return transported.Elements.Select(decode).ToArray();
    }
}

/// <summary>Inner-value metadata for an Unreal TOptional.</summary>
/// <param name="ValueUnrealType">Unreal property kind with type suffix of the inner value.</param>
/// <param name="ValueSize">Declared inner value size in bytes.</param>
/// <param name="ValueByteOffset">Byte offset for boolean inner values.</param>
/// <param name="ValueByteMask">Byte mask for boolean inner values.</param>
/// <param name="ValueFieldMask">Field mask for boolean inner values.</param>
/// <param name="ValueStruct">Field-wise metadata when the inner value is a script struct.</param>
public sealed record UnrealOptionalDescriptor(
    string ValueUnrealType,
    int ValueSize,
    int ValueByteOffset = 0,
    int ValueByteMask = 0,
    int ValueFieldMask = 0,
    UnrealStructDescriptor? ValueStruct = null);

/// <summary>A set or unset value transported through a generated TOptional adapter.</summary>
/// <param name="Descriptor">The inner-value layout metadata.</param>
/// <param name="IsSet">Whether the optional is set.</param>
/// <param name="Value">The transported inner value; null when unset.</param>
public sealed record UnrealOptionalValue(
    UnrealOptionalDescriptor Descriptor,
    bool IsSet,
    UnrealValue Value);

/// <summary>
/// Key/value metadata for an Unreal TMap. Keys are scalar-only in RogueMod ABI 13; values may
/// carry a nested TArray descriptor. The property itself reports an 80-byte FScriptMap on the
/// supported Deadzone: Rogue 1.4.2.0 / UE 5.6.1 build, which the runtime validates.
/// </summary>
/// <param name="KeyUnrealType">Unreal property kind with type suffix of the key.</param>
/// <param name="KeySize">Native key size in bytes.</param>
/// <param name="ValueUnrealType">Unreal property kind with type suffix of the value.</param>
/// <param name="ValueSize">Native value size in bytes.</param>
/// <param name="KeyByteOffset">Byte offset for boolean keys.</param>
/// <param name="KeyByteMask">Byte mask for boolean keys.</param>
/// <param name="KeyFieldMask">Field mask for boolean keys.</param>
/// <param name="ValueByteOffset">Byte offset for boolean values.</param>
/// <param name="ValueByteMask">Byte mask for boolean values.</param>
/// <param name="ValueFieldMask">Field mask for boolean values.</param>
/// <param name="KeyStruct">Field-wise metadata when keys are script structs.</param>
/// <param name="ValueStruct">Field-wise metadata when values are script structs.</param>
public sealed record UnrealMapDescriptor(
    string KeyUnrealType,
    int KeySize,
    string ValueUnrealType,
    int ValueSize,
    int KeyByteOffset = 0,
    int KeyByteMask = 0,
    int KeyFieldMask = 0,
    int ValueByteOffset = 0,
    int ValueByteMask = 0,
    int ValueFieldMask = 0,
    UnrealStructDescriptor? KeyStruct = null,
    UnrealStructDescriptor? ValueStruct = null)
{
    /// <summary>Element metadata when this map's value is another TArray.</summary>
    public UnrealArrayDescriptor? ValueArray { get; init; }
}

/// <summary>Element metadata for an Unreal TSet.</summary>
/// <param name="ElementUnrealType">Unreal property kind with type suffix of the element.</param>
/// <param name="ElementSize">Native element size in bytes.</param>
/// <param name="ElementByteOffset">Byte offset for boolean elements.</param>
/// <param name="ElementByteMask">Byte mask for boolean elements.</param>
/// <param name="ElementFieldMask">Field mask for boolean elements.</param>
/// <param name="ElementStruct">Field-wise metadata when elements are script structs.</param>
public sealed record UnrealSetDescriptor(
    string ElementUnrealType,
    int ElementSize,
    int ElementByteOffset = 0,
    int ElementByteMask = 0,
    int ElementFieldMask = 0,
    UnrealStructDescriptor? ElementStruct = null)
{
    /// <summary>Element metadata when this set contains another TArray.</summary>
    public UnrealArrayDescriptor? ElementArray { get; init; }
}

/// <summary>A managed map transported through a generated TMap adapter.</summary>
public sealed record UnrealMapValue(
    UnrealMapDescriptor Descriptor,
    IReadOnlyList<KeyValuePair<UnrealValue, UnrealValue>> Entries)
{
    /// <summary>Encodes a managed dictionary into a transported TMap value.</summary>
    /// <typeparam name="TKey">The managed key type.</typeparam>
    /// <typeparam name="TValue">The managed value type.</typeparam>
    /// <param name="descriptor">The map descriptor describing native key and value layouts.</param>
    /// <param name="values">The managed entries.</param>
    /// <param name="encodeKey">Converts one managed key into its transported representation.</param>
    /// <param name="encodeValue">Converts one managed value into its transported representation.</param>
    /// <returns>The transported value.</returns>
    public static UnrealValue From<TKey, TValue>(
        UnrealMapDescriptor descriptor,
        IReadOnlyDictionary<TKey, TValue> values,
        Func<TKey, UnrealValue> encodeKey,
        Func<TValue, UnrealValue> encodeValue)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(encodeKey);
        ArgumentNullException.ThrowIfNull(encodeValue);
        return UnrealValue.From(new UnrealMapValue(
            descriptor,
            values
                .Select(pair => new KeyValuePair<UnrealValue, UnrealValue>(
                    encodeKey(pair.Key),
                    encodeValue(pair.Value)))
                .ToArray()));
    }

    /// <summary>Decodes a transported TMap value into a managed dictionary.</summary>
    /// <typeparam name="TKey">The managed key type.</typeparam>
    /// <typeparam name="TValue">The managed value type.</typeparam>
    /// <param name="value">The transported value.</param>
    /// <param name="decodeKey">Converts one transported key into its managed representation.</param>
    /// <param name="decodeValue">Converts one transported value into its managed representation.</param>
    /// <returns>The decoded dictionary.</returns>
    public static IReadOnlyDictionary<TKey, TValue> ToDictionary<TKey, TValue>(
        UnrealValue value,
        Func<UnrealValue, TKey> decodeKey,
        Func<UnrealValue, TValue> decodeValue)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(decodeKey);
        ArgumentNullException.ThrowIfNull(decodeValue);
        var transported = value.As<UnrealMapValue>();
        return transported.Entries.ToDictionary(
            pair => decodeKey(pair.Key),
            pair => decodeValue(pair.Value));
    }
}

/// <summary>A managed set transported through a generated TSet adapter.</summary>
public sealed record UnrealSetValue(
    UnrealSetDescriptor Descriptor,
    IReadOnlyList<UnrealValue> Elements)
{
    /// <summary>Encodes a managed set into a transported TSet value.</summary>
    /// <typeparam name="T">The managed element type.</typeparam>
    /// <param name="descriptor">The set descriptor describing the native element layout.</param>
    /// <param name="values">The managed values.</param>
    /// <param name="encode">Converts one managed value into its transported representation.</param>
    /// <returns>The transported value.</returns>
    public static UnrealValue From<T>(
        UnrealSetDescriptor descriptor,
        IReadOnlySet<T> values,
        Func<T, UnrealValue> encode)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(encode);
        return UnrealValue.From(new UnrealSetValue(descriptor, values.Select(encode).ToArray()));
    }

    /// <summary>Decodes a transported TSet value into a managed set.</summary>
    /// <typeparam name="T">The managed element type.</typeparam>
    /// <param name="value">The transported value.</param>
    /// <param name="decode">Converts one transported element into its managed representation.</param>
    /// <returns>The decoded set.</returns>
    public static IReadOnlySet<T> ToSet<T>(UnrealValue value, Func<UnrealValue, T> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var transported = value.As<UnrealSetValue>();
        return transported.Elements.Select(decode).ToArray().ToHashSet();
    }
}

/// <summary>
/// Identity-preserving transport for an Unreal FLazyObjectPtr. The persistent GUID remains
/// available even when the weak target is currently unloaded.
/// </summary>
public sealed class UnrealLazyObjectValue
{
    /// <summary>The exact native storage size of an FLazyObjectPtr in bytes.</summary>
    public const int NativeStorageSize = 24;

    private readonly byte[] nativeStorage;

    /// <summary>Initializes the transport from its components and raw native bytes.</summary>
    /// <param name="objectId">The persistent object GUID.</param>
    /// <param name="cachedHandle">The currently cached target, or null when pending.</param>
    /// <param name="nativeStorage">Exactly <see cref="NativeStorageSize"/> raw bytes.</param>
    public UnrealLazyObjectValue(
        UnrealGuid objectId,
        UnrealObjectHandle cachedHandle,
        ReadOnlySpan<byte> nativeStorage)
    {
        if (nativeStorage.Length != NativeStorageSize)
        {
            throw new ArgumentException(
                $"An Unreal lazy object reference requires exactly {NativeStorageSize} bytes of native storage.",
                nameof(nativeStorage));
        }

        ObjectId = objectId;
        CachedHandle = cachedHandle;
        this.nativeStorage = nativeStorage.ToArray();
    }

    /// <summary>Gets the persistent object GUID.</summary>
    public UnrealGuid ObjectId { get; }

    /// <summary>Gets the currently cached target, or null when the reference is pending.</summary>
    public UnrealObjectHandle CachedHandle { get; }

    /// <summary>Gets a value indicating whether the reference refers to no object.</summary>
    public bool IsNull => ObjectId.IsEmpty && CachedHandle.IsNull;

    /// <summary>Returns a copy of the raw native storage.</summary>
    public byte[] CopyNativeStorage() => (byte[])nativeStorage.Clone();

    /// <summary>Gets the null lazy reference.</summary>
    public static UnrealLazyObjectValue Null { get; } =
        new(UnrealGuid.Empty, UnrealObjectHandle.Null, new byte[NativeStorageSize]);
}

/// <summary>
/// A typed Unreal lazy reference. Unlike a weak reference, it retains its persistent object
/// identity while the target is unloaded; it does not load or keep the target alive.
/// </summary>
/// <typeparam name="T">The generated wrapper type of the referenced object.</typeparam>
public sealed class UnrealLazyObjectReference<T> where T : UnrealObject
{
    private readonly UnrealLazyObjectValue transported;

    private UnrealLazyObjectReference(UnrealLazyObjectValue transported, T? target)
    {
        this.transported = transported;
        CachedTarget = target;
    }

    /// <summary>Gets the persistent object GUID.</summary>
    public UnrealGuid ObjectId => transported.ObjectId;

    /// <summary>The target already cached by Unreal, or null when the reference is pending or stale.</summary>
    public T? CachedTarget { get; }

    /// <summary>Gets a value indicating whether the reference refers to no object.</summary>
    public bool IsNull => transported.IsNull;

    /// <summary>Gets a value indicating whether the reference is set but currently unloaded.</summary>
    public bool IsPending => !IsNull && CachedTarget is null;

    /// <summary>Gets the null lazy reference.</summary>
    public static UnrealLazyObjectReference<T> Null { get; } =
        new(UnrealLazyObjectValue.Null, null);

    /// <summary>Encodes the reference for transport.</summary>
    public UnrealValue ToUnrealValue() => UnrealValue.From(transported);

    /// <summary>Decodes a transported lazy reference.</summary>
    /// <param name="value">The transported value.</param>
    /// <param name="factory">Creates the wrapper for the cached target handle.</param>
    /// <returns>The typed lazy reference.</returns>
    public static UnrealLazyObjectReference<T> FromUnrealValue(
        UnrealValue value,
        Func<UnrealObjectHandle, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var transported = value.As<UnrealLazyObjectValue>();
        var target = transported.CachedHandle.IsNull
            ? null
            : factory(transported.CachedHandle);
        return new UnrealLazyObjectReference<T>(transported, target);
    }
}

/// <summary>
/// Transport for an Unreal TSoftObjectPtr value: the persistent asset path plus an optional
/// already-cached target. The native storage field is ABI-reserved and opaque; writes are
/// rebuilt from <see cref="Path"/> by the game's Kismet APIs and never replay these bytes.
/// </summary>
public sealed class UnrealSoftObjectValue
{
    /// <summary>The exact native storage size of a TSoftObjectPtr in bytes.</summary>
    public const int NativeStorageSize = 40;

    private readonly byte[] nativeStorage;

    /// <summary>Initializes the transport from its components and raw native bytes.</summary>
    /// <param name="path">The persistent asset path.</param>
    /// <param name="cachedHandle">The currently cached target, or null when unloaded.</param>
    /// <param name="nativeStorage">Exactly <see cref="NativeStorageSize"/> raw bytes.</param>
    public UnrealSoftObjectValue(string path, UnrealObjectHandle cachedHandle, ReadOnlySpan<byte> nativeStorage)
    {
        if (nativeStorage.Length != NativeStorageSize)
        {
            throw new ArgumentException(
                $"An Unreal soft object reference requires exactly {NativeStorageSize} bytes of native storage.",
                nameof(nativeStorage));
        }

        Path = path ?? throw new ArgumentNullException(nameof(path));
        CachedHandle = cachedHandle;
        this.nativeStorage = nativeStorage.ToArray();
    }

    /// <summary>Gets the persistent asset path.</summary>
    public string Path { get; }

    /// <summary>Gets the currently cached target, or null when unloaded.</summary>
    public UnrealObjectHandle CachedHandle { get; }

    /// <summary>Gets a value indicating whether the reference has no asset path.</summary>
    public bool IsNull => Path.Length == 0;

    /// <summary>Returns a copy of the raw native storage.</summary>
    public byte[] CopyNativeStorage() => (byte[])nativeStorage.Clone();
}

/// <summary>
/// A typed Unreal soft object reference. The persistent asset path remains available whether
/// or not the target is loaded; <see cref="CachedTarget"/> is only the already-cached object
/// and never loads or roots the target.
/// </summary>
/// <typeparam name="T">The generated wrapper type of the referenced object.</typeparam>
public sealed class UnrealSoftObjectReference<T> where T : UnrealObject
{
    private readonly UnrealSoftObjectValue transported;

    private UnrealSoftObjectReference(UnrealSoftObjectValue transported, T? target)
    {
        this.transported = transported;
        CachedTarget = target;
    }

    /// <summary>Gets the persistent asset path.</summary>
    public string Path => transported.Path;

    /// <summary>Gets the already-cached target, or null when unloaded.</summary>
    public T? CachedTarget { get; }

    /// <summary>Gets a value indicating whether the reference has no asset path.</summary>
    public bool IsNull => transported.IsNull;

    /// <summary>Gets the null soft reference.</summary>
    public static UnrealSoftObjectReference<T> Null { get; } =
        new(new UnrealSoftObjectValue(string.Empty, UnrealObjectHandle.Null, new byte[UnrealSoftObjectValue.NativeStorageSize]), null);

    /// <summary>
    /// Creates an unloaded soft reference from its persistent Unreal asset path.
    /// This does not load or resolve the referenced object.
    /// </summary>
    /// <param name="path">The persistent Unreal asset path, for example <c>/Game/Items/MyItem.MyItem</c>.</param>
    /// <returns>The typed soft reference.</returns>
    public static UnrealSoftObjectReference<T> FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new UnrealSoftObjectReference<T>(
            new UnrealSoftObjectValue(
                path,
                UnrealObjectHandle.Null,
                new byte[UnrealSoftObjectValue.NativeStorageSize]),
            null);
    }

    /// <summary>Encodes the reference for transport.</summary>
    public UnrealValue ToUnrealValue() => UnrealValue.From(transported);

    /// <summary>Decodes a transported soft reference.</summary>
    /// <param name="value">The transported value.</param>
    /// <param name="factory">Creates the wrapper for the cached target handle.</param>
    /// <returns>The typed soft reference.</returns>
    public static UnrealSoftObjectReference<T> FromUnrealValue(
        UnrealValue value,
        Func<UnrealObjectHandle, T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var transported = value.As<UnrealSoftObjectValue>();
        var target = transported.CachedHandle.IsNull
            ? null
            : factory(transported.CachedHandle);
        return new UnrealSoftObjectReference<T>(transported, target);
    }
}

/// <summary>
/// Strongly typed TOptional value. Unlike nullable annotations, this preserves Unreal's
/// set/unset state even when <typeparamref name="T"/> is itself nullable.
/// </summary>
/// <typeparam name="T">The wrapped value type.</typeparam>
public readonly record struct UnrealOptional<T>
{
    private readonly T? value;

    private UnrealOptional(bool isSet, T? value)
    {
        IsSet = isSet;
        this.value = value;
    }

    /// <summary>Gets a value indicating whether the optional is set.</summary>
    public bool IsSet { get; }

    /// <summary>Gets the wrapped value.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the optional is unset.</exception>
    public T Value => IsSet
        ? value!
        : throw new InvalidOperationException("An unset Unreal TOptional has no value.");

    /// <summary>Gets the unset optional.</summary>
    public static UnrealOptional<T> Unset => default;

    /// <summary>Creates a set optional.</summary>
    /// <param name="value">The wrapped value.</param>
    public static UnrealOptional<T> FromValue(T value) => new(true, value);

    /// <summary>Encodes the optional for transport.</summary>
    /// <param name="descriptor">The inner-value layout metadata.</param>
    /// <param name="encode">Converts the wrapped value into its transported representation.</param>
    public UnrealValue ToUnrealValue(
        UnrealOptionalDescriptor descriptor,
        Func<T, UnrealValue> encode)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(encode);
        return UnrealValue.From(new UnrealOptionalValue(
            descriptor,
            IsSet,
            IsSet ? encode(value!) : UnrealValue.Null));
    }

    /// <summary>Decodes a transported optional.</summary>
    /// <param name="value">The transported value.</param>
    /// <param name="decode">Converts the transported inner value into its managed representation.</param>
    public static UnrealOptional<T> FromUnrealValue(
        UnrealValue value,
        Func<UnrealValue, T> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var transported = value.As<UnrealOptionalValue>();
        return transported.IsSet
            ? FromValue(decode(transported.Value))
            : Unset;
    }
}

/// <summary>
/// A transport value used between generated wrappers and the runtime marshaller.
/// Object references are represented by <see cref="UnrealObjectHandle"/>, never raw pointers.
/// </summary>
/// <param name="Value">The wrapped transport object, or null for <see cref="Null"/>.</param>
public readonly record struct UnrealValue(object? Value)
{
    /// <summary>Gets the null transport value.</summary>
    public static UnrealValue Null => default;

    /// <summary>Wraps a value for transport.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to wrap.</param>
    public static UnrealValue From<T>(T value) => new(value);

    /// <summary>Unwraps the transported value as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <exception cref="InvalidCastException">Thrown when the wrapped value has an incompatible type.</exception>
    public T As<T>()
    {
        if (Value is T typed)
        {
            return typed;
        }

        if (Value is null && default(T) is null)
        {
            return default!;
        }

        if (Value is not null && typeof(T).IsEnum)
        {
            try
            {
                return (T)Enum.ToObject(typeof(T), Value);
            }
            catch (ArgumentException)
            {
                // Fall through to the descriptive exception below.
            }
        }

        throw new InvalidCastException(
            $"Unreal value '{Value?.GetType().FullName ?? "null"}' cannot be read as '{typeof(T).FullName}'.");
    }

    /// <summary>Unwraps the transported value as an object handle.</summary>
    public UnrealObjectHandle AsObjectHandle() => As<UnrealObjectHandle>();
}

/// <summary>The result of a UFunction invocation.</summary>
/// <param name="ReturnValue">The transported return value; <see cref="UnrealValue.Null"/> when the function returns void.</param>
/// <param name="OutArguments">The transported out/ref arguments by reflected name.</param>
public sealed record UnrealInvocationResult(
    UnrealValue ReturnValue,
    IReadOnlyDictionary<string, UnrealValue> OutArguments)
{
    /// <summary>Gets an empty result for functions without return value or out arguments.</summary>
    public static UnrealInvocationResult Empty { get; } =
        new(UnrealValue.Null, new Dictionary<string, UnrealValue>(StringComparer.Ordinal));

    /// <summary>Reads one out argument in its managed representation.</summary>
    /// <typeparam name="T">The expected argument type.</typeparam>
    /// <param name="name">The reflected out parameter name.</param>
    /// <exception cref="KeyNotFoundException">Thrown when the function did not produce that out argument.</exception>
    public T GetOut<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return OutArguments.TryGetValue(name, out var value)
            ? value.As<T>()
            : throw new KeyNotFoundException($"UFunction did not return an out argument named '{name}'.");
    }
}

/// <summary>
/// A snapshot of a hooked UFunction call. Pre hooks may replace input/ref arguments;
/// post hooks may replace the return value and out/ref arguments.
/// </summary>
public sealed class UnrealHookContext
{
    private readonly Dictionary<string, UnrealValue> argumentReplacements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnrealValue> outputReplacements = new(StringComparer.Ordinal);
    private UnrealValue? returnReplacement;

    /// <summary>Initializes the snapshot for one hooked call.</summary>
    /// <param name="object">The object the UFunction was called on.</param>
    /// <param name="function">The hooked function descriptor.</param>
    /// <param name="phase">The hook phase.</param>
    /// <param name="arguments">The call's input arguments by reflected name.</param>
    /// <param name="result">The original call result, available in post hooks.</param>
    public UnrealHookContext(
        UnrealObjectHandle @object,
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        IReadOnlyDictionary<string, UnrealValue> arguments,
        UnrealInvocationResult result)
    {
        Object = @object;
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Phase = phase;
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>Gets the handle of the object the UFunction was called on.</summary>
    public UnrealObjectHandle Object { get; }

    /// <summary>Gets the hooked function descriptor.</summary>
    public UnrealFunctionDescriptor Function { get; }

    /// <summary>Gets the hook phase.</summary>
    public UnrealHookPhase Phase { get; }

    /// <summary>Gets the call's input arguments by reflected name; post hooks decode only return and out/ref values.</summary>
    public IReadOnlyDictionary<string, UnrealValue> Arguments { get; }

    /// <summary>Gets the original call result, available in post hooks.</summary>
    public UnrealInvocationResult Result { get; }

    /// <summary>Replaces an input or reference parameter before the original UFunction runs.</summary>
    /// <param name="name">The reflected input parameter name.</param>
    /// <param name="value">The replacement value.</param>
    /// <exception cref="InvalidOperationException">Thrown from a post hook.</exception>
    /// <exception cref="ArgumentException">Thrown when the function has no input or ref parameter with that name.</exception>
    public void SetArgument(string name, UnrealValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Phase != UnrealHookPhase.Pre)
        {
            throw new InvalidOperationException("UFunction arguments can only be replaced from a pre hook.");
        }
        var descriptor = Function.ParameterList.FirstOrDefault(parameter =>
            parameter.IsInput && StringComparer.Ordinal.Equals(parameter.Name, name));
        if (descriptor is null)
        {
            throw new ArgumentException($"UFunction '{Function.Path}' has no input or ref parameter named '{name}'.", nameof(name));
        }
        argumentReplacements[name] = value;
    }

    /// <summary>Replaces the UFunction return value after the original call.</summary>
    /// <param name="value">The replacement value.</param>
    /// <exception cref="InvalidOperationException">Thrown from a pre hook or when the function returns void.</exception>
    public void SetReturnValue(UnrealValue value)
    {
        if (Phase != UnrealHookPhase.Post)
        {
            throw new InvalidOperationException("A UFunction return value can only be replaced from a post hook.");
        }
        if (!Function.ParameterList.Any(parameter => parameter.IsReturn))
        {
            throw new InvalidOperationException($"UFunction '{Function.Path}' has no return value.");
        }
        returnReplacement = value;
    }

    /// <summary>Replaces an out or reference parameter after the original call.</summary>
    /// <param name="name">The reflected out/ref parameter name.</param>
    /// <param name="value">The replacement value.</param>
    /// <exception cref="InvalidOperationException">Thrown from a pre hook.</exception>
    /// <exception cref="ArgumentException">Thrown when the function has no out/ref parameter with that name.</exception>
    public void SetOutArgument(string name, UnrealValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Phase != UnrealHookPhase.Post)
        {
            throw new InvalidOperationException("UFunction out/ref arguments can only be replaced from a post hook.");
        }
        var descriptor = Function.ParameterList.FirstOrDefault(parameter =>
            parameter.IsOutput && !parameter.IsReturn && StringComparer.Ordinal.Equals(parameter.Name, name));
        if (descriptor is null)
        {
            throw new ArgumentException($"UFunction '{Function.Path}' has no out/ref parameter named '{name}'.", nameof(name));
        }
        outputReplacements[name] = value;
    }

    internal IReadOnlyDictionary<string, UnrealValue> ArgumentReplacements => argumentReplacements;

    internal IReadOnlyDictionary<string, UnrealValue> OutputReplacements => outputReplacements;

    internal UnrealValue? ReturnReplacement => returnReplacement;
}

/// <summary>Value adapters used by strongly typed generated hook callbacks.</summary>
public static class UnrealHookValue
{
    /// <summary>Wraps a transported object reference into its generated wrapper, or returns null.</summary>
    /// <typeparam name="TWrapper">The generated wrapper type.</typeparam>
    /// <param name="value">The transported value.</param>
    /// <param name="unreal">The live reflection service.</param>
    /// <param name="factory">Creates the wrapper for a non-null handle.</param>
    /// <returns>The wrapper, or null when the transported reference is null.</returns>
    public static TWrapper? WrapObject<TWrapper>(
        UnrealValue value,
        IUnrealReflection unreal,
        Func<IUnrealReflection, UnrealObjectHandle, TWrapper> factory)
        where TWrapper : UnrealObject
    {
        ArgumentNullException.ThrowIfNull(unreal);
        ArgumentNullException.ThrowIfNull(factory);
        var handle = value.AsObjectHandle();
        return handle.IsNull ? null : factory(unreal, handle);
    }
}

/// <summary>
/// Base class for type-safe wrappers emitted by the RogueMod SDK generator. Wrappers hold a
/// handle and a reflection service; they never own or root the underlying object.
/// </summary>
public class UnrealObject
{
    /// <summary>Initializes the wrapper for one live object.</summary>
    /// <param name="unreal">The reflection service used for all member access.</param>
    /// <param name="handle">The object handle.</param>
    public UnrealObject(IUnrealReflection unreal, UnrealObjectHandle handle)
    {
        Unreal = unreal ?? throw new ArgumentNullException(nameof(unreal));
        Handle = handle;
    }

    /// <summary>Gets the reflection service backing this wrapper.</summary>
    public IUnrealReflection Unreal { get; }

    /// <summary>Gets the wrapped object handle.</summary>
    public UnrealObjectHandle Handle { get; }

    /// <summary>Gets a value indicating whether the wrapped object is still alive.</summary>
    public bool IsValid => Unreal.IsValid(Handle);

    /// <summary>Gets the full Unreal path name of the wrapped object, or null when unavailable.</summary>
    public string? PathName => Unreal.GetPathName(Handle);

    /// <summary>Reads one property in its managed representation.</summary>
    protected T Read<T>(UnrealPropertyDescriptor property) =>
        Unreal.ReadProperty(Handle, property).As<T>();

    /// <summary>Reads one property as a raw transport value.</summary>
    protected UnrealValue ReadValue(UnrealPropertyDescriptor property) =>
        Unreal.ReadProperty(Handle, property);

    /// <summary>Reads one object-reference property, wrapping a non-null target.</summary>
    protected TWrapper? ReadObject<TWrapper>(
        UnrealPropertyDescriptor property,
        Func<IUnrealReflection, UnrealObjectHandle, TWrapper> factory)
        where TWrapper : UnrealObject
    {
        var handle = Unreal.ReadProperty(Handle, property).AsObjectHandle();
        return handle.IsNull ? null : factory(Unreal, handle);
    }

    /// <summary>Writes one property from its managed representation.</summary>
    protected void Write<T>(UnrealPropertyDescriptor property, T value) =>
        Unreal.WriteProperty(Handle, property, UnrealValue.From(value));

    /// <summary>Writes one property from a raw transport value.</summary>
    protected void WriteValue(UnrealPropertyDescriptor property, UnrealValue value) =>
        Unreal.WriteProperty(Handle, property, value);

    /// <summary>Writes one object-reference property, or clears it with null.</summary>
    protected void WriteObject(UnrealPropertyDescriptor property, UnrealObject? value) =>
        Unreal.WriteProperty(Handle, property, UnrealValue.From(value?.Handle ?? UnrealObjectHandle.Null));

    /// <summary>Wraps a transported object reference.</summary>
    protected TWrapper? WrapObject<TWrapper>(
        UnrealValue value,
        Func<IUnrealReflection, UnrealObjectHandle, TWrapper> factory)
        where TWrapper : UnrealObject
    {
        var handle = value.AsObjectHandle();
        return handle.IsNull ? null : factory(Unreal, handle);
    }

    /// <summary>Invokes one reflected UFunction.</summary>
    /// <param name="function">The function descriptor.</param>
    /// <param name="arguments">Input arguments.</param>
    /// <returns>The invocation result.</returns>
    protected UnrealInvocationResult Call(
        UnrealFunctionDescriptor function,
        params UnrealArgument[] arguments) => Unreal.Invoke(Handle, function, arguments);
}

/// <summary>Static construction contract implemented by generated Unreal object wrappers.</summary>
/// <typeparam name="TSelf">The wrapper type itself.</typeparam>
public interface IUnrealObjectType<TSelf> where TSelf : UnrealObject
{
    /// <summary>Gets the reflected Unreal class short name used for discovery.</summary>
    static abstract string UnrealClassName { get; }

    /// <summary>Creates a wrapper for one live object.</summary>
    /// <param name="unreal">The reflection service.</param>
    /// <param name="handle">The object handle.</param>
    static abstract TSelf Create(IUnrealReflection unreal, UnrealObjectHandle handle);
}

/// <summary>Type-safe object discovery for wrappers emitted by the RogueMod SDK generator.</summary>
public static class UnrealObjectDiscoveryExtensions
{
    /// <summary>Finds the first live instance of the wrapper's Unreal class.</summary>
    /// <typeparam name="T">The generated wrapper type.</typeparam>
    /// <param name="unreal">The reflection service.</param>
    /// <returns>The wrapper, or null when no live instance was found.</returns>
    public static T? FindFirst<T>(this IUnrealReflection unreal)
        where T : UnrealObject, IUnrealObjectType<T>
    {
        ArgumentNullException.ThrowIfNull(unreal);
        var handle = unreal.FindFirstOf(T.UnrealClassName);
        return handle.IsNull || !unreal.IsValid(handle) ? null : T.Create(unreal, handle);
    }

    /// <summary>Finds every live instance of the wrapper's Unreal class.</summary>
    /// <typeparam name="T">The generated wrapper type.</typeparam>
    /// <param name="unreal">The reflection service.</param>
    /// <returns>The wrappers of all matching live instances.</returns>
    public static IReadOnlyList<T> FindAll<T>(this IUnrealReflection unreal)
        where T : UnrealObject, IUnrealObjectType<T>
    {
        ArgumentNullException.ThrowIfNull(unreal);
        return unreal.FindAllOf(T.UnrealClassName)
            .Where(handle => !handle.IsNull && unreal.IsValid(handle))
            .Select(handle => T.Create(unreal, handle))
            .ToArray();
    }
}

/// <summary>Logger writing into <c>RogueMod.log</c> under the mod's own source prefix.</summary>
public interface IModLogger
{
    /// <summary>Writes one log entry.</summary>
    /// <param name="level">The severity level.</param>
    /// <param name="message">The message text.</param>
    void Log(ModLogLevel level, string message);
}

/// <summary>Severity levels for <see cref="IModLogger"/>.</summary>
public enum ModLogLevel
{
    /// <summary>Diagnostic detail for development.</summary>
    Trace,

    /// <summary>Debugging information.</summary>
    Debug,

    /// <summary>Normal operational messages.</summary>
    Information,

    /// <summary>Recoverable problems worth attention.</summary>
    Warning,

    /// <summary>Errors that disabled one operation.</summary>
    Error,

    /// <summary>Fatal problems that disable the mod.</summary>
    Critical
}
