using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RogueMod.Runtime;

namespace RogueMod.Tests.Native;

internal static unsafe class NativeHookTestCallbacks
{
    internal static delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealReflection.NativeUnrealParameter*, int> Callback;
    internal static ulong Context;
    internal static int Phase;
    internal static int Priority;
    internal static ulong InstanceFilter;
    internal static NativeUnrealReflection.NativeUnrealParameter[] Parameters = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int Register(
        char* functionPath,
        int phase,
        int priority,
        ulong instanceFilter,
        uint parameterCount,
        NativeUnrealReflection.NativeUnrealParameter* parameters,
        delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealReflection.NativeUnrealParameter*, int> callback,
        ulong context,
        ulong* token)
    {
        if (functionPath == null || callback == null || token == null || phase is not (1 or 2))
        {
            return -2;
        }
        Callback = callback;
        Context = context;
        Phase = phase;
        Priority = priority;
        InstanceFilter = instanceFilter;
        Parameters = new NativeUnrealReflection.NativeUnrealParameter[parameterCount];
        for (var index = 0; index < parameterCount; index++)
        {
            Parameters[index] = parameters[index];
        }
        *token = context == 0 ? 1UL : context;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int Unregister(ulong token)
    {
        if (token == 0)
        {
            return -2;
        }
        Callback = null;
        Context = 0;
        Phase = 0;
        Priority = 0;
        InstanceFilter = 0;
        Parameters = [];
        return 0;
    }

    internal static int Dispatch(ulong objectHandle, params ulong[] values)
    {
        if (Callback == null || values.Length != Parameters.Length)
        {
            return -1;
        }
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = Parameters[index];
            var value = parameter.Value;
            value.Data = values[index];
            parameter.Value = value;
            Parameters[index] = parameter;
        }
        fixed (NativeUnrealReflection.NativeUnrealParameter* parameters = Parameters)
        {
            return Callback(Context, objectHandle, Phase, (uint)Parameters.Length, parameters);
        }
    }

    internal static int DispatchNative(ulong objectHandle, params NativeUnrealReflection.NativeUnrealValue[] values)
    {
        if (Callback == null || values.Length != Parameters.Length)
        {
            return -1;
        }
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = Parameters[index];
            parameter.Value = values[index];
            Parameters[index] = parameter;
        }
        fixed (NativeUnrealReflection.NativeUnrealParameter* parameters = Parameters)
        {
            return Callback(Context, objectHandle, Phase, (uint)Parameters.Length, parameters);
        }
    }

    internal static void ReleaseTransportedValues()
    {
        for (var index = 0; index < Parameters.Length; index++)
        {
            var parameter = Parameters[index];
            Release(parameter.Value);
            parameter.Value = default;
            Parameters[index] = parameter;
        }

        static void Release(NativeUnrealReflection.NativeUnrealValue value)
        {
            var kind = value.Kind & 0xffU;
            if (value.Data == 0)
            {
                return;
            }
            if (kind == 17)
            {
                var elements = (NativeUnrealReflection.NativeUnrealValue*)value.Data;
                for (var index = 0U; index < value.Reserved; index++)
                {
                    Release(elements[index]);
                }
            }
            else if (kind == 18 && value.Reserved == 1)
            {
                Release(*(NativeUnrealReflection.NativeUnrealValue*)value.Data);
            }
            else if (kind is not (13 or 14 or 15 or 16 or 20))
            {
                return;
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
        }
    }
}
