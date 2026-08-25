using System.Buffers.Binary;
using RogueMod.Abstractions;

namespace RogueMod.Tests.Live;

public sealed class DeadzoneLiveReflectionProbe : IRogueMod, IRogueModGameEvents
{
    private const int MaximumUpdateAttempts = 1_800;
    private static readonly UnrealOptionalDescriptor TileUpdateModeValue = new(
        "EnumProperty:/Script/Niagara.ENiagaraLwcTileUpdateMode",
        1);
    private static readonly UnrealPropertyDescriptor TileUpdateModeProperty = new(
        "/Script/Niagara.NiagaraSystem",
        "LargeWorldCoordinateTileUpdateMode",
        "OptionalProperty",
        82,
        1,
        "CPF_Edit | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_AdvancedDisplay | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPrivate",
        2)
    {
        Optional = TileUpdateModeValue
    };
    private static readonly UnrealPropertyDescriptor TickCallbackHelperProperty = new(
        "/Script/Valhalla.ValGameInstance",
        "TickCallbackHelper",
        "WeakObjectProperty:/Script/Valhalla.ValTickCallbackHelper",
        1600,
        1,
        "CPF_ZeroConstructor | CPF_Transient | CPF_IsPlainOldData | CPF_NoDestructor | CPF_Protected | CPF_UObjectWrapper | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierProtected",
        8);
    private static readonly (string ClassName, UnrealPropertyDescriptor Property)[] LazyCandidates =
    [
        ("/Script/Niagara.Default__NiagaraDataInterfaceActorComponent", new(
            "/Script/Niagara.NiagaraDataInterfaceActorComponent",
            "SourceActor",
            "LazyObjectProperty:/Script/Engine.Actor",
            64,
            1,
            "CPF_Edit | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_UObjectWrapper",
            24)),
        ("/Script/Niagara.Default__NiagaraDataInterfaceSocketReader", new(
            "/Script/Niagara.NiagaraDataInterfaceSocketReader",
            "SourceActor",
            "LazyObjectProperty:/Script/Engine.Actor",
            80,
            1,
            "CPF_Edit | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_UObjectWrapper",
            24)),
        ("/Script/VariantManagerContent.Default__VariantObjectBinding", new(
            "/Script/VariantManagerContent.VariantObjectBinding",
            "LazyObjectPtr",
            "LazyObjectProperty:/Script/CoreUObject.Object",
            88,
            1,
            "CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_UObjectWrapper",
            24))
    ];

    private IModContext? context;
    private int updateAttempts;
    private bool optionalCompleted;
    private bool weakCompleted;
    private bool lazyCompleted;

    public ValueTask LoadAsync(IModContext modContext, CancellationToken cancellationToken = default)
    {
        context = modContext;
        modContext.Logger.Log(ModLogLevel.Information, "LIVE-PROBE loaded");
        TryVerifyOptional();
        TryVerifyWeakReference();
        TryVerifyLazyReference();
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        context = null;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
        if ((optionalCompleted && weakCompleted && lazyCompleted) || context is null)
        {
            return;
        }
        if (eventKind is ModGameEventKind.UnrealInitialized or ModGameEventKind.UiInitialized)
        {
            TryVerifyOptional();
            TryVerifyWeakReference();
            TryVerifyLazyReference();
            return;
        }
        if (eventKind == ModGameEventKind.Update && ++updateAttempts <= MaximumUpdateAttempts)
        {
            TryVerifyOptional();
            TryVerifyWeakReference();
            if (updateAttempts % 300 == 0)
            {
                TryVerifyLazyReference();
            }
        }
        if (updateAttempts == MaximumUpdateAttempts)
        {
            if (!optionalCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-TOPTIONAL FAIL: no writable NiagaraSystem instance was verified");
            }
            if (!weakCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-WEAK FAIL: no writable ValGameInstance weak reference was verified");
            }
            if (!lazyCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-LAZY FAIL: no real lazy property completed pending/null/restore verification");
            }
        }
    }

    private void TryVerifyLazyReference()
    {
        if (lazyCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.LazyObjectReferences) == 0)
        {
            return;
        }

