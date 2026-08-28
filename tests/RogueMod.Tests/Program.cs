using RogueMod.Abstractions;
using RogueMod.Core.Authoring;
using RogueMod.Core.Diagnostics;
using RogueMod.Core.Mods;
using RogueMod.Core.Profiles;
using RogueMod.Sdk;
using RogueMod.Runtime;
using RogueMod.Tests.Fixtures;
using RogueMod.Tests.Native;
using System.Runtime.InteropServices;
using Xunit;

namespace RogueMod.Tests;

public sealed class RogueModTests
{
    [Fact]
    public void ProfileLoadsTest() => ProfileLoads();

    [Fact]
    public void FingerprintsAreNormalizedTest() => FingerprintsAreNormalized();

    [Fact]
    public void InspectorAcceptsCompleteInstallationTest() => InspectorAcceptsCompleteInstallation();

    [Fact]
    public void ManifestRejectsUnsafeEntryPointTest() => ManifestRejectsUnsafeEntryPoint();

    [Fact]
    public void ManagedPackageManifestLoadsTest() => ManagedPackageManifestLoads();

    [Fact]
    public void ManifestLoadsLocalizedMetadataTest() => ManifestLoadsLocalizedMetadata();

    [Fact]
    public void ManagedModScaffolderCreatesStandaloneStarterTest() => ManagedModScaffolderCreatesStandaloneStarter();

    [Fact]
    public void ManagedPackageInstallsTransactionallyTest() => ManagedPackageInstallsTransactionally();

    [Fact]
    public void NativePackageInstallsAndActivatesTransactionallyTest() => NativePackageInstallsAndActivatesTransactionally();

    [Fact]
    public void ModManagerControlsAllPackageKindsTest() => ModManagerControlsAllPackageKinds();

    [Fact]
    public void RuntimeInstallsAndActivatesTransactionallyTest() => RuntimeInstallsAndActivatesTransactionally();

    [Fact]
    public void NativeBootstrapValidatesAbiTest() => NativeBootstrapValidatesAbi();

    [Fact]
    public void NativeReflectionTypeRegistryPreservesAbiTest() => NativeReflectionTypeRegistryPreservesAbi();

    [Fact]
    public unsafe void NativeFunctionHooksDispatchAndUnregisterTest() => NativeFunctionHooksDispatchAndUnregister();

    [Fact]
    public unsafe void NativeHookComplexMutationOwnershipTest() => NativeHookComplexMutationOwnership();

    [Fact]
    public unsafe void NativeHookMapSetAndNonPodMutationTest() => NativeHookMapSetAndNonPodMutation();

    [Fact]
    public Task ManagedModLoadsAndUnloadsTest() => ManagedModLoadsAndUnloads().AsTask();

    [Fact]
    public void JMapImportsAndGeneratesTypedSdkTest() => JMapImportsAndGeneratesTypedSdk();

    static void NativeReflectionTypeRegistryPreservesAbi()
    {
        Assert((uint)NativePropertyKind.Boolean == 1, "Boolean ABI kind changed.");
        Assert((uint)NativePropertyKind.Object == 12, "Object ABI kind changed.");
        Assert((uint)NativePropertyKind.Array == 17, "Array ABI kind changed.");
        Assert((uint)NativePropertyKind.Optional == 18, "Optional ABI kind changed.");
        Assert((uint)NativePropertyKind.LazyObject == 20, "Lazy-object ABI kind changed.");
        Assert((uint)NativePropertyKind.Interface == 22, "Interface ABI kind changed.");
        Assert((uint)NativePropertyKind.Map == 23, "Map ABI kind changed.");
        Assert((uint)NativePropertyKind.Set == 24, "Set ABI kind changed.");

        Assert(NativeReflectionTypeRegistry.GetPropertyKind("EnumProperty:/Script/Test.Mode", 4) == NativePropertyKind.UInt32,
            "Enum storage was not resolved through the shared type registry.");
        Assert(NativeReflectionTypeRegistry.GetPropertyKind("ArrayProperty", 16) == NativePropertyKind.Array,
            "TArray was not resolved through the shared type registry.");
        Assert(NativeReflectionTypeRegistry.GetPropertyKind("MapProperty", 80) == NativePropertyKind.Map,
            "TMap was not resolved through the shared type registry.");
        Assert(NativeReflectionTypeRegistry.GetPropertyKind("SetProperty", 80) == NativePropertyKind.Set,
            "TSet was not resolved through the shared type registry.");
        Assert(NativeReflectionTypeRegistry.GetPropertyKind("InterfaceProperty:/Script/TeamSupport.WithTeamInterface", 16) == NativePropertyKind.Interface,
            "FScriptInterface was not resolved through the shared type registry.");
        Assert(NativeReflectionTypeRegistry.DecodePropertyKind(17U | 6U << 8) == NativePropertyKind.Array,
            "Nested container encoding did not preserve the outer ABI kind.");
        Assert(NativeReflectionTypeRegistry.DecodePropertyKind(23U | 6U << 8 | 13U << 16) == NativePropertyKind.Map,
            "TMap encoding did not preserve the outer ABI kind.");
        Assert(NativeReflectionTypeRegistry.DecodePropertyKind(24U | 6U << 8) == NativePropertyKind.Set,
            "TSet encoding did not preserve the outer ABI kind.");

        const float floatValue = 13.25F;
        var floatWire = NativeScalarValueCodec.Encode(NativePropertyKind.Float, floatValue);
        Assert(NativeScalarValueCodec.Decode(NativePropertyKind.Float, floatWire) is float decodedFloat
            && decodedFloat == floatValue, "Float scalar codec did not round-trip.");

        var handle = new UnrealObjectHandle(0x1234_5678UL);
        var handleWire = NativeScalarValueCodec.Encode(NativePropertyKind.Object, handle);
        Assert(NativeScalarValueCodec.Decode(NativePropertyKind.Object, handleWire) is UnrealObjectHandle decodedHandle
            && decodedHandle == handle, "UObject handle scalar codec did not round-trip.");

        var interfaceHandle = new UnrealObjectHandle(0x1212_3456UL);
        var interfaceWire = NativeScalarValueCodec.Encode(NativePropertyKind.Interface, interfaceHandle);
        Assert(NativeScalarValueCodec.Decode(NativePropertyKind.Interface, interfaceWire) is UnrealObjectHandle decodedInterface
            && decodedInterface == interfaceHandle, "UInterface handle scalar codec did not round-trip.");
    }

    static unsafe void NativeFunctionHooksDispatchAndUnregister()
    {
        var reflection = new NativeUnrealReflection(
            &NativeBootstrapTestCallbacks.UnrealIsAvailable,
            null,
            &NativeBootstrapTestCallbacks.UnrealIsValid,
            null,
            null,
            &NativeBootstrapTestCallbacks.UnrealGetCapabilities,
            null,
            null,
            null,
            null,
            &NativeHookTestCallbacks.Register,
            &NativeHookTestCallbacks.Unregister,
            null,
            null,
            (_, _) => { });
        var function = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:Calculate",
            "Calculate",
            "FUNC_Public",
            [
                new("Input", "IntProperty", 0, 1, "CPF_Parm", 4),
                new("ReturnValue", "IntProperty", 4, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 4)
            ]);

