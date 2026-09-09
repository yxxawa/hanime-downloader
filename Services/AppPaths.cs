using System.Diagnostics;
using System.IO;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 统一解析程序数据目录，保证多人分发时每个用户的数据互相独立、且始终可写。
/// 规则：
///   1. 优先使用 exe 所在目录（便携模式，与旧版行为一致，老用户数据位置不变）；
///   2. 该目录不可写时（例如安装到 C:\Program Files 或只读共享目录），回退到
///      %LOCALAPPDATA%\Hanime1Downloader.CSharp —— 每个 Windows 用户一份，互不干扰。
/// 注意：WebView2 的 Cloudflare 会话本就存放在 %LOCALAPPDATA%，天然按用户隔离。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Hanime1Downloader.CSharp";
    private static readonly Lazy<string> DataDirectoryLazy = new(ResolveDataDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => DataDirectoryLazy.Value;

    /// <summary>true = 数据写在程序目录（便携模式）；false = 回退到 %LOCALAPPDATA%。</summary>
    public static bool IsPortable => string.Equals(DataDirectory, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string FavoritesFile => Path.Combine(DataDirectory, "favorites.json");
    public static string DownloadHistoryFile => Path.Combine(DataDirectory, "download_history.json");
    public static string DownloadQueueFile => Path.Combine(DataDirectory, "download_queue.json");
    public static string LegacyCookieCacheFile => Path.Combine(DataDirectory, "cookies.json");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");
    public static string CrashLogFile => Path.Combine(DataDirectory, "crash.log");
    public static string DefaultDownloadDirectory => Path.Combine(DataDirectory, "Downloads");

    public static string CookieCacheFile(string? siteHost)
    {
        var host = string.IsNullOrWhiteSpace(siteHost) ? "default" : siteHost.Trim().ToLowerInvariant();
        host = host.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(DataDirectory, $"cookies.{host}.json");
    }

    private static string ResolveDataDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (IsWritable(baseDirectory))
        {
            return baseDirectory;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
            // 极端情况下连 %LOCALAPPDATA% 都不可用：仍然返回该路径，让后续写入错误显式暴露。
        }

        Debug.WriteLine($"[storage] 程序目录不可写，数据目录回退到 {fallback}");
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
