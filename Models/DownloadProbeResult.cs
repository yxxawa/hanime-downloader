namespace Hanime1Downloader.CSharp.Models;

using Hanime1Downloader.CSharp.Services;

public sealed class DownloadProbeResult
{
    public string ContentType { get; init; } = string.Empty;
    public long? ContentLength { get; init; }
    public bool IsPartial { get; init; }

    /// <summary>m3u8 探测时顺带解析出的播放列表，供下载阶段复用，避免重复拉取。</summary>
    public M3u8Playlist? Playlist { get; init; }
}
