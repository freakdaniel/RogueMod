using System.Security.Cryptography;
using System.Text.Json;

namespace RogueMod.Sdk;

/// <summary>Imports a UE4SS JMAP reflection dump into an <see cref="UnrealSdkModel"/>.</summary>
public sealed class JMapImporter
{
    /// <summary>Parses the dump at <paramref name="path"/>, computes its SHA-256, and maps every class, script struct, and enum into the model.</summary>
    /// <param name="path">Path to the <c>.jmap</c> file.</param>
    /// <returns>The imported model with full provenance metadata.</returns>
    public UnrealSdkModel Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var objects = root.GetProperty("objects");

        var rawObjects = objects.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        var types = new List<UnrealSdkType>();
        foreach (var (objectPath, value) in rawObjects.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var kindText = RequiredString(value, "type");
            var kind = kindText switch
            {
                "Class" => UnrealSdkTypeKind.Class,
                "ScriptStruct" => UnrealSdkTypeKind.Struct,
                "Enum" => UnrealSdkTypeKind.Enum,
                _ => (UnrealSdkTypeKind?)null
            };
            if (kind is null)
            {
                continue;
            }

            var functions = new List<UnrealSdkFunction>();
            if (kind == UnrealSdkTypeKind.Class && value.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    var childPath = child.GetString();
                    if (childPath is null
                        || !rawObjects.TryGetValue(childPath, out var function)
                        || !RequiredString(function, "type").Equals("Function", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    functions.Add(new UnrealSdkFunction(
                        childPath,
                        LeafName(childPath),
                        OptionalString(function, "function_flags") ?? string.Empty,
                        ReadProperties(function)));
                }
            }

            var enumValues = new List<UnrealSdkEnumValue>();
            if (kind == UnrealSdkTypeKind.Enum && value.TryGetProperty("names", out var names))
            {
                foreach (var pair in names.EnumerateArray())
                {
                    var parts = pair.EnumerateArray().ToArray();
                    if (parts.Length == 2 && parts[0].GetString() is { } name)
                    {
                        enumValues.Add(new UnrealSdkEnumValue(name, parts[1].GetInt64()));
                    }
                }
            }

            types.Add(new UnrealSdkType(
                objectPath,
                LeafName(objectPath),
                kind.Value,
                OptionalString(value, "super_struct"),
                kind == UnrealSdkTypeKind.Enum ? [] : ReadProperties(value),
                functions.OrderBy(function => function.Path, StringComparer.Ordinal).ToArray(),
                enumValues,
                OptionalInt(value, "properties_size"),
                OptionalInt(value, "min_alignment"),
                OptionalString(value, "struct_flags") ?? string.Empty));
        }

        var metadata = root.TryGetProperty("metadata", out var metadataElement) ? metadataElement : default;
        var engineVersion = metadataElement.ValueKind == JsonValueKind.Object
            && metadataElement.TryGetProperty("engine_version", out var engine)
            ? engine
            : default;
        return new UnrealSdkModel(
            new UnrealSdkMetadata(
                Path.GetFileName(fullPath),
                hash,
                OptionalInt(engineVersion, "major"),
                OptionalInt(engineVersion, "minor"),
                OptionalString(metadataElement, "timestamp")),
            types);
    }

    private static IReadOnlyList<UnrealSdkProperty> ReadProperties(JsonElement owner)
    {
        if (!owner.TryGetProperty("properties", out var properties))
        {
            return [];
        }

        return properties.EnumerateArray()
            .Select(property => new UnrealSdkProperty(
                RequiredString(property, "name"),
                ReadType(property),
                property.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
                property.TryGetProperty("array_dim", out var arrayDimension) ? arrayDimension.GetInt32() : 1,
                OptionalString(property, "flags") ?? string.Empty,
                OptionalInt(property, "size"),
                OptionalInt(property, "byte_offset"),
                OptionalInt(property, "byte_mask"),
                OptionalInt(property, "field_mask")))
            .ToArray();
    }

    private static UnrealSdkTypeReference ReadType(JsonElement property)
    {
        var kind = RequiredString(property, "type");
        UnrealSdkTypeReference reference = kind switch
        {
            "StructProperty" => new(kind, OptionalString(property, "struct")),
            "ObjectProperty" or "ClassProperty" or "SoftObjectProperty" or "SoftClassProperty"
                or "WeakObjectProperty" or "LazyObjectProperty" => new(kind, OptionalString(property, "property_class")),
            "InterfaceProperty" => new(kind, OptionalString(property, "interface_class")),
            "EnumProperty" => new(kind, OptionalString(property, "enum"),
                property.TryGetProperty("container", out var enumContainer) ? ReadType(enumContainer) : null),
            "ByteProperty" => new(kind, OptionalString(property, "enum")),
            "ArrayProperty" or "OptionalProperty" => new(kind, Inner: ReadType(property.GetProperty("inner"))),
            "SetProperty" => new(kind, Inner: ReadType(property.GetProperty("key_prop"))),
            "MapProperty" => new(kind,
                Key: ReadType(property.GetProperty("key_prop")),
                Value: ReadType(property.GetProperty("value_prop"))),
            _ => new(kind)
        };
        return reference with
        {
            Size = OptionalInt(property, "size"),
            ByteOffset = OptionalInt(property, "byte_offset"),
            ByteMask = OptionalInt(property, "byte_mask"),
            FieldMask = OptionalInt(property, "field_mask")
        };
    }

    private static string LeafName(string path)
    {
        var separator = path.LastIndexOfAny(['.', ':']);
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"jmap field '{name}' is null.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int OptionalInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result)
            ? result
            : 0;
}
