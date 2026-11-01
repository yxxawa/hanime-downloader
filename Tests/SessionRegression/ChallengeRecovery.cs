using System.Net.Http;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static partial class Program
{
    private static readonly BrowserFetchResult AccountPage = new()
    {
        Status = 200, Title = "Account",
        Html = "<html><a id='user-modal-trigger' href='/user/7'>Account</a><span id='user-modal-name'>Test</span></html>"
    };

    private static async Task CheckChallengeRecoveryAsync()
    {
        var blockedSamples = new[]
        {
            (Name: "challenge 200", Response: Challenge(200)),
            (Name: "challenge 403", Response: Challenge(403)),
            (Name: "plain 403", Response: new BrowserFetchResult { Status = 403, Title = "Forbidden", Html = "<html>Forbidden</html>" })
        };
        foreach (var sample in blockedSamples)
        {
            {
                var browser = Browser(null, Good);
                using var handler = new Handler(sample.Response);
                using var http = new HttpClient(handler);
                var api = new HanimeApiClient(browser, http);
                var result = await api.SearchAsync("recover-direct");
                Check(result.Results.Count == 1 && handler.Requests.Count == 1 &&
                      browser.RecoverCalls == 1 && browser.PageCalls == 2 && browser.NavigateCalls == 0,
                    $"HTTP {sample.Name} falls back to the healthy browser session");
                await api.SearchAsync("recover-direct");
                Check(browser.PageCalls == 2, $"HTTP {sample.Name}: recovered HTML is cached");
                browser.Responses.Enqueue(null);
                browser.Responses.Enqueue(Good);
                await api.SearchAsync("during-http-cooldown");
                Check(handler.Requests.Count == 1 && browser.RecoverCalls == 2,
                    $"HTTP {sample.Name}: direct HTTP stays in cooldown while browser requests recover");
            }
            {
                var browser = Browser(sample.Response, Good);
                using var handler = new Handler(sample.Response);
                using var http = new HttpClient(handler);
                var result = await new HanimeApiClient(browser, http).SearchAsync("recover-page");
                Check(result.Results.Count == 1 && browser.RecoverCalls == 1 && browser.NavigateCalls == 0 && handler.Requests.Count == 0,
                    $"page {sample.Name} retries the browser without probing direct HTTP");
            }
            {
                var browser = Browser(null, sample.Response);
                browser.NavigationResponse = Good;
                var result = await new HanimeApiClient(browser).SearchAsync("recover-navigation");
                Check(result.Results.Count == 1 && browser.RecoverCalls == 1 && browser.NavigateCalls == 1,
                    $"recovered page {sample.Name} falls back to navigation");
            }
            {
                var browser = Browser(sample.Response, sample.Response);
                browser.NavigationResponse = sample.Response;
                var api = new HanimeApiClient(browser);
                var failure = await CaptureChallengeAsync(() => api.SearchAsync("still-blocked"));
                Check(failure?.RequestUrl == "https://hanime1.com/search?query=still-blocked&page=1" &&
                      browser.RecoverCalls == 1 && browser.PageCalls == 2 && browser.NavigateCalls == 1,
                    $"persistent {sample.Name} preserves the original URL and escalates after bounded attempts");
                browser.Responses.Enqueue(Good);
                var result = await api.SearchAsync("still-blocked");
                Check(result.Results.Count == 1 && browser.PageCalls == 3,
                    $"persistent {sample.Name} is not cached and a later retry can recover");
            }
            {
                var browser = Browser(sample.Response, AccountPage);
                var account = await new HanimeAccountService(browser, "hanime1.com").GetCurrentAccountAsync();
                Check(account?.UserId == "7" && browser.RecoverCalls == 1 && browser.NavigateCalls == 0,
                    $"account {sample.Name} recovers before parsing identity");
            }
            {
                var browser = Browser(null, sample.Response);
                browser.NavigationResponse = AccountPage;
                var account = await new HanimeAccountService(browser, "hanime1.com").GetCurrentAccountAsync();
                Check(account?.UserId == "7" && browser.RecoverCalls == 1 && browser.NavigateCalls == 1,
                    $"account recovered {sample.Name} falls back to navigation");
            }
            {
                var browser = Browser(sample.Response, sample.Response);
                browser.NavigationResponse = sample.Response;
                var failure = await CaptureChallengeAsync(() =>
                    new HanimeAccountService(browser, "hanime1.com").FetchFavoriteVideosAsync("7"));
                Check(failure?.RequestUrl == "https://hanime1.com/user/7/likes?page=1" &&
                      browser.RecoverCalls == 1 && browser.NavigateCalls == 1,
                    $"account persistent {sample.Name} escalates with the exact favorites URL");
            }
        }

        // Both public details paths must benefit from recovery, not only search.
        foreach (var options in new[] { VideoDetailsLoadOptions.Basic, VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources })
        {
            var browser = Browser(Challenge(), Good, Good);
            var details = await new HanimeApiClient(browser).GetDetailsAsync("42", options);
            Check(details?.VideoId == "42" && browser.RecoverCalls == 1 && browser.NavigateCalls == 0,
                $"details recover a challenge for load options {options}");
        }

        foreach (var accountRequest in new[] { false, true })
        {
            foreach (var outcome in new[] { "unavailable", "throws" })
            {
                var browser = Browser(Challenge());
                browser.RecoverySucceeds = false;
                if (outcome == "throws")
                    browser.RecoveryHandler = _ => Task.FromException<bool>(new InvalidOperationException("context lost"));
                browser.NavigationResponse = accountRequest ? AccountPage : Good;
                await FetchForRecoveryTestAsync(browser, accountRequest);
                Check(browser.PageCalls == 1 && browser.RecoverCalls == 1 && browser.NavigateCalls == 1,
                    $"account={accountRequest}: context recovery {outcome} still permits navigation");
            }
            {
                var browser = Browser(Challenge(), Challenge());
                browser.NavigationHandler = (_, _) => Task.FromException<BrowserFetchResult>(new TimeoutException("navigation timed out"));
                var failure = await CaptureChallengeAsync(() => FetchForRecoveryTestAsync(browser, accountRequest));
                var expectedPath = accountRequest ? "/" : "/search?query=fallback-error&page=1";
                Check(failure?.RequestUrl == "https://hanime1.com" + expectedPath,
                    $"account={accountRequest}: navigation timeout cannot erase an observed challenge");
            }
            {
                var browser = Browser(null, null);
                browser.NavigationHandler = (_, _) => Task.FromException<BrowserFetchResult>(new TimeoutException("navigation timed out"));
                var timeout = false;
                try { await FetchForRecoveryTestAsync(browser, accountRequest); }
                catch (TimeoutException) { timeout = true; }
                Check(timeout, $"account={accountRequest}: a transport failure without a challenge remains a transport failure");
            }
            {
                var browser = Browser(Challenge(), Challenge());
                var expected = new CloudflareChallengeException("Cloudflare navigation challenge", "https://hanime1.com/exact-navigation-request");
                browser.NavigationHandler = (_, _) => Task.FromException<BrowserFetchResult>(expected);
                Check(ReferenceEquals(await CaptureChallengeAsync(() => FetchForRecoveryTestAsync(browser, accountRequest)), expected),
                    $"account={accountRequest}: the navigation challenge and its request URL are preserved");
            }
            foreach (var stage in new[] { "page", "recovery", "navigation" })
            {
                using var cancellation = new CancellationTokenSource();
                var browser = Browser(Challenge(), Challenge());
                if (stage == "page")
                    browser.PageHandler = (_, _) => { cancellation.Cancel(); return Task.FromCanceled<BrowserFetchResult?>(cancellation.Token); };
                if (stage == "recovery")
                    browser.RecoveryHandler = _ => { cancellation.Cancel(); return Task.FromCanceled<bool>(cancellation.Token); };
                if (stage == "navigation")
                    browser.NavigationHandler = (_, _) => { cancellation.Cancel(); return Task.FromCanceled<BrowserFetchResult>(cancellation.Token); };
                var canceled = false;
                try { await FetchForRecoveryTestAsync(browser, accountRequest, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && browser.RecoverCalls == (stage == "page" ? 0 : 1) &&
                      browser.NavigateCalls == (stage == "navigation" ? 1 : 0),
                    $"account={accountRequest}: {stage} cancellation is never converted into success or a challenge");
            }
        }
        {
            // The direct response must survive even when no subsequent browser response is available.
            var browser = Browser();
            browser.RecoveryHandler = _ => Task.FromException<bool>(new InvalidOperationException("context lost"));
            browser.NavigationHandler = (_, _) => Task.FromException<BrowserFetchResult>(new TimeoutException("navigation timed out"));
            using var handler = new Handler(Challenge());
            using var http = new HttpClient(handler);
            var failure = await CaptureChallengeAsync(() => new HanimeApiClient(browser, http).SearchAsync("direct-evidence"));
            Check(failure?.RequestUrl == handler.Requests.Single().AbsoluteUri && browser.NavigateCalls == 1,
                "direct HTTP challenge evidence survives failed context recovery and navigation");
        }
        {
            // A canceled caller must not cancel the request shared by another search.
            using var cancellation = new CancellationTokenSource();
            var firstResponse = new TaskCompletionSource<BrowserFetchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var browser = Browser();
            browser.PageHandler = (_, _) => browser.PageCalls == 1 ? firstResponse.Task : Task.FromResult<BrowserFetchResult?>(Good);
            var api = new HanimeApiClient(browser);
            var canceledSearch = api.SearchAsync("shared", cancellationToken: cancellation.Token);
            var survivingSearch = api.SearchAsync("shared");
            cancellation.Cancel();
            var canceled = false;
            try { await canceledSearch; }
            catch (OperationCanceledException) { canceled = true; }
            firstResponse.SetResult(Challenge());
            var result = await survivingSearch;
            Check(canceled && result.Results.Count == 1 && browser.PageCalls == 2 && browser.RecoverCalls == 1 && browser.NavigateCalls == 0,
                "concurrent identical requests share one recovery even when one caller cancels");
        }
    }

    private static async Task FetchForRecoveryTestAsync(CloudflareWindow browser, bool accountRequest, CancellationToken token = default)
    {
        if (accountRequest)
            await new HanimeAccountService(browser, "hanime1.com").GetCurrentAccountAsync(token);
        else
            await new HanimeApiClient(browser).SearchAsync("fallback-error", cancellationToken: token);
    }

    private static async Task<CloudflareChallengeException?> CaptureChallengeAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (CloudflareChallengeException ex) { return ex; }
    }
}
