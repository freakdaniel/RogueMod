namespace RogueMod.Core.Mods;

internal static class Ue4ssModsFile
{
    public static bool IsEnabled(string path, string loaderId)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        var line = File.ReadLines(path).LastOrDefault(value => IsModLine(value, loaderId));
        if (line is null)
        {
            return false;
        }
        var separator = line.IndexOf(':');
        return separator >= 0 && line[(separator + 1)..].Trim().Equals("1", StringComparison.Ordinal);
    }

    public static void SetEnabled(string path, string loaderId, bool enabled)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var matches = lines.Select((line, index) => (line, index))
            .Where(value => IsModLine(value.line, loaderId))
            .Select(value => value.index)
            .ToArray();
        var stateLine = $"{loaderId} : {(enabled ? 1 : 0)}";
        if (matches.Length > 0)
        {
            lines[matches[0]] = stateLine;
            for (var index = matches.Length - 1; index > 0; index--)
            {
                lines.RemoveAt(matches[index]);
            }
        }
        else
        {
            var insertion = lines.FindIndex(line => IsModLine(line, "Keybinds"));
            lines.Insert(insertion < 0 ? lines.Count : insertion, stateLine);
        }
        WriteAtomic(path, lines);
    }

    public static void Remove(string path, string loaderId)
    {
        if (!File.Exists(path))
        {
            return;
        }
        WriteAtomic(path, File.ReadAllLines(path).Where(line => !IsModLine(line, loaderId)).ToArray());
    }

    private static void WriteAtomic(string path, IReadOnlyCollection<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".roguemod-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsModLine(string line, string loaderId)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith(';'))
        {
            return false;
        }
        var separator = trimmed.IndexOf(':');
        return separator > 0
            && trimmed[..separator].Trim().Equals(loaderId, StringComparison.OrdinalIgnoreCase);
    }
}
