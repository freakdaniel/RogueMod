using System.Reflection;
using System.Runtime.Loader;

namespace RogueMod.Runtime;

internal static class ManagedSharedAssemblyCatalog
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.Ordinal);
    private static readonly AssemblyLoadContext RuntimeLoadContext =
        AssemblyLoadContext.GetLoadContext(typeof(ManagedSharedAssemblyCatalog).Assembly)
        ?? throw new InvalidOperationException("RogueMod.Runtime is not associated with an AssemblyLoadContext.");

    public static void RegisterDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            return;
        }

        lock (Sync)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Shared managed assemblies cannot be links: {path}");
                }

                var candidateName = AssemblyName.GetAssemblyName(path);
                var simpleName = candidateName.Name
                    ?? throw new InvalidDataException($"Shared managed assembly has no simple name: {path}");
                if (Assemblies.TryGetValue(simpleName, out var existing))
                {
                    EnsureCompatible(candidateName, existing.GetName(), path);
                    continue;
                }

                using var assemblyStream = File.OpenRead(path);
                var assembly = RuntimeLoadContext.LoadFromStream(assemblyStream);
                EnsureCompatible(candidateName, assembly.GetName(), path);
                Assemblies.Add(simpleName, assembly);
            }
        }
    }

    public static Assembly? Resolve(AssemblyName requestedName)
    {
        var simpleName = requestedName.Name;
        if (simpleName is null)
        {
            return null;
        }

        lock (Sync)
        {
            if (!Assemblies.TryGetValue(simpleName, out var assembly))
            {
                return null;
            }

            EnsureCompatible(requestedName, assembly.GetName(), simpleName);
            return assembly;
        }
    }

    private static void EnsureCompatible(AssemblyName requested, AssemblyName available, string source)
    {
        if (requested.Name?.Equals(available.Name, StringComparison.Ordinal) != true ||
            requested.Version is not null && requested.Version != available.Version)
        {
            throw new FileLoadException(
                $"Shared assembly '{source}' provides '{available.FullName}', but the mod requires '{requested.FullName}'.");
        }
    }
}
