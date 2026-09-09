namespace Hanime1Downloader.CSharp.Models;

public sealed class VideoDetails
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string CoverUrl { get; set; } = string.Empty;
    public string UploadDate { get; set; } = string.Empty;
    public string Views { get; set; } = string.Empty;
    public string Likes { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<VideoSummary> RelatedVideos { get; set; } = [];
    public List<VideoSource> Sources { get; set; } = [];
    public VideoDetailsLoadOptions LoadOptions { get; set; } = VideoDetailsLoadOptions.Basic;
}
