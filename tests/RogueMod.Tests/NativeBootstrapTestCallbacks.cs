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
    private const uint IntArrayKind = 17U | (6U << 8);

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
    internal static int UnrealIsValid(ulong handle) => handle == 0x0000_0007_0000_002A ? 1 : 0;

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
    internal static uint UnrealGetCapabilities() => (1U << 0) | (1U << 1) | (1U << 2) | (1U << 3) | (1U << 4);

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
