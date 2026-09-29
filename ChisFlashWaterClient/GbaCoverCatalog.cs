using Microsoft.VisualBasic.FileIO;

namespace ChisFlashWaterClient;

public sealed record GbaCoverCatalogEntry(string GameCode, string Title, string CoverPath);

public static class GbaCoverCatalog
{
    public static string? FindCatalogDirectory()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "covers");
        return File.Exists(Path.Combine(directory, "cover_index.csv"))
            ? directory
            : null;
    }

    public static Dictionary<string, GbaCoverCatalogEntry> LoadPrimaryByGameCode(string directory)
    {
        string catalogPath = Path.Combine(directory, "cover_index.csv");
        using TextFieldParser parser = new(catalogPath, System.Text.Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        string[] headers = parser.ReadFields() ?? throw new InvalidDataException("Cover catalog is empty.");
        Dictionary<string, int> columns = headers
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GbaCoverCatalogEntry> result = new(StringComparer.OrdinalIgnoreCase);
        while (!parser.EndOfData)
        {
            string[]? fields = parser.ReadFields();
            if (fields is null || fields.Length < headers.Length)
            {
                continue;
            }

            string code = fields[columns["game_code"]];
            bool primary = bool.TryParse(fields[columns["primary_for_code"]], out bool parsed) && parsed;
            if (!primary || result.ContainsKey(code))
            {
                continue;
            }

            string relativePath = fields[columns["cover_file"]].Replace('/', Path.DirectorySeparatorChar);
            string coverPath = Path.GetFullPath(Path.Combine(directory, relativePath));
            if (File.Exists(coverPath))
            {
                result[code] = new GbaCoverCatalogEntry(code, fields[columns["canonical_title"]], coverPath);
            }
        }

        string overridesPath = Path.Combine(directory, "cover_overrides.csv");
        if (File.Exists(overridesPath))
        {
            using TextFieldParser overridesParser = new(overridesPath, System.Text.Encoding.UTF8)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
            };
            overridesParser.SetDelimiters(",");
            string[] overrideHeaders = overridesParser.ReadFields() ?? [];
            Dictionary<string, int> overrideColumns = overrideHeaders
                .Select((name, index) => (name, index))
                .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            while (!overridesParser.EndOfData)
            {
                string[]? fields = overridesParser.ReadFields();
                if (fields is null || fields.Length < overrideHeaders.Length)
                {
                    continue;
                }
                string code = fields[overrideColumns["game_code"]];
                string relativePath = fields[overrideColumns["cover_file"]].Replace('/', Path.DirectorySeparatorChar);
                string coverPath = Path.GetFullPath(Path.Combine(directory, relativePath));
                if (!result.ContainsKey(code) && File.Exists(coverPath))
                {
                    result[code] = new GbaCoverCatalogEntry(
                        code,
                        fields[overrideColumns["canonical_title"]],
                        coverPath);
                }
            }
        }

        return result;
    }

    public static bool TryFindByGameCode(
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog,
        string? gameCode,
        out GbaCoverCatalogEntry? entry,
        out string matchedGameCode)
    {
        entry = null;
        matchedGameCode = string.Empty;
        string normalizedCode = (gameCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCode.Length != 4)
        {
            return false;
        }

        if (catalog.TryGetValue(normalizedCode, out entry))
        {
            matchedGameCode = normalizedCode;
            return true;
        }

        HashSet<string> variants = [normalizedCode];
        for (int index = 0; index < normalizedCode.Length; index++)
        {
            char replacement = normalizedCode[index] switch
            {
                '0' => 'O',
                'O' => '0',
                _ => '\0',
            };
            if (replacement == '\0')
            {
                continue;
            }

            foreach (string candidate in variants.ToArray())
            {
                char[] characters = candidate.ToCharArray();
                characters[index] = replacement;
                variants.Add(new string(characters));
            }
        }

        List<KeyValuePair<string, GbaCoverCatalogEntry>> matches = variants
            .Where(candidate => !string.Equals(candidate, normalizedCode, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => catalog.TryGetValue(candidate, out GbaCoverCatalogEntry? candidateEntry)
                ? new KeyValuePair<string, GbaCoverCatalogEntry>(candidate, candidateEntry)
                : default)
            .Where(pair => pair.Value is not null)
            .ToList();
        if (matches.Count != 1)
        {
            return false;
        }

        matchedGameCode = matches[0].Key;
        entry = matches[0].Value;
        return true;
    }
}
