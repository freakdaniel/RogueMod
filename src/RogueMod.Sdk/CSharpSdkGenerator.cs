using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RogueMod.Sdk;

/// <summary>
/// Generates the strongly typed C# game SDK from an imported JMAP model: the wrapper source,
/// a source manifest with provenance, a packable project, and package metadata.
/// </summary>
public sealed class CSharpSdkGenerator
{

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

    /// <summary>Generates the SDK with the default package metadata.</summary>
    /// <param name="model">The imported reflection model.</param>
    /// <param name="outputDirectory">Directory receiving the generated files.</param>
    /// <param name="rootNamespace">Root namespace of the generated wrappers.</param>
    /// <param name="abstractionsProjectPath">Optional project reference replacing the package reference to <c>RogueMod.Abstractions</c>.</param>
    /// <returns>The generated file paths and type count.</returns>
    public CSharpSdkGenerationResult Generate(
        UnrealSdkModel model,
        string outputDirectory,
        string rootNamespace,
        string? abstractionsProjectPath = null) =>
        Generate(model, outputDirectory, rootNamespace, abstractionsProjectPath, CSharpSdkPackageMetadata.Default);

    /// <summary>Generates the SDK source, manifest, project, and README into the output directory.</summary>
    /// <param name="model">The imported reflection model.</param>
    /// <param name="outputDirectory">Directory receiving the generated files.</param>
    /// <param name="rootNamespace">Root namespace of the generated wrappers.</param>
    /// <param name="abstractionsProjectPath">Optional project reference replacing the package reference to <c>RogueMod.Abstractions</c>.</param>
    /// <param name="packageMetadata">Package identity and compatibility metadata.</param>
    /// <returns>The generated file paths and type count.</returns>
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

