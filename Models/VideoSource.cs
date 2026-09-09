namespace Hanime1Downloader.CSharp.Models;

public sealed class VideoSource
{
    public required string Url { get; init; }
    public required string Type { get; init; }
    public int Quality { get; init; }

    public string QualityText => Quality > 0 ? $"{Quality}p" : "未知清晰度";
    public string TypeText => Type.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? "M3U8" : "MP4";

    public override string ToString() => Quality > 0 ? $"{Quality}p - {TypeText}" : TypeText;
}
