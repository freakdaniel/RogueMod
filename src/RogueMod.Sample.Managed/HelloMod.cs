using RogueMod.Abstractions;

namespace RogueMod.Sample.Managed;

public sealed class HelloMod : IRogueMod, IRogueModGameEvents
{
    private IModLogger? _logger;
    private IUnrealReflection? _unreal;
    private bool _firstUpdateLogged;
    private bool _reflectionLogged;
    private bool _invocationLogged;
    private bool _stringSmokeLogged;
    private bool _textSmokeLogged;
    private bool _nonEmptyArraySmokeLogged;
    private bool _objectEnumerationLogged;
    private bool _mutateAddYawInput;
    private bool _mutateCanRestartPlayer;
    private bool _nonMatchingInstanceHookInvoked;
    private int _addYawInputHookStage;
    private IDisposable? _addYawInputHook;
    private IDisposable? _addYawInputHighPriorityHook;
    private IDisposable? _nonMatchingInstanceHook;
    private IDisposable? _canRestartPlayerHook;

    public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
    {
        _logger = context.Logger;
        _unreal = context.Unreal;
        _logger.Log(ModLogLevel.Information, $"Hello from {context.ModId} on {context.GameProfileId}.");
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        _addYawInputHook?.Dispose();
        _addYawInputHook = null;
        _addYawInputHighPriorityHook?.Dispose();
        _addYawInputHighPriorityHook = null;
        _nonMatchingInstanceHook?.Dispose();
        _nonMatchingInstanceHook = null;
        _canRestartPlayerHook?.Dispose();
        _canRestartPlayerHook = null;
        _logger?.Log(ModLogLevel.Information, "Hello managed mod unloaded.");
        _logger = null;
        _unreal = null;
        _firstUpdateLogged = false;
        _reflectionLogged = false;
        _invocationLogged = false;
        _stringSmokeLogged = false;
        _textSmokeLogged = false;
        _nonEmptyArraySmokeLogged = false;
        _objectEnumerationLogged = false;
        _mutateAddYawInput = false;
        _mutateCanRestartPlayer = false;
        _nonMatchingInstanceHookInvoked = false;
        _addYawInputHookStage = 0;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind)
    {
        if (eventKind == ModGameEventKind.Update)
        {
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                _logger?.Log(ModLogLevel.Information, $"Game event: {eventKind}.");
            }
        }
        else
        {
            _logger?.Log(ModLogLevel.Information, $"Game event: {eventKind}.");
        }

