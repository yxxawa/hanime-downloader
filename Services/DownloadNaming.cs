using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Hanime1Downloader.CSharp.Services;

public sealed record DownloadNamingContext(string? Title, string? VideoId, int Quality,
    string? Author = null, string? UploadDate = null, DateTime? NamingTime = null);

/// <summary>One renderer for previews, saved queue rules, and actual paths. Only template separators create folders.</summary>
public static class DownloadNaming
{
    public const string DefaultRule = "{title}_{videoId}";
    public const string AuthorRule = @"{author}\{date}-{name}-{quality}";
    public const string SimpleRule = "{name}-{quality}";
    private static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly Regex AuthorPrefix = new(@"^\s*(?:\[(?<author>[^\[\]]+)\]|【(?<author>[^【】]+)】)\s*", RegexOptions.Compiled);
    private static readonly HashSet<string> Tokens = new(StringComparer.OrdinalIgnoreCase)
        { "title", "name", "author", "date", "downloadDate", "videoId", "quality", "timestamp" };

    public static bool TryValidate(string? template, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(template)) { error = "请输入命名模板。"; return false; }
        if (Path.IsPathRooted(template) || template.IndexOfAny([':', '<', '>', '\"', '|', '?', '*']) >= 0 || template.Any(char.IsControl))
        { error = "模板必须是相对路径，不能包含盘符或 Windows 非法字符。"; return false; }
        var segments = template.Replace('/', '\\').Split('\\');
        if (segments.Length > 6 || segments.Any(s => string.IsNullOrWhiteSpace(s) || s.Trim() is "." or ".."))
        { error = "子目录不能留空或使用 .、..，最多支持 5 层子目录。"; return false; }
        var matches = Token.Matches(template);
        if (matches.Count == 0) { error = "模板至少包含一个字段，例如 {title}。"; return false; }
        foreach (Match match in matches)
            if (!Tokens.Contains(match.Groups[1].Value))
            { error = $"未知字段 {match.Value}，请使用下方按钮插入字段。"; return false; }
        if (Token.Replace(template, "").IndexOfAny(['{', '}']) >= 0)
        { error = "字段的大括号必须配对，不能嵌套。"; return false; }
        return true;
    }

    public static string InferAuthor(string? title) => AuthorPrefix.Match(title ?? "").Groups["author"].Value.Trim();

    public static string BuildRelativePath(string template, DownloadNamingContext context)
    {
        if (!TryValidate(template, out var error)) throw new ArgumentException(error, nameof(template));
        var now = context.NamingTime ?? DateTime.Now;
        var title = string.IsNullOrWhiteSpace(context.Title) ? $"hanime_{now:yyyyMMdd_HHmmss}" : context.Title.Trim();
        var inferredAuthor = InferAuthor(title);
        var name = string.IsNullOrEmpty(inferredAuthor) ? title : AuthorPrefix.Replace(title, "", 1);
        var uploadDate = DateTime.TryParseExact(context.UploadDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsedDate) ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "未知日期";
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = title, ["name"] = string.IsNullOrWhiteSpace(name) ? title : name,
            ["author"] = string.IsNullOrWhiteSpace(context.Author) ? (string.IsNullOrEmpty(inferredAuthor) ? "未知作者" : inferredAuthor) : context.Author,
            ["date"] = uploadDate, ["downloadDate"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["videoId"] = string.IsNullOrWhiteSpace(context.VideoId) ? "unknown" : context.VideoId,
            ["quality"] = context.Quality > 0 ? $"{context.Quality}p" : "unknown",
            ["timestamp"] = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
        };
        var segments = template.Trim().Replace('/', '\\').Split('\\');
        var extension = segments[^1].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? segments[^1][^4..] : ".mp4";
        if (segments[^1].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) segments[^1] = segments[^1][..^4];
        for (var i = 0; i < segments.Length; i++)
        {
            var expanded = Token.Replace(segments[i], m => DownloadPathGuard.SanitizeFileName(fields[m.Groups[1].Value]));
            segments[i] = DownloadPathGuard.SanitizeFileName(expanded);
        }
        segments[^1] += extension;
        return Path.Combine(segments);
    }
}
