using RogueMod.Abstractions;

namespace RogueMod.Runtime;

/// <summary>
/// Owns the stable mapping between reflected Unreal property metadata and the
/// RogueMod wire kinds. Property access, invocation, and future hook arguments
/// must all use this registry instead of maintaining independent type switches.
/// </summary>
internal static class NativeReflectionTypeRegistry
{
    private const uint PropertyKindMask = 0xff;

    internal static NativePropertyKind GetPropertyKind(string unrealType, int size)
    {
        var separator = unrealType.IndexOf(':');
        var type = separator < 0 ? unrealType : unrealType[..separator];
        return type switch
        {
            "BoolProperty" => NativePropertyKind.Boolean,
            "Int8Property" => NativePropertyKind.Int8,
            "ByteProperty" => NativePropertyKind.UInt8,
            "Int16Property" => NativePropertyKind.Int16,
            "UInt16Property" => NativePropertyKind.UInt16,
            "IntProperty" => NativePropertyKind.Int32,
            "UInt32Property" => NativePropertyKind.UInt32,
            "Int64Property" => NativePropertyKind.Int64,
            "UInt64Property" => NativePropertyKind.UInt64,
            "FloatProperty" => NativePropertyKind.Float,
            "DoubleProperty" => NativePropertyKind.Double,
            "ObjectProperty" or "ClassProperty" => NativePropertyKind.Object,
            "WeakObjectProperty" when size == 8 => NativePropertyKind.WeakObject,
            "LazyObjectProperty" when size == UnrealLazyObjectValue.NativeStorageSize => NativePropertyKind.LazyObject,
            "StrProperty" when size == 16 => NativePropertyKind.String,
            "NameProperty" when size == 8 => NativePropertyKind.Name,
            "TextProperty" when size == 16 => NativePropertyKind.Text,
            "EnumProperty" when size == 1 => NativePropertyKind.UInt8,
            "EnumProperty" when size == 2 => NativePropertyKind.UInt16,
            "EnumProperty" when size == 4 => NativePropertyKind.UInt32,
            "EnumProperty" when size == 8 => NativePropertyKind.UInt64,
            "StructProperty" when size > 0 => NativePropertyKind.Struct,
            "ArrayProperty" when size == 16 => NativePropertyKind.Array,
            "OptionalProperty" when size > 0 => NativePropertyKind.Optional,
            _ => throw new NotSupportedException($"Property type '{unrealType}' is not supported by RogueMod ABI 13.")
        };
    }

    internal static NativePropertyKind DecodePropertyKind(uint encodedKind) =>
        (NativePropertyKind)(encodedKind & PropertyKindMask);

    internal static StructFieldKind GetFieldKind(string unrealType, int size)
    {
        var separator = unrealType.IndexOf(':');
        var type = separator < 0 ? unrealType : unrealType[..separator];
        return type switch
        {
            "BoolProperty" when size == 1 => StructFieldKind.Boolean,
            "Int8Property" when size == 1 => StructFieldKind.Int8,
            "ByteProperty" when size == 1 => StructFieldKind.UInt8,
            "Int16Property" when size == 2 => StructFieldKind.Int16,
            "UInt16Property" when size == 2 => StructFieldKind.UInt16,
            "IntProperty" when size == 4 => StructFieldKind.Int32,
            "UInt32Property" when size == 4 => StructFieldKind.UInt32,
            "Int64Property" when size == 8 => StructFieldKind.Int64,
            "UInt64Property" when size == 8 => StructFieldKind.UInt64,
            "FloatProperty" when size == 4 => StructFieldKind.Float,
            "DoubleProperty" when size == 8 => StructFieldKind.Double,
            "EnumProperty" when size == 1 => StructFieldKind.UInt8,
            "EnumProperty" when size == 2 => StructFieldKind.UInt16,
            "EnumProperty" when size == 4 => StructFieldKind.UInt32,
            "EnumProperty" when size == 8 => StructFieldKind.UInt64,
            "StructProperty" => StructFieldKind.Struct,
            _ => throw new NotSupportedException($"POD struct field type '{unrealType}' with size {size} is not supported.")
        };
    }
}

