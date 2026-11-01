using System.Net.Http;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;

internal static partial class Program
{
    private static async Task CheckFirstRequestAsync()
    {
        var browser = Browser(Good);
        browser.HasSessionCookies = false;
        using var handler = new Handler(Good);
        using var http = new HttpClient(handler);
        var api = new HanimeApiClient(browser, http);
        var search = await CaptureChallengeAsync(() => api.SearchAsync("first"));
        Check(search?.RequestUrl == "https://hanime1.com/search?query=first&page=1",
            "first search without a valid Cookie immediately requests verification of the search URL");
        var details = await CaptureChallengeAsync(() => api.GetDetailsAsync("42", VideoDetailsLoadOptions.Basic));
        Check(details?.RequestUrl == "https://hanime1.com/watch?v=42",
            "details without a valid Cookie immediately request verification");
        var account = await CaptureChallengeAsync(() => new HanimeAccountService(browser, "hanime1.com").FetchFavoriteVideosAsync("7"));
        Check(account?.RequestUrl == "https://hanime1.com/user/7/likes?page=1",
            "account requests without a valid Cookie immediately request verification");
        Check(browser.PageCalls == 0 && browser.RecoverCalls == 0 && browser.NavigateCalls == 0 && handler.Requests.Count == 0,
            "missing Cookie does not enter HTTP retries, page recovery, or navigation waits");
        browser.HasSessionCookies = true;
        var result = await api.SearchAsync("first");
        Check(result.Results.Count == 1 && browser.PageCalls == 1,
            "the original search resumes after a Cookie is available; preflight failure is not cached");
    }
}
