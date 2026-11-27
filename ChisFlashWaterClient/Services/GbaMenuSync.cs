using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChisFlashWaterClient.Services;

internal sealed record GbaMenuRelease(string Tag, bool IsPrerelease);

internal static class GbaMenuSync
{
    private const string Repository = "ChisLightyear/ChisFlash-Multicart-Menus";
    private const string ActiveFile = "active-release.txt";
    internal const string NsAsset = "menu_NS.gba";
    internal const string NsLiteAsset = "menu_NSLite.gba";
    private static readonly Regex ReleaseTag = new(@"\Av[0-9]{4}\.[0-9]{2}\.[0-9]{2}(-r[0-9]+)?\z");
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
        DefaultRequestHeaders = { UserAgent = { new("ChisFlashWaterClient", "1.0") } },
    };

    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChisFlashWaterClient", "menu-cache");

    internal static string? GetActiveTag(string? cacheRoot = null)
    {
        string marker = Path.Combine(cacheRoot ?? CacheRoot, ActiveFile);
        if (!File.Exists(marker)) return null;
        string tag = File.ReadAllText(marker).Trim();
        return ReleaseTag.IsMatch(tag) ? tag : null;
    }

    internal static string? GetActiveMenuDirectory()
    {
        string? tag = GetActiveTag();
        return tag is null ? null : Path.Combine(CacheRoot, tag);
    }

    internal static string? GetBundledTag()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "menu-version.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static Version? ParseVersion(string? tag) =>
        Version.TryParse(tag?.TrimStart('v').Replace("-r", "."), out var version) ? version : null;

    internal static async Task<List<GbaMenuRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default, HttpClient? client = null)
    {
        using var response = await (client ?? Client).GetAsync(
            $"https://api.github.com/repos/{Repository}/releases?per_page=20", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ReadReleases(document.RootElement);
    }

    internal static List<GbaMenuRelease> ReadReleases(JsonElement releases)
    {
        var result = new List<GbaMenuRelease>();
        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!ReleaseTag.IsMatch(tag)) continue;
            var names = release.GetProperty("assets").EnumerateArray()
                .Select(asset => asset.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
            if (names.Contains(NsAsset) && names.Contains(NsLiteAsset))
                result.Add(new(tag, release.GetProperty("prerelease").GetBoolean()));
        }
        return result.OrderByDescending(release => ParseVersion(release.Tag)).ToList();
    }

    internal static async Task<GbaMenuRelease?> SyncLatestAsync(
        string? cacheRoot = null, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        var releases = await GetReleasesAsync(cancellationToken, client).ConfigureAwait(false);
        var latest = releases.FirstOrDefault();
        var currentVersion = ParseVersion(GetActiveTag(cacheRoot) ?? GetBundledTag());
        if (latest is null || ParseVersion(latest.Tag) is not Version latestVersion || latestVersion <= currentVersion)
            return null;

        await SyncAsync(latest, cacheRoot, client, cancellationToken).ConfigureAwait(false);
        return latest;
    }

    internal static async Task SyncAsync(GbaMenuRelease release, string? cacheRoot = null, HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(release.Tag);
        if (!ReleaseTag.IsMatch(release.Tag)) throw new ArgumentException("Invalid menu release tag.");
        string root = cacheRoot ?? CacheRoot;
        string versionDirectory = Path.Combine(root, release.Tag);
        if (!Directory.Exists(versionDirectory))
        {
            string download = $"https://github.com/{Repository}/releases/download/{release.Tag}";
            HttpClient http = client ?? Client;
            byte[][] menus = await Task.WhenAll(
                http.GetByteArrayAsync($"{download}/{NsAsset}", cancellationToken),
                http.GetByteArrayAsync($"{download}/{NsLiteAsset}", cancellationToken)).ConfigureAwait(false);
            GbaMenuRomService.ValidateUniversalMenuRom(menus[0]);
            GbaMenuRomService.ValidateUniversalMenuRom(menus[1]);
            if (!GbaMenuRomService.HasUniversalMenuSignature(menus[0]) ||
                !GbaMenuRomService.HasSimplifiedNsMenuSignature(menus[1]))
                throw new InvalidOperationException(LocalizedText.T("ui", "sync_gba_menu_invalid", "The release assets do not match NS and NS Lite."));

            // 两份菜单齐备后才公布版本目录。下载中断时仍使用原来选中的版本。
            string staging = Path.Combine(root, $".incoming-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(staging, NsAsset), menus[0], cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(staging, NsLiteAsset), menus[1], cancellationToken).ConfigureAwait(false);
                try { Directory.Move(staging, versionDirectory); }
                catch (IOException) when (Directory.Exists(versionDirectory)) { }
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    File.Delete(Path.Combine(staging, NsAsset));
                    File.Delete(Path.Combine(staging, NsLiteAsset));
                    Directory.Delete(staging);
                }
            }
        }

        string marker = Path.Combine(root, ActiveFile);
        string next = Path.Combine(root, Path.GetRandomFileName());
        try
        {
            await File.WriteAllTextAsync(next, release.Tag + "\n", cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(next, marker, overwrite: true);
        }
        finally { File.Delete(next); }
    }
}
