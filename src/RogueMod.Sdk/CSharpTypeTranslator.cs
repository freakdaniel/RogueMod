using System.Text;
using System.Text.Json;

namespace RogueMod.Sdk;

/// <summary>
/// Central translation policy from reflected Unreal types to generated C# types,
/// transport expressions, and runtime descriptors. Generated properties,
/// invocations, and future hook signatures share this policy.
/// </summary>
internal static class CSharpTypeTranslator
{
    private const int MaximumArrayNestingDepth = 3;

    internal static CsType Resolve(
        UnrealSdkTypeReference type,
        int arrayDimension,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        CsType result = type.Kind switch
        {
            "BoolProperty" => Simple("bool"),
            "ByteProperty" when type.TypePath is not null && typeNames.TryGetValue(type.TypePath, out var byteEnum) => Simple(byteEnum),
            "ByteProperty" => Simple("byte"),
            "Int8Property" => Simple("sbyte"),
            "Int16Property" => Simple("short"),
            "IntProperty" => Simple("int"),
            "Int64Property" => Simple("long"),
            "UInt16Property" => Simple("ushort"),
            "UInt32Property" => Simple("uint"),
            "UInt64Property" => Simple("ulong"),
            "FloatProperty" => Simple("float"),
            "DoubleProperty" => Simple("double"),
            "StrProperty" or "NameProperty" or "TextProperty" => Simple("string"),
            "EnumProperty" when type.TypePath is not null && typeNames.TryGetValue(type.TypePath, out var enumName) => Simple(enumName),
            "StructProperty" when type.TypePath is not null
                && supportedStructPaths.Contains(type.TypePath)
                && typeNames.TryGetValue(type.TypePath, out var structName) => Struct(structName),
            "SoftObjectProperty" or "SoftClassProperty" when type.TypePath is not null
                && typeNames.TryGetValue(type.TypePath, out var softObjectName) => SoftObject(softObjectName),
            "SoftObjectProperty" or "SoftClassProperty" => SoftObject("UnrealObject"),
            "ObjectProperty" or "ClassProperty" or "InterfaceProperty" or "WeakObjectProperty" when type.TypePath is not null
                && typeNames.TryGetValue(type.TypePath, out var objectName) => new CsType(objectName + "?", true, objectName),
            "ObjectProperty" or "ClassProperty" or "InterfaceProperty" or "WeakObjectProperty" =>
                new CsType("UnrealObject?", true, "UnrealObject"),
            "LazyObjectProperty" when type.TypePath is not null
                && typeNames.TryGetValue(type.TypePath, out var lazyObjectName) => LazyObject(lazyObjectName),
            "LazyObjectProperty" => LazyObject("UnrealObject"),
            "ArrayProperty" when type.Inner is not null && IsSupportedArrayElement(type.Inner, supportedStructPaths) =>
                Array(Resolve(type.Inner, 1, typeNames, supportedStructPaths)),
            "SetProperty" when type.Inner is not null && IsSupportedArrayElement(type.Inner, supportedStructPaths) =>
                SetType(Resolve(type.Inner, 1, typeNames, supportedStructPaths)),
            "MapProperty" when type.Key is not null && type.Value is not null
                && IsSupportedMapKey(type.Key)
                && IsSupportedArrayElement(type.Value, supportedStructPaths) =>
                MapType(
                    Resolve(type.Key, 1, typeNames, supportedStructPaths),
                    Resolve(type.Value, 1, typeNames, supportedStructPaths)),
            "OptionalProperty" when type.Inner is not null && IsSupportedOptionalValue(type.Inner, supportedStructPaths) =>
                Optional(Resolve(type.Inner, 1, typeNames, supportedStructPaths)),
            _ => Simple("UnrealValue")
        };
        return arrayDimension > 1 ? Array(result) : result;
    }

