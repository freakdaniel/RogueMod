using System.Globalization;
using System.Text;

namespace RogueMod.Core.Profiles;

internal static class Ue4ssSettingsEditor
{
    private const string Section = "EngineVersionOverride";

    internal static void Apply(string path, Ue4ssEngineVersionOverride version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(version);

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var sectionStart = FindSection(lines, Section);
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }
            lines.Add($"[{Section}]");
            sectionStart = lines.Count - 1;
        }

        var sectionEnd = FindSectionEnd(lines, sectionStart);
        SetValue(lines, sectionStart, ref sectionEnd, "MajorVersion", version.MajorVersion);
        SetValue(lines, sectionStart, ref sectionEnd, "MinorVersion", version.MinorVersion);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + $".roguemod-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static bool Matches(string path, Ue4ssEngineVersionOverride expected)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        var lines = File.ReadAllLines(path);
        var sectionStart = FindSection(lines, Section);
        if (sectionStart < 0)
        {
            return false;
        }
        var sectionEnd = FindSectionEnd(lines, sectionStart);
        return TryReadValue(lines, sectionStart, sectionEnd, "MajorVersion", out var major)
            && TryReadValue(lines, sectionStart, sectionEnd, "MinorVersion", out var minor)
            && major == expected.MajorVersion
            && minor == expected.MinorVersion;
    }

    private static int FindSection(IReadOnlyList<string> lines, string name)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindSectionEnd(IReadOnlyList<string> lines, int sectionStart)
    {
        for (var index = sectionStart + 1; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                return index;
            }
        }
        return lines.Count;
    }

    private static void SetValue(
        List<string> lines,
        int sectionStart,
        ref int sectionEnd,
        string key,
        int value)
    {
        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            if (IsKey(lines[index], key))
            {
                lines[index] = $"{key} = {value.ToString(CultureInfo.InvariantCulture)}";
                return;
            }
        }
        lines.Insert(sectionEnd++, $"{key} = {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool TryReadValue(
        IReadOnlyList<string> lines,
        int sectionStart,
        int sectionEnd,
        string key,
        out int value)
    {
        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            var line = lines[index].Trim();
            if (!IsKey(line, key))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            var raw = line[(separator + 1)..].Split(';', 2)[0].Trim();
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        value = default;
        return false;
    }

    private static bool IsKey(string line, string key)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
        {
            return false;
        }
        var separator = trimmed.IndexOf('=');
        return separator >= 0
            && trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
