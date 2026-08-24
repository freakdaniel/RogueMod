namespace RogueMod.Sdk;

public sealed record UnrealSdkModel(
    UnrealSdkMetadata Metadata,
    IReadOnlyList<UnrealSdkType> Types);

public sealed record UnrealSdkMetadata(
    string SourceFile,
    string Sha256,
    int EngineMajor,
    int EngineMinor,
    string? Timestamp);

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

public enum UnrealSdkTypeKind
{
    Class,
    Struct,
    Enum
}

public sealed record UnrealSdkFunction(
    string Path,
    string Name,
    string Flags,
    IReadOnlyList<UnrealSdkProperty> Parameters);

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

public sealed record UnrealSdkEnumValue(string Name, long Value);
