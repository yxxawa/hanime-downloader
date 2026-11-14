using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;

internal static partial class Program
{
    private static async Task CheckChallengeFastPathAsync()
    {
        var observed = Stopwatch.GetTimestamp();
        var marker = new CloudflareChallengeObservation("browser-fetch-headers", observed, observed);
        var confirmed = new BrowserFetchResult { Status = 200, Challenge = marker };
        var browser = Browser(confirmed, Good);
        var failure = await CaptureChallengeAsync(() => new HanimeApiClient(browser).SearchAsync("header-fast"));
        Check(failure?.Observation == marker && failure.RequestUrl == "https://hanime1.com/search?query=header-fast&page=1" &&
              browser.PageCalls == 1 && browser.RecoverCalls == 0 && browser.NavigateCalls == 0,
            "confirmed browser response header preserves the failed URL and skips fallback even at HTTP 200");

        browser = Browser(null, confirmed);
        failure = await CaptureChallengeAsync(() => new HanimeApiClient(browser).SearchAsync("recovered-header"));
        Check(failure?.Observation == marker && browser.RecoverCalls == 1 && browser.PageCalls == 2 && browser.NavigateCalls == 0,
            "confirmed header on the recovered browser request is not swallowed by fallback");

        browser = Browser(confirmed, AccountPage);
        failure = await CaptureChallengeAsync(() => new HanimeAccountService(browser, "hanime1.com").GetCurrentAccountAsync());
        Check(failure?.Observation == marker && browser.PageCalls == 1 && browser.RecoverCalls == 0 && browser.NavigateCalls == 0,
            "account page reads also propagate confirmed browser challenges immediately");

        var url = new Uri("https://hanime1.com/search?query=probe&page=1");
        Check(!VerificationRequestPolicy.IsAccepted(new BrowserFetchResult { Status = 200, Url = url.AbsoluteUri, Challenge = marker }, url),
            "an HTTP 200 challenge header cannot confirm original-request recovery");

        using (var handler = new HeaderOnlyHandler())
        using (var http = new HttpClient(handler))
        {
            browser = Browser(null, Good);
            var result = await new HanimeApiClient(browser, http).SearchAsync("native-header");
            Check(result.Results.Count == 1 && handler.BodyReads == 0 && browser.RecoverCalls == 1 && browser.NavigateCalls == 0,
                "direct HTTP detects the header without reading the body but still prefers a healthy browser");
        }

        using (var handler = new HeaderOnlyHandler())
        using (var http = new HttpClient(handler))
        {
            browser = Browser((BrowserFetchResult?)null);
            browser.RecoverySucceeds = false;
            browser.NavigationHandler = (_, _) => Task.FromException<BrowserFetchResult>(new TimeoutException("navigation unavailable"));
            failure = await CaptureChallengeAsync(() => new HanimeApiClient(browser, http).SearchAsync("native-evidence"));
            Check(failure?.Observation is { Source: "http-headers", HeadersObservedAt: not null } && handler.BodyReads == 0,
                "confirmed direct header remains available if browser fallback fails");
        }

        using (var handler = new HeaderOnlyHandler(new Uri("https://untrusted.invalid/search")))
        using (var http = new HttpClient(handler))
        {
            browser = Browser(null, Good);
            var result = await new HanimeApiClient(browser, http).SearchAsync("foreign-header");
            Check(result.Results.Count == 1 && handler.BodyReads > 0 && browser.RecoverCalls == 1,
                "an unrelated redirect's response header cannot be trusted as a challenge for this site");
        }
    }

    private sealed class HeaderOnlyHandler(Uri? effectiveUri = null) : HttpMessageHandler
    {
        public int BodyReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = effectiveUri is null ? request : new HttpRequestMessage(HttpMethod.Get, effectiveUri),
                Content = new UnreadableContent(() => BodyReads++)
            };
            response.Headers.TryAddWithoutValidation("CF-Mitigated", "Challenge");
            return Task.FromResult(response);
        }
    }

    private sealed class UnreadableContent(Action onRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            onRead();
            return Task.FromException(new IOException("Test body must not be read on the fast path."));
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