internal static class NativeScalarValueCodec
{
    internal static object Decode(NativePropertyKind kind, ulong data) => kind switch
    {
        NativePropertyKind.Boolean => data != 0,
        NativePropertyKind.Int8 => unchecked((sbyte)data),
        NativePropertyKind.UInt8 => unchecked((byte)data),
        NativePropertyKind.Int16 => unchecked((short)data),
        NativePropertyKind.UInt16 => unchecked((ushort)data),
        NativePropertyKind.Int32 => unchecked((int)data),
        NativePropertyKind.UInt32 => unchecked((uint)data),
        NativePropertyKind.Int64 => unchecked((long)data),
        NativePropertyKind.UInt64 => data,
        NativePropertyKind.Float => BitConverter.Int32BitsToSingle(unchecked((int)data)),
        NativePropertyKind.Double => BitConverter.Int64BitsToDouble(unchecked((long)data)),
        NativePropertyKind.Object or NativePropertyKind.WeakObject => new UnrealObjectHandle(data),
        _ => throw new InvalidOperationException($"Unreal property kind '{kind}' is not a scalar wire value.")
    };

    internal static ulong Encode(NativePropertyKind kind, object? value)
    {
        var managed = value is Enum enumValue
            ? kind switch
            {
                NativePropertyKind.Int8 => Convert.ToSByte(enumValue),
                NativePropertyKind.UInt8 => Convert.ToByte(enumValue),
                NativePropertyKind.Int16 => Convert.ToInt16(enumValue),
                NativePropertyKind.UInt16 => Convert.ToUInt16(enumValue),
                NativePropertyKind.Int32 => Convert.ToInt32(enumValue),
                NativePropertyKind.UInt32 => Convert.ToUInt32(enumValue),
                NativePropertyKind.Int64 => Convert.ToInt64(enumValue),
                NativePropertyKind.UInt64 => Convert.ToUInt64(enumValue),
                _ => value
            }
            : value;

        return kind switch
        {
            NativePropertyKind.Boolean when managed is bool typed => typed ? 1UL : 0UL,
            NativePropertyKind.Int8 when managed is sbyte typed => unchecked((ulong)typed),
            NativePropertyKind.UInt8 when managed is byte typed => typed,
            NativePropertyKind.Int16 when managed is short typed => unchecked((ulong)typed),
            NativePropertyKind.UInt16 when managed is ushort typed => typed,
            NativePropertyKind.Int32 when managed is int typed => unchecked((ulong)typed),
            NativePropertyKind.UInt32 when managed is uint typed => typed,
            NativePropertyKind.Int64 when managed is long typed => unchecked((ulong)typed),
            NativePropertyKind.UInt64 when managed is ulong typed => typed,
            NativePropertyKind.Float when managed is float typed => unchecked((uint)BitConverter.SingleToInt32Bits(typed)),
            NativePropertyKind.Double when managed is double typed => unchecked((ulong)BitConverter.DoubleToInt64Bits(typed)),
            NativePropertyKind.Object or NativePropertyKind.WeakObject when managed is UnrealObjectHandle typed => typed.Value,
            _ => throw new InvalidCastException(
                $"Unreal property kind '{kind}' cannot be written from managed value type " +
                $"'{value?.GetType().FullName ?? "null"}'.")
        };
    }
}

internal enum NativePropertyKind : uint
{
    Boolean = 1,
    Int8 = 2,
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Float = 10,
    Double = 11,
    Object = 12,
    String = 13,
    Name = 14,
    Struct = 15,
    Text = 16,
    Array = 17,
    Optional = 18,
    WeakObject = 19,
    LazyObject = 20
}

internal enum StructFieldKind
{
    Boolean,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    Struct
}