        UnrealHookContext? observed = null;
        var filteredInstance = new UnrealObjectHandle(0x0000_0007_0000_002A);
        using (reflection.RegisterHook(
                   function,
                   UnrealHookPhase.Pre,
                   new UnrealHookOptions(Priority: 42, Instance: filteredInstance),
                   context =>
                   {
                       observed = context;
                       context.SetArgument("Input", UnrealValue.From(11));
                   }))
        {
            Assert(NativeHookTestCallbacks.Priority == 42
                && NativeHookTestCallbacks.InstanceFilter == filteredInstance.Value,
                "Managed hook ordering and instance filter were not forwarded to the native ABI.");
            Assert(NativeHookTestCallbacks.Dispatch(0x0000_0007_0000_002A, 7, 0) == 0,
                "Managed pre-hook callback rejected the native snapshot.");
            Assert(observed is { Phase: UnrealHookPhase.Pre }
                && observed.Arguments["Input"].As<int>() == 7,
                "Managed pre-hook did not decode its input argument.");
            Assert(NativeHookTestCallbacks.Parameters[0].Value.Data == 11
                && (NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0,
                "Managed pre-hook did not encode its input replacement into the native callback buffer.");
        }
        Assert(NativeHookTestCallbacks.Callback == null,
            "Disposing a managed hook did not unregister its native token.");

        observed = null;
        using (reflection.RegisterHook(function, UnrealHookPhase.Post, context =>
        {
            observed = context;
            context.SetReturnValue(UnrealValue.From(13));
        }))
        {
            Assert(NativeHookTestCallbacks.Dispatch(0x0000_0007_0000_002A, 7, 9) == 0,
                "Managed post-hook callback rejected the native snapshot.");
            Assert(observed is { Phase: UnrealHookPhase.Post }
                && observed.Result.ReturnValue.As<int>() == 9,
                "Managed post-hook did not decode its return value.");
            Assert(NativeHookTestCallbacks.Parameters[1].Value.Data == 13
                && (NativeHookTestCallbacks.Parameters[1].Flags & 8U) != 0,
                "Managed post-hook did not encode its return replacement into the native callback buffer.");
        }

        var unsupportedInputFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:ObserveUnsupportedInput",
            "ObserveUnsupportedInput",
            "FUNC_Public",
            [
                new("ComplexInput", "StructProperty:/Script/Test.Unsupported", 0, 1, "CPF_Parm", 64),
                new("ReturnValue", "IntProperty", 64, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 4)
            ]);
        observed = null;
        using (reflection.RegisterHook(
                   unsupportedInputFunction,
                   UnrealHookPhase.Post,
                   new UnrealHookOptions(SkipInputDecoding: true),
                   context => observed = context))
        {
            Assert(NativeHookTestCallbacks.Dispatch(0x0000_0007_0000_002A, 0, 17) == 0,
                "Managed post-hook decoded an explicitly skipped unsupported input.");
            Assert(observed is { Phase: UnrealHookPhase.Post }
                && observed.Arguments.Count == 0
                && observed.Result.ReturnValue.As<int>() == 17,
                "Managed post-hook did not preserve return data while skipping pure inputs.");
        }

    }

    static unsafe void NativeHookComplexMutationOwnership()
    {
        var reflection = new NativeUnrealReflection(
            &NativeBootstrapTestCallbacks.UnrealIsAvailable,
            null,
            null,
            null,
            null,
            &NativeBootstrapTestCallbacks.UnrealGetCapabilities,
            null,
            null,
            null,
            null,
            &NativeHookTestCallbacks.Register,
            &NativeHookTestCallbacks.Unregister,
            null,
            null,
            (_, _) => { });

        var stringFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:RewriteString",
            "RewriteString",
            "FUNC_Public",
            [new("Input", "StrProperty", 0, 1, "CPF_Parm", 16)]);
        using (reflection.RegisterHook(stringFunction, UnrealHookPhase.Pre,
                   hook => hook.SetArgument("Input", UnrealValue.From("after"))))
        {
            var original = Marshal.StringToCoTaskMemUni("before");
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = 13,
                        Reserved = 6,
                        Data = unchecked((ulong)original)
                    }) == 0,
                "Managed string pre-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacement.Reserved == 5
                && Marshal.PtrToStringUni(unchecked((nint)replacement.Data), 5) == "after",
                "Managed string pre-hook did not transfer replacement ownership to native code.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }

        var arrayDescriptor = new UnrealArrayDescriptor("IntProperty", 4);
        var arrayFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:RewriteArray",
            "RewriteArray",
            "FUNC_Public",
            [new("Input", "ArrayProperty", 0, 1, "CPF_Parm", 16, Array: arrayDescriptor)]);
        using (reflection.RegisterHook(arrayFunction, UnrealHookPhase.Pre, hook =>
               {
                   var observed = hook.Arguments["Input"].As<UnrealArrayValue>();
                   Assert(observed.Elements.Select(value => value.As<int>()).SequenceEqual([1, 2]),
                       "Managed array pre-hook did not decode its native snapshot.");
                   hook.SetArgument(
                       "Input",
                       UnrealArrayValue.From(arrayDescriptor, [3, 4, 5], UnrealValue.From));
               }))
        {
            const uint encodedArrayKind = 17U | 6U << 8;
            var original = Marshal.AllocCoTaskMem(2 * sizeof(NativeUnrealReflection.NativeUnrealValue));
            var originalValues = (NativeUnrealReflection.NativeUnrealValue*)original;
            originalValues[0] = new() { Kind = 6, Data = 1 };
            originalValues[1] = new() { Kind = 6, Data = 2 };
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = encodedArrayKind,
                        Reserved = 2,
                        Data = unchecked((ulong)original)
                    }) == 0,
                "Managed array pre-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            var replacementValues = (NativeUnrealReflection.NativeUnrealValue*)replacement.Data;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacement.Reserved == 3
                && replacementValues[0].Data == 3
                && replacementValues[1].Data == 4
                && replacementValues[2].Data == 5,
                "Managed array pre-hook did not transfer recursive replacement ownership to native code.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }
    }

    static unsafe void NativeHookMapSetAndNonPodMutation()
    {
        var reflection = new NativeUnrealReflection(
            &NativeBootstrapTestCallbacks.UnrealIsAvailable,
            null,
            null,
            null,
            null,
            &NativeBootstrapTestCallbacks.UnrealGetCapabilities,
            null,
            null,
            null,
            null,
            &NativeHookTestCallbacks.Register,
            &NativeHookTestCallbacks.Unregister,
            null,
            null,
            (_, _) => { });

        var mapDescriptor = new UnrealMapDescriptor("IntProperty", 4, "StrProperty", 16);
        var mapFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:RewriteMap",
            "RewriteMap",
            "FUNC_Public",
            [new("Input", "MapProperty", 0, 1, "CPF_Parm", 80) { Map = mapDescriptor }]);
        using (reflection.RegisterHook(mapFunction, UnrealHookPhase.Pre, hook =>
               {
                   var observed = hook.Arguments["Input"].As<UnrealMapValue>();
                   Assert(observed.Entries.Count == 1
                       && observed.Entries[0].Key.As<int>() == 1
                       && observed.Entries[0].Value.As<string>() == "before",
                       "Managed TMap pre-hook did not decode its native snapshot.");
                   hook.SetArgument(
                       "Input",
                       UnrealMapValue.From(
                           mapDescriptor,
                           new Dictionary<int, string> { [2] = "after" },
                           UnrealValue.From,
                           UnrealValue.From));
               }))
        {
            const uint encodedMapKind = 23U | 6U << 8 | 13U << 16;
            var originalString = Marshal.StringToCoTaskMemUni("before");
            var original = Marshal.AllocCoTaskMem(2 * sizeof(NativeUnrealReflection.NativeUnrealValue));
            var originalEntries = (NativeUnrealReflection.NativeUnrealValue*)original;
            originalEntries[0] = new() { Kind = 6, Data = 1 };
            originalEntries[1] = new() { Kind = 13, Reserved = 6, Data = unchecked((ulong)originalString) };
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = encodedMapKind,
                        Reserved = 1,
                        Data = unchecked((ulong)original)
                    }) == 0,
                "Managed TMap pre-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            var replacementEntries = (NativeUnrealReflection.NativeUnrealValue*)replacement.Data;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacement.Kind == encodedMapKind
                && replacement.Reserved == 1
                && replacementEntries[0].Data == 2
                && replacementEntries[1].Reserved == 5
                && Marshal.PtrToStringUni(unchecked((nint)replacementEntries[1].Data), 5) == "after",
                "Managed TMap pre-hook did not transfer recursive replacement ownership.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }

        var setDescriptor = new UnrealSetDescriptor("IntProperty", 4);
        var setFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:RewriteSet",
            "RewriteSet",
            "FUNC_Public",
            [new("ReturnValue", "SetProperty", 0, 1, "CPF_Parm | CPF_OutParm | CPF_ReturnParm", 80) { Set = setDescriptor }]);
        using (reflection.RegisterHook(setFunction, UnrealHookPhase.Post, hook =>
               {
                   var observed = hook.Result.ReturnValue.As<UnrealSetValue>();
                   Assert(observed.Elements.Select(value => value.As<int>()).SequenceEqual([3, 4]),
                       "Managed TSet post-hook did not decode its native return snapshot.");
                   hook.SetReturnValue(UnrealSetValue.From(
                       setDescriptor,
                       new HashSet<int> { 5, 6, 7 },
                       UnrealValue.From));
               }))
        {
            const uint encodedSetKind = 24U | 6U << 8;
            var original = Marshal.AllocCoTaskMem(2 * sizeof(NativeUnrealReflection.NativeUnrealValue));
            var originalElements = (NativeUnrealReflection.NativeUnrealValue*)original;
            originalElements[0] = new() { Kind = 6, Data = 3 };
            originalElements[1] = new() { Kind = 6, Data = 4 };
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = encodedSetKind,
                        Reserved = 2,
                        Data = unchecked((ulong)original)
                    }) == 0,
                "Managed TSet post-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            var replacementElements = (NativeUnrealReflection.NativeUnrealValue*)replacement.Data;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacement.Kind == encodedSetKind
                && replacement.Reserved == 3
                && replacementElements[0].Data == 5
                && replacementElements[1].Data == 6
                && replacementElements[2].Data == 7,
                "Managed TSet post-hook did not transfer recursive replacement ownership.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }

        var structDescriptor = new UnrealStructDescriptor(
            "/Script/Test.Loadout",
            24,
            8,
            [
                new("DisplayName", "StrProperty", 0, 16),
                new("Level", "IntProperty", 16, 4)
            ]);
        var structFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:RewriteLoadout",
            "RewriteLoadout",
            "FUNC_Public",
            [new("Input", "StructProperty:/Script/Test.Loadout", 0, 1, "CPF_Parm", 24, Struct: structDescriptor)]);
        using (reflection.RegisterHook(structFunction, UnrealHookPhase.Pre, hook =>
               {
                   var observed = hook.Arguments["Input"].As<UnrealStructValue>();
                   Assert(observed.GetField("DisplayName").As<string>() == "Scout"
                       && observed.GetField("Level").As<int>() == 4,
                       "Managed non-POD struct pre-hook did not decode its field-wise snapshot.");
                   hook.SetArgument(
                       "Input",
                       UnrealValue.From(new UnrealStructValue(
                           structDescriptor,
                           new Dictionary<string, UnrealValue>
                           {
                               ["DisplayName"] = UnrealValue.From("Vanguard"),
                               ["Level"] = UnrealValue.From(9)
                           })));
               }))
        {
            var originalString = Marshal.StringToCoTaskMemUni("Scout");
            var original = Marshal.AllocCoTaskMem(2 * sizeof(NativeUnrealReflection.NativeUnrealValue));
            var originalFields = (NativeUnrealReflection.NativeUnrealValue*)original;
            originalFields[0] = new() { Kind = 13, Reserved = 5, Data = unchecked((ulong)originalString) };
            originalFields[1] = new() { Kind = 6, Data = 4 };
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = 15,
                        Reserved = 2,
                        Data = unchecked((ulong)original)
                    }) == 0,
                "Managed non-POD struct pre-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            var replacementFields = (NativeUnrealReflection.NativeUnrealValue*)replacement.Data;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacement.Kind == 15
                && replacement.Reserved == 2
                && replacementFields[0].Reserved == 8
                && Marshal.PtrToStringUni(unchecked((nint)replacementFields[0].Data), 8) == "Vanguard"
                && replacementFields[1].Data == 9,
                "Managed non-POD struct pre-hook did not transfer recursive replacement ownership.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }

        var tagDescriptor = new UnrealStructDescriptor(
            "/Script/Test.Tag",
            8,
            8,
            [new("Name", "NameProperty", 0, 8)]);
        var tagArrayDescriptor = new UnrealArrayDescriptor(
            "StructProperty:/Script/Test.Tag",
            8,
            ElementStruct: tagDescriptor);
        var envelopeDescriptor = new UnrealStructDescriptor(
            "/Script/Test.DamageEnvelope",
            24,
            8,
            [
                new("Source", "WeakObjectProperty:/Script/Engine.Actor", 0, 8),
                new("Tags", "ArrayProperty", 8, 16, Array: tagArrayDescriptor)
            ]);
        var envelopeFunction = new UnrealFunctionDescriptor(
            "/Script/Test.HookOwner",
            "/Script/Test.HookOwner:ObserveEnvelope",
            "ObserveEnvelope",
            "FUNC_Public",
            [new("Input", "StructProperty:/Script/Test.DamageEnvelope", 0, 1, "CPF_Parm", 24, Struct: envelopeDescriptor)]);
        using (reflection.RegisterHook(envelopeFunction, UnrealHookPhase.Pre, hook =>
               {
                   var observed = hook.Arguments["Input"].As<UnrealStructValue>();
                   var tags = observed.GetField("Tags").As<UnrealArrayValue>();
                   Assert(observed.GetField("Source").As<UnrealObjectHandle>().Value == 0x0000_0007_0000_0011
                       && tags.Elements.Count == 1
                       && tags.Elements[0].As<UnrealStructValue>().GetField("Name").As<string>() == "Damage.Bullet",
                       "Managed struct-with-container hook did not decode its recursive snapshot.");
                   hook.SetArgument(
                       "Input",
                       UnrealValue.From(new UnrealStructValue(
                           envelopeDescriptor,
                           new Dictionary<string, UnrealValue>
                           {
                               ["Source"] = UnrealValue.From(new UnrealObjectHandle(0x0000_0007_0000_0011)),
                               ["Tags"] = UnrealArrayValue.From(
                                   tagArrayDescriptor,
                                   ["Damage.Critical", "Damage.Weakpoint"],
                                   name => UnrealValue.From(new UnrealStructValue(
                                       tagDescriptor,
                                       new Dictionary<string, UnrealValue>
                                       {
                                           ["Name"] = UnrealValue.From(name)
                                       })))
                           })));
               }))
        {
            var originalName = Marshal.StringToCoTaskMemUni("Damage.Bullet");
            var originalTagFields = Marshal.AllocCoTaskMem(sizeof(NativeUnrealReflection.NativeUnrealValue));
            *(NativeUnrealReflection.NativeUnrealValue*)originalTagFields = new()
            {
                Kind = 14,
                Reserved = 13,
                Data = unchecked((ulong)originalName)
            };
            var originalTags = Marshal.AllocCoTaskMem(sizeof(NativeUnrealReflection.NativeUnrealValue));
            *(NativeUnrealReflection.NativeUnrealValue*)originalTags = new()
            {
                Kind = 15,
                Reserved = 1,
                Data = unchecked((ulong)originalTagFields)
            };
            var originalEnvelope = Marshal.AllocCoTaskMem(2 * sizeof(NativeUnrealReflection.NativeUnrealValue));
            var originalEnvelopeFields = (NativeUnrealReflection.NativeUnrealValue*)originalEnvelope;
            originalEnvelopeFields[0] = new() { Kind = 19, Data = 0x0000_0007_0000_0011 };
            originalEnvelopeFields[1] = new()
            {
                Kind = 17U | (15U << 8),
                Reserved = 1,
                Data = unchecked((ulong)originalTags)
            };
            Assert(NativeHookTestCallbacks.DispatchNative(
                    0x0000_0007_0000_002A,
                    new NativeUnrealReflection.NativeUnrealValue
                    {
                        Kind = 15,
                        Reserved = 2,
                        Data = unchecked((ulong)originalEnvelope)
                    }) == 0,
                "Managed struct-with-container pre-hook rejected the native snapshot.");
            var replacement = NativeHookTestCallbacks.Parameters[0].Value;
            var replacementFields = (NativeUnrealReflection.NativeUnrealValue*)replacement.Data;
            var replacementTags = (NativeUnrealReflection.NativeUnrealValue*)replacementFields[1].Data;
            var firstReplacementTag = (NativeUnrealReflection.NativeUnrealValue*)replacementTags[0].Data;
            var secondReplacementTag = (NativeUnrealReflection.NativeUnrealValue*)replacementTags[1].Data;
            Assert((NativeHookTestCallbacks.Parameters[0].Flags & 8U) != 0
                && replacementFields[0].Kind == 19
                && replacementFields[0].Data == 0x0000_0007_0000_0011
                && replacementFields[1].Kind == (17U | (15U << 8))
                && replacementFields[1].Reserved == 2
                && Marshal.PtrToStringUni(unchecked((nint)firstReplacementTag[0].Data), 15) == "Damage.Critical"
                && Marshal.PtrToStringUni(unchecked((nint)secondReplacementTag[0].Data), 16) == "Damage.Weakpoint",
                "Managed struct-with-container hook did not transfer recursive replacement ownership.");
            NativeHookTestCallbacks.ReleaseTransportedValues();
        }
    }

    static void ProfileLoads()
    {
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        Assert(profile.SteamAppId == 3228590, "Unexpected Steam app id.");
        Assert(profile.Ue4ss.CompatibilityFiles.Count == 1, "Compatibility file is missing.");
        Assert(profile.Ue4ss.EngineVersionOverride is { MajorVersion: 5, MinorVersion: 6 },
            "Deadzone UE4SS engine-version override is missing.");
    }

    static void FingerprintsAreNormalized()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "first.ini");
        var second = Path.Combine(directory.Path, "second.ini");
        File.WriteAllText(first, "[Section]\r\nValue=1\r\n");
        File.WriteAllText(second, "[Section]\nValue=1\n\n  \n");
        Assert(FileFingerprint.ComputeNormalizedTextSha256(first) == FileFingerprint.ComputeNormalizedTextSha256(second), "Fingerprints differ.");
    }

    static void InspectorAcceptsCompleteInstallation()
    {
        using var directory = new TemporaryDirectory();
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        Touch(directory.Path, profile.ExecutableRelativePath);
        Touch(directory.Path, profile.Ue4ss.ProxyRelativePath);
        Touch(directory.Path, profile.Ue4ss.LibraryRelativePath);

        var compatibility = profile.Ue4ss.CompatibilityFiles.Single();
        var source = FindRepositoryFile(compatibility.SourceRelativePath);
        var destination = Combine(directory.Path, compatibility.DestinationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);

        var modsFile = Combine(directory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
        File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nRogueModBridge : 1\nHelloNativeMod : 1\n");
        var settingsFile = Combine(directory.Path, profile.Ue4ss.RootRelativePath, "UE4SS-settings.ini");
        File.WriteAllText(settingsFile, "[EngineVersionOverride]\nMajorVersion = 5\nMinorVersion = 6\n");

        var report = new InstallationInspector().Inspect(profile, directory.Path);
        Assert(report.IsCompatible, string.Join(Environment.NewLine, report.Checks.Where(check => check.Status == DiagnosticStatus.Fail)));
        Assert(report.Checks.Single(check => check.Id == "built-in-mods").Status == DiagnosticStatus.Pass,
            "Enabled RogueMod components were mistaken for bundled UE4SS mods.");
        Assert(report.Checks.Single(check => check.Id == "ue4ss-engine-version").Status == DiagnosticStatus.Pass,
            "The required UE4SS engine-version override was not accepted.");

        File.WriteAllText(settingsFile, "[EngineVersionOverride]\nMajorVersion = \nMinorVersion = \n");
        var unsafeReport = new InstallationInspector().Inspect(profile, directory.Path);
        Assert(!unsafeReport.IsCompatible
            && unsafeReport.Checks.Single(check => check.Id == "ue4ss-engine-version").Status == DiagnosticStatus.Fail,
            "An unpinned UE4SS engine version was not rejected.");
    }

    static void ManifestRejectsUnsafeEntryPoint()
    {
        var manifest = new ModManifest("sample.mod", "Sample", "1.0.0", ModKind.Managed, "/absolute/mod.dll");
        Assert(manifest.Validate().Count > 0, "Unsafe manifest was accepted.");

        var selfDependent = new ModManifest(
            "sample.mod",
            "Sample",
            "1.0.0",
            ModKind.Managed,
            "mod.dll::Sample.Mod",
            ["sample.mod"]);
        Assert(selfDependent.Validate().Any(error => error.Contains("itself", StringComparison.Ordinal)), "Self dependency was accepted.");

        var nativeWithoutLoaderId = new ModManifest(
            "sample.native",
            "Sample native",
            "1.0.0",
            ModKind.Native,
            "dlls/main.dll");
        Assert(nativeWithoutLoaderId.Validate().Any(error => error.Contains("loaderId", StringComparison.Ordinal)),
            "Native manifest without loaderId was accepted.");
    }

    static void ManagedPackageManifestLoads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "mod.json");
        File.WriteAllText(path, """
        {"id":"sample.hello-managed","name":"Hello","version":"0.1.0","kind":"managed","entryPoint":"dlls/Hello.dll::Hello.Mod"}
        """);
        var manifest = ModManifestLoader.Load(path);
        Assert(manifest.Kind == ModKind.Managed, "Managed kind was not parsed.");
        Assert(manifest.EntryPoint == "dlls/Hello.dll::Hello.Mod", "Managed entry point changed.");
    }

    static void ManifestLoadsLocalizedMetadata()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "mod.json");
        File.WriteAllText(path, """
        {
          "id":"sample.localized",
          "name":"Localized sample",
          "version":"1.0.0",
          "kind":"managed",
          "entryPoint":"dlls/Sample.dll::Sample.Mod",
          "description":"English description",
          "icon":"media/icon.png",
          "images":["media/first.webp"],
          "defaultLanguage":"en",
          "supportedLanguages":["en","ru","uk"],
          "localizations":{
            "ru":{"description":"Описание на русском"},
            "uk":{"description":"Опис українською"}
          }
        }
        """);

        var manifest = ModManifestLoader.Load(path);
        Assert(manifest.Description == "English description", "Default description was not loaded.");
        Assert(manifest.Icon == "media/icon.png" && manifest.Images?.Single() == "media/first.webp",
            "Mod media metadata was not loaded.");
        Assert(manifest.SupportedLanguages?.SequenceEqual(["en", "ru", "uk"]) == true,
            "Supported languages changed while loading.");
        Assert(manifest.Localizations?["ru"].Description == "Описание на русском",
            "Localized description was not loaded.");

        var invalid = manifest with { SupportedLanguages = ["en", "not-a-game-language"] };
        Assert(invalid.Validate().Any(error => error.Contains("not supported", StringComparison.Ordinal)),
            "An unknown game language was accepted.");
        var escapingImage = manifest with { Icon = "../icon.png" };
        Assert(escapingImage.Validate().Any(error => error.Contains("inside", StringComparison.Ordinal)),
            "An escaping media path was accepted.");
    }

    static void ManagedPackageInstallsTransactionally()
    {
        using var gameDirectory = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory();
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        var assemblyPath = typeof(TestManagedMod).Assembly.Location;
        var dllDirectory = Path.Combine(packageDirectory.Path, "dlls");
        Directory.CreateDirectory(dllDirectory);
        File.Copy(assemblyPath, Path.Combine(dllDirectory, Path.GetFileName(assemblyPath)));
        Touch(packageDirectory.Path, "media/icon.png");
        Touch(packageDirectory.Path, "media/gallery.webp");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "mod.json"), $$$$"""
        {"id":"sample.mod","name":"Sample","version":"1.0.0","kind":"managed","entryPoint":"dlls/{{{{Path.GetFileName(assemblyPath)}}}}::{{{{typeof(TestManagedMod).FullName}}}}","description":"Sample description","icon":"media/icon.png","images":["media/gallery.webp"],"defaultLanguage":"en","supportedLanguages":["en","ru"],"localizations":{"ru":{"description":"Описание"}}}
        """);

        var installer = new ManagedModInstaller();
        var result = installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
        Assert(result.Destination == Path.Combine(gameDirectory.Path, "Mods", "sample.mod"),
            "Managed mod was not installed in the game-root Mods directory.");
        Assert(File.Exists(Path.Combine(result.Destination, "mod.json")), "Installed manifest is missing.");
        Assert(File.Exists(Path.Combine(result.Destination, "dlls", Path.GetFileName(assemblyPath))), "Installed assembly is missing.");
        Assert(File.Exists(Path.Combine(result.Destination, "media", "icon.png")), "Installed mod icon is missing.");

        var refusedReplacement = false;
        try
        {
            installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
        }
        catch (IOException)
        {
            refusedReplacement = true;
        }
        Assert(refusedReplacement, "Existing mod was replaced without explicit permission.");

        var replaced = installer.Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
        Assert(replaced.Replaced, "Explicit replacement was not reported.");
        var parent = Directory.GetParent(replaced.Destination)!;
        Assert(!parent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Staging directory was left behind.");
        Assert(!parent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Backup directory was left behind.");
    }

    static void RuntimeInstallsAndActivatesTransactionally()
    {
        using var gameDirectory = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory();
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        Touch(packageDirectory.Path, "dlls/main.dll");
        Touch(packageDirectory.Path, "runtime/managed/RogueMod.Runtime.dll");
        Touch(packageDirectory.Path, "runtime/managed/RogueMod.Runtime.runtimeconfig.json");
        Touch(packageDirectory.Path, "runtime/dotnet/host/fxr/10.0.10/hostfxr.dll");
        var modsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
        File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nKeybinds : 1\n");
        var settingsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "UE4SS-settings.ini");
        File.WriteAllText(settingsFile, "[EngineVersionOverride]\nMajorVersion = \nMinorVersion = \nDebugBuild = \n");

        var result = new RogueModRuntimeInstaller().Install(profile, gameDirectory.Path, packageDirectory.Path);
        var expectedRuntimeRoot = Path.Combine(gameDirectory.Path, RogueModLayout.RuntimeDirectoryName);
        var expectedBridgeRoot = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/RogueModBridge");
        Assert(result.Destination == expectedRuntimeRoot, "Technical runtime was not installed in the game-root RogueMod directory.");
        Assert(result.BridgeDeployment == expectedBridgeRoot, "Bridge deployment path is incorrect.");
        Assert(File.Exists(Path.Combine(result.Destination, "runtime", "managed", "RogueMod.Runtime.dll")),
            "Managed runtime was not installed in the technical RogueMod directory.");
        Assert(File.Exists(Path.Combine(expectedBridgeRoot, "dlls", "main.dll")), "Runtime bridge was not deployed to UE4SS.");
        Assert(!Directory.Exists(Path.Combine(expectedBridgeRoot, "runtime")), "UE4SS bridge deployment contains the full runtime.");
        var userModsRoot = Path.Combine(gameDirectory.Path, "Mods");
        Assert(!Directory.Exists(userModsRoot) || !Directory.EnumerateFileSystemEntries(userModsRoot).Any(),
            "The runtime installer polluted the user-mod directory before any mods were migrated.");
        var lines = File.ReadAllLines(modsFile);
        Assert(lines.Count(line => line == "RogueModBridge : 1") == 1, "Runtime was not activated exactly once.");
        Assert(Array.IndexOf(lines, "RogueModBridge : 1") < Array.IndexOf(lines, "Keybinds : 1"), "Runtime was inserted below Keybinds.");
        var settings = File.ReadAllText(settingsFile);
        Assert(settings.Contains("MajorVersion = 5", StringComparison.Ordinal)
            && settings.Contains("MinorVersion = 6", StringComparison.Ordinal),
            "Runtime installation did not pin the UE4SS engine version.");

        var legacyMod = Path.Combine(result.Destination, "managed-mods", "user.mod");
        Directory.CreateDirectory(Path.Combine(legacyMod, "dlls"));
        File.WriteAllText(Path.Combine(legacyMod, "mod.json"),
            """{"id":"user.mod","name":"User mod","version":"1.0.0","kind":"managed","entryPoint":"dlls/UserMod.dll::UserMod.Entry"}""");
        Touch(legacyMod, "dlls/UserMod.dll");
        Touch(result.Destination, "runtime/shared/DeadzoneRogue.Sdk.dll");
        Directory.Delete(expectedBridgeRoot, recursive: true);
        var legacyDestination = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods", RogueModLayout.LegacyLoaderModName);
        Directory.Move(result.Destination, legacyDestination);
        File.WriteAllText(modsFile, "RogueMod : 1\nKeybinds : 1\n");
        var replaced = new RogueModRuntimeInstaller().Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
        Assert(replaced.MigratedFromLegacyLayout, "Legacy runtime layout migration was not reported.");
        Assert(replaced.MigratedManagedModCount == 1, "Legacy managed mod migration count is incorrect.");
        Assert(!Directory.Exists(legacyDestination), "Legacy runtime directory was left behind.");
        Assert(File.Exists(Path.Combine(gameDirectory.Path, "Mods", "user.mod", "mod.json")),
            "Runtime update did not migrate the managed mod to the game-root Mods directory.");
        Assert(!Directory.Exists(Path.Combine(replaced.Destination, "managed-mods")),
            "The new runtime still contains the legacy managed-mods directory.");
        Assert(File.Exists(Path.Combine(replaced.Destination, "runtime", "shared", "DeadzoneRogue.Sdk.dll")),
            "Runtime migration discarded the generated shared game SDK.");
        Assert(!Directory.Exists(Path.Combine(replaced.BridgeDeployment!, "runtime")),
            "Migrated UE4SS bridge deployment contains technical runtime files.");
        Assert(File.ReadAllLines(modsFile).Contains("RogueModBridge : 1"), "Migrated runtime was not activated under its bridge name.");
        Assert(!File.ReadAllLines(modsFile).Any(line => line.StartsWith("RogueMod :", StringComparison.Ordinal)), "Legacy runtime activation was left behind.");
    }

    static void NativePackageInstallsAndActivatesTransactionally()
    {
        using var gameDirectory = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory();
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        Touch(packageDirectory.Path, "dlls/main.dll");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "mod.json"), """
        {"id":"sample.native","name":"Sample native","version":"1.0.0","kind":"native","entryPoint":"dlls/main.dll","loaderId":"SampleNativeMod"}
        """);
        var modsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(modsFile)!);
        File.WriteAllText(modsFile, "ConsoleEnablerMod : 0\nSampleNativeMod : 0\nSampleNativeMod : 0\nKeybinds : 1\n");

        var installer = new NativeModInstaller();
        var result = installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
        Assert(File.Exists(Path.Combine(result.Destination, "dlls", "main.dll")), "Native entry DLL was not installed.");
        Assert(File.Exists(Path.Combine(result.Deployment, "dlls", "main.dll")), "Native entry DLL was not deployed to UE4SS.");
        var lines = File.ReadAllLines(modsFile);
        Assert(Path.GetFileName(result.Destination) == "sample.native", "Native package was not stored under its package id.");
        Assert(Path.GetFileName(result.Deployment) == "SampleNativeMod", "Native mod was not deployed under its UE4SS loaderId.");
        Assert(lines.Count(line => line == "SampleNativeMod : 1") == 1, "Native mod was not activated exactly once.");
        Assert(Array.IndexOf(lines, "SampleNativeMod : 1") < Array.IndexOf(lines, "Keybinds : 1"), "Native mod was activated below Keybinds.");

        var refusedReplacement = false;
        try
        {
            installer.Install(profile, gameDirectory.Path, packageDirectory.Path);
        }
        catch (IOException)
        {
            refusedReplacement = true;
        }
        Assert(refusedReplacement, "Existing native mod was replaced without explicit permission.");

        var replaced = installer.Install(profile, gameDirectory.Path, packageDirectory.Path, replace: true);
        Assert(replaced.Replaced, "Explicit native replacement was not reported.");
        var parent = Directory.GetParent(replaced.Destination)!;
        Assert(!parent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Native staging directory was left behind.");
        Assert(!parent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Native backup directory was left behind.");
        var deploymentParent = Directory.GetParent(replaced.Deployment)!;
        Assert(!deploymentParent.EnumerateDirectories(".stage-*", SearchOption.TopDirectoryOnly).Any(), "Native deployment staging directory was left behind.");
        Assert(!deploymentParent.EnumerateDirectories(".backup-*", SearchOption.TopDirectoryOnly).Any(), "Native deployment backup directory was left behind.");
    }

    static void ModManagerControlsAllPackageKinds()
    {
        using var gameDirectory = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory();
        var profile = GameProfileLoader.Load(FindRepositoryFile("config/Profiles/deadzone-rogue.json"));
        var manager = new ModManager();
        var managed = Path.Combine(packageDirectory.Path, "managed-v1");
        var managedUpdate = Path.Combine(packageDirectory.Path, "managed-v2");
        var native = Path.Combine(packageDirectory.Path, "native");
        var lua = Path.Combine(packageDirectory.Path, "lua");
        var pak = Path.Combine(packageDirectory.Path, "pak");

        CreateManagedPackage(managed, "manager.managed", "1.0.0");
        CreateManagedPackage(managedUpdate, "manager.managed", "1.1.0");
        CreatePackage(native,
            """{"id":"manager.native","name":"Manager native","version":"1.0.0","kind":"native","entryPoint":"dlls/main.dll","loaderId":"ManagerNative"}""",
            "dlls/main.dll");
        CreatePackage(lua,
            """{"id":"manager.lua","name":"Manager lua","version":"1.0.0","kind":"lua","entryPoint":"Scripts/main.lua","loaderId":"ManagerLua"}""",
            "Scripts/main.lua");
        CreatePackage(pak,
            """{"id":"manager.pak","name":"Manager pak","version":"1.0.0","kind":"pak","entryPoint":"paks/Manager.pak"}""",
            "paks/Manager.pak",
            "paks/Manager.utoc",
            "paks/Manager.ucas");

        var managedInstall = manager.Install(profile, gameDirectory.Path, managed);
        var nativeInstall = manager.Install(profile, gameDirectory.Path, native);
        var luaInstall = manager.Install(profile, gameDirectory.Path, lua);
        var pakInstall = manager.Install(profile, gameDirectory.Path, pak);
        Assert(managedInstall.Deployments.Count == 0, "Managed mod unexpectedly created a deployment copy.");
        Assert(nativeInstall.Deployments.Count == 1 && Directory.Exists(nativeInstall.Deployments[0]),
            "Native deployment was not created.");
        Assert(luaInstall.Deployments.Count == 1 && Directory.Exists(luaInstall.Deployments[0]),
            "Lua deployment was not created.");
        Assert(pakInstall.Deployments.Count == 3 && pakInstall.Deployments.All(File.Exists),
            "Pak payload and companions were not deployed.");
        Assert(manager.List(profile, gameDirectory.Path).All(mod => mod.State == ModActivationState.Enabled),
            "A newly installed mod was not enabled.");

        Directory.Delete(nativeInstall.Deployments[0], recursive: true);
        Assert(manager.List(profile, gameDirectory.Path).Single(mod => mod.Manifest.Id == "manager.native").State == ModActivationState.Broken,
            "A missing enabled native deployment was not reported as broken.");
        manager.SetEnabled(profile, gameDirectory.Path, "manager.native", enabled: true);
        Assert(Directory.Exists(nativeInstall.Deployments[0]), "Enabling did not repair the native deployment.");

        foreach (var id in new[] { "manager.managed", "manager.native", "manager.lua", "manager.pak" })
        {
            manager.SetEnabled(profile, gameDirectory.Path, id, enabled: false);
        }
        Assert(manager.List(profile, gameDirectory.Path).All(mod => mod.State == ModActivationState.Disabled),
            "Disabled state was not reported for every package kind.");
        Assert(File.Exists(Path.Combine(managedInstall.Destination, RogueModLayout.DisabledMarkerFileName)),
            "Managed disable marker was not written.");
        Assert(pakInstall.Deployments.All(path => !File.Exists(path)), "Pak deployment remained mounted after disable.");

        var update = manager.Update(profile, gameDirectory.Path, managedUpdate);
        Assert(update.PreviousVersion == "1.0.0" && update.CurrentVersion == "1.1.0", "Managed update versions are incorrect.");
        Assert(update.PreservedDisabledState, "Update did not report preserved disabled state.");
        Assert(manager.List(profile, gameDirectory.Path).Single(mod => mod.Manifest.Id == "manager.managed").State == ModActivationState.Disabled,
            "Managed update silently enabled a disabled mod.");

        foreach (var id in new[] { "manager.managed", "manager.native", "manager.lua", "manager.pak" })
        {
            manager.SetEnabled(profile, gameDirectory.Path, id, enabled: true);
        }
        Assert(manager.List(profile, gameDirectory.Path).All(mod => mod.State == ModActivationState.Enabled),
            "Enabled state was not restored for every package kind.");

        foreach (var id in new[] { "manager.managed", "manager.native", "manager.lua", "manager.pak" })
        {
            manager.Uninstall(profile, gameDirectory.Path, id);
        }
        Assert(manager.List(profile, gameDirectory.Path).Count == 0, "Uninstalled packages remained in the canonical store.");
        Assert(!Directory.Exists(nativeInstall.Deployments[0]), "Native deployment remained after uninstall.");
        Assert(!Directory.Exists(luaInstall.Deployments[0]), "Lua deployment remained after uninstall.");
        Assert(pakInstall.Deployments.All(path => !File.Exists(path)), "Pak deployment remained after uninstall.");
        var modsFile = Combine(gameDirectory.Path, profile.Ue4ss.RootRelativePath, "Mods/mods.txt");
        Assert(!File.ReadAllText(modsFile).Contains("ManagerNative", StringComparison.Ordinal),
            "Native activation line remained after uninstall.");
        Assert(!File.ReadAllText(modsFile).Contains("ManagerLua", StringComparison.Ordinal),
            "Lua activation line remained after uninstall.");
    }

    static void CreateManagedPackage(string root, string id, string version)
    {
        var assembly = typeof(TestManagedMod).Assembly.Location;
        var relativeAssembly = $"dlls/{Path.GetFileName(assembly)}";
        CreatePackage(root,
            $$"""{"id":"{{id}}","name":"Manager managed","version":"{{version}}","kind":"managed","entryPoint":"{{relativeAssembly}}::{{typeof(TestManagedMod).FullName}}"}""",
            relativeAssembly);
        File.Copy(assembly, Combine(root, relativeAssembly), overwrite: true);
    }

    static void CreatePackage(string root, string manifest, params string[] files)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mod.json"), manifest);
        foreach (var file in files)
        {
            Touch(root, file);
        }
    }

    static unsafe void NativeBootstrapValidatesAbi()
    {
        using var directory = new TemporaryDirectory();
        Assert(sizeof(NativeBootstrapTestCallbacks.HostApi) == 160, "Managed ABI 13 host table has an unexpected size.");
        Assert(sizeof(NativeBootstrapTestCallbacks.NativeUnrealParameter) == 40, "Managed ABI 13 parameter has an unexpected size.");
        NativeBootstrapTestCallbacks.Messages.Clear();
        NativeBootstrapTestCallbacks.PropertyWritten = false;
        NativeBootstrapTestCallbacks.StringPropertyWritten = false;
        NativeBootstrapTestCallbacks.OptionalPropertyWritten = false;
        NativeBootstrapTestCallbacks.OptionalUnsetPropertyWritten = false;
        NativeBootstrapTestCallbacks.WeakPropertyWritten = false;
        NativeBootstrapTestCallbacks.WeakNullPropertyWritten = false;
        NativeBootstrapTestCallbacks.LazyPropertyWritten = false;
        NativeBootstrapTestCallbacks.LazyNullPropertyWritten = false;
        NativeBootstrapTestCallbacks.SoftPropertyWritten = false;
        NativeBootstrapTestCallbacks.MapPropertyWritten = false;
        NativeBootstrapTestCallbacks.SetPropertyWritten = false;
        NativeBootstrapTestCallbacks.ObjectCreated = false;
        NativeBootstrapTestCallbacks.ActorSpawned = false;

        var assemblyPath = typeof(TestManagedMod).Assembly.Location;
        var modsRoot = Path.Combine(directory.Path, "Mods");
        var modDirectory = Path.Combine(modsRoot, "sample.mod");
        var dllDirectory = Path.Combine(modDirectory, "dlls");
        Directory.CreateDirectory(dllDirectory);
        File.Copy(assemblyPath, Path.Combine(dllDirectory, Path.GetFileName(assemblyPath)));
        File.WriteAllText(Path.Combine(modDirectory, "mod.json"), $$"""
        {"id":"sample.mod","name":"Sample","version":"1.0.0","kind":"managed","entryPoint":"dlls/{{Path.GetFileName(assemblyPath)}}::{{typeof(TestManagedMod).FullName}}"}
        """);

        var modRoot = directory.Path;
        var profileId = "deadzone-rogue-steam";
        fixed (char* modRootPointer = modRoot)
        fixed (char* profileIdPointer = profileId)
        fixed (char* modsRootPointer = modsRoot)
        {
            var api = new NativeBootstrapTestCallbacks.HostApi
            {
                Size = (uint)sizeof(NativeBootstrapTestCallbacks.HostApi),
                AbiVersion = 13,
                Log = &NativeBootstrapTestCallbacks.CaptureLog,
                ModRoot = modRootPointer,
                GameProfileId = profileIdPointer,
                UnrealIsAvailable = &NativeBootstrapTestCallbacks.UnrealIsAvailable,
                UnrealFindFirstOf = &NativeBootstrapTestCallbacks.UnrealFindFirstOf,
                UnrealIsValid = &NativeBootstrapTestCallbacks.UnrealIsValid,
                UnrealGetClass = &NativeBootstrapTestCallbacks.UnrealGetClass,
                UnrealGetPathName = &NativeBootstrapTestCallbacks.UnrealGetPathName,
                UnrealGetCapabilities = &NativeBootstrapTestCallbacks.UnrealGetCapabilities,
                UnrealInvokeZeroParameter = &NativeBootstrapTestCallbacks.UnrealInvokeZeroParameter,
                UnrealReadProperty = &NativeBootstrapTestCallbacks.UnrealReadProperty,
                UnrealWriteProperty = &NativeBootstrapTestCallbacks.UnrealWriteProperty,
                UnrealInvoke = &NativeBootstrapTestCallbacks.UnrealInvoke,
                GameModsRoot = modsRootPointer,
                UnrealFindAllOf = &NativeBootstrapTestCallbacks.UnrealFindAllOf,
                UnrealRegisterHook = &NativeBootstrapTestCallbacks.UnrealRegisterHook,
                UnrealUnregisterHook = &NativeBootstrapTestCallbacks.UnrealUnregisterHook,
                UnrealCreateObject = &NativeBootstrapTestCallbacks.UnrealCreateObject,
                UnrealSpawnActor = &NativeBootstrapTestCallbacks.UnrealSpawnActor
            };
            delegate* unmanaged[Cdecl]<nint, int> initialize = &NativeBootstrap.Initialize;
            delegate* unmanaged[Cdecl]<int, int> dispatchGameEvent = &NativeBootstrap.DispatchGameEvent;
            delegate* unmanaged[Cdecl]<int> shutdown = &NativeBootstrap.Shutdown;
            Assert(initialize((nint)(&api)) == 0, "Native bootstrap rejected ABI version 13.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] loaded:sample.mod"), "Installed managed mod was not loaded.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] reflection:/Test/PlayerController"), "Native reflection ABI was not exposed to the managed mod.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] discovery:/Test/PlayerController:1"), "Typed object discovery was not exposed to the managed mod.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] invoked:Pause"), "Generated-style zero-parameter UFunction wrapper was not invoked.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] marshalled:True:42"), "UFunction input/return/out values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] strings:ReturnName:Output String"), "FString/FName input, return, and out values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] text:Output Text"), "FText input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] array:4:5:6"), "TArray input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] nested-array:4|5,6"), "Nested TArray input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] struct:4:5:6"), "POD struct input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:SpawnLocation=7:8:9"), "Generated-style POD struct property was not read.");
            Assert(NativeBootstrapTestCallbacks.StructPropertyWritten, "Generated-style POD struct property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:bShouldPerformFullTickWhenPaused=True"), "Generated-style bool property was not read.");
            Assert(NativeBootstrapTestCallbacks.PropertyWritten, "Generated-style bool property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:PlayerName=Rogue"), "Generated-style FString property was not read.");
            Assert(NativeBootstrapTestCallbacks.StringPropertyWritten, "Generated-style FString property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:DisplayText=Display Text"), "Generated-style FText property was not read.");
            Assert(NativeBootstrapTestCallbacks.TextPropertyWritten, "Generated-style FText property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:Scores=7:8:9"), "Generated-style TArray property was not read.");
            Assert(NativeBootstrapTestCallbacks.ArrayPropertyWritten, "Generated-style TArray property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:ScoreGroups=7,8|9"), "Generated-style nested TArray property was not read.");
            Assert(NativeBootstrapTestCallbacks.NestedArrayPropertyWritten, "Generated-style nested TArray property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:ScoresByName=7:Rogue:8:Vera"), "Generated-style TMap property was not read.");
            Assert(NativeBootstrapTestCallbacks.MapPropertyWritten, "Generated-style TMap property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:UniqueScores=7,8,9"), "Generated-style TSet property was not read.");
            Assert(NativeBootstrapTestCallbacks.SetPropertyWritten, "Generated-style TSet property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:OptionalScore=set:11"), "Generated-style TOptional property was not read.");
            Assert(NativeBootstrapTestCallbacks.OptionalPropertyWritten, "Generated-style TOptional property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:OptionalUnsetScore=unset"), "Generated-style unset TOptional property was not read.");
            Assert(NativeBootstrapTestCallbacks.OptionalUnsetPropertyWritten, "Generated-style unset TOptional property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] optional:set:13"), "TOptional UFunction input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:WeakController=/Test/PlayerController"), "Generated-style weak UObject property was not read.");
            Assert(NativeBootstrapTestCallbacks.WeakNullPropertyWritten, "A null weak UObject property was not written.");
            Assert(NativeBootstrapTestCallbacks.WeakPropertyWritten, "A non-null weak UObject property was not written.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] weak:/Test/PlayerController"), "Weak UObject UFunction input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:LazyController=11111111-22222222-33333333-44444444:/Test/PlayerController"), "Generated-style lazy UObject property did not preserve identity and resolve its target.");
            Assert(NativeBootstrapTestCallbacks.LazyNullPropertyWritten, "A null lazy UObject property was not written.");
            Assert(NativeBootstrapTestCallbacks.LazyPropertyWritten, "An identity-bearing lazy UObject property was not restored.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] lazy:11111111-22222222-33333333-44444444:/Test/PlayerController"), "Lazy UObject UFunction input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] soft:/Game/Test/ManagedAbi.ManagedAbi:/Test/PlayerController"), "Soft UObject property path and cached target were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.SoftPropertyWritten, "A soft UObject property was not round-tripped.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] property:Leader=/Test/PlayerController"), "Generated-style interface UObject property was not read.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] interface:/Test/PlayerController"), "Interface UObject UFunction input and return values were not marshalled.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] created:000000070000002B"), "Managed object creation did not reach the native ABI.");
            Assert(NativeBootstrapTestCallbacks.ObjectCreated, "Managed object creation arguments were not forwarded correctly.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] spawned:000000070000002C"), "Managed actor spawning did not reach the native ABI.");
            Assert(NativeBootstrapTestCallbacks.ActorSpawned, "Managed actor spawning arguments were not forwarded correctly.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[ManagedRuntime] Managed runtime initialized. Loaded 1 mod(s)."), "Initialization was not logged.");
            Assert(dispatchGameEvent((int)ModGameEventKind.ProgramStarted) == 0, "Game event was rejected.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] event:ProgramStarted"), "Game event did not reach the managed mod.");
            Assert(dispatchGameEvent(999) == -2, "Unknown game event was accepted.");
            Assert(shutdown() == 0, "Native bootstrap shutdown failed.");
            ForceCollectibleContextsToUnload();
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[C#:sample.mod] unloaded"), "Installed managed mod was not unloaded.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[ManagedRuntime] Managed runtime shut down."), "Shutdown was not logged.");

            File.WriteAllText(Path.Combine(modDirectory, RogueModLayout.DisabledMarkerFileName), "disabled");
            NativeBootstrapTestCallbacks.Messages.Clear();
            Assert(initialize((nint)(&api)) == 0, "Runtime rejected a valid host while testing disabled discovery.");
            Assert(NativeBootstrapTestCallbacks.Messages.Contains("[ManagedRuntime] Managed runtime initialized. Loaded 0 mod(s)."),
                "Disabled managed mod was loaded by the runtime.");
            Assert(shutdown() == 0, "Runtime shutdown failed after disabled discovery.");

            api.AbiVersion = 999;
            Assert(initialize((nint)(&api)) == -2, "Unsupported ABI version was accepted.");
        }
    }

    static async ValueTask ManagedModLoadsAndUnloads()
    {
        var logger = new TestLogger();
        var context = new TestModContext("sample.mod", "deadzone-rogue-steam", logger, new UnavailableUnrealReflection());
        var assemblyPath = typeof(TestManagedMod).Assembly.Location;
        var manifest = new ModManifest(
            "sample.mod",
            "Sample",
            "1.0.0",
            ModKind.Managed,
            $"{Path.GetFileName(assemblyPath)}::{typeof(TestManagedMod).FullName}");

        await using var host = await ManagedModHost.LoadAsync(manifest, Path.GetDirectoryName(assemblyPath)!, context);
        Assert(host.IsLoaded, "Managed mod was not loaded.");
        Assert(logger.Messages.Contains("loaded:sample.mod"), "Managed load callback was not invoked.");
        host.DispatchGameEvent(ModGameEventKind.UnrealInitialized);
        Assert(logger.Messages.Contains("event:UnrealInitialized"), "Managed game-event callback was not invoked.");

        await host.UnloadAsync();
        Assert(!host.IsLoaded, "Managed mod was not unloaded.");
        Assert(logger.Messages.Contains("unloaded"), "Managed unload callback was not invoked.");
    }

    static void JMapImportsAndGeneratesTypedSdk()
    {
        var softReference = UnrealSoftObjectReference<UnrealObject>.FromPath("/Game/Test/Asset.Asset");
        Assert(softReference.Path == "/Game/Test/Asset.Asset" && softReference.CachedTarget is null,
            "Typed soft reference construction did not preserve an unloaded asset path.");
        Assert(softReference.ToUnrealValue().As<UnrealSoftObjectValue>().Path == softReference.Path,
            "Typed soft reference construction did not produce the native transport value.");

        using var directory = new TemporaryDirectory();
        var jmapPath = Path.Combine(directory.Path, "fixture.jmap");
        File.WriteAllText(jmapPath, """
        {
          "metadata": {
            "timestamp": "2026-08-24T00:00:00Z",
            "engine_version": { "major": 5, "minor": 6 }
          },
          "objects": {
            "/Script/CoreUObject.Vector": {
              "type": "ScriptStruct",
              "super_struct": null,
              "properties_size": 24,
              "min_alignment": 8,
              "struct_flags": "STRUCT_IsPlainOldData | STRUCT_NoDestructor | STRUCT_ZeroConstructor",
              "children": [],
              "properties": [
                { "name": "X", "type": "DoubleProperty", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Y", "type": "DoubleProperty", "offset": 8, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Z", "type": "DoubleProperty", "offset": 16, "array_dim": 1, "size": 8, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Script/Valhalla.PlayerLoadoutEntry": {
              "type": "ScriptStruct",
              "super_struct": null,
              "properties_size": 48,
              "min_alignment": 8,
              "struct_flags": "STRUCT_HasDestructor",
              "children": [],
              "properties": [
                { "name": "DisplayName", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_BlueprintVisible" },
                { "name": "Level", "type": "IntProperty", "offset": 16, "array_dim": 1, "size": 4, "flags": "CPF_BlueprintVisible" },
                { "name": "Origin", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 24, "array_dim": 1, "size": 24, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Script/GameplayTags.GameplayTag": {
              "type": "ScriptStruct",
              "super_struct": null,
              "properties_size": 8,
              "min_alignment": 8,
              "struct_flags": "STRUCT_HasDestructor",
              "children": [],
              "properties": [
                { "name": "TagName", "type": "NameProperty", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" }
              ]
            },
            "/Script/Valhalla.DamageEnvelope": {
              "type": "ScriptStruct",
              "super_struct": null,
              "properties_size": 24,
              "min_alignment": 8,
              "struct_flags": "STRUCT_HasDestructor",
              "children": [],
              "properties": [
                { "name": "Source", "type": "WeakObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible | CPF_UObjectWrapper" },
                { "name": "Tags", "type": "ArrayProperty", "offset": 8, "array_dim": 1, "size": 16, "inner": { "name": "Tags", "type": "StructProperty", "struct": "/Script/GameplayTags.GameplayTag", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" }, "flags": "CPF_BlueprintVisible | CPF_ZeroConstructor" }
              ]
            },
            "/Script/Engine.Actor": {
              "type": "Class",
              "super_struct": null,
              "children": [],
              "properties": []
            },
            "/Game/Test.BP_Player_C": {
              "type": "Class",
              "super_struct": "/Script/Engine.Actor",
              "children": ["/Game/Test.BP_Player_C:SetHealth", "/Game/Test.BP_Player_C:GetHealth", "/Game/Test.BP_Player_C:SetPlayerName", "/Game/Test.BP_Player_C:SetLocation", "/Game/Test.BP_Player_C:GetLocation", "/Game/Test.BP_Player_C:EchoText", "/Game/Test.BP_Player_C:EchoNumbers", "/Game/Test.BP_Player_C:EchoNumberGroups", "/Game/Test.BP_Player_C:EchoOptional", "/Game/Test.BP_Player_C:EchoWeak", "/Game/Test.BP_Player_C:EchoLazy", "/Game/Test.BP_Player_C:EchoSoft", "/Game/Test.BP_Player_C:EchoScoresByName", "/Game/Test.BP_Player_C:EchoUniqueScores", "/Game/Test.BP_Player_C:EchoLoadout"],
              "properties": [
                { "name": "Health", "type": "FloatProperty", "offset": 256, "array_dim": 1, "size": 4, "flags": "CPF_Edit | CPF_BlueprintVisible" },
                { "name": "ReadOnlyTuning", "type": "FloatProperty", "offset": 260, "array_dim": 1, "size": 4, "flags": "CPF_BlueprintVisible | CPF_BlueprintReadOnly" },
                { "name": "EditConstSetting", "type": "FloatProperty", "offset": 252, "array_dim": 1, "size": 4, "flags": "CPF_BlueprintVisible | CPF_BlueprintReadOnly | CPF_EditConst" },
                { "name": "Target", "type": "ObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 264, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" },
                { "name": "PlayerName", "type": "StrProperty", "offset": 272, "array_dim": 1, "size": 16, "flags": "CPF_BlueprintVisible" },
                { "name": "Mode", "type": "NameProperty", "offset": 288, "array_dim": 1, "size": 8, "flags": "CPF_BlueprintVisible" },
                { "name": "DisplayText", "type": "TextProperty", "offset": 296, "array_dim": 1, "size": 16, "flags": "CPF_BlueprintVisible" },
                { "name": "Location", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 320, "array_dim": 1, "size": 24, "flags": "CPF_BlueprintVisible | CPF_IsPlainOldData | CPF_NoDestructor" },
                { "name": "Scores", "type": "ArrayProperty", "offset": 344, "array_dim": 1, "size": 16, "inner": { "name": "Scores", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_BlueprintVisible | CPF_ZeroConstructor" },
                { "name": "ScoreGroups", "type": "ArrayProperty", "offset": 360, "array_dim": 1, "size": 16, "inner": { "name": "ScoreGroups", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "ScoreGroup", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_ZeroConstructor" }, "flags": "CPF_BlueprintVisible | CPF_ZeroConstructor" },
                { "name": "PreferredScore", "type": "OptionalProperty", "offset": 376, "array_dim": 1, "size": 8, "inner": { "name": "PreferredScore", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Edit | CPF_BlueprintVisible | CPF_ZeroConstructor" },
                { "name": "WeakTarget", "type": "WeakObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 384, "array_dim": 1, "size": 8, "flags": "CPF_Edit | CPF_BlueprintVisible | CPF_UObjectWrapper" },
                { "name": "LazyTarget", "type": "LazyObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 392, "array_dim": 1, "size": 24, "flags": "CPF_Edit | CPF_BlueprintVisible | CPF_UObjectWrapper" },
                { "name": "SoftTarget", "type": "SoftObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 416, "array_dim": 1, "size": 40, "flags": "CPF_Edit | CPF_BlueprintVisible | CPF_UObjectWrapper" },
                { "name": "ScoresByName", "type": "MapProperty", "offset": 440, "array_dim": 1, "size": 80, "key_prop": { "name": "Key", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "value_prop": { "name": "Value", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_BlueprintVisible" },
                { "name": "UniqueScores", "type": "SetProperty", "offset": 456, "array_dim": 1, "size": 80, "key_prop": { "name": "Element", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_BlueprintVisible" },
                { "name": "Loadout", "type": "StructProperty", "struct": "/Script/Valhalla.PlayerLoadoutEntry", "offset": 536, "array_dim": 1, "size": 48, "flags": "CPF_BlueprintVisible" },
                { "name": "PlayerLoadoutEntry", "type": "StructProperty", "struct": "/Script/Valhalla.PlayerLoadoutEntry", "offset": 584, "array_dim": 1, "size": 48, "flags": "CPF_BlueprintVisible" },
                { "name": "LastDamage", "type": "StructProperty", "struct": "/Script/Valhalla.DamageEnvelope", "offset": 632, "array_dim": 1, "size": 24, "flags": "CPF_BlueprintVisible" }
              ]
            },
            "/Game/Test.BP_Player_C:SetHealth": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewHealth", "type": "FloatProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_Parm" }
              ]
            },
            "/Game/Test.BP_Player_C:GetHealth": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "ReturnValue", "type": "FloatProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_Parm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:SetPlayerName": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewName", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_Parm" }
              ]
            },
            "/Game/Test.BP_Player_C:SetLocation": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "NewLocation", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 0, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Game/Test.BP_Player_C:GetLocation": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable",
              "properties": [
                { "name": "ReturnValue", "type": "StructProperty", "struct": "/Script/CoreUObject.Vector", "offset": 0, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_IsPlainOldData | CPF_NoDestructor" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoText": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "TextProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_Parm" },
                { "name": "ReturnValue", "type": "TextProperty", "offset": 16, "array_dim": 1, "size": 16, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoNumbers": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "Input", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_ZeroConstructor" },
                { "name": "ReturnValue", "type": "ArrayProperty", "offset": 16, "array_dim": 1, "size": 16, "inner": { "name": "ReturnValue", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_ZeroConstructor" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoNumberGroups": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "Input", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "InputGroup", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_ZeroConstructor" }, "flags": "CPF_Parm | CPF_ZeroConstructor" },
                { "name": "ReturnValue", "type": "ArrayProperty", "offset": 16, "array_dim": 1, "size": 16, "inner": { "name": "ReturnValue", "type": "ArrayProperty", "offset": 0, "array_dim": 1, "size": 16, "inner": { "name": "ReturnGroup", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_ZeroConstructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_ZeroConstructor" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoOptional": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "OptionalProperty", "offset": 0, "array_dim": 1, "size": 8, "inner": { "name": "Input", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_ZeroConstructor" },
                { "name": "ReturnValue", "type": "OptionalProperty", "offset": 8, "array_dim": 1, "size": 8, "inner": { "name": "ReturnValue", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_ZeroConstructor" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoWeak": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "WeakObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 0, "array_dim": 1, "size": 8, "flags": "CPF_Parm | CPF_UObjectWrapper" },
                { "name": "ReturnValue", "type": "WeakObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 8, "array_dim": 1, "size": 8, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_UObjectWrapper" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoLazy": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "LazyObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 0, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_UObjectWrapper" },
                { "name": "ReturnValue", "type": "LazyObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 24, "array_dim": 1, "size": 24, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_UObjectWrapper" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoSoft": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Callback", "type": "SoftObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 0, "array_dim": 1, "size": 40, "flags": "CPF_Parm | CPF_UObjectWrapper" },
                { "name": "ReturnValue", "type": "SoftObjectProperty", "property_class": "/Script/Engine.Actor", "offset": 40, "array_dim": 1, "size": 40, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm | CPF_UObjectWrapper" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoScoresByName": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "MapProperty", "offset": 0, "array_dim": 1, "size": 80, "key_prop": { "name": "Key", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "value_prop": { "name": "Value", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_ZeroConstructor" }, "flags": "CPF_Parm" },
                { "name": "ReturnValue", "type": "MapProperty", "offset": 80, "array_dim": 1, "size": 80, "key_prop": { "name": "Key", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "value_prop": { "name": "Value", "type": "StrProperty", "offset": 0, "array_dim": 1, "size": 16, "flags": "CPF_ZeroConstructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoUniqueScores": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "SetProperty", "offset": 0, "array_dim": 1, "size": 80, "key_prop": { "name": "Element", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm" },
                { "name": "ReturnValue", "type": "SetProperty", "offset": 80, "array_dim": 1, "size": 80, "key_prop": { "name": "Element", "type": "IntProperty", "offset": 0, "array_dim": 1, "size": 4, "flags": "CPF_IsPlainOldData | CPF_NoDestructor" }, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm" }
              ]
            },
            "/Game/Test.BP_Player_C:EchoLoadout": {
              "type": "Function",
              "function_flags": "FUNC_Public | FUNC_BlueprintCallable | FUNC_BlueprintPure",
              "properties": [
                { "name": "Input", "type": "StructProperty", "struct": "/Script/Valhalla.PlayerLoadoutEntry", "offset": 0, "array_dim": 1, "size": 48, "flags": "CPF_Parm" },
                { "name": "ReturnValue", "type": "StructProperty", "struct": "/Script/Valhalla.PlayerLoadoutEntry", "offset": 48, "array_dim": 1, "size": 48, "flags": "CPF_Parm | CPF_OutParm | CPF_ReturnParm" }
              ]
            }
          },
          "vtables": {}
        }
        """);

        var model = new JMapImporter().Import(jmapPath);
        Assert(model.Metadata.EngineMajor == 5 && model.Metadata.EngineMinor == 6, "Engine version was not imported.");
        var player = model.Types.Single(type => type.Path == "/Game/Test.BP_Player_C");
        Assert(player.Functions.Count == 15, "UFunctions were not attached to their class.");

        var output = Path.Combine(directory.Path, "sdk");
        var abstractionsProject = FindRepositoryFile("src/RogueMod.Abstractions/RogueMod.Abstractions.csproj");
        var result = new CSharpSdkGenerator().Generate(model, output, "DeadzoneRogue.Sdk", abstractionsProject);
        var generatedSource = File.ReadAllText(result.SourcePath);
        var source = generatedSource.Replace("global::DeadzoneRogue.Sdk.", string.Empty, StringComparison.Ordinal);
        var secondOutput = Path.Combine(directory.Path, "sdk-repeat");
        var repeatedResult = new CSharpSdkGenerator().Generate(model, secondOutput, "DeadzoneRogue.Sdk", abstractionsProject);
        Assert(File.ReadAllBytes(result.SourcePath).SequenceEqual(File.ReadAllBytes(repeatedResult.SourcePath)),
            "The C# type translator produced non-deterministic generated source.");
        Assert(source.Contains("public class BP_Player : Actor", StringComparison.Ordinal), "Generated class inheritance is missing.");
        Assert(source.Contains("IUnrealObjectType<BP_Player>", StringComparison.Ordinal), "Generated typed object construction contract is missing.");
        Assert(source.Contains("public new const string DefaultObjectPath = \"/Game/Test.Default__BP_Player_C\";", StringComparison.Ordinal),
            "Generated class default-object path is missing.");
        Assert(source.Contains("public new static BP_Player? FindDefaultObject", StringComparison.Ordinal),
            "Generated typed default-object lookup is missing.");
        Assert(source.Contains("public new static IReadOnlyList<BP_Player> FindAll", StringComparison.Ordinal), "Generated typed FindAll wrapper is missing.");
        Assert(source.Contains("public float Health", StringComparison.Ordinal), "Generated typed property is missing.");
        Assert(source.Contains("get => Read<float>(__ReadOnlyTuning);\n        set => Write(__ReadOnlyTuning, value);", StringComparison.Ordinal),
            "Blueprint-read-only metadata incorrectly removed the native C# property setter.");
        Assert(!source.Contains("set => Write(__EditConstSetting, value);", StringComparison.Ordinal),
            "Edit-const property unexpectedly received a C# property setter.");
        Assert(source.Contains("public Actor? Target", StringComparison.Ordinal), "Generated object wrapper property is missing.");
        Assert(source.Contains("public string PlayerName", StringComparison.Ordinal), "Generated FString property is missing.");
        Assert(source.Contains("public string Mode", StringComparison.Ordinal), "Generated FName property is missing.");
        Assert(source.Contains("public string DisplayText", StringComparison.Ordinal), "Generated FText property is missing.");
        Assert(source.Contains("public Vector Location", StringComparison.Ordinal), "Generated POD struct property is missing.");
        Assert(source.Contains("public IReadOnlyList<int> Scores", StringComparison.Ordinal), "Generated TArray property is missing.");
        Assert(source.Contains("public IReadOnlyList<IReadOnlyList<int>> ScoreGroups", StringComparison.Ordinal), "Generated nested TArray property is missing.");
        Assert(source.Contains("public UnrealOptional<int> PreferredScore", StringComparison.Ordinal), "Generated TOptional property is missing.");
        Assert(source.Contains("public Actor? WeakTarget", StringComparison.Ordinal), "Generated weak UObject property is missing.");
        Assert(source.Contains("public UnrealLazyObjectReference<Actor> LazyTarget", StringComparison.Ordinal), "Generated identity-preserving lazy UObject property is missing.");
        Assert(source.Contains("public UnrealSoftObjectReference<Actor> SoftTarget", StringComparison.Ordinal), "Generated path-preserving soft UObject property is missing.");
        Assert(source.Contains("public IReadOnlyDictionary<int, string> ScoresByName", StringComparison.Ordinal), "Generated TMap property is missing.");
        Assert(source.Contains("public IReadOnlySet<int> UniqueScores", StringComparison.Ordinal), "Generated TSet property is missing.");
        Assert(source.Contains("public PlayerLoadoutEntry Loadout", StringComparison.Ordinal), "Generated non-POD struct property is missing.");
        Assert(source.Contains("public DamageEnvelope LastDamage", StringComparison.Ordinal), "Generated struct-with-container property is missing.");
        Assert(source.Contains("public Actor? Source { get; init; }", StringComparison.Ordinal), "Generated struct UObject field is missing.");
        Assert(source.Contains("public IReadOnlyList<GameplayTag> Tags { get; init; }", StringComparison.Ordinal), "Generated struct TArray field is missing.");
        Assert(source.Contains("Array: new(\"StructProperty:/Script/GameplayTags.GameplayTag\", 8", StringComparison.Ordinal),
            "Generated struct TArray descriptor is missing.");
        Assert(generatedSource.Contains(
                "global::DeadzoneRogue.Sdk.PlayerLoadoutEntry.FromUnrealValue(ReadValue(__PlayerLoadoutEntry), Unreal)",
                StringComparison.Ordinal),
            "Generated type references were not globally qualified against a colliding member name.");
        Assert(source.Contains("public readonly record struct PlayerLoadoutEntry", StringComparison.Ordinal), "Generated non-POD struct is missing.");
        Assert(source.Contains("public string DisplayName { get; init; }", StringComparison.Ordinal), "Generated non-POD struct FString field is missing.");
        Assert(source.Contains("public int Level { get; init; }", StringComparison.Ordinal), "Generated non-POD struct scalar field is missing.");
        Assert(source.Contains("public Vector Origin { get; init; }", StringComparison.Ordinal), "Generated non-POD struct nested-struct field is missing.");
        Assert(source.Contains("Map = new(\"IntProperty\", 4, \"StrProperty\", 16", StringComparison.Ordinal), "Generated TMap descriptor is missing.");
        Assert(source.Contains("Set = new(\"IntProperty\", 4", StringComparison.Ordinal), "Generated TSet descriptor is missing.");
        Assert(source.Contains("UnrealMapValue.ToDictionary<int, string>", StringComparison.Ordinal), "Generated TMap property did not emit dictionary transport.");
        Assert(source.Contains("UnrealSetValue.ToSet<int>", StringComparison.Ordinal), "Generated TSet property did not emit set transport.");
        Assert(source.Contains("UnrealMapValue.From", StringComparison.Ordinal), "Generated TMap property did not emit dictionary write transport.");
        Assert(source.Contains("UnrealSetValue.From", StringComparison.Ordinal), "Generated TSet property did not emit set write transport.");
        Assert(source.Contains("public void SetHealth(float newHealth)", StringComparison.Ordinal), "Generated void UFunction wrapper is missing.");
        Assert(source.Contains("public float GetHealth()", StringComparison.Ordinal), "Generated return value wrapper is missing.");
        Assert(source.Contains("public void SetPlayerName(string newName)", StringComparison.Ordinal), "Generated FString UFunction wrapper is missing.");
        Assert(source.Contains("public void SetLocation(Vector newLocation)", StringComparison.Ordinal), "Generated POD struct input wrapper is missing.");
        Assert(source.Contains("public Vector GetLocation()", StringComparison.Ordinal), "Generated POD struct return wrapper is missing.");
        Assert(source.Contains("public string EchoText(string input)", StringComparison.Ordinal), "Generated FText UFunction wrapper is missing.");
        Assert(source.Contains("public IReadOnlyList<int> EchoNumbers(IReadOnlyList<int> input)", StringComparison.Ordinal), "Generated TArray UFunction wrapper is missing.");
        Assert(source.Contains("public IReadOnlyList<IReadOnlyList<int>> EchoNumberGroups(IReadOnlyList<IReadOnlyList<int>> input)", StringComparison.Ordinal), "Generated nested TArray UFunction wrapper is missing.");
        Assert(source.Contains("public UnrealOptional<int> EchoOptional(UnrealOptional<int> input)", StringComparison.Ordinal), "Generated TOptional UFunction wrapper is missing.");
        Assert(source.Contains("public Actor? EchoWeak(Actor? input)", StringComparison.Ordinal), "Generated weak UObject UFunction wrapper is missing.");
        Assert(source.Contains("public UnrealLazyObjectReference<Actor> EchoLazy(UnrealLazyObjectReference<Actor> input)", StringComparison.Ordinal), "Generated lazy UObject UFunction wrapper is missing.");
        Assert(source.Contains("public UnrealSoftObjectReference<Actor> EchoSoft(UnrealSoftObjectReference<Actor> callback)", StringComparison.Ordinal), "Generated soft UObject UFunction wrapper is missing.");
        Assert(source.Contains("public IReadOnlyDictionary<int, string> EchoScoresByName(IReadOnlyDictionary<int, string> input)", StringComparison.Ordinal), "Generated TMap UFunction wrapper is missing.");
        Assert(source.Contains("public IReadOnlySet<int> EchoUniqueScores(IReadOnlySet<int> input)", StringComparison.Ordinal), "Generated TSet UFunction wrapper is missing.");
        Assert(source.Contains("public PlayerLoadoutEntry EchoLoadout(PlayerLoadoutEntry input)", StringComparison.Ordinal), "Generated non-POD struct UFunction wrapper is missing.");
        Assert(source.Contains("EchoSoftPreHookHandler(BP_Player context, ref UnrealSoftObjectReference<Actor> callback2)", StringComparison.Ordinal), "Generated hook parameter collided with its callback delegate.");
        Assert(source.Contains("public static IDisposable RegisterSetHealthPreHook", StringComparison.Ordinal),
            "Generated strongly typed pre-hook registration is missing.");
        Assert(source.Contains("callback, UnrealHookOptions options = default", StringComparison.Ordinal),
            "Generated hook registration did not expose ordering and instance filtering options.");
        Assert(source.Contains("options with { SkipInputDecoding = true }", StringComparison.Ordinal),
            "Generated post hooks do not skip unused pure input decoding.");
        Assert(source.Contains("public static IDisposable RegisterGetHealthPostHook", StringComparison.Ordinal),
            "Generated strongly typed post-hook registration is missing.");
        Assert(source.Contains("public delegate void EchoNumbersPreHookHandler(BP_Player context, ref IReadOnlyList<int> input)", StringComparison.Ordinal),
            "Generated TArray hook callback did not use its translated argument type.");
        Assert(source.Contains("hook.SetArgument(\"Input\", UnrealArrayValue.From", StringComparison.Ordinal),
            "Generated TArray pre-hook did not emit container replacement transport.");
        Assert(source.Contains("hook.SetReturnValue(UnrealArrayValue.From", StringComparison.Ordinal),
            "Generated TArray post-hook did not emit container replacement transport.");
        Assert(source.Contains("public delegate void EchoScoresByNamePreHookHandler(BP_Player context, ref IReadOnlyDictionary<int, string> input)", StringComparison.Ordinal),
            "Generated TMap hook callback did not use its translated argument type.");
        Assert(source.Contains("hook.SetArgument(\"Input\", UnrealMapValue.From", StringComparison.Ordinal),
            "Generated TMap pre-hook did not emit container replacement transport.");
        Assert(source.Contains("hook.SetReturnValue(UnrealMapValue.From", StringComparison.Ordinal),
            "Generated TMap post-hook did not emit container replacement transport.");
        Assert(source.Contains("public delegate void EchoUniqueScoresPostHookHandler(BP_Player context, ref IReadOnlySet<int> returnValue)", StringComparison.Ordinal),
            "Generated TSet hook callback did not use its translated return type.");
        Assert(source.Contains("hook.SetArgument(\"Input\", UnrealSetValue.From", StringComparison.Ordinal),
            "Generated TSet pre-hook did not emit container replacement transport.");
        Assert(source.Contains("hook.SetReturnValue(UnrealSetValue.From", StringComparison.Ordinal),
            "Generated TSet post-hook did not emit container replacement transport.");
        Assert(source.Contains("public delegate void EchoLoadoutPreHookHandler(BP_Player context, ref PlayerLoadoutEntry input)", StringComparison.Ordinal),
            "Generated non-POD struct hook callback did not use its SDK type.");
        Assert(source.Contains("hook.SetArgument(\"Input\", input.ToUnrealValue())", StringComparison.Ordinal),
            "Generated non-POD struct pre-hook did not emit field-wise replacement transport.");
        Assert(source.Contains("hook.SetReturnValue(returnValue.ToUnrealValue())", StringComparison.Ordinal),
            "Generated non-POD struct post-hook did not emit field-wise replacement transport.");
        Assert(source.Contains("Array: new(\"IntProperty\", 4", StringComparison.Ordinal), "Generated TArray element descriptor is missing.");
        Assert(source.Contains("ElementArray = new(\"IntProperty\", 4", StringComparison.Ordinal), "Generated nested TArray descriptor is missing.");
        Assert(source.Contains("Optional = new(\"IntProperty\", 4", StringComparison.Ordinal), "Generated TOptional value descriptor is missing.");
        Assert(source.Contains("public static UnrealStructDescriptor Descriptor", StringComparison.Ordinal), "Generated POD struct descriptor is missing.");
        Assert(source.Contains("new(\"NewHealth\", \"FloatProperty\", 0, 1, \"CPF_Parm\", 4", StringComparison.Ordinal),
            "Generated UFunction runtime layout metadata is missing.");
        Assert(File.Exists(result.ManifestPath), "SDK manifest was not generated.");
        Assert(File.Exists(result.ProjectPath), "Buildable SDK project was not generated.");

        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{result.ProjectPath}\" -c Release --nologo",
            WorkingDirectory = output,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start generated SDK compilation.");
        var standardOutput = build.StandardOutput.ReadToEnd();
        var standardError = build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert(build.ExitCode == 0, $"Generated SDK did not compile:{Environment.NewLine}{standardOutput}{standardError}");
    }

    static void Touch(string root, string relativePath)
    {
        var path = Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    static string Combine(string root, params string[] relativeParts)
    {
        var parts = relativeParts.SelectMany(part => part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine([root, .. parts]);
    }

    static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    static void ForceCollectibleContextsToUnload()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    static void ManagedModScaffolderCreatesStandaloneStarter()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "FirstMod");
        var result = new ManagedModScaffolder().Create(new ManagedModScaffoldOptions
        {
            ModId = "example.first-mod",
            ProjectName = "Example.FirstMod",
            DisplayName = "Example first mod",
            OutputDirectory = output,
            RogueModSdkVersion = "0.1.0-preview.1",
            GameSdkVersion = "0.1.0"
        });

        Assert(File.Exists(result.SolutionPath), "The starter solution was not created.");
        Assert(File.Exists(result.ProjectPath), "The starter project was not created.");
        Assert(File.Exists(Path.Combine(output, ".gitignore")), "The starter .gitignore was not created.");
        var project = File.ReadAllText(result.ProjectPath);
        Assert(project.Contains("<RogueModModId>example.first-mod</RogueModModId>", StringComparison.Ordinal),
            "The requested mod id was not applied.");
        Assert(project.Contains("<RogueModEntryPoint>Example.FirstMod.Mod</RogueModEntryPoint>", StringComparison.Ordinal),
            "The requested entry point was not applied.");
        var packageVersions = File.ReadAllText(Path.Combine(output, "Directory.Packages.props"));
        Assert(packageVersions.Contains("Version=\"0.1.0-preview.1\"", StringComparison.Ordinal),
            "The requested SDK version was not applied.");
        Assert(!Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Any(text => text.Contains("ROGUEMOD_SDK_VERSION", StringComparison.Ordinal)),
            "A template token leaked into the generated starter.");

        var refusedOverwrite = false;
        try
        {
            new ManagedModScaffolder().Create(new ManagedModScaffoldOptions
            {
                ModId = "example.first-mod",
                ProjectName = "Example.FirstMod",
                DisplayName = "Example first mod",
                OutputDirectory = output
            });
        }
        catch (IOException)
        {
            refusedOverwrite = true;
        }
        Assert(refusedOverwrite, "The scaffolder overwrote an existing project directory.");
    }
}

file sealed class TemporaryDirectory : IDisposable
{
    private const int DeleteAttempts = 20;

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"roguemod-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < DeleteAttempts - 1)
            {
                ReleaseCollectibleLoadContexts();
            }
            catch (IOException) when (attempt < DeleteAttempts - 1)
            {
                ReleaseCollectibleLoadContexts();
            }
        }

        Directory.Delete(Path, recursive: true);
    }

    private static void ReleaseCollectibleLoadContexts()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(50);
    }
}

file sealed record TestModContext(
    string ModId,
    string GameProfileId,
    IModLogger Logger,
    IUnrealReflection Unreal) : IModContext;

file sealed class UnavailableUnrealReflection : IUnrealReflection
{
    public bool IsAvailable => false;
    public UnrealObjectHandle FindFirstOf(string className) => UnrealObjectHandle.Null;
    public bool IsValid(UnrealObjectHandle handle) => false;
    public UnrealObjectHandle GetClass(UnrealObjectHandle handle) => UnrealObjectHandle.Null;
    public string? GetPathName(UnrealObjectHandle handle) => null;
}

file sealed class TestLogger : IModLogger
{
    public List<string> Messages { get; } = [];

    public void Log(ModLogLevel level, string message) => Messages.Add(message);
}
