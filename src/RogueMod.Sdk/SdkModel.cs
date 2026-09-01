namespace RogueMod.Sdk;

/// <summary>The complete reflection model imported from one UE4SS JMAP dump.</summary>
/// <param name="Metadata">Source provenance and engine version metadata.</param>
/// <param name="Types">Every reflected class, struct, and enum in the dump.</param>
public sealed record UnrealSdkModel(
    UnrealSdkMetadata Metadata,
    IReadOnlyList<UnrealSdkType> Types);

/// <summary>Provenance metadata of the JMAP dump an SDK was generated from.</summary>
/// <param name="SourceFile">File name of the source dump.</param>
/// <param name="Sha256">SHA-256 hash of the dump contents.</param>
/// <param name="EngineMajor">Unreal Engine major version reported by the dump.</param>
/// <param name="EngineMinor">Unreal Engine minor version reported by the dump.</param>
/// <param name="Timestamp">Optional dump creation timestamp.</param>
public sealed record UnrealSdkMetadata(
    string SourceFile,
    string Sha256,
    int EngineMajor,
    int EngineMinor,
    string? Timestamp);

/// <summary>One reflected Unreal type: a class, script struct, or enum.</summary>
/// <param name="Path">Full Unreal object path, for example <c>/Script/Engine.Actor</c>.</param>
/// <param name="Name">Reflected short name.</param>
/// <param name="Kind">The reflected kind.</param>
/// <param name="SuperPath">Full path of the reflected base type, or null.</param>
/// <param name="Properties">Reflected properties.</param>
/// <param name="Functions">Reflected UFunctions; classes only.</param>
/// <param name="EnumValues">Enumerator values; enums only.</param>
/// <param name="Size">Declared native size in bytes; zero when unknown.</param>
/// <param name="Alignment">Declared native alignment in bytes; zero when unknown.</param>
/// <param name="Flags">Reflected type flags as a pipe-separated string.</param>
public sealed record UnrealSdkType(
    string Path,
    string Name,
    UnrealSdkTypeKind Kind,
    string? SuperPath,
    IReadOnlyList<UnrealSdkProperty> Properties,
    IReadOnlyList<UnrealSdkFunction> Functions,
    IReadOnlyList<UnrealSdkEnumValue> EnumValues,
    int Size = 0,
    int Alignment = 0,
    string Flags = "");

/// <summary>The kind of a reflected Unreal type.</summary>
public enum UnrealSdkTypeKind
{
    /// <summary>A reflected UClass.</summary>
    Class,

    /// <summary>A reflected UScriptStruct.</summary>
    Struct,

    /// <summary>A reflected UEnum.</summary>
    Enum
}

/// <summary>One reflected UFunction.</summary>
/// <param name="Path">Full Unreal function path.</param>
/// <param name="Name">Reflected short name.</param>
/// <param name="Flags">Unreal function flags as a pipe-separated string.</param>
/// <param name="Parameters">Reflected parameters in declaration order.</param>
public sealed record UnrealSdkFunction(
    string Path,
    string Name,
    string Flags,
    IReadOnlyList<UnrealSdkProperty> Parameters);

/// <summary>One reflected property or UFunction parameter.</summary>
/// <param name="Name">Reflected property name.</param>
/// <param name="Type">The property type reference.</param>
/// <param name="Offset">Offset inside the owner layout.</param>
/// <param name="ArrayDimension">Fixed-array dimension; one for ordinary properties.</param>
/// <param name="Flags">Reflected property flags as a pipe-separated string.</param>
/// <param name="Size">Declared size in bytes; zero when unknown.</param>
/// <param name="ByteOffset">Byte offset for boolean properties.</param>
/// <param name="ByteMask">Byte mask for boolean properties.</param>
/// <param name="FieldMask">Field mask for boolean properties.</param>
public sealed record UnrealSdkProperty(
    string Name,
    UnrealSdkTypeReference Type,
    int Offset,
    int ArrayDimension,
    string Flags,
    int Size = 0,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0);

/// <summary>A referenced Unreal property type with optional container nesting.</summary>
/// <param name="Kind">Unreal property kind, for example <c>StructProperty</c> or <c>ArrayProperty</c>.</param>
/// <param name="TypePath">Full path of the referenced type, when the kind carries one.</param>
/// <param name="Inner">Element type for array-like kinds.</param>
/// <param name="Key">Key type for map kinds.</param>
/// <param name="Value">Value type for map kinds.</param>
/// <param name="Size">Declared size in bytes.</param>
/// <param name="ByteOffset">Byte offset for boolean types.</param>
/// <param name="ByteMask">Byte mask for boolean types.</param>
/// <param name="FieldMask">Field mask for boolean types.</param>
public sealed record UnrealSdkTypeReference(
    string Kind,
    string? TypePath = null,
    UnrealSdkTypeReference? Inner = null,
    UnrealSdkTypeReference? Key = null,
    UnrealSdkTypeReference? Value = null,
    int Size = 0,
    int ByteOffset = 0,
    int ByteMask = 0,
    int FieldMask = 0);

/// <summary>One reflected enum enumerator.</summary>
/// <param name="Name">Full enumerator name, for example <c>EDamageType::NewType0</c>.</param>
/// <param name="Value">The enumerator's numeric value.</param>
public sealed record UnrealSdkEnumValue(string Name, long Value);
