using System.Net;
using Hanime1Downloader.CSharp.Models;
using Microsoft.Web.WebView2.Core;

namespace Hanime1Downloader.CSharp.Services;

public sealed class CookieSessionBridge(string siteHost = "hanime1.me")
{
    private readonly string _defaultDomain = $".{siteHost}";

    public async Task<IReadOnlyList<BrowserCookieRecord>> ExportCookiesAsync(CoreWebView2CookieManager cookieManager)
    {
        var cookies = await cookieManager.GetCookiesAsync(string.Empty);
        return cookies
            // 只导出本站域名的 Cookie，避免第三方 Cookie 被持久化到磁盘（隐私泄漏）。
            .Where(cookie => string.IsNullOrWhiteSpace(cookie.Domain) ||
                             cookie.Domain.Equals(siteHost, StringComparison.OrdinalIgnoreCase) ||
                             cookie.Domain.EndsWith("." + siteHost, StringComparison.OrdinalIgnoreCase))
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
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    public CookieContainer CreateCookieContainer(IEnumerable<BrowserCookieRecord> cookies)
    {
        var container = new CookieContainer();
        foreach (var record in cookies)
        {
            if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Value))
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
