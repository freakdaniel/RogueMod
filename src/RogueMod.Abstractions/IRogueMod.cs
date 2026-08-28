namespace RogueMod.Abstractions;

/// <summary>Stable managed entry point implemented by a C# mod.</summary>
public interface IRogueMod
{
    ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default);

    ValueTask UnloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional synchronous lifecycle callbacks dispatched on the Unreal game thread.
/// A throwing handler is disabled for the remainder of the session.
/// </summary>
public interface IRogueModGameEvents
{
    void OnGameEvent(ModGameEventKind eventKind);
}

public enum ModGameEventKind
{
    ProgramStarted = 1,
    UnrealInitialized = 2,
    UiInitialized = 3,
    Update = 4,
    CppModsLoaded = 5
}

public interface IModContext
{
    string ModId { get; }

    string GameProfileId { get; }

    IModLogger Logger { get; }

    IUnrealReflection Unreal { get; }
}

public interface IUnrealReflection
{
    bool IsAvailable { get; }

    UnrealReflectionCapabilities Capabilities =>
        IsAvailable ? UnrealReflectionCapabilities.Objects : UnrealReflectionCapabilities.None;

    UnrealObjectHandle FindFirstOf(string className);

    IReadOnlyList<UnrealObjectHandle> FindAllOf(string className) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal object enumeration.");

    bool IsValid(UnrealObjectHandle handle);

    UnrealObjectHandle GetClass(UnrealObjectHandle handle);

    string? GetPathName(UnrealObjectHandle handle);

    UnrealValue ReadProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal property reads.");

    void WriteProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property, UnrealValue value) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal property writes.");

    UnrealInvocationResult Invoke(
        UnrealObjectHandle handle,
        UnrealFunctionDescriptor function,
        IReadOnlyList<UnrealArgument> arguments) =>
        throw new NotSupportedException("The active RogueMod bridge does not support UFunction invocation.");

    /// <summary>Observes calls to a reflected UFunction until the returned subscription is disposed.</summary>
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
    UnrealObjectHandle CreateObject(
        UnrealObjectHandle classHandle,
        UnrealObjectHandle outerHandle,
        string? objectName = null) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal object creation.");

    /// <summary>
    /// Spawns an actor of the given class into the world that owns the context object.
    /// Requires <see cref="UnrealReflectionCapabilities.ActorSpawning"/>.
    /// </summary>
    UnrealObjectHandle SpawnActor(
        UnrealObjectHandle contextObject,
        UnrealObjectHandle classHandle,
        UnrealVector location,
        UnrealRotator rotation) =>
        throw new NotSupportedException("The active RogueMod bridge does not support Unreal actor spawning.");

    /// <summary>Registers a UFunction hook with deterministic ordering and an optional exact-object filter.</summary>
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

[Flags]
public enum UnrealReflectionCapabilities
{
    None = 0,
    Objects = 1 << 0,
    PropertyRead = 1 << 1,
    PropertyWrite = 1 << 2,
    FunctionInvocation = 1 << 3,
    ObjectEnumeration = 1 << 4,
    NestedArrays = 1 << 5,
    OptionalValues = 1 << 6,
    WeakObjectReferences = 1 << 7,
    LazyObjectReferences = 1 << 8,
    FunctionHooks = 1 << 9,
    ObjectCreation = 1 << 10,
    ActorSpawning = 1 << 11,
    SoftObjectReferences = 1 << 12,
    InterfaceReferences = 1 << 13,
    MapSetProperties = 1 << 14,
    MapSetWrites = 1 << 15
}

public enum UnrealHookPhase
{
    Pre = 1,
    Post = 2
}

/// <summary>
/// Registration policy for a UFunction hook. Higher priorities run first; equal priorities
/// retain registration order. A null instance handle matches every object. Post hooks may skip
/// decoding pure input parameters when their callback only consumes return and out/ref values.
/// </summary>
public readonly record struct UnrealHookOptions(
    int Priority = 0,
    UnrealObjectHandle Instance = default,
    bool SkipInputDecoding = false);

