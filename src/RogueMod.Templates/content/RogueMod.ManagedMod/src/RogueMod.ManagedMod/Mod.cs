using RogueMod.Abstractions;

namespace RogueMod.ManagedMod;

public sealed class Mod : IRogueMod, IRogueModGameEvents
{
    private IModLogger? _logger;

    public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
    {
        _logger = context.Logger;
        _logger.Log(ModLogLevel.Information, "Sample managed mod loaded.");
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        _logger?.Log(ModLogLevel.Information, "Sample managed mod unloaded.");
        _logger = null;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
        if (eventKind == ModGameEventKind.ProgramStarted)
        {
            _logger?.Log(ModLogLevel.Information, "Deadzone: Rogue started.");
        }
    }
}
