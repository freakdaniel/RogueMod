using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RogueMod.Sdk;

public sealed class CSharpSdkGenerator
{
    private const int MaximumArrayNestingDepth = 3;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while", "record", "required", "file", "scoped"
    };

    public CSharpSdkGenerationResult Generate(
        UnrealSdkModel model,
        string outputDirectory,
        string rootNamespace,
        string? abstractionsProjectPath = null) =>
        Generate(model, outputDirectory, rootNamespace, abstractionsProjectPath, CSharpSdkPackageMetadata.Default);

    public CSharpSdkGenerationResult Generate(
        UnrealSdkModel model,
        string outputDirectory,
        string rootNamespace,
        string? abstractionsProjectPath,
        CSharpSdkPackageMetadata packageMetadata)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(packageMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageMetadata.PackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageMetadata.PackageVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageMetadata.RogueModVersion);

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var typeNames = BuildTypeNames(model.Types);
        var source = GenerateSource(model, rootNamespace, typeNames);
        var sourcePath = Path.Combine(output, "RogueMod.GameSdk.g.cs");
        File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var manifestPath = Path.Combine(output, "RogueMod.GameSdk.json");
        var manifest = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            rootNamespace,
            source = model.Metadata,
            package = new
            {
                id = packageMetadata.PackageId,
                version = packageMetadata.PackageVersion,
                gameVersion = packageMetadata.GameVersion,
                rogueModVersion = packageMetadata.RogueModVersion
            },
            typeCount = model.Types.Count,
            classCount = model.Types.Count(type => type.Kind == UnrealSdkTypeKind.Class),
            structCount = model.Types.Count(type => type.Kind == UnrealSdkTypeKind.Struct),
            enumCount = model.Types.Count(type => type.Kind == UnrealSdkTypeKind.Enum)
        };
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var projectPath = Path.Combine(output, "DeadzoneRogue.Sdk.csproj");
        File.WriteAllText(
            projectPath,
            GenerateProject(output, rootNamespace, abstractionsProjectPath, packageMetadata),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (string.IsNullOrWhiteSpace(abstractionsProjectPath))
        {
            File.WriteAllText(
                Path.Combine(output, "Directory.Packages.props"),
                GenerateCentralPackageVersions(packageMetadata),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        File.WriteAllText(
            Path.Combine(output, "README.md"),
            GenerateReadme(packageMetadata),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new CSharpSdkGenerationResult(sourcePath, manifestPath, projectPath, model.Types.Count);
    }

    private static string GenerateProject(
        string outputDirectory,
        string rootNamespace,
        string? abstractionsProjectPath,
        CSharpSdkPackageMetadata packageMetadata)
    {
        string projectReference;
        if (!string.IsNullOrWhiteSpace(abstractionsProjectPath))
        {
            var fullPath = Path.GetFullPath(abstractionsProjectPath);
            var relativePath = Path.GetRelativePath(outputDirectory, fullPath).Replace('\\', '/');
            projectReference = $"""

              <ItemGroup>
                <ProjectReference Include="{EscapeXml(relativePath)}" PrivateAssets="none" />
              </ItemGroup>
            """;
        }
        else
        {
            projectReference = $"""

              <ItemGroup>
                <PackageReference Include="RogueMod.Abstractions" />
              </ItemGroup>
            """;
        }

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <Deterministic>true</Deterministic>
                <AssemblyName>DeadzoneRogue.Sdk</AssemblyName>
                <RootNamespace>{EscapeXml(NormalizeNamespace(rootNamespace))}</RootNamespace>
                <PackageId>{EscapeXml(packageMetadata.PackageId)}</PackageId>
                <Version>{EscapeXml(packageMetadata.PackageVersion)}</Version>
                <Title>Deadzone: Rogue typed SDK</Title>
                <Authors>RogueMod contributors</Authors>
                <Description>Strongly typed Deadzone: Rogue Unreal SDK generated and maintained by RogueMod.</Description>
                <PackageTags>deadzone-rogue;modding;ue4ss;unreal-engine;sdk</PackageTags>
                <PackageProjectUrl>https://github.com/freakdaniel/RogueMod</PackageProjectUrl>
                <RepositoryUrl>https://github.com/freakdaniel/RogueMod.git</RepositoryUrl>
                <RepositoryType>git</RepositoryType>
                <PackageReadmeFile>README.md</PackageReadmeFile>
                <IncludeSymbols>true</IncludeSymbols>
                <SymbolPackageFormat>snupkg</SymbolPackageFormat>
                <IncludeBuildOutput>true</IncludeBuildOutput>
              </PropertyGroup>{projectReference}
              <ItemGroup>
                <None Include="RogueMod.GameSdk.json" Pack="true" PackagePath="sdk/" />
                <None Include="README.md" Pack="true" PackagePath="\" />
              </ItemGroup>
            </Project>
            """ + Environment.NewLine;
    }

    private static string GenerateCentralPackageVersions(CSharpSdkPackageMetadata packageMetadata) => $"""
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="RogueMod.Abstractions" Version="{EscapeXml(packageMetadata.RogueModVersion)}" />
          </ItemGroup>
        </Project>
        """ + Environment.NewLine;

    private static string GenerateReadme(CSharpSdkPackageMetadata packageMetadata)
    {
        var gameCompatibility = string.IsNullOrWhiteSpace(packageMetadata.GameVersion)
            ? "the Deadzone: Rogue build recorded in `sdk/RogueMod.GameSdk.json`"
            : $"Deadzone: Rogue {packageMetadata.GameVersion}";
        return $$"""
            # {{packageMetadata.PackageId}}

            Strongly typed Unreal wrappers for {{gameCompatibility}}. The package is generated and published by RogueMod maintainers from a verified reflection snapshot.

            Mod authors do not need UE4SS development tools, JMAP files, dump hotkeys, or a local game installation. Add this package alongside `RogueMod.Sdk`:

            ```xml
            <PackageReference Include="RogueMod.Sdk" Version="{{packageMetadata.RogueModVersion}}" />
            <PackageReference Include="{{packageMetadata.PackageId}}" Version="{{packageMetadata.PackageVersion}}" />
            ```

            `PackageRogueMod` does not copy this assembly into each mod. The matching SDK is installed once in RogueMod runtime's shared assembly directory and resolved for every managed mod.

            Use the generated wrappers through `IModContext.Unreal`, for example `context.Unreal.FindFirst<T>()`.

            The source manifest, engine version, source hash, and compatibility metadata are included at `sdk/RogueMod.GameSdk.json`.
            """ + Environment.NewLine;
    }

    private static string GenerateSource(
        UnrealSdkModel model,
        string rootNamespace,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var builder = new StringBuilder();
        var supportedStructPaths = BuildSupportedStructPaths(model.Types);
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine($"// Source: {model.Metadata.SourceFile}; SHA-256: {model.Metadata.Sha256}");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS0108, CS0114 // Reflected Unreal members intentionally hide base wrappers.");
        builder.AppendLine("using RogueMod.Abstractions;");
        builder.AppendLine();
        builder.Append("namespace ").Append(NormalizeNamespace(rootNamespace)).AppendLine(";");
        builder.AppendLine();

        foreach (var type in model.Types.OrderBy(type => type.Path, StringComparer.Ordinal))
        {
            switch (type.Kind)
            {
                case UnrealSdkTypeKind.Enum:
                    WriteEnum(builder, type, typeNames[type.Path]);
                    break;
                case UnrealSdkTypeKind.Struct:
                    WriteStruct(builder, type, typeNames[type.Path], typeNames, supportedStructPaths);
                    break;
                case UnrealSdkTypeKind.Class:
                    WriteClass(builder, type, typeNames[type.Path], typeNames, supportedStructPaths);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type.Kind), type.Kind, null);
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void WriteEnum(StringBuilder builder, UnrealSdkType type, string typeName)
    {
        builder.Append("public enum ").Append(typeName).AppendLine(" : long");
        builder.AppendLine("{");
        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };
        foreach (var value in type.EnumValues)
        {
            var name = UniqueIdentifier(LeafName(value.Name), used);
            builder.Append("    ").Append(name).Append(" = ").Append(value.Value).AppendLine(",");
        }
        builder.AppendLine("}");
    }

    private static void WriteStruct(
        StringBuilder builder,
        UnrealSdkType type,
        string typeName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("public readonly record struct ").Append(typeName).AppendLine();
        builder.AppendLine("{");
        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };
        foreach (var property in type.Properties.Where(property => !HasFlag(property.Flags, "CPF_Parm")))
        {
            var name = UniqueIdentifier(property.Name, used);
            var propertyType = ResolveType(property.Type, property.ArrayDimension, typeNames, supportedStructPaths);
            builder.Append("    public ").Append(propertyType.Name).Append(' ').Append(name).AppendLine(" { get; init; }");
        }
        if (supportedStructPaths.Contains(type.Path))
        {
            WriteStructAdapter(builder, type, typeName, typeNames, supportedStructPaths);
        }
        builder.AppendLine("}");
    }

    private static void WriteClass(
        StringBuilder builder,
        UnrealSdkType type,
        string typeName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        string? generatedBase = null;
        var hasGeneratedBase = type.SuperPath is not null && typeNames.TryGetValue(type.SuperPath, out generatedBase);
        var baseName = hasGeneratedBase ? generatedBase : "UnrealObject";
        var hidingModifier = hasGeneratedBase ? "new " : string.Empty;
        builder.Append("public class ").Append(typeName).Append(" : ").Append(baseName)
            .Append(", IUnrealObjectType<").Append(typeName).AppendLine(">");
        builder.AppendLine("{");
        builder.Append("    public ").Append(hidingModifier).Append("const string UnrealPath = ").Append(Literal(type.Path)).AppendLine(";");
        builder.Append("    public ").Append(hidingModifier).Append("const string UnrealName = ").Append(Literal(type.Name)).AppendLine(";");
        builder.AppendLine();
        builder.Append("    public ").Append(typeName)
            .Append("(IUnrealReflection unreal, UnrealObjectHandle handle) : base(unreal, handle) { }").AppendLine();
        builder.AppendLine();
        builder.Append("    static string IUnrealObjectType<").Append(typeName).AppendLine(">.UnrealClassName => UnrealName;");
        builder.Append("    static ").Append(typeName).Append(" IUnrealObjectType<").Append(typeName)
            .Append(">.Create(IUnrealReflection unreal, UnrealObjectHandle handle) => new(unreal, handle);").AppendLine();
        builder.AppendLine();
        builder.Append("    public ").Append(hidingModifier).Append("static ").Append(typeName).AppendLine("? FindFirst(IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.Append("        return unreal.FindFirst<").Append(typeName).AppendLine(">();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.Append("    public ").Append(hidingModifier).Append("static IReadOnlyList<").Append(typeName)
            .AppendLine("> FindAll(IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.Append("        return unreal.FindAll<").Append(typeName).AppendLine(">();");
        builder.AppendLine("    }");

        var usedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            typeName, "UnrealPath", "UnrealName", "FindFirst", "FindAll", "Handle", "Unreal", "IsValid", "PathName"
        };
        foreach (var property in type.Properties.Where(property => !HasFlag(property.Flags, "CPF_Parm")))
        {
            builder.AppendLine();
            WriteProperty(builder, type, property, UniqueIdentifier(property.Name, usedMembers), typeNames, supportedStructPaths);
        }

        foreach (var function in type.Functions)
        {
            builder.AppendLine();
            WriteFunction(builder, type, function, UniqueIdentifier(function.Name, usedMembers), typeNames, supportedStructPaths);
        }
        builder.AppendLine("}");
    }

    private static void WriteProperty(
        StringBuilder builder,
        UnrealSdkType owner,
        UnrealSdkProperty property,
        string memberName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        var descriptorName = DescriptorIdentifier(memberName);
        var type = ResolveType(property.Type, property.ArrayDimension, typeNames, supportedStructPaths);
        builder.Append("    private static readonly UnrealPropertyDescriptor ").Append(descriptorName).Append(" = new(")
            .Append(Literal(owner.Path)).Append(", ")
            .Append(Literal(property.Name)).Append(", ")
            .Append(Literal(DescribeType(property.Type))).Append(", ")
            .Append(property.Offset).Append(", ")
            .Append(property.ArrayDimension).Append(", ")
            .Append(Literal(property.Flags)).Append(", ")
            .Append(property.Size).Append(", ")
            .Append(property.ByteOffset).Append(", ")
            .Append(property.ByteMask).Append(", ")
            .Append(property.FieldMask);
        AppendValueDescriptors(builder, property.Type, typeNames, supportedStructPaths);
        builder.Append(')');
        AppendValueDescriptorInitializer(builder, property.Type, typeNames, supportedStructPaths);
        builder.AppendLine(";");
        builder.Append("    public ").Append(type.Name).Append(' ').Append(memberName).AppendLine();
        builder.AppendLine("    {");
        if (type.ObjectWrapper)
        {
            builder.Append("        get => ReadObject(").Append(descriptorName).Append(", static (unreal, handle) => new ")
                .Append(type.NonNullableName).AppendLine("(unreal, handle));");
        }
        else if (type.StructAdapter)
        {
            builder.Append("        get => ").Append(type.Name).Append(".FromUnrealValue(ReadValue(")
                .Append(descriptorName).AppendLine("));");
        }
        else if (type.ArrayAdapter || type.OptionalAdapter || type.LazyObjectAdapter)
        {
            builder.Append("        get => ").Append(ReadValueExpression(
                type,
                $"ReadValue({descriptorName})",
                type.LazyObjectAdapter ? null : ValueDescriptorExpression(type, descriptorName))).AppendLine(";");
        }
        else
        {
            builder.Append("        get => Read<").Append(type.Name).Append(">(").Append(descriptorName).AppendLine(");");
        }

        if (CanWrite(property.Flags))
        {
            if (type.StructAdapter)
            {
                builder.Append("        set => WriteValue(").Append(descriptorName).AppendLine(", value.ToUnrealValue());");
            }
            else if (type.ArrayAdapter || type.OptionalAdapter || type.LazyObjectAdapter)
            {
                builder.Append("        set => WriteValue(").Append(descriptorName).Append(", ")
                    .Append(type.LazyObjectAdapter
                        ? "value.ToUnrealValue()"
                        : WriteValueExpression(type, "value", ValueDescriptorExpression(type, descriptorName)))
                    .AppendLine(");");
            }
            else
            {
                builder.Append(type.ObjectWrapper ? "        set => WriteObject(" : "        set => Write(")
                    .Append(descriptorName).AppendLine(", value);");
            }
        }
        builder.AppendLine("    }");
    }

    private static void WriteFunction(
        StringBuilder builder,
        UnrealSdkType owner,
        UnrealSdkFunction function,
        string methodName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        var descriptorName = DescriptorIdentifier(methodName);
        builder.Append("    private static readonly UnrealFunctionDescriptor ").Append(descriptorName).Append(" = new(")
            .Append(Literal(owner.Path)).Append(", ")
            .Append(Literal(function.Path)).Append(", ")
            .Append(Literal(function.Name)).Append(", ")
            .Append(Literal(function.Flags));
        if (function.Parameters.Count == 0)
        {
            builder.AppendLine(");");
        }
        else
        {
            builder.AppendLine(", [");
            foreach (var parameter in function.Parameters)
            {
                builder.Append("        new(")
                    .Append(Literal(parameter.Name)).Append(", ")
                    .Append(Literal(DescribeType(parameter.Type))).Append(", ")
                    .Append(parameter.Offset).Append(", ")
                    .Append(parameter.ArrayDimension).Append(", ")
                    .Append(Literal(parameter.Flags)).Append(", ")
                    .Append(parameter.Size).Append(", ")
                    .Append(parameter.ByteOffset).Append(", ")
                    .Append(parameter.ByteMask).Append(", ")
                    .Append(parameter.FieldMask);
                AppendValueDescriptors(builder, parameter.Type, typeNames, supportedStructPaths);
                builder.Append(')');
                AppendValueDescriptorInitializer(builder, parameter.Type, typeNames, supportedStructPaths);
                builder.AppendLine(",");
            }
            builder.AppendLine("    ]);");
        }

        var returnParameter = function.Parameters.FirstOrDefault(parameter => HasFlag(parameter.Flags, "CPF_ReturnParm"));
        var outputs = function.Parameters
            .Where(parameter => !HasFlag(parameter.Flags, "CPF_ReturnParm") && HasFlag(parameter.Flags, "CPF_OutParm"))
            .ToArray();
        var inputs = function.Parameters
            .Where(parameter => !HasFlag(parameter.Flags, "CPF_ReturnParm")
                && (!HasFlag(parameter.Flags, "CPF_OutParm") || HasFlag(parameter.Flags, "CPF_ReferenceParm")))
            .ToArray();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal) { "result" };
        var inputNames = inputs.ToDictionary(
            parameter => parameter,
            parameter => UniqueIdentifier(ToCamelCase(parameter.Name), parameterNames));

        var directReturnType = returnParameter is null
            ? new CsType("void", false, "void")
            : ResolveType(returnParameter.Type, returnParameter.ArrayDimension, typeNames, supportedStructPaths);
        var resultName = methodName + "InvocationResult";
        if (outputs.Length > 0)
        {
            builder.Append("    public readonly record struct ").Append(resultName).Append('(');
            var resultParts = new List<string>();
            if (returnParameter is not null)
            {
                resultParts.Add($"{directReturnType.Name} ReturnValue");
            }
            var outputNames = new HashSet<string>(StringComparer.Ordinal) { "ReturnValue" };
            resultParts.AddRange(outputs.Select(output =>
                $"{ResolveType(output.Type, output.ArrayDimension, typeNames, supportedStructPaths).Name} {UniqueIdentifier(output.Name, outputNames)}"));
            builder.Append(string.Join(", ", resultParts)).AppendLine(");");
        }

        var methodReturnType = outputs.Length > 0 ? resultName : directReturnType.Name;
        builder.Append("    public ").Append(methodReturnType).Append(' ').Append(methodName).Append('(');
        builder.Append(string.Join(", ", inputs.Select(input =>
            $"{ResolveType(input.Type, input.ArrayDimension, typeNames, supportedStructPaths).Name} {inputNames[input]}")));
        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.Append("        var result = Call(").Append(descriptorName);
        foreach (var input in inputs)
        {
            var type = ResolveType(input.Type, input.ArrayDimension, typeNames, supportedStructPaths);
            var value = inputNames[input];
            var expression = type.ObjectWrapper
                ? $"UnrealValue.From({value}?.Handle ?? UnrealObjectHandle.Null)"
                : type.StructAdapter ? $"{value}.ToUnrealValue()"
                : type.LazyObjectAdapter ? $"{value}.ToUnrealValue()"
                : type.ArrayAdapter || type.OptionalAdapter
                    ? WriteValueExpression(
                        type,
                        value,
                        ValueDescriptorExpression(
                            type,
                            $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, input)}]"))
                    : $"UnrealValue.From({value})";
            builder.AppendLine(",");
            builder.Append("            new UnrealArgument(").Append(Literal(input.Name)).Append(", ").Append(expression).Append(')');
        }
        builder.AppendLine(");");

        if (outputs.Length > 0)
        {
            var values = new List<string>();
            if (returnParameter is not null)
            {
                values.Add(ReadValueExpression(
                    directReturnType,
                    "result.ReturnValue",
                    ValueDescriptorExpressionOrNull(
                        directReturnType,
                        $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, returnParameter)}]")));
            }
            values.AddRange(outputs.Select(output =>
            {
                var type = ResolveType(output.Type, output.ArrayDimension, typeNames, supportedStructPaths);
                return ReadValueExpression(
                    type,
                    $"result.OutArguments[{Literal(output.Name)}]",
                    ValueDescriptorExpressionOrNull(
                        type,
                        $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, output)}]"));
            }));
            builder.Append("        return new ").Append(resultName).Append('(').Append(string.Join(", ", values)).AppendLine(");");
        }
        else if (returnParameter is not null)
        {
            builder.Append("        return ").Append(ReadValueExpression(
                directReturnType,
                "result.ReturnValue",
                ValueDescriptorExpressionOrNull(
                    directReturnType,
                    $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, returnParameter)}]"))).AppendLine(";");
        }
        builder.AppendLine("    }");
    }

    private static string ReadValueExpression(
        CsType type,
        string valueExpression,
        string? descriptorExpression = null,
        int containerDepth = 0) =>
        type.ObjectWrapper
            ? $"WrapObject({valueExpression}, static (unreal, handle) => new {type.NonNullableName}(unreal, handle))"
            : type.LazyObjectAdapter
                ? $"UnrealLazyObjectReference<{type.NonNullableName}>.FromUnrealValue({valueExpression}, handle => new {type.NonNullableName}(Unreal, handle))"
            : type.StructAdapter
                ? $"{type.Name}.FromUnrealValue({valueExpression})"
                : type.ArrayAdapter
                    ? $"UnrealArrayValue.ToList<{type.Element!.Name}>({valueExpression}, element{containerDepth} => {ReadValueExpression(type.Element, $"element{containerDepth}", null, containerDepth + 1)})"
                    : type.OptionalAdapter
                        ? $"UnrealOptional<{type.Element!.Name}>.FromUnrealValue({valueExpression}, optional{containerDepth} => {ReadValueExpression(type.Element, $"optional{containerDepth}", null, containerDepth + 1)})"
                        : $"{valueExpression}.As<{type.Name}>()";

    private static string WriteValueExpression(CsType type, string valueExpression, string descriptorExpression)
    {
        if (type.Element is null)
        {
            throw new InvalidOperationException($"C# type '{type.Name}' is not an Unreal container adapter.");
        }
        return type.ArrayAdapter
            ? WriteArrayValueExpression(type, valueExpression, descriptorExpression, 0)
            : type.OptionalAdapter
                ? WriteOptionalValueExpression(type, valueExpression, descriptorExpression, 0)
                : throw new InvalidOperationException($"C# type '{type.Name}' is not an Unreal container adapter.");
    }

    private static string WriteArrayValueExpression(
        CsType type,
        string valueExpression,
        string arrayDescriptorExpression,
        int arrayDepth)
    {
        var element = type.Element
            ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal array element type.");
        var elementName = $"element{arrayDepth}";
        var encoded = element.ObjectWrapper
            ? $"UnrealValue.From({elementName}?.Handle ?? UnrealObjectHandle.Null)"
            : element.StructAdapter
                ? $"{elementName}.ToUnrealValue()"
                : element.ArrayAdapter
                    ? WriteArrayValueExpression(
                        element,
                        elementName,
                        $"{arrayDescriptorExpression}.ElementArray!",
                        arrayDepth + 1)
                    : $"UnrealValue.From({elementName})";
        return $"UnrealArrayValue.From({arrayDescriptorExpression}, {valueExpression}, {elementName} => {encoded})";
    }

    private static string WriteOptionalValueExpression(
        CsType type,
        string valueExpression,
        string optionalDescriptorExpression,
        int containerDepth)
    {
        var element = type.Element
            ?? throw new InvalidOperationException($"C# type '{type.Name}' has no Unreal optional value type.");
        var elementName = $"optional{containerDepth}";
        var encoded = element.ObjectWrapper
            ? $"UnrealValue.From({elementName}?.Handle ?? UnrealObjectHandle.Null)"
            : element.StructAdapter
                ? $"{elementName}.ToUnrealValue()"
                : $"UnrealValue.From({elementName})";
        return $"{valueExpression}.ToUnrealValue({optionalDescriptorExpression}, {elementName} => {encoded})";
    }

    private static string ValueDescriptorExpression(CsType type, string descriptorOwnerExpression) =>
        type.ArrayAdapter
            ? $"{descriptorOwnerExpression}.Array!"
            : type.OptionalAdapter
                ? $"{descriptorOwnerExpression}.Optional!"
                : throw new InvalidOperationException($"C# type '{type.Name}' has no container descriptor.");

    private static string? ValueDescriptorExpressionOrNull(CsType type, string descriptorOwnerExpression) =>
        type.ArrayAdapter || type.OptionalAdapter
            ? ValueDescriptorExpression(type, descriptorOwnerExpression)
            : null;

    private static CsType ResolveType(
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
            "ObjectProperty" or "ClassProperty" or "InterfaceProperty" or "SoftObjectProperty"
                or "SoftClassProperty" or "WeakObjectProperty" when type.TypePath is not null
                && typeNames.TryGetValue(type.TypePath, out var objectName) => new CsType(objectName + "?", true, objectName),
            "ObjectProperty" or "ClassProperty" or "InterfaceProperty" or "SoftObjectProperty"
                or "SoftClassProperty" or "WeakObjectProperty" => new CsType("UnrealObject?", true, "UnrealObject"),
            "LazyObjectProperty" when type.TypePath is not null
                && typeNames.TryGetValue(type.TypePath, out var lazyObjectName) => LazyObject(lazyObjectName),
            "LazyObjectProperty" => LazyObject("UnrealObject"),
            "ArrayProperty" when type.Inner is not null
                && IsSupportedArrayElement(type.Inner, supportedStructPaths) =>
                Array(ResolveType(type.Inner, 1, typeNames, supportedStructPaths)),
            "SetProperty" when type.Inner is not null => Container("IReadOnlySet", ResolveType(type.Inner, 1, typeNames, supportedStructPaths)),
            "MapProperty" when type.Key is not null && type.Value is not null =>
                new CsType($"IReadOnlyDictionary<{ResolveType(type.Key, 1, typeNames, supportedStructPaths).Name}, {ResolveType(type.Value, 1, typeNames, supportedStructPaths).Name}>", false, string.Empty),
            "OptionalProperty" when type.Inner is not null
                && IsSupportedOptionalValue(type.Inner, supportedStructPaths) =>
                Optional(ResolveType(type.Inner, 1, typeNames, supportedStructPaths)),
            _ => Simple("UnrealValue")
        };
        return arrayDimension > 1 ? Container("IReadOnlyList", result) : result;
    }

    private static CsType Simple(string name) => new(name, false, name);

    private static CsType Struct(string name) => new(name, false, name, true);

    private static CsType Array(CsType inner) =>
        new($"IReadOnlyList<{inner.Name}>", false, string.Empty, ArrayAdapter: true, Element: inner);

    private static CsType Container(string name, CsType inner) =>
        new($"{name}<{inner.Name}>", false, string.Empty);

    private static CsType Optional(CsType inner) =>
        new($"UnrealOptional<{inner.Name}>", false, string.Empty, OptionalAdapter: true, Element: inner);

    private static CsType LazyObject(string targetName) =>
        new($"UnrealLazyObjectReference<{targetName}>", false, targetName, LazyObjectAdapter: true);

    private static void WriteStructAdapter(
        StringBuilder builder,
        UnrealSdkType type,
        string typeName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        var fields = type.Properties.Where(property => !HasFlag(property.Flags, "CPF_Parm")).ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };
        var fieldNames = fields.ToDictionary(field => field, field => UniqueIdentifier(field.Name, used));

        builder.AppendLine();
        builder.AppendLine("    public static UnrealStructDescriptor Descriptor { get; } = new(");
        builder.Append("        ").Append(Literal(type.Path)).Append(", ").Append(type.Size).Append(", ").Append(type.Alignment).AppendLine(", [");
        foreach (var field in fields)
        {
            builder.Append("        new(")
                .Append(Literal(field.Name)).Append(", ")
                .Append(Literal(DescribeType(field.Type))).Append(", ")
                .Append(field.Offset).Append(", ")
                .Append(field.Size).Append(", ")
                .Append(field.ArrayDimension).Append(", ")
                .Append(field.ByteOffset).Append(", ")
                .Append(field.ByteMask).Append(", ")
                .Append(field.FieldMask);
            AppendValueDescriptors(builder, field.Type, typeNames, supportedStructPaths);
            builder.AppendLine("),");
        }
        builder.AppendLine("    ]);");

        builder.AppendLine();
        builder.AppendLine("    public UnrealValue ToUnrealValue() => UnrealValue.From(new UnrealStructValue(");
        builder.AppendLine("        Descriptor,");
        builder.AppendLine("        new Dictionary<string, UnrealValue>(StringComparer.Ordinal)");
        builder.AppendLine("        {");
        foreach (var field in fields)
        {
            var fieldType = ResolveType(field.Type, field.ArrayDimension, typeNames, supportedStructPaths);
            var expression = fieldType.StructAdapter
                ? $"{fieldNames[field]}.ToUnrealValue()"
                : $"UnrealValue.From({fieldNames[field]})";
            builder.Append("            [").Append(Literal(field.Name)).Append("] = ").Append(expression).AppendLine(",");
        }
        builder.AppendLine("        }));");

        builder.AppendLine();
        builder.Append("    public static ").Append(typeName).AppendLine(" FromUnrealValue(UnrealValue value)");
        builder.AppendLine("    {");
        builder.AppendLine("        var transported = value.As<UnrealStructValue>();");
        builder.AppendLine("        if (!StringComparer.Ordinal.Equals(transported.Descriptor.Path, Descriptor.Path))");
        builder.AppendLine("        {");
        builder.Append("            throw new InvalidCastException($\"Unreal struct '{transported.Descriptor.Path}' cannot be read as '")
            .Append(type.Path).AppendLine("'.\");");
        builder.AppendLine("        }");
        builder.Append("        return new ").Append(typeName).AppendLine();
        builder.AppendLine("        {");
        foreach (var field in fields)
        {
            var fieldType = ResolveType(field.Type, field.ArrayDimension, typeNames, supportedStructPaths);
            var transported = $"transported.GetField({Literal(field.Name)})";
            var expression = fieldType.StructAdapter
                ? $"{fieldType.Name}.FromUnrealValue({transported})"
                : $"{transported}.As<{fieldType.Name}>()";
            builder.Append("            ").Append(fieldNames[field]).Append(" = ").Append(expression).AppendLine(",");
        }
        builder.AppendLine("        };");
        builder.AppendLine("    }");
    }

    private static void AppendValueDescriptors(
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

    private static void AppendValueDescriptorInitializer(
        StringBuilder builder,
        UnrealSdkTypeReference type,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        if (type.Kind != "OptionalProperty"
            || type.Inner is null
            || !IsSupportedOptionalValue(type.Inner, supportedStructPaths))
        {
            return;
        }
        builder.Append(" { Optional = ");
        AppendOptionalDescriptor(builder, type.Inner, typeNames, supportedStructPaths);
        builder.Append(" }");
    }

    private static void AppendOptionalDescriptor(
        StringBuilder builder,
        UnrealSdkTypeReference value,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        builder.Append("new(")
            .Append(Literal(DescribeType(value))).Append(", ")
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
            .Append(Literal(DescribeType(element))).Append(", ")
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

    private static bool IsSupportedArrayElement(
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

    private static bool IsSupportedOptionalValue(
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

    private static int IndexOf(IReadOnlyList<UnrealSdkProperty> properties, UnrealSdkProperty property)
    {
        for (var index = 0; index < properties.Count; index++)
        {
            if (ReferenceEquals(properties[index], property))
            {
                return index;
            }
        }
        throw new InvalidOperationException($"Unreal parameter '{property.Name}' was not found in its function descriptor.");
    }

    private static IReadOnlySet<string> BuildSupportedStructPaths(IReadOnlyList<UnrealSdkType> types)
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

    private static IReadOnlyDictionary<string, string> BuildTypeNames(IReadOnlyList<UnrealSdkType> types)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(type => type.Path, StringComparer.Ordinal))
        {
            var preferred = type.Name.EndsWith("_C", StringComparison.Ordinal) ? type.Name[..^2] : type.Name;
            var identifier = Identifier(preferred);
            if (!used.Add(identifier))
            {
                identifier += "_" + ShortHash(type.Path);
                used.Add(identifier);
            }
            result.Add(type.Path, identifier);
        }
        return result;
    }

    private static string UniqueIdentifier(string value, ISet<string> used)
    {
        var identifier = Identifier(value);
        var candidate = identifier;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = identifier + suffix++;
        }
        return candidate;
    }

    private static string DescriptorIdentifier(string publicIdentifier) =>
        Identifier("__" + publicIdentifier.TrimStart('@'));

    private static string Identifier(string value)
    {
        var builder = new StringBuilder(value.Length + 1);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }
        if (builder.Length == 0 || char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }
        var result = builder.ToString();
        return Keywords.Contains(result) ? "@" + result : result;
    }

    private static string NormalizeNamespace(string value) =>
        string.Join('.', value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Identifier));

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string LeafName(string value)
    {
        var separator = value.LastIndexOfAny([':', '.']);
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private static bool HasFlag(string flags, string flag) =>
        flags.Split('|', StringSplitOptions.TrimEntries).Contains(flag, StringComparer.Ordinal);

    private static bool CanWrite(string flags) =>
        !HasFlag(flags, "CPF_BlueprintReadOnly")
        && !HasFlag(flags, "CPF_EditConst")
        && !HasFlag(flags, "CPF_ConstParm");

    private static string DescribeType(UnrealSdkTypeReference type) => type.TypePath is null
        ? type.Kind
        : $"{type.Kind}:{type.TypePath}";

    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private static string EscapeXml(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    private sealed record CsType(
        string Name,
        bool ObjectWrapper,
        string NonNullableName,
        bool StructAdapter = false,
        bool ArrayAdapter = false,
        bool OptionalAdapter = false,
        bool LazyObjectAdapter = false,
        CsType? Element = null);
}

public sealed record CSharpSdkGenerationResult(string SourcePath, string ManifestPath, string ProjectPath, int TypeCount);

public sealed record CSharpSdkPackageMetadata(
    string PackageId = "DeadzoneRogue.Sdk",
    string PackageVersion = "0.1.0",
    string RogueModVersion = "0.1.0",
    string? GameVersion = null)
{
    public static CSharpSdkPackageMetadata Default { get; } = new();
}
