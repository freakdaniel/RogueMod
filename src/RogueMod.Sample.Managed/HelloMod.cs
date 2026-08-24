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

    public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
    {
        _logger = context.Logger;
        _unreal = context.Unreal;
        _logger.Log(ModLogLevel.Information, $"Hello from {context.ModId} on {context.GameProfileId}.");
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        _logger?.Log(ModLogLevel.Information, "Hello managed mod unloaded.");
        _logger = null;
        _unreal = null;
        _firstUpdateLogged = false;
        _reflectionLogged = false;
        _invocationLogged = false;
        _stringSmokeLogged = false;
        _textSmokeLogged = false;
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
            if (!playerController.IsNull)
            {
                var objectPath = _unreal.GetPathName(playerController) ?? "<unknown>";
                var classPath = _unreal.GetPathName(_unreal.GetClass(playerController)) ?? "<unknown>";
                _logger?.Log(
                    ModLogLevel.Information,
                    $"Unreal reflection: PlayerController={objectPath}; Class={classPath}.");
                _reflectionLogged = true;

                if (!_invocationLogged
                    && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionInvocation) != 0)
                {
                    var controller = new PlayerControllerSdk(_unreal, playerController);
                    controller.ResetControllerLightColor();
                    _logger?.Log(ModLogLevel.Information, "Typed UFunction call succeeded: PlayerController.ResetControllerLightColor().");
                    controller.AddYawInput(0.0f);
                    _logger?.Log(ModLogLevel.Information, "Typed input marshalling succeeded: PlayerController.AddYawInput(0.0f).");
                    var canRestartPlayer = controller.CanRestartPlayer();
                    _logger?.Log(
                        ModLogLevel.Information,
                        $"Typed return marshalling succeeded: PlayerController.CanRestartPlayer()={canRestartPlayer}.");
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
        }

        if (eventKind == ModGameEventKind.Update
            && !_stringSmokeLogged
            && _unreal?.IsAvailable == true
            && (_unreal.Capabilities & UnrealReflectionCapabilities.FunctionInvocation) != 0)
        {
            var playerState = _unreal.FindFirstOf("PlayerState");
            if (!playerState.IsNull)
            {
                var state = new PlayerStateSdk(_unreal, playerState);
                _logger?.Log(ModLogLevel.Information, $"FString marshalling succeeded: PlayerName={state.GetPlayerName()}.");
                _stringSmokeLogged = true;
            }
        }

        if (eventKind == ModGameEventKind.Update
            && !_textSmokeLogged
            && _unreal?.IsAvailable == true
            && (_unreal.Capabilities & (UnrealReflectionCapabilities.PropertyRead | UnrealReflectionCapabilities.PropertyWrite))
                == (UnrealReflectionCapabilities.PropertyRead | UnrealReflectionCapabilities.PropertyWrite))
        {
            var textBlock = _unreal.FindFirstOf("TextBlock");
            if (!textBlock.IsNull)
            {
                var block = new TextBlockSdk(_unreal, textBlock);
                var text = block.Text;
                _logger?.Log(ModLogLevel.Information, $"FText property read succeeded: Text={text}.");
                block.Text = text;
                _logger?.Log(ModLogLevel.Information, "FText property write succeeded: Text was preserved.");
                _textSmokeLogged = true;
            }
        }
    }

    private sealed class PlayerControllerSdk(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle)
    {
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

        public bool CanRestartPlayer() => Call(CanRestartPlayerFunction).ReturnValue.As<bool>();

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
