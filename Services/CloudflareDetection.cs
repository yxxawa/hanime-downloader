namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// Cloudflare 挑战页识别的统一入口（HanimeApiClient 与 CloudflareWindow 共用）。
/// 判定原则：
///  1. 确定性信号：window._cf_chl_opt / 标题以 "Just a moment" 开头；
///  2. Cloudflare 原文标语保留为强标记；
///  3. 弱标记必须成对出现（challenge-form + Just a moment），避免真实页面内联脚本误判；
///  4. 已移除：cf-challenge、cf-mitigated（响应头内容）、裸 "security verification"。
/// </summary>
public static class CloudflareDetection
{
    public static bool IsChallengePage(string? html, string? title = null) =>
        FindChallengeMarker(html, title) is not null;

    /// <summary>返回命中的标记名（用于诊断日志）；未命中返回 null。</summary>
    public static string? FindChallengeMarker(string? html, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (html.Contains("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase))
        {
            return "window._cf_chl_opt";
        }

        if (!string.IsNullOrWhiteSpace(title) &&
            title.StartsWith("Just a moment", StringComparison.OrdinalIgnoreCase))
        {
            return "title=Just a moment";
        }

        if (html.Contains("Performing security verification", StringComparison.OrdinalIgnoreCase))
        {
            return "Performing security verification";
        }

        if (html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase))
        {
            return "Enable JavaScript and cookies to continue";
        }

        var hasChallengeForm = html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase);
        var hasJustAMoment = html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
        if (hasChallengeForm && hasJustAMoment)
        {
            return "challenge-form + Just a moment";
        }

        return null;
    }

    /// <summary>截取标记附近的一小段 HTML 上下文（≤160 字符），仅用于日志排查。</summary>
    public static string BuildContextSnippet(string? html, string? marker)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(marker))
        {
            return string.Empty;
        }

        var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, index - 60);
        var length = Math.Min(160, html.Length - start);
        return html.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ');
    }
}
