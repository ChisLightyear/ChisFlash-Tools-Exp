using System.Globalization;

namespace GbaMultiMenuBuilder.Services;

public static class MetadataDependencyStampService
{
    public static string Build(string version)
    {
        static long GetTicks(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;

        return string.Join(
            "|",
            version,
            GetTicks(SaveTypeRuleConfigService.ConfigPath).ToString(CultureInfo.InvariantCulture),
            GetTicks(SaveTypeOverrideService.ConfigPath).ToString(CultureInfo.InvariantCulture));
    }
}
