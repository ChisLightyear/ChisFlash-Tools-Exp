namespace ChisFlashWaterClient.Services;

public static class SaveTypeRuleConfigService
{
    private static readonly object SyncRoot = new();
    private static IReadOnlyList<SaveTypePatternRule>? cachedRules;
    private static DateTime cachedLastWriteTimeUtc = DateTime.MinValue;

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "save_types.ini");

    public static IReadOnlyList<SaveTypePatternRule> LoadRules()
    {
        bool configExists = File.Exists(ConfigPath);
        DateTime lastWriteTimeUtc = configExists
            ? File.GetLastWriteTimeUtc(ConfigPath)
            : DateTime.MinValue;

        lock (SyncRoot)
        {
            if (cachedRules is not null && lastWriteTimeUtc == cachedLastWriteTimeUtc)
            {
                return cachedRules;
            }

            List<SaveTypePatternRule> rules = configExists ? LoadConfiguredRules() : [];
            if (rules.Count == 0)
            {
                rules = BuildDefaultRules();
            }

            cachedRules = rules
                .OrderByDescending(static rule => rule.Signature.Length)
                .ThenByDescending(static rule => rule.VersionDigitCount)
                .ToList();
            cachedLastWriteTimeUtc = lastWriteTimeUtc;
            return cachedRules;
        }
    }

    private static List<SaveTypePatternRule> LoadConfiguredRules()
    {
        IniDocument document = IniDocument.Load(ConfigPath);
        List<SaveTypePatternRule> rules = [];

        LoadSectionRules(document, "EEPROM", DetectedSaveTypeKind.Eeprom, rules);
        LoadSectionRules(document, "FLASH", DetectedSaveTypeKind.Flash, rules);
        LoadSectionRules(document, "SRAM", DetectedSaveTypeKind.Sram, rules);

        return rules;
    }

    private static void LoadSectionRules(
        IniDocument document,
        string section,
        DetectedSaveTypeKind defaultKind,
        ICollection<SaveTypePatternRule> target)
    {
        foreach (KeyValuePair<string, string> entry in document.GetEntries(section))
        {
            if (TryParseRule(entry.Key, entry.Value, defaultKind, out SaveTypePatternRule rule))
            {
                target.Add(rule);
            }
        }
    }

    private static bool TryParseRule(
        string signature,
        string value,
        DetectedSaveTypeKind defaultKind,
        out SaveTypePatternRule rule)
    {
        rule = null!;

        string trimmedSignature = signature.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSignature))
        {
            return false;
        }

        string trimmedValue = value.Trim();
        DetectedSaveTypeKind kind = defaultKind;
        int versionDigitCount = 0;

        if (!string.IsNullOrWhiteSpace(trimmedValue))
        {
            string[] parts = trimmedValue.Split(
                '|',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1)
            {
                if (!int.TryParse(parts[0], out versionDigitCount) || versionDigitCount < 0)
                {
                    return false;
                }
            }
            else if (parts.Length == 2)
            {
                if (!Enum.TryParse(parts[0], true, out kind) || kind == DetectedSaveTypeKind.Unknown)
                {
                    return false;
                }

                if (!int.TryParse(parts[1], out versionDigitCount) || versionDigitCount < 0)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        rule = new SaveTypePatternRule(trimmedSignature, kind, versionDigitCount);
        return true;
    }

    private static List<SaveTypePatternRule> BuildDefaultRules()
    {
        return
        [
            new("FLASH1M_V", DetectedSaveTypeKind.Flash1M, 3),
            new("FLASH512_V", DetectedSaveTypeKind.Flash512, 3),
            new("FLASH512", DetectedSaveTypeKind.Flash512, 0),
            new("FLASH_V", DetectedSaveTypeKind.Flash, 3),
            new("EEPROM_V", DetectedSaveTypeKind.Eeprom, 3),
            new("SRAM_F_V", DetectedSaveTypeKind.Sram, 3),
            new("SRAM_V", DetectedSaveTypeKind.Sram, 3),
        ];
    }
}
