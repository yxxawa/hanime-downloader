using System.Net;
using System.Net.Http;
using Hanime1Downloader.CSharp.Models;

namespace Hanime1Downloader.CSharp.Services;

public sealed class HanimeHttpClientFactory
{
    public HttpClient Create(IReadOnlyList<BrowserCookieRecord> cookies, string siteHost = "hanime1.me", string? browserVersion = null)
    {
        var bridge = new CookieSessionBridge(siteHost);
        var handler = new HttpClientHandler
        {
            CookieContainer = bridge.CreateCookieContainer(cookies),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 8
        };

        var baseUrl = $"https://{siteHost}/";
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        var userAgent = BrowserIdentity.BuildUserAgent(browserVersion);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "video/webm,video/ogg,video/*;q=0.9,application/ogg;q=0.7,audio/*;q=0.6,*/*;q=0.5");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", baseUrl);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua", BrowserIdentity.BuildSecChUa(browserVersion));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "video");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        return client;
    }
}
