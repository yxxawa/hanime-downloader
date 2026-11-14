using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hanime1Downloader.CSharp;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static partial class Program
{
    private static async Task CheckQueueDetailNamingAsync()
    {
        const string fullTitle = "【制作组】山间散步";
        const string released = "2025-10-14";
        const string rule = @"{author}\{date}-{name}-{quality}";
        var fileName = $"{released}-山间散步-720p.mp4";
        var requestedMedia = new List<string>();
        using var client = Client(request =>
        {
            requestedMedia.Add(request.RequestUri!.AbsolutePath);
            return request.Headers.Range is not null ? Partial(0, [1], 5) : Response(HttpStatusCode.OK, Bytes("NAMED"));
        });
        var window = Setup(client, 1, "full-detail-naming");
        var browser = new CloudflareWindow("queue-naming.example.invalid");
        try
        {
            var settings = (AppSettings)Field(window, "_settings")!;
            settings.SiteHost = browser.SessionBaseUri.Host;
            settings.DefaultQuality = "720";
            settings.FileNamingRule = rule;
            var target = Path.Combine(settings.DownloadPath, "制作组", fileName);
            var api = new HanimeApiClient(browser, client);
            SetField(window, "_apiClient", api);
            var watchCount = 0;
            SeedHtml(api, browser, "watch?v=101", () =>
            {
                watchCount++;
                return Task.FromResult(new BrowserFetchResult
                {
                    Status = 200, Url = new Uri(browser.SessionBaseUri, "watch?v=101").AbsoluteUri,
                    Html = $"<html><title>短标题</title><body><div id='shareBtn-title'>{fullTitle}</div>" +
                           $"<div class='video-description-panel'>{released}</div><video id='player'>" +
                           $"<source size='1080' src='{Host}101-1080.mp4' type='video/mp4'/>" +
                           $"<source size='720' src='{Host}101-720.mp4' type='video/mp4'/></video></body></html>"
                });
            });
            var summary = new VideoSummary { VideoId = "101", Title = "列表短标题", Url = new Uri(browser.SessionBaseUri, "watch?v=101").AbsoluteUri };
            var queue = (IList)Field(window, "_downloadQueue")!;
            await ((Task)Call(window, "QueueVideosForDownloadAsync", new[] { summary }, false)!).WaitAsync(TimeoutForTest);
            var fromMenu = queue.Cast<object>().Single();
            Console.WriteLine($"QUEUE-NAMING watch={watchCount}; title={Get(fromMenu, "Title")}; file={Path.GetFileName((string)Get(fromMenu, "TargetPath")!)}");
            Check(watchCount == 1 && (string?)Get(fromMenu, "Title") == fullTitle &&
                  (string?)Get(fromMenu, "TargetPath") == target && requestedMedia.Count == 0,
                "right-click queueing reads the watch title, date and preferred quality before rendering the saved path");
            Check((string?)Get(fromMenu, "PlannedFileName") == fileName && (int?)Get(fromMenu, "Quality") == 720,
                "queue shows the customized file name without replacing the full title");

            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1000, 700));
            root.Arrange(new Rect(0, 0, 1000, 700));
            root.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var queueList = (ListBox)window.FindName("DownloadQueueList");
            var row = (ListBoxItem)queueList.ItemContainerGenerator.ContainerFromItem(fromMenu)!;
            var text = Descendants(row).OfType<TextBlock>().SingleOrDefault(block => block.Text == $"\u6587\u4ef6\u540d\uff1a{fileName}");
            Check(text is { Visibility: Visibility.Visible } && (string?)text.ToolTip == target,
                "the actual queue row renders the customized name and exposes the full path");

            var cache = (Dictionary<string, VideoDetails>)Field(window, "_videoDetailsCache")!;
            var details = cache.Values.Single(item => item.VideoId == "101");
            queue.Remove(fromMenu);
            Call(window, "ApplyVideoDetails", details);
            Call(window, "QueueVideoSource", details.Sources.Single(item => item.Quality == 720), false);
            await UntilAsync(() => queue.Count == 1);
            var fromDetails = queue.Cast<object>().Single();
            Check((string?)Get(fromDetails, "Title") == (string?)Get(fromMenu, "Title") &&
                  (string?)Get(fromDetails, "TargetPath") == (string?)Get(fromMenu, "TargetPath") && watchCount == 1,
                "the detail-source button and right-click produce the same title and path for the same quality");

            SeedHtml(api, browser, "watch?v=102", () => Task.FromException<BrowserFetchResult>(new IOException("Synthetic detail failure")));
            await ((Task)Call(window, "QueueVideosForDownloadAsync", new[] { Summary("102") }, false)!).WaitAsync(TimeoutForTest);
            Check(queue.Count == 1 && ((TextBlock)window.FindName("StatusText")).Text.Contains("未加入", StringComparison.Ordinal),
                "unavailable details do not silently create a short-title queue entry");

            await Start(window).WaitAsync(TimeoutForTest);
            Check(queue.Count == 0 && watchCount == 1 && File.ReadAllText(target) == "NAMED" &&
                  requestedMedia.SequenceEqual(new[] { "/101-720.mp4", "/101-720.mp4" }),
                "download writes the same customized path from the already resolved detail metadata");
        }
        finally { browser.Close(); window.Close(); }
    }

    private static void SeedHtml(HanimeApiClient api, CloudflareWindow browser, string route, Func<Task<BrowserFetchResult>> handler)
    {
        var inFlight = (ConcurrentDictionary<string, Lazy<Task<BrowserFetchResult>>>)typeof(HanimeApiClient)
            .GetField("_htmlInFlight", PrivateInstance)!.GetValue(api)!;
        inFlight[new Uri(browser.SessionBaseUri, route).AbsoluteUri] = new Lazy<Task<BrowserFetchResult>>(handler);
    }
}