    internal static string ReadValueExpression(
        CsType type,
        string valueExpression,
        string? descriptorExpression = null,
        int containerDepth = 0) =>
        type.ObjectWrapper
            ? $"WrapObject({valueExpression}, static (unreal, handle) => new {type.NonNullableName}(unreal, handle))"
            : type.LazyObjectAdapter
                ? $"UnrealLazyObjectReference<{type.NonNullableName}>.FromUnrealValue({valueExpression}, handle => new {type.NonNullableName}(Unreal, handle))"
            : type.SoftObjectAdapter
                ? $"UnrealSoftObjectReference<{type.NonNullableName}>.FromUnrealValue({valueExpression}, handle => new {type.NonNullableName}(Unreal, handle))"
            : type.StructAdapter
                ? $"{type.Name}.FromUnrealValue({valueExpression})"
: type.ArrayAdapter
                ? $"UnrealArrayValue.ToList<{type.Element!.Name}>({valueExpression}, element{containerDepth} => {ReadValueExpression(type.Element, $"element{containerDepth}", null, containerDepth + 1)})"
                : type.OptionalAdapter
                    ? $"UnrealOptional<{type.Element!.Name}>.FromUnrealValue({valueExpression}, optional{containerDepth} => {ReadValueExpression(type.Element, $"optional{containerDepth}", null, containerDepth + 1)})"
                    : type.SetAdapter
                        ? $"UnrealSetValue.ToSet<{type.Element!.Name}>({valueExpression}, element{containerDepth} => {ReadValueExpression(type.Element, $"element{containerDepth}", null, containerDepth + 1)})"
                        : type.MapAdapter
                            ? $"UnrealMapValue.ToDictionary<{type.Key!.Name}, {type.Value!.Name}>({valueExpression}, key{containerDepth} => {ReadValueExpression(type.Key, $"key{containerDepth}", null, containerDepth + 1)}, value{containerDepth} => {ReadValueExpression(type.Value, $"value{containerDepth}", null, containerDepth + 1)})"
                            : $"{valueExpression}.As<{type.Name}>()";

    internal static string ReadHookValueExpression(
        CsType type,
        string valueExpression,
        string unrealExpression,
        int containerDepth = 0) =>
        type.ObjectWrapper
            ? $"UnrealHookValue.WrapObject<{type.NonNullableName}>({valueExpression}, {unrealExpression}, static (reflection, handle) => new {type.NonNullableName}(reflection, handle))"
            : type.LazyObjectAdapter
                ? $"UnrealLazyObjectReference<{type.NonNullableName}>.FromUnrealValue({valueExpression}, handle => new {type.NonNullableName}({unrealExpression}, handle))"
            : type.SoftObjectAdapter
                ? $"UnrealSoftObjectReference<{type.NonNullableName}>.FromUnrealValue({valueExpression}, handle => new {type.NonNullableName}({unrealExpression}, handle))"
            : type.StructAdapter
                ? $"{type.Name}.FromUnrealValue({valueExpression})"
: type.ArrayAdapter
                ? $"UnrealArrayValue.ToList<{type.Element!.Name}>({valueExpression}, hookElement{containerDepth} => {ReadHookValueExpression(type.Element, $"hookElement{containerDepth}", unrealExpression, containerDepth + 1)})"
                : type.OptionalAdapter
                    ? $"UnrealOptional<{type.Element!.Name}>.FromUnrealValue({valueExpression}, hookOptional{containerDepth} => {ReadHookValueExpression(type.Element, $"hookOptional{containerDepth}", unrealExpression, containerDepth + 1)})"
                    : type.SetAdapter
                        ? $"UnrealSetValue.ToSet<{type.Element!.Name}>({valueExpression}, hookElement{containerDepth} => {ReadHookValueExpression(type.Element, $"hookElement{containerDepth}", unrealExpression, containerDepth + 1)})"
                        : type.MapAdapter
                            ? $"UnrealMapValue.ToDictionary<{type.Key!.Name}, {type.Value!.Name}>({valueExpression}, hookKey{containerDepth} => {ReadHookValueExpression(type.Key, $"hookKey{containerDepth}", unrealExpression, containerDepth + 1)}, hookValue{containerDepth} => {ReadHookValueExpression(type.Value, $"hookValue{containerDepth}", unrealExpression, containerDepth + 1)})"
                            : $"{valueExpression}.As<{type.Name}>()";

    internal static string WriteHookValueExpression(
        CsType type,
        string valueExpression,
        string descriptorOwnerExpression) =>
        type.ObjectWrapper
            ? $"UnrealValue.From({valueExpression}?.Handle ?? UnrealObjectHandle.Null)"
            : type.StructAdapter
                ? $"{valueExpression}.ToUnrealValue()"
                : type.LazyObjectAdapter
                    ? $"{valueExpression}.ToUnrealValue()"
                    : type.SoftObjectAdapter
                        ? $"{valueExpression}.ToUnrealValue()"
                    : type.ArrayAdapter || type.OptionalAdapter || type.SetAdapter || type.MapAdapter
                        ? WriteValueExpression(
                            type,
                            valueExpression,
                            ValueDescriptorExpression(type, descriptorOwnerExpression))
                        : $"UnrealValue.From({valueExpression})";

