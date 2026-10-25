using System.Net;
using Hanime1Downloader.CSharp.Models;
using Microsoft.Web.WebView2.Core;

namespace Hanime1Downloader.CSharp.Services;

public sealed class CookieSessionBridge
{
    private readonly SiteSessionContext _session;
    private readonly string _defaultDomain;

    public CookieSessionBridge(string siteHost = "hanime1.me") : this(new SiteSessionContext(siteHost)) { }

    public CookieSessionBridge(SiteSessionContext session)
    {
        _session = session;
        _defaultDomain = $".{session.BaseUri.Host}";
    }

    public async Task<IReadOnlyList<BrowserCookieRecord>> ExportCookiesAsync(CoreWebView2CookieManager cookieManager)
    {
        var cookies = await cookieManager.GetCookiesAsync(string.Empty);
        return cookies
            // 保留已知站点别名的 Cookie 原始域，仍排除第三方 Cookie。
            .Where(cookie => _session.IsTrustedCookieDomain(cookie.Domain))
            .Select(cookie => new BrowserCookieRecord
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = string.IsNullOrWhiteSpace(cookie.Domain) ? _defaultDomain : cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                IsSecure = cookie.IsSecure,
                IsHttpOnly = cookie.IsHttpOnly,
                Expires = cookie.Expires is { } expiresDateTime && expiresDateTime != default
                    ? new DateTimeOffset(expiresDateTime).ToUnixTimeSeconds()
                    : null
            })
            .ToList();
    }

    public string BuildCookieHeader(IEnumerable<BrowserCookieRecord> cookies)
    {
        // A raw Cookie header has no domain metadata: scope it to the actual request origin.
        return CreateCookieContainer(cookies).GetCookieHeader(_session.BaseUri);
    }

    public bool HasValidClearance(IEnumerable<BrowserCookieRecord> cookies)
    {
        var minimumExpiry = DateTime.UtcNow.AddSeconds(30);
        return CreateCookieContainer(cookies).GetCookies(_session.BaseUri).Cast<Cookie>().Any(cookie =>
            cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cookie.Value) &&
            (cookie.Expires == DateTime.MinValue || cookie.Expires.ToUniversalTime() > minimumExpiry));
    }

    public CookieContainer CreateCookieContainer(IEnumerable<BrowserCookieRecord> cookies)
    {
        var container = new CookieContainer();
        foreach (var record in cookies)
        {
            if (!_session.IsTrustedCookieDomain(record.Domain) ||
                string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Value))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(record.Name, record.Value, string.IsNullOrWhiteSpace(record.Path) ? "/" : record.Path, NormalizeDomain(record.Domain))
                {
                    Secure = record.IsSecure,
                    HttpOnly = record.IsHttpOnly
                };
                if (record.Expires is > 0 and <= 253402300799)
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)record.Expires.Value).UtcDateTime;
                }
                container.Add(cookie);
            }
            catch (CookieException ex)
            {
                AppLogger.Info("cookie", $"跳过无效 Cookie '{record.Name}': {ex.Message}");
            }
        }
        return container;
    }

    private string NormalizeDomain(string? domain)
    {
        return string.IsNullOrWhiteSpace(domain) ? _defaultDomain : domain.StartsWith('.') ? domain : $".{domain}";
    }
}
