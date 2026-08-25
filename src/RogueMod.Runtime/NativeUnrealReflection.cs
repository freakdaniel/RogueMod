using RogueMod.Abstractions;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RogueMod.Runtime;

internal sealed unsafe class NativeUnrealReflection(
    delegate* unmanaged[Cdecl]<int> isAvailable,
    delegate* unmanaged[Cdecl]<char*, ulong> findFirstOf,
    delegate* unmanaged[Cdecl]<ulong, int> isValid,
    delegate* unmanaged[Cdecl]<ulong, ulong> getClass,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, uint*, int> getPathName,
    delegate* unmanaged[Cdecl]<uint> getCapabilities,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> readProperty,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> writeProperty,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealParameter*, int> invokeFunction,
    delegate* unmanaged[Cdecl]<char*, ulong*, uint, uint*, int> findAllOf) : IUnrealReflection
{
    private const uint MaximumPathLength = 1_048_576;
    private const uint MaximumStringLength = 1_048_576;
    private const int MaximumStructSize = 1_048_576;
    private const uint MaximumArrayLength = 1_048_576;
    private const uint MaximumObjectCount = 1_048_576;
    private const uint LazyObjectWireSize = 48;
    private const int LazyObjectStorageSize = UnrealLazyObjectValue.NativeStorageSize;
    private const uint PropertyKindMask = 0xff;
    private const int ContainerValueKindShift = 8;
    private const uint MaximumEncodedValueKind = 0x00ff_ffff;

    public bool IsAvailable => isAvailable != null && isAvailable() != 0;

    public UnrealReflectionCapabilities Capabilities =>
        !IsAvailable || getCapabilities == null
            ? UnrealReflectionCapabilities.None
            : (UnrealReflectionCapabilities)getCapabilities();

    public UnrealObjectHandle FindFirstOf(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        if (!IsAvailable || findFirstOf == null)
        {
            return UnrealObjectHandle.Null;
        }

        fixed (char* classNamePointer = className)
        {
            return new(findFirstOf(classNamePointer));
        }
    }

    public IReadOnlyList<UnrealObjectHandle> FindAllOf(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        if ((Capabilities & UnrealReflectionCapabilities.ObjectEnumeration) == 0 || findAllOf == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal object enumeration.");
        }

        fixed (char* classNamePointer = className)
        {
            uint required = 0;
            var result = findAllOf(classNamePointer, null, 0, &required);
            if (result < 0 || required > MaximumObjectCount)
            {
                throw new InvalidOperationException($"Unreal object enumeration failed with native status {result}.");
            }
            if (required == 0)
            {
                return [];
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var handles = new ulong[required];
                fixed (ulong* handlePointer = handles)
                {
                    result = findAllOf(classNamePointer, handlePointer, (uint)handles.Length, &required);
                }
                if (result == 0)
                {
                    if (required < handles.Length)
                    {
                        Array.Resize(ref handles, checked((int)required));
                    }
                    return handles.Select(value => new UnrealObjectHandle(value)).ToArray();
                }
                if (result < 0 || required > MaximumObjectCount)
                {
                    throw new InvalidOperationException($"Unreal object enumeration failed with native status {result}.");
                }
            }
        }

        throw new InvalidOperationException("Unreal object enumeration changed repeatedly while results were being copied.");
    }

    public bool IsValid(UnrealObjectHandle handle) =>
        !handle.IsNull && IsAvailable && isValid != null && isValid(handle.Value) != 0;

    public UnrealObjectHandle GetClass(UnrealObjectHandle handle)
    {
        if (!IsValid(handle) || getClass == null)
        {
            return UnrealObjectHandle.Null;
        }
        return new(getClass(handle.Value));
    }

    public string? GetPathName(UnrealObjectHandle handle)
    {
        if (!IsValid(handle) || getPathName == null)
        {
            return null;
        }

        uint required = 0;
        var result = getPathName(handle.Value, null, 0, &required);
        if (result < 0 || required is 0 or > MaximumPathLength)
        {
            return null;
        }

        var buffer = new char[required];
        fixed (char* bufferPointer = buffer)
        {
            result = getPathName(handle.Value, bufferPointer, required, &required);
        }
        return result == 0 && required > 0
            ? new string(buffer, 0, checked((int)required - 1))
            : null;
    }

    public UnrealInvocationResult Invoke(
        UnrealObjectHandle handle,
        UnrealFunctionDescriptor function,
        IReadOnlyList<UnrealArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        if ((Capabilities & UnrealReflectionCapabilities.FunctionInvocation) == 0 || invokeFunction == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support UFunction invocation.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot invoke a UFunction on an invalid Unreal object handle.");
        }
        var descriptors = function.ParameterList;
        var suppliedArguments = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!suppliedArguments.TryAdd(argument.Name, argument.Value))
            {
                throw new ArgumentException($"UFunction argument '{argument.Name}' was supplied more than once.", nameof(arguments));
            }
        }
        var nativeParameters = new NativeUnrealParameter[descriptors.Count];
        using var inputAllocations = new NativeAllocations();
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (descriptor.ArrayDimension != 1)
            {
                throw new NotSupportedException(
                    $"UFunction parameter '{function.Path}:{descriptor.Name}' is a fixed native array; ABI 10 supports scalar parameters and dynamic TArray values only.");
            }
            var kind = GetPropertyKind(descriptor.UnrealType, descriptor.Size);
            EnsurePropertyCapabilities(kind, descriptor.Array, descriptor.Optional);
            var encodedKind = EncodePropertyKind(kind, descriptor.Array, descriptor.Optional);
            var flags = (descriptor.IsInput ? NativeParameterFlags.Input : 0)
                | (descriptor.IsOutput ? NativeParameterFlags.Output : 0)
                | (descriptor.IsReturn ? NativeParameterFlags.Return : 0);
            NativeUnrealValue nativeValue;
            if (descriptor.IsInput)
            {
                if (!suppliedArguments.Remove(descriptor.Name, out var argument))
                {
                    throw new ArgumentException(
                        $"Required UFunction argument '{descriptor.Name}' was not supplied for '{function.Path}'.",
                        nameof(arguments));
                }
                nativeValue = ToNativeValue(
                    kind,
                    encodedKind,
                    argument,
                    descriptor.Struct,
                    descriptor.Array,
                    descriptor.Optional,
                    inputAllocations);
            }
            else
            {
                nativeValue = new NativeUnrealValue { Kind = encodedKind };
            }
            nativeParameters[index] = new NativeUnrealParameter
            {
                Kind = encodedKind,
                Flags = (uint)flags,
                Offset = descriptor.Offset,
                Size = descriptor.Size,
                ArrayDimension = (uint)descriptor.ArrayDimension,
                BoolLayout = (uint)(descriptor.ByteOffset & 0xff)
                    | (uint)(descriptor.ByteMask & 0xff) << 8
                    | (uint)(descriptor.FieldMask & 0xff) << 16,
                Value = nativeValue
            };
        }
        if (suppliedArguments.Count != 0)
        {
            throw new ArgumentException(
                $"Unknown UFunction argument(s) for '{function.Path}': {string.Join(", ", suppliedArguments.Keys)}.",
                nameof(arguments));
        }

        try
        {
            int result;
            fixed (char* functionName = function.Name)
            fixed (NativeUnrealParameter* parameterPointer = nativeParameters)
            {
                result = invokeFunction(handle.Value, functionName, (uint)nativeParameters.Length, parameterPointer);
            }
            if (result == -4 || result == -8)
            {
                throw new InvalidOperationException(
                    $"UFunction '{function.Path}' no longer matches its generated SDK descriptor (native status {result}). " +
                    "Regenerate the SDK from a current JMAP dump.");
            }
            if (result != 0)
            {
                throw new InvalidOperationException($"UFunction '{function.Path}' invocation failed with native status {result}.");
            }
            var returnValue = UnrealValue.Null;
            var outputs = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
            for (var index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                if (!descriptor.IsOutput && !descriptor.IsReturn)
                {
                    continue;
                }
                var value = ToManagedValue(
                    DecodePropertyKind(nativeParameters[index].Kind),
                    nativeParameters[index].Value,
                    descriptor.Struct,
                    descriptor.Array,
                    descriptor.Optional);
                if (descriptor.IsReturn)
                {
                    returnValue = value;
                }
                else
                {
                    outputs.Add(descriptor.Name, value);
                }
            }
            return new UnrealInvocationResult(returnValue, outputs);
        }
        finally
        {
            FreeNativeOutputAllocations(descriptors, nativeParameters, inputAllocations);
        }
    }

    public UnrealValue ReadProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if ((Capabilities & UnrealReflectionCapabilities.PropertyRead) == 0 || readProperty == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal property reads.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot read a property from an invalid Unreal object handle.");
        }

        var kind = GetPropertyKind(property.UnrealType, property.Size);
        EnsurePropertyCapabilities(kind, property.Array, property.Optional);
        var encodedKind = EncodePropertyKind(kind, property.Array, property.Optional);
        NativeUnrealValue nativeValue;
        int result;
        fixed (char* propertyName = property.Name)
        {
            result = readProperty(handle.Value, propertyName, encodedKind, &nativeValue);
        }
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unreal property '{property.OwnerPath}:{property.Name}' read failed with native status {result}.");
        }

        try
        {
            return ToManagedValue(kind, nativeValue, property.Struct, property.Array, property.Optional);
        }
        finally
        {
            FreeNativeAllocation(encodedKind, nativeValue, null);
        }
    }

    private static UnrealValue ToManagedValue(
        NativePropertyKind kind,
        NativeUnrealValue nativeValue,
        UnrealStructDescriptor? structDescriptor = null,
        UnrealArrayDescriptor? arrayDescriptor = null,
        UnrealOptionalDescriptor? optionalDescriptor = null)
    {
        object managedValue = kind switch
        {
            NativePropertyKind.Boolean => nativeValue.Data != 0,
            NativePropertyKind.Int8 => unchecked((sbyte)nativeValue.Data),
            NativePropertyKind.UInt8 => unchecked((byte)nativeValue.Data),
            NativePropertyKind.Int16 => unchecked((short)nativeValue.Data),
            NativePropertyKind.UInt16 => unchecked((ushort)nativeValue.Data),
            NativePropertyKind.Int32 => unchecked((int)nativeValue.Data),
            NativePropertyKind.UInt32 => unchecked((uint)nativeValue.Data),
            NativePropertyKind.Int64 => unchecked((long)nativeValue.Data),
            NativePropertyKind.UInt64 => nativeValue.Data,
            NativePropertyKind.Float => BitConverter.Int32BitsToSingle(unchecked((int)nativeValue.Data)),
            NativePropertyKind.Double => BitConverter.Int64BitsToDouble(unchecked((long)nativeValue.Data)),
            NativePropertyKind.Object or NativePropertyKind.WeakObject => new UnrealObjectHandle(nativeValue.Data),
            NativePropertyKind.LazyObject => ReadNativeLazyObject(nativeValue),
            NativePropertyKind.String or NativePropertyKind.Name or NativePropertyKind.Text => ReadNativeString(nativeValue),
            NativePropertyKind.Struct => ReadNativeStruct(nativeValue, RequireStructDescriptor(structDescriptor)),
            NativePropertyKind.Array => ReadNativeArray(nativeValue, RequireArrayDescriptor(arrayDescriptor)),
            NativePropertyKind.Optional => ReadNativeOptional(nativeValue, RequireOptionalDescriptor(optionalDescriptor)),
            _ => throw new System.Diagnostics.UnreachableException()
        };
        return new UnrealValue(managedValue);
    }

    public void WriteProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property, UnrealValue value)
    {
        ArgumentNullException.ThrowIfNull(property);
        if ((Capabilities & UnrealReflectionCapabilities.PropertyWrite) == 0 || writeProperty == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal property writes.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot write a property on an invalid Unreal object handle.");
        }

        var kind = GetPropertyKind(property.UnrealType, property.Size);
        EnsurePropertyCapabilities(kind, property.Array, property.Optional);
        var encodedKind = EncodePropertyKind(kind, property.Array, property.Optional);
        using var inputAllocations = new NativeAllocations();
        var nativeValue = ToNativeValue(
            kind,
            encodedKind,
            value,
            property.Struct,
            property.Array,
            property.Optional,
            inputAllocations);
        int result;
        fixed (char* propertyName = property.Name)
        {
            result = writeProperty(handle.Value, propertyName, encodedKind, &nativeValue);
        }
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unreal property '{property.OwnerPath}:{property.Name}' write failed with native status {result}.");
        }
    }

    private static NativeUnrealValue ToNativeValue(
        NativePropertyKind kind,
        uint encodedKind,
        UnrealValue value,
        UnrealStructDescriptor? structDescriptor,
        UnrealArrayDescriptor? arrayDescriptor,
        UnrealOptionalDescriptor? optionalDescriptor,
        NativeAllocations allocations)
    {
        var managed = value.Value;
        if (managed is Enum enumValue)
        {
            managed = kind switch
            {
                NativePropertyKind.Int8 => Convert.ToSByte(enumValue),
                NativePropertyKind.UInt8 => Convert.ToByte(enumValue),
                NativePropertyKind.Int16 => Convert.ToInt16(enumValue),
                NativePropertyKind.UInt16 => Convert.ToUInt16(enumValue),
                NativePropertyKind.Int32 => Convert.ToInt32(enumValue),
                NativePropertyKind.UInt32 => Convert.ToUInt32(enumValue),
                NativePropertyKind.Int64 => Convert.ToInt64(enumValue),
                NativePropertyKind.UInt64 => Convert.ToUInt64(enumValue),
                _ => managed
            };
        }
        if (kind is NativePropertyKind.String or NativePropertyKind.Name or NativePropertyKind.Text)
        {
            if (managed is not string text)
            {
                throw new InvalidCastException(
                    $"Unreal property kind '{kind}' cannot be written from managed value type " +
                    $"'{value.Value?.GetType().FullName ?? "null"}'.");
            }
            if (text.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Unreal FString and FName values cannot contain embedded null characters.", nameof(value));
            }
            if (text.Length > MaximumStringLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Unreal string values cannot exceed {MaximumStringLength} UTF-16 code units.");
            }
            var pointer = allocations.AddString(text);
            return new NativeUnrealValue
            {
                Kind = encodedKind,
                Reserved = (uint)text.Length,
                Data = unchecked((ulong)pointer)
            };
        }

        if (kind == NativePropertyKind.Struct)
        {
            var descriptor = RequireStructDescriptor(structDescriptor);
            if (managed is not UnrealStructValue structValue)
            {
                throw new InvalidCastException(
                    $"Unreal struct '{descriptor.Path}' requires an UnrealStructValue, not " +
                    $"'{value.Value?.GetType().FullName ?? "null"}'.");
            }
            var bytes = SerializeStruct(descriptor, structValue);
            var pointer = allocations.AddBytes(bytes);
            return new NativeUnrealValue
            {
                Kind = encodedKind,
                Reserved = (uint)bytes.Length,
                Data = unchecked((ulong)pointer)
            };
        }

        if (kind == NativePropertyKind.Array)
        {
            return WriteNativeArray(
                encodedKind,
                value,
                RequireArrayDescriptor(arrayDescriptor),
                allocations);
        }

        if (kind == NativePropertyKind.Optional)
        {
            return WriteNativeOptional(
                encodedKind,
                value,
                RequireOptionalDescriptor(optionalDescriptor),
                allocations);
        }
        if (kind == NativePropertyKind.LazyObject)
        {
            return WriteNativeLazyObject(encodedKind, value, allocations);
        }

        ulong data = kind switch
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
                $"'{value.Value?.GetType().FullName ?? "null"}'.")
        };
        return new NativeUnrealValue { Kind = encodedKind, Data = data };
    }

    private static NativePropertyKind GetPropertyKind(string unrealType, int size)
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
            "LazyObjectProperty" when size == LazyObjectStorageSize => NativePropertyKind.LazyObject,
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
            _ => throw new NotSupportedException($"Property type '{unrealType}' is not supported by RogueMod ABI 10.")
        };
    }

    private static uint EncodePropertyKind(
        NativePropertyKind kind,
        UnrealArrayDescriptor? arrayDescriptor = null,
        UnrealOptionalDescriptor? optionalDescriptor = null)
    {
        if (kind is not (NativePropertyKind.Array or NativePropertyKind.Optional))
        {
            return (uint)kind;
        }
        uint encodedValueKind;
        if (kind == NativePropertyKind.Array)
        {
            var descriptor = RequireArrayDescriptor(arrayDescriptor);
            var valueKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
            encodedValueKind = EncodePropertyKind(valueKind, descriptor.ElementArray);
        }
        else
        {
            var descriptor = RequireOptionalDescriptor(optionalDescriptor);
            var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
            encodedValueKind = EncodePropertyKind(valueKind);
        }
        if (encodedValueKind > MaximumEncodedValueKind)
        {
            throw new NotSupportedException("RogueMod ABI 10 supports at most three nested container levels.");
        }
        return (uint)kind | encodedValueKind << ContainerValueKindShift;
    }

    private static NativePropertyKind DecodePropertyKind(uint encodedKind) =>
        (NativePropertyKind)(encodedKind & PropertyKindMask);

    private static string ReadNativeString(NativeUnrealValue value)
    {
        if (value.Reserved > MaximumStringLength)
        {
            throw new InvalidOperationException(
                $"The native bridge returned an Unreal string longer than {MaximumStringLength} UTF-16 code units.");
        }
        if (value.Data == 0)
        {
            return value.Reserved == 0
                ? string.Empty
                : throw new InvalidOperationException("The native bridge returned a null Unreal string pointer with a non-zero length.");
        }
        return Marshal.PtrToStringUni(unchecked((nint)value.Data), checked((int)value.Reserved));
    }

    private static NativeUnrealValue WriteNativeArray(
        uint encodedKind,
        UnrealValue value,
        UnrealArrayDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealArrayValue arrayValue)
        {
            throw new InvalidCastException(
                $"Unreal TArray<{descriptor.ElementUnrealType}> requires an UnrealArrayValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!ArrayDescriptorsMatch(arrayValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal array element '{arrayValue.Descriptor.ElementUnrealType}' cannot be written as " +
                $"'{descriptor.ElementUnrealType}'.");
        }
        if (arrayValue.Elements.Count > MaximumArrayLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Unreal arrays cannot exceed {MaximumArrayLength} elements.");
        }
        if (arrayValue.Elements.Count == 0)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var elementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var encodedElementKind = EncodePropertyKind(elementKind, descriptor.ElementArray);
        var values = allocations.AddValues(arrayValue.Elements.Count);
        for (var index = 0; index < arrayValue.Elements.Count; index++)
        {
            values[index] = ToNativeValue(
                elementKind,
                encodedElementKind,
                arrayValue.Elements[index],
                descriptor.ElementStruct,
                descriptor.ElementArray,
                null,
                allocations);
        }
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = checked((uint)arrayValue.Elements.Count),
            Data = unchecked((ulong)values)
        };
    }

    private static UnrealArrayValue ReadNativeArray(NativeUnrealValue value, UnrealArrayDescriptor descriptor)
    {
        if (value.Reserved > MaximumArrayLength || (value.Data == 0 && value.Reserved != 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TArray<{descriptor.ElementUnrealType}> buffer.");
        }
        var expectedElementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var expectedEncodedElementKind = EncodePropertyKind(expectedElementKind, descriptor.ElementArray);
        var expectedEncodedKind = EncodePropertyKind(NativePropertyKind.Array, descriptor);
        if (value.Kind != expectedEncodedKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TArray<{descriptor.ElementUnrealType}> element kind.");
        }
        var elements = new UnrealValue[checked((int)value.Reserved)];
        var values = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < elements.Length; index++)
        {
            if (values[index].Kind != expectedEncodedElementKind)
            {
                throw new InvalidOperationException(
                    $"The native bridge returned a mismatched element at TArray index {index}.");
            }
            elements[index] = ToManagedValue(
                expectedElementKind,
                values[index],
                descriptor.ElementStruct,
                descriptor.ElementArray);
        }
        return new UnrealArrayValue(descriptor, elements);
    }

    private static NativeUnrealValue WriteNativeLazyObject(
        uint encodedKind,
        UnrealValue value,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealLazyObjectValue lazyValue)
        {
            throw new InvalidCastException(
                $"An Unreal lazy object reference requires an UnrealLazyObjectValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }

        var wire = new byte[LazyObjectWireSize];
        lazyValue.CopyNativeStorage().CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(LazyObjectStorageSize), lazyValue.CachedHandle.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(32), lazyValue.ObjectId.A);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(36), lazyValue.ObjectId.B);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(40), lazyValue.ObjectId.C);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(44), lazyValue.ObjectId.D);
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = LazyObjectWireSize,
            Data = unchecked((ulong)allocations.AddBytes(wire))
        };
    }

    private static UnrealLazyObjectValue ReadNativeLazyObject(NativeUnrealValue value)
    {
        if (value.Reserved != LazyObjectWireSize || value.Data == 0)
        {
            throw new InvalidOperationException("The native bridge returned an invalid lazy object reference buffer.");
        }

        var wire = new ReadOnlySpan<byte>((void*)value.Data, checked((int)value.Reserved));
        var storage = wire[..LazyObjectStorageSize].ToArray();
        var cachedHandle = new UnrealObjectHandle(BinaryPrimitives.ReadUInt64LittleEndian(wire[LazyObjectStorageSize..]));
        var objectId = new UnrealGuid(
            BinaryPrimitives.ReadUInt32LittleEndian(wire[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[44..]));
        return new UnrealLazyObjectValue(objectId, cachedHandle, storage);
    }

    private static NativeUnrealValue WriteNativeOptional(
        uint encodedKind,
        UnrealValue value,
        UnrealOptionalDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealOptionalValue optionalValue)
        {
            throw new InvalidCastException(
                $"Unreal TOptional<{descriptor.ValueUnrealType}> requires an UnrealOptionalValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!OptionalDescriptorsMatch(optionalValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal optional value '{optionalValue.Descriptor.ValueUnrealType}' cannot be written as " +
                $"'{descriptor.ValueUnrealType}'.");
        }
        if (!optionalValue.IsSet)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var encodedValueKind = EncodePropertyKind(valueKind);
        var nativeValue = allocations.AddValues(1);
        *nativeValue = ToNativeValue(
            valueKind,
            encodedValueKind,
            optionalValue.Value,
            descriptor.ValueStruct,
            null,
            null,
            allocations);
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = 1,
            Data = unchecked((ulong)nativeValue)
        };
    }

    private static UnrealOptionalValue ReadNativeOptional(
        NativeUnrealValue value,
        UnrealOptionalDescriptor descriptor)
    {
        var expectedValueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var expectedEncodedValueKind = EncodePropertyKind(expectedValueKind);
        var expectedEncodedKind = EncodePropertyKind(NativePropertyKind.Optional, optionalDescriptor: descriptor);
        if (value.Kind != expectedEncodedKind
            || value.Reserved > 1
            || (value.Reserved == 0 && value.Data != 0)
            || (value.Reserved == 1 && value.Data == 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TOptional<{descriptor.ValueUnrealType}> buffer.");
        }
        if (value.Reserved == 0)
        {
            return new UnrealOptionalValue(descriptor, false, UnrealValue.Null);
        }

        var nativeValue = *(NativeUnrealValue*)value.Data;
        if (nativeValue.Kind != expectedEncodedValueKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TOptional<{descriptor.ValueUnrealType}> value kind.");
        }
        return new UnrealOptionalValue(
            descriptor,
            true,
            ToManagedValue(expectedValueKind, nativeValue, descriptor.ValueStruct));
    }

    private static bool OptionalDescriptorsMatch(UnrealOptionalDescriptor left, UnrealOptionalDescriptor right) =>
        StringComparer.Ordinal.Equals(left.ValueUnrealType, right.ValueUnrealType)
        && left.ValueSize == right.ValueSize
        && left.ValueByteOffset == right.ValueByteOffset
        && left.ValueByteMask == right.ValueByteMask
        && left.ValueFieldMask == right.ValueFieldMask
        && StringComparer.Ordinal.Equals(left.ValueStruct?.Path, right.ValueStruct?.Path)
        && left.ValueStruct?.Size == right.ValueStruct?.Size;

    private static bool ArrayDescriptorsMatch(UnrealArrayDescriptor left, UnrealArrayDescriptor right) =>
        StringComparer.Ordinal.Equals(left.ElementUnrealType, right.ElementUnrealType)
        && left.ElementSize == right.ElementSize
        && left.ElementByteOffset == right.ElementByteOffset
        && left.ElementByteMask == right.ElementByteMask
        && left.ElementFieldMask == right.ElementFieldMask
        && StringComparer.Ordinal.Equals(left.ElementStruct?.Path, right.ElementStruct?.Path)
        && left.ElementStruct?.Size == right.ElementStruct?.Size
        && (left.ElementArray, right.ElementArray) switch
        {
            (null, null) => true,
            ({ } leftNested, { } rightNested) => ArrayDescriptorsMatch(leftNested, rightNested),
            _ => false
        };

    private static UnrealArrayDescriptor RequireArrayDescriptor(UnrealArrayDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TArray element descriptor.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.ElementUnrealType)
            || descriptor.ElementSize <= 0
            || descriptor.ElementSize > MaximumStructSize)
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TArray element descriptor.");
        }
        var kind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        if (kind == NativePropertyKind.Array)
        {
            if (descriptor.ElementArray is null)
            {
                throw new InvalidOperationException("The generated SDK did not provide a nested TArray descriptor.");
            }
            RequireArrayDescriptor(descriptor.ElementArray);
        }

        else if (descriptor.ElementArray is not null)
        {
            throw new InvalidOperationException("A non-array TArray element cannot have a nested array descriptor.");
        }
        if (kind == NativePropertyKind.Boolean
            && (descriptor.ElementByteOffset < 0
                || descriptor.ElementByteOffset >= descriptor.ElementSize
                || descriptor.ElementByteMask is < 0 or > byte.MaxValue
                || descriptor.ElementFieldMask is < 0 or > byte.MaxValue))
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TArray bool layout.");
        }
        if (kind == NativePropertyKind.Struct)
        {
            var structDescriptor = RequireStructDescriptor(descriptor.ElementStruct);
            if (structDescriptor.Size != descriptor.ElementSize)
            {
                throw new InvalidOperationException("The generated SDK provided a mismatched TArray struct size.");
            }
        }
        return descriptor;
    }

    private static UnrealOptionalDescriptor RequireOptionalDescriptor(UnrealOptionalDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TOptional value descriptor.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.ValueUnrealType)
            || descriptor.ValueSize <= 0
            || descriptor.ValueSize > MaximumStructSize)
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TOptional value descriptor.");
        }
        var kind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        if (kind is NativePropertyKind.Array or NativePropertyKind.Optional)
        {
            throw new NotSupportedException("Nested containers inside TOptional are not supported yet.");
        }
        if (kind == NativePropertyKind.Boolean
            && (descriptor.ValueByteOffset < 0
                || descriptor.ValueByteOffset >= descriptor.ValueSize
                || descriptor.ValueByteMask is < 0 or > byte.MaxValue
                || descriptor.ValueFieldMask is < 0 or > byte.MaxValue))
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TOptional bool layout.");
        }
        if (kind == NativePropertyKind.Struct)
        {
            var structDescriptor = RequireStructDescriptor(descriptor.ValueStruct);
            if (structDescriptor.Size != descriptor.ValueSize)
            {
                throw new InvalidOperationException("The generated SDK provided a mismatched TOptional struct size.");
            }
        }
        return descriptor;
    }

    private void EnsurePropertyCapabilities(
        NativePropertyKind kind,
        UnrealArrayDescriptor? arrayDescriptor,
        UnrealOptionalDescriptor? optionalDescriptor)
    {
        if (arrayDescriptor?.ElementArray is not null
            && (Capabilities & UnrealReflectionCapabilities.NestedArrays) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support nested TArray values.");
        }
        if (optionalDescriptor is not null
            && (Capabilities & UnrealReflectionCapabilities.OptionalValues) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support TOptional values.");
        }
        if (kind == NativePropertyKind.WeakObject
            && (Capabilities & UnrealReflectionCapabilities.WeakObjectReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support weak UObject references.");
        }
        if (kind == NativePropertyKind.LazyObject
            && (Capabilities & UnrealReflectionCapabilities.LazyObjectReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support lazy UObject references.");
        }
    }

    private static UnrealStructDescriptor RequireStructDescriptor(UnrealStructDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a POD struct descriptor.");
        }
        ValidateStructDescriptor(descriptor);
        return descriptor;
    }

    private static void ValidateStructDescriptor(UnrealStructDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Path)
            || descriptor.Size <= 0
            || descriptor.Size > MaximumStructSize
            || descriptor.Alignment <= 0
            || (descriptor.Alignment & (descriptor.Alignment - 1)) != 0)
        {
            throw new InvalidOperationException($"Unreal struct descriptor '{descriptor.Path}' has an invalid size or alignment.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name)
                || !names.Add(field.Name)
                || field.ArrayDimension != 1
                || field.Offset < 0
                || field.Size <= 0
                || (long)field.Offset + field.Size > descriptor.Size)
            {
                throw new InvalidOperationException($"Unreal struct descriptor '{descriptor.Path}:{field.Name}' is invalid.");
            }
            var kind = GetFieldKind(field.UnrealType, field.Size);
            if (kind == StructFieldKind.Boolean
                && (field.ByteOffset < 0
                    || field.ByteOffset >= field.Size
                    || field.ByteMask is < 0 or > byte.MaxValue
                    || field.FieldMask is < 0 or > byte.MaxValue))
            {
                throw new InvalidOperationException($"Bool field '{descriptor.Path}:{field.Name}' has an invalid bit layout.");
            }
            if (kind == StructFieldKind.Struct)
            {
                ValidateStructDescriptor(field.Struct
                    ?? throw new InvalidOperationException($"Nested Unreal struct field '{descriptor.Path}:{field.Name}' has no descriptor."));
                if (field.Struct.Size != field.Size)
                {
                    throw new InvalidOperationException($"Nested Unreal struct field '{descriptor.Path}:{field.Name}' has a size mismatch.");
                }
            }
        }
    }

    private static byte[] SerializeStruct(UnrealStructDescriptor descriptor, UnrealStructValue value)
    {
        ValidateStructDescriptor(descriptor);
        if (!StringComparer.Ordinal.Equals(descriptor.Path, value.Descriptor.Path))
        {
            throw new InvalidCastException($"Unreal struct '{value.Descriptor.Path}' cannot be written as '{descriptor.Path}'.");
        }
        var bytes = new byte[descriptor.Size];
        foreach (var field in descriptor.Fields)
        {
            if (!value.Fields.TryGetValue(field.Name, out var fieldValue))
            {
                throw new InvalidOperationException($"Unreal struct '{descriptor.Path}' is missing field '{field.Name}'.");
            }
            WriteStructField(bytes, field, fieldValue);
        }
        return bytes;
    }

    private static void WriteStructField(Span<byte> destination, UnrealStructFieldDescriptor field, UnrealValue value)
    {
        var kind = GetFieldKind(field.UnrealType, field.Size);
        var target = destination.Slice(field.Offset, field.Size);
        var managed = value.Value is Enum enumValue ? Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType())) : value.Value;
        switch (kind)
        {
            case StructFieldKind.Boolean:
            {
                if (managed is not bool boolean)
                {
                    throw InvalidStructFieldCast(field, value);
                }
                var byteOffset = field.ByteOffset;
                var mask = field.ByteMask == 0 ? 1 : field.ByteMask;
                if (byteOffset < 0 || byteOffset >= field.Size || mask is < 1 or > 255)
                {
                    throw new InvalidOperationException($"Bool field '{field.Name}' has an invalid bit layout.");
                }
                target[byteOffset] = boolean
                    ? (byte)(target[byteOffset] | mask)
                    : (byte)(target[byteOffset] & ~mask);
                break;
            }
            case StructFieldKind.Int8: target[0] = unchecked((byte)Convert.ToSByte(managed)); break;
            case StructFieldKind.UInt8: target[0] = Convert.ToByte(managed); break;
            case StructFieldKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(target, Convert.ToInt16(managed)); break;
            case StructFieldKind.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(target, Convert.ToUInt16(managed)); break;
            case StructFieldKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(target, Convert.ToInt32(managed)); break;
            case StructFieldKind.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(target, Convert.ToUInt32(managed)); break;
            case StructFieldKind.Int64: BinaryPrimitives.WriteInt64LittleEndian(target, Convert.ToInt64(managed)); break;
            case StructFieldKind.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(target, Convert.ToUInt64(managed)); break;
            case StructFieldKind.Float:
                BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(Convert.ToSingle(managed)));
                break;
            case StructFieldKind.Double:
                BinaryPrimitives.WriteInt64LittleEndian(target, BitConverter.DoubleToInt64Bits(Convert.ToDouble(managed)));
                break;
            case StructFieldKind.Struct:
            {
                var nestedDescriptor = field.Struct!;
                if (value.Value is not UnrealStructValue nestedValue)
                {
                    throw InvalidStructFieldCast(field, value);
                }
                SerializeStruct(nestedDescriptor, nestedValue).CopyTo(target);
                break;
            }
            default: throw new System.Diagnostics.UnreachableException();
        }
    }

    private static UnrealStructValue ReadNativeStruct(NativeUnrealValue value, UnrealStructDescriptor descriptor)
    {
        if (value.Data == 0 || value.Reserved != descriptor.Size)
        {
            throw new InvalidOperationException(
                $"The native bridge returned {value.Reserved} bytes for Unreal struct '{descriptor.Path}', expected {descriptor.Size}.");
        }
        var bytes = new byte[descriptor.Size];
        Marshal.Copy(unchecked((nint)value.Data), bytes, 0, bytes.Length);
        return DeserializeStruct(descriptor, bytes);
    }

    private static UnrealStructValue DeserializeStruct(UnrealStructDescriptor descriptor, ReadOnlySpan<byte> source)
    {
        ValidateStructDescriptor(descriptor);
        if (source.Length != descriptor.Size)
        {
            throw new InvalidOperationException($"Unreal struct '{descriptor.Path}' buffer size does not match its descriptor.");
        }
        var fields = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            var fieldBytes = source.Slice(field.Offset, field.Size);
            object fieldValue = GetFieldKind(field.UnrealType, field.Size) switch
            {
                StructFieldKind.Boolean => (fieldBytes[field.ByteOffset] & (field.ByteMask == 0 ? 1 : field.ByteMask)) != 0,
                StructFieldKind.Int8 => unchecked((sbyte)fieldBytes[0]),
                StructFieldKind.UInt8 => fieldBytes[0],
                StructFieldKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(fieldBytes),
                StructFieldKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(fieldBytes),
                StructFieldKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(fieldBytes),
                StructFieldKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(fieldBytes),
                StructFieldKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(fieldBytes),
                StructFieldKind.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(fieldBytes),
                StructFieldKind.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(fieldBytes)),
                StructFieldKind.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(fieldBytes)),
                StructFieldKind.Struct => DeserializeStruct(field.Struct!, fieldBytes),
                _ => throw new System.Diagnostics.UnreachableException()
            };
            fields.Add(field.Name, UnrealValue.From(fieldValue));
        }
        return new UnrealStructValue(descriptor, fields);
    }

    private static StructFieldKind GetFieldKind(string unrealType, int size)
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

    private static InvalidCastException InvalidStructFieldCast(UnrealStructFieldDescriptor field, UnrealValue value) =>
        new($"Unreal struct field '{field.Name}' cannot be written from '{value.Value?.GetType().FullName ?? "null"}'.");

    private static void FreeNativeOutputAllocations(
        IReadOnlyList<UnrealParameterDescriptor> descriptors,
        NativeUnrealParameter[] parameters,
        NativeAllocations inputAllocations)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (!descriptors[index].IsOutput && !descriptors[index].IsReturn)
            {
                continue;
            }
            FreeNativeAllocation(parameters[index].Kind, parameters[index].Value, inputAllocations);
        }
    }

    private static void FreeNativeAllocation(
        uint encodedKind,
        NativeUnrealValue value,
        NativeAllocations? inputAllocations)
    {
        var kind = DecodePropertyKind(encodedKind);
        if (value.Data == 0 || inputAllocations?.Contains(value.Data) == true)
        {
            return;
        }
        if (kind == NativePropertyKind.Array)
        {
            if (value.Reserved <= MaximumArrayLength)
            {
                var values = (NativeUnrealValue*)value.Data;
                for (var index = 0U; index < value.Reserved; index++)
                {
                    FreeNativeAllocation(values[index].Kind, values[index], inputAllocations);
                }
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind == NativePropertyKind.Optional)
        {
            if (value.Reserved == 1)
            {
                var nested = *(NativeUnrealValue*)value.Data;
                FreeNativeAllocation(nested.Kind, nested, inputAllocations);
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind is not (NativePropertyKind.String
            or NativePropertyKind.Name
            or NativePropertyKind.Text
            or NativePropertyKind.Struct
            or NativePropertyKind.LazyObject))
        {
            return;
        }
        Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
    }

    private sealed class NativeAllocations : IDisposable
    {
        private readonly List<nint> allocations = [];

        internal nint AddString(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUni(value);
            allocations.Add(pointer);
            return pointer;
        }

        internal nint AddBytes(byte[] value)
        {
            var pointer = Marshal.AllocCoTaskMem(value.Length);
            Marshal.Copy(value, 0, pointer, value.Length);
            allocations.Add(pointer);
            return pointer;
        }

        internal NativeUnrealValue* AddValues(int count)
        {
            var bytes = checked(count * sizeof(NativeUnrealValue));
            var pointer = Marshal.AllocCoTaskMem(bytes);
            new Span<byte>((void*)pointer, bytes).Clear();
            allocations.Add(pointer);
            return (NativeUnrealValue*)pointer;
        }

        internal bool Contains(ulong pointer) =>
            allocations.Contains(unchecked((nint)pointer));

        public void Dispose()
        {
            foreach (var pointer in allocations)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
            allocations.Clear();
        }
    }

    internal struct NativeUnrealValue
    {
        internal uint Kind;
        internal uint Reserved;
        internal ulong Data;
    }

    internal struct NativeUnrealParameter
    {
        internal uint Kind;
        internal uint Flags;
        internal int Offset;
        internal int Size;
        internal uint ArrayDimension;
        internal uint BoolLayout;
        internal NativeUnrealValue Value;
    }

    [Flags]
    private enum NativeParameterFlags : uint
    {
        Input = 1,
        Output = 2,
        Return = 4
    }

    private enum NativePropertyKind : uint
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

    private enum StructFieldKind
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
}
