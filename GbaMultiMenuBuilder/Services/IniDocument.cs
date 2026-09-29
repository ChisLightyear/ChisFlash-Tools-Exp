namespace GbaMultiMenuBuilder.Services;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static IniDocument Load(string path)
    {
        var document = new IniDocument();
        if (!File.Exists(path))
        {
            return document;
        }

        var currentSection = string.Empty;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                document.GetOrCreateSection(currentSection);
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unescape(line[(separator + 1)..].Trim());
            document.Set(currentSection, key, value);
        }

        return document;
    }

    public string Get(string section, string key, string fallback = "")
    {
        if (_sections.TryGetValue(section, out var entries) &&
            entries.TryGetValue(key, out var value))
        {
            return value;
        }

        return fallback;
    }

    public IEnumerable<KeyValuePair<string, string>> GetEntries(string section)
    {
        if (_sections.TryGetValue(section, out var entries))
        {
            return entries;
        }

        return [];
    }

    public void Set(string section, string key, string value)
    {
        var entries = GetOrCreateSection(section);
        entries[key] = value;
    }

    public void Remove(string section, string key)
    {
        if (!_sections.TryGetValue(section, out var entries))
        {
            return;
        }

        entries.Remove(key);
        if (entries.Count == 0)
        {
            _sections.Remove(section);
        }
    }

    public void Save(string path)
    {
        var lines = new List<string>();
        foreach (var section in _sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Key))
            {
                lines.Add($"[{section.Key}]");
            }

            foreach (var entry in section.Value)
            {
                lines.Add($"{entry.Key}={Escape(entry.Value)}");
            }

            lines.Add(string.Empty);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, lines);
    }

    private Dictionary<string, string> GetOrCreateSection(string section)
    {
        if (!_sections.TryGetValue(section, out var entries))
        {
            entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = entries;
        }

        return entries;
    }

    private static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current != '\\' || i == value.Length - 1)
            {
                builder.Append(current);
                continue;
            }

            var next = value[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => '\\',
            });

            if (next is not ('n' or 'r' or 't' or '\\'))
            {
                builder.Append(next);
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