    internal static string WriteValueExpression(CsType type, string valueExpression, string descriptorExpression)
    {
        if (type.MapAdapter)
        {
            return WriteMapValueExpression(type, valueExpression, descriptorExpression, 0);
        }
        if (type.Element is null)
        {
            throw new InvalidOperationException($"C# type '{type.Name}' is not an Unreal container adapter.");
        }
        return type.ArrayAdapter
            ? WriteArrayValueExpression(type, valueExpression, descriptorExpression, 0)
            : type.OptionalAdapter
                ? WriteOptionalValueExpression(type, valueExpression, descriptorExpression, 0)
                : type.SetAdapter
                    ? WriteSetValueExpression(type, valueExpression, descriptorExpression, 0)
                    : throw new InvalidOperationException($"C# type '{type.Name}' is not an Unreal container adapter.");
    }

    internal static string ValueDescriptorExpression(CsType type, string descriptorOwnerExpression) =>
        type.ArrayAdapter
            ? $"{descriptorOwnerExpression}.Array!"
            : type.OptionalAdapter
                ? $"{descriptorOwnerExpression}.Optional!"
                : type.SetAdapter
                    ? $"{descriptorOwnerExpression}.Set!"
                    : type.MapAdapter
                        ? $"{descriptorOwnerExpression}.Map!"
                        : throw new InvalidOperationException($"C# type '{type.Name}' has no container descriptor.");

internal static string? ValueDescriptorExpressionOrNull(CsType type, string descriptorOwnerExpression) =>
    type.ArrayAdapter || type.OptionalAdapter || type.SetAdapter || type.MapAdapter
        ? ValueDescriptorExpression(type, descriptorOwnerExpression)
        : null;

    internal static void AppendValueDescriptors(
        StringBuilder builder,
        UnrealSdkTypeReference type,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        if (type.Kind == "StructProperty"
            && type.TypePath is not null
            && supportedStructPaths.Contains(type.TypePath)
            && typeNames.TryGetValue(type.TypePath, out var typeName))
        {
            builder.Append(", Struct: ").Append(typeName).Append(".Descriptor");
        }
        if (type.Kind == "ArrayProperty"
            && type.Inner is not null
            && IsSupportedArrayElement(type.Inner, supportedStructPaths))
        {
            builder.Append(", Array: ");
            AppendArrayDescriptor(builder, type.Inner, typeNames, supportedStructPaths);
        }
    }

    internal static void AppendValueDescriptorInitializer(
        StringBuilder builder,
        UnrealSdkTypeReference type,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        var hasOptional = type.Kind == "OptionalProperty"
            && type.Inner is not null
            && IsSupportedOptionalValue(type.Inner, supportedStructPaths);
        var hasSet = type.Kind == "SetProperty"
            && type.Inner is not null
            && IsSupportedArrayElement(type.Inner, supportedStructPaths);
        var hasMap = type.Kind == "MapProperty"
            && type.Key is not null
            && type.Value is not null
            && IsSupportedMapKey(type.Key)
            && IsSupportedArrayElement(type.Value, supportedStructPaths);
        if (!hasOptional && !hasSet && !hasMap)
        {
            return;
        }
        builder.Append(" { ");
        var separator = string.Empty;
        if (hasOptional)
        {
            builder.Append("Optional = ");
            AppendOptionalDescriptor(builder, type.Inner!, typeNames, supportedStructPaths);
            separator = ", ";
        }
        if (hasSet)
        {
            builder.Append(separator).Append("Set = ");
            AppendSetDescriptor(builder, type.Inner!, typeNames, supportedStructPaths);
            separator = ", ";
        }
        if (hasMap)
        {
            builder.Append(separator).Append("Map = ");
            AppendMapDescriptor(builder, type.Key!, type.Value!, typeNames, supportedStructPaths);
        }
        builder.Append(" }");
    }

    internal static bool IsSupportedArrayElement(
        UnrealSdkTypeReference type,
        IReadOnlySet<string> supportedStructPaths,
        int arrayDepth = 1) => type.Kind switch
    {
        "BoolProperty" or "Int8Property" or "ByteProperty" => type.Size == 1,
        "Int16Property" or "UInt16Property" => type.Size == 2,
        "IntProperty" or "UInt32Property" or "FloatProperty" => type.Size == 4,
        "Int64Property" or "UInt64Property" or "DoubleProperty" => type.Size == 8,
        "EnumProperty" => type.Size is 1 or 2 or 4 or 8,
        "ObjectProperty" or "ClassProperty" => type.Size == 8,
        "StrProperty" or "TextProperty" => type.Size == 16,
        "NameProperty" => type.Size == 8,
        "StructProperty" when type.TypePath is not null => supportedStructPaths.Contains(type.TypePath),
        "ArrayProperty" when type.Size == 16 && type.Inner is not null && arrayDepth < MaximumArrayNestingDepth =>
            IsSupportedArrayElement(type.Inner, supportedStructPaths, arrayDepth + 1),
        _ => false
    };

    internal static bool IsSupportedMapKey(UnrealSdkTypeReference type) => type.Kind switch
    {
        "BoolProperty" or "Int8Property" or "ByteProperty" => type.Size == 1,
        "Int16Property" or "UInt16Property" => type.Size == 2,
        "IntProperty" or "UInt32Property" or "FloatProperty" => type.Size == 4,
        "Int64Property" or "UInt64Property" or "DoubleProperty" => type.Size == 8,
        "EnumProperty" => type.Size is 1 or 2 or 4 or 8,
        "StrProperty" or "TextProperty" => type.Size == 16,
        "NameProperty" => type.Size == 8,
        _ => false
    };

    internal static bool IsSupportedOptionalValue(
        UnrealSdkTypeReference type,
        IReadOnlySet<string> supportedStructPaths) => type.Kind switch
    {
        "BoolProperty" or "Int8Property" or "ByteProperty" => type.Size == 1,
        "Int16Property" or "UInt16Property" => type.Size == 2,
        "IntProperty" or "UInt32Property" or "FloatProperty" => type.Size == 4,
        "Int64Property" or "UInt64Property" or "DoubleProperty" => type.Size == 8,
        "EnumProperty" => type.Size is 1 or 2 or 4 or 8,
        "ObjectProperty" or "ClassProperty" => type.Size == 8,
        "StrProperty" or "TextProperty" => type.Size == 16,
        "NameProperty" => type.Size == 8,
        "StructProperty" when type.TypePath is not null => supportedStructPaths.Contains(type.TypePath),
        _ => false
    };

