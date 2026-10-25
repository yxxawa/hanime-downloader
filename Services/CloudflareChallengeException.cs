namespace Hanime1Downloader.CSharp.Services;

/// <summary>Carry the blocked request into verification so a healthy homepage cannot falsely confirm recovery.</summary>
public sealed class CloudflareChallengeException(string message, string? requestUrl)
    : InvalidOperationException(message)
{
    public string? RequestUrl { get; } = requestUrl;
}
