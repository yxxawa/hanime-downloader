namespace Hanime1Downloader.CSharp.Models;

public sealed class DownloadProgressInfo
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    /// <summary>自本次尝试开始的平均速度（用于 ETA 计算）。</summary>
    public double BytesPerSecond { get; init; }
    /// <summary>4 秒滑动窗口瞬时速度（0 表示暂无采样，UI 可回退到平均速度）。</summary>
    public double InstantBytesPerSecond { get; init; }
    /// <summary>没有收到数据或正在重试，UI 显示 0 B/s 并暂停 ETA。</summary>
    public bool IsWaitingForData { get; init; }
    public string? StatusText { get; init; }
    public TimeSpan? EstimatedRemaining =>
        !IsWaitingForData && TotalBytes is > 0 && BytesPerSecond > 0 && BytesReceived < TotalBytes.Value
            ? TimeSpan.FromSeconds((TotalBytes.Value - BytesReceived) / BytesPerSecond)
            : null;
    public double? Percentage => TotalBytes is > 0 ? BytesReceived * 100d / TotalBytes.Value : null;
}
