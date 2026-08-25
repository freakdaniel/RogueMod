using System.Reflection;
using System.Runtime.Loader;
using RogueMod.Abstractions;

namespace RogueMod.Runtime;

internal sealed class ManagedModLoadContext(string mainAssemblyPath)
    : AssemblyLoadContext($"RogueMod:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
{
    private static readonly string AbstractionsAssemblyName = typeof(IRogueMod).Assembly.GetName().Name!;
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name?.Equals(AbstractionsAssemblyName, StringComparison.Ordinal) == true)
        {
            // The runtime itself may have been loaded by hostfxr into a non-default
            // context. Return its contract assembly explicitly so every collectible
            // mod context sees the same IRogueMod type identity.
            return typeof(IRogueMod).Assembly;
        }

        var sharedAssembly = ManagedSharedAssemblyCatalog.Resolve(assemblyName);
        if (sharedAssembly is not null)
        {
            return sharedAssembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
