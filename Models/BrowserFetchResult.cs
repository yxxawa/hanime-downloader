namespace Hanime1Downloader.CSharp.Models;

public sealed class BrowserFetchResult
{
    public int Status { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Html { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public CloudflareChallengeObservation? Challenge { get; init; }
}

/// <summary>Monotonic host-side timestamps; a DOM observation may not have a header timestamp.</summary>
public sealed record CloudflareChallengeObservation(string Source, long DetectedAt, long? HeadersObservedAt = null);
