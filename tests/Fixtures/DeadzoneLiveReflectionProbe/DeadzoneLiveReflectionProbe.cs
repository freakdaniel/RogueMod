using System.Buffers.Binary;
using RogueMod.Abstractions;

namespace RogueMod.Tests.Live;

public sealed class DeadzoneLiveReflectionProbe : IRogueMod, IRogueModGameEvents
{
    [Flags]
    private enum LiveFeature
    {
        Optional = 1 << 0,
        WeakReference = 1 << 1,
        LazyReference = 1 << 2,
        NameArray = 1 << 3,
        ObjectPtr = 1 << 4,
        ObjectCreation = 1 << 5,
        ActorSpawn = 1 << 6,
        SoftObject = 1 << 7,
        All = Optional | WeakReference | LazyReference | NameArray | ObjectPtr | ObjectCreation | ActorSpawn | SoftObject
    }

    private const LiveFeature EnabledFeatures = LiveFeature.All;
    private const int MaximumUpdateAttempts = 1_800;
    private const string NameArrayMarker = "RogueModLiveProbe";
    private static readonly UnrealArrayDescriptor ActorTagsValue = new("NameProperty", 8);
    private static readonly UnrealPropertyDescriptor ActorTagsProperty = new(
        "/Script/Engine.Actor",
        "Tags",
        "ArrayProperty",
        480,
        1,
        "CPF_Edit | CPF_BlueprintVisible | CPF_ZeroConstructor | CPF_AdvancedDisplay | CPF_NativeAccessSpecifierPublic",
        16)
    {
        Array = ActorTagsValue
    };

    private static readonly UnrealPropertyDescriptor ActorOwnerProperty = new(
        "/Script/Engine.Actor",
        "Owner",
        "ObjectProperty:/Script/Engine.Actor",
        344,
        1,
        "CPF_Net | CPF_ZeroConstructor | CPF_RepNotify | CPF_NoDestructor | CPF_UObjectWrapper | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic | CPF_TObjectPtr",
        8);
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

    private static readonly UnrealPropertyDescriptor LevelStreamingWorldAssetProperty = new(
        "/Script/Engine.LevelStreaming",
        "WorldAsset",
        "SoftObjectProperty:/Script/Engine.World",
        40,
        1,
        "CPF_Edit | CPF_BlueprintVisible | CPF_BlueprintReadOnly | CPF_EditConst | CPF_Protected | CPF_UObjectWrapper | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierProtected",
        40);
    private static readonly UnrealFunctionDescriptor K2DestroyActorFunction = new(
        "/Script/Engine.Actor",
        "/Script/Engine.Actor:K2_DestroyActor",
        "K2_DestroyActor",
        "FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable");
    private static readonly UnrealFunctionDescriptor K2DestroyComponentFunction = new(
        "/Script/Engine.ActorComponent",
        "/Script/Engine.ActorComponent:K2_DestroyComponent",
        "K2_DestroyComponent",
        "FUNC_Final | FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable",
        [new(
            "Object",
            "ObjectProperty:/Script/CoreUObject.Object",
            0,
            1,
            "CPF_Parm | CPF_ZeroConstructor | CPF_NoDestructor | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic",
            8)]);

    private IModContext? context;
    private int updateAttempts;
    private bool optionalCompleted;
    private bool weakCompleted;
    private bool lazyCompleted;
    private bool nameArrayCompleted;
    private bool nameArrayFailureLogged;
    private bool objectPtrCompleted;
    private bool objectPtrFailureLogged;
    private bool objectCreationCompleted;
    private bool objectCreationFailureLogged;
    private bool actorSpawnCompleted;
    private bool actorSpawnFailureLogged;
    private bool softObjectCompleted;
    private bool softObjectFailureLogged;

