using Microsoft.VisualBasic.FileIO;

namespace GbaMultiMenuBuilder.Services;

public sealed record GbaCoverCatalogEntry(string GameCode, string Title, string CoverPath);

public static class GbaCoverCatalog
{
    public static string? FindCatalogDirectory(string? preferredDirectory = null)
    {
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(preferredDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "covers")
                : Path.GetFullPath(preferredDirectory.Trim());
        }
        catch
        {
            return null;
        }
        return File.Exists(Path.Combine(directory, "cover_index.csv")) ? directory : null;
    }

    public static Dictionary<string, GbaCoverCatalogEntry> LoadPrimaryByGameCode(string directory)
    {
        var result = new Dictionary<string, GbaCoverCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        LoadCsv(Path.Combine(directory, "cover_index.csv"), directory, result, primaryOnly: true);
        LoadCsv(Path.Combine(directory, "cover_overrides.csv"), directory, result, primaryOnly: false);
        return result;
    }

    private static void LoadCsv(
        string csvPath,
        string directory,
        IDictionary<string, GbaCoverCatalogEntry> result,
        bool primaryOnly)
    {
        if (!File.Exists(csvPath))
        {
            return;
        }

        using TextFieldParser parser = new(csvPath, System.Text.Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? [];
        var columns = headers
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("game_code") || !columns.ContainsKey("cover_file"))
        {
            return;
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length < headers.Length)
            {
                continue;
            }

            var code = fields[columns["game_code"]].Trim().ToUpperInvariant();
            if (code.Length != 4 || result.ContainsKey(code))
            {
                continue;
            }
            if (primaryOnly &&
                (!columns.TryGetValue("primary_for_code", out var primaryColumn) ||
                 !bool.TryParse(fields[primaryColumn], out var isPrimary) || !isPrimary))
            {
                continue;
            }

            var relativePath = fields[columns["cover_file"]].Replace('/', Path.DirectorySeparatorChar);
            var coverPath = Path.GetFullPath(Path.Combine(directory, relativePath));
            if (!File.Exists(coverPath))
            {
                continue;
            }

            var title = columns.TryGetValue("canonical_title", out var titleColumn)
                ? fields[titleColumn]
                : string.Empty;
            result[code] = new GbaCoverCatalogEntry(code, title, coverPath);
        }
    }

    public static bool TryFindByGameCode(
        IReadOnlyDictionary<string, GbaCoverCatalogEntry> catalog,
        string? gameCode,
        out GbaCoverCatalogEntry? entry)
    {
        entry = null;
        var normalizedCode = (gameCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCode.Length != 4)
        {
            return false;
        }
        if (catalog.TryGetValue(normalizedCode, out entry))
        {
            return true;
        }

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedCode };
        for (var index = 0; index < normalizedCode.Length; index++)
        {
            var replacement = normalizedCode[index] switch { '0' => 'O', 'O' => '0', _ => '\0' };
            if (replacement == '\0') continue;
            foreach (var candidate in variants.ToArray())
            {
                var characters = candidate.ToCharArray();
                characters[index] = replacement;
                variants.Add(new string(characters));
            }
        }

        var matches = variants
            .Where(candidate => !candidate.Equals(normalizedCode, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => catalog.TryGetValue(candidate, out var value) ? value : null)
            .Where(value => value is not null)
            .Distinct()
            .ToList();
        if (matches.Count != 1) return false;
        entry = matches[0];
        return true;
    }
}
