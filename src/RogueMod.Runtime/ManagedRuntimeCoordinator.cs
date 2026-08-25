using RogueMod.Abstractions;
using RogueMod.Core.Mods;

namespace RogueMod.Runtime;

internal sealed class ManagedRuntimeCoordinator(
    string modsRoot,
    string gameProfileId,
    IUnrealReflection unreal,
    Action<ModLogLevel, string> log)
{
    private readonly List<ManagedModHost> _loadedMods = [];

    public int LoadedCount => _loadedMods.Count;

    public void DispatchGameEvent(ModGameEventKind eventKind)
    {
        foreach (var host in _loadedMods)
        {
            try
            {
                host.DispatchGameEvent(eventKind);
            }
            catch (Exception exception)
            {
                LogRuntime(
                    ModLogLevel.Error,
                    $"Disabled game-event callbacks for '{host.Manifest.Id}' after {eventKind}: {exception}");
            }
        }
    }

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(modsRoot))
        {
            LogRuntime(ModLogLevel.Information, $"Managed mods directory does not exist: {modsRoot}");
            return;
        }

        var discovered = Discover(modsRoot);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new HashSet<string>(StringComparer.Ordinal);

        while (discovered.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var madeProgress = RejectUnavailableDependencies(discovered, failed);

            foreach (var descriptor in discovered.Values
                         .Where(candidate => candidate.Manifest.Dependencies?.All(loaded.Contains) != false)
                         .OrderBy(candidate => candidate.Manifest.Id, StringComparer.Ordinal)
                         .ToArray())
            {
                discovered.Remove(descriptor.Manifest.Id);
                madeProgress = true;
                try
                {
                    var context = new RuntimeModContext(
                        descriptor.Manifest.Id,
                        gameProfileId,
                        new RuntimeModLogger(descriptor.Manifest.Id, log),
                        new OwnedUnrealReflection(unreal));
                    var host = await ManagedModHost.LoadAsync(
                        descriptor.Manifest,
                        descriptor.Directory,
                        context,
                        cancellationToken).ConfigureAwait(false);
                    _loadedMods.Add(host);
                    loaded.Add(descriptor.Manifest.Id);
                    LogRuntime(ModLogLevel.Information, $"Loaded managed mod '{descriptor.Manifest.Id}'.");
                }
                catch (Exception exception)
                {
                    failed.Add(descriptor.Manifest.Id);
                    LogRuntime(ModLogLevel.Error, $"Could not load managed mod '{descriptor.Manifest.Id}': {exception}");
                }
            }

            if (!madeProgress)
            {
                foreach (var descriptor in discovered.Values.OrderBy(value => value.Manifest.Id, StringComparer.Ordinal))
                {
                    failed.Add(descriptor.Manifest.Id);
                    LogRuntime(ModLogLevel.Error, $"Dependency cycle prevents loading managed mod '{descriptor.Manifest.Id}'.");
                }
                discovered.Clear();
            }
        }
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        for (var index = _loadedMods.Count - 1; index >= 0; index--)
        {
            var host = _loadedMods[index];
            try
            {
                await host.UnloadAsync(cancellationToken).ConfigureAwait(false);
                LogRuntime(ModLogLevel.Information, $"Unloaded managed mod '{host.Manifest.Id}'.");
            }
            catch (Exception exception)
            {
                LogRuntime(ModLogLevel.Error, $"Could not unload managed mod '{host.Manifest.Id}': {exception}");
            }
        }
        _loadedMods.Clear();
    }

    private Dictionary<string, ModDescriptor> Discover(string modsRoot)
    {
        var discovered = new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(modsRoot).Order(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(directory, RogueModLayout.DisabledMarkerFileName)))
            {
                LogRuntime(ModLogLevel.Trace, $"Skipping disabled managed package: {directory}");
                continue;
            }
            var manifestPath = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifestPath))
            {
                LogRuntime(ModLogLevel.Warning, $"Skipping managed mod directory without mod.json: {directory}");
                continue;
            }

            try
            {
                var manifest = ModManifestLoader.Load(manifestPath);
                if (manifest.Kind != ModKind.Managed)
                {
                    LogRuntime(ModLogLevel.Trace, $"Skipping non-managed package '{manifest.Id}' in the shared Mods directory.");
                    continue;
                }
                if (!discovered.TryAdd(manifest.Id, new(manifest, directory)))
                {
                    LogRuntime(ModLogLevel.Error, $"Duplicate managed mod id '{manifest.Id}' was ignored: {directory}");
                }
            }
            catch (Exception exception)
            {
                LogRuntime(ModLogLevel.Error, $"Could not read managed mod manifest '{manifestPath}': {exception.Message}");
            }
        }
        return discovered;
    }

    private bool RejectUnavailableDependencies(
        Dictionary<string, ModDescriptor> discovered,
        HashSet<string> failed)
    {
        var rejected = false;
        var availableIds = discovered.Keys.Concat(_loadedMods.Select(host => host.Manifest.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var descriptor in discovered.Values.ToArray())
        {
            var unavailable = descriptor.Manifest.Dependencies?
                .FirstOrDefault(dependency => failed.Contains(dependency) || !availableIds.Contains(dependency));
            if (unavailable is null)
            {
                continue;
            }

            discovered.Remove(descriptor.Manifest.Id);
            failed.Add(descriptor.Manifest.Id);
            rejected = true;
            LogRuntime(ModLogLevel.Error, $"Managed mod '{descriptor.Manifest.Id}' requires unavailable mod '{unavailable}'.");
        }
        return rejected;
    }

    private sealed record ModDescriptor(ModManifest Manifest, string Directory);

    private void LogRuntime(ModLogLevel level, string message) =>
        log(level, $"[ManagedRuntime] {message}");

    private sealed record RuntimeModContext(
        string ModId,
        string GameProfileId,
        IModLogger Logger,
        IUnrealReflection Unreal) : IModContext, IDisposable
    {
        public void Dispose() => (Unreal as IDisposable)?.Dispose();
    }

    private sealed class RuntimeModLogger(string modId, Action<ModLogLevel, string> log) : IModLogger
    {
        public void Log(ModLogLevel level, string message) => log(level, $"[C#:{modId}] {message}");
    }
}
