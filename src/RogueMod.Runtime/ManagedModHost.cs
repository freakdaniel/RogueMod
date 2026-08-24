using RogueMod.Abstractions;
using RogueMod.Core.Mods;

namespace RogueMod.Runtime;

public sealed class ManagedModHost : IAsyncDisposable
{
    private ManagedModLoadContext? _loadContext;
    private IRogueMod? _instance;
    private bool _eventCallbacksEnabled = true;

    private ManagedModHost(ModManifest manifest, ManagedModLoadContext loadContext, IRogueMod instance)
    {
        Manifest = manifest;
        _loadContext = loadContext;
        _instance = instance;
    }

    public ModManifest Manifest { get; }

    public bool IsLoaded => _instance is not null;

    public void DispatchGameEvent(ModGameEventKind eventKind)
    {
        if (!_eventCallbacksEnabled || _instance is not IRogueModGameEvents eventSink)
        {
            return;
        }

        try
        {
            eventSink.OnGameEvent(eventKind);
        }
        catch
        {
            _eventCallbacksEnabled = false;
            throw;
        }
    }

    public static async ValueTask<ManagedModHost> LoadAsync(
        ModManifest manifest,
        string modDirectory,
        IModContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        ArgumentNullException.ThrowIfNull(context);

        if (manifest.Kind != ModKind.Managed)
        {
            throw new InvalidOperationException($"Mod '{manifest.Id}' is {manifest.Kind}, not Managed.");
        }

        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"Invalid manifest for '{manifest.Id}': {string.Join(" ", errors)}");
        }

        var entryPoint = ManagedEntryPoint.Parse(manifest.EntryPoint);
        var assemblyPath = ResolveInsideDirectory(modDirectory, entryPoint.AssemblyRelativePath);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Managed mod assembly was not found: {assemblyPath}", assemblyPath);
        }

        var loadContext = new ManagedModLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(entryPoint.TypeName, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IRogueMod).IsAssignableFrom(type))
            {
                throw new InvalidDataException($"Type '{entryPoint.TypeName}' does not implement {nameof(IRogueMod)}.");
            }

            var instance = Activator.CreateInstance(type) as IRogueMod
                ?? throw new InvalidDataException($"Type '{entryPoint.TypeName}' must have a public parameterless constructor.");
            await instance.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            return new(manifest, loadContext, instance);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        var instance = Interlocked.Exchange(ref _instance, null);
        var loadContext = Interlocked.Exchange(ref _loadContext, null);
        if (instance is null || loadContext is null)
        {
            return;
        }

        try
        {
            await instance.UnloadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    public ValueTask DisposeAsync() => UnloadAsync();

    private static string ResolveInsideDirectory(string directory, string relativePath)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Managed entryPoint escapes the mod directory.");
        }

        return candidate;
    }
}
