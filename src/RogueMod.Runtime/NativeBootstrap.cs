using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RogueMod.Abstractions;

namespace RogueMod.Runtime;

/// <summary>Entry points invoked by the native UE4SS bridge through hostfxr.</summary>
public static unsafe class NativeBootstrap
{
    private const uint SupportedAbiVersion = 11;
    private static NativeHostApi _hostApi;
    private static ManagedRuntimeCoordinator? _coordinator;
    private static int _initialized;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(nint hostApiAddress)
    {
        try
        {
            if (hostApiAddress == nint.Zero)
            {
                return -1;
            }

            var candidate = *(NativeHostApi*)hostApiAddress;
            if (candidate.Size < (uint)sizeof(NativeHostApi) || candidate.AbiVersion != SupportedAbiVersion)
            {
                return -2;
            }

            if (candidate.ModRoot == null || candidate.GameProfileId == null || candidate.GameModsRoot == null)
            {
                return -2;
            }

            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            {
                return 0;
            }

            _hostApi = candidate;
            var modRoot = new string(candidate.ModRoot);
            var gameProfileId = new string(candidate.GameProfileId);
            var gameModsRoot = new string(candidate.GameModsRoot);
            ManagedSharedAssemblyCatalog.RegisterDirectory(Path.Combine(modRoot, "runtime", "shared"));
            var unreal = new NativeUnrealReflection(
                candidate.UnrealIsAvailable,
                candidate.UnrealFindFirstOf,
                candidate.UnrealIsValid,
                candidate.UnrealGetClass,
                candidate.UnrealGetPathName,
                candidate.UnrealGetCapabilities,
                candidate.UnrealReadProperty,
                candidate.UnrealWriteProperty,
                candidate.UnrealInvoke,
                candidate.UnrealFindAllOf,
                candidate.UnrealRegisterHook,
                candidate.UnrealUnregisterHook,
                Log);
            _coordinator = new ManagedRuntimeCoordinator(gameModsRoot, gameProfileId, unreal, Log);
            _coordinator.LoadAsync().AsTask().GetAwaiter().GetResult();
            LogRuntime(ModLogLevel.Information, $"Managed runtime initialized. Loaded {_coordinator.LoadedCount} mod(s).");
            return 0;
        }
        catch (Exception exception)
        {
            LogRuntime(ModLogLevel.Error, $"Managed runtime initialization failed: {exception}");
            var coordinator = Interlocked.Exchange(ref _coordinator, null);
            try
            {
                coordinator?.UnloadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception unloadException)
            {
                LogRuntime(ModLogLevel.Error, $"Managed runtime rollback failed: {unloadException}");
            }
            _hostApi = default;
            Volatile.Write(ref _initialized, 0);
            return -3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int DispatchGameEvent(int eventKind)
    {
        try
        {
            if (Volatile.Read(ref _initialized) == 0 || _coordinator is null)
            {
                return -1;
            }
            if (!Enum.IsDefined((ModGameEventKind)eventKind))
            {
                return -2;
            }

            _coordinator.DispatchGameEvent((ModGameEventKind)eventKind);
            return 0;
        }
        catch (Exception exception)
        {
            LogRuntime(ModLogLevel.Error, $"Game-event dispatch failed: {exception}");
            return -3;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Shutdown()
    {
        try
        {
            if (Interlocked.Exchange(ref _initialized, 0) == 0)
            {
                return 0;
            }

            var coordinator = Interlocked.Exchange(ref _coordinator, null);
            coordinator?.UnloadAsync().AsTask().GetAwaiter().GetResult();
            LogRuntime(ModLogLevel.Information, "Managed runtime shut down.");
            _hostApi = default;
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    private static void LogRuntime(ModLogLevel level, string message) =>
        Log(level, $"[ManagedRuntime] {message}");

    private static void Log(ModLogLevel level, string message)
    {
        var callback = _hostApi.Log;
        if (callback == null)
        {
            return;
        }

        fixed (char* messagePointer = message)
        {
            callback((int)level, messagePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeHostApi
    {
        public readonly uint Size;
        public readonly uint AbiVersion;
        public readonly delegate* unmanaged[Cdecl]<int, char*, void> Log;
        public readonly char* ModRoot;
        public readonly char* GameProfileId;
        public readonly delegate* unmanaged[Cdecl]<int> UnrealIsAvailable;
        public readonly delegate* unmanaged[Cdecl]<char*, ulong> UnrealFindFirstOf;
        public readonly delegate* unmanaged[Cdecl]<ulong, int> UnrealIsValid;
        public readonly delegate* unmanaged[Cdecl]<ulong, ulong> UnrealGetClass;
        public readonly delegate* unmanaged[Cdecl]<ulong, char*, uint, uint*, int> UnrealGetPathName;
        public readonly delegate* unmanaged[Cdecl]<uint> UnrealGetCapabilities;
        public readonly delegate* unmanaged[Cdecl]<ulong, char*, int> UnrealInvokeZeroParameter;
        public readonly delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> UnrealReadProperty;
        public readonly delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> UnrealWriteProperty;
        public readonly delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealParameter*, int> UnrealInvoke;
        public readonly char* GameModsRoot;
        public readonly delegate* unmanaged[Cdecl]<char*, ulong*, uint, uint*, int> UnrealFindAllOf;
        public readonly delegate* unmanaged[Cdecl]<char*, int, uint, NativeUnrealReflection.NativeUnrealParameter*, delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealReflection.NativeUnrealParameter*, int>, ulong, ulong*, int> UnrealRegisterHook;
        public readonly delegate* unmanaged[Cdecl]<ulong, int> UnrealUnregisterHook;
    }
}
