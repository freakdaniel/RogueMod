using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RogueMod.Tooling.ApiDocsGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Run(args);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        var assemblies = new List<string>();
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--assembly" when index + 1 < args.Length:
                    assemblies.Add(args[++index]);
                    break;
                case "--out" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (assemblies.Count == 0 || output is null)
        {
            throw new ArgumentException("Usage: ApiDocsGen --assembly <dll> [--assembly <dll> ...] --out <directory>.");
        }

        Directory.CreateDirectory(output);
        var loaded = new List<Assembly>();
        foreach (var path in assemblies.Select(Path.GetFullPath))
        {
            loaded.Add(Assembly.LoadFrom(path));
        }

        var model = new ApiModel(loaded);
        var emitter = new MarkdownEmitter(model, output);
        emitter.Emit();
        Console.WriteLine(
            $"API reference written to {Path.GetFullPath(output)}: {model.Types.Count} types in {model.Namespaces.Count} namespaces.");
    }
}

/// <summary>XML documentation comments of one assembly, indexed by Roslyn-style member ids.</summary>
internal sealed class DocComments
{
    private readonly Dictionary<string, XElement> members;

    private DocComments(Dictionary<string, XElement> members) => this.members = members;

    public static DocComments Load(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"warning: XML documentation file not found: {path}");
            return new([]);
        }

        var document = XDocument.Load(path);
        return new(document
            .Descendants("member")
            .ToDictionary(element => element.Attribute("name")?.Value ?? string.Empty, StringComparer.Ordinal));
    }

    public XElement? Find(string id) => members.TryGetValue(id, out var element) ? element : null;
}

internal sealed class ApiModel
{
    public ApiModel(IReadOnlyList<Assembly> assemblies)
    {
        Assemblies = assemblies;
        foreach (var assembly in assemblies)
        {
            Comments[assembly] = DocComments.Load(Path.ChangeExtension(assembly.Location, ".xml"));
            foreach (var type in assembly.GetTypes().Where(type => type.IsVisible))
            {
                Types[type] = "T:" + (type.FullName ?? type.Name);
            }
        }
        Namespaces = Types.Keys
            .Select(type => type.Namespace ?? string.Empty)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<Assembly> Assemblies { get; }

    public Dictionary<Assembly, DocComments> Comments { get; } = new();

    public Dictionary<Type, string> Types { get; } = new();

    public IReadOnlyList<string> Namespaces { get; }

    public string? DocId(Type type) => Types.TryGetValue(type, out var id) ? id : null;

    public DocComments CommentsFor(Type type) => Comments[type.Assembly];
}

internal sealed class MarkdownEmitter
{
    private readonly ApiModel model;
    private readonly string output;

    public MarkdownEmitter(ApiModel model, string output)
    {
        this.model = model;
        this.output = output;
    }

