using RogueMod.Abstractions;

namespace RogueMod.Tests.Fixtures;

public sealed class TestManagedMod : IRogueMod, IRogueModGameEvents
{
    private IModLogger? _logger;

    public ValueTask LoadAsync(IModContext context, CancellationToken cancellationToken = default)
    {
        _logger = context.Logger;
        _logger.Log(ModLogLevel.Information, $"loaded:{context.ModId}");
        if (context.Unreal.IsAvailable)
        {
            var instance = context.Unreal.FindFirstOf("PlayerController");
            _logger.Log(ModLogLevel.Information, $"reflection:{context.Unreal.GetPathName(instance)}");
            var typedInstance = context.Unreal.FindFirst<TestPlayerController>();
            var typedInstances = context.Unreal.FindAll<TestPlayerController>();
            _logger.Log(ModLogLevel.Information, $"discovery:{typedInstance?.PathName}:{typedInstances.Count}");
            if ((context.Unreal.Capabilities & UnrealReflectionCapabilities.FunctionInvocation) != 0)
            {
                var playerController = new TestPlayerController(context.Unreal, instance);
                playerController.Pause();
                _logger.Log(ModLogLevel.Information, "invoked:Pause");
                var marshalled = playerController.TestMarshalling(1.25f);
                _logger.Log(ModLogLevel.Information, $"marshalled:{marshalled.ReturnValue}:{marshalled.OutputValue}");
                var strings = playerController.TestStringMarshalling("InputName");
                _logger.Log(ModLogLevel.Information, $"strings:{strings.ReturnValue}:{strings.OutputValue}");
                var text = playerController.TestTextMarshalling("Input Text");
                _logger.Log(ModLogLevel.Information, $"text:{text}");
                var numbers = playerController.TestArrayMarshalling([1, 2, 3]);
                _logger.Log(ModLogLevel.Information, $"array:{string.Join(':', numbers)}");
                var numberGroups = playerController.TestNestedArrayMarshalling([[1, 2], [3]]);
                _logger.Log(ModLogLevel.Information, $"nested-array:{FormatGroups(numberGroups)}");
                var vector = playerController.TestStructMarshalling(new TestVector { X = 1.0, Y = 2.0, Z = 3.0 });
                _logger.Log(ModLogLevel.Information, $"struct:{vector.X}:{vector.Y}:{vector.Z}");
                var spawnLocation = playerController.SpawnLocation;
                _logger.Log(ModLogLevel.Information, $"property:SpawnLocation={spawnLocation.X}:{spawnLocation.Y}:{spawnLocation.Z}");
                playerController.SpawnLocation = spawnLocation;
                var fullTickWhenPaused = playerController.bShouldPerformFullTickWhenPaused;
                _logger.Log(ModLogLevel.Information, $"property:bShouldPerformFullTickWhenPaused={fullTickWhenPaused}");
                playerController.bShouldPerformFullTickWhenPaused = fullTickWhenPaused;
                var playerName = playerController.PlayerName;
                _logger.Log(ModLogLevel.Information, $"property:PlayerName={playerName}");
                playerController.PlayerName = playerName;
                var displayText = playerController.DisplayText;
                _logger.Log(ModLogLevel.Information, $"property:DisplayText={displayText}");
                playerController.DisplayText = displayText;
                var scores = playerController.Scores;
                _logger.Log(ModLogLevel.Information, $"property:Scores={string.Join(':', scores)}");
                playerController.Scores = scores;
                var scoreGroups = playerController.ScoreGroups;
                _logger.Log(ModLogLevel.Information, $"property:ScoreGroups={FormatGroups(scoreGroups)}");
                playerController.ScoreGroups = scoreGroups;
                if ((context.Unreal.Capabilities & UnrealReflectionCapabilities.MapSetProperties) != 0)
                {
                    var scoresByName = playerController.ScoresByName;
                    _logger.Log(ModLogLevel.Information, $"property:ScoresByName={FormatMap(scoresByName)}");
                    var uniqueScores = playerController.UniqueScores;
                    _logger.Log(ModLogLevel.Information, $"property:UniqueScores={string.Join(',', uniqueScores)}");
                }
                var optionalScore = playerController.OptionalScore;
                _logger.Log(ModLogLevel.Information, $"property:OptionalScore={FormatOptional(optionalScore)}");
                playerController.OptionalScore = optionalScore;
                var optionalUnsetScore = playerController.OptionalUnsetScore;
                _logger.Log(ModLogLevel.Information, $"property:OptionalUnsetScore={FormatOptional(optionalUnsetScore)}");
                playerController.OptionalUnsetScore = optionalUnsetScore;
                var optionalResult = playerController.TestOptionalMarshalling(UnrealOptional<int>.FromValue(7));
                _logger.Log(ModLogLevel.Information, $"optional:{FormatOptional(optionalResult)}");
                var weakController = playerController.WeakController;
                _logger.Log(ModLogLevel.Information, $"property:WeakController={weakController?.PathName}");
                playerController.WeakController = null;
                playerController.WeakController = weakController;
                var weakResult = playerController.TestWeakObjectMarshalling(playerController);
                _logger.Log(ModLogLevel.Information, $"weak:{weakResult?.PathName}");
                var lazyController = playerController.LazyController;
                _logger.Log(ModLogLevel.Information, $"property:LazyController={lazyController.ObjectId}:{lazyController.CachedTarget?.PathName}");
                playerController.LazyController = UnrealLazyObjectReference<TestPlayerController>.Null;
                playerController.LazyController = lazyController;
                var lazyResult = playerController.TestLazyObjectMarshalling(lazyController);
                _logger.Log(ModLogLevel.Information, $"lazy:{lazyResult.ObjectId}:{lazyResult.CachedTarget?.PathName}");
                var softController = playerController.SoftController;
                _logger.Log(ModLogLevel.Information, $"soft:{softController.Path}:{softController.CachedTarget?.PathName}");
                playerController.SoftController = softController;
                var leader = playerController.Leader;
                _logger.Log(ModLogLevel.Information, $"property:Leader={leader?.PathName}");
                var interfaceResult = playerController.TestInterfaceMarshalling(playerController);
                _logger.Log(ModLogLevel.Information, $"interface:{interfaceResult?.PathName}");
            }
            if ((context.Unreal.Capabilities & UnrealReflectionCapabilities.ObjectCreation) != 0)
            {
                var created = context.Unreal.CreateObject(instance, instance, "ManagedAbiObject");
                _logger.Log(ModLogLevel.Information, $"created:{created.Value:X16}");
            }
            if ((context.Unreal.Capabilities & UnrealReflectionCapabilities.ActorSpawning) != 0)
            {
                var spawned = context.Unreal.SpawnActor(
                    instance,
                    instance,
                    new UnrealVector(1.0f, 2.0f, 3.0f),
                    new UnrealRotator(4.0f, 5.0f, 6.0f));
                _logger.Log(ModLogLevel.Information, $"spawned:{spawned.Value:X16}");
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken = default)
    {
        _logger?.Log(ModLogLevel.Information, "unloaded");
        _logger = null;
        return ValueTask.CompletedTask;
    }

    public void OnGameEvent(ModGameEventKind eventKind) =>
        _logger?.Log(ModLogLevel.Information, $"event:{eventKind}");

    private static string FormatGroups(IReadOnlyList<IReadOnlyList<int>> groups) =>
        string.Join('|', groups.Select(group => string.Join(',', group)));

    private static string FormatMap(IReadOnlyDictionary<int, string> map) =>
        string.Join(':', map.Select(pair => $"{pair.Key}:{pair.Value}"));

    private static string FormatOptional<T>(UnrealOptional<T> value) =>
        value.IsSet ? $"set:{value.Value}" : "unset";

    private sealed class TestPlayerController(IUnrealReflection unreal, UnrealObjectHandle handle)
        : UnrealObject(unreal, handle), IUnrealObjectType<TestPlayerController>
    {
        static string IUnrealObjectType<TestPlayerController>.UnrealClassName => "PlayerController";

        static TestPlayerController IUnrealObjectType<TestPlayerController>.Create(
            IUnrealReflection unreal,
            UnrealObjectHandle handle) => new(unreal, handle);

        private static readonly UnrealFunctionDescriptor PauseFunction =
            new("/Script/Engine.PlayerController", "/Script/Engine.PlayerController:Pause", "Pause", "FUNC_Native | FUNC_Public");
        private static readonly UnrealPropertyDescriptor FullTickWhenPausedProperty =
            new("/Script/Engine.PlayerController", "bShouldPerformFullTickWhenPaused", "BoolProperty", 1568, 1, "CPF_Protected");
        private static readonly UnrealFunctionDescriptor TestMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestMarshalling",
            "TestMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("InputValue", "FloatProperty", 0, 1, "CPF_Parm", 4),
                new("OutputValue", "IntProperty", 4, 1, "CPF_Parm | CPF_OutParm", 4),
                new("ReturnValue", "BoolProperty", 8, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 1, 0, 1, 255)
            ]);
        private static readonly UnrealFunctionDescriptor TestStringMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestStringMarshalling",
            "TestStringMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("InputName", "NameProperty", 0, 1, "CPF_Parm", 8),
                new("OutputValue", "StrProperty", 8, 1, "CPF_Parm | CPF_OutParm", 16),
                new("ReturnValue", "NameProperty", 24, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 8)
            ]);
        private static readonly UnrealFunctionDescriptor TestTextMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestTextMarshalling",
            "TestTextMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "TextProperty", 0, 1, "CPF_Parm", 16),
                new("ReturnValue", "TextProperty", 16, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 16)
            ]);
        private static readonly UnrealArrayDescriptor IntArrayDescriptor = new("IntProperty", 4);
        private static readonly UnrealOptionalDescriptor IntOptionalDescriptor = new("IntProperty", 4);
        private static readonly UnrealArrayDescriptor NestedIntArrayDescriptor = new("ArrayProperty", 16)
        {
            ElementArray = IntArrayDescriptor
        };
        private static readonly UnrealFunctionDescriptor TestArrayMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestArrayMarshalling",
            "TestArrayMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "ArrayProperty", 0, 1, "CPF_Parm", 16, Array: IntArrayDescriptor),
                new("ReturnValue", "ArrayProperty", 16, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 16, Array: IntArrayDescriptor)
            ]);
        private static readonly UnrealFunctionDescriptor TestNestedArrayMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestNestedArrayMarshalling",
            "TestNestedArrayMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "ArrayProperty", 0, 1, "CPF_Parm", 16, Array: NestedIntArrayDescriptor),
                new("ReturnValue", "ArrayProperty", 16, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 16, Array: NestedIntArrayDescriptor)
            ]);
        private static readonly UnrealPropertyDescriptor PlayerNameProperty =
            new("/Script/Engine.PlayerController", "PlayerName", "StrProperty", 1600, 1, "CPF_Protected", 16);
        private static readonly UnrealPropertyDescriptor SpawnLocationProperty = new(
            "/Script/Engine.PlayerController",
            "SpawnLocation",
            "StructProperty:/Script/CoreUObject.Vector",
            1640,
            1,
            "CPF_Protected | CPF_IsPlainOldData | CPF_NoDestructor",
            24,
            Struct: TestVector.Descriptor);
        private static readonly UnrealPropertyDescriptor DisplayTextProperty =
            new("/Script/Engine.PlayerController", "DisplayText", "TextProperty", 1664, 1, "CPF_Protected", 16);
        private static readonly UnrealPropertyDescriptor ScoresProperty =
            new("/Script/Engine.PlayerController", "Scores", "ArrayProperty", 1680, 1, "CPF_Protected", 16, Array: IntArrayDescriptor);
        private static readonly UnrealPropertyDescriptor ScoreGroupsProperty =
            new("/Script/Engine.PlayerController", "ScoreGroups", "ArrayProperty", 1696, 1, "CPF_Protected", 16, Array: NestedIntArrayDescriptor);
        private static readonly UnrealPropertyDescriptor ScoresByNameProperty =
            new("/Script/Engine.PlayerController", "ScoresByName", "MapProperty", 1712, 1, "CPF_Protected", 80)
            {
                Map = new UnrealMapDescriptor("IntProperty", 4, "StrProperty", 16)
            };
        private static readonly UnrealPropertyDescriptor UniqueScoresProperty =
            new("/Script/Engine.PlayerController", "UniqueScores", "SetProperty", 1720, 1, "CPF_Protected", 80)
            {
                Set = new UnrealSetDescriptor("IntProperty", 4)
            };
        private static readonly UnrealPropertyDescriptor OptionalScoreProperty =
            new("/Script/Engine.PlayerController", "OptionalScore", "OptionalProperty", 1712, 1, "CPF_Protected", 8)
            {
                Optional = IntOptionalDescriptor
            };
        private static readonly UnrealPropertyDescriptor OptionalUnsetScoreProperty =
            new("/Script/Engine.PlayerController", "OptionalUnsetScore", "OptionalProperty", 1720, 1, "CPF_Protected", 8)
            {
                Optional = IntOptionalDescriptor
            };
        private static readonly UnrealFunctionDescriptor TestOptionalMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestOptionalMarshalling",
            "TestOptionalMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "OptionalProperty", 0, 1, "CPF_Parm", 8) { Optional = IntOptionalDescriptor },
                new("ReturnValue", "OptionalProperty", 8, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 8)
                {
                    Optional = IntOptionalDescriptor
                }
            ]);
        private static readonly UnrealPropertyDescriptor WeakControllerProperty =
            new(
                "/Script/Engine.PlayerController",
                "WeakController",
                "WeakObjectProperty:/Script/Engine.PlayerController",
                1728,
                1,
                "CPF_Protected | CPF_UObjectWrapper",
                8);
        private static readonly UnrealFunctionDescriptor TestWeakObjectMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestWeakObjectMarshalling",
            "TestWeakObjectMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "WeakObjectProperty:/Script/Engine.PlayerController", 0, 1, "CPF_Parm", 8),
                new("ReturnValue", "WeakObjectProperty:/Script/Engine.PlayerController", 8, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 8)
            ]);
        private static readonly UnrealPropertyDescriptor LazyControllerProperty =
            new(
                "/Script/Engine.PlayerController",
                "LazyController",
                "LazyObjectProperty:/Script/Engine.PlayerController",
                1736,
                1,
                "CPF_Protected | CPF_UObjectWrapper",
                24);
        private static readonly UnrealFunctionDescriptor TestLazyObjectMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestLazyObjectMarshalling",
            "TestLazyObjectMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "LazyObjectProperty:/Script/Engine.PlayerController", 0, 1, "CPF_Parm", 24),
                new("ReturnValue", "LazyObjectProperty:/Script/Engine.PlayerController", 24, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 24)
            ]);
        private static readonly UnrealPropertyDescriptor SoftControllerProperty =
            new(
                "/Script/Engine.PlayerController",
                "SoftController",
                "SoftObjectProperty:/Script/Engine.PlayerController",
                1760,
                1,
                "CPF_Protected | CPF_UObjectWrapper",
                40);
        private static readonly UnrealPropertyDescriptor LeaderProperty =
            new(
                "/Script/Engine.PlayerController",
                "Leader",
                "InterfaceProperty:/Script/GameplayTags.GameplayTagAssetInterface",
                1776,
                1,
                "CPF_Protected | CPF_ZeroConstructor | CPF_IsPlainOldData | CPF_NoDestructor",
                16);
        private static readonly UnrealFunctionDescriptor TestInterfaceMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestInterfaceMarshalling",
            "TestInterfaceMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "InterfaceProperty:/Script/GameplayTags.GameplayTagAssetInterface", 0, 1, "CPF_Parm", 16),
                new("ReturnValue", "InterfaceProperty:/Script/GameplayTags.GameplayTagAssetInterface", 16, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 16)
            ]);
        private static readonly UnrealFunctionDescriptor TestStructMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestStructMarshalling",
            "TestStructMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "StructProperty:/Script/CoreUObject.Vector", 0, 1, "CPF_Parm", 24, Struct: TestVector.Descriptor),
                new("ReturnValue", "StructProperty:/Script/CoreUObject.Vector", 24, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 24, Struct: TestVector.Descriptor)
            ]);

        public void Pause() => Call(PauseFunction);

        public TestMarshallingResult TestMarshalling(float inputValue)
        {
            var result = Call(
                TestMarshallingFunction,
                new UnrealArgument("InputValue", UnrealValue.From(inputValue)));
            return new(result.ReturnValue.As<bool>(), result.GetOut<int>("OutputValue"));
        }

        public readonly record struct TestMarshallingResult(bool ReturnValue, int OutputValue);

        public TestStringMarshallingResult TestStringMarshalling(string inputName)
        {
            var result = Call(
                TestStringMarshallingFunction,
                new UnrealArgument("InputName", UnrealValue.From(inputName)));
            return new(result.ReturnValue.As<string>(), result.GetOut<string>("OutputValue"));
        }

        public readonly record struct TestStringMarshallingResult(string ReturnValue, string OutputValue);

        public string TestTextMarshalling(string input)
        {
            var result = Call(
                TestTextMarshallingFunction,
                new UnrealArgument("Input", UnrealValue.From(input)));
            return result.ReturnValue.As<string>();
        }

        public IReadOnlyList<int> TestArrayMarshalling(IReadOnlyList<int> input)
        {
            var packed = UnrealArrayValue.From(IntArrayDescriptor, input, UnrealValue.From);
            var result = Call(TestArrayMarshallingFunction, new UnrealArgument("Input", packed));
            return UnrealArrayValue.ToList<int>(result.ReturnValue, value => value.As<int>());
        }

        public IReadOnlyList<IReadOnlyList<int>> TestNestedArrayMarshalling(IReadOnlyList<IReadOnlyList<int>> input)
        {
            var result = Call(
                TestNestedArrayMarshallingFunction,
                new UnrealArgument("Input", PackNestedArray(input)));
            return UnpackNestedArray(result.ReturnValue);
        }

        public TestVector TestStructMarshalling(TestVector input)
        {
            var result = Call(TestStructMarshallingFunction, new UnrealArgument("Input", input.ToUnrealValue()));
            return TestVector.FromUnrealValue(result.ReturnValue);
        }

        public bool bShouldPerformFullTickWhenPaused
        {
            get => Read<bool>(FullTickWhenPausedProperty);
            set => Write(FullTickWhenPausedProperty, value);
        }

        public string PlayerName
        {
            get => Read<string>(PlayerNameProperty);
            set => Write(PlayerNameProperty, value);
        }

        public TestVector SpawnLocation
        {
            get => TestVector.FromUnrealValue(ReadValue(SpawnLocationProperty));
            set => WriteValue(SpawnLocationProperty, value.ToUnrealValue());
        }

        public string DisplayText
        {
            get => Read<string>(DisplayTextProperty);
            set => Write(DisplayTextProperty, value);
        }

        public IReadOnlyList<int> Scores
        {
            get => UnrealArrayValue.ToList<int>(ReadValue(ScoresProperty), value => value.As<int>());
            set => WriteValue(ScoresProperty, UnrealArrayValue.From(IntArrayDescriptor, value, UnrealValue.From));
        }

        public IReadOnlyList<IReadOnlyList<int>> ScoreGroups
        {
            get => UnpackNestedArray(ReadValue(ScoreGroupsProperty));
            set => WriteValue(ScoreGroupsProperty, PackNestedArray(value));
        }

        public IReadOnlyDictionary<int, string> ScoresByName
        {
            get => UnrealMapValue.ToDictionary<int, string>(
                ReadValue(ScoresByNameProperty),
                key => key.As<int>(),
                value => value.As<string>());
        }

        public IReadOnlySet<int> UniqueScores
        {
            get => UnrealSetValue.ToSet<int>(
                ReadValue(UniqueScoresProperty),
                value => value.As<int>());
        }

        public UnrealOptional<int> OptionalScore
        {
            get => UnrealOptional<int>.FromUnrealValue(ReadValue(OptionalScoreProperty), value => value.As<int>());
            set => WriteValue(
                OptionalScoreProperty,
                value.ToUnrealValue(IntOptionalDescriptor, UnrealValue.From));
        }

        public UnrealOptional<int> OptionalUnsetScore
        {
            get => UnrealOptional<int>.FromUnrealValue(ReadValue(OptionalUnsetScoreProperty), value => value.As<int>());
            set => WriteValue(
                OptionalUnsetScoreProperty,
                value.ToUnrealValue(IntOptionalDescriptor, UnrealValue.From));
        }

        public UnrealOptional<int> TestOptionalMarshalling(UnrealOptional<int> input)
        {
            var result = Call(
                TestOptionalMarshallingFunction,
                new UnrealArgument(
                    "Input",
                    input.ToUnrealValue(IntOptionalDescriptor, UnrealValue.From)));
            return UnrealOptional<int>.FromUnrealValue(result.ReturnValue, value => value.As<int>());
        }

        public TestPlayerController? WeakController
        {
            get => ReadObject(WeakControllerProperty, static (unreal, handle) => new TestPlayerController(unreal, handle));
            set => WriteObject(WeakControllerProperty, value);
        }

        public TestPlayerController? TestWeakObjectMarshalling(TestPlayerController? input)
        {
            var result = Call(
                TestWeakObjectMarshallingFunction,
                new UnrealArgument("Input", UnrealValue.From(input?.Handle ?? UnrealObjectHandle.Null)));
            return WrapObject(
                result.ReturnValue,
                static (unreal, handle) => new TestPlayerController(unreal, handle));
        }

        public UnrealLazyObjectReference<TestPlayerController> LazyController
        {
            get => UnrealLazyObjectReference<TestPlayerController>.FromUnrealValue(
                ReadValue(LazyControllerProperty),
                handle => new TestPlayerController(Unreal, handle));
            set => WriteValue(LazyControllerProperty, value.ToUnrealValue());
        }

        public UnrealLazyObjectReference<TestPlayerController> TestLazyObjectMarshalling(
            UnrealLazyObjectReference<TestPlayerController> input)
        {
            var result = Call(
                TestLazyObjectMarshallingFunction,
                new UnrealArgument("Input", input.ToUnrealValue()));
            return UnrealLazyObjectReference<TestPlayerController>.FromUnrealValue(
                result.ReturnValue,
                handle => new TestPlayerController(Unreal, handle));
        }

        public UnrealSoftObjectReference<TestPlayerController> SoftController
        {
            get => UnrealSoftObjectReference<TestPlayerController>.FromUnrealValue(
                ReadValue(SoftControllerProperty),
                handle => new TestPlayerController(Unreal, handle));
            set => WriteValue(SoftControllerProperty, value.ToUnrealValue());
        }

        public TestPlayerController? Leader =>
            ReadObject(LeaderProperty, static (unreal, handle) => new TestPlayerController(unreal, handle));

        public TestPlayerController? TestInterfaceMarshalling(TestPlayerController? input)
        {
            var result = Call(
                TestInterfaceMarshallingFunction,
                new UnrealArgument("Input", UnrealValue.From(input?.Handle ?? UnrealObjectHandle.Null)));
            return WrapObject(
                result.ReturnValue,
                static (unreal, handle) => new TestPlayerController(unreal, handle));
        }

        private static UnrealValue PackNestedArray(IReadOnlyList<IReadOnlyList<int>> values) =>
            UnrealArrayValue.From(
                NestedIntArrayDescriptor,
                values,
                group => UnrealArrayValue.From(IntArrayDescriptor, group, UnrealValue.From));

        private static IReadOnlyList<IReadOnlyList<int>> UnpackNestedArray(UnrealValue value) =>
            UnrealArrayValue.ToList<IReadOnlyList<int>>(
                value,
                group => UnrealArrayValue.ToList<int>(group, element => element.As<int>()));
    }

    public readonly record struct TestVector
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

        public static TestVector FromUnrealValue(UnrealValue value)
        {
            var transported = value.As<UnrealStructValue>();
            return new TestVector
            {
                X = transported.GetField("X").As<double>(),
                Y = transported.GetField("Y").As<double>(),
                Z = transported.GetField("Z").As<double>()
            };
        }
    }
}