        foreach (var candidate in LazyCandidates)
        {
            IReadOnlyList<UnrealObjectHandle> handles;
            if (candidate.ClassName[0] == '/')
            {
                var exact = context.Unreal.FindFirstOf(candidate.ClassName);
                handles = exact.IsNull ? [] : [exact];
            }
            else
            {
                try
                {
                    handles = context.Unreal.FindAllOf(candidate.ClassName);
                }
                catch (InvalidOperationException exception)
                {
                    context.Logger.Log(
                        ModLogLevel.Debug,
                        $"LIVE-LAZY enumeration deferred for {candidate.ClassName}: {exception.Message}");
                    continue;
                }
            }
            foreach (var handle in handles)
            {
                if (!context.Unreal.IsValid(handle))
                {
                    continue;
                }
                try
                {
                    VerifyLazyRoundTrip(handle, candidate.Property);
                    lazyCompleted = true;
                    return;
                }
                catch (Exception exception)
                {
                    context.Logger.Log(
                        ModLogLevel.Debug,
                        $"LIVE-LAZY candidate rejected: {context.Unreal.GetPathName(handle)}: {exception.Message}");
                }
            }
        }
    }

    private void VerifyLazyRoundTrip(UnrealObjectHandle owner, UnrealPropertyDescriptor property)
    {
        var unreal = context!.Unreal;
        var original = unreal.ReadProperty(owner, property).As<UnrealLazyObjectValue>();
        var pending = CreatePendingLazyValue();

        try
        {
            unreal.WriteProperty(owner, property, UnrealValue.From(pending));
            var pendingRead = unreal.ReadProperty(owner, property).As<UnrealLazyObjectValue>();
            if (pendingRead.ObjectId != pending.ObjectId
                || !pendingRead.CachedHandle.IsNull
                || !pendingRead.CopyNativeStorage().SequenceEqual(pending.CopyNativeStorage()))
            {
                throw new InvalidOperationException("pending lazy identity did not survive a write/read round-trip");
            }

            unreal.WriteProperty(owner, property, UnrealValue.From(UnrealLazyObjectValue.Null));
            var cleared = unreal.ReadProperty(owner, property).As<UnrealLazyObjectValue>();
            if (!cleared.IsNull || cleared.CopyNativeStorage().Any(static item => item != 0))
            {
                throw new InvalidOperationException("null lazy reference did not survive a write/read round-trip");
            }
        }
        finally
        {
            unreal.WriteProperty(owner, property, UnrealValue.From(original));
        }

        var restored = unreal.ReadProperty(owner, property).As<UnrealLazyObjectValue>();
        if (restored.ObjectId != original.ObjectId
            || !restored.CopyNativeStorage().SequenceEqual(original.CopyNativeStorage()))
        {
            throw new InvalidOperationException("original lazy reference identity was not restored");
        }

        var targetPath = restored.CachedHandle.IsNull
            ? "<pending>"
            : unreal.GetPathName(restored.CachedHandle) ?? "<stale>";
        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-LAZY PASS: {unreal.GetPathName(owner)} property={property.Name} original={original.ObjectId} pending={pending.ObjectId} target={targetPath} pending=ok null=ok restore=ok");
    }

    private static UnrealLazyObjectValue CreatePendingLazyValue()
    {
        var objectId = new UnrealGuid(0x524F_4755, 0x454D_4F44, 0x4C49_5645, 0x5445_5354);
        var storage = new byte[UnrealLazyObjectValue.NativeStorageSize];
        BinaryPrimitives.WriteUInt32LittleEndian(storage.AsSpan(8), objectId.A);
        BinaryPrimitives.WriteUInt32LittleEndian(storage.AsSpan(12), objectId.B);
        BinaryPrimitives.WriteUInt32LittleEndian(storage.AsSpan(16), objectId.C);
        BinaryPrimitives.WriteUInt32LittleEndian(storage.AsSpan(20), objectId.D);
        return new UnrealLazyObjectValue(objectId, UnrealObjectHandle.Null, storage);
    }

    private void TryVerifyOptional()
    {
        if (optionalCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.OptionalValues) == 0)
        {
            return;
        }

        foreach (var handle in context.Unreal.FindAllOf("NiagaraSystem"))
        {
            if (!context.Unreal.IsValid(handle))
            {
                continue;
            }
            if (TryVerifyOptionalCandidate(handle, out var failure))
            {
                optionalCompleted = true;
                return;
            }
            context.Logger.Log(
                ModLogLevel.Debug,
                $"LIVE-TOPTIONAL candidate rejected: {context.Unreal.GetPathName(handle)}: {failure}");
        }
    }

    private bool TryVerifyOptionalCandidate(UnrealObjectHandle handle, out string? failure)
    {
        try
        {
            VerifyOptionalRoundTrip(handle);
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private void TryVerifyWeakReference()
    {
        if (weakCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.WeakObjectReferences) == 0)
        {
            return;
        }

        foreach (var handle in context.Unreal.FindAllOf("ValGameInstance"))
        {
            if (!context.Unreal.IsValid(handle))
            {
                continue;
            }
            if (TryVerifyWeakCandidate(handle, out var failure))
            {
                weakCompleted = true;
                return;
            }
            context.Logger.Log(
                ModLogLevel.Debug,
                $"LIVE-WEAK candidate rejected: {context.Unreal.GetPathName(handle)}: {failure}");
        }
    }

    private bool TryVerifyWeakCandidate(UnrealObjectHandle handle, out string? failure)
    {
        try
        {
            VerifyWeakRoundTrip(handle);
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private void VerifyWeakRoundTrip(UnrealObjectHandle owner)
    {
        var unreal = context!.Unreal;
        var original = unreal.ReadProperty(owner, TickCallbackHelperProperty).AsObjectHandle();
        if (original.IsNull || !unreal.IsValid(original))
        {
            throw new InvalidOperationException("TickCallbackHelper is null or stale");
        }

        try
        {
            unreal.WriteProperty(owner, TickCallbackHelperProperty, UnrealValue.From(UnrealObjectHandle.Null));
            var cleared = unreal.ReadProperty(owner, TickCallbackHelperProperty).AsObjectHandle();
            if (!cleared.IsNull)
            {
                throw new InvalidOperationException("null weak reference did not survive a write/read round-trip");
            }
        }
        finally
        {
            unreal.WriteProperty(owner, TickCallbackHelperProperty, UnrealValue.From(original));
        }

        var restored = unreal.ReadProperty(owner, TickCallbackHelperProperty).AsObjectHandle();
        if (restored != original || !unreal.IsValid(restored))
        {
            throw new InvalidOperationException("original weak reference was not restored");
        }

        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-WEAK PASS: {unreal.GetPathName(owner)} target={unreal.GetPathName(restored)} null=ok restore=ok");
    }

    private void VerifyOptionalRoundTrip(UnrealObjectHandle handle)
    {
        var unreal = context!.Unreal;
        var original = ReadOptional(unreal, handle);
        try
        {
            WriteOptional(unreal, handle, UnrealOptional<byte>.Unset);
            var unset = ReadOptional(unreal, handle);
            if (unset.IsSet)
            {
                throw new InvalidOperationException("unset state did not survive a write/read round-trip");
            }

            var safeValue = original.IsSet ? original.Value : (byte)0;
            WriteOptional(unreal, handle, UnrealOptional<byte>.FromValue(safeValue));
            var set = ReadOptional(unreal, handle);
            if (!set.IsSet || set.Value != safeValue)
            {
                throw new InvalidOperationException("set state did not survive a write/read round-trip");
            }
        }
        finally
        {
            WriteOptional(unreal, handle, original);
        }

        var restored = ReadOptional(unreal, handle);
        if (restored.IsSet != original.IsSet
            || (original.IsSet && restored.Value != original.Value))
        {
            throw new InvalidOperationException("original TOptional state was not restored");
        }

        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-TOPTIONAL PASS: {unreal.GetPathName(handle)} original={Format(original)} unset=ok set=ok restore=ok");
    }

    private static UnrealOptional<byte> ReadOptional(IUnrealReflection unreal, UnrealObjectHandle handle) =>
        UnrealOptional<byte>.FromUnrealValue(
            unreal.ReadProperty(handle, TileUpdateModeProperty),
            value => value.As<byte>());

    private static void WriteOptional(
        IUnrealReflection unreal,
        UnrealObjectHandle handle,
        UnrealOptional<byte> value) =>
        unreal.WriteProperty(
            handle,
            TileUpdateModeProperty,
            value.ToUnrealValue(TileUpdateModeValue, UnrealValue.From));

    private static string Format(UnrealOptional<byte> value) =>
        value.IsSet ? $"set:{value.Value}" : "unset";
}
