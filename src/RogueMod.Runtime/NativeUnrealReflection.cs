using RogueMod.Abstractions;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static RogueMod.Runtime.NativeReflectionTypeRegistry;

namespace RogueMod.Runtime;

internal sealed unsafe class NativeUnrealReflection(
    delegate* unmanaged[Cdecl]<int> isAvailable,
    delegate* unmanaged[Cdecl]<char*, ulong> findFirstOf,
    delegate* unmanaged[Cdecl]<ulong, int> isValid,
    delegate* unmanaged[Cdecl]<ulong, ulong> getClass,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, uint*, int> getPathName,
    delegate* unmanaged[Cdecl]<uint> getCapabilities,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> readProperty,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealValue*, int> writeProperty,
    delegate* unmanaged[Cdecl]<ulong, char*, uint, NativeUnrealReflection.NativeUnrealParameter*, int> invokeFunction,
    delegate* unmanaged[Cdecl]<char*, ulong*, uint, uint*, int> findAllOf,
    delegate* unmanaged[Cdecl]<char*, int, int, ulong, uint, NativeUnrealReflection.NativeUnrealParameter*, delegate* unmanaged[Cdecl]<ulong, ulong, int, uint, NativeUnrealReflection.NativeUnrealParameter*, int>, ulong, ulong*, int> registerHook,
    delegate* unmanaged[Cdecl]<ulong, int> unregisterHook,
    delegate* unmanaged[Cdecl]<ulong, ulong, char*, ulong> createObject,
    delegate* unmanaged[Cdecl]<ulong, ulong, float*, float*, ulong> spawnActor,
    Action<ModLogLevel, string> log) : IUnrealReflection
{
    private const uint MaximumPathLength = 1_048_576;
    private const uint MaximumStringLength = 1_048_576;
    private const int MaximumStructSize = 1_048_576;
    private const uint MaximumArrayLength = 1_048_576;
    private const uint MaximumObjectCount = 1_048_576;
    private const uint LazyObjectWireSize = 48;
    private const uint SoftObjectWireSize = 56;
    private const int LazyObjectStorageSize = UnrealLazyObjectValue.NativeStorageSize;
    private const int ContainerValueKindShift = 8;
    private const uint MaximumEncodedValueKind = 0x00ff_ffff;
    private const uint MaximumMapKeyEncodedValueKind = 0xff;
    private const uint MaximumMapValueEncodedValueKind = 0xffff;
    private static readonly ConcurrentDictionary<ulong, HookRegistration> HookRegistrations = new();
    private static long nextHookContext;

    public bool IsAvailable => isAvailable != null && isAvailable() != 0;

    public UnrealReflectionCapabilities Capabilities =>
        !IsAvailable || getCapabilities == null
            ? UnrealReflectionCapabilities.None
            : (UnrealReflectionCapabilities)getCapabilities();

    public UnrealObjectHandle FindFirstOf(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        if (!IsAvailable || findFirstOf == null)
        {
            return UnrealObjectHandle.Null;
        }

        fixed (char* classNamePointer = className)
        {
            return new(findFirstOf(classNamePointer));
        }
    }

    public UnrealObjectHandle CreateObject(
        UnrealObjectHandle classHandle,
        UnrealObjectHandle outerHandle,
        string? objectName = null)
    {
        if ((Capabilities & UnrealReflectionCapabilities.ObjectCreation) == 0 || createObject == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal object creation.");
        }
        if (classHandle.IsNull || !IsValid(classHandle))
        {
            throw new InvalidOperationException("Cannot create an Unreal object from an invalid class handle.");
        }
        if (!outerHandle.IsNull && !IsValid(outerHandle))
        {
            throw new InvalidOperationException("Cannot create an Unreal object with an invalid outer handle.");
        }

        fixed (char* namePointer = objectName)
        {
            return new(createObject(classHandle.Value, outerHandle.Value, namePointer));
        }
    }

    public UnrealObjectHandle SpawnActor(
        UnrealObjectHandle contextObject,
        UnrealObjectHandle classHandle,
        UnrealVector location,
        UnrealRotator rotation)
    {
        if ((Capabilities & UnrealReflectionCapabilities.ActorSpawning) == 0 || spawnActor == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal actor spawning.");
        }
        if (contextObject.IsNull || !IsValid(contextObject))
        {
            throw new InvalidOperationException("Cannot spawn an actor from an invalid world-context object.");
        }
        if (classHandle.IsNull || !IsValid(classHandle))
        {
            throw new InvalidOperationException("Cannot spawn an actor from an invalid class handle.");
        }

        float[] locationBuffer = [location.X, location.Y, location.Z];
        float[] rotationBuffer = [rotation.Pitch, rotation.Yaw, rotation.Roll];
        fixed (float* locationPointer = locationBuffer)
        fixed (float* rotationPointer = rotationBuffer)
        {
            return new(spawnActor(contextObject.Value, classHandle.Value, locationPointer, rotationPointer));
        }
    }

    public IReadOnlyList<UnrealObjectHandle> FindAllOf(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        if ((Capabilities & UnrealReflectionCapabilities.ObjectEnumeration) == 0 || findAllOf == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal object enumeration.");
        }

        fixed (char* classNamePointer = className)
        {
            uint required = 0;
            var result = findAllOf(classNamePointer, null, 0, &required);
            if (result < 0 || required > MaximumObjectCount)
            {
                throw new InvalidOperationException($"Unreal object enumeration failed with native status {result}.");
            }
            if (required == 0)
            {
                return [];
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var handles = new ulong[required];
                fixed (ulong* handlePointer = handles)
                {
                    result = findAllOf(classNamePointer, handlePointer, (uint)handles.Length, &required);
                }
                if (result == 0)
                {
                    if (required < handles.Length)
                    {
                        Array.Resize(ref handles, checked((int)required));
                    }
                    return handles.Select(value => new UnrealObjectHandle(value)).ToArray();
                }
                if (result < 0 || required > MaximumObjectCount)
                {
                    throw new InvalidOperationException($"Unreal object enumeration failed with native status {result}.");
                }
            }
        }

        throw new InvalidOperationException("Unreal object enumeration changed repeatedly while results were being copied.");
    }

    public bool IsValid(UnrealObjectHandle handle) =>
        !handle.IsNull && IsAvailable && isValid != null && isValid(handle.Value) != 0;

    public UnrealObjectHandle GetClass(UnrealObjectHandle handle)
    {
        if (!IsValid(handle) || getClass == null)
        {
            return UnrealObjectHandle.Null;
        }
        return new(getClass(handle.Value));
    }

    public string? GetPathName(UnrealObjectHandle handle)
    {
        if (!IsValid(handle) || getPathName == null)
        {
            return null;
        }

        uint required = 0;
        var result = getPathName(handle.Value, null, 0, &required);
        if (result < 0 || required is 0 or > MaximumPathLength)
        {
            return null;
        }

        var buffer = new char[required];
        fixed (char* bufferPointer = buffer)
        {
            result = getPathName(handle.Value, bufferPointer, required, &required);
        }
        return result == 0 && required > 0
            ? new string(buffer, 0, checked((int)required - 1))
            : null;
    }

    public UnrealInvocationResult Invoke(
        UnrealObjectHandle handle,
        UnrealFunctionDescriptor function,
        IReadOnlyList<UnrealArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        if ((Capabilities & UnrealReflectionCapabilities.FunctionInvocation) == 0 || invokeFunction == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support UFunction invocation.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot invoke a UFunction on an invalid Unreal object handle.");
        }
        var descriptors = function.ParameterList;
        var suppliedArguments = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (!suppliedArguments.TryAdd(argument.Name, argument.Value))
            {
                throw new ArgumentException($"UFunction argument '{argument.Name}' was supplied more than once.", nameof(arguments));
            }
        }
        var nativeParameters = CreateNativeParameterDescriptors(function);
        using var inputAllocations = new NativeAllocations();
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            var kind = GetPropertyKind(descriptor.UnrealType, descriptor.Size);
            if (!descriptor.IsInput)
            {
                continue;
            }
            if (!suppliedArguments.Remove(descriptor.Name, out var argument))
            {
                throw new ArgumentException(
                    $"Required UFunction argument '{descriptor.Name}' was not supplied for '{function.Path}'.",
                    nameof(arguments));
            }
            nativeParameters[index].Value = ToNativeValue(
                kind,
                nativeParameters[index].Kind,
                argument,
                descriptor.Struct,
                descriptor.Array,
                descriptor.Optional,
                descriptor.Map,
                descriptor.Set,
                inputAllocations);
        }
        if (suppliedArguments.Count != 0)
        {
            throw new ArgumentException(
                $"Unknown UFunction argument(s) for '{function.Path}': {string.Join(", ", suppliedArguments.Keys)}.",
                nameof(arguments));
        }

        try
        {
            int result;
            fixed (char* functionName = function.Name)
            fixed (NativeUnrealParameter* parameterPointer = nativeParameters)
            {
                result = invokeFunction(handle.Value, functionName, (uint)nativeParameters.Length, parameterPointer);
            }
            if (result == -4 || result == -8)
            {
                throw new InvalidOperationException(
                    $"UFunction '{function.Path}' no longer matches its generated SDK descriptor (native status {result}). " +
                    "Regenerate the SDK from a current JMAP dump.");
            }
            if (result != 0)
            {
                throw new InvalidOperationException($"UFunction '{function.Path}' invocation failed with native status {result}.");
            }
            var returnValue = UnrealValue.Null;
            var outputs = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
            for (var index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                if (!descriptor.IsOutput && !descriptor.IsReturn)
                {
                    continue;
                }
                var value = ToManagedValue(
                    DecodePropertyKind(nativeParameters[index].Kind),
                    nativeParameters[index].Value,
                    descriptor.Struct,
                    descriptor.Array,
                    descriptor.Optional);
                if (descriptor.IsReturn)
                {
                    returnValue = value;
                }
                else
                {
                    outputs.Add(descriptor.Name, value);
                }
            }
            return new UnrealInvocationResult(returnValue, outputs);
        }
        finally
        {
            FreeNativeOutputAllocations(descriptors, nativeParameters, inputAllocations);
        }
    }

    public IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        Action<UnrealHookContext> callback) =>
        RegisterHook(function, phase, default, callback);

    public IDisposable RegisterHook(
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        UnrealHookOptions options,
        Action<UnrealHookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(callback);
        if (phase is not (UnrealHookPhase.Pre or UnrealHookPhase.Post))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
        if ((Capabilities & UnrealReflectionCapabilities.FunctionHooks) == 0 || registerHook == null || unregisterHook == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support UFunction hooks.");
        }
        if (!options.Instance.IsNull && !IsValid(options.Instance))
        {
            throw new InvalidOperationException("A UFunction hook cannot target an invalid Unreal object handle.");
        }

        var nativeParameters = CreateNativeParameterDescriptors(function);
        var context = unchecked((ulong)Interlocked.Increment(ref nextHookContext));
        var registration = new HookRegistration(
            this,
            function,
            phase,
            callback);
        if (!HookRegistrations.TryAdd(context, registration))
        {
            throw new InvalidOperationException("Could not allocate a unique managed UFunction hook context.");
        }

        ulong nativeToken = 0;
        int result;
        fixed (char* functionPath = function.Path)
        fixed (NativeUnrealParameter* parameterPointer = nativeParameters)
        {
            result = registerHook(
                functionPath,
                (int)phase,
                options.Priority,
                options.Instance.Value,
                (uint)nativeParameters.Length,
                parameterPointer,
                &DispatchHook,
                context,
                &nativeToken);
        }
        if (result != 0 || nativeToken == 0)
        {
            HookRegistrations.TryRemove(context, out _);
            if (result is -4 or -5)
            {
                throw new InvalidOperationException(
                    $"UFunction '{function.Path}' no longer matches its generated hook descriptor (native status {result}). " +
                    "Regenerate the SDK from a current JMAP dump.");
            }
            if (result == -7)
            {
                throw new InvalidOperationException(
                    $"The instance filter for UFunction '{function.Path}' became invalid during native hook registration.");
            }
            throw new InvalidOperationException($"UFunction hook registration for '{function.Path}' failed with native status {result}.");
        }

        return new HookSubscription(context, nativeToken, unregisterHook);
    }

    private NativeUnrealParameter[] CreateNativeParameterDescriptors(UnrealFunctionDescriptor function)
    {
        var descriptors = function.ParameterList;
        var nativeParameters = new NativeUnrealParameter[descriptors.Count];
        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (descriptor.ArrayDimension != 1)
            {
                throw new NotSupportedException(
                    $"UFunction parameter '{function.Path}:{descriptor.Name}' is a fixed native array; RogueMod supports scalar parameters and dynamic TArray values only.");
            }
            var kind = GetPropertyKind(descriptor.UnrealType, descriptor.Size);
            EnsurePropertyCapabilities(kind, descriptor.Array, descriptor.Optional, descriptor.Map, descriptor.Set);
            var encodedKind = EncodePropertyKind(kind, descriptor.Array, descriptor.Optional, descriptor.Map, descriptor.Set);
            var flags = (descriptor.IsInput ? NativeParameterFlags.Input : 0)
                | (descriptor.IsOutput ? NativeParameterFlags.Output : 0)
                | (descriptor.IsReturn ? NativeParameterFlags.Return : 0);
            nativeParameters[index] = new NativeUnrealParameter
            {
                Kind = encodedKind,
                Flags = (uint)flags,
                Offset = descriptor.Offset,
                Size = descriptor.Size,
                ArrayDimension = (uint)descriptor.ArrayDimension,
                BoolLayout = (uint)(descriptor.ByteOffset & 0xff)
                    | (uint)(descriptor.ByteMask & 0xff) << 8
                    | (uint)(descriptor.FieldMask & 0xff) << 16,
                Value = new NativeUnrealValue { Kind = encodedKind }
            };
        }
        return nativeParameters;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DispatchHook(
        ulong context,
        ulong objectHandle,
        int nativePhase,
        uint parameterCount,
        NativeUnrealParameter* parameters)
    {
        if (!HookRegistrations.TryGetValue(context, out var registration)
            || Volatile.Read(ref registration.Disabled) != 0)
        {
            return 0;
        }

        try
        {
            var phase = (UnrealHookPhase)nativePhase;
            if (phase != registration.Phase || parameterCount != registration.Function.ParameterList.Count
                || (parameterCount != 0 && parameters == null))
            {
                return -2;
            }

            var arguments = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
            var outputs = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
            var returnValue = UnrealValue.Null;
            for (var index = 0; index < registration.Function.ParameterList.Count; index++)
            {
                var descriptor = registration.Function.ParameterList[index];
                if (phase == UnrealHookPhase.Pre && !descriptor.IsInput)
                {
                    continue;
                }
                var value = ToManagedValue(
                    DecodePropertyKind(parameters[index].Kind),
                    parameters[index].Value,
                    descriptor.Struct,
                    descriptor.Array,
                    descriptor.Optional);
                if (descriptor.IsInput)
                {
                    arguments[descriptor.Name] = value;
                }
                if (phase == UnrealHookPhase.Post && descriptor.IsReturn)
                {
                    returnValue = value;
                }
                else if (phase == UnrealHookPhase.Post && descriptor.IsOutput)
                {
                    outputs[descriptor.Name] = value;
                }
            }

            var hook = new UnrealHookContext(
                new UnrealObjectHandle(objectHandle),
                registration.Function,
                phase,
                arguments,
                new UnrealInvocationResult(returnValue, outputs));
            registration.Callback(hook);
            ApplyHookReplacements(hook, parameters);
            return 0;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref registration.Disabled, 1);
            registration.Owner.LogHookFailure(registration.Function.Path, exception);
            return -3;
        }
    }

    private void LogHookFailure(string functionPath, Exception exception) =>
        log(ModLogLevel.Error, $"Disabled UFunction hook callback for '{functionPath}' after an exception: {exception}");

    private static void ApplyHookReplacements(
        UnrealHookContext hook,
        NativeUnrealParameter* parameters)
    {
        for (var index = 0; index < hook.Function.ParameterList.Count; index++)
        {
            var descriptor = hook.Function.ParameterList[index];
            UnrealValue? replacement = null;
            if (hook.Phase == UnrealHookPhase.Pre
                && descriptor.IsInput
                && hook.ArgumentReplacements.TryGetValue(descriptor.Name, out var argumentReplacement))
            {
                replacement = argumentReplacement;
            }
            else if (hook.Phase == UnrealHookPhase.Post && descriptor.IsReturn)
            {
                replacement = hook.ReturnReplacement;
            }
            else if (hook.Phase == UnrealHookPhase.Post && descriptor.IsOutput
                && hook.OutputReplacements.TryGetValue(descriptor.Name, out var outputReplacement))
            {
                replacement = outputReplacement;
            }
            if (replacement is not { } replacementValue)
            {
                continue;
            }

            var kind = GetPropertyKind(descriptor.UnrealType, descriptor.Size);
            using var replacementAllocations = new NativeAllocations();
            var nativeValue = ToNativeValue(
                kind,
                parameters[index].Kind,
                replacementValue,
                descriptor.Struct,
                descriptor.Array,
                descriptor.Optional,
                descriptor.Map,
                descriptor.Set,
                replacementAllocations);

            // The native bridge owns both the incoming snapshot and the replacement after
            // this callback returns. Release the old wire value before handing over the new one.
            FreeNativeAllocation(parameters[index].Kind, parameters[index].Value, null);
            parameters[index].Value = nativeValue;
            parameters[index].Flags |= (uint)NativeParameterFlags.Modified;
            replacementAllocations.Release();
        }
    }

    public UnrealValue ReadProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if ((Capabilities & UnrealReflectionCapabilities.PropertyRead) == 0 || readProperty == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal property reads.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot read a property from an invalid Unreal object handle.");
        }

        var kind = GetPropertyKind(property.UnrealType, property.Size);
        EnsurePropertyCapabilities(kind, property.Array, property.Optional, property.Map, property.Set);
        var encodedKind = EncodePropertyKind(kind, property.Array, property.Optional, property.Map, property.Set);
        NativeUnrealValue nativeValue;
        int result;
        fixed (char* propertyName = property.Name)
        {
            result = readProperty(handle.Value, propertyName, encodedKind, &nativeValue);
        }
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unreal property '{property.OwnerPath}:{property.Name}' read failed with native status {result}.");
        }

        try
        {
            return ToManagedValue(kind, nativeValue, property.Struct, property.Array, property.Optional, property.Map, property.Set);
        }
        finally
        {
            FreeNativeAllocation(encodedKind, nativeValue, null);
        }
    }

    private static UnrealValue ToManagedValue(
        NativePropertyKind kind,
        NativeUnrealValue nativeValue,
        UnrealStructDescriptor? structDescriptor = null,
        UnrealArrayDescriptor? arrayDescriptor = null,
        UnrealOptionalDescriptor? optionalDescriptor = null,
        UnrealMapDescriptor? mapDescriptor = null,
        UnrealSetDescriptor? setDescriptor = null)
    {
        object managedValue = kind switch
        {
            NativePropertyKind.LazyObject => ReadNativeLazyObject(nativeValue),
            NativePropertyKind.SoftObject => ReadNativeSoftObject(nativeValue),
            NativePropertyKind.String or NativePropertyKind.Name or NativePropertyKind.Text => ReadNativeString(nativeValue),
            NativePropertyKind.Struct => ReadNativeStruct(nativeValue, RequireStructDescriptor(structDescriptor)),
            NativePropertyKind.Array => ReadNativeArray(nativeValue, RequireArrayDescriptor(arrayDescriptor)),
            NativePropertyKind.Optional => ReadNativeOptional(nativeValue, RequireOptionalDescriptor(optionalDescriptor)),
            NativePropertyKind.Map => ReadNativeMap(nativeValue, RequireMapDescriptor(mapDescriptor)),
            NativePropertyKind.Set => ReadNativeSet(nativeValue, RequireSetDescriptor(setDescriptor)),
            _ => NativeScalarValueCodec.Decode(kind, nativeValue.Data)
        };
        return new UnrealValue(managedValue);
    }

    public void WriteProperty(UnrealObjectHandle handle, UnrealPropertyDescriptor property, UnrealValue value)
    {
        ArgumentNullException.ThrowIfNull(property);
        if ((Capabilities & UnrealReflectionCapabilities.PropertyWrite) == 0 || writeProperty == null)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support Unreal property writes.");
        }
        if (!IsValid(handle))
        {
            throw new InvalidOperationException("Cannot write a property on an invalid Unreal object handle.");
        }

        var kind = GetPropertyKind(property.UnrealType, property.Size);
        EnsurePropertyCapabilities(kind, property.Array, property.Optional, property.Map, property.Set);
        if (kind is NativePropertyKind.Map or NativePropertyKind.Set
            && (Capabilities & UnrealReflectionCapabilities.MapSetWrites) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support TMap/TSet writes.");
        }
        var encodedKind = EncodePropertyKind(kind, property.Array, property.Optional, property.Map, property.Set);
        using var inputAllocations = new NativeAllocations();
        var nativeValue = ToNativeValue(
            kind,
            encodedKind,
            value,
            property.Struct,
            property.Array,
            property.Optional,
            property.Map,
            property.Set,
            inputAllocations);
        int result;
        fixed (char* propertyName = property.Name)
        {
            result = writeProperty(handle.Value, propertyName, encodedKind, &nativeValue);
        }
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Unreal property '{property.OwnerPath}:{property.Name}' write failed with native status {result}.");
        }
    }

    private static NativeUnrealValue ToNativeValue(
        NativePropertyKind kind,
        uint encodedKind,
        UnrealValue value,
        UnrealStructDescriptor? structDescriptor,
        UnrealArrayDescriptor? arrayDescriptor,
        UnrealOptionalDescriptor? optionalDescriptor,
        UnrealMapDescriptor? mapDescriptor,
        UnrealSetDescriptor? setDescriptor,
        NativeAllocations allocations)
    {
        var managed = value.Value;
        if (kind == NativePropertyKind.Map)
        {
            return WriteNativeMap(
                encodedKind,
                value,
                RequireMapDescriptor(mapDescriptor),
                allocations);
        }
        if (kind == NativePropertyKind.Set)
        {
            return WriteNativeSet(
                encodedKind,
                value,
                RequireSetDescriptor(setDescriptor),
                allocations);
        }
        if (kind is NativePropertyKind.String or NativePropertyKind.Name or NativePropertyKind.Text)
        {
            if (managed is not string text)
            {
                throw new InvalidCastException(
                    $"Unreal property kind '{kind}' cannot be written from managed value type " +
                    $"'{value.Value?.GetType().FullName ?? "null"}'.");
            }
            if (text.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Unreal FString and FName values cannot contain embedded null characters.", nameof(value));
            }
            if (text.Length > MaximumStringLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Unreal string values cannot exceed {MaximumStringLength} UTF-16 code units.");
            }
            var pointer = allocations.AddString(text);
            return new NativeUnrealValue
            {
                Kind = encodedKind,
                Reserved = (uint)text.Length,
                Data = unchecked((ulong)pointer)
            };
        }

        if (kind == NativePropertyKind.Struct)
        {
            return WriteNativeStruct(
                encodedKind,
                value,
                RequireStructDescriptor(structDescriptor),
                allocations);
        }

        if (kind == NativePropertyKind.Array)
        {
            return WriteNativeArray(
                encodedKind,
                value,
                RequireArrayDescriptor(arrayDescriptor),
                allocations);
        }

        if (kind == NativePropertyKind.Optional)
        {
            return WriteNativeOptional(
                encodedKind,
                value,
                RequireOptionalDescriptor(optionalDescriptor),
                allocations);
        }
        if (kind == NativePropertyKind.LazyObject)
        {
            return WriteNativeLazyObject(encodedKind, value, allocations);
        }
        if (kind == NativePropertyKind.SoftObject)
        {
            return WriteNativeSoftObject(encodedKind, value, allocations);
        }

        var data = NativeScalarValueCodec.Encode(kind, managed);
        return new NativeUnrealValue { Kind = encodedKind, Data = data };
    }

    private static uint EncodePropertyKind(
        NativePropertyKind kind,
        UnrealArrayDescriptor? arrayDescriptor = null,
        UnrealOptionalDescriptor? optionalDescriptor = null,
        UnrealMapDescriptor? mapDescriptor = null,
        UnrealSetDescriptor? setDescriptor = null)
    {
        switch (kind)
        {
            case NativePropertyKind.Array:
            {
                var descriptor = RequireArrayDescriptor(arrayDescriptor);
                var elementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
                var encodedElementKind = EncodePropertyKind(elementKind, descriptor.ElementArray);
                if (encodedElementKind > MaximumEncodedValueKind)
                {
                    throw new NotSupportedException("RogueMod ABI 13 supports at most three nested container levels.");
                }
                return (uint)kind | encodedElementKind << ContainerValueKindShift;
            }
            case NativePropertyKind.Set:
            {
                var descriptor = RequireSetDescriptor(setDescriptor);
                var elementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
                var encodedElementKind = EncodePropertyKind(elementKind, descriptor.ElementArray);
                if (encodedElementKind > MaximumEncodedValueKind)
                {
                    throw new NotSupportedException("RogueMod ABI 13 supports at most three nested container levels.");
                }
                return (uint)kind | encodedElementKind << ContainerValueKindShift;
            }
            case NativePropertyKind.Optional:
            {
                var descriptor = RequireOptionalDescriptor(optionalDescriptor);
                var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
                var encodedValueKind = EncodePropertyKind(valueKind);
                if (encodedValueKind > MaximumEncodedValueKind)
                {
                    throw new NotSupportedException("RogueMod ABI 13 supports at most three nested container levels.");
                }
                return (uint)kind | encodedValueKind << ContainerValueKindShift;
            }
            case NativePropertyKind.Map:
            {
                var descriptor = RequireMapDescriptor(mapDescriptor);
                var keyKind = GetPropertyKind(descriptor.KeyUnrealType, descriptor.KeySize);
                if (keyKind is NativePropertyKind.Struct
                    or NativePropertyKind.Array
                    or NativePropertyKind.Optional
                    or NativePropertyKind.Map
                    or NativePropertyKind.Set)
                {
                    throw new NotSupportedException("RogueMod ABI 13 does not support struct or container TMap keys.");
                }
                if ((uint)keyKind > MaximumMapKeyEncodedValueKind)
                {
                    throw new NotSupportedException("RogueMod ABI 13 supports at most one TMap key kind.");
                }
                var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
                var encodedValueKind = EncodePropertyKind(valueKind, descriptor.ValueArray);
                if (encodedValueKind > MaximumMapValueEncodedValueKind)
                {
                    throw new NotSupportedException(
                        "RogueMod ABI 13 supports at most two nested container levels in a TMap value.");
                }
                return (uint)kind | ((uint)keyKind << ContainerValueKindShift)
                    | (encodedValueKind << (2 * ContainerValueKindShift));
            }
            default:
                return (uint)kind;
        }
    }

    private static string ReadNativeString(NativeUnrealValue value)
    {
        if (value.Reserved > MaximumStringLength)
        {
            throw new InvalidOperationException(
                $"The native bridge returned an Unreal string longer than {MaximumStringLength} UTF-16 code units.");
        }
        if (value.Data == 0)
        {
            return value.Reserved == 0
                ? string.Empty
                : throw new InvalidOperationException("The native bridge returned a null Unreal string pointer with a non-zero length.");
        }
        return Marshal.PtrToStringUni(unchecked((nint)value.Data), checked((int)value.Reserved));
    }

    private static NativeUnrealValue WriteNativeArray(
        uint encodedKind,
        UnrealValue value,
        UnrealArrayDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealArrayValue arrayValue)
        {
            throw new InvalidCastException(
                $"Unreal TArray<{descriptor.ElementUnrealType}> requires an UnrealArrayValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!ArrayDescriptorsMatch(arrayValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal array element '{arrayValue.Descriptor.ElementUnrealType}' cannot be written as " +
                $"'{descriptor.ElementUnrealType}'.");
        }
        if (arrayValue.Elements.Count > MaximumArrayLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Unreal arrays cannot exceed {MaximumArrayLength} elements.");
        }
        if (arrayValue.Elements.Count == 0)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var elementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var encodedElementKind = EncodePropertyKind(elementKind, descriptor.ElementArray);
        var values = allocations.AddValues(arrayValue.Elements.Count);
        for (var index = 0; index < arrayValue.Elements.Count; index++)
        {
            values[index] = ToNativeValue(
                elementKind,
                encodedElementKind,
                arrayValue.Elements[index],
                descriptor.ElementStruct,
                descriptor.ElementArray,
                null,
                null,
                null,
                allocations);
        }
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = checked((uint)arrayValue.Elements.Count),
            Data = unchecked((ulong)values)
        };
    }

    private static NativeUnrealValue WriteNativeSet(
        uint encodedKind,
        UnrealValue value,
        UnrealSetDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealSetValue setValue)
        {
            throw new InvalidCastException(
                $"Unreal TSet<{descriptor.ElementUnrealType}> requires an UnrealSetValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!SetDescriptorsMatch(setValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal set element '{setValue.Descriptor.ElementUnrealType}' cannot be written as " +
                $"'{descriptor.ElementUnrealType}'.");
        }
        if (setValue.Elements.Count > MaximumArrayLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Unreal sets cannot exceed {MaximumArrayLength} elements.");
        }
        if (setValue.Elements.Count == 0)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var elementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var encodedElementKind = EncodePropertyKind(elementKind, descriptor.ElementArray);
        var values = allocations.AddValues(setValue.Elements.Count);
        for (var index = 0; index < setValue.Elements.Count; index++)
        {
            values[index] = ToNativeValue(
                elementKind,
                encodedElementKind,
                setValue.Elements[index],
                descriptor.ElementStruct,
                descriptor.ElementArray,
                null,
                null,
                null,
                allocations);
        }
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = checked((uint)setValue.Elements.Count),
            Data = unchecked((ulong)values)
        };
    }

    private static NativeUnrealValue WriteNativeMap(
        uint encodedKind,
        UnrealValue value,
        UnrealMapDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealMapValue mapValue)
        {
            throw new InvalidCastException(
                $"Unreal TMap<{descriptor.KeyUnrealType}, {descriptor.ValueUnrealType}> requires an UnrealMapValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!MapDescriptorsMatch(mapValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal map '{mapValue.Descriptor.KeyUnrealType}, {mapValue.Descriptor.ValueUnrealType}' cannot be written as " +
                $"'{descriptor.KeyUnrealType}, {descriptor.ValueUnrealType}'.");
        }
        if (mapValue.Entries.Count > MaximumArrayLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Unreal maps cannot exceed {MaximumArrayLength} entries.");
        }
        if (mapValue.Entries.Count == 0)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var keyKind = GetPropertyKind(descriptor.KeyUnrealType, descriptor.KeySize);
        var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var encodedValueKind = EncodePropertyKind(valueKind, descriptor.ValueArray);
        var values = allocations.AddValues(mapValue.Entries.Count * 2);
        for (var index = 0; index < mapValue.Entries.Count; index++)
        {
            var entry = mapValue.Entries[index];
            values[index * 2] = ToNativeValue(
                keyKind,
                (uint)keyKind,
                entry.Key,
                descriptor.KeyStruct,
                null,
                null,
                null,
                null,
                allocations);
            values[index * 2 + 1] = ToNativeValue(
                valueKind,
                encodedValueKind,
                entry.Value,
                descriptor.ValueStruct,
                descriptor.ValueArray,
                null,
                null,
                null,
                allocations);
        }
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = checked((uint)mapValue.Entries.Count),
            Data = unchecked((ulong)values)
        };
    }

    private static UnrealArrayValue ReadNativeArray(NativeUnrealValue value, UnrealArrayDescriptor descriptor)
    {
        if (value.Reserved > MaximumArrayLength || (value.Data == 0 && value.Reserved != 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TArray<{descriptor.ElementUnrealType}> buffer.");
        }
        var expectedElementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var expectedEncodedElementKind = EncodePropertyKind(expectedElementKind, descriptor.ElementArray);
        var expectedEncodedKind = EncodePropertyKind(NativePropertyKind.Array, descriptor);
        if (value.Kind != expectedEncodedKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TArray<{descriptor.ElementUnrealType}> element kind.");
        }
        var elements = new UnrealValue[checked((int)value.Reserved)];
        var values = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < elements.Length; index++)
        {
            if (values[index].Kind != expectedEncodedElementKind)
            {
                throw new InvalidOperationException(
                    $"The native bridge returned a mismatched element at TArray index {index}.");
            }
            elements[index] = ToManagedValue(
                expectedElementKind,
                values[index],
                descriptor.ElementStruct,
                descriptor.ElementArray);
        }
        return new UnrealArrayValue(descriptor, elements);
    }

    private static UnrealSetValue ReadNativeSet(NativeUnrealValue value, UnrealSetDescriptor descriptor)
    {
        if (value.Reserved > MaximumArrayLength || (value.Data == 0 && value.Reserved != 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TSet<{descriptor.ElementUnrealType}> buffer.");
        }
        var expectedElementKind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        var expectedEncodedElementKind = EncodePropertyKind(expectedElementKind, descriptor.ElementArray);
        var expectedEncodedKind = EncodePropertyKind(NativePropertyKind.Set, setDescriptor: descriptor);
        if (value.Kind != expectedEncodedKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TSet<{descriptor.ElementUnrealType}> element kind.");
        }
        var elements = new UnrealValue[checked((int)value.Reserved)];
        var values = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < elements.Length; index++)
        {
            if (values[index].Kind != expectedEncodedElementKind)
            {
                throw new InvalidOperationException(
                    $"The native bridge returned a mismatched element at TSet index {index}.");
            }
            elements[index] = ToManagedValue(
                expectedElementKind,
                values[index],
                descriptor.ElementStruct,
                descriptor.ElementArray);
        }
        return new UnrealSetValue(descriptor, elements);
    }

    private static UnrealMapValue ReadNativeMap(NativeUnrealValue value, UnrealMapDescriptor descriptor)
    {
        if (value.Reserved > MaximumArrayLength || (value.Data == 0 && value.Reserved != 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TMap<{descriptor.KeyUnrealType}, {descriptor.ValueUnrealType}> buffer.");
        }
        var expectedKeyKind = GetPropertyKind(descriptor.KeyUnrealType, descriptor.KeySize);
        var expectedValueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var expectedEncodedValueKind = EncodePropertyKind(expectedValueKind, descriptor.ValueArray);
        var expectedEncodedKind = EncodePropertyKind(
            NativePropertyKind.Map,
            mapDescriptor: descriptor);
        if (value.Kind != expectedEncodedKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TMap<{descriptor.KeyUnrealType}, {descriptor.ValueUnrealType}> key/value kind.");
        }
        var entries = new List<KeyValuePair<UnrealValue, UnrealValue>>(checked((int)value.Reserved));
        var values = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < checked((int)value.Reserved); index++)
        {
            var key = values[index * 2];
            var entryValue = values[index * 2 + 1];
            if (key.Kind != (uint)expectedKeyKind || entryValue.Kind != expectedEncodedValueKind)
            {
                throw new InvalidOperationException(
                    $"The native bridge returned a mismatched entry at TMap index {index}.");
            }
            entries.Add(new KeyValuePair<UnrealValue, UnrealValue>(
                ToManagedValue(expectedKeyKind, key, descriptor.KeyStruct),
                ToManagedValue(expectedValueKind, entryValue, descriptor.ValueStruct, descriptor.ValueArray)));
        }
        return new UnrealMapValue(descriptor, entries);
    }

    private static NativeUnrealValue WriteNativeLazyObject(
        uint encodedKind,
        UnrealValue value,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealLazyObjectValue lazyValue)
        {
            throw new InvalidCastException(
                $"An Unreal lazy object reference requires an UnrealLazyObjectValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }

        var wire = new byte[LazyObjectWireSize];
        lazyValue.CopyNativeStorage().CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(wire.AsSpan(LazyObjectStorageSize), lazyValue.CachedHandle.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(32), lazyValue.ObjectId.A);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(36), lazyValue.ObjectId.B);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(40), lazyValue.ObjectId.C);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(44), lazyValue.ObjectId.D);
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = LazyObjectWireSize,
            Data = unchecked((ulong)allocations.AddBytes(wire))
        };
    }

    private static UnrealLazyObjectValue ReadNativeLazyObject(NativeUnrealValue value)
    {
        if (value.Reserved != LazyObjectWireSize || value.Data == 0)
        {
            throw new InvalidOperationException("The native bridge returned an invalid lazy object reference buffer.");
        }

        var wire = new ReadOnlySpan<byte>((void*)value.Data, checked((int)value.Reserved));
        var storage = wire[..LazyObjectStorageSize].ToArray();
        var cachedHandle = new UnrealObjectHandle(BinaryPrimitives.ReadUInt64LittleEndian(wire[LazyObjectStorageSize..]));
        var objectId = new UnrealGuid(
            BinaryPrimitives.ReadUInt32LittleEndian(wire[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(wire[44..]));
        return new UnrealLazyObjectValue(objectId, cachedHandle, storage);
    }

    private static NativeUnrealValue WriteNativeSoftObject(
        uint encodedKind,
        UnrealValue value,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealSoftObjectValue softValue)
        {
            throw new InvalidCastException(
                $"An Unreal soft object reference requires an UnrealSoftObjectValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }

        var wire = new byte[SoftObjectWireSize];
        softValue.CopyNativeStorage().CopyTo(wire, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            wire.AsSpan(UnrealSoftObjectValue.NativeStorageSize),
            softValue.CachedHandle.Value);
        var wirePointer = allocations.AddBytes(wire);
        var pathPointer = allocations.AddString(softValue.Path);
        Marshal.WriteIntPtr(wirePointer, UnrealSoftObjectValue.NativeStorageSize + sizeof(ulong), pathPointer);
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = SoftObjectWireSize,
            Data = unchecked((ulong)wirePointer)
        };
    }

    private static UnrealSoftObjectValue ReadNativeSoftObject(NativeUnrealValue value)
    {
        if (value.Reserved != SoftObjectWireSize || value.Data == 0)
        {
            throw new InvalidOperationException("The native bridge returned an invalid soft object reference buffer.");
        }

        var wire = *(NativeSoftObjectWire*)value.Data;
        var path = wire.Path == null ? string.Empty : new string(wire.Path);
        var storage = new ReadOnlySpan<byte>(wire.Storage, UnrealSoftObjectValue.NativeStorageSize).ToArray();
        return new UnrealSoftObjectValue(path, new UnrealObjectHandle(wire.CachedHandle), storage);
    }

    private static NativeUnrealValue WriteNativeOptional(
        uint encodedKind,
        UnrealValue value,
        UnrealOptionalDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealOptionalValue optionalValue)
        {
            throw new InvalidCastException(
                $"Unreal TOptional<{descriptor.ValueUnrealType}> requires an UnrealOptionalValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!OptionalDescriptorsMatch(optionalValue.Descriptor, descriptor))
        {
            throw new InvalidCastException(
                $"Unreal optional value '{optionalValue.Descriptor.ValueUnrealType}' cannot be written as " +
                $"'{descriptor.ValueUnrealType}'.");
        }
        if (!optionalValue.IsSet)
        {
            return new NativeUnrealValue { Kind = encodedKind };
        }

        var valueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var encodedValueKind = EncodePropertyKind(valueKind);
        var nativeValue = allocations.AddValues(1);
        *nativeValue = ToNativeValue(
            valueKind,
            encodedValueKind,
            optionalValue.Value,
            descriptor.ValueStruct,
            null,
            null,
            null,
            null,
            allocations);
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = 1,
            Data = unchecked((ulong)nativeValue)
        };
    }

    private static UnrealOptionalValue ReadNativeOptional(
        NativeUnrealValue value,
        UnrealOptionalDescriptor descriptor)
    {
        var expectedValueKind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        var expectedEncodedValueKind = EncodePropertyKind(expectedValueKind);
        var expectedEncodedKind = EncodePropertyKind(NativePropertyKind.Optional, optionalDescriptor: descriptor);
        if (value.Kind != expectedEncodedKind
            || value.Reserved > 1
            || (value.Reserved == 0 && value.Data != 0)
            || (value.Reserved == 1 && value.Data == 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned an invalid TOptional<{descriptor.ValueUnrealType}> buffer.");
        }
        if (value.Reserved == 0)
        {
            return new UnrealOptionalValue(descriptor, false, UnrealValue.Null);
        }

        var nativeValue = *(NativeUnrealValue*)value.Data;
        if (nativeValue.Kind != expectedEncodedValueKind)
        {
            throw new InvalidOperationException(
                $"The native bridge returned a mismatched TOptional<{descriptor.ValueUnrealType}> value kind.");
        }
        return new UnrealOptionalValue(
            descriptor,
            true,
            ToManagedValue(expectedValueKind, nativeValue, descriptor.ValueStruct));
    }

    private static bool OptionalDescriptorsMatch(UnrealOptionalDescriptor left, UnrealOptionalDescriptor right) =>
        StringComparer.Ordinal.Equals(left.ValueUnrealType, right.ValueUnrealType)
        && left.ValueSize == right.ValueSize
        && left.ValueByteOffset == right.ValueByteOffset
        && left.ValueByteMask == right.ValueByteMask
        && left.ValueFieldMask == right.ValueFieldMask
        && StringComparer.Ordinal.Equals(left.ValueStruct?.Path, right.ValueStruct?.Path)
        && left.ValueStruct?.Size == right.ValueStruct?.Size;

    private static bool ArrayDescriptorsMatch(UnrealArrayDescriptor left, UnrealArrayDescriptor right) =>
        StringComparer.Ordinal.Equals(left.ElementUnrealType, right.ElementUnrealType)
        && left.ElementSize == right.ElementSize
        && left.ElementByteOffset == right.ElementByteOffset
        && left.ElementByteMask == right.ElementByteMask
        && left.ElementFieldMask == right.ElementFieldMask
        && StringComparer.Ordinal.Equals(left.ElementStruct?.Path, right.ElementStruct?.Path)
        && left.ElementStruct?.Size == right.ElementStruct?.Size
        && (left.ElementArray, right.ElementArray) switch
        {
            (null, null) => true,
            ({ } leftNested, { } rightNested) => ArrayDescriptorsMatch(leftNested, rightNested),
            _ => false
        };

    private static bool SetDescriptorsMatch(UnrealSetDescriptor left, UnrealSetDescriptor right) =>
        StringComparer.Ordinal.Equals(left.ElementUnrealType, right.ElementUnrealType)
        && left.ElementSize == right.ElementSize
        && left.ElementByteOffset == right.ElementByteOffset
        && left.ElementByteMask == right.ElementByteMask
        && left.ElementFieldMask == right.ElementFieldMask
        && StringComparer.Ordinal.Equals(left.ElementStruct?.Path, right.ElementStruct?.Path)
        && left.ElementStruct?.Size == right.ElementStruct?.Size
        && (left.ElementArray, right.ElementArray) switch
        {
            (null, null) => true,
            ({ } leftNested, { } rightNested) => ArrayDescriptorsMatch(leftNested, rightNested),
            _ => false
        };

    private static bool MapDescriptorsMatch(UnrealMapDescriptor left, UnrealMapDescriptor right) =>
        StringComparer.Ordinal.Equals(left.KeyUnrealType, right.KeyUnrealType)
        && left.KeySize == right.KeySize
        && left.KeyByteOffset == right.KeyByteOffset
        && left.KeyByteMask == right.KeyByteMask
        && left.KeyFieldMask == right.KeyFieldMask
        && StringComparer.Ordinal.Equals(left.KeyStruct?.Path, right.KeyStruct?.Path)
        && left.KeyStruct?.Size == right.KeyStruct?.Size
        && StringComparer.Ordinal.Equals(left.ValueUnrealType, right.ValueUnrealType)
        && left.ValueSize == right.ValueSize
        && left.ValueByteOffset == right.ValueByteOffset
        && left.ValueByteMask == right.ValueByteMask
        && left.ValueFieldMask == right.ValueFieldMask
        && StringComparer.Ordinal.Equals(left.ValueStruct?.Path, right.ValueStruct?.Path)
        && left.ValueStruct?.Size == right.ValueStruct?.Size
        && (left.ValueArray, right.ValueArray) switch
        {
            (null, null) => true,
            ({ } leftNested, { } rightNested) => ArrayDescriptorsMatch(leftNested, rightNested),
            _ => false
        };

    private static UnrealArrayDescriptor RequireArrayDescriptor(UnrealArrayDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TArray element descriptor.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.ElementUnrealType)
            || descriptor.ElementSize <= 0
            || descriptor.ElementSize > MaximumStructSize)
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TArray element descriptor.");
        }
        var kind = GetPropertyKind(descriptor.ElementUnrealType, descriptor.ElementSize);
        if (kind == NativePropertyKind.Array)
        {
            if (descriptor.ElementArray is null)
            {
                throw new InvalidOperationException("The generated SDK did not provide a nested TArray descriptor.");
            }
            RequireArrayDescriptor(descriptor.ElementArray);
        }

        else if (descriptor.ElementArray is not null)
        {
            throw new InvalidOperationException("A non-array TArray element cannot have a nested array descriptor.");
        }
        if (kind == NativePropertyKind.Boolean
            && (descriptor.ElementByteOffset < 0
                || descriptor.ElementByteOffset >= descriptor.ElementSize
                || descriptor.ElementByteMask is < 0 or > byte.MaxValue
                || descriptor.ElementFieldMask is < 0 or > byte.MaxValue))
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TArray bool layout.");
        }
        if (kind == NativePropertyKind.Struct)
        {
            var structDescriptor = RequireStructDescriptor(descriptor.ElementStruct);
            if (structDescriptor.Size != descriptor.ElementSize)
            {
                throw new InvalidOperationException("The generated SDK provided a mismatched TArray struct size.");
            }
        }
        return descriptor;
    }

    private static UnrealOptionalDescriptor RequireOptionalDescriptor(UnrealOptionalDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TOptional value descriptor.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.ValueUnrealType)
            || descriptor.ValueSize <= 0
            || descriptor.ValueSize > MaximumStructSize)
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TOptional value descriptor.");
        }
        var kind = GetPropertyKind(descriptor.ValueUnrealType, descriptor.ValueSize);
        if (kind is NativePropertyKind.Array or NativePropertyKind.Optional)
        {
            throw new NotSupportedException("Nested containers inside TOptional are not supported yet.");
        }
        if (kind == NativePropertyKind.Boolean
            && (descriptor.ValueByteOffset < 0
                || descriptor.ValueByteOffset >= descriptor.ValueSize
                || descriptor.ValueByteMask is < 0 or > byte.MaxValue
                || descriptor.ValueFieldMask is < 0 or > byte.MaxValue))
        {
            throw new InvalidOperationException("The generated SDK provided an invalid TOptional bool layout.");
        }
        if (kind == NativePropertyKind.Struct)
        {
            var structDescriptor = RequireStructDescriptor(descriptor.ValueStruct);
            if (structDescriptor.Size != descriptor.ValueSize)
            {
                throw new InvalidOperationException("The generated SDK provided a mismatched TOptional struct size.");
            }
        }
        return descriptor;
    }

    private static (NativePropertyKind Kind, UnrealArrayDescriptor? Array) ValidateContainerValue(
        string label,
        string unrealType,
        int size,
        int byteOffset,
        int byteMask,
        int fieldMask,
        UnrealStructDescriptor? structDescriptor,
        UnrealArrayDescriptor? arrayDescriptor,
        bool allowContainers,
        bool allowStruct)
    {
        if (string.IsNullOrWhiteSpace(unrealType) || size <= 0 || size > MaximumStructSize)
        {
            throw new InvalidOperationException($"The generated SDK provided an invalid {label} descriptor.");
        }
        var kind = GetPropertyKind(unrealType, size);
        if (kind == NativePropertyKind.Array)
        {
            if (!allowContainers)
            {
                throw new NotSupportedException($"RogueMod ABI 13 does not support container {label}s.");
            }
            if (arrayDescriptor is null)
            {
                throw new InvalidOperationException($"The generated SDK did not provide a nested {label} descriptor.");
            }
            RequireArrayDescriptor(arrayDescriptor);
            return (kind, arrayDescriptor);
        }
        if (arrayDescriptor is not null)
        {
            throw new InvalidOperationException($"A non-array {label} cannot have a nested array descriptor.");
        }
        if (kind == NativePropertyKind.Boolean
            && (byteOffset < 0 || byteOffset >= size
                || byteMask is < 0 or > byte.MaxValue
                || fieldMask is < 0 or > byte.MaxValue))
        {
            throw new InvalidOperationException($"The generated SDK provided an invalid {label} bool layout.");
        }
        if (kind == NativePropertyKind.Struct)
        {
            if (!allowStruct)
            {
                throw new NotSupportedException($"RogueMod ABI 13 does not support struct {label}s.");
            }
            var structValue = RequireStructDescriptor(structDescriptor);
            if (structValue.Size != size)
            {
                throw new InvalidOperationException($"The generated SDK provided a mismatched {label} struct size.");
            }
        }
        return (kind, null);
    }

    private static UnrealSetDescriptor RequireSetDescriptor(UnrealSetDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TSet descriptor.");
        }
        var (elementKind, _) = ValidateContainerValue(
            "TSet element",
            descriptor.ElementUnrealType,
            descriptor.ElementSize,
            descriptor.ElementByteOffset,
            descriptor.ElementByteMask,
            descriptor.ElementFieldMask,
            descriptor.ElementStruct,
            descriptor.ElementArray,
            allowContainers: true,
            allowStruct: true);
        if (elementKind is NativePropertyKind.Map
            or NativePropertyKind.Set
            or NativePropertyKind.Optional)
        {
            throw new NotSupportedException("RogueMod ABI 13 does not support nested TMap/TSet/TOptional TSet elements.");
        }
        return descriptor;
    }

    private static UnrealMapDescriptor RequireMapDescriptor(UnrealMapDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a TMap descriptor.");
        }
        var (keyKind, _) = ValidateContainerValue(
            "TMap key",
            descriptor.KeyUnrealType,
            descriptor.KeySize,
            descriptor.KeyByteOffset,
            descriptor.KeyByteMask,
            descriptor.KeyFieldMask,
            descriptor.KeyStruct,
            null,
            allowContainers: false,
            allowStruct: false);
        if (keyKind is NativePropertyKind.Struct
            or NativePropertyKind.Optional
            or NativePropertyKind.Map
            or NativePropertyKind.Set)
        {
            throw new NotSupportedException("RogueMod ABI 13 does not support struct or container TMap keys.");
        }
        var (valueKind, _) = ValidateContainerValue(
            "TMap value",
            descriptor.ValueUnrealType,
            descriptor.ValueSize,
            descriptor.ValueByteOffset,
            descriptor.ValueByteMask,
            descriptor.ValueFieldMask,
            descriptor.ValueStruct,
            descriptor.ValueArray,
            allowContainers: true,
            allowStruct: true);
        if (valueKind is NativePropertyKind.Map
            or NativePropertyKind.Set
            or NativePropertyKind.Optional)
        {
            throw new NotSupportedException("RogueMod ABI 13 does not support nested TMap/TSet/TOptional TMap values.");
        }
        return descriptor;
    }

    private void EnsurePropertyCapabilities(
        NativePropertyKind kind,
        UnrealArrayDescriptor? arrayDescriptor,
        UnrealOptionalDescriptor? optionalDescriptor,
        UnrealMapDescriptor? mapDescriptor,
        UnrealSetDescriptor? setDescriptor)
    {
        if (arrayDescriptor?.ElementArray is not null
            && (Capabilities & UnrealReflectionCapabilities.NestedArrays) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support nested TArray values.");
        }
        if (optionalDescriptor is not null
            && (Capabilities & UnrealReflectionCapabilities.OptionalValues) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support TOptional values.");
        }
        if (kind == NativePropertyKind.WeakObject
            && (Capabilities & UnrealReflectionCapabilities.WeakObjectReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support weak UObject references.");
        }
        if (kind == NativePropertyKind.LazyObject
            && (Capabilities & UnrealReflectionCapabilities.LazyObjectReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support lazy UObject references.");
        }
        if (kind == NativePropertyKind.SoftObject
            && (Capabilities & UnrealReflectionCapabilities.SoftObjectReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support soft object references.");
        }
        if (kind == NativePropertyKind.Interface
            && (Capabilities & UnrealReflectionCapabilities.InterfaceReferences) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support interface references.");
        }
        if ((kind == NativePropertyKind.Map || kind == NativePropertyKind.Set)
            && (Capabilities & UnrealReflectionCapabilities.MapSetProperties) == 0)
        {
            throw new NotSupportedException("The active RogueMod bridge does not support TMap/TSet values.");
        }
    }

    private static UnrealStructDescriptor RequireStructDescriptor(UnrealStructDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            throw new NotSupportedException("The generated SDK did not provide a POD struct descriptor.");
        }
        ValidateStructDescriptor(descriptor);
        return descriptor;
    }

    private static void ValidateStructDescriptor(UnrealStructDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Path)
            || descriptor.Size <= 0
            || descriptor.Size > MaximumStructSize
            || descriptor.Alignment <= 0
            || (descriptor.Alignment & (descriptor.Alignment - 1)) != 0)
        {
            throw new InvalidOperationException($"Unreal struct descriptor '{descriptor.Path}' has an invalid size or alignment.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name)
                || !names.Add(field.Name)
                || field.ArrayDimension != 1
                || field.Offset < 0
                || field.Size <= 0
                || (long)field.Offset + field.Size > descriptor.Size)
            {
                throw new InvalidOperationException($"Unreal struct descriptor '{descriptor.Path}:{field.Name}' is invalid.");
            }
            var kind = GetPropertyKind(field.UnrealType, field.Size);
            if (kind is NativePropertyKind.Array
                or NativePropertyKind.Map
                or NativePropertyKind.Set
                or NativePropertyKind.Optional)
            {
                throw new NotSupportedException(
                    $"Unreal struct field '{descriptor.Path}:{field.Name}' is a container; " +
                    "struct fields support scalars, enums, strings, object references, and nested structs only.");
            }
            if (kind == NativePropertyKind.Boolean
                && (field.ByteOffset < 0
                    || field.ByteOffset >= field.Size
                    || field.ByteMask is < 0 or > byte.MaxValue
                    || field.FieldMask is < 0 or > byte.MaxValue))
            {
                throw new InvalidOperationException($"Bool field '{descriptor.Path}:{field.Name}' has an invalid bit layout.");
            }
            if (kind == NativePropertyKind.Struct)
            {
                ValidateStructDescriptor(field.Struct
                    ?? throw new InvalidOperationException($"Nested Unreal struct field '{descriptor.Path}:{field.Name}' has no descriptor."));
                if (field.Struct.Size != field.Size)
                {
                    throw new InvalidOperationException($"Nested Unreal struct field '{descriptor.Path}:{field.Name}' has a size mismatch.");
                }
            }
        }
    }

    private static NativeUnrealValue WriteNativeStruct(
        uint encodedKind,
        UnrealValue value,
        UnrealStructDescriptor descriptor,
        NativeAllocations allocations)
    {
        if (value.Value is not UnrealStructValue structValue)
        {
            throw new InvalidCastException(
                $"Unreal struct '{descriptor.Path}' requires an UnrealStructValue, not " +
                $"'{value.Value?.GetType().FullName ?? "null"}'.");
        }
        if (!StringComparer.Ordinal.Equals(descriptor.Path, structValue.Descriptor.Path))
        {
            throw new InvalidCastException(
                $"Unreal struct '{structValue.Descriptor.Path}' cannot be written as '{descriptor.Path}'.");
        }
        var fields = descriptor.Fields;
        var values = allocations.AddValues(fields.Count);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (!structValue.Fields.TryGetValue(field.Name, out var fieldValue))
            {
                throw new InvalidOperationException($"Unreal struct '{descriptor.Path}' is missing field '{field.Name}'.");
            }
            var fieldKind = GetPropertyKind(field.UnrealType, field.Size);
            values[index] = ToNativeValue(
                fieldKind,
                EncodePropertyKind(fieldKind),
                fieldValue,
                field.Struct,
                null,
                null,
                null,
                null,
                allocations);
        }
        return new NativeUnrealValue
        {
            Kind = encodedKind,
            Reserved = checked((uint)fields.Count),
            Data = unchecked((ulong)values)
        };
    }

    private static UnrealStructValue ReadNativeStruct(NativeUnrealValue value, UnrealStructDescriptor descriptor)
    {
        var fields = descriptor.Fields;
        if (value.Reserved != fields.Count || (value.Data == 0 && value.Reserved != 0))
        {
            throw new InvalidOperationException(
                $"The native bridge returned {value.Reserved} fields for Unreal struct '{descriptor.Path}', expected {fields.Count}.");
        }
        var fieldValues = new Dictionary<string, UnrealValue>(StringComparer.Ordinal);
        var values = (NativeUnrealValue*)value.Data;
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var fieldKind = GetPropertyKind(field.UnrealType, field.Size);
            if (values[index].Kind != EncodePropertyKind(fieldKind))
            {
                throw new InvalidOperationException(
                    $"The native bridge returned a mismatched field '{field.Name}' for struct '{descriptor.Path}'.");
            }
            fieldValues.Add(
                field.Name,
                ToManagedValue(fieldKind, values[index], field.Struct));
        }
        return new UnrealStructValue(descriptor, fieldValues);
    }

    private static void FreeNativeOutputAllocations(
        IReadOnlyList<UnrealParameterDescriptor> descriptors,
        NativeUnrealParameter[] parameters,
        NativeAllocations inputAllocations)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (!descriptors[index].IsOutput && !descriptors[index].IsReturn)
            {
                continue;
            }
            FreeNativeAllocation(parameters[index].Kind, parameters[index].Value, inputAllocations);
        }
    }

    private static void FreeNativeAllocation(
        uint encodedKind,
        NativeUnrealValue value,
        NativeAllocations? inputAllocations)
    {
        var kind = DecodePropertyKind(encodedKind);
        if (value.Data == 0 || inputAllocations?.Contains(value.Data) == true)
        {
            return;
        }
        if (kind is NativePropertyKind.Array or NativePropertyKind.Set or NativePropertyKind.Struct)
        {
            if (value.Reserved <= MaximumArrayLength)
            {
                var values = (NativeUnrealValue*)value.Data;
                for (var index = 0U; index < value.Reserved; index++)
                {
                    FreeNativeAllocation(values[index].Kind, values[index], inputAllocations);
                }
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind == NativePropertyKind.Map)
        {
            if (value.Reserved <= MaximumArrayLength)
            {
                var values = (NativeUnrealValue*)value.Data;
                for (var index = 0U; index < value.Reserved; index++)
                {
                    FreeNativeAllocation(values[index * 2].Kind, values[index * 2], inputAllocations);
                    FreeNativeAllocation(values[index * 2 + 1].Kind, values[index * 2 + 1], inputAllocations);
                }
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind == NativePropertyKind.Optional)
        {
            if (value.Reserved == 1)
            {
                var nested = *(NativeUnrealValue*)value.Data;
                FreeNativeAllocation(nested.Kind, nested, inputAllocations);
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind == NativePropertyKind.SoftObject)
        {
            var wire = *(NativeSoftObjectWire*)value.Data;
            if (wire.Path != null)
            {
                Marshal.FreeCoTaskMem((nint)wire.Path);
            }
            Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
            return;
        }
        if (kind is not (NativePropertyKind.String
            or NativePropertyKind.Name
            or NativePropertyKind.Text
            or NativePropertyKind.LazyObject))
        {
            return;
        }
        Marshal.FreeCoTaskMem(unchecked((nint)value.Data));
    }

    private sealed class NativeAllocations : IDisposable
    {
        private readonly List<nint> allocations = [];

        internal nint AddString(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUni(value);
            allocations.Add(pointer);
            return pointer;
        }

        internal nint AddBytes(byte[] value)
        {
            var pointer = Marshal.AllocCoTaskMem(value.Length);
            Marshal.Copy(value, 0, pointer, value.Length);
            allocations.Add(pointer);
            return pointer;
        }

        internal NativeUnrealValue* AddValues(int count)
        {
            var bytes = checked(count * sizeof(NativeUnrealValue));
            var pointer = Marshal.AllocCoTaskMem(bytes);
            new Span<byte>((void*)pointer, bytes).Clear();
            allocations.Add(pointer);
            return (NativeUnrealValue*)pointer;
        }

        internal bool Contains(ulong pointer) =>
            allocations.Contains(unchecked((nint)pointer));

        internal void Release() => allocations.Clear();

        public void Dispose()
        {
            foreach (var pointer in allocations)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
            allocations.Clear();
        }
    }

    private sealed class HookRegistration(
        NativeUnrealReflection owner,
        UnrealFunctionDescriptor function,
        UnrealHookPhase phase,
        Action<UnrealHookContext> callback)
    {
        internal NativeUnrealReflection Owner { get; } = owner;
        internal UnrealFunctionDescriptor Function { get; } = function;
        internal UnrealHookPhase Phase { get; } = phase;
        internal Action<UnrealHookContext> Callback { get; } = callback;
        internal int Disabled;
    }

    private sealed class HookSubscription(
        ulong context,
        ulong nativeToken,
        delegate* unmanaged[Cdecl]<ulong, int> unregister) : IDisposable
    {
        private long managedContext = unchecked((long)context);
        private long token = unchecked((long)nativeToken);

        public void Dispose()
        {
            var releasedToken = unchecked((ulong)Interlocked.Exchange(ref token, 0));
            var releasedContext = unchecked((ulong)Interlocked.Exchange(ref managedContext, 0));
            if (releasedContext != 0)
            {
                HookRegistrations.TryRemove(releasedContext, out _);
            }
            if (releasedToken != 0 && unregister != null)
            {
                unregister(releasedToken);
            }
        }
    }

    internal struct NativeUnrealValue
    {
        internal uint Kind;
        internal uint Reserved;
        internal ulong Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSoftObjectWire
    {
        internal fixed byte Storage[UnrealSoftObjectValue.NativeStorageSize];
        internal ulong CachedHandle;
        internal char* Path;
    }

    internal struct NativeUnrealParameter
    {
        internal uint Kind;
        internal uint Flags;
        internal int Offset;
        internal int Size;
        internal uint ArrayDimension;
        internal uint BoolLayout;
        internal NativeUnrealValue Value;
    }

    [Flags]
    private enum NativeParameterFlags : uint
    {
        Input = 1,
        Output = 2,
        Return = 4,
        Modified = 8
    }

}
