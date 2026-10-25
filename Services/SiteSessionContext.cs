namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// Keeps the configured browser profile separate from the verified request origin.
/// Only the known com/me aliases may share a session; cookie domains are never rewritten.
/// </summary>
public sealed class SiteSessionContext
{
    private readonly string[] _trustedHosts;
    private Uri _baseUri;

    public SiteSessionContext(string siteHost)
    {
        _baseUri = new Uri($"https://{siteHost}/");
        var host = WithoutWww(_baseUri.Host);
        _trustedHosts = host is "hanime1.com" or "hanime1.me"
            ? ["hanime1.com", "hanime1.me"]
            : [host];
    }

    // Uri is immutable. Readers (including background parsers) never access WebView2.
    public Uri BaseUri => Volatile.Read(ref _baseUri);

    public bool IsTrustedOrigin(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        _trustedHosts.Contains(WithoutWww(uri.Host), StringComparer.OrdinalIgnoreCase);

    public bool TryUpdateFromNavigation(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTrustedOrigin(uri))
        {
            return false;
        }

        Volatile.Write(ref _baseUri, new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"));
        return true;
    }

    public bool IsTrustedCookieDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return true;
        var normalized = domain.TrimStart('.');
        return _trustedHosts.Any(host => DomainMatchesHost(host, normalized));
    }

    public static bool DomainMatchesHost(string domain, string host) =>
        !string.IsNullOrWhiteSpace(domain) &&
        (host.Equals(domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase) ||
         host.EndsWith("." + domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    private static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}