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
        private static readonly UnrealFunctionDescriptor TestArrayMarshallingFunction = new(
            "/Script/Engine.PlayerController",
            "/Script/Engine.PlayerController:TestArrayMarshalling",
            "TestArrayMarshalling",
            "FUNC_Native | FUNC_Public",
            [
                new("Input", "ArrayProperty", 0, 1, "CPF_Parm", 16, Array: IntArrayDescriptor),
                new("ReturnValue", "ArrayProperty", 16, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 16, Array: IntArrayDescriptor)
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