    public void Emit()
    {
        var position = 1;
        EmitRootIndex();
        foreach (var ns in model.Namespaces)
        {
            var types = model.Types.Keys
                .Where(type => (type.Namespace ?? string.Empty) == ns)
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToList();
            var directory = Path.Combine(output, ns);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "_category_.json"),
                $$"""{ "label": "{{ns}}", "position": {{position}}, "link": { "type": "doc", "id": "{{ns}}/index" } }""" + "\n");
            EmitNamespaceIndex(ns, types);
            foreach (var type in types)
            {
                EmitType(ns, type);
            }
            position++;
        }
    }

    private void EmitRootIndex()
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine("title: API Reference");
        builder.AppendLine("sidebar_position: 0");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("# API Reference");
        builder.AppendLine();
        builder.AppendLine("Generated from the XML documentation comments shipped with the RogueMod assemblies.");
        builder.AppendLine();
        foreach (var ns in model.Namespaces)
        {
            builder.AppendLine($"- [`{ns}`](/api/{ns})");
        }
        File.WriteAllText(Path.Combine(output, "index.md"), builder.ToString());
    }

    private void EmitNamespaceIndex(string ns, List<Type> types)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"title: {ns}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"# Namespace `{ns}`");
        builder.AppendLine();
        builder.AppendLine("| Type | Kind | Summary |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var type in types)
        {
            builder.Append("| [`").Append(Format.Friendly(type)).Append("`](/api/")
                .Append(ns).Append('/').Append(FileSafe(type)).Append(") | ").Append(Format.Kind(type)).Append(" | ")
                .AppendLine(EscapeTable(FirstSentence(Summary(type)) ?? string.Empty));
        }
        File.WriteAllText(Path.Combine(output, ns, "index.md"), builder.ToString());
    }

    private void EmitType(string ns, Type type)
    {
        var builder = new StringBuilder();
        var fileName = FileSafe(type);
        builder.AppendLine("---");
        builder.AppendLine($"title: {Format.BareName(type)}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"# `{Format.Friendly(type)}`");
        builder.AppendLine();

        var summary = Summary(type);
        if (summary is not null)
        {
            builder.AppendLine(summary);
            builder.AppendLine();
        }

        builder.AppendLine("```csharp");
        builder.AppendLine(Format.Declaration(type));
        builder.AppendLine("```");
        builder.AppendLine();

        var baseChain = InheritanceChain(type);
        if (baseChain.Count > 0)
        {
            builder.Append("**Inherits:** ").AppendLine(string.Join(" → ", baseChain));
            builder.AppendLine();
        }

        EmitNestedTypes(builder, type);
        EmitEnumerators(builder, type);
        EmitFields(builder, type);
        EmitProperties(builder, type);
        EmitConstructors(builder, type);
        EmitMethods(builder, type);
        File.WriteAllText(Path.Combine(output, ns, fileName + ".md"), builder.ToString());
    }

    private static string FileSafe(Type type) => (type.FullName ?? type.Name).Replace('+', '.').Replace('`', '-');

    private void EmitNestedTypes(StringBuilder builder, Type type)
    {
        var nested = type.GetNestedTypes(BindingFlags.Public)
            .Where(nestedType => model.Types.ContainsKey(nestedType))
            .OrderBy(nestedType => nestedType.Name, StringComparer.Ordinal)
            .ToList();
        if (nested.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Nested types");
        builder.AppendLine();
        builder.AppendLine("| Type | Summary |");
        builder.AppendLine("| --- | --- |");
        foreach (var nestedType in nested)
        {
            builder.Append("| [`").Append(Format.Friendly(nestedType)).Append("`](/api/")
                .Append(nestedType.Namespace).Append('/').Append(FileSafe(nestedType)).Append(") | ")
                .AppendLine(EscapeTable(FirstSentence(Summary(nestedType)) ?? string.Empty));
        }
        builder.AppendLine();
    }

    private void EmitEnumerators(StringBuilder builder, Type type)
    {
        if (!type.IsEnum)
        {
            return;
        }

        builder.AppendLine("## Values");
        builder.AppendLine();
        builder.AppendLine("| Name | Value | Description |");
        builder.AppendLine("| --- | --- | --- |");
        var underlying = Enum.GetUnderlyingType(type);
        var prefix = MemberPrefix(type);
        foreach (var name in Enum.GetNames(type))
        {
            var value = Convert.ChangeType(Enum.Parse(type, name), underlying);
            builder.Append("| `").Append(name).Append("` | `").Append(value).Append("` | ")
                .AppendLine(EscapeTable(FieldText(type, prefix, name) ?? string.Empty));
        }
        builder.AppendLine();
    }

    private void EmitFields(StringBuilder builder, Type type)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => !field.IsSpecialName)
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToList();
        if (fields.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Fields");
        builder.AppendLine();
        builder.AppendLine("| Field | Type | Description |");
        builder.AppendLine("| --- | --- | --- |");
        var prefix = MemberPrefix(type);
        foreach (var field in fields)
        {
            builder.Append("| `").Append(field.Name).Append("` | `")
                .Append(Format.Friendly(field.FieldType)).Append("` | ")
                .AppendLine(EscapeTable(FieldText(type, prefix, field.Name) ?? string.Empty));
        }
        builder.AppendLine();
    }

    private void EmitProperties(StringBuilder builder, Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToList();
        if (properties.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Properties");
        builder.AppendLine();
        builder.AppendLine("| Property | Type | Description |");
        builder.AppendLine("| --- | --- | --- |");
        var prefix = MemberPrefix(type);
        foreach (var property in properties)
        {
            var accessors = (property.CanRead, property.CanWrite) switch
            {
                (false, false) => string.Empty,
                (_, false) => " (get-only)",
                (false, _) => " (set-only)",
                _ => string.Empty
            };
            builder.Append("| `").Append(property.Name).Append("`").Append(accessors).Append(" | `")
                .Append(Format.Friendly(property.PropertyType)).Append("` | ")
                .AppendLine(EscapeTable(PropertyText(type, prefix, property.Name) ?? string.Empty));
        }
        builder.AppendLine();
    }

    private void EmitConstructors(StringBuilder builder, Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(constructor => constructor.GetParameters().Length)
            .ToList();
        if (constructors.Count == 0 || type.IsEnum)
        {
            return;
        }

        builder.AppendLine("## Constructors");
        builder.AppendLine();
        foreach (var constructor in constructors)
        {
            var id = "M:" + MemberPrefix(type) + ".#ctor" + ParamSuffix(constructor);
            builder.AppendLine("```csharp");
            builder.AppendLine(Format.Signature(constructor, model));
            builder.AppendLine("```");
            builder.AppendLine();
            AppendDocs(builder, type, id);
            AppendParameterTable(builder, constructor, id, type);
        }
    }

    private void EmitMethods(StringBuilder builder, Type type)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName
                && method.DeclaringType != typeof(object))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.GetParameters().Length)
            .ToList();
        if (methods.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Methods");
        builder.AppendLine();
        foreach (var method in methods)
        {
            var id = "M:" + MemberPrefix(type) + "." + method.Name
                + (method.IsGenericMethodDefinition ? "``" + method.GetGenericArguments().Length : string.Empty)
                + ParamSuffix(method);
            builder.AppendLine("```csharp");
            builder.AppendLine(Format.Signature(method, model));
            builder.AppendLine("```");
            builder.AppendLine();
            AppendDocs(builder, type, id);
            AppendParameterTable(builder, method, id, type, returns: true);
        }
    }

    private void AppendDocs(StringBuilder builder, Type type, string id)
    {
        var element = model.CommentsFor(type).Find(id);
        if (element is null)
        {
            return;
        }

        var summary = element.Element("summary");
        if (summary is not null)
        {
            builder.AppendLine(Render(summary));
            builder.AppendLine();
        }

        var remarks = element.Element("remarks");
        if (remarks is not null)
        {
            builder.AppendLine(Render(remarks));
            builder.AppendLine();
        }
    }

    private void AppendParameterTable(StringBuilder builder, MethodBase method, string id, Type type, bool returns = false)
    {
        var parameters = method.GetParameters();
        var comments = model.CommentsFor(type);
        var element = comments.Find(id);
        if (parameters.Length > 0)
        {
            builder.AppendLine("| Parameter | Type | Description |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var parameter in parameters)
            {
                var description = element?
                    .Elements("param")
                    .FirstOrDefault(param => param.Attribute("name")?.Value == parameter.Name) is { } paramElement
                    ? Render(paramElement)
                    : string.Empty;
                builder.Append("| `").Append(parameter.Name).Append("` | `")
                    .Append(Format.Friendly(parameter.ParameterType)).Append("` | ")
                    .AppendLine(EscapeTable(description));
            }
            builder.AppendLine();
        }

        if (returns && method is MethodInfo info && info.ReturnType != typeof(void))
        {
            var returnsElement = element?.Element("returns");
            if (returnsElement is not null)
            {
                builder.Append("**Returns:** ").AppendLine(Render(returnsElement));
                builder.AppendLine();
            }
        }
    }

    private string? Summary(Type type) =>
        model.CommentsFor(type).Find("T:" + MemberPrefix(type)) is { } element
            ? Render(element.Element("summary") ?? new XElement("summary"))
            : null;

    private string? FieldText(Type type, string prefix, string name) =>
        model.CommentsFor(type).Find("F:" + prefix + "." + name) is { } element
            ? Render(element.Element("summary") ?? new XElement("summary"))
            : null;

    private string? PropertyText(Type type, string prefix, string name) =>
        model.CommentsFor(type).Find("P:" + prefix + "." + name) is { } element
            ? Render(element.Element("summary") ?? new XElement("summary"))
            : null;

    private static string MemberPrefix(Type type) => type.FullName ?? type.Name;

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var sentence = Regex.Match(text, @"^[^.]*\.");
        return sentence.Success && sentence.Value.Length < 200 ? sentence.Value : text;
    }

    private static string EscapeTable(string text) => text.Replace("|", "\\|").Replace("\n", " ").Trim();

    private static string EscapeProse(string text) => text
        .Replace("<", "&lt;")
        .Replace("{", "&#123;")
        .Replace("}", "&#125;");

    private static string ParamSuffix(MethodBase method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 0 ? string.Empty : "(" + string.Join(",", parameters.Select(parameter => XmlTypeName(parameter.ParameterType))) + ")";
    }

    private static string XmlTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return XmlTypeName(type.GetElementType()!) + "@";
        }
        if (type.IsArray)
        {
            return XmlTypeName(type.GetElementType()!) + "[]";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var definition = type.GetGenericTypeDefinition();
            var definitionName = definition.FullName ?? definition.Name;
            var arity = definitionName.IndexOf('`');
            var args = string.Join(",", type.GetGenericArguments().Select(XmlTypeName));
            return definitionName[..arity] + "{" + args + "}";
        }
        return type.FullName ?? type.Name;
    }

    private List<string> InheritanceChain(Type type)
    {
        var chain = new List<string>();
        var current = type.BaseType;
        while (current is not null && current != typeof(object))
        {
            chain.Add(model.Types.ContainsKey(current) ? $"[`{Format.Friendly(current)}`]({TypeLink(current)})" : $"`{Format.Friendly(current)}`");
            current = current.BaseType;
        }
        return chain;
    }

    private string TypeLink(Type type) => $"/api/{type.Namespace}/{FileSafe(type)}";

    private string Render(XElement element)
    {
        var builder = new StringBuilder();
        RenderNodes(element.Nodes(), builder);
        return EscapeProse(builder.ToString().Trim());
    }

    private void RenderNodes(IEnumerable<XNode> nodes, StringBuilder builder)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    builder.Append(Regex.Replace(text.Value, @"\s+", " "));
                    break;
                case XElement element:
                    RenderElement(element, builder);
                    break;
            }
        }
    }

    private void RenderElement(XElement element, StringBuilder builder)
    {
        switch (element.Name.LocalName)
        {
            case "see":
                AppendSee(element, builder);
                break;
            case "paramref" or "typeparamref":
                builder.Append('`').Append(element.Attribute("name")?.Value).Append('`');
                break;
            case "c":
                builder.Append('`').Append(element.Value).Append('`');
                break;
            case "para":
                builder.Append(' ');
                RenderNodes(element.Nodes(), builder);
                builder.Append(' ');
                break;
            case "param" or "returns" or "summary" or "remarks" or "value":
                RenderNodes(element.Nodes(), builder);
                break;
            default:
                builder.Append(' ');
                RenderNodes(element.Nodes(), builder);
                builder.Append(' ');
                break;
        }
    }

    private void AppendSee(XElement element, StringBuilder builder)
    {
        var cref = element.Attribute("cref")?.Value;
        if (cref is null)
        {
            builder.Append('`').Append(element.Value).Append('`');
            return;
        }

        if (cref.StartsWith("T:", StringComparison.Ordinal))
        {
            var match = model.Types.Keys.FirstOrDefault(candidate =>
                (candidate.FullName ?? candidate.Name) == cref[2..].Replace('/', '+'));
            if (match is not null)
            {
                builder.Append('[').Append(Format.Friendly(match)).Append("](").Append(TypeLink(match)).Append(')');
                return;
            }
        }

        var reference = cref;
        var parenthesis = reference.IndexOf('(');
        if (parenthesis >= 0)
        {
            reference = reference[..parenthesis];
        }
        var lastDot = reference.LastIndexOf('.');
        var name = lastDot >= 0 ? reference[(lastDot + 1)..] : reference;
        if (name.Length > 2 && name[1] == ':')
        {
            name = name[2..];
        }
        builder.Append('`').Append(name).Append('`');
    }
}

