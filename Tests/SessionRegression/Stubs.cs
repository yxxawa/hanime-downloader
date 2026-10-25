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
        public bool RecoverySucceeds = true;
        public BrowserFetchResult NavigationResponse { get; set; } = new() { Status = 200, Html = "<html><title>OK</title></html>" };
        public Task<BrowserFetchResult?> TryFetchHtmlInPageAsync(string url, CancellationToken token = default)
        {
            PageCalls++;
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : null);
        }
        public Task<bool> TryRecoverPageContextAsync(CancellationToken token = default)
        {
            RecoverCalls++;
            return Task.FromResult(RecoverySucceeds);
        }
        public Task<BrowserFetchResult> FetchHtmlAsync(string url, CancellationToken token = default)
        {
            NavigateCalls++;
            return Task.FromResult(NavigationResponse);
        }
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