public readonly record struct UnrealObjectHandle(ulong Value)
{
    public static UnrealObjectHandle Null => default;

    public bool IsNull => Value == 0;
}

/// <summary>A stable property identity emitted by the generated game SDK.</summary>
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
public readonly record struct UnrealGuid(uint A, uint B, uint C, uint D)
{
    public static UnrealGuid Empty => default;

    public bool IsEmpty => A == 0 && B == 0 && C == 0 && D == 0;

    public override string ToString() => $"{A:X8}-{B:X8}-{C:X8}-{D:X8}";
}

/// <summary>An Unreal FVector translation (three consecutive float components).</summary>
public readonly record struct UnrealVector(float X, float Y, float Z)
{
    public static UnrealVector Zero => default;
}

/// <summary>An Unreal FRotator (pitch, yaw, roll, three consecutive float components).</summary>
public readonly record struct UnrealRotator(float Pitch, float Yaw, float Roll)
{
    public static UnrealRotator Zero => default;
}

/// <summary>Runtime layout metadata for one reflected UFunction parameter.</summary>
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

    public bool IsOutput => HasFlag("CPF_OutParm");

    public bool IsReturn => HasFlag("CPF_ReturnParm");

    public bool IsReference => HasFlag("CPF_ReferenceParm");

    public bool IsInput => !IsReturn && (!IsOutput || IsReference);

    private bool HasFlag(string flag) =>
        Flags.Split('|', StringSplitOptions.TrimEntries).Contains(flag, StringComparer.Ordinal);
}

/// <summary>A stable UFunction identity emitted by the generated game SDK.</summary>
public sealed record UnrealFunctionDescriptor(
    string OwnerPath,
    string Path,
    string Name,
    string Flags,
    IReadOnlyList<UnrealParameterDescriptor>? Parameters = null)
{
    public IReadOnlyList<UnrealParameterDescriptor> ParameterList => Parameters ?? [];
}

public readonly record struct UnrealArgument(string Name, UnrealValue Value);

/// <summary>Field-wise layout metadata for a transportable Unreal script struct.</summary>
public sealed record UnrealStructDescriptor(
    string Path,
    int Size,
    int Alignment,
    IReadOnlyList<UnrealStructFieldDescriptor> Fields,
    bool RawLayout = false);

/// <summary>Layout and nested-type metadata for one field in a transportable Unreal struct.</summary>
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
public sealed record UnrealStructValue(
    UnrealStructDescriptor Descriptor,
    IReadOnlyDictionary<string, UnrealValue> Fields)
{
    public UnrealValue GetField(string name) => Fields.TryGetValue(name, out var value)
        ? value
        : throw new KeyNotFoundException($"Unreal struct '{Descriptor.Path}' has no transported field named '{name}'.");
}

/// <summary>Element metadata for a one-dimensional Unreal TArray.</summary>
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

    public static IReadOnlyList<T> ToList<T>(UnrealValue value, Func<UnrealValue, T> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        var transported = value.As<UnrealArrayValue>();
        return transported.Elements.Select(decode).ToArray();
    }
}

/// <summary>Inner-value metadata for an Unreal TOptional.</summary>
public sealed record UnrealOptionalDescriptor(
    string ValueUnrealType,
    int ValueSize,
    int ValueByteOffset = 0,
    int ValueByteMask = 0,
    int ValueFieldMask = 0,
    UnrealStructDescriptor? ValueStruct = null);

/// <summary>A set or unset value transported through a generated TOptional adapter.</summary>
public sealed record UnrealOptionalValue(
    UnrealOptionalDescriptor Descriptor,
    bool IsSet,
    UnrealValue Value);

