using System.IO;
using System.Windows;

namespace Hanime1Downloader.CSharp.Models;

public sealed class AppSettings
{
    public static string DefaultDownloadPath => Path.Combine(AppContext.BaseDirectory, "Downloads");

    public string DownloadPath { get; set; } = DefaultDownloadPath;
    public string FileNamingRule { get; set; } = "{title}_{videoId}";
    public bool ShowListCovers { get; set; } = true;
    public bool CompactMode { get; set; } = false;
    public string DefaultQuality { get; set; } = "highest";
    public string SiteHost { get; set; } = "hanime1.com";
    public List<string> CustomSiteHosts { get; set; } = [];
    public bool PersistDownloadQueue { get; set; } = true;
    public string ThemeMode { get; set; } = "light";
    public int MaxConcurrentDownloads { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
    public int SpeedLimitKBps { get; set; } = 0;
    public List<string> SearchHistory { get; set; } = [];
    public VideoDetailsVisibilitySettings VideoDetailsVisibility { get; set; } = new();
    public PlayerWindowSettings PlayerWindow { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public WindowState WindowState { get; set; } = WindowState.Normal;

    public string SiteBaseUrl => $"https://{SiteHost}";
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
    public double? PlaybackPosition { get; set; }
    public double? Volume { get; set; }
}
