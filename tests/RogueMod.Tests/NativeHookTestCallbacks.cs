using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RogueMod.Runtime;

namespace RogueMod.Tests.Native;

internal static unsafe class NativeHookTestCallbacks
{
    internal static delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealReflection.NativeUnrealParameter*, int> Callback;
    internal static ulong Context;
    internal static int Phase;
    internal static NativeUnrealReflection.NativeUnrealParameter[] Parameters = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int Register(
        char* functionPath,
        int phase,
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
}