/// <summary>
/// Key/value metadata for an Unreal TMap. Keys are scalar-only in RogueMod ABI 13; values may
/// carry a nested TArray descriptor. The property itself reports an 80-byte FScriptMap on the
/// supported Deadzone: Rogue 1.4.2.0 / UE 5.6.1 build, which the runtime validates.
/// </summary>
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
    public const int NativeStorageSize = 24;

    private readonly byte[] nativeStorage;

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

    public UnrealGuid ObjectId { get; }

    public UnrealObjectHandle CachedHandle { get; }

    public bool IsNull => ObjectId.IsEmpty && CachedHandle.IsNull;

    public byte[] CopyNativeStorage() => (byte[])nativeStorage.Clone();

    public static UnrealLazyObjectValue Null { get; } =
        new(UnrealGuid.Empty, UnrealObjectHandle.Null, new byte[NativeStorageSize]);
}

/// <summary>
/// A typed Unreal lazy reference. Unlike a weak reference, it retains its persistent object
/// identity while the target is unloaded; it does not load or keep the target alive.
/// </summary>
public sealed class UnrealLazyObjectReference<T> where T : UnrealObject
{
    private readonly UnrealLazyObjectValue transported;

    private UnrealLazyObjectReference(UnrealLazyObjectValue transported, T? target)
    {
        this.transported = transported;
        CachedTarget = target;
    }

    public UnrealGuid ObjectId => transported.ObjectId;

    /// <summary>The target already cached by Unreal, or null when the reference is pending or stale.</summary>
    public T? CachedTarget { get; }

    public bool IsNull => transported.IsNull;

    public bool IsPending => !IsNull && CachedTarget is null;

    public static UnrealLazyObjectReference<T> Null { get; } =
        new(UnrealLazyObjectValue.Null, null);

    public UnrealValue ToUnrealValue() => UnrealValue.From(transported);

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
    public const int NativeStorageSize = 40;

    private readonly byte[] nativeStorage;

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

    public string Path { get; }

    public UnrealObjectHandle CachedHandle { get; }

    public bool IsNull => Path.Length == 0;

    public byte[] CopyNativeStorage() => (byte[])nativeStorage.Clone();
}

/// <summary>
/// A typed Unreal soft object reference. The persistent asset path remains available whether
/// or not the target is loaded; <see cref="CachedTarget"/> is only the already-cached object
/// and never loads or roots the target.
/// </summary>
public sealed class UnrealSoftObjectReference<T> where T : UnrealObject
{
    private readonly UnrealSoftObjectValue transported;

    private UnrealSoftObjectReference(UnrealSoftObjectValue transported, T? target)
    {
        this.transported = transported;
        CachedTarget = target;
    }

    public string Path => transported.Path;

    public T? CachedTarget { get; }

    public bool IsNull => transported.IsNull;

    public static UnrealSoftObjectReference<T> Null { get; } =
        new(new UnrealSoftObjectValue(string.Empty, UnrealObjectHandle.Null, new byte[UnrealSoftObjectValue.NativeStorageSize]), null);

    /// <summary>
    /// Creates an unloaded soft reference from its persistent Unreal asset path.
    /// This does not load or resolve the referenced object.
    /// </summary>
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

