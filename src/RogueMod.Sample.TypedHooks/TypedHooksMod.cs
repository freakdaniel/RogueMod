using DeadzoneRogue.Sdk;
using RogueMod.Abstractions;

namespace RogueMod.Sample.TypedHooks;

/// <summary>
/// End-to-end authoring sample built only against the public RogueMod and generated game SDKs.
/// No reflected path, parameter descriptor, native offset, or wire value is authored here.
/// </summary>
public sealed class TypedHooksMod : IRogueMod, IRogueModGameEvents
{
    private IModContext? context;
    private IDisposable? preHook;
    private IDisposable? postHook;
    private bool completed;
    private bool probeCallActive;
    private bool preHookObserved;
    private bool postHookObserved;
    private int attempts;

    public ValueTask LoadAsync(IModContext modContext, CancellationToken cancellationToken = default)
    {
        context = modContext;
        modContext.Logger.Log(ModLogLevel.Information, "TYPED-HOOKS loaded; waiting for Unreal reflection");
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        DisposeHooks();
        context?.Logger.Log(ModLogLevel.Information, "TYPED-HOOKS unloaded");
        context = null;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
        if (completed || context is null
            || eventKind is not (ModGameEventKind.UnrealInitialized or ModGameEventKind.Update))
        {
            return;
        }

        if (++attempts > 1_800)
        {
            completed = true;
            context.Logger.Log(ModLogLevel.Error, "TYPED-HOOKS FAIL: KismetSystemLibrary default object did not become available");
            return;
        }

        TryRun();
    }

    private void TryRun()
    {
        var unreal = context!.Unreal;
        if (!unreal.IsAvailable
            || (unreal.Capabilities & (UnrealReflectionCapabilities.FunctionInvocation
                | UnrealReflectionCapabilities.FunctionHooks
                | UnrealReflectionCapabilities.MapSetProperties
                | UnrealReflectionCapabilities.MapSetWrites))
                != (UnrealReflectionCapabilities.FunctionInvocation
                    | UnrealReflectionCapabilities.FunctionHooks
                    | UnrealReflectionCapabilities.MapSetProperties
                    | UnrealReflectionCapabilities.MapSetWrites))
        {
            return;
        }

        var library = KismetSystemLibrary.FindDefaultObject(unreal);
        if (library is null)
        {
            return;
        }

        try
        {
            RegisterHooks(unreal, library);
            probeCallActive = true;
            var result = library.ParseCommandLine("ignored-by-pre-hook");
            probeCallActive = false;

            if (!preHookObserved
                || !postHookObserved
                || result.OutParams.Count != 1
                || !result.OutParams.TryGetValue("RogueModTypedHook", out var value)
                || !StringComparer.Ordinal.Equals(value, "post-replaced"))
            {
                throw new InvalidOperationException(
                    "The generated invocation did not pass through both typed hooks and return the replacement map.");
            }

            completed = true;
            DisposeHooks();
            context.Logger.Log(
                ModLogLevel.Information,
                "TYPED-HOOKS PASS: generated owner lookup, call, pre-hook and TMap out-parameter post-hook all succeeded");
        }
        catch (Exception exception)
        {
            probeCallActive = false;
            completed = true;
            DisposeHooks();
            context.Logger.Log(ModLogLevel.Error, $"TYPED-HOOKS FAIL: {exception}");
        }
    }

    private void RegisterHooks(IUnrealReflection unreal, KismetSystemLibrary library)
    {
        preHook ??= KismetSystemLibrary.RegisterParseCommandLinePreHook(
            unreal,
            (KismetSystemLibrary _, ref string commandLine) =>
            {
                if (probeCallActive)
                {
                    preHookObserved = true;
                    commandLine = "-RogueModTypedHook=pre-replaced";
                }
            },
            new UnrealHookOptions(Priority: 100, Instance: library.Handle));

        postHook ??= KismetSystemLibrary.RegisterParseCommandLinePostHook(
            unreal,
            (
                KismetSystemLibrary _,
                ref IReadOnlyList<string> tokens,
                ref IReadOnlyList<string> switches,
                ref IReadOnlyDictionary<string, string> parameters) =>
            {
                if (probeCallActive)
                {
                    postHookObserved = true;
                    parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RogueModTypedHook"] = "post-replaced"
                    };
                }
            },
            new UnrealHookOptions(Priority: 100, Instance: library.Handle));
    }

    private void DisposeHooks()
    {
        postHook?.Dispose();
        postHook = null;
        preHook?.Dispose();
        preHook = null;
    }
}