internal static class Format
{
    public static string Kind(Type type) => type.IsInterface ? "interface"
        : type.IsEnum ? "enum"
        : type.IsValueType ? "struct"
        : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
        : "class";

    public static string BareName(Type type)
    {
        var name = type.Name;
        var arity = name.IndexOf('`');
        return arity > 0 ? name[..arity] : name;
    }

    private static readonly Dictionary<string, string> KeywordNames = new(StringComparer.Ordinal)
    {
        ["String"] = "string",
        ["Int32"] = "int",
        ["Int64"] = "long",
        ["Int16"] = "short",
        ["Byte"] = "byte",
        ["SByte"] = "sbyte",
        ["UInt32"] = "uint",
        ["UInt64"] = "ulong",
        ["UInt16"] = "ushort",
        ["Boolean"] = "bool",
        ["Single"] = "float",
        ["Double"] = "double",
        ["Object"] = "object",
        ["Char"] = "char",
        ["Decimal"] = "decimal"
    };

    public static string Friendly(Type type)
    {
        if (type == typeof(void))
        {
            return "void";
        }
        if (type.IsByRef)
        {
            return Friendly(type.GetElementType()!);
        }
        if (type.IsArray)
        {
            return Friendly(type.GetElementType()!) + "[]";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (type.IsGenericType)
        {
            var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
            var bare = BareName(definition);
            var arguments = type.GetGenericArguments().Select(Friendly);
            return type.IsNested
                ? Friendly(type.DeclaringType!) + "." + bare + "<" + string.Join(", ", arguments) + ">"
                : bare + "<" + string.Join(", ", arguments) + ">";
        }
        var name = type.IsNested ? Friendly(type.DeclaringType!) + "." + type.Name : type.Name;
        return KeywordNames.TryGetValue(name, out var keyword) ? keyword : name;
    }

    public static string Declaration(Type type)
    {
        if (type.IsInterface)
        {
            return "public interface " + Friendly(type);
        }
        if (type.IsEnum)
        {
            return "public enum " + Friendly(type) + " : " + Enum.GetUnderlyingType(type).Name;
        }
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            var invoke = type.GetMethod("Invoke")!;
            return "public delegate " + Friendly(invoke.ReturnType) + " " + Friendly(type)
                + "(" + string.Join(", ", invoke.GetParameters().Select(p =>
                    Friendly(p.ParameterType) + " " + p.Name)) + ")";
        }
        if (type.IsValueType)
        {
            return "public struct " + Friendly(type);
        }
        var declaration = "public class " + Friendly(type);
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType.IsVisible)
        {
            declaration += " : " + Friendly(baseType);
        }
        return declaration;
    }

    public static string Signature(MethodBase method, ApiModel model)
    {
        var builder = new StringBuilder();
        if (method is MethodInfo info)
        {
            if (info.DeclaringType!.IsInterface && info.IsStatic && info.IsAbstract)
            {
                builder.Append("static abstract ");
            }
            else if (method.IsStatic)
            {
                builder.Append("static ");
            }
            builder.Append(Friendly(info.ReturnType)).Append(' ');
        }
        else
        {
            builder.Append(BareName(method.DeclaringType!));
        }

        if (method.IsGenericMethodDefinition)
        {
            builder.Append(method.Name)
                .Append('<')
                .Append(string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name)))
                .Append('>');
        }
        else
        {
            builder.Append(method.Name);
        }

        builder.Append('(')
            .Append(string.Join(", ", method.GetParameters().Select(FriendlyParameter)))
            .Append(')');
        return builder.ToString();
    }

    private static string FriendlyParameter(ParameterInfo parameter)
    {
        var builder = new StringBuilder();
        if (parameter.ParameterType.IsByRef)
        {
            builder.Append(parameter.IsOut ? "out " : "ref ");
        }
        if (parameter.GetCustomAttributes<ParamArrayAttribute>().Any())
        {
            builder.Append("params ");
        }
        builder.Append(Friendly(parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType));
        builder.Append(' ').Append(parameter.Name);
        if (parameter.HasDefaultValue)
        {
            builder.Append(" = ").Append(DefaultValue(parameter));
        }
        return builder.ToString();
    }

    private static string DefaultValue(ParameterInfo parameter)
    {
        var value = parameter.RawDefaultValue;
        return value switch
        {
            null => "null",
            true => "true",
            false => "false",
            string text => $"\"{text}\"",
            char character => $"'{character}'",
            _ => value.ToString() ?? "null"
        };
    }
}
