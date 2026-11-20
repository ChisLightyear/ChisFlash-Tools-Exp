using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChisFlashWaterClient.Services;

internal sealed record ClientReleaseUpdate(Version Version, string Tag, bool IsPrerelease)
{
    private const string Repository = "ChisLightyear/ChisFlash-Tools-Exp";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { UserAgent = { new("ChisFlashWaterClient", "1.0") } },
    };

    internal string DownloadPage => $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(Tag)}";

    internal static async Task<ClientReleaseUpdate?> FetchLatestAsync()
    {
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/releases").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return FindLatest(document.RootElement);
    }

    internal static ClientReleaseUpdate? FindLatest(JsonElement releases)
    {
        ClientReleaseUpdate? latest = null;
        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string? tag = release.GetProperty("tag_name").GetString();
            Version? version = ParseVersion(tag);
            if (version is null || (latest is not null && version < latest.Version)) continue;
            if (!release.GetProperty("assets").EnumerateArray().Any(asset =>
            {
                string? name = asset.GetProperty("name").GetString();
                return name is not null && name.StartsWith("ChisFlashWaterClient", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase);
            })) continue;

            bool isPrerelease = release.GetProperty("prerelease").GetBoolean();
            if (latest?.Version == version && (isPrerelease || !latest.IsPrerelease)) continue;
            latest = new(version, tag!, isPrerelease);
        }
        return latest;
    }

    internal static Version? ParseVersion(string? text)
    {
        Match match = Regex.Match(text ?? string.Empty,
            @"\Av?(?<version>\d+\.\d+)(?:[- ]r(?<revision>\d+))?(?:-\d+(?:\.\d+)+)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        string revision = match.Groups["revision"].Success ? match.Groups["revision"].Value : "0";
        return Version.TryParse(match.Groups["version"].Value + "." + revision, out Version? version) ? version : null;
    }
}
