using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 版本更新检查。
/// 优先用 GitHub 的 releases/latest 页面重定向拿 tag（无 API 速率限制），
/// 失败时回退到 REST API；两种方式都失败则静默返回 null。
/// </summary>
public static class UpdateChecker
{
    public const string RepositoryOwner = "yxxawa";
    public const string RepositoryName = "hanime1-downloader";
    private const string ReleasesLatestUrl = "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
    private const string TagMarker = "/releases/tag/";

    public sealed record UpdateCheckResult(
        bool HasUpdate,
        Version CurrentVersion,
        Version? LatestVersion,
        string TagName,
        string ReleaseUrl,
        string? Notes);

    public static Version GetCurrentVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? new Version(0, 0, 0) : Normalize(version);
    }

    public static async Task<UpdateCheckResult?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await TryGetLatestFromReleasesPageAsync(cancellationToken)
                         ?? await TryGetLatestFromApiAsync(cancellationToken);
            if (latest is null)
            {
                return null;
            }

            var normalizedTag = latest.Value.Tag.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(normalizedTag, out var latestVersion))
            {
                return null;
            }

            latestVersion = Normalize(latestVersion);
            var current = Normalize(currentVersion);
            return new UpdateCheckResult(
                latestVersion > current,
                current,
                latestVersion,
                latest.Value.Tag,
                string.IsNullOrWhiteSpace(latest.Value.Url) ? ReleasesLatestUrl : latest.Value.Url,
                latest.Value.Notes);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserIdentity.DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    /// <summary>读取 releases/latest 的最终重定向地址（.../releases/tag/&lt;tag&gt;）。</summary>
    private static async Task<(string Tag, string Url, string? Notes)?> TryGetLatestFromReleasesPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(ReleasesLatestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            var index = finalUrl.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return null;
            }

            var tag = Uri.UnescapeDataString(finalUrl[(index + TagMarker.Length)..]).Trim();
            return string.IsNullOrWhiteSpace(tag) ? null : (tag, finalUrl, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>REST API 兜底（可能受 GitHub 未认证速率限制）。</summary>
    private static async Task<(string Tag, string Url, string? Notes)?> TryGetLatestFromApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            var endpoint = "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
            var json = await client.GetStringAsync(endpoint, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            return string.IsNullOrWhiteSpace(tag) ? null : (tag, url, notes);
        }
        catch
        {
            return null;
        }
    }

    private static Version Normalize(Version version) => new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
}
