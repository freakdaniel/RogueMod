namespace RogueMod.Core.Mods;

internal static class ModPackageFileSystem
{
    public static void CopyTree(string sourceRoot, string destinationRoot)
    {
        RejectLink(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            RejectLink(sourceEntry);
            var destination = ResolveInside(destinationRoot, Path.GetFileName(sourceEntry));
            if (Directory.Exists(sourceEntry))
            {
                CopyTree(sourceEntry, destination);
            }
            else
            {
                File.Copy(sourceEntry, destination, overwrite: false);
            }
        }
    }

    public static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Mod packages cannot contain symbolic links or reparse points: {path}");
        }
    }

    public static string Resolve(string root, params string[] relativeParts) =>
        ResolveInside(root, Path.Combine(relativeParts));

    public static string ResolveInside(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Package path must be relative: {relativePath}");
        }
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Package path escapes its root: {relativePath}");
        }
        return candidate;
    }

    public static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static void RestoreFile(string path, byte[]? content)
    {
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, content);
        }
    }
}