    public ValueTask LoadAsync(IModContext modContext, CancellationToken cancellationToken = default)
    {
        context = modContext;
        modContext.Logger.Log(ModLogLevel.Information, "LIVE-PROBE loaded");
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        context = null;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
        if (context is null
            || (IsComplete(LiveFeature.Optional, optionalCompleted)
                && IsComplete(LiveFeature.WeakReference, weakCompleted)
                && IsComplete(LiveFeature.LazyReference, lazyCompleted)
                && IsComplete(LiveFeature.NameArray, nameArrayCompleted)
                && IsComplete(LiveFeature.ObjectPtr, objectPtrCompleted)
                && IsComplete(LiveFeature.ObjectCreation, objectCreationCompleted)
                && IsComplete(LiveFeature.ActorSpawn, actorSpawnCompleted)
                && IsComplete(LiveFeature.SoftObject, softObjectCompleted)))
        {
            return;
        }
        if (eventKind is ModGameEventKind.UnrealInitialized or ModGameEventKind.UiInitialized)
        {
            TryNextFeature();
            return;
        }
        if (eventKind == ModGameEventKind.Update && ++updateAttempts <= MaximumUpdateAttempts)
        {
            TryNextFeature();
        }
        if (updateAttempts == MaximumUpdateAttempts)
        {
            if (IsEnabled(LiveFeature.Optional) && !optionalCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-TOPTIONAL FAIL: no writable NiagaraSystem instance was verified");
            }
            if (IsEnabled(LiveFeature.WeakReference) && !weakCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-WEAK FAIL: no writable ValGameInstance weak reference was verified");
            }
            if (IsEnabled(LiveFeature.LazyReference) && !lazyCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-LAZY FAIL: no real lazy property completed pending/null/restore verification");
            }
            if (IsEnabled(LiveFeature.NameArray) && !nameArrayCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-NAME-ARRAY FAIL: no Actor.Tags resize/restore round-trip completed");
            }
            if (IsEnabled(LiveFeature.ObjectPtr) && !objectPtrCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-TOBJECTPTR FAIL: no CPF_TObjectPtr CDO swap/restore round-trip completed");
            }
            if (IsEnabled(LiveFeature.ObjectCreation) && !objectCreationCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-CREATE FAIL: no object creation round-trip completed");
            }
            if (IsEnabled(LiveFeature.ActorSpawn) && !actorSpawnCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-SPAWN FAIL: no actor spawn round-trip completed");
            }
            if (IsEnabled(LiveFeature.SoftObject) && !softObjectCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-SOFT FAIL: no soft object reference round-trip completed");
            }
        }
    }

    private static bool IsEnabled(LiveFeature feature) => (EnabledFeatures & feature) != 0;

    private static bool IsComplete(LiveFeature feature, bool completed) => !IsEnabled(feature) || completed;

    private void TryNextFeature()
    {
        // Object enumeration is inherently unstable while Unreal is creating and destroying
        // startup objects. Run at most one probe per callback so retries cannot cascade through
        // several FindAllOf calls and mutations in the same frame.
        switch (updateAttempts % 8)
        {
            case 0: TryVerifyOptional(); break;
            case 1: TryVerifyWeakReference(); break;
            case 2: TryVerifyLazyReference(); break;
            case 3: TryVerifyNameArrayResize(); break;
            case 4: TryVerifyObjectPtr(); break;
            case 5: TryVerifyObjectCreation(); break;
            case 6: TryVerifyActorSpawn(); break;
            case 7: TryVerifySoftObject(); break;
        }
    }

    private void TryVerifySoftObject()
    {
        if ((EnabledFeatures & LiveFeature.SoftObject) == 0
            || softObjectCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.SoftObjectReferences) == 0)
        {
            return;
        }

        var owner = context.Unreal.FindFirstOf("/Script/Engine.Default__LevelStreaming");
        if (owner.IsNull || !context.Unreal.IsValid(owner))
        {
            return;
        }

