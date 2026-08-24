namespace RogueMod.Runtime;

public sealed record ManagedEntryPoint(string AssemblyRelativePath, string TypeName)
{
    public static ManagedEntryPoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var separator = value.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 2 || value.IndexOf("::", separator + 2, StringComparison.Ordinal) >= 0)
        {
            throw new FormatException("Managed entryPoint must use '<assembly.dll>::<namespace.type>'.");
        }

        var assembly = value[..separator].Trim();
        var type = value[(separator + 2)..].Trim();
        if (Path.IsPathRooted(assembly) || string.IsNullOrWhiteSpace(type))
        {
            throw new FormatException("Managed assembly must be a relative path and type name must not be empty.");
        }

        return new(assembly, type);
    }
}
