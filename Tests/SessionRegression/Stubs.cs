using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;

// Offline boundary only: policy, cookies, API and account implementation are production source.
namespace Hanime1Downloader.CSharp.Views
{
    public sealed class CloudflareWindow(string host = "hanime1.com")
    {
        public SiteSessionContext Session { get; } = new(host);
        public Uri SessionBaseUri => Session.BaseUri;
        public Queue<BrowserFetchResult?> Responses { get; } = new();
        public int PageCalls, RecoverCalls, NavigateCalls;
        public bool HasSessionCookies { get; set; } = true;
        public Task<bool> HasValidSessionCookiesAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(HasSessionCookies);
        }
        public bool RecoverySucceeds = true;
        public BrowserFetchResult NavigationResponse { get; set; } = new() { Status = 200, Html = "<html><title>OK</title></html>" };
        public Func<string, CancellationToken, Task<BrowserFetchResult?>>? PageHandler { get; set; }
        public Func<CancellationToken, Task<bool>>? RecoveryHandler { get; set; }
        public Func<string, CancellationToken, Task<BrowserFetchResult>>? NavigationHandler { get; set; }
        public async Task<BrowserFetchResult?> TryFetchHtmlInPageAsync(string url, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            PageCalls++;
            var result = PageHandler is not null
                ? await PageHandler(url, token)
                : Responses.Count > 0 ? Responses.Dequeue() : null;
            return result is null ? null : WithRequestUrl(result, url);
        }
        public Task<bool> TryRecoverPageContextAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            RecoverCalls++;
            return RecoveryHandler?.Invoke(token) ?? Task.FromResult(RecoverySucceeds);
        }
        public async Task<BrowserFetchResult> FetchHtmlAsync(string url, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            NavigateCalls++;
            var result = NavigationHandler is not null ? await NavigationHandler(url, token) : NavigationResponse;
            return WithRequestUrl(result, url);
        }
        private BrowserFetchResult WithRequestUrl(BrowserFetchResult response, string url) => new()
        {
            Status = response.Status, Html = response.Html, Title = response.Title,
            Url = string.IsNullOrEmpty(response.Url) ? new Uri(SessionBaseUri, url).AbsoluteUri : response.Url
        };
        public Task<BrowserFetchResult?> PostFormInPageAsync(string url, IReadOnlyDictionary<string, string> fields,
            string? csrfToken = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
namespace Hanime1Downloader.CSharp.Services
{
    public static class AppLogger
    {
        public static readonly List<string> Messages = [];
        public static void Info(string category, string message) { Messages.Add(message); }
        public static void InfoThrottled(string category, string message, TimeSpan interval) { Messages.Add(message); }
    }
}
