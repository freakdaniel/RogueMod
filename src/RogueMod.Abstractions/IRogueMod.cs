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
}

[Flags]
public enum UnrealReflectionCapabilities
{
    None = 0,
    Objects = 1 << 0,
    PropertyRead = 1 << 1,
    PropertyWrite = 1 << 2,
    FunctionInvocation = 1 << 3,
    ObjectEnumeration = 1 << 4
}

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
    UnrealArrayDescriptor? Array = null);

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

/// <summary>Field-wise layout metadata for a POD Unreal script struct.</summary>
public sealed record UnrealStructDescriptor(
    string Path,
    int Size,
    int Alignment,
    IReadOnlyList<UnrealStructFieldDescriptor> Fields);

/// <summary>Layout and nested-type metadata for one field in a POD Unreal struct.</summary>
public sealed record UnrealStructFieldDescriptor(
    string Name,
    string UnrealType,
    int Offset,
    int Size,
    int ArrayDimension = 1,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0,
    UnrealStructDescriptor? Struct = null);

/// <summary>A field-wise POD struct value used by generated SDK adapters.</summary>
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
    UnrealStructDescriptor? ElementStruct = null);

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