        // Generated SDKs must not inherit unrelated Directory.Build.props files from a
        // parent temp/workspace directory. The project contains all required settings.
        File.WriteAllText(
            Path.Combine(output, "Directory.Build.props"),
            "<Project />" + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
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
        var allStructs = model.Types
            .Where(t => t.Kind == UnrealSdkTypeKind.Struct)
            .ToDictionary(t => t.Path, StringComparer.Ordinal);
        var normalizedNamespace = NormalizeNamespace(rootNamespace);
        var referenceTypeNames = typeNames.ToDictionary(
            pair => pair.Key,
            pair => $"global::{normalizedNamespace}.{pair.Value}",
            StringComparer.Ordinal);
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine($"// Source: {model.Metadata.SourceFile}; SHA-256: {model.Metadata.Sha256}");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS0108, CS0114 // Reflected Unreal members intentionally hide base wrappers.");
        builder.AppendLine("using RogueMod.Abstractions;");
        builder.AppendLine();
        builder.Append("namespace ").Append(normalizedNamespace).AppendLine(";");
        builder.AppendLine();

        foreach (var type in model.Types.OrderBy(type => type.Path, StringComparer.Ordinal))
        {
            switch (type.Kind)
            {
                case UnrealSdkTypeKind.Enum:
                    WriteEnum(builder, type, typeNames[type.Path]);
                    break;
                case UnrealSdkTypeKind.Struct:
                    WriteStruct(builder, type, typeNames[type.Path], referenceTypeNames, supportedStructPaths, allStructs);
                    break;
                case UnrealSdkTypeKind.Class:
                    WriteClass(builder, type, typeNames[type.Path], referenceTypeNames, supportedStructPaths);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type.Kind), type.Kind, null);
            }
            builder.AppendLine();
        }

        WriteGameHelpers(builder, model.Types);

        return builder.ToString();
    }

    private static void WriteGameHelpers(StringBuilder builder, IReadOnlyList<UnrealSdkType> types)
    {
        const string masterPath = "/Game/Abilities/Devices/Gun/GA_Gun_Master.GA_Gun_Master_C";
        const string instantPath = "/Game/Abilities/Devices/Gun/GA_Gun_Master_Instant.GA_Gun_Master_Instant_C";
        string[] requiredPaths =
        [
            "/Script/Valhalla.ValCharacter",
            "/Script/Valhalla.ValAbilityFunctionLibrary",
            masterPath,
            instantPath,
        ];
        var typesByPath = types.ToDictionary(type => type.Path, StringComparer.Ordinal);
        if (requiredPaths.Any(path => !typesByPath.ContainsKey(path)))
        {
            return;
        }
        var masterClasses = types
            .Where(type => type.Kind == UnrealSdkTypeKind.Class && IsDerivedFrom(type, masterPath, typesByPath))
            .Select(type => type.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var instantClasses = types
            .Where(type => type.Kind == UnrealSdkTypeKind.Class && IsDerivedFrom(type, instantPath, typesByPath))
            .Select(type => type.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        builder.AppendLine("/// <summary>Deadzone: Rogue helpers that resolve equipment owned by one live character.</summary>");
        builder.AppendLine("public static class DeadzoneRogueEquipment");
        builder.AppendLine("{");
        WritePathSet(builder, "GunMasterClasses", masterClasses);
        WritePathSet(builder, "InstantGunClasses", instantClasses);
        builder.AppendLine();
        builder.AppendLine("    public readonly record struct EquippedGun(");
        builder.AppendLine("        GameplayTag Slot,");
        builder.AppendLine("        GA_Gun_Master Master,");
        builder.AppendLine("        GA_Gun_Master_Instant Instant);");
        builder.AppendLine();
        builder.AppendLine("    public readonly record struct ActiveGunResolution(");
        builder.AppendLine("        EquippedGun? Gun,");
        builder.AppendLine("        string Status,");
        builder.AppendLine("        string? AbilityClassPath);");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Resolves the character's active live gun ability and its linked instant-fire ability.</summary>");
        builder.AppendLine("    public static EquippedGun? GetActiveGun(");
        builder.AppendLine("        global::RogueMod.Abstractions.IUnrealReflection unreal,");
        builder.AppendLine("        ValCharacter character)");
        builder.AppendLine("        => ResolveActiveGun(unreal, character).Gun;");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>Resolves the active gun and reports the exact stage when it is not ready.</summary>");
        builder.AppendLine("    public static ActiveGunResolution ResolveActiveGun(");
        builder.AppendLine("        global::RogueMod.Abstractions.IUnrealReflection unreal,");
        builder.AppendLine("        ValCharacter character)");
        builder.AppendLine("    {");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(unreal);");
        builder.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(character);");
        builder.AppendLine("        if (!character.IsValid || character.AbilitySystemComponent is not { IsValid: true } abilitySystem)");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"ability-system-unavailable\", null);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var library = ValAbilityFunctionLibrary.FindDefaultObject(unreal);");
        builder.AppendLine("        if (library is null)");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"ability-library-unavailable\", null);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var handle = character.GetActiveEquippedAbility();");
        builder.AppendLine("        var ability = library.GetPrimaryAbilityInstanceFromHandle(abilitySystem, handle);");
        builder.AppendLine("        if (ability is not { IsValid: true })");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"active-ability-unavailable\", null);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var abilityClassPath = unreal.GetPathName(unreal.GetClass(ability.Handle));");
        builder.AppendLine("        GA_Gun_Master master;");
        builder.AppendLine("        GA_Gun_Master_Instant instant;");
        builder.AppendLine("        if (abilityClassPath is not null && GunMasterClasses.Contains(abilityClassPath))");
        builder.AppendLine("        {");
        builder.AppendLine("            master = new GA_Gun_Master(unreal, ability.Handle);");
        builder.AppendLine("            if (master.InstantAbility is not { IsValid: true } linkedInstant)");
        builder.AppendLine("            {");
        builder.AppendLine("                return new(null, \"instant-ability-unavailable\", abilityClassPath);");
        builder.AppendLine("            }");
        builder.AppendLine("            instant = linkedInstant;");
        builder.AppendLine("        }");
        builder.AppendLine("        else if (abilityClassPath is not null && InstantGunClasses.Contains(abilityClassPath))");
        builder.AppendLine("        {");
        builder.AppendLine("            instant = new GA_Gun_Master_Instant(unreal, ability.Handle);");
        builder.AppendLine("            if (instant.GA_Gun is not { IsValid: true } linkedMaster)");
        builder.AppendLine("            {");
        builder.AppendLine("                return new(null, \"master-ability-unavailable\", abilityClassPath);");
        builder.AppendLine("            }");
        builder.AppendLine("            master = linkedMaster;");
        builder.AppendLine("        }");
        builder.AppendLine("        else");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"active-ability-is-not-a-gun\", abilityClassPath);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var source = master.SourceCharacter;");
        builder.AppendLine("        if (source is not null && source.Handle != character.Handle)");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"gun-owned-by-another-character\", abilityClassPath);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        if (master.InstantAbility?.Handle != instant.Handle || instant.GA_Gun?.Handle != master.Handle)");
        builder.AppendLine("        {");
        builder.AppendLine("            return new(null, \"gun-link-mismatch\", abilityClassPath);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        var gun = new EquippedGun(character.GetActiveEquipSlotTag(), master, instant);");
        builder.AppendLine("        return new(gun, \"ok\", abilityClassPath);");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private static bool IsDerivedFrom(
        UnrealSdkType type,
        string basePath,
        IReadOnlyDictionary<string, UnrealSdkType> typesByPath)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; visited.Add(current.Path);)
        {
            if (StringComparer.Ordinal.Equals(current.Path, basePath))
            {
                return true;
            }
            if (current.SuperPath is null || !typesByPath.TryGetValue(current.SuperPath, out current))
            {
                return false;
            }
        }
        return false;
    }

    private static void WritePathSet(StringBuilder builder, string name, IReadOnlyList<string> paths)
    {
        builder.Append("    private static readonly global::System.Collections.Generic.HashSet<string> ")
            .Append(name)
            .AppendLine(" = new(global::System.StringComparer.Ordinal)");
        builder.AppendLine("    {");
        foreach (var path in paths)
        {
            builder.Append("        ").Append(Literal(path)).AppendLine(",");
        }
        builder.AppendLine("    };");
    }

    private static void WriteEnum(StringBuilder builder, UnrealSdkType type, string typeName)
    {
        WriteDoc(builder, string.Empty, $"Generated wrapper for the Unreal enum {type.Path}.");
        builder.Append("public enum ").Append(typeName).AppendLine(" : long");
        builder.AppendLine("{");
        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };
        foreach (var value in type.EnumValues)
        {
            var name = UniqueIdentifier(LeafName(value.Name), used);
            WriteDoc(builder, "    ", $"Reflected enum value {value.Name}.");
            builder.Append("    ").Append(name).Append(" = ").Append(value.Value).AppendLine(",");
        }
        builder.AppendLine("}");
    }

    private static void WriteStruct(
        StringBuilder builder,
        UnrealSdkType type,
        string typeName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths,
        IReadOnlyDictionary<string, UnrealSdkType> allStructs)
    {
        WriteDoc(
            builder,
            string.Empty,
            supportedStructPaths.Contains(type.Path)
                ? $"Generated wrapper for the Unreal struct {type.Path}. Supported for field-wise transport; the Descriptor member exposes the verified layout."
                : $"Generated wrapper for the Unreal struct {type.Path}. Not transportable; instances cross the boundary as UnrealValue.");
        builder.Append("public readonly record struct ").Append(typeName).AppendLine();
        builder.AppendLine("{");
        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };

        var allFields = CSharpTypeTranslator.GetAllStructFields(type, allStructs);

        foreach (var property in allFields)
        {
            var propertyType = ResolveType(property.Type, property.ArrayDimension, typeNames, supportedStructPaths);
            var name = UniqueIdentifier(property.Name, used);
            WriteDoc(
                builder,
                "    ",
                $"Reflected field {property.Name} of kind {property.Type.Kind}"
                + (property.ArrayDimension > 1 ? $" (fixed array of {property.ArrayDimension})." : "."));
            builder.Append("    public ").Append(propertyType.Name).Append(' ').Append(name).AppendLine(" { get; init; }");
        }

        if (supportedStructPaths.Contains(type.Path))
        {
            WriteStructAdapter(builder, type, typeName, typeNames, supportedStructPaths, allStructs);
        }

        // For numeric vector-like types (the main thing modders touch inside DamageData etc.)
        // emit a clean, culture-invariant ToString so output always uses '.' as decimal.
        var vectorCoordinates = allFields
            .Where(property => property.Name is "X" or "Y" or "Z")
            .ToArray();
        var isVectorLike = vectorCoordinates.Length == 3
            && allFields.Count <= 4
            && vectorCoordinates.All(property => IsNumericCoordinate(
                ResolveType(property.Type, property.ArrayDimension, typeNames, supportedStructPaths)));
        if (isVectorLike)
        {
            builder.AppendLine();
            WriteDoc(builder, "    ", "Formats the vector with culture-invariant '.' decimal separators.");
            builder.AppendLine("    public override string ToString()");
            builder.AppendLine("    {");
            builder.AppendLine("        return global::System.FormattableString.Invariant($\"({X}, {Y}, {Z})\");");
            builder.AppendLine("    }");
        }

        builder.AppendLine("}");
    }

    private static bool IsNumericCoordinate(CsType type) => type.Name is
        "sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double";

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
        WriteDoc(builder, string.Empty, $"Generated wrapper for the Unreal class {type.Path}.");
        builder.Append("public class ").Append(typeName).Append(" : ").Append(baseName)
            .Append(", IUnrealObjectType<").Append(typeName).AppendLine(">");
        builder.AppendLine("{");
        WriteDoc(builder, "    ", "Full Unreal path of the reflected class.");
        builder.Append("    public ").Append(hidingModifier).Append("const string UnrealPath = ").Append(Literal(type.Path)).AppendLine(";");
        WriteDoc(builder, "    ", "Reflected class short name.");
        builder.Append("    public ").Append(hidingModifier).Append("const string UnrealName = ").Append(Literal(type.Name)).AppendLine(";");
        WriteDoc(builder, "    ", "Path of the engine class default object (CDO).");
        builder.Append("    public ").Append(hidingModifier).Append("const string DefaultObjectPath = ")
            .Append(Literal(GetDefaultObjectPath(type.Path, type.Name))).AppendLine(";");
        builder.AppendLine();
        WriteDoc(builder, "    ", "Wraps one live instance of the class.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        WriteParamDoc(builder, "    ", "handle", "The object handle to wrap.");
        builder.Append("    public ").Append(typeName)
            .Append("(IUnrealReflection unreal, UnrealObjectHandle handle) : base(unreal, handle) { }").AppendLine();
        builder.AppendLine();
        builder.Append("    static string IUnrealObjectType<").Append(typeName).AppendLine(">.UnrealClassName => UnrealName;");
        builder.Append("    static ").Append(typeName).Append(" IUnrealObjectType<").Append(typeName)
            .Append(">.Create(IUnrealReflection unreal, UnrealObjectHandle handle) => new(unreal, handle);").AppendLine();
        builder.AppendLine();
        WriteDoc(builder, "    ", "Finds the first live instance of the class, or null when none exists.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        builder.Append("    public ").Append(hidingModifier).Append("static ").Append(typeName).AppendLine("? FindFirst(IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.Append("        return unreal.FindFirst<").Append(typeName).AppendLine(">();");
        builder.AppendLine("    }");
        builder.AppendLine();
        WriteDoc(builder, "    ", "Finds the engine class default object (CDO), or null when it is not loaded.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        builder.Append("    public ").Append(hidingModifier).Append("static ").Append(typeName).AppendLine("? FindDefaultObject(IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.AppendLine("        var handle = unreal.FindFirstOf(DefaultObjectPath);");
        builder.AppendLine("        return handle.IsNull || !unreal.IsValid(handle) ? null : new(unreal, handle);");
        builder.AppendLine("    }");
        builder.AppendLine();
        WriteDoc(builder, "    ", "Finds every live instance of the class.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        builder.Append("    public ").Append(hidingModifier).Append("static IReadOnlyList<").Append(typeName)
            .AppendLine("> FindAll(IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.Append("        return unreal.FindAll<").Append(typeName).AppendLine(">();");
        builder.AppendLine("    }");

        var usedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            typeName, "UnrealPath", "UnrealName", "DefaultObjectPath", "FindFirst", "FindDefaultObject", "FindAll",
            "Handle", "Unreal", "IsValid", "PathName"
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

    private static string GetDefaultObjectPath(string classPath, string className)
    {
        var separator = classPath.LastIndexOf('.');
        return separator >= 0
            ? classPath.Insert(separator + 1, "Default__")
            : $"{classPath}.Default__{className}";
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
        WriteDoc(
            builder,
            "    ",
            CanWrite(property.Flags)
                ? $"Reflected property {property.Name} of kind {property.Type.Kind}. Readable and writable."
                : $"Reflected property {property.Name} of kind {property.Type.Kind}. Read-only.");
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
                .Append(descriptorName).AppendLine("), Unreal);");
        }
        else if (type.ArrayAdapter || type.OptionalAdapter || type.LazyObjectAdapter || type.SoftObjectAdapter
            || type.SetAdapter || type.MapAdapter)
        {
            builder.Append("        get => ").Append(ReadValueExpression(
                type,
                $"ReadValue({descriptorName})",
                type.LazyObjectAdapter || type.SoftObjectAdapter ? null : ValueDescriptorExpression(type, descriptorName))).AppendLine(";");
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
            else if (type.ArrayAdapter || type.OptionalAdapter || type.LazyObjectAdapter || type.SoftObjectAdapter
                || type.SetAdapter || type.MapAdapter)
            {
                builder.Append("        set => WriteValue(").Append(descriptorName).Append(", ")
                    .Append(type.LazyObjectAdapter || type.SoftObjectAdapter
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
            WriteDoc(builder, "    ", $"Typed result of {methodName}, exposing the return value and out arguments.");
            if (returnParameter is not null)
            {
                WriteParamDoc(builder, "    ", "ReturnValue", "The original UFunction return value.");
            }
            var outputResultNames = new HashSet<string>(StringComparer.Ordinal) { "ReturnValue" };
            foreach (var output in outputs)
            {
                WriteParamDoc(
                    builder,
                    "    ",
                    UniqueIdentifier(output.Name, outputResultNames),
                    $"The out parameter {output.Name} of kind {output.Type.Kind}.");
            }
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
        WriteDoc(builder, "    ", $"Invokes the reflected Unreal function {function.Path}.");
        foreach (var input in inputs)
        {
            WriteParamDoc(builder, "    ", inputNames[input], $"The {input.Name} input of kind {input.Type.Kind}.");
        }
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
                : type.LazyObjectAdapter || type.SoftObjectAdapter ? $"{value}.ToUnrealValue()"
                : type.ArrayAdapter || type.OptionalAdapter || type.SetAdapter || type.MapAdapter
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
        WriteFunctionHooks(
            builder,
            typeNames[owner.Path],
            function,
            methodName,
            descriptorName,
            inputs,
            returnParameter,
            outputs,
            typeNames,
            supportedStructPaths);
    }

    private static void WriteFunctionHooks(
        StringBuilder builder,
        string ownerTypeName,
        UnrealSdkFunction function,
        string methodName,
        string descriptorName,
        IReadOnlyList<UnrealSdkProperty> inputs,
        UnrealSdkProperty? returnParameter,
        IReadOnlyList<UnrealSdkProperty> outputs,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
    {
        var safeMethodName = methodName.TrimStart('@');
        var preHandlerName = Identifier(safeMethodName + "PreHookHandler");
        var postHandlerName = Identifier(safeMethodName + "PostHookHandler");
        var preMethodName = Identifier("Register" + safeMethodName + "PreHook");
        var postMethodName = Identifier("Register" + safeMethodName + "PostHook");

        var preParts = new List<string> { $"{ownerTypeName} context" };
        var usedPreNames = new HashSet<string>(StringComparer.Ordinal) { "context", "callback" };
        var preParameters = inputs.Select(input =>
        {
            var type = ResolveType(input.Type, input.ArrayDimension, typeNames, supportedStructPaths);
            return (Property: input, Type: type, Name: UniqueIdentifier(ToCamelCase(input.Name), usedPreNames));
        }).ToArray();
        preParts.AddRange(preParameters.Select(parameter => $"ref {parameter.Type.Name} {parameter.Name}"));

        var postParts = new List<string> { $"{ownerTypeName} context" };
        var usedPostNames = new HashSet<string>(StringComparer.Ordinal) { "context", "returnValue", "callback" };
        (UnrealSdkProperty Property, CsType Type, string Name)? postReturn = null;
        if (returnParameter is not null)
        {
            var type = ResolveType(returnParameter.Type, returnParameter.ArrayDimension, typeNames, supportedStructPaths);
            postReturn = (returnParameter, type, "returnValue");
            postParts.Add($"ref {type.Name} returnValue");
        }
        var postOutputs = outputs.Select(output =>
        {
            var type = ResolveType(output.Type, output.ArrayDimension, typeNames, supportedStructPaths);
            return (Property: output, Type: type, Name: UniqueIdentifier(ToCamelCase(output.Name), usedPostNames));
        }).ToArray();
        postParts.AddRange(postOutputs.Select(parameter => $"ref {parameter.Type.Name} {parameter.Name}"));

        builder.AppendLine();
        WriteDoc(builder, "    ", $"Callback for the pre-hook of {function.Name}; assignments to ref parameters are written back into the call before the original function runs.");
        WriteParamDoc(builder, "    ", "context", "The wrapped instance the UFunction was called on.");
        foreach (var parameter in preParameters)
        {
            WriteParamDoc(builder, "    ", parameter.Name, $"The {parameter.Property.Name} input of kind {parameter.Property.Type.Kind}; assign through the ref to replace it.");
        }
        builder.Append("    public delegate void ").Append(preHandlerName).Append('(')
            .Append(string.Join(", ", preParts)).AppendLine(");");
        WriteDoc(
            builder,
            "    ",
            $"Registers a pre-hook observing {function.Path}. Returns a subscription; dispose it to remove the hook early.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        WriteParamDoc(builder, "    ", "callback", "The callback invoked for every matching call.");
        WriteParamDoc(builder, "    ", "options", "Optional priority and instance filter.");
        builder.Append("    public static IDisposable ").Append(preMethodName)
            .Append("(IUnrealReflection unreal, ").Append(preHandlerName)
            .AppendLine(" callback, UnrealHookOptions options = default)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(callback);");
        builder.Append("        return unreal.RegisterHook(").Append(descriptorName)
            .AppendLine(", UnrealHookPhase.Pre, options, hook =>");
        builder.AppendLine("        {");
        for (var index = 0; index < preParameters.Length; index++)
        {
            var parameter = preParameters[index];
            builder.Append("            var ").Append(parameter.Name).Append(" = ")
                .Append(CSharpTypeTranslator.ReadHookValueExpression(
                    parameter.Type,
                    $"hook.Arguments[{Literal(parameter.Property.Name)}]",
                    "unreal"))
                .AppendLine(";");
            builder.Append("            var original").Append(index).Append(" = ").Append(parameter.Name).AppendLine(";");
        }
        builder.Append("            callback(new ").Append(ownerTypeName).Append("(unreal, hook.Object)");
        foreach (var parameter in preParameters)
        {
            builder.Append(", ref ").Append(parameter.Name);
        }
        builder.AppendLine(");");
        for (var index = 0; index < preParameters.Length; index++)
        {
            var parameter = preParameters[index];
            builder.Append("            if (!EqualityComparer<").Append(parameter.Type.Name)
                .Append(">.Default.Equals(").Append(parameter.Name).Append(", original").Append(index).AppendLine("))");
            builder.AppendLine("            {");
            builder.Append("                hook.SetArgument(").Append(Literal(parameter.Property.Name)).Append(", ")
                .Append(CSharpTypeTranslator.WriteHookValueExpression(
                    parameter.Type,
                    parameter.Name,
                    $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, parameter.Property)}]"))
                .AppendLine(");");
            builder.AppendLine("            }");
        }
        builder.AppendLine("        });");
        builder.AppendLine("    }");

        builder.AppendLine();
        WriteDoc(builder, "    ", $"Callback for the post-hook of {function.Name}; assignments to the ref return value and ref out parameters are written back into the call results.");
        WriteParamDoc(builder, "    ", "context", "The wrapped instance the UFunction was called on.");
        if (postReturn is { } documentedReturn)
        {
            WriteParamDoc(builder, "    ", documentedReturn.Name, "The original return value; assign through the ref to replace it.");
        }
        foreach (var parameter in postOutputs)
        {
            WriteParamDoc(builder, "    ", parameter.Name, $"The {parameter.Property.Name} out parameter of kind {parameter.Property.Type.Kind}; assign through the ref to replace it.");
        }
        builder.Append("    public delegate void ").Append(postHandlerName).Append('(')
            .Append(string.Join(", ", postParts)).AppendLine(");");
        WriteDoc(
            builder,
            "    ",
            $"Registers a post-hook observing {function.Path}. Pure input parameters are not decoded. Returns a subscription; dispose it to remove the hook early.");
        WriteParamDoc(builder, "    ", "unreal", "The live reflection service.");
        WriteParamDoc(builder, "    ", "callback", "The callback invoked for every matching call.");
        WriteParamDoc(builder, "    ", "options", "Optional priority and instance filter.");
        builder.Append("    public static IDisposable ").Append(postMethodName)
            .Append("(IUnrealReflection unreal, ").Append(postHandlerName)
            .AppendLine(" callback, UnrealHookOptions options = default)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(callback);");
        builder.Append("        return unreal.RegisterHook(").Append(descriptorName)
            .AppendLine(", UnrealHookPhase.Post, options with { SkipInputDecoding = true }, hook =>");
        builder.AppendLine("        {");
        if (postReturn is { } returned)
        {
            builder.Append("            var returnValue = ")
                .Append(CSharpTypeTranslator.ReadHookValueExpression(
                returned.Type,
                "hook.Result.ReturnValue",
                "unreal"))
                .AppendLine(";");
            builder.AppendLine("            var originalReturnValue = returnValue;");
        }
        for (var index = 0; index < postOutputs.Length; index++)
        {
            var parameter = postOutputs[index];
            builder.Append("            var ").Append(parameter.Name).Append(" = ")
                .Append(CSharpTypeTranslator.ReadHookValueExpression(
                    parameter.Type,
                    $"hook.Result.OutArguments[{Literal(parameter.Property.Name)}]",
                    "unreal"))
                .AppendLine(";");
            builder.Append("            var originalOutput").Append(index).Append(" = ").Append(parameter.Name).AppendLine(";");
        }
        builder.Append("            callback(new ").Append(ownerTypeName).Append("(unreal, hook.Object)");
        if (postReturn is not null)
        {
            builder.Append(", ref returnValue");
        }
        foreach (var parameter in postOutputs)
        {
            builder.Append(", ref ").Append(parameter.Name);
        }
        builder.AppendLine(");");
        if (postReturn is { } returnedValue)
        {
            builder.Append("            if (!EqualityComparer<").Append(returnedValue.Type.Name)
                .AppendLine(">.Default.Equals(returnValue, originalReturnValue))");
            builder.AppendLine("            {");
            builder.Append("                hook.SetReturnValue(")
                .Append(CSharpTypeTranslator.WriteHookValueExpression(
                    returnedValue.Type,
                    "returnValue",
                    $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, returnedValue.Property)}]"))
                .AppendLine(");");
            builder.AppendLine("            }");
        }
        for (var index = 0; index < postOutputs.Length; index++)
        {
            var parameter = postOutputs[index];
            builder.Append("            if (!EqualityComparer<").Append(parameter.Type.Name)
                .Append(">.Default.Equals(").Append(parameter.Name).Append(", originalOutput").Append(index).AppendLine("))");
            builder.AppendLine("            {");
            builder.Append("                hook.SetOutArgument(").Append(Literal(parameter.Property.Name)).Append(", ")
                .Append(CSharpTypeTranslator.WriteHookValueExpression(
                    parameter.Type,
                    parameter.Name,
                    $"{descriptorName}.ParameterList[{IndexOf(function.Parameters, parameter.Property)}]"))
                .AppendLine(");");
            builder.AppendLine("            }");
        }
        builder.AppendLine("        });");
        builder.AppendLine("    }");
    }

    private static string ReadValueExpression(
        CsType type,
        string valueExpression,
        string? descriptorExpression = null,
        int containerDepth = 0) =>
        CSharpTypeTranslator.ReadValueExpression(type, valueExpression, descriptorExpression, "Unreal", containerDepth);

    private static string WriteValueExpression(CsType type, string valueExpression, string descriptorExpression) =>
        CSharpTypeTranslator.WriteValueExpression(type, valueExpression, descriptorExpression);

    private static string ValueDescriptorExpression(CsType type, string descriptorOwnerExpression) =>
        CSharpTypeTranslator.ValueDescriptorExpression(type, descriptorOwnerExpression);

    private static string? ValueDescriptorExpressionOrNull(CsType type, string descriptorOwnerExpression) =>
        CSharpTypeTranslator.ValueDescriptorExpressionOrNull(type, descriptorOwnerExpression);

    private static CsType ResolveType(
        UnrealSdkTypeReference type,
        int arrayDimension,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
        => CSharpTypeTranslator.Resolve(type, arrayDimension, typeNames, supportedStructPaths);

    private static void WriteStructAdapter(
        StringBuilder builder,
        UnrealSdkType type,
        string typeName,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths,
        IReadOnlyDictionary<string, UnrealSdkType> allStructs)
    {
        var fields = CSharpTypeTranslator.GetAllStructFields(type, allStructs).ToArray();

        var used = new HashSet<string>(StringComparer.Ordinal) { typeName };
        var fieldNames = fields.ToDictionary(field => field, field => UniqueIdentifier(field.Name, used));

        builder.AppendLine();
        WriteDoc(builder, "    ", "Verified field-wise transport layout of the struct.");
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
        var rawLayout = type.Properties.Count == 0
            && type.SuperPath is not null
            && fields.Length > 0
            && HasFlag(type.Flags, "STRUCT_IsPlainOldData")
            && HasFlag(type.Flags, "STRUCT_NoDestructor");
        builder.Append("    ]");
        if (rawLayout)
        {
            builder.Append(", RawLayout: true");
        }
        builder.AppendLine(");");

        builder.AppendLine();
        WriteDoc(builder, "    ", "Encodes this instance for field-wise transport.");
        builder.AppendLine("    public UnrealValue ToUnrealValue() => UnrealValue.From(new UnrealStructValue(");
        builder.AppendLine("        Descriptor,");
        builder.AppendLine("        new Dictionary<string, UnrealValue>(StringComparer.Ordinal)");
        builder.AppendLine("        {");
        foreach (var field in fields)
        {
            var fieldType = ResolveType(field.Type, field.ArrayDimension, typeNames, supportedStructPaths);
            var expression = CSharpTypeTranslator.WriteHookValueExpression(
                fieldType,
                fieldNames[field],
                $"Descriptor.Fields[{IndexOf(fields, field)}]");
            builder.Append("            [").Append(Literal(field.Name)).Append("] = ").Append(expression).AppendLine(",");
        }
        builder.AppendLine("        }));");

        builder.AppendLine();
        WriteDoc(builder, "    ", "Decodes a transported struct value into this wrapper.");
        builder.Append("    public static ").Append(typeName)
            .AppendLine(" FromUnrealValue(UnrealValue value, IUnrealReflection unreal)");
        builder.AppendLine("    {");
        builder.AppendLine("        ArgumentNullException.ThrowIfNull(unreal);");
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
            var expression = CSharpTypeTranslator.ReadHookValueExpression(fieldType, transported, "unreal");
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
        => CSharpTypeTranslator.AppendValueDescriptors(builder, type, typeNames, supportedStructPaths);

    private static void AppendValueDescriptorInitializer(
        StringBuilder builder,
        UnrealSdkTypeReference type,
        IReadOnlyDictionary<string, string> typeNames,
        IReadOnlySet<string> supportedStructPaths)
        => CSharpTypeTranslator.AppendValueDescriptorInitializer(builder, type, typeNames, supportedStructPaths);

    private static bool IsSupportedArrayElement(
        UnrealSdkTypeReference type,
        IReadOnlySet<string> supportedStructPaths,
        int arrayDepth = 1) => CSharpTypeTranslator.IsSupportedArrayElement(type, supportedStructPaths, arrayDepth);

    private static bool IsSupportedOptionalValue(
        UnrealSdkTypeReference type,
        IReadOnlySet<string> supportedStructPaths) =>
        CSharpTypeTranslator.IsSupportedOptionalValue(type, supportedStructPaths);

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
        => CSharpTypeTranslator.BuildSupportedStructPaths(types);

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
        !HasFlag(flags, "CPF_EditConst")
        && !HasFlag(flags, "CPF_ConstParm");

    private static string DescribeType(UnrealSdkTypeReference type) => CSharpTypeTranslator.Describe(type);

    private static void WriteDoc(StringBuilder builder, string indent, string text)
    {
        builder.Append(indent).Append("/// <summary>").Append(EscapeXml(text)).AppendLine("</summary>");
    }

    private static void WriteParamDoc(StringBuilder builder, string indent, string name, string text)
    {
        builder.Append(indent).Append("/// <param name=\"").Append(EscapeXml(name)).Append("\">")
            .Append(EscapeXml(text)).AppendLine("</param>");
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value);

    private static string EscapeXml(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

}

/// <summary>The file paths and type count produced by one SDK generation.</summary>
/// <param name="SourcePath">Path of the generated <c>RogueMod.GameSdk.g.cs</c> source.</param>
/// <param name="ManifestPath">Path of the generated source manifest JSON.</param>
/// <param name="ProjectPath">Path of the generated SDK project.</param>
/// <param name="TypeCount">Number of reflected types included in the SDK.</param>
public sealed record CSharpSdkGenerationResult(string SourcePath, string ManifestPath, string ProjectPath, int TypeCount);

/// <summary>Package identity and compatibility metadata for a generated game SDK.</summary>
/// <param name="PackageId">NuGet package id, for example <c>DeadzoneRogue.Sdk</c>.</param>
/// <param name="PackageVersion">NuGet package version.</param>
/// <param name="RogueModVersion">The compatible RogueMod version.</param>
/// <param name="GameVersion">The verified game build version, when known.</param>
public sealed record CSharpSdkPackageMetadata(
    string PackageId = "DeadzoneRogue.Sdk",
    string PackageVersion = "0.1.0",
    string RogueModVersion = "0.1.0",
    string? GameVersion = null)
{
    /// <summary>Gets the default metadata used for local, non-published generation.</summary>
    public static CSharpSdkPackageMetadata Default { get; } = new();
}
