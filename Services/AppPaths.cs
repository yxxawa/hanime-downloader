using System.Diagnostics;
using System.IO;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 统一解析程序数据目录：始终使用 %LOCALAPPDATA%\Hanime1Downloader.CSharp，
/// exe 所在目录保持干净（不再生成 settings / cookies / 日志 / thumbcache / Downloads 等）。
/// 首次运行时会把旧版留在 exe 目录的便携数据迁移过来，并把仍指向 exe 目录的下载路径改到新目录。
/// 注意：WebView2 的 Cloudflare 会话同样在 %LOCALAPPDATA%，天然按用户隔离。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Hanime1Downloader.CSharp";
    private static readonly Lazy<string> DataDirectoryLazy = new(ResolveDataDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => DataDirectoryLazy.Value;

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string FavoritesFile => Path.Combine(DataDirectory, "favorites.json");
    public static string DownloadHistoryFile => Path.Combine(DataDirectory, "download_history.json");
    public static string DownloadQueueFile => Path.Combine(DataDirectory, "download_queue.json");
    public static string LegacyCookieCacheFile => Path.Combine(DataDirectory, "cookies.json");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");
    public static string CrashLogFile => Path.Combine(DataDirectory, "crash.log");
    /// <summary>默认下载目录：exe 所在目录下的 Downloads（数据/缓存仍放 %LOCALAPPDATA%）。</summary>
    public static string DefaultDownloadDirectory => Path.Combine(AppContext.BaseDirectory, "Downloads");

    public static string CookieCacheFile(string? siteHost)
    {
        var host = string.IsNullOrWhiteSpace(siteHost) ? "default" : siteHost.Trim().ToLowerInvariant();
        host = host.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return Path.Combine(DataDirectory, $"cookies.{host}.json");
    }

    private static string ResolveDataDirectory()
    {
        var userDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
        try
        {
            Directory.CreateDirectory(userDirectory);
        }
        catch
        {
            // 极端情况下连 %LOCALAPPDATA% 都不可用：仍然返回该路径，让后续写入错误显式暴露。
        }

        TryMigrateLegacyPortableData(userDirectory);
        return userDirectory;
    }

    /// <summary>
    /// 旧版把数据放在 exe 目录（便携模式）。升级后数据目录改为 %LOCALAPPDATA%，
    /// 首次启动时把旧数据搬过来；下载路径若仍指向 exe 目录，一并改到新目录。
    /// </summary>
    private static void TryMigrateLegacyPortableData(string targetDirectory)
    {
        try
        {
            var legacyDirectory = AppContext.BaseDirectory;
            if (string.Equals(Path.GetFullPath(legacyDirectory), Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var legacySettings = Path.Combine(legacyDirectory, "settings.json");
            var targetSettings = Path.Combine(targetDirectory, "settings.json");
            if (!File.Exists(legacySettings) || File.Exists(targetSettings))
            {
                return; // 没有旧数据，或新目录已有数据：不覆盖
            }

            foreach (var name in new[] { "settings.json", "favorites.json", "download_history.json", "download_queue.json" })
            {
                var source = Path.Combine(legacyDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(targetDirectory, name), overwrite: true);
                }
            }

            foreach (var cookieFile in Directory.EnumerateFiles(legacyDirectory, "cookies*.json"))
            {
                File.Copy(cookieFile, Path.Combine(targetDirectory, Path.GetFileName(cookieFile)), overwrite: true);
            }

        }
        catch
        {
            // 迁移失败不影响启动：新目录按默认设置运行。
        }
    }
}
