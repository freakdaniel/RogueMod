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
        Interface = 1 << 8,
        MapSet = 1 << 9,
        MapSetWrite = 1 << 10,
        NonPodStruct = 1 << 11,
        All = Optional | WeakReference | LazyReference | NameArray | ObjectPtr | ObjectCreation | ActorSpawn | SoftObject | Interface | MapSet | MapSetWrite | NonPodStruct
    }

    private const LiveFeature EnabledFeatures = LiveFeature.MapSet | LiveFeature.MapSetWrite | LiveFeature.NonPodStruct;
    private const int MaximumUpdateAttempts = 18_000;
    private const int GameplayObservationInterval = 60;
    private const string NameArrayMarker = "RogueModLiveProbe";

    private enum MapProbeShape
    {
        ObjectToInt,
        StringToObject,
        ByteToInt,
        NameToInt,
        IntToString
    }

    private readonly record struct ObservedObject(UnrealObjectHandle Handle, string Path);
    private readonly record struct MapProbeCandidate(
        string Name,
        string ClassName,
        UnrealPropertyDescriptor Property,
        MapProbeShape Shape);
    private readonly record struct MapProbeMatch(
        MapProbeCandidate Candidate,
        ObservedObject Owner,
        UnrealMapValue Value);
    private readonly record struct SetProbeCandidate(
        string Name,
        string ClassName,
        UnrealPropertyDescriptor Property,
        bool HasByteElements);
    private readonly record struct SetProbeMatch(
        SetProbeCandidate Candidate,
        ObservedObject Owner,
        UnrealSetValue Value);

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

    private static readonly UnrealPropertyDescriptor ChooserInputValueProperty = new(
        "/Script/Chooser.ChooserColumnBool",
        "InputValue",
        "InterfaceProperty:/Script/Chooser.ChooserParameterBool",
        48,
        1,
        "CPF_Edit | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_UObjectWrapper | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic",
        16);

    private static readonly UnrealStructDescriptor GameplayTagStruct = new(
        "/Script/GameplayTags.GameplayTag",
        8,
        4,
        [new("TagName", "NameProperty", 0, 8)]);

    private static readonly UnrealStructDescriptor WakeupSequenceDataStruct = new(
        "/Script/Valhalla.ValWakeupSequenceData",
        12,
        4,
        [
            new("bPlayWakeupSequence", "BoolProperty", 0, 1, 1, 0, 1, 255),
            new("WakeupSequenceTag", "StructProperty:/Script/GameplayTags.GameplayTag", 4, 8, Struct: GameplayTagStruct)
        ]);

    private static readonly UnrealPropertyDescriptor WakeupSequenceDataProperty = new(
        "/Script/Valhalla.ValPlayerController",
        "r_WakeupSequenceData",
        "StructProperty:/Script/Valhalla.ValWakeupSequenceData",
        3352,
        1,
        "CPF_BlueprintVisible | CPF_BlueprintReadOnly | CPF_Net | CPF_RepNotify | CPF_NoDestructor | CPF_Protected | CPF_NativeAccessSpecifierProtected",
        12,
        Struct: WakeupSequenceDataStruct);

    private static readonly MapProbeCandidate[] GameplayMapCandidates =
    [
        new(
            "EnhancedPlayerInput.AppliedInputContexts",
            "EnhancedPlayerInput",
            CreateMapProperty(
                "/Script/EnhancedInput.EnhancedPlayerInput",
                "AppliedInputContexts",
                1416,
                "CPF_Transient | CPF_NativeAccessSpecifierPrivate",
                "ObjectProperty:/Script/EnhancedInput.InputMappingContext",
                8,
                "IntProperty",
                4),
            MapProbeShape.ObjectToInt),
        new(
            "EnhancedInputUserSettings.SavedKeyProfilesMap",
            "EnhancedInputUserSettings",
            CreateMapProperty(
                "/Script/EnhancedInput.EnhancedInputUserSettings",
                "SavedKeyProfilesMap",
                216,
                "CPF_Transient | CPF_Protected | CPF_UObjectWrapper | CPF_NativeAccessSpecifierProtected | CPF_TObjectPtr",
                "StrProperty",
                16,
                "ObjectProperty:/Script/EnhancedInput.EnhancedPlayerMappableKeyProfile",
                8),
            MapProbeShape.StringToObject),
        new(
            "ValCharacter.OwnedSpawnedActorsLimit",
            "ValCharacter",
            CreateMapProperty(
                "/Script/Valhalla.ValCharacter",
                "OwnedSpawnedActorsLimit",
                8928,
                "CPF_Edit | CPF_BlueprintVisible | CPF_BlueprintReadOnly | CPF_DisableEditOnInstance | CPF_Protected | CPF_NativeAccessSpecifierProtected",
                "EnumProperty:/Script/Valhalla.EValOwnedSpawnType",
                1,
                "IntProperty",
                4),
            MapProbeShape.ByteToInt),
        new(
            "ValGameState_SpaceDungeon.NamedSeededRands",
            "ValGameState_SpaceDungeon",
            CreateMapProperty(
                "/Script/Valhalla.ValGameState_SpaceDungeon",
                "NamedSeededRands",
                2504,
                "CPF_Transient | CPF_Protected | CPF_NativeAccessSpecifierProtected",
                "NameProperty",
                8,
                "IntProperty",
                4),
            MapProbeShape.NameToInt),
        new(
            "ValGameState.PlayerIdToNameMap",
            "ValGameState",
            CreateMapProperty(
                "/Script/Valhalla.ValGameState",
                "PlayerIdToNameMap",
                1320,
                "CPF_Edit | CPF_EditConst | CPF_NativeAccessSpecifierPublic",
                "IntProperty",
                4,
                "StrProperty",
                16),
            MapProbeShape.IntToString)
    ];

    private static readonly SetProbeCandidate[] GameplaySetCandidates =
    [
        new(
            "UI_Game_Visor.CancelInputIDs",
            "UI_Game_Visor_C",
            CreateSetProperty(
                "/Game/UI/Components/Game/UI_Game_Visor.UI_Game_Visor_C",
                "CancelInputIDs",
                1672,
                "CPF_Edit | CPF_BlueprintVisible | CPF_DisableEditOnInstance",
                "EnumProperty:/Script/Valhalla.EValAbilityInputID",
                1),
            true),
        new(
            "EnhancedInputUserSettings.RegisteredMappingContexts",
            "EnhancedInputUserSettings",
            CreateSetProperty(
                "/Script/EnhancedInput.EnhancedInputUserSettings",
                "RegisteredMappingContexts",
                304,
                "CPF_Transient | CPF_Protected | CPF_UObjectWrapper | CPF_NativeAccessSpecifierProtected | CPF_TObjectPtr",
                "ObjectProperty:/Script/EnhancedInput.InputMappingContext",
                8),
            false),
        new(
            "WorldDataLayers.DataLayerInstances",
            "WorldDataLayers",
            CreateSetProperty(
                "/Script/Engine.WorldDataLayers",
                "DataLayerInstances",
                864,
                "CPF_UObjectWrapper | CPF_NativeAccessSpecifierPrivate | CPF_TObjectPtr",
                "ObjectProperty:/Script/Engine.DataLayerInstance",
                8),
            false),
        new(
            "World.ComponentsThatNeedPreEndOfFrameSync",
            "World",
            CreateSetProperty(
                "/Script/Engine.World",
                "ComponentsThatNeedPreEndOfFrameSync",
                712,
                "CPF_ExportObject | CPF_Transient | CPF_NonTransactional | CPF_ContainsInstancedReference | CPF_UObjectWrapper | CPF_NativeAccessSpecifierPrivate | CPF_TObjectPtr",
                "ObjectProperty:/Script/Engine.ActorComponent",
                8),
            false)
    ];

    private static UnrealPropertyDescriptor CreateSetProperty(
        string classPath,
        string propertyName,
        int offset,
        string flags,
        string elementType,
        int elementSize) =>
        new(
            classPath,
            propertyName,
            "SetProperty",
            offset,
            1,
            flags,
            80)
        {
            Set = new UnrealSetDescriptor(elementType, elementSize)
        };

    private static UnrealPropertyDescriptor CreateMapProperty(
        string classPath,
        string propertyName,
        int offset,
        string flags,
        string keyType,
        int keySize,
        string valueType,
        int valueSize) =>
        new(
            classPath,
            propertyName,
            "MapProperty",
            offset,
            1,
            flags,
            80)
        {
            Map = new UnrealMapDescriptor(keyType, keySize, valueType, valueSize)
        };

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
    private bool interfaceCompleted;
    private bool mapSetCompleted;
    private bool mapSetFailureLogged;
    private bool mapSetWriteCompleted;
    private bool mapSetWriteFailureLogged;
    private bool nonPodStructCompleted;
    private bool nonPodStructFailureLogged;
    private bool shipReady;
    private bool shipReadyLogged;
    private string? gameplayPhaseSignature;

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
                && IsComplete(LiveFeature.SoftObject, softObjectCompleted)
                && IsComplete(LiveFeature.Interface, interfaceCompleted)
                && IsComplete(LiveFeature.MapSet, mapSetCompleted)
                && IsComplete(LiveFeature.MapSetWrite, mapSetWriteCompleted)
                && IsComplete(LiveFeature.NonPodStruct, nonPodStructCompleted)))
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
            if (IsEnabled(LiveFeature.MapSet)
                && (updateAttempts == 1 || updateAttempts % GameplayObservationInterval == 0))
            {
                ObserveGameplayPhase();
            }
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
            if (IsEnabled(LiveFeature.Interface) && !interfaceCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-INTERFACE FAIL: no interface property read/write/restore round-trip completed");
            }
            if (IsEnabled(LiveFeature.MapSet) && !mapSetCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-MAP-SET FAIL: no TMap/TSet property read was verified");
            }
            if (IsEnabled(LiveFeature.MapSetWrite) && !mapSetWriteCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-MAP-SET-WRITE FAIL: no TMap/TSet write round-trip was verified");
            }
            if (IsEnabled(LiveFeature.NonPodStruct) && !nonPodStructCompleted)
            {
                context.Logger.Log(ModLogLevel.Error, "LIVE-NONPOD-STRUCT FAIL: no non-POD struct write round-trip was verified");
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
        switch (updateAttempts % 11)
        {
            case 0: TryVerifyOptional(); break;
            case 1: TryVerifyWeakReference(); break;
            case 2: TryVerifyLazyReference(); break;
            case 3: TryVerifyNameArrayResize(); break;
            case 4: TryVerifyObjectPtr(); break;
            case 5: TryVerifyObjectCreation(); break;
            case 6: TryVerifyActorSpawn(); break;
            case 7: TryVerifySoftObject(); break;
            case 8: TryVerifyInterface(); break;
            case 9: TryVerifyMapSet(); break;
            case 10: TryVerifyNonPodStruct(); break;
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

    private void ObserveGameplayPhase()
    {
        if (context is null || !context.Unreal.IsAvailable || shipReady)
        {
            return;
        }

        try
        {
            var unreal = context.Unreal;
            var worlds = FindRuntimeObjects(unreal, "World");
            var controllers = FindRuntimeObjects(unreal, "PlayerController");
            var gameStates = FindRuntimeObjects(unreal, "ValGameState");
            var characters = FindRuntimeObjects(unreal, "ValCharacter");
            var signature = string.Join(
                '|',
                FormatObservedObjects(worlds),
                FormatObservedObjects(controllers),
                FormatObservedObjects(gameStates),
                FormatObservedObjects(characters));

            if (!StringComparer.Ordinal.Equals(gameplayPhaseSignature, signature))
            {
                gameplayPhaseSignature = signature;
                context.Logger.Log(
                    ModLogLevel.Information,
                    $"LIVE-PHASE objects: worlds=[{FormatObservedObjects(worlds)}] " +
                    $"controllers=[{FormatObservedObjects(controllers)}] " +
                    $"gameStates=[{FormatObservedObjects(gameStates)}] " +
                    $"characters=[{FormatObservedObjects(characters)}]");
            }

            var startingShipWorld = worlds.FirstOrDefault(static value =>
                value.Path.Contains("StartingShip", StringComparison.Ordinal));
            var startingShipController = controllers.FirstOrDefault(static value =>
                value.Path.Contains("StartingShip", StringComparison.Ordinal));
            shipReady = !startingShipWorld.Handle.IsNull
                && !startingShipController.Handle.IsNull
                && gameStates.Count > 0
                && characters.Count > 0;
            if (shipReady && !shipReadyLogged)
            {
                shipReadyLogged = true;
                var character = characters[0];
                var persistentWorld = FindMatchingWorld(worlds, character);
                context.Logger.Log(
                    ModLogLevel.Information,
                    $"LIVE-PHASE SHIP-READY: world={persistentWorld.Path} " +
                    $"startingShipWorld={startingShipWorld.Path} " +
                    $"controller={startingShipController.Path} gameState={gameStates[0].Path} " +
                    $"character={character.Path}");
            }
        }
        catch (Exception exception)
        {
            context.Logger.Log(ModLogLevel.Error, $"LIVE-PHASE observation failed: {exception}");
            throw;
        }
    }

    private static IReadOnlyList<ObservedObject> FindRuntimeObjects(IUnrealReflection unreal, string className)
    {
        var objects = new List<ObservedObject>();
        foreach (var handle in unreal.FindAllOf(className))
        {
            if (handle.IsNull || !unreal.IsValid(handle))
            {
                continue;
            }

            var path = unreal.GetPathName(handle);
            if (string.IsNullOrEmpty(path) || path.Contains("Default__", StringComparison.Ordinal))
            {
                continue;
            }

            objects.Add(new(handle, path));
        }

        return objects
            .OrderBy(static value => value.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatObservedObjects(IReadOnlyList<ObservedObject> objects) =>
        objects.Count == 0
            ? "none"
            : string.Join(", ", objects.Take(4).Select(static value => value.Path))
                + (objects.Count > 4 ? $", ... ({objects.Count} total)" : string.Empty);

    private static ObservedObject FindMatchingWorld(
        IReadOnlyList<ObservedObject> worlds,
        ObservedObject observedObject)
    {
        var package = observedObject.Path.Split('.', 2)[0];
        var world = worlds.FirstOrDefault(candidate =>
            candidate.Path.StartsWith(package, StringComparison.Ordinal));
        return world.Handle.IsNull ? worlds[0] : world;
    }

    private void TryVerifyMapSet()
    {
        if ((EnabledFeatures & LiveFeature.MapSet) == 0
            || mapSetCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || !shipReady
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.MapSetProperties) == 0)
        {
            return;
        }

        try
        {
            var unreal = context.Unreal;
            var mapMatch = FindFirstNonEmptyGameplayMap(unreal, out var mapObservations);
            var setMatch = FindFirstNonEmptyGameplaySet(unreal, out var setObservations);
            if (mapMatch is null || setMatch is null)
            {
                throw new InvalidOperationException(
                    $"gameplay containers were not both available: maps=[{mapObservations}] sets=[{setObservations}]");
            }

            var (mapCandidate, mapOwner, mapValue) = mapMatch.Value;
            var (setCandidate, setOwner, setValue) = setMatch.Value;

            if (IsEnabled(LiveFeature.MapSetWrite) && !mapSetWriteCompleted)
            {
                TryVerifyMapSetWriteRoundTrip(mapCandidate, mapOwner, mapValue, setCandidate, setOwner, setValue);
            }

            mapSetCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-MAP-SET PASS: map={mapCandidate.Name} count={mapValue.Entries.Count} " +
                $"on {mapOwner.Path}; set={setCandidate.Name} count={setValue.Elements.Count} " +
                $"on {setOwner.Path}");
        }
        catch (Exception exception)
        {
            if (!mapSetFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-MAP-SET candidate rejected: {exception.Message}");
                mapSetFailureLogged = true;
            }
        }
    }

    private void TryVerifyMapSetWriteRoundTrip(
        MapProbeCandidate mapCandidate,
        ObservedObject mapOwner,
        UnrealMapValue mapValue,
        SetProbeCandidate setCandidate,
        ObservedObject setOwner,
        UnrealSetValue setValue)
    {
        if (context is null
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.MapSetWrites) == 0)
        {
            return;
        }

        try
        {
            var unreal = context.Unreal;
            // Writing the value twice forces the second write to construct and destroy a
            // bridge-built FScriptMap/FScriptSet, exercising the full build/swap/destroy path
            // that the read-only path never touches. The logical contents are unchanged.
            unreal.WriteProperty(mapOwner.Handle, mapCandidate.Property, UnrealValue.From(mapValue));
            unreal.WriteProperty(mapOwner.Handle, mapCandidate.Property, UnrealValue.From(mapValue));
            var mapAfterWrite = unreal.ReadProperty(mapOwner.Handle, mapCandidate.Property).As<UnrealMapValue>();
            if (mapAfterWrite.Entries.Count != mapValue.Entries.Count)
            {
                throw new InvalidOperationException(
                    $"map write changed the entry count: expected {mapValue.Entries.Count} actual {mapAfterWrite.Entries.Count}");
            }

            unreal.WriteProperty(setOwner.Handle, setCandidate.Property, UnrealValue.From(setValue));
            unreal.WriteProperty(setOwner.Handle, setCandidate.Property, UnrealValue.From(setValue));
            var setAfterWrite = unreal.ReadProperty(setOwner.Handle, setCandidate.Property).As<UnrealSetValue>();
            if (setAfterWrite.Elements.Count != setValue.Elements.Count)
            {
                throw new InvalidOperationException(
                    $"set write changed the element count: expected {setValue.Elements.Count} actual {setAfterWrite.Elements.Count}");
            }

            mapSetWriteCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-MAP-SET-WRITE PASS: map={mapCandidate.Name} on {mapOwner.Path}; " +
                $"set={setCandidate.Name} on {setOwner.Path} build=ok swap=ok destroy=ok");
        }
        catch (Exception exception)
        {
            if (!mapSetWriteFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-MAP-SET-WRITE rejected: {exception.Message}");
                mapSetWriteFailureLogged = true;
            }
        }
    }

    private void TryVerifyNonPodStruct()
    {
        if ((EnabledFeatures & LiveFeature.NonPodStruct) == 0
            || nonPodStructCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || !shipReady)
        {
            return;
        }

        var controller = context.Unreal.FindFirstOf("PlayerController");
        if (controller.IsNull || !context.Unreal.IsValid(controller))
        {
            return;
        }

        try
        {
            var unreal = context.Unreal;
            var original = unreal.ReadProperty(controller, WakeupSequenceDataProperty).As<UnrealStructValue>();
            var originalBool = original.GetField("bPlayWakeupSequence").As<bool>();
            var originalTag = ReadGameplayTag(original);

            // Write the value back twice: the second write constructs and destroys a
            // bridge-built struct, exercising the field-wise build/swap/destroy path that the
            // raw-byte POD transport never used.
            unreal.WriteProperty(controller, WakeupSequenceDataProperty, UnrealValue.From(original));
            unreal.WriteProperty(controller, WakeupSequenceDataProperty, UnrealValue.From(original));

            var after = unreal.ReadProperty(controller, WakeupSequenceDataProperty).As<UnrealStructValue>();
            if (after.GetField("bPlayWakeupSequence").As<bool>() != originalBool
                || !StringComparer.Ordinal.Equals(ReadGameplayTag(after), originalTag))
            {
                throw new InvalidOperationException(
                    $"non-POD struct did not survive a write/read round-trip: original=({originalBool}, {originalTag}) " +
                    $"actual=({after.GetField("bPlayWakeupSequence").As<bool>()}, {ReadGameplayTag(after)})");
            }

            nonPodStructCompleted = true;
            context.Logger.Log(
                ModLogLevel.Information,
                $"LIVE-NONPOD-STRUCT PASS: {unreal.GetPathName(controller)} property=r_WakeupSequenceData " +
                $"bPlayWakeupSequence={originalBool} tag={originalTag} build=ok swap=ok destroy=ok");
        }
        catch (Exception exception)
        {
            if (!nonPodStructFailureLogged)
            {
                context.Logger.Log(ModLogLevel.Debug, $"LIVE-NONPOD-STRUCT rejected: {exception.Message}");
                nonPodStructFailureLogged = true;
            }
        }
    }

    private static string ReadGameplayTag(UnrealStructValue value) =>
        value.GetField("WakeupSequenceTag").As<UnrealStructValue>().GetField("TagName").As<string>();

    private static MapProbeMatch? FindFirstNonEmptyGameplayMap(
        IUnrealReflection unreal,
        out string observationsText)
    {
        var observations = new List<string>();
        foreach (var candidate in GameplayMapCandidates)
        {
            var owners = FindRuntimeObjects(unreal, candidate.ClassName);
            if (owners.Count == 0)
            {
                observations.Add($"{candidate.Name}=unavailable");
                continue;
            }

            foreach (var owner in owners)
            {
                try
                {
                    var value = unreal.ReadProperty(owner.Handle, candidate.Property).As<UnrealMapValue>();
                    DecodeMapEntries(candidate, value);
                    observations.Add($"{candidate.Name}@{owner.Path}={value.Entries.Count}");
                    if (value.Entries.Count > 0)
                    {
                        observationsText = string.Join("; ", observations);
                        return new(candidate, owner, value);
                    }
                }
                catch (InvalidOperationException exception)
                {
                    observations.Add($"{candidate.Name}@{owner.Path}=read-error({exception.Message})");
                }
            }
        }

        observationsText = string.Join("; ", observations);
        return null;
    }

    private static void DecodeMapEntries(MapProbeCandidate candidate, UnrealMapValue value)
    {
        foreach (var pair in value.Entries)
        {
            switch (candidate.Shape)
            {
                case MapProbeShape.ObjectToInt:
                    _ = pair.Key.As<UnrealObjectHandle>();
                    _ = pair.Value.As<int>();
                    break;
                case MapProbeShape.StringToObject:
                    _ = pair.Key.As<string>();
                    _ = pair.Value.As<UnrealObjectHandle>();
                    break;
                case MapProbeShape.ByteToInt:
                    _ = pair.Key.As<byte>();
                    _ = pair.Value.As<int>();
                    break;
                case MapProbeShape.NameToInt:
                    _ = pair.Key.As<string>();
                    _ = pair.Value.As<int>();
                    break;
                case MapProbeShape.IntToString:
                    _ = pair.Key.As<int>();
                    _ = pair.Value.As<string>();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(candidate));
            }
        }
    }

    private static SetProbeMatch? FindFirstNonEmptyGameplaySet(
        IUnrealReflection unreal,
        out string observationsText)
    {
        var observations = new List<string>();
        foreach (var candidate in GameplaySetCandidates)
        {
            var owners = FindRuntimeObjects(unreal, candidate.ClassName);
            if (owners.Count == 0)
            {
                observations.Add($"{candidate.Name}=unavailable");
                continue;
            }

            foreach (var owner in owners)
            {
                try
                {
                    var value = unreal.ReadProperty(owner.Handle, candidate.Property).As<UnrealSetValue>();
                    foreach (var element in value.Elements)
                    {
                        if (candidate.HasByteElements)
                        {
                            _ = element.As<byte>();
                        }
                        else
                        {
                            _ = element.As<UnrealObjectHandle>();
                        }
                    }

                    observations.Add($"{candidate.Name}@{owner.Path}={value.Elements.Count}");
                    if (value.Elements.Count > 0)
                    {
                        observationsText = string.Join("; ", observations);
                        return new(candidate, owner, value);
                    }
                }
                catch (InvalidOperationException exception)
                {
                    observations.Add($"{candidate.Name}@{owner.Path}=read-error({exception.Message})");
                }
            }
        }

        observationsText = string.Join("; ", observations);
        return null;
    }

    private void TryVerifyInterface()
    {
        if ((EnabledFeatures & LiveFeature.Interface) == 0
            || interfaceCompleted
            || context is null
            || !context.Unreal.IsAvailable
            || (context.Unreal.Capabilities & UnrealReflectionCapabilities.InterfaceReferences) == 0)
        {
            return;
        }

        // Write verification targets a freshly created inert ChooserColumnBool whose InputValue
        // interface property is never consumed by gameplay, so a misbehaving engine setter
        // cannot corrupt an actively used reference.
        var unreal = context.Unreal;
        var classHandle = unreal.FindFirstOf("/Script/Chooser.ChooserColumnBool");
        var outer = unreal.FindFirstOf("/Script/AIModule.Default__AIController");
        if (classHandle.IsNull || !unreal.IsValid(classHandle)
            || outer.IsNull || !unreal.IsValid(outer))
        {
            return;
        }

        try
        {
            VerifyInterfaceRoundTrip();
            interfaceCompleted = true;
        }
        catch (Exception exception)
        {
            context.Logger.Log(ModLogLevel.Debug, $"LIVE-INTERFACE rejected: {exception.Message}");
        }
    }

    private void VerifyInterfaceRoundTrip()
    {
        var unreal = context!.Unreal;
        var classHandle = unreal.FindFirstOf("/Script/Chooser.ChooserColumnBool");
        var outer = unreal.FindFirstOf("/Script/AIModule.Default__AIController");
        var created = unreal.CreateObject(classHandle, outer, "RogueModLiveProbeInterface");
        if (created.IsNull || !unreal.IsValid(created))
        {
            throw new InvalidOperationException("created ChooserColumnBool is null or stale");
        }

        var original = unreal.ReadProperty(created, ChooserInputValueProperty).AsObjectHandle();
        var originalText = DescribeHandle(unreal, original);

        // SET: write the property's own value back. A self-referential InputValue already
        // carries an object that (if the class implements the interface) the engine accepts,
        // confirming the SetInterfacePropertyByName path lands.
        if (!original.IsNull)
        {
            unreal.WriteProperty(created, ChooserInputValueProperty, UnrealValue.From(original));
            var setBack = unreal.ReadProperty(created, ChooserInputValueProperty).AsObjectHandle();
            if (setBack != original)
            {
                throw new InvalidOperationException(
                    $"interface set did not stick: original={originalText} actual={DescribeHandle(unreal, setBack)}");
            }
        }

        // CLEAR: a null interface reference is written directly. SetInterfacePropertyByName
        // skips null values in UE5, so the bridge zeroes the FScriptInterface slot.
        unreal.WriteProperty(created, ChooserInputValueProperty, UnrealValue.From(UnrealObjectHandle.Null));
        var cleared = unreal.ReadProperty(created, ChooserInputValueProperty).AsObjectHandle();
        if (!cleared.IsNull)
        {
            throw new InvalidOperationException(
                $"interface clear did not stick: actual={DescribeHandle(unreal, cleared)}");
        }

        unreal.WriteProperty(created, ChooserInputValueProperty, UnrealValue.From(original));
        var restored = unreal.ReadProperty(created, ChooserInputValueProperty).AsObjectHandle();
        if (restored != original)
        {
            throw new InvalidOperationException("original interface reference was not restored");
        }

        context.Logger.Log(
            ModLogLevel.Information,
            $"LIVE-INTERFACE PASS: {unreal.GetPathName(created)} property={ChooserInputValueProperty.Name} " +
            $"original={originalText} set=ok clear=ok restore=ok");
    }

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
