using System.Net;
using System.Net.Http;
using System.Reflection;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static class Program
{
    private static int _passes;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BrowserFetchResult Good = new() { Status = 200, Html = "<html><title>OK</title><a href='/watch?v=1'>Result</a></html>", Title = "OK" };
    private static BrowserFetchResult Challenge(int status = 403) => new() { Status = status, Html = "<html><title>Just a moment...</title><script>window._cf_chl_opt={};</script></html>", Title = "Just a moment..." };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _passes++;
        Console.WriteLine("PASS: " + message);
    }
    private static BrowserCookieRecord Cookie(string domain, string name = "cf_clearance", double? expires = null, string path = "/") =>
        new() { Domain = domain, Name = name, Value = domain.Contains(".me") ? "test-me" : "test-com", Expires = expires, Path = path, IsSecure = true };
    private static CloudflareWindow Browser(params BrowserFetchResult?[] responses)
    {
        var browser = new CloudflareWindow();
        foreach (var response in responses) browser.Responses.Enqueue(response);
        return browser;
    }
    private static async Task<bool> Blocked(Func<Task> action)
    {
        try { await action(); return false; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Cloudflare")) { return true; }
    }
    private static Task<BrowserFetchResult> Raw(HanimeApiClient api, string url) =>
        (Task<BrowserFetchResult>)typeof(HanimeApiClient).GetMethod("FetchHtmlAsync", Private)!.Invoke(api, [url, CancellationToken.None, false])!;

    private static async Task Main()
    {
        var session = new SiteSessionContext("hanime1.com");
        Check(session.BaseUri.Host == "hanime1.com", "configured host initializes request origin");
        foreach (var url in new[] { "https://hanime1.me/", "https://www.hanime1.me/search?q=x", "https://www.hanime1.com/", "https://hanime1.com/" })
        {
            Check(session.TryUpdateFromNavigation(url), "known alias accepted: " + new Uri(url).Host);
            Check(session.BaseUri == new Uri(new Uri(url).GetLeftPart(UriPartial.Authority) + "/"), "effective origin follows actual navigation, with path removed");
        }
        foreach (var url in new[] { "http://hanime1.me/", "https://hanime1.me:444/", "https://hanime1.me.evil.invalid/", "https://evilhanime1.me/", "https://sub.hanime1.me/", "https://hanimeone.me/", "https://javchu.com/", "https://user@hanime1.me/", "about:blank", "/relative" })
        {
            var before = session.BaseUri;
            Check(!session.TryUpdateFromNavigation(url) && session.BaseUri == before, "untrusted navigation cannot change session: " + url);
        }
        var custom = new SiteSessionContext("custom.invalid");
        Check(custom.TryUpdateFromNavigation("https://www.custom.invalid/"), "custom host keeps its www variant");
        Check(!custom.TryUpdateFromNavigation("https://hanime1.me/"), "custom host cannot adopt public site aliases");
        Check(!custom.IsTrustedCookieDomain(".hanime1.me"), "custom host excludes public alias cookies");
        Check(!new SiteSessionContext("hanimeone.me").TryUpdateFromNavigation("https://hanime1.me/"), "other preset is not implicitly a trusted alias");
        foreach (var domain in new[] { ".hanime1.com", "hanime1.me", ".www.hanime1.me" })
            Check(session.IsTrustedCookieDomain(domain), "first-party cookie accepted without relabeling: " + domain);
        foreach (var domain in new[] { ".com", ".me", ".evilhanime1.me", ".hanime1.me.evil.invalid", ".thirdparty.invalid" })
            Check(!session.IsTrustedCookieDomain(domain), "third-party cookie rejected: " + domain);

        var bridge = new CookieSessionBridge(session);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cookies = new[] { Cookie(".hanime1.com", expires: now + 600), Cookie(".hanime1.me", expires: now + 600),
            Cookie(".thirdparty.invalid", "unrelated"), Cookie(".hanime1.me", "expired", now - 60), Cookie(".hanime1.me", "private_path", path: "/account") };
        Check(bridge.BuildCookieHeader(cookies) == "cf_clearance=test-com", "com raw header contains only com cookies");
        session.TryUpdateFromNavigation("https://hanime1.me/");
        Check(bridge.BuildCookieHeader(cookies) == "cf_clearance=test-me", "me raw header excludes com, expired, and wrong-path cookies");
        Check(bridge.HasValidClearance(cookies), "effective me clearance recognized");
        Check(!bridge.HasValidClearance([Cookie(".hanime1.com")]), "old com clearance cannot validate a me session");
        Check(!bridge.HasValidClearance([Cookie(".hanime1.me", expires: now - 60)]), "expired clearance cannot validate session");
        Check(!bridge.HasValidClearance([Cookie(".hanime1.me", expires: now + 5)]), "nearly expired clearance cannot validate session");
        Check(!bridge.HasValidClearance([Cookie(".hanime1.me", path: "/account")]), "wrong-path clearance cannot validate root session");
        Check(bridge.HasValidClearance([Cookie(".hanime1.me")]), "session clearance remains supported");
        var container = bridge.CreateCookieContainer(cookies);
        Check(container.GetCookieHeader(new Uri("https://hanime1.com/")) == "cf_clearance=test-com", "CookieContainer preserves original com scope");
        Check(container.GetCookieHeader(new Uri("https://hanime1.me/")) == "cf_clearance=test-me", "CookieContainer preserves original me scope");
        Check(container.GetCookieHeader(new Uri("https://thirdparty.invalid/")) == "", "CookieContainer excludes unrelated cookies");
        Check(container.GetCookieHeader(new Uri("http://hanime1.me/")) == "", "secure cookies are not sent over HTTP");
        Check(container.GetCookieHeader(new Uri("https://hanime1.me/account" )).Contains("private_path="), "cookie path preserved");
        Check(cookies[0].Domain == ".hanime1.com" && cookies[1].Domain == ".hanime1.me", "input cookie domains remain unchanged");
        Check(bridge.BuildCookieHeader([Cookie("")]) == "", "legacy domainless cookie stays on configured host after redirect");
        session.TryUpdateFromNavigation("https://www.hanime1.me/");
        Check(bridge.BuildCookieHeader(cookies) == "cf_clearance=test-me", "domain cookie works on actual www origin");

        // Same client survives a normal redirect, including cache keys, HTTP target/referrer, and output links.
        var browser = Browser(null, null);
        using (var handler = new Handler(Good))
        using (var http = new HttpClient(handler))
        {
            var api = new HanimeApiClient(browser, http, "hanime1.com");
            var account = new HanimeAccountService(browser, "hanime1.com");
            await Raw(api, "search?query=same");
            Check(handler.Requests.Single().Host == "hanime1.com", "initial API request uses configured origin");
            browser.Session.TryUpdateFromNavigation("https://hanime1.me/");
            await Raw(api, "search?query=same");
            Check(handler.Requests.Count == 2 && handler.Requests.Last().Host == "hanime1.me", "redirect changes request target and cache key without recreating API client");
            Check(handler.Referrers.Last()?.Host == "hanime1.me", "HTTP referrer matches effective origin");
            Check((string)typeof(HanimeApiClient).GetProperty("_siteBase", Private)!.GetValue(api)! == "https://hanime1.me", "search/detail link base follows verified origin");
            Check((string)typeof(HanimeAccountService).GetProperty("_siteBase", Private)!.GetValue(account)! == "https://hanime1.me", "account/favorites link base follows verified origin");
        }
        foreach (var status in new[] { 200, 403 })
        {
            var b = Browser(null, Good);
            using var handler = new Handler(Challenge(status));
            using var http = new HttpClient(handler);
            Check(await Blocked(() => new HanimeApiClient(b, http).SearchAsync("blocked")), "HTTP challenge still reaches verification handler: " + status);
            Check(handler.Requests.Count == 1 && b.RecoverCalls == 0, "existing challenge escalation policy unchanged: " + status);
        }
        {
            var b = Browser(Challenge());
            using var handler = new Handler(Good);
            using var http = new HttpClient(handler);
            Check(await Blocked(() => new HanimeApiClient(b, http).SearchAsync("blocked")) && handler.Requests.Count == 0,
                "page challenge still escalates rather than being suppressed");
        }
        {
            var b = Browser(Good);
            using var handler = new Handler(Challenge());
            using var http = new HttpClient(handler);
            Check((await Raw(new HanimeApiClient(b, http), "search?query=good")).Status == 200 && handler.Requests.Count == 0,
                "healthy browser fetch never falls back to challenged HTTP");
        }
        {
            var b = Browser(null, Good);
            Check((await Raw(new HanimeApiClient(b), "search?query=recover")).Status == 200 && b.RecoverCalls == 1,
                "ordinary context recovery remains available");
        }
        // Receiving a response must not be logged as application-level success.
        foreach (var route in new[] { "page", "http", "recovered" })
        foreach (var sample in new[] {
            (Name: "normal 200", Response: Good, Blocked: false),
            (Name: "plain 403", Response: new BrowserFetchResult { Status = 403, Html = "<html><title>Forbidden</title></html>", Title = "Forbidden" }, Blocked: true),
            (Name: "challenge 403", Response: Challenge(403), Blocked: true),
            (Name: "challenge 200", Response: Challenge(200), Blocked: true)
        })
        {
            AppLogger.Messages.Clear();
            var b = route switch {
                "page" => Browser(sample.Response),
                "recovered" => Browser(null, sample.Response),
                _ => Browser()
            };
            using var handler = new Handler(sample.Response);
            using var http = new HttpClient(handler);
            var api = new HanimeApiClient(b, route == "http" ? http : null);
            var handledCorrectly = sample.Blocked
                ? await Blocked(() => api.SearchAsync("response-log-check"))
                : (await Raw(api, "search?query=response-log-check")).Status == 200;
            var prefix = route switch {
                "page" => "页内 fetch 收到响应:",
                "http" => "直连 HTTP 收到响应:",
                _ => "恢复页面上下文后页内 fetch 收到响应:"
            };
            var receipt = AppLogger.Messages.SingleOrDefault(x => x.StartsWith(prefix));
            Check(handledCorrectly && receipt is not null &&
                  receipt.Contains($"status={sample.Response.Status} blocked={sample.Blocked}") &&
                  AppLogger.Messages.All(x => !x.Contains("成功")) &&
                  handler.Requests.Count == (route == "http" ? 1 : 0) &&
                  b.RecoverCalls == (route == "recovered" ? 1 : 0) && b.NavigateCalls == 0,
                $"{route}: {sample.Name} logs receipt/status/blocking without false success; routing and CF escalation remain unchanged");
        }
        // Auto-completion must confirm the failed endpoint, not just the homepage or a cookie.
        var origin = new SiteSessionContext("hanime1.com");
        origin.TryUpdateFromNavigation("https://hanime1.me/");
        var target = VerificationRequestPolicy.ResolveTarget(origin, "https://hanime1.com/search?query=probe&page=2")!;
        Check(target.AbsoluteUri == "https://hanime1.me/search?query=probe&page=2", "probe follows effective host while preserving original path and query");
        Check(VerificationRequestPolicy.ResolveTarget(origin, "https://external.invalid/search") is null, "recovery cannot probe an unrelated host");
        Check(VerificationRequestPolicy.ResolveTarget(origin, "http://hanime1.me/search") is null, "recovery cannot downgrade HTTPS");
        Check(VerificationRequestPolicy.ResolveTarget(origin, null) == origin.BaseUri, "manual verification checks homepage when no failed request exists");
        BrowserFetchResult ProbeResponse(int status, string? url = null, string? html = null) => new()
        { Status = status, Url = url ?? target.AbsoluteUri, Html = html ?? "<html>OK</html>" };
        Check(VerificationRequestPolicy.IsAccepted(ProbeResponse(200), target), "matching successful original request confirms recovery");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(403), target), "403 keeps verification pending");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(500), target), "server error cannot confirm recovery");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(200, html: "<script>window._cf_chl_opt={};</script>"), target), "HTTP 200 challenge cannot confirm recovery");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(200, "https://hanime1.me/"), target), "successful homepage cannot stand in for failed search");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(200, "https://hanime1.me/login"), target), "login redirect cannot confirm original request");
        Check(!VerificationRequestPolicy.IsAccepted(ProbeResponse(200, "https://external.invalid/search?query=probe&page=2"), target), "redirect to unrelated host cannot confirm recovery");
        Check(!VerificationRequestPolicy.IsAccepted(null, target), "failed or timed-out page fetch keeps window pending");
        var pending = new VerificationRequestSet();
        var calls = new List<string>();
        Check(!await pending.ConfirmAsync((url, ct) => Task.FromResult(true), CancellationToken.None), "empty recovery set is not silently successful");
        pending.Add("search?query=a"); pending.Add("search?query=a");
        Check(await pending.ConfirmAsync((url, ct) => { calls.Add(url); return Task.FromResult(true); }, CancellationToken.None) && calls.Count == 1,
            "identical concurrent failures share one probe");
        Check(pending.IsConfirmed, "confirmation remains current until new failure arrives");
        pending.Add("download?v=1");
        Check(!pending.IsConfirmed, "late failure invalidates confirmation before window closes");
        Check(!await pending.ConfirmAsync((url, ct) => Task.FromResult(url.StartsWith("search")), CancellationToken.None), "one failed original endpoint keeps shared verification pending");
        calls.Clear();
        Check(await pending.ConfirmAsync((url, ct) => { calls.Add(url); return Task.FromResult(true); }, CancellationToken.None) && calls.Count == 2,
            "all pending original requests must pass");
        var inserted = false;
        Check(!await pending.ConfirmAsync((url, ct) => { if (!inserted) { pending.Add("watch?v=2"); inserted = true; } return Task.FromResult(true); }, CancellationToken.None),
            "failure arriving during probe cannot be missed");
        Check(await pending.ConfirmAsync((url, ct) => Task.FromResult(true), CancellationToken.None), "next check includes the newly joined request");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var observed = false;
            try { await pending.ConfirmAsync((url, ct) => Task.FromResult(true), canceled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Check(observed && !pending.IsConfirmed, "canceled confirmation cannot complete verification");
        }
        using (var canceledDuringProbe = new CancellationTokenSource())
        {
            var lastProbe = new VerificationRequestSet();
            lastProbe.Add("search?query=cancel");
            var observed = false;
            try
            {
                await lastProbe.ConfirmAsync((url, ct) =>
                {
                    canceledDuringProbe.Cancel();
                    return Task.FromResult(true);
                }, canceledDuringProbe.Token);
            }
            catch (OperationCanceledException) { observed = true; }
            Check(observed && !lastProbe.IsConfirmed, "cancellation during final probe cannot record a successful confirmation");
        }
        pending.Clear();
        Check(!pending.IsConfirmed, "clearing recovery invalidates the completed snapshot");
        {
            var b = Browser();
            using var handler = new Handler(Challenge()); using var http = new HttpClient(handler);
            CloudflareChallengeException? blocked = null;
            try { await new HanimeApiClient(b, http).SearchAsync("original-request"); }
            catch (CloudflareChallengeException ex) { blocked = ex; }
            Check(blocked?.RequestUrl == handler.Requests.Single().AbsoluteUri, "Cloudflare exception preserves exact blocked request for retry confirmation");
        }
        Console.WriteLine($"TOTAL: {_passes} offline assertions passed. Browser/HTTP are stubbed here; this is not a live-site test.");
    }

    private sealed class Handler(BrowserFetchResult response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<Uri?> Referrers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            Referrers.Add(request.Headers.Referrer);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)response.Status)
            { Content = new StringContent(response.Html), RequestMessage = request });
        }
    }
}