        if (eventKind == ModGameEventKind.Update && !_reflectionLogged && _unreal?.IsAvailable == true)
        {
            var playerController = _unreal.FindFirstOf("PlayerController");
            if (!playerController.IsNull && _unreal.IsValid(playerController))
            {
                try
                {
                    var objectPath = _unreal.GetPathName(playerController) ?? "<unknown>";
                    var classPath = _unreal.GetPathName(_unreal.GetClass(playerController)) ?? "<unknown>";
                    _logger?.Log(
                        ModLogLevel.Information,
                        $"Unreal reflection: PlayerController={objectPath}; Class={classPath}.");
                    _reflectionLogged = true;

                    if (!_objectEnumerationLogged
                        && (_unreal.Capabilities & UnrealReflectionCapabilities.ObjectEnumeration) != 0)
                    {
                        var controllers = _unreal.FindAll<PlayerControllerSdk>();
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"Typed object enumeration succeeded: PlayerController.Count={controllers.Count}.");
                        _objectEnumerationLogged = true;
                    }

                    if (!_invocationLogged
                        && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionInvocation) != 0)
                    {
                        var controller = new PlayerControllerSdk(_unreal, playerController);
                        if (_addYawInputHook is null
                            && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionHooks) != 0)
                        {
                            _addYawInputHook = PlayerControllerSdk.RegisterAddYawInputPreHook(
                                _unreal,
                                (PlayerControllerSdk _, ref float value) =>
                                {
                                    if (_mutateAddYawInput)
                                    {
                                        if (_addYawInputHookStage != 1 || value != 0.5f)
                                        {
                                            throw new InvalidOperationException("UFunction hooks did not run in descending priority order.");
                                        }
                                        var original = value;
                                        value = 0.0f;
                                        _addYawInputHookStage = 2;
                                        _logger?.Log(
                                            ModLogLevel.Information,
                                            $"Typed priority-0 pre-hook chained: PlayerController.AddYawInput({original}) -> {value}.");
                                    }
                                },
                                new UnrealHookOptions(Priority: 0, Instance: playerController));
                            _addYawInputHighPriorityHook = PlayerControllerSdk.RegisterAddYawInputPreHook(
                                _unreal,
                                (PlayerControllerSdk _, ref float value) =>
                                {
                                    if (_mutateAddYawInput)
                                    {
                                        if (_addYawInputHookStage != 0 || value != 0.25f)
                                        {
                                            throw new InvalidOperationException("The high-priority UFunction hook received an unexpected value.");
                                        }
                                        var original = value;
                                        value = 0.5f;
                                        _addYawInputHookStage = 1;
                                        _logger?.Log(
                                            ModLogLevel.Information,
                                            $"Typed priority-100 pre-hook ran first: PlayerController.AddYawInput({original}) -> {value}.");
                                    }
                                },
                                new UnrealHookOptions(Priority: 100, Instance: playerController));
                            var nonMatchingInstance = _unreal.GetClass(playerController);
                            _nonMatchingInstanceHook = PlayerControllerSdk.RegisterAddYawInputPreHook(
                                _unreal,
                                (PlayerControllerSdk _, ref float _) => _nonMatchingInstanceHookInvoked = true,
                                new UnrealHookOptions(Priority: int.MaxValue, Instance: nonMatchingInstance));
                            _logger?.Log(
                                ModLogLevel.Information,
                                "Typed instance-filtered UFunction pre-hook chain registered: PlayerController.AddYawInput.");
                        }
                        if (_canRestartPlayerHook is null
                            && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionHooks) != 0)
                        {
                            _canRestartPlayerHook = PlayerControllerSdk.RegisterCanRestartPlayerPostHook(
                                _unreal,
                                (PlayerControllerSdk _, ref bool value) =>
                                {
                                    if (_mutateCanRestartPlayer)
                                    {
                                        var original = value;
                                        value = false;
                                        _logger?.Log(
                                            ModLogLevel.Information,
                                            $"Typed post-hook mutation requested: PlayerController.CanRestartPlayer() {original} -> {value}.");
                                    }
                                },
                                new UnrealHookOptions(Priority: 50, Instance: playerController));
                            _logger?.Log(
                                ModLogLevel.Information,
                                "Typed instance-filtered UFunction post-hook registered: PlayerController.CanRestartPlayer.");
                        }
                        controller.ResetControllerLightColor();
                        _logger?.Log(ModLogLevel.Information, "Typed UFunction call succeeded: PlayerController.ResetControllerLightColor().");
                        _mutateAddYawInput = true;
                        _addYawInputHookStage = 0;
                        try
                        {
                            controller.AddYawInput(0.25f);
                        }
                        finally
                        {
                            _mutateAddYawInput = false;
                        }
                        if (_addYawInputHookStage != 2)
                        {
                            throw new InvalidOperationException("The ordered UFunction pre-hook chain did not complete.");
                        }
                        if (_nonMatchingInstanceHookInvoked)
                        {
                            throw new InvalidOperationException("A UFunction hook ran for an object outside its instance filter.");
                        }
                        _logger?.Log(
                            ModLogLevel.Information,
                            "Typed ordered pre-hook chain completed: PlayerController.AddYawInput(0.25f) -> 0.5f -> 0.0f.");
                        _logger?.Log(
                            ModLogLevel.Information,
                            "Typed native instance filter rejected a valid non-matching UObject handle.");
                        bool canRestartPlayer;
                        _mutateCanRestartPlayer = true;
                        try
                        {
                            canRestartPlayer = controller.CanRestartPlayer();
                        }
                        finally
                        {
                            _mutateCanRestartPlayer = false;
                        }
                        if (canRestartPlayer)
                        {
                            throw new InvalidOperationException("Typed post-hook return mutation did not reach the calling wrapper.");
                        }
                        _logger?.Log(
                            ModLogLevel.Information,
                            "Typed post-hook return mutation completed: PlayerController.CanRestartPlayer()=False.");
                        var fullTickWhenPaused = controller.bShouldPerformFullTickWhenPaused;
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"Typed property read succeeded: PlayerController.bShouldPerformFullTickWhenPaused={fullTickWhenPaused}.");
                        controller.bShouldPerformFullTickWhenPaused = fullTickWhenPaused;
                        _logger?.Log(
                            ModLogLevel.Information,
                            "Typed property write succeeded: PlayerController.bShouldPerformFullTickWhenPaused was preserved.");
                        var controllerState = controller.StateName;
                        _logger?.Log(ModLogLevel.Information, $"FName property read succeeded: StateName={controllerState}.");
                        var attachSocket = controller.GetAttachParentSocketName();
                        _logger?.Log(ModLogLevel.Information, $"FName return marshalling succeeded: AttachSocket={attachSocket}.");
                        var hasSmokeTag = controller.ActorHasTag("RogueModSmokeTest");
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"FName input marshalling succeeded: ActorHasTag(RogueModSmokeTest)={hasSmokeTag}.");
                        var actorScale = controller.GetActorScale3D();
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"POD struct return marshalling succeeded: ActorScale3D=({actorScale.X}, {actorScale.Y}, {actorScale.Z}).");
                        controller.SetActorScale3D(actorScale);
                        _logger?.Log(
                            ModLogLevel.Information,
                            "POD struct input marshalling succeeded: ActorScale3D was preserved.");
                        var hiddenActors = controller.HiddenActors;
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"TArray property read succeeded: HiddenActors.Count={hiddenActors.Count}.");
                        controller.HiddenActors = hiddenActors;
                        _logger?.Log(
                            ModLogLevel.Information,
                            "TArray property write succeeded: HiddenActors was preserved.");
                        var childActors = controller.GetAllChildActors(includeDescendants: false);
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"TArray UFunction output marshalling succeeded: ChildActors.Count={childActors.Count}.");
                        _invocationLogged = true;
                    }
                }
                catch (InvalidOperationException) when (!_unreal.IsValid(playerController))
                {
                    _reflectionLogged = false;
                }
            }
        }

        if (eventKind == ModGameEventKind.Update
            && !_stringSmokeLogged
            && _unreal?.IsAvailable == true
            && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionInvocation) != 0)
        {
            var playerState = _unreal.FindFirstOf("PlayerState");
            if (!playerState.IsNull && _unreal.IsValid(playerState))
            {
                try
                {
                    var state = new PlayerStateSdk(_unreal, playerState);
                    _logger?.Log(ModLogLevel.Information, $"FString marshalling succeeded: PlayerName={state.GetPlayerName()}.");
                    _stringSmokeLogged = true;
                }
                catch (InvalidOperationException) when (!_unreal.IsValid(playerState))
                {
                    // The next Update will discover the replacement PlayerState.
                }
            }
        }

        if (eventKind == ModGameEventKind.Update
            && !_textSmokeLogged
            && _unreal?.IsAvailable == true
            && (_unreal.Capabilities & (UnrealReflectionCapabilities.PropertyRead | UnrealReflectionCapabilities.PropertyWrite))
                == (UnrealReflectionCapabilities.PropertyRead | UnrealReflectionCapabilities.PropertyWrite))
        {
            var textBlock = _unreal.FindFirstOf("TextBlock");
            if (!textBlock.IsNull && _unreal.IsValid(textBlock))
            {
                try
                {
                    var block = new TextBlockSdk(_unreal, textBlock);
                    var text = block.Text;
                    _logger?.Log(ModLogLevel.Information, $"FText property read succeeded: Text={text}.");
                    block.Text = text;
                    _logger?.Log(ModLogLevel.Information, "FText property write succeeded: Text was preserved.");
                    _textSmokeLogged = true;
                }
                catch (InvalidOperationException) when (!_unreal.IsValid(textBlock))
                {
                    // The next Update will discover a live widget instance.
                }
            }
        }

        if (eventKind == ModGameEventKind.Update
            && !_nonEmptyArraySmokeLogged
            && _unreal?.IsAvailable == true
            && (_unreal.Capabilities & (UnrealReflectionCapabilities.FunctionInvocation
                                        | UnrealReflectionCapabilities.PropertyRead
                                        | UnrealReflectionCapabilities.PropertyWrite))
                == (UnrealReflectionCapabilities.FunctionInvocation
                    | UnrealReflectionCapabilities.PropertyRead
                    | UnrealReflectionCapabilities.PropertyWrite))
        {
            var panelWidget = _unreal.FindFirstOf("PanelWidget");
            if (!panelWidget.IsNull && _unreal.IsValid(panelWidget))
            {
                try
                {
                    var panel = new PanelWidgetSdk(_unreal, panelWidget);
                    var slots = panel.Slots;
                    if (slots.Count > 0)
                    {
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"Non-empty TArray property read succeeded: PanelWidget.Slots.Count={slots.Count}.");
                        var children = panel.GetAllChildren();
                        _logger?.Log(
                            ModLogLevel.Information,
                            $"Non-empty TArray return marshalling succeeded: PanelWidget.GetAllChildren().Count={children.Count}.");
                        _nonEmptyArraySmokeLogged = children.Count > 0;
                    }
                }
                catch (InvalidOperationException) when (!_unreal.IsValid(panelWidget))
                {
                    // Retry after the widget tree finishes rebuilding.
                }
            }
        }
    }

    private sealed class PlayerControllerSdk(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle), IUnrealObjectType<PlayerControllerSdk>
    {
        static string IUnrealObjectType<PlayerControllerSdk>.UnrealClassName => "PlayerController";

        static PlayerControllerSdk IUnrealObjectType<PlayerControllerSdk>.Create(
            IUnrealReflection unreal,
            UnrealObjectHandle handle) => new(unreal, handle);

        private static readonly UnrealFunctionDescriptor ResetControllerLightColorFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:ResetControllerLightColor",
            "ResetControllerLightColor",
            "FUNC_Final | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable");
        private static readonly UnrealFunctionDescriptor AddYawInputFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:AddYawInput",
            "AddYawInput",
            "FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable",
            [new("Val", "FloatProperty", 0, 1, "CPF_Parm", 4)]);
        private static readonly UnrealFunctionDescriptor CanRestartPlayerFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:CanRestartPlayer",
            "CanRestartPlayer",
            "FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable",
            [new(
                "ReturnValue",
                "BoolProperty",
                0,
                1,
                "CPF_Parm | CPF_OutParm | CPF_ReturnParm",
                1,
                0,
                1,
                255)]);
        private static readonly UnrealFunctionDescriptor GetAttachParentSocketNameFunction = new(
            "/Script/Engine.Actor",
            "/Script/Engine.Actor:GetAttachParentSocketName",
            "GetAttachParentSocketName",
            "FUNC_Final | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [new(
                "ReturnValue",
                "NameProperty",
                0,
                1,
                "CPF_Parm | CPF_OutParm | CPF_ReturnParm",
                8)]);
        private static readonly UnrealFunctionDescriptor ActorHasTagFunction = new(
            "/Script/Engine.Actor",
            "/Script/Engine.Actor:ActorHasTag",
            "ActorHasTag",
            "FUNC_Final | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [
                new("Tag", "NameProperty", 0, 1, "CPF_Parm", 8),
                new("ReturnValue", "BoolProperty", 8, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 1, 0, 1, 255)
            ]);
        private static readonly UnrealFunctionDescriptor GetActorScale3DFunction = new(
            "/Script/Engine.Actor",
            "/Script/Engine.Actor:GetActorScale3D",
            "GetActorScale3D",
            "FUNC_Final | FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_HasDefaults | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [new(
                "ReturnValue",
                "StructProperty:/Script/CoreUObject.Vector",
                0,
                1,
                "CPF_Parm | CPF_OutParm | CPF_ZeroConstructor | CPF_ReturnParm | CPF_IsPlainOldData | CPF_NoDestructor | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic",
                24,
                Struct: Vector.Descriptor)]);
        private static readonly UnrealFunctionDescriptor SetActorScale3DFunction = new(
            "/Script/Engine.Actor",
            "/Script/Engine.Actor:SetActorScale3D",
            "SetActorScale3D",
            "FUNC_Final | FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_HasDefaults | FUNC_BlueprintCallable",
            [new(
                "NewScale3D",
                "StructProperty:/Script/CoreUObject.Vector",
                0,
                1,
                "CPF_Parm | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic",
                24,
                Struct: Vector.Descriptor)]);
        private static readonly UnrealArrayDescriptor ActorArrayDescriptor =
            new("ObjectProperty:/Script/Engine.Actor", 8);
        private static readonly UnrealPropertyDescriptor HiddenActorsProperty = new(
            "/Script/Engine.PlayerController",
            "HiddenActors",
            "ArrayProperty",
            952,
            1,
            "CPF_ZeroConstructor | CPF_UObjectWrapper | CPF_NativeAccessSpecifierPublic | CPF_TObjectPtr",
            16,
            Array: ActorArrayDescriptor);
        private static readonly UnrealFunctionDescriptor GetAllChildActorsFunction = new(
            "/Script/Engine.Actor",
            "/Script/Engine.Actor:GetAllChildActors",
            "GetAllChildActors",
            "FUNC_Final | FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_HasOutParms | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [
                new(
                    "ChildActors",
                    "ArrayProperty",
                    0,
                    1,
                    "CPF_Parm | CPF_OutParm | CPF_ZeroConstructor | CPF_NativeAccessSpecifierPublic",
                    16,
                    Array: ActorArrayDescriptor),
                new(
                    "bIncludeDescendants",
                    "BoolProperty",
                    16,
                    1,
                    "CPF_Parm | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor | CPF_HasGetValueTypeHash | CPF_NativeAccessSpecifierPublic",
                    1,
                    0,
                    1,
                    255)
            ]);
        private static readonly UnrealPropertyDescriptor FullTickWhenPausedProperty = new(
            "/Script/Engine.PlayerController",
            "bShouldPerformFullTickWhenPaused",
            "BoolProperty",
            1568,
            1,
            "CPF_Protected");
        private static readonly UnrealPropertyDescriptor StateNameProperty = new(
            "/Script/Engine.Controller",
            "StateName",
            "NameProperty",
            744,
            1,
            "CPF_NativeAccessSpecifierPublic",
            8);

        public void ResetControllerLightColor() => Call(ResetControllerLightColorFunction);

        public void AddYawInput(float value) =>
            Call(AddYawInputFunction, new UnrealArgument("Val", UnrealValue.From(value)));

        public delegate void AddYawInputPreHookHandler(PlayerControllerSdk context, ref float value);

        public static IDisposable RegisterAddYawInputPreHook(
            IUnrealReflection unreal,
            AddYawInputPreHookHandler callback,
            UnrealHookOptions options = default)
        {
            ArgumentNullException.ThrowIfNull(unreal);
            ArgumentNullException.ThrowIfNull(callback);
            return unreal.RegisterHook(
                AddYawInputFunction,
                UnrealHookPhase.Pre,
                options,
                hook =>
                {
                    var value = hook.Arguments["Val"].As<float>();
                    var original = value;
                    callback(new PlayerControllerSdk(unreal, hook.Object), ref value);
                    if (value != original)
                    {
                        hook.SetArgument("Val", UnrealValue.From(value));
                    }
                });
        }

        public bool CanRestartPlayer() => Call(CanRestartPlayerFunction).ReturnValue.As<bool>();

        public delegate void CanRestartPlayerPostHookHandler(PlayerControllerSdk context, ref bool returnValue);

        public static IDisposable RegisterCanRestartPlayerPostHook(
            IUnrealReflection unreal,
            CanRestartPlayerPostHookHandler callback,
            UnrealHookOptions options = default)
        {
            ArgumentNullException.ThrowIfNull(unreal);
            ArgumentNullException.ThrowIfNull(callback);
            return unreal.RegisterHook(
                CanRestartPlayerFunction,
                UnrealHookPhase.Post,
                options,
                hook =>
                {
                    var returnValue = hook.Result.ReturnValue.As<bool>();
                    var original = returnValue;
                    callback(new PlayerControllerSdk(unreal, hook.Object), ref returnValue);
                    if (returnValue != original)
                    {
                        hook.SetReturnValue(UnrealValue.From(returnValue));
                    }
                });
        }

        public string GetAttachParentSocketName() =>
            Call(GetAttachParentSocketNameFunction).ReturnValue.As<string>();

        public bool ActorHasTag(string tag) =>
            Call(ActorHasTagFunction, new UnrealArgument("Tag", UnrealValue.From(tag))).ReturnValue.As<bool>();

        public Vector GetActorScale3D() =>
            Vector.FromUnrealValue(Call(GetActorScale3DFunction).ReturnValue);

        public void SetActorScale3D(Vector value) =>
            Call(SetActorScale3DFunction, new UnrealArgument("NewScale3D", value.ToUnrealValue()));

        public IReadOnlyList<UnrealObjectHandle> HiddenActors
        {
            get => UnrealArrayValue.ToList<UnrealObjectHandle>(
                ReadValue(HiddenActorsProperty),
                value => value.AsObjectHandle());
            set => WriteValue(
                HiddenActorsProperty,
                UnrealArrayValue.From(ActorArrayDescriptor, value, UnrealValue.From));
        }

        public IReadOnlyList<UnrealObjectHandle> GetAllChildActors(bool includeDescendants)
        {
            var result = Call(
                GetAllChildActorsFunction,
                new UnrealArgument("bIncludeDescendants", UnrealValue.From(includeDescendants)));
            return UnrealArrayValue.ToList<UnrealObjectHandle>(
                result.OutArguments["ChildActors"],
                value => value.AsObjectHandle());
        }

        public string StateName => Read<string>(StateNameProperty);

        public bool bShouldPerformFullTickWhenPaused
        {
            get => Read<bool>(FullTickWhenPausedProperty);
            set => Write(FullTickWhenPausedProperty, value);
        }
    }

    private readonly record struct Vector
    {
        public double X { get; init; }

        public double Y { get; init; }

        public double Z { get; init; }

        public static UnrealStructDescriptor Descriptor { get; } = new(
            "/Script/CoreUObject.Vector",
            24,
            8,
            [
                new("X", "DoubleProperty", 0, 8),
                new("Y", "DoubleProperty", 8, 8),
                new("Z", "DoubleProperty", 16, 8)
            ]);

        public UnrealValue ToUnrealValue() => UnrealValue.From(new UnrealStructValue(
            Descriptor,
            new Dictionary<string, UnrealValue>(StringComparer.Ordinal)
            {
                ["X"] = UnrealValue.From(X),
                ["Y"] = UnrealValue.From(Y),
                ["Z"] = UnrealValue.From(Z)
            }));

        public static Vector FromUnrealValue(UnrealValue value)
        {
            var transported = value.As<UnrealStructValue>();
            if (!StringComparer.Ordinal.Equals(transported.Descriptor.Path, Descriptor.Path))
            {
                throw new InvalidCastException(
                    $"Unreal struct '{transported.Descriptor.Path}' cannot be read as '{Descriptor.Path}'.");
            }

            return new Vector
            {
                X = transported.GetField("X").As<double>(),
                Y = transported.GetField("Y").As<double>(),
                Z = transported.GetField("Z").As<double>()
            };
        }
    }

    private sealed class TextBlockSdk(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle)
    {
        private static readonly UnrealPropertyDescriptor TextProperty = new(
            "/Script/UMG.TextBlock",
            "Text",
            "TextProperty",
            392,
            1,
            "CPF_Edit | CPF_BlueprintVisible | CPF_NativeAccessSpecifierPublic",
            16);

        public string Text
        {
            get => Read<string>(TextProperty);
            set => Write(TextProperty, value);
        }
    }

    private sealed class PanelWidgetSdk(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle)
    {
        private static readonly UnrealArrayDescriptor PanelSlotArrayDescriptor =
            new("ObjectProperty:/Script/UMG.PanelSlot", 8);
        private static readonly UnrealArrayDescriptor WidgetArrayDescriptor =
            new("ObjectProperty:/Script/UMG.Widget", 8);
        private static readonly UnrealPropertyDescriptor SlotsProperty = new(
            "/Script/UMG.PanelWidget",
            "Slots",
            "ArrayProperty",
            360,
            1,
            "CPF_ExportObject | CPF_ZeroConstructor | CPF_ContainsInstancedReference | CPF_Protected | CPF_UObjectWrapper | CPF_NativeAccessSpecifierProtected | CPF_TObjectPtr",
            16,
            Array: PanelSlotArrayDescriptor);
        private static readonly UnrealFunctionDescriptor GetAllChildrenFunction = new(
            "/Script/UMG.PanelWidget",
            "/Script/UMG.PanelWidget:GetAllChildren",
            "GetAllChildren",
            "FUNC_Final | FUNC_RequiredAPI | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [new(
                "ReturnValue",
                "ArrayProperty",
                0,
                1,
                "CPF_ExportObject | CPF_Parm | CPF_OutParm | CPF_ZeroConstructor | CPF_ReturnParm | CPF_ContainsInstancedReference | CPF_NativeAccessSpecifierPublic",
                16,
                Array: WidgetArrayDescriptor)]);

        public IReadOnlyList<UnrealObjectHandle> Slots
        {
            get => UnrealArrayValue.ToList<UnrealObjectHandle>(
                ReadValue(SlotsProperty),
                value => value.AsObjectHandle());
        }

        public IReadOnlyList<UnrealObjectHandle> GetAllChildren() =>
            UnrealArrayValue.ToList<UnrealObjectHandle>(
                Call(GetAllChildrenFunction).ReturnValue,
                value => value.AsObjectHandle());
    }

    private sealed class PlayerStateSdk(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle)
    {
        private static readonly UnrealFunctionDescriptor GetPlayerNameFunction = new(
            "/Script/Engine.PlayerState",
            "/Script/Engine.PlayerState:GetPlayerName",
            "GetPlayerName",
            "FUNC_Final | FUNC_Native | FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Const",
            [new(
                "ReturnValue",
                "StrProperty",
                0,
                1,
                "CPF_Parm | CPF_OutParm | CPF_ReturnParm",
                16)]);

        public string GetPlayerName() => Call(GetPlayerNameFunction).ReturnValue.As<string>();
    }
}