        try
        {
            var unreal = context.Unreal;
            var original = ReadSoftObject(unreal, owner);
            var marker = new UnrealSoftObjectValue(
                "/Game/Test/RogueModLiveProbe.RogueModLiveProbe",
                UnrealObjectHandle.Null,
                original.CopyNativeStorage());
            try
            {
                unreal.WriteProperty(owner, LevelStreamingWorldAssetProperty, UnrealValue.From(marker));
                var written = ReadSoftObject(unreal, owner);
                if (!StringComparer.Ordinal.Equals(written.Path, marker.Path))
                {
                    throw new InvalidOperationException($"soft path did not survive a write/read round-trip: '{written.Path}'");
                }
            }
            finally
            {
                unreal.WriteProperty(owner, LevelStreamingWorldAssetProperty, UnrealValue.From(original));
            }

            var restored = ReadSoftObject(unreal, owner);
            if (!StringComparer.Ordinal.Equals(restored.Path, original.Path))
            {
                throw new InvalidOperationException($"original soft path was not restored: '{restored.Path}'");
            }

            softObjectCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-SOFT PASS: {unreal.GetPathName(owner)} original='{original.Path}' marker='{marker.Path}' write=ok restore=ok");
        }
        catch (Exception exception)
        {
            if (!softObjectFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-SOFT rejected: {exception.Message}");
                softObjectFailureLogged = true;
            }
        }
    }

    private static UnrealSoftObjectValue ReadSoftObject(IUnrealReflection unreal, UnrealObjectHandle owner) =>
        unreal.ReadProperty(owner, LevelStreamingWorldAssetProperty).As<UnrealSoftObjectValue>();

    private void TryVerifyActorSpawn()
    {
        if ((EnabledFeatures & LiveFeature.ActorSpawn) == 0
            || actorSpawnCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.ActorSpawning) == 0)
        {
            return;
        }

        var contextObject = context.Unreal.FindFirstOf("PlayerController");
        if (contextObject.IsNull)
        {
            return;
        }

        var classHandle = context.Unreal.FindFirstOf("/Script/Engine.Actor");
        if (classHandle.IsNull || !context.Unreal.IsValid(classHandle))
        {
            return;
        }

        try
        {
            var spawned = context.Unreal.SpawnActor(
                contextObject,
                classHandle,
                new UnrealVector(0f, 0f, 0f),
                UnrealRotator.Zero);
            if (spawned.IsNull || !context.Unreal.IsValid(spawned))
            {
                throw new InvalidOperationException("spawned actor is null or stale");
            }
            var spawnedClass = context.Unreal.GetClass(spawned);
            if (spawnedClass != classHandle)
            {
                throw new InvalidOperationException("spawned actor has the wrong class");
            }
            var path = context.Unreal.GetPathName(spawned);
            if (path is null)
            {
                throw new InvalidOperationException("spawned actor has no path");
            }

            context.Unreal.Invoke(spawned, K2DestroyActorFunction, []);
            actorSpawnCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-SPAWN PASS: {path} class={context.Unreal.GetPathName(spawnedClass)} valid=ok destroy=ok");
        }
        catch (Exception exception)
        {
            if (!actorSpawnFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-SPAWN rejected: {exception.Message}");
                actorSpawnFailureLogged = true;
            }
        }
    }

    private void TryVerifyObjectCreation()
    {
        if ((EnabledFeatures & LiveFeature.ObjectCreation) == 0
            || objectCreationCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.ObjectCreation) == 0)
        {
            return;
        }

        var classHandle = context.Unreal.FindFirstOf("/Script/Engine.SceneComponent");
        if (classHandle.IsNull || !context.Unreal.IsValid(classHandle))
        {
            return;
        }
        var outer = context.Unreal.FindFirstOf("/Script/AIModule.Default__AIController");
        if (outer.IsNull || !context.Unreal.IsValid(outer))
        {
            return;
        }

        try
        {
            var created = context.Unreal.CreateObject(classHandle, outer, "RogueModLiveProbe");
            if (created.IsNull || !context.Unreal.IsValid(created))
            {
                throw new InvalidOperationException("created object is null or stale");
            }
            var createdClass = context.Unreal.GetClass(created);
            if (createdClass != classHandle)
            {
                throw new InvalidOperationException("created object has the wrong class");
            }
            var path = context.Unreal.GetPathName(created);
            if (path is null || !path.Contains("RogueModLiveProbe", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"created object path does not carry the requested name: {path ?? "<null>"}");
            }
            context.Unreal.Invoke(
                created,
                K2DestroyComponentFunction,
                [new UnrealArgument("Object", UnrealValue.From(created))]);
            objectCreationCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-CREATE PASS: {path} class={context.Unreal.GetPathName(createdClass)} valid=ok name=ok destroy=ok");
        }
        catch (Exception exception)
        {
            if (!objectCreationFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-CREATE rejected: {exception.Message}");
                objectCreationFailureLogged = true;
            }
        }
    }

    private void TryVerifyObjectPtr()
    {
        if ((EnabledFeatures & LiveFeature.ObjectPtr) == 0
            || objectPtrCompleted || context is null || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.ActorSpawning) == 0)
        {
            return;
        }

        var contextObject = context.Unreal.FindFirstOf("PlayerController");
        var actorClass = context.Unreal.FindFirstOf("/Script/Engine.Actor");
        if (contextObject.IsNull || !context.Unreal.IsValid(contextObject)
            || actorClass.IsNull || !context.Unreal.IsValid(actorClass))
        {
            return;
        }

        UnrealObjectHandle owner = default;
        UnrealObjectHandle alternate = default;
        try
        {
            owner = context.Unreal.SpawnActor(contextObject, actorClass, UnrealVector.Zero, UnrealRotator.Zero);
            alternate = context.Unreal.SpawnActor(contextObject, actorClass, UnrealVector.Zero, UnrealRotator.Zero);
            if (owner.IsNull || alternate.IsNull
                || !context.Unreal.IsValid(owner) || !context.Unreal.IsValid(alternate))
            {
                throw new InvalidOperationException("probe actors are null or stale");
            }
            VerifyObjectPtrSwapRoundTrip(owner, ActorOwnerProperty, alternate);
            objectPtrCompleted = true;
        }
        catch (Exception exception)
        {
            if (!objectPtrFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-TOBJECTPTR rejected: {exception.Message}");
                objectPtrFailureLogged = true;
            }
        }
        finally
        {
            if (!owner.IsNull && context.Unreal.IsValid(owner))
            {
                context.Unreal.Invoke(owner, K2DestroyActorFunction, []);
            }
            if (!alternate.IsNull && context.Unreal.IsValid(alternate))
            {
                context.Unreal.Invoke(alternate, K2DestroyActorFunction, []);
            }
        }
    }

    private void VerifyObjectPtrSwapRoundTrip(
        UnrealObjectHandle owner,
        UnrealPropertyDescriptor property,
        UnrealObjectHandle alternate)
    {
        var unreal = context!.Unreal;
        var original = unreal.ReadProperty(owner, property).AsObjectHandle();
        if (original == alternate || (!original.IsNull && !unreal.IsValid(original)))
        {
            throw new InvalidOperationException("property is stale or already equals the alternate target");
        }

        try
        {
            unreal.WriteProperty(owner, property, UnrealValue.From(alternate));
            var swapped = unreal.ReadProperty(owner, property).AsObjectHandle();
            if (swapped != alternate)
            {
                throw new InvalidOperationException(
                    $"write did not replace the value: expected={DescribeHandle(unreal, alternate)} " +
                    $"actual={DescribeHandle(unreal, swapped)}");
            }
        }
        finally
        {
            unreal.WriteProperty(owner, property, UnrealValue.From(original));
        }

        var restored = unreal.ReadProperty(owner, property).AsObjectHandle();
        if (restored != original || (!restored.IsNull && !unreal.IsValid(restored)))
        {
            throw new InvalidOperationException("original TObjectPtr value was not restored");
        }

        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-TOBJECTPTR PASS: {unreal.GetPathName(owner)} property={property.Name} " +
            $"original={DescribeHandle(unreal, restored)} swap={DescribeHandle(unreal, alternate)} swap=ok restore=ok");
    }

    private static string DescribeHandle(IUnrealReflection unreal, UnrealObjectHandle handle) =>
        handle.IsNull ? "<null>" : unreal.GetPathName(handle) ?? "<stale>";

    private void TryVerifyNameArrayResize()
    {
        if ((EnabledFeatures & LiveFeature.NameArray) == 0
            || nameArrayCompleted || context is null || !context.Unreal.IsAvailable)
        {
            return;
        }

        var handle = context.Unreal.FindFirstOf("PlayerController");
        if (!handle.IsNull && context.Unreal.IsValid(handle))
        {
            try
            {
                VerifyNameArrayResizeRoundTrip(handle);
                nameArrayCompleted = true;
            }
            catch (Exception exception)
            {
                if (!nameArrayFailureLogged)
                {
                    context.Logger.Log(
                        ModLogLevel.Debug,
                        $"LIVE-NAME-ARRAY candidate rejected: {context.Unreal.GetPathName(handle)}: {exception.Message}");
                    nameArrayFailureLogged = true;
                }
            }
        }
    }

    private void VerifyNameArrayResizeRoundTrip(UnrealObjectHandle owner)
    {
        var unreal = context!.Unreal;
        var original = ReadNameArray(unreal, owner);
        var replacement = original.Append(NameArrayMarker).ToArray();

        try
        {
            WriteNameArray(unreal, owner, replacement);
            var grown = ReadNameArray(unreal, owner);
            if (!grown.SequenceEqual(replacement, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"grown TArray<FName> mismatch: expected=[{string.Join(",", replacement)}] " +
                    $"actual=[{string.Join(",", grown)}]");
            }
        }
        finally
        {
            WriteNameArray(unreal, owner, original);
        }

        var restored = ReadNameArray(unreal, owner);
        if (!restored.SequenceEqual(original, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("original TArray<FName> was not restored");
        }

        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-NAME-ARRAY PASS: {unreal.GetPathName(owner)} original={original.Count} grown={replacement.Length} restore=ok");
    }

    private static IReadOnlyList<string> ReadNameArray(IUnrealReflection unreal, UnrealObjectHandle owner) =>
        UnrealArrayValue.ToList(
            unreal.ReadProperty(owner, ActorTagsProperty),
            static value => value.As<string>());

    private static void WriteNameArray(
        IUnrealReflection unreal,
        UnrealObjectHandle owner,
        IReadOnlyList<string> values) =>
        unreal.WriteProperty(
            owner,
            ActorTagsProperty,
            UnrealArrayValue.From(ActorTagsValue, values, UnrealValue.From));

    private void TryVerifyLazyReference()
    {
        if ((EnabledFeatures & LiveFeature.LazyReference) == 0
            || lazyCompleted
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
        if ((EnabledFeatures & LiveFeature.Optional) == 0
            || optionalCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.OptionalValues) == 0)
        {
            return;
        }

        var handle = context.Unreal.FindFirstOf("/Script/Niagara.Default__NiagaraSystem");
        if (!handle.IsNull && context.Unreal.IsValid(handle))
        {
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
        if ((EnabledFeatures & LiveFeature.WeakReference) == 0
            || weakCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.WeakObjectReferences) == 0)
        {
            return;
        }

        var handle = context.Unreal.FindFirstOf("ValGameInstance");
        if (!handle.IsNull && context.Unreal.IsValid(handle))
        {
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
