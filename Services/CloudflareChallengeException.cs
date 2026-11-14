using Hanime1Downloader.CSharp.Models;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>Carry the blocked request into verification so a healthy homepage cannot falsely confirm recovery.</summary>
public sealed class CloudflareChallengeException(string message, string? requestUrl, CloudflareChallengeObservation? observation = null)
    : InvalidOperationException(message)
{
    public string? RequestUrl { get; } = requestUrl;
    public CloudflareChallengeObservation? Observation { get; } = observation;
}
