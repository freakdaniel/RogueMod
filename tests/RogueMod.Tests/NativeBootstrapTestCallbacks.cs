using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RogueMod.Tests.Native;

internal static unsafe class NativeBootstrapTestCallbacks
{
    internal static readonly List<string> Messages = [];
    internal static bool PropertyWritten;
    internal static bool StringPropertyWritten;
    internal static bool StructPropertyWritten;
    internal static bool TextPropertyWritten;
    internal static bool ArrayPropertyWritten;
    internal static bool NestedArrayPropertyWritten;
    internal static bool OptionalPropertyWritten;
    internal static bool OptionalUnsetPropertyWritten;
    internal static bool WeakPropertyWritten;
    internal static bool WeakNullPropertyWritten;
    internal static bool LazyPropertyWritten;
    internal static bool LazyNullPropertyWritten;
    internal static bool SoftPropertyWritten;
    internal static bool ObjectCreated;
    internal static bool ActorSpawned;
    internal static delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealParameter*, int> RegisteredHookCallback;
    internal static ulong RegisteredHookContext;
    internal static int RegisteredHookPhase;
    internal static int RegisteredHookPriority;
    internal static ulong RegisteredHookInstanceFilter;
    internal static NativeUnrealParameter[] RegisteredHookParameters = [];
    private const uint IntArrayKind = 17U | (6U << 8);
    private const uint NestedIntArrayKind = 17U | (17U << 8) | (6U << 16);
    private const uint OptionalIntKind = 18U | (6U << 8);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void CaptureLog(int level, char* message)
    {
        Messages.Add(new string(message));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealIsAvailable() => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static ulong UnrealFindFirstOf(char* className) => 0x0000_0007_0000_002A;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealFindAllOf(char* className, ulong* handles, uint capacity, uint* required)
    {
        if (required == null)
        {
            return -2;
        }
        *required = 1;
        if (handles == null || capacity < 1)
        {
            return 1;
        }
        handles[0] = 0x0000_0007_0000_002A;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealIsValid(ulong handle) =>
        handle is 0x0000_0007_0000_002A or 0x0000_0007_0000_002B or 0x0000_0007_0000_002C ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static ulong UnrealGetClass(ulong handle) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealGetPathName(ulong handle, char* buffer, uint capacity, uint* required)
    {
        const string path = "/Test/PlayerController";
        if (required == null)
        {
            return -2;
        }
        *required = (uint)path.Length + 1U;
        if (buffer == null || capacity < *required)
        {
            return 1;
        }
        for (var index = 0; index < path.Length; index++)
        {
            buffer[index] = path[index];
        }
        buffer[path.Length] = '\0';
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static uint UnrealGetCapabilities() =>
        (1U << 0) | (1U << 1) | (1U << 2) | (1U << 3) | (1U << 4) | (1U << 5) | (1U << 6) | (1U << 7) | (1U << 8) | (1U << 9)
        | (1U << 10) | (1U << 11) | (1U << 12);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static ulong UnrealCreateObject(ulong classHandle, ulong outerHandle, char* objectName)
    {
        ObjectCreated = classHandle == 0x0000_0007_0000_002A
            && outerHandle == 0x0000_0007_0000_002A
            && objectName != null
            && new string(objectName) == "ManagedAbiObject";
        return ObjectCreated ? 0x0000_0007_0000_002BUL : 0UL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static ulong UnrealSpawnActor(
        ulong contextObjectHandle,
        ulong classHandle,
        float* location,
        float* rotation)
    {
        ActorSpawned = contextObjectHandle == 0x0000_0007_0000_002A
            && classHandle == 0x0000_0007_0000_002A
            && location != null
            && rotation != null
            && location[0] == 1.0f && location[1] == 2.0f && location[2] == 3.0f
            && rotation[0] == 4.0f && rotation[1] == 5.0f && rotation[2] == 6.0f;
        return ActorSpawned ? 0x0000_0007_0000_002CUL : 0UL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealRegisterHook(
        char* functionPath,
        int phase,
        int priority,
        ulong instanceFilter,
        uint parameterCount,
        NativeUnrealParameter* parameters,
        delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealParameter*, int> callback,
        ulong context,
        ulong* token)
    {
        if (functionPath == null || callback == null || token == null || phase is not (1 or 2))
        {
            return -2;
        }
        RegisteredHookCallback = callback;
        RegisteredHookContext = context;
        RegisteredHookPhase = phase;
        RegisteredHookPriority = priority;
        RegisteredHookInstanceFilter = instanceFilter;
        RegisteredHookParameters = new NativeUnrealParameter[parameterCount];
        for (var index = 0; index < parameterCount; index++)
        {
            RegisteredHookParameters[index] = parameters[index];
        }
        *token = context == 0 ? 1UL : context;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealUnregisterHook(ulong token)
    {
        if (token == 0)
        {
            return -2;
        }
        RegisteredHookCallback = null;
        RegisteredHookContext = 0;
        RegisteredHookPhase = 0;
        RegisteredHookPriority = 0;
        RegisteredHookInstanceFilter = 0;
        RegisteredHookParameters = [];
        return 0;
    }

    internal static int DispatchRegisteredHook(ulong objectHandle, params ulong[] values)
    {
        if (RegisteredHookCallback == null || values.Length != RegisteredHookParameters.Length)
        {
            return -1;
        }
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = RegisteredHookParameters[index];
            var value = parameter.Value;
            value.Data = values[index];
            parameter.Value = value;
            RegisteredHookParameters[index] = parameter;
        }
        fixed (NativeUnrealParameter* parameters = RegisteredHookParameters)
        {
            return RegisteredHookCallback(
                RegisteredHookContext,
                objectHandle,
                RegisteredHookPhase,
                (uint)RegisteredHookParameters.Length,
                parameters);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealInvokeZeroParameter(ulong handle, char* functionName) =>
        handle == 0x0000_0007_0000_002A && new string(functionName) == "Pause" ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealReadProperty(ulong handle, char* propertyName, uint propertyKind, NativeUnrealValue* value)
    {
        if (handle != 0x0000_0007_0000_002A || value == null)
        {
            return -1;
        }
        if (new string(propertyName) == "PlayerName" && propertyKind == 13)
        {
            *value = AllocateStringValue(propertyKind, "Rogue");
            return 0;
        }
        if (new string(propertyName) == "SpawnLocation" && propertyKind == 15)
        {
            *value = AllocateVectorValue(7.0, 8.0, 9.0);
            return 0;
        }
        if (new string(propertyName) == "DisplayText" && propertyKind == 16)
        {
            *value = AllocateStringValue(propertyKind, "Display Text");
            return 0;
        }
        if (new string(propertyName) == "Scores" && propertyKind == IntArrayKind)
        {
            *value = AllocateIntArrayValue([7, 8, 9]);
            return 0;
        }
        if (new string(propertyName) == "ScoreGroups" && propertyKind == NestedIntArrayKind)
        {
            *value = AllocateNestedIntArrayValue([[7, 8], [9]]);
            return 0;
        }
        if (new string(propertyName) == "OptionalScore" && propertyKind == OptionalIntKind)
        {
            *value = AllocateOptionalIntValue(11);
            return 0;
        }
        if (new string(propertyName) == "OptionalUnsetScore" && propertyKind == OptionalIntKind)
        {
            *value = new NativeUnrealValue { Kind = OptionalIntKind };
            return 0;
        }
        if (new string(propertyName) == "WeakController" && propertyKind == 19)
        {
            *value = new NativeUnrealValue { Kind = 19, Data = 0x0000_0007_0000_002A };
            return 0;
        }
        if (new string(propertyName) == "LazyController" && propertyKind == 20)
        {
            *value = AllocateLazyObjectValue();
            return 0;
        }
        if (new string(propertyName) == "SoftController" && propertyKind == 21)
        {
            *value = AllocateSoftObjectValue();
            return 0;
        }
        if (new string(propertyName) != "bShouldPerformFullTickWhenPaused")
        {
            return -1;
        }
        value->Kind = propertyKind;
        value->Reserved = 0;
        value->Data = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealWriteProperty(ulong handle, char* propertyName, uint propertyKind, NativeUnrealValue* value)
    {
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "PlayerName"
            && propertyKind == 13
            && value != null)
        {
            StringPropertyWritten = ReadStringValue(*value) == "Rogue";
            return StringPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "SpawnLocation"
            && propertyKind == 15
            && value != null)
        {
            var vector = ReadVectorValue(*value);
            StructPropertyWritten = vector == (7.0, 8.0, 9.0);
            return StructPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "DisplayText"
            && propertyKind == 16
            && value != null)
        {
            TextPropertyWritten = ReadStringValue(*value) == "Display Text";
            return TextPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "Scores"
            && propertyKind == IntArrayKind
            && value != null)
        {
            ArrayPropertyWritten = ReadIntArrayValue(*value).SequenceEqual([7, 8, 9]);
            return ArrayPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "ScoreGroups"
            && propertyKind == NestedIntArrayKind
            && value != null)
        {
            NestedArrayPropertyWritten = NestedArraysEqual(ReadNestedIntArrayValue(*value), [[7, 8], [9]]);
            return NestedArrayPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "OptionalScore"
            && propertyKind == OptionalIntKind
            && value != null)
        {
            OptionalPropertyWritten = ReadOptionalIntValue(*value) == 11;
            return OptionalPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "OptionalUnsetScore"
            && propertyKind == OptionalIntKind
            && value != null)
        {
            OptionalUnsetPropertyWritten = value->Kind == OptionalIntKind
                && value->Reserved == 0
                && value->Data == 0;
            return OptionalUnsetPropertyWritten ? 0 : -1;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "WeakController"
            && propertyKind == 19
            && value != null
            && value->Kind == 19)
        {
            WeakPropertyWritten |= value->Data == 0x0000_0007_0000_002A;
            WeakNullPropertyWritten |= value->Data == 0;
            return 0;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "LazyController"
            && propertyKind == 20
            && value != null
            && value->Kind == 20)
        {
            var wire = ReadLazyObjectWire(*value);
            LazyPropertyWritten |= wire.SequenceEqual(CreateLazyObjectWire());
            LazyNullPropertyWritten |= wire.All(static item => item == 0);
            return 0;
        }
        if (handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "SoftController"
            && propertyKind == 21
            && value != null
            && value->Kind == 21)
        {
            var (path, cachedHandle, storage) = ReadSoftObjectWire(*value);
            SoftPropertyWritten = path == "/Game/Test/ManagedAbi.ManagedAbi"
                && cachedHandle == 0x0000_0007_0000_002A
                && storage.SequenceEqual(CreateSoftObjectStorage());
            return SoftPropertyWritten ? 0 : -1;
        }
        PropertyWritten = handle == 0x0000_0007_0000_002A
            && new string(propertyName) == "bShouldPerformFullTickWhenPaused"
            && propertyKind == 1
            && value != null
            && value->Kind == propertyKind
            && value->Data == 1;
        return PropertyWritten ? 0 : -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int UnrealInvoke(ulong handle, char* functionName, uint parameterCount, NativeUnrealParameter* parameters)
    {
        if (handle != 0x0000_0007_0000_002A)
        {
            return -1;
        }
        var name = new string(functionName);
        if (name == "Pause")
        {
            return parameterCount == 0 ? 0 : -4;
        }
        if (name == "TestStringMarshalling")
        {
            if (parameterCount != 3 || parameters == null
                || parameters[0].Kind != 14 || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 8
                || parameters[1].Kind != 13 || parameters[1].Flags != 2 || parameters[1].Offset != 8 || parameters[1].Size != 16
                || parameters[2].Kind != 14 || parameters[2].Flags != 6 || parameters[2].Offset != 24 || parameters[2].Size != 8
                || ReadStringValue(parameters[0].Value) != "InputName")
            {
                return -5;
            }
            parameters[1].Value = AllocateStringValue(13, "Output String");
            parameters[2].Value = AllocateStringValue(14, "ReturnName");
            return 0;
        }
        if (name == "TestStructMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != 15 || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 24
                || parameters[0].Value.Reserved != 24 || parameters[0].Value.Data == 0
                || parameters[1].Kind != 15 || parameters[1].Flags != 6 || parameters[1].Offset != 24 || parameters[1].Size != 24)
            {
                return -5;
            }
            var input = new byte[24];
            Marshal.Copy(unchecked((nint)parameters[0].Value.Data), input, 0, input.Length);
            if (BitConverter.ToDouble(input, 0) != 1.0
                || BitConverter.ToDouble(input, 8) != 2.0
                || BitConverter.ToDouble(input, 16) != 3.0)
            {
                return -5;
            }
            var output = new byte[24];
            BitConverter.GetBytes(4.0).CopyTo(output, 0);
            BitConverter.GetBytes(5.0).CopyTo(output, 8);
            BitConverter.GetBytes(6.0).CopyTo(output, 16);
            var pointer = Marshal.AllocCoTaskMem(output.Length);
            Marshal.Copy(output, 0, pointer, output.Length);
            parameters[1].Value = new NativeUnrealValue { Kind = 15, Reserved = 24, Data = unchecked((ulong)pointer) };
            return 0;
        }
        if (name == "TestTextMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != 16 || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 16
                || parameters[1].Kind != 16 || parameters[1].Flags != 6 || parameters[1].Offset != 16 || parameters[1].Size != 16
                || ReadStringValue(parameters[0].Value) != "Input Text")
            {
                return -5;
            }
            parameters[1].Value = AllocateStringValue(16, "Output Text");
            return 0;
        }
        if (name == "TestArrayMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != IntArrayKind || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 16
                || parameters[1].Kind != IntArrayKind || parameters[1].Flags != 6 || parameters[1].Offset != 16 || parameters[1].Size != 16
                || !ReadIntArrayValue(parameters[0].Value).SequenceEqual([1, 2, 3]))
            {
                return -5;
            }
            parameters[1].Value = AllocateIntArrayValue([4, 5, 6]);
            return 0;
        }
        if (name == "TestNestedArrayMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != NestedIntArrayKind || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 16
                || parameters[1].Kind != NestedIntArrayKind || parameters[1].Flags != 6 || parameters[1].Offset != 16 || parameters[1].Size != 16
                || !NestedArraysEqual(ReadNestedIntArrayValue(parameters[0].Value), [[1, 2], [3]]))
            {
                return -5;
            }
            parameters[1].Value = AllocateNestedIntArrayValue([[4], [5, 6]]);
            return 0;
        }
        if (name == "TestOptionalMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != OptionalIntKind || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 8
                || parameters[1].Kind != OptionalIntKind || parameters[1].Flags != 6 || parameters[1].Offset != 8 || parameters[1].Size != 8
                || ReadOptionalIntValue(parameters[0].Value) != 7)
            {
                return -5;
            }
            parameters[1].Value = AllocateOptionalIntValue(13);
            return 0;
        }
        if (name == "TestWeakObjectMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != 19 || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 8
                || parameters[0].Value.Data != 0x0000_0007_0000_002A
                || parameters[1].Kind != 19 || parameters[1].Flags != 6 || parameters[1].Offset != 8 || parameters[1].Size != 8)
            {
                return -5;
            }
            parameters[1].Value = new NativeUnrealValue { Kind = 19, Data = 0x0000_0007_0000_002A };
            return 0;
        }
        if (name == "TestLazyObjectMarshalling")
        {
            if (parameterCount != 2 || parameters == null
                || parameters[0].Kind != 20 || parameters[0].Flags != 1 || parameters[0].Offset != 0 || parameters[0].Size != 24
                || !ReadLazyObjectWire(parameters[0].Value).SequenceEqual(CreateLazyObjectWire())
                || parameters[1].Kind != 20 || parameters[1].Flags != 6 || parameters[1].Offset != 24 || parameters[1].Size != 24)
            {
                return -5;
            }
            parameters[1].Value = AllocateLazyObjectValue();
            return 0;
        }
        if (name != "TestMarshalling" || parameterCount != 3 || parameters == null)
        {
            return -3;
        }

        var expectedFloat = unchecked((uint)BitConverter.SingleToInt32Bits(1.25f));
        if (parameters[0].Kind != 10 || parameters[0].Flags != 1 || parameters[0].Offset != 0
            || parameters[0].Size != 4 || parameters[0].Value.Data != expectedFloat
            || parameters[1].Kind != 6 || parameters[1].Flags != 2 || parameters[1].Offset != 4
            || parameters[2].Kind != 1 || parameters[2].Flags != 6 || parameters[2].Offset != 8)
        {
            return -5;
        }

        parameters[1].Value = new NativeUnrealValue { Kind = 6, Data = 42 };
        parameters[2].Value = new NativeUnrealValue { Kind = 1, Data = 1 };
        return 0;
    }

    private static NativeUnrealValue AllocateStringValue(uint kind, string value)
    {
        var pointer = Marshal.StringToCoTaskMemUni(value);
        return new NativeUnrealValue
        {
            Kind = kind,
            Reserved = (uint)value.Length,
            Data = unchecked((ulong)pointer)
        };
    }

    private static string ReadStringValue(NativeUnrealValue value) =>
        value.Data == 0
            ? string.Empty
            : Marshal.PtrToStringUni(unchecked((nint)value.Data), checked((int)value.Reserved));

    private static NativeUnrealValue AllocateVectorValue(double x, double y, double z)
    {
        var bytes = new byte[24];
        BitConverter.GetBytes(x).CopyTo(bytes, 0);
        BitConverter.GetBytes(y).CopyTo(bytes, 8);
        BitConverter.GetBytes(z).CopyTo(bytes, 16);
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new NativeUnrealValue
        {
            Kind = 15,
            Reserved = (uint)bytes.Length,
            Data = unchecked((ulong)pointer)
        };
    }

    private static (double X, double Y, double Z) ReadVectorValue(NativeUnrealValue value)
    {
        if (value.Kind != 15 || value.Reserved != 24 || value.Data == 0)
        {
            return default;
        }
        var bytes = new byte[24];
        Marshal.Copy(unchecked((nint)value.Data), bytes, 0, bytes.Length);
        return (BitConverter.ToDouble(bytes, 0), BitConverter.ToDouble(bytes, 8), BitConverter.ToDouble(bytes, 16));
    }

    private static NativeUnrealValue AllocateIntArrayValue(IReadOnlyList<int> values)
    {
        var bytes = checked(values.Count * sizeof(NativeUnrealValue));
        var pointer = Marshal.AllocCoTaskMem(bytes);
        var elements = (NativeUnrealValue*)pointer;
        for (var index = 0; index < values.Count; index++)
        {
            elements[index] = new NativeUnrealValue { Kind = 6, Data = unchecked((uint)values[index]) };
        }
        return new NativeUnrealValue
        {
            Kind = IntArrayKind,
            Reserved = checked((uint)values.Count),
            Data = unchecked((ulong)pointer)
        };
    }

    private static IReadOnlyList<int> ReadIntArrayValue(NativeUnrealValue value)
    {
        if (value.Kind != IntArrayKind || (value.Reserved != 0 && value.Data == 0))
        {
            return [];
        }
        var result = new int[checked((int)value.Reserved)];
        var elements = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < result.Length; index++)
        {
            if (elements[index].Kind != 6)
            {
                return [];
            }
            result[index] = unchecked((int)elements[index].Data);
        }
        return result;
    }

    private static NativeUnrealValue AllocateLazyObjectValue()
    {
        var wire = CreateLazyObjectWire();
        var pointer = Marshal.AllocCoTaskMem(wire.Length);
        Marshal.Copy(wire, 0, pointer, wire.Length);
        return new NativeUnrealValue { Kind = 20, Reserved = (uint)wire.Length, Data = unchecked((ulong)pointer) };
    }

    private static byte[] CreateLazyObjectWire()
    {
        var wire = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(wire, 42);
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(8), 0x1111_1111);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(12), 0x2222_2222);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(16), 0x3333_3333);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(20), 0x4444_4444);
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(24), 0x0000_0007_0000_002A);
        wire.AsSpan(8, 16).CopyTo(wire.AsSpan(32));
        return wire;
    }

    private static byte[] ReadLazyObjectWire(NativeUnrealValue value)
    {
        if (value.Kind != 20 || value.Reserved != 48 || value.Data == 0)
        {
            return [];
        }
        var wire = new byte[48];
        Marshal.Copy(unchecked((nint)value.Data), wire, 0, wire.Length);
        return wire;
    }

    private static NativeUnrealValue AllocateSoftObjectValue()
    {
        const string path = "/Game/Test/ManagedAbi.ManagedAbi";
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var wirePointer = Marshal.AllocCoTaskMem(56);
        var storage = CreateSoftObjectStorage();
        Marshal.Copy(storage, 0, wirePointer, storage.Length);
        Marshal.WriteInt64(wirePointer, 40, unchecked((long)0x0000_0007_0000_002A));
        Marshal.WriteIntPtr(wirePointer, 48, pathPointer);
        return new NativeUnrealValue { Kind = 21, Reserved = 56, Data = unchecked((ulong)wirePointer) };
    }

    private static (string Path, ulong CachedHandle, byte[] Storage) ReadSoftObjectWire(NativeUnrealValue value)
    {
        if (value.Kind != 21 || value.Reserved != 56 || value.Data == 0)
        {
            return (string.Empty, 0, []);
        }
        var wirePointer = unchecked((nint)value.Data);
        var storage = new byte[40];
        Marshal.Copy(wirePointer, storage, 0, storage.Length);
        var cachedHandle = unchecked((ulong)Marshal.ReadInt64(wirePointer, 40));
        var pathPointer = Marshal.ReadIntPtr(wirePointer, 48);
        var path = pathPointer == nint.Zero ? string.Empty : Marshal.PtrToStringUni(pathPointer) ?? string.Empty;
        return (path, cachedHandle, storage);
    }

    private static byte[] CreateSoftObjectStorage() =>
        Enumerable.Range(0, 40).Select(static value => checked((byte)(value + 1))).ToArray();

    private static NativeUnrealValue AllocateOptionalIntValue(int value)
    {
        var pointer = Marshal.AllocCoTaskMem(sizeof(NativeUnrealValue));
        *(NativeUnrealValue*)pointer = new NativeUnrealValue { Kind = 6, Data = unchecked((uint)value) };
        return new NativeUnrealValue
        {
            Kind = OptionalIntKind,
            Reserved = 1,
            Data = unchecked((ulong)pointer)
        };
    }

    private static int? ReadOptionalIntValue(NativeUnrealValue value)
    {
        if (value.Kind != OptionalIntKind || value.Reserved > 1
            || (value.Reserved == 0 && value.Data != 0)
            || (value.Reserved == 1 && value.Data == 0))
        {
            return null;
        }
        if (value.Reserved == 0)
        {
            return null;
        }
        var nested = *(NativeUnrealValue*)value.Data;
        return nested.Kind == 6 ? unchecked((int)nested.Data) : null;
    }

    private static NativeUnrealValue AllocateNestedIntArrayValue(IReadOnlyList<IReadOnlyList<int>> values)
    {
        var bytes = checked(values.Count * sizeof(NativeUnrealValue));
        var pointer = Marshal.AllocCoTaskMem(bytes);
        var elements = (NativeUnrealValue*)pointer;
        for (var index = 0; index < values.Count; index++)
        {
            elements[index] = AllocateIntArrayValue(values[index]);
        }
        return new NativeUnrealValue
        {
            Kind = NestedIntArrayKind,
            Reserved = checked((uint)values.Count),
            Data = unchecked((ulong)pointer)
        };
    }

    private static IReadOnlyList<IReadOnlyList<int>> ReadNestedIntArrayValue(NativeUnrealValue value)
    {
        if (value.Kind != NestedIntArrayKind || (value.Reserved != 0 && value.Data == 0))
        {
            return [];
        }
        var result = new IReadOnlyList<int>[checked((int)value.Reserved)];
        var elements = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = ReadIntArrayValue(elements[index]);
        }
        return result;
    }

    private static bool NestedArraysEqual(
        IReadOnlyList<IReadOnlyList<int>> left,
        IReadOnlyList<IReadOnlyList<int>> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    [StructLayout(LayoutKind.Sequential)]
    internal struct HostApi
    {
        internal uint Size;
        internal uint AbiVersion;
        internal delegate* unmanaged[Cdecl]<int, char*, void> Log;
        internal char* ModRoot;
        internal char* GameProfileId;
        internal delegate* unmanaged[Cdecl]<int> UnrealIsAvailable;
        internal delegate* unmanaged[Cdecl]<char*, ulong> UnrealFindFirstOf;
        internal delegate* unmanaged[Cdecl]<ulong, int> UnrealIsValid;
        internal delegate* unmanaged[Cdecl]<ulong, ulong> UnrealGetClass;
        internal delegate* unmanaged[Cdecl]<ulong, char*, uint, uint*, int> UnrealGetPathName;
        internal delegate* unmanaged[Cdecl]<uint> UnrealGetCapabilities;
        internal delegate* unmanaged[Cdecl]<ulong, char*, int> UnrealInvokeZeroParameter;
        internal delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealValue*, int> UnrealReadProperty;
        internal delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealValue*, int> UnrealWriteProperty;
        internal delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealParameter*, int> UnrealInvoke;
        internal char* GameModsRoot;
        internal delegate* unmanaged[Cdecl]<char*, ulong*, uint, uint*, int> UnrealFindAllOf;
        internal delegate* unmanaged[Cdecl]<char*, int, int, ulong, uint, NativeUnrealParameter*, delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealParameter*, int>, ulong, ulong*, int> UnrealRegisterHook;
        internal delegate* unmanaged[Cdecl]<ulong, int> UnrealUnregisterHook;
        internal delegate* unmanaged[Cdecl]<ulong, ulong, char*, ulong> UnrealCreateObject;
        internal delegate* unmanaged[Cdecl]<ulong, ulong, float*, float*, ulong> UnrealSpawnActor;
    }

    internal struct NativeUnrealValue
    {
        internal uint Kind;
        internal uint Reserved;
        internal ulong Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeUnrealParameter
    {
        internal uint Kind { get; set; }
        internal uint Flags { get; set; }
        internal int Offset { get; set; }
        internal int Size { get; set; }
        internal uint ArrayDimension { get; set; }
        internal uint BoolLayout { get; set; }
        internal NativeUnrealValue Value { get; set; }
    }
}