    internal static IReadOnlySet<string> BuildSupportedStructPaths(IReadOnlyList<UnrealSdkType> types)
    {
        var structs = types
            .Where(type => type.Kind == UnrealSdkTypeKind.Struct)
            .ToDictionary(type => type.Path, StringComparer.Ordinal);
        var supported = new HashSet<string>(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var type in structs.Values)
            {
                if (supported.Contains(type.Path)
                    || type.SuperPath is not null
                    || type.Size <= 0
                    || type.Alignment <= 0
                    || !HasFlag(type.Flags, "STRUCT_IsPlainOldData")
                    || !HasFlag(type.Flags, "STRUCT_NoDestructor"))
                {
                    continue;
                }
                var fields = type.Properties.Where(property => !HasFlag(property.Flags, "CPF_Parm")).ToArray();
                if (fields.All(field => IsSupportedPodField(field, structs, supported, type.Size)))
                {
                    supported.Add(type.Path);
                    changed = true;
                }
            }
        }
        return supported;
    }

    private static string WriteSetValueExpression(CsType type, string valueExpression, string setDescriptorExpression, int containerDepth)
    {
        var element = type.Element ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal set element type.");
        var elementName = $"element{containerDepth}";
        return $"UnrealSetValue.From({setDescriptorExpression}, {valueExpression}, {elementName} => {ContainerElementEncodeExpression(element, elementName, $"{setDescriptorExpression}.ElementArray!")})";
    }

    private static string WriteMapValueExpression(CsType type, string valueExpression, string mapDescriptorExpression, int containerDepth)
    {
        var key = type.Key ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal map key type.");
        var value = type.Value ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal map value type.");
        var keyName = $"key{containerDepth}";
        var valueName = $"value{containerDepth}";
        return $"UnrealMapValue.From({mapDescriptorExpression}, {valueExpression}, {keyName} => {ContainerElementEncodeExpression(key, keyName, null)}, {valueName} => {ContainerElementEncodeExpression(value, valueName, $"{mapDescriptorExpression}.ValueArray!")})";
    }

    private static string ContainerElementEncodeExpression(CsType element, string elementName, string? arrayDescriptorExpression)
    {
        if (element.ObjectWrapper)
        {
            return $"UnrealValue.From({elementName}?.Handle ?? UnrealObjectHandle.Null)";
        }
        if (element.StructAdapter)
        {
            return $"{elementName}.ToUnrealValue()";
        }
        if (element.ArrayAdapter)
        {
            return WriteArrayValueExpression(element, elementName, arrayDescriptorExpression!, 1);
        }
        return $"UnrealValue.From({elementName})";
    }

    private static string WriteArrayValueExpression(CsType type, string valueExpression, string arrayDescriptorExpression, int arrayDepth)
    {
        var element = type.Element ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal array element type.");
        var elementName = $"element{arrayDepth}";
        var encoded = element.ObjectWrapper
            ? $"UnrealValue.From({elementName}?.Handle ?? UnrealObjectHandle.Null)"
            : element.StructAdapter
                ? $"{elementName}.ToUnrealValue()"
                : element.ArrayAdapter
                    ? WriteArrayValueExpression(element, elementName, $"{arrayDescriptorExpression}.ElementArray!", arrayDepth + 1)
                    : $"UnrealValue.From({elementName})";
        return $"UnrealArrayValue.From({arrayDescriptorExpression}, {valueExpression}, {elementName} => {encoded})";
    }

    private static string WriteOptionalValueExpression(CsType type, string valueExpression, string optionalDescriptorExpression, int containerDepth)
    {
        var element = type.Element ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal optional value type.");
        var elementName = $"optional{containerDepth}";
        var encoded = element.ObjectWrapper
            ? $"UnrealValue.From({elementName}?.Handle ?? UnrealObjectHandle.Null)"
            : element.StructAdapter ? $"{elementName}.ToUnrealValue()" : $"UnrealValue.From({elementName})";
        return $"{valueExpression}.ToUnrealValue({optionalDescriptorExpression}, {elementName} => {encoded})";
    }

    private static void AppendOptionalDescriptor(
        StringBuilder builder,
        UnrealSdkTypeReference value,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("new(")
            .Append(Literal(Describe(value))).Append(", ")
            .Append(value.Size).Append(", ")
            .Append(value.ByteOffset).Append(", ")
            .Append(value.ByteMask).Append(", ")
            .Append(value.FieldMask);
        if (value.Kind == "StructProperty"
            && value.TypePath is not null
            && supportedStructPaths.Contains(value.TypePath)
            && typeNames.TryGetValue(value.TypePath, out var valueTypeName))
        {
            builder.Append(", ValueStruct: ").Append(valueTypeName).Append(".Descriptor");
        }
        builder.Append(')');
    }

    private static void AppendArrayDescriptor(
        StringBuilder builder,
        UnrealSdkTypeReference element,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("new(")
            .Append(Literal(Describe(element))).Append(", ")
            .Append(element.Size).Append(", ")
            .Append(element.ByteOffset).Append(", ")
            .Append(element.ByteMask).Append(", ")
            .Append(element.FieldMask);
        if (element.Kind == "StructProperty"
            && element.TypePath is not null
            && supportedStructPaths.Contains(element.TypePath)
            && typeNames.TryGetValue(element.TypePath, out var elementTypeName))
        {
            builder.Append(", ElementStruct: ").Append(elementTypeName).Append(".Descriptor");
        }
        builder.Append(')');
        if (element.Kind == "ArrayProperty" && element.Inner is not null)
        {
            builder.Append(" { ElementArray = ");
            AppendArrayDescriptor(builder, element.Inner, typeNames, supportedStructPaths);
            builder.Append(" }");
        }
    }

    private static void AppendSetDescriptor(
        StringBuilder builder,
        UnrealSdkTypeReference element,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("new(")
            .Append(Literal(Describe(element))).Append(", ")
            .Append(element.Size).Append(", ")
            .Append(element.ByteOffset).Append(", ")
            .Append(element.ByteMask).Append(", ")
            .Append(element.FieldMask);
        if (element.Kind == "StructProperty"
            && element.TypePath is not null
            && supportedStructPaths.Contains(element.TypePath)
            && typeNames.TryGetValue(element.TypePath, out var elementTypeName))
        {
            builder.Append(", ElementStruct: ").Append(elementTypeName).Append(".Descriptor");
        }
        builder.Append(')');
        if (element.Kind == "ArrayProperty" && element.Inner is not null)
        {
            builder.Append(" { ElementArray = ");
            AppendArrayDescriptor(builder, element.Inner, typeNames, supportedStructPaths);
            builder.Append(" }");
        }
    }

    private static void AppendMapDescriptor(
        StringBuilder builder,
        UnrealSdkTypeReference key,
        UnrealSdkTypeReference value,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("new(")
            .Append(Literal(Describe(key))).Append(", ")
            .Append(key.Size).Append(", ")
            .Append(Literal(Describe(value))).Append(", ")
            .Append(value.Size).Append(", ")
            .Append(key.ByteOffset).Append(", ")
            .Append(key.ByteMask).Append(", ")
            .Append(key.FieldMask).Append(", ")
            .Append(value.ByteOffset).Append(", ")
            .Append(value.ByteMask).Append(", ")
            .Append(value.FieldMask);
        if (key.Kind == "StructProperty"
            && key.TypePath is not null
            && supportedStructPaths.Contains(key.TypePath)
            && typeNames.TryGetValue(key.TypePath, out var keyTypeName))
        {
            builder.Append(", KeyStruct: ").Append(keyTypeName).Append(".Descriptor");
        }
        if (value.Kind == "StructProperty"
            && value.TypePath is not null
            && supportedStructPaths.Contains(value.TypePath)
            && typeNames.TryGetValue(value.TypePath, out var valueTypeName))
        {
            builder.Append(", ValueStruct: ").Append(valueTypeName).Append(".Descriptor");
        }
        builder.Append(')');
        if (value.Kind == "ArrayProperty" && value.Inner is not null)
        {
            builder.Append(" { ValueArray = ");
            AppendArrayDescriptor(builder, value.Inner, typeNames, supportedStructPaths);
            builder.Append(" }");
        }
    }

    private static bool IsSupportedPodField(
        UnrealSdkProperty field,
        IReadOnlyDictionary<string, UnrealSdkType> structs,
        IReadOnlySet<string> supported,
        int ownerSize)
    {
        if (field.ArrayDimension != 1 || field.Offset < 0 || field.Size <= 0 || field.Offset + field.Size > ownerSize)
        {
            return false;
        }
        return field.Type.Kind switch
        {
            "BoolProperty" or "Int8Property" or "ByteProperty" => field.Size == 1,
            "Int16Property" or "UInt16Property" => field.Size == 2,
            "IntProperty" or "UInt32Property" or "FloatProperty" => field.Size == 4,
            "Int64Property" or "UInt64Property" or "DoubleProperty" => field.Size == 8,
            "EnumProperty" => field.Size is 1 or 2 or 4 or 8,
            "StructProperty" when field.Type.TypePath is not null
                && supported.Contains(field.Type.TypePath)
                && structs.TryGetValue(field.Type.TypePath, out var nested) => field.Size == nested.Size,
            _ => false
        };
    }

    internal static string Describe(UnrealSdkTypeReference type) =>
        type.TypePath is null ? type.Kind : $"{type.Kind}:{type.TypePath}";

    private static CsType Simple(string name) => new(name, false, name);
    private static CsType Struct(string name) => new(name, false, name, true);
    private static CsType Array(CsType inner) => new($"IReadOnlyList<{inner.Name}>", false, string.Empty, ArrayAdapter: true, Element: inner);
    private static CsType SetType(CsType inner) => new($"IReadOnlySet<{inner.Name}>", false, string.Empty, SetAdapter: true, Element: inner);
    private static CsType MapType(CsType key, CsType value) =>
        new($"IReadOnlyDictionary<{key.Name}, {value.Name}>", false, string.Empty, MapAdapter: true, Key: key, Value: value);
    private static CsType Optional(CsType inner) => new($"UnrealOptional<{inner.Name}>", false, string.Empty, OptionalAdapter: true, Element: inner);
    private static CsType LazyObject(string targetName) => new($"UnrealLazyObjectReference<{targetName}>", false, targetName, LazyObjectAdapter: true);
    private static CsType SoftObject(string targetName) => new($"UnrealSoftObjectReference<{targetName}>", false, targetName, SoftObjectAdapter: true);
    private static bool HasFlag(string flags, string flag) =>
        flags.Split('|', StringSplitOptions.TrimEntries).Contains(flag, StringComparer.Ordinal);
    private static string Literal(string value) => JsonSerializer.Serialize(value);
}

internal sealed record CsType(
    string Name,
    bool ObjectWrapper,
    string NonNullableName,
    bool StructAdapter = false,
    bool ArrayAdapter = false,
    bool OptionalAdapter = false,
    bool LazyObjectAdapter = false,
    bool SoftObjectAdapter = false,
    bool SetAdapter = false,
    bool MapAdapter = false,
    CsType? Element = null,
    CsType? Key = null,
    CsType? Value = null);