    public UnrealValue ToUnrealValue() => UnrealValue.From(transported);

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
public readonly record struct UnrealOptional<T>
{
    private readonly T? value;

    private UnrealOptional(bool isSet, T? value)
    {
        IsSet = isSet;
        this.value = value;
    }

    public bool IsSet { get; }

    public T Value => IsSet
        ? value!
        : throw new InvalidOperationException("An unset Unreal TOptional has no value.");

    public static UnrealOptional<T> Unset => default;

    public static UnrealOptional<T> FromValue(T value) => new(true, value);

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
public readonly record struct UnrealValue(object? Value)
{
    public static UnrealValue Null => default;

    public static UnrealValue From<T>(T value) => new(value);

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

    public UnrealObjectHandle AsObjectHandle() => As<UnrealObjectHandle>();
}

public sealed record UnrealInvocationResult(
    UnrealValue ReturnValue,
    IReadOnlyDictionary<string, UnrealValue> OutArguments)
{
    public static UnrealInvocationResult Empty { get; } =
        new(UnrealValue.Null, new Dictionary<string, UnrealValue>(StringComparer.Ordinal));

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

    public UnrealObjectHandle Object { get; }

    public UnrealFunctionDescriptor Function { get; }

    public UnrealHookPhase Phase { get; }

    public IReadOnlyDictionary<string, UnrealValue> Arguments { get; }

    public UnrealInvocationResult Result { get; }

    /// <summary>Replaces an input or reference parameter before the original UFunction runs.</summary>
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
            throw new ArgumentException($"UFunction '{Function.Path}' has no out or ref parameter named '{name}'.", nameof(name));
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

/// <summary>Base class for type-safe wrappers emitted by the RogueMod SDK generator.</summary>
public class UnrealObject
{
    public UnrealObject(IUnrealReflection unreal, UnrealObjectHandle handle)
    {
        Unreal = unreal ?? throw new ArgumentNullException(nameof(unreal));
        Handle = handle;
    }

    public IUnrealReflection Unreal { get; }

    public UnrealObjectHandle Handle { get; }

    public bool IsValid => Unreal.IsValid(Handle);

    public string? PathName => Unreal.GetPathName(Handle);

    protected T Read<T>(UnrealPropertyDescriptor property) =>
        Unreal.ReadProperty(Handle, property).As<T>();

    protected UnrealValue ReadValue(UnrealPropertyDescriptor property) =>
        Unreal.ReadProperty(Handle, property);

    protected TWrapper? ReadObject<TWrapper>(
        UnrealPropertyDescriptor property,
        Func<IUnrealReflection, UnrealObjectHandle, TWrapper> factory)
        where TWrapper : UnrealObject
    {
        var handle = Unreal.ReadProperty(Handle, property).AsObjectHandle();
        return handle.IsNull ? null : factory(Unreal, handle);
    }

    protected void Write<T>(UnrealPropertyDescriptor property, T value) =>
        Unreal.WriteProperty(Handle, property, UnrealValue.From(value));

    protected void WriteValue(UnrealPropertyDescriptor property, UnrealValue value) =>
        Unreal.WriteProperty(Handle, property, value);

    protected void WriteObject(UnrealPropertyDescriptor property, UnrealObject? value) =>
        Unreal.WriteProperty(Handle, property, UnrealValue.From(value?.Handle ?? UnrealObjectHandle.Null));

    protected TWrapper? WrapObject<TWrapper>(
        UnrealValue value,
        Func<IUnrealReflection, UnrealObjectHandle, TWrapper> factory)
        where TWrapper : UnrealObject
    {
        var handle = value.AsObjectHandle();
        return handle.IsNull ? null : factory(Unreal, handle);
    }

    protected UnrealInvocationResult Call(
        UnrealFunctionDescriptor function,
        params UnrealArgument[] arguments) => Unreal.Invoke(Handle, function, arguments);
}

/// <summary>Static construction contract implemented by generated Unreal object wrappers.</summary>
public interface IUnrealObjectType<TSelf> where TSelf : UnrealObject
{
    static abstract string UnrealClassName { get; }

    static abstract TSelf Create(IUnrealReflection unreal, UnrealObjectHandle handle);
}

/// <summary>Type-safe object discovery for wrappers emitted by the RogueMod SDK generator.</summary>
public static class UnrealObjectDiscoveryExtensions
{
    public static T? FindFirst<T>(this IUnrealReflection unreal)
        where T : UnrealObject, IUnrealObjectType<T>
    {
        ArgumentNullException.ThrowIfNull(unreal);
        var handle = unreal.FindFirstOf(T.UnrealClassName);
        return handle.IsNull || !unreal.IsValid(handle) ? null : T.Create(unreal, handle);
    }

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

public interface IModLogger
{
    void Log(ModLogLevel level, string message);
}

public enum ModLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}
