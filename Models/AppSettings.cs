using System.IO;
using System.Windows;
using Hanime1Downloader.CSharp.Services;

namespace Hanime1Downloader.CSharp.Models;

public sealed class AppSettings
{
    public static string DefaultDownloadPath => AppPaths.DefaultDownloadDirectory;

    public string DownloadPath { get; set; } = DefaultDownloadPath;
    public string FileNamingRule { get; set; } = "{title}_{videoId}";
    public bool ShowListCovers { get; set; } = true;
    public string DefaultQuality { get; set; } = "highest";
    public string SiteHost { get; set; } = "hanime1.com";
    public List<string> CustomSiteHosts { get; set; } = [];
    public bool PersistDownloadQueue { get; set; } = true;
    public string ThemeMode { get; set; } = "light";
    public int MaxConcurrentDownloads { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
    public List<string> SearchHistory { get; set; } = [];
    public VideoDetailsVisibilitySettings VideoDetailsVisibility { get; set; } = new();
    public PlayerWindowSettings PlayerWindow { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public WindowState WindowState { get; set; } = WindowState.Normal;

}

public sealed class VideoDetailsVisibilitySettings
{
    public bool Title { get; set; } = true;
    public bool UploadDate { get; set; } = true;
    public bool Likes { get; set; } = true;
    public bool Views { get; set; } = true;
    public bool Duration { get; set; } = true;
    public bool Tags { get; set; } = true;
    public bool Cover { get; set; } = true;
    public bool RelatedVideos { get; set; } = true;
}

public sealed class PlayerWindowSettings
{
    public double Width { get; set; } = 920;
    public double Height { get; set; } = 620;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public WindowState WindowState { get; set; } = WindowState.Normal;

    private Dictionary<string, double> _playbackPositions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按视频 ID 记录播放进度（秒），播完自动移除；反序列化后仍保持忽略大小写。</summary>
    public Dictionary<string, double> PlaybackPositions
    {
        get => _playbackPositions;
        set => _playbackPositions = value is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(value, StringComparer.OrdinalIgnoreCase);
    }

    public double? Volume { get; set; }
}
