using Hanime1Downloader.CSharp.Models;

namespace Hanime1Downloader.CSharp.Services;

public static class VerificationRequestPolicy
{
    public static Uri? ResolveTarget(SiteSessionContext session, string? requestUrl)
    {
        if (string.IsNullOrWhiteSpace(requestUrl)) return session.BaseUri;
        if (!Uri.TryCreate(session.BaseUri, requestUrl, out var original) || !session.IsTrustedOrigin(original)) return null;
        return new Uri(session.BaseUri, original.PathAndQuery);
    }

    public static bool IsAccepted(BrowserFetchResult? response, Uri target) =>
        response is { Status: >= 200 and < 300 } &&
        !CloudflareDetection.IsChallengePage(response.Html, response.Title) &&
        Uri.TryCreate(response.Url, UriKind.Absolute, out var actual) &&
        actual.GetLeftPart(UriPartial.Authority).Equals(target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
        actual.PathAndQuery.Equals(target.PathAndQuery, StringComparison.Ordinal);
}
