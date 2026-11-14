using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Hanime1Downloader.CSharp;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static partial class Program
{
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeoutForTest;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for optimization test state.");
            await Task.Delay(15);
        }
    }

    private static byte[] TsPackets(int count, byte marker)
    {
        var bytes = new byte[188 * count];
        for (var offset = 0; offset < bytes.Length; offset += 188)
        {
            bytes[offset] = 0x47;
            bytes[offset + 1] = marker;
        }
        return bytes;
    }

    private sealed class HeldSegment(byte[] data, Task release, TaskCompletionSource waiting) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0 && !release.IsCompleted)
            {
                waiting.TrySetResult();
                await release.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task CheckStreamingHlsAsync()
    {
        const int count = 64;
        var uri = Host + "many-segments.m3u8";
        var list = "#EXTM3U\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"#EXTINF:1,\n{i}.ts\n")) + "#EXT-X-ENDLIST\n";
        var firstPart = TsPackets(1024, 0);
        var release = Signal();
        var waiting = Signal();
        var active = 0;
        var peak = 0;
        var sync = new object();
        using var client = new HttpClient(new Mock(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".m3u8", StringComparison.Ordinal)) return Playlist(list, uri);
            var index = int.Parse(Path.GetFileNameWithoutExtension(path));
            lock (sync) peak = Math.Max(peak, ++active);
            try
            {
                await Task.Delay(10, token);
                if (index != 0) return Response(HttpStatusCode.OK, TsPackets(2, (byte)index), "video/mp2t");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HeldSegment(firstPart, release.Task, waiting))
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp2t");
                return response;
            }
            finally { lock (sync) active--; }
        }));

        var path = Path.Combine(Root, "long-stream.mp4");
        var work = path + ".hls";
        var download = Service(client).DownloadAsync(uri, path);
        try
        {
            await waiting.Task.WaitAsync(TimeoutForTest);
            var partial = Directory.GetFiles(work, "*000000.bin.tmp").SingleOrDefault();
            Check(partial is not null && new FileInfo(partial).Length > 0 && !download.IsCompleted,
                "plain HLS segment is written to disk while its response body is still open");
        }
        finally { release.TrySetResult(); }
        var finalPath = await download.WaitAsync(TimeoutForTest);
        var expected = firstPart.Concat(Enumerable.Range(1, count - 1).SelectMany(i => TsPackets(2, (byte)i)));
        Check(peak is > 1 and <= 4 && File.ReadAllBytes(finalPath).SequenceEqual(expected) && !Directory.Exists(work),
            "long HLS uses bounded concurrent workers and merges in playlist order");

        var plaintext = TsPackets(3, 99);
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] encrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            encrypted = aes.EncryptCbc(plaintext, new byte[16], PaddingMode.PKCS7);
        }
        var rangeList = $"#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x00000000000000000000000000000000\n#EXT-X-BYTERANGE:{encrypted.Length}@4\n#EXTINF:1,\nrange.ts\n#EXT-X-ENDLIST\n";
        using var rangeClient = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/range.m3u8" => Playlist(rangeList, Host + "range.m3u8"),
            "/key.bin" => Response(HttpStatusCode.OK, key, "application/octet-stream"),
            "/range.ts" when request.Headers.Range?.ToString() == $"bytes=4-{3 + encrypted.Length}" =>
                Response(HttpStatusCode.OK, [0, 0, 0, 0, .. encrypted], "video/mp2t"),
            _ => Response(HttpStatusCode.NotFound, [])
        });
        var rangePath = await Service(rangeClient).DownloadAsync(Host + "range.m3u8", Path.Combine(Root, "range.mp4"));
        Check(File.ReadAllBytes(rangePath).SequenceEqual(plaintext),
            "AES-128 and byte-range fallback remain compatible with the streaming merge");
    }

    private static async Task CheckSharedVerificationAsync()
    {
        using var unusedHttp = Client(_ => throw new InvalidOperationException("Unexpected native request in WPF verification test."));
        var window = Setup(unusedHttp, 1, "shared-verification");
        var host = $"shared-verification-{Guid.NewGuid():N}.invalid";
        ((AppSettings)Field(window, "_settings")!).SiteHost = host;
        var browser = new CloudflareWindow(host);
        SetField(window, "_cloudflareWindow", browser);
        var startup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var import = Signal();
        using var cancelA = new CancellationTokenSource();
        using var cancelB = new CancellationTokenSource();
        using var cancelLast = new CancellationTokenSource();
        try
        {
            await ((Task)typeof(CloudflareWindow).GetMethod("EnsureInitializedAsync", PrivateInstance)!.Invoke(browser, null)!).WaitAsync(TimeoutForTest);
            var core = ((WebView2)browser.FindName("Browser")).CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            var navCount = 0;
            var firstProbes = 0;
            var secondProbes = 0;
            core.NavigationStarting += (_, _) => navCount++;
            core.WebResourceRequested += (_, e) =>
            {
                var target = new Uri(e.Request.Uri);
                if (target.Host != host) throw new InvalidOperationException("Unexpected external browser request.");
                var probe = e.Request.Headers.Contains("X-Requested-With");
                var status = 200;
                if (probe && target.AbsolutePath == "/first") { firstProbes++; status = 403; }
                if (probe && target.AbsolutePath == "/second") secondProbes++;
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<html><title>Ready</title><body><p>Ready</p></body></html>")),
                    status, status == 200 ? "OK" : "Forbidden", "Content-Type: text/html\r\nCache-Control: no-store\r\n");
            };
            var cookie = core.CookieManager.CreateCookie("cf_clearance", "synthetic-test", host, "/");
            cookie.Expires = DateTime.UtcNow.AddMinutes(10);
            cookie.IsSecure = true;
            core.CookieManager.AddOrUpdateCookie(cookie);
            await browser.FetchHtmlAsync("previous-healthy-page").WaitAsync(TimeoutForTest);
            var navigationBefore = navCount;
            SetField(window, "_startupSessionTask", startup.Task);
            var finish = (Button)browser.FindName("FinishButton");
            Task<bool> Request(string route, CancellationToken token) => (Task<bool>)Call(window, "EnsureVerifiedSessionAsync",
                "Synthetic test challenge", token, new CloudflareChallengeException("Synthetic challenge", $"https://{host}/{route}"), null)!;

            var first = Request("first", cancelA.Token);
            Check(browser.IsVisible && !finish.IsEnabled && navCount == navigationBefore && !startup.Task.IsCompleted,
                "foreground verification is visible before startup releases the browser, with the old document disabled");
            await (Task)typeof(CloudflareWindow).GetMethod("CompleteVerificationManuallyAsync", PrivateInstance)!.Invoke(browser, null)!;
            Check(!first.IsCompleted, "a healthy old document cannot be manually accepted during the handoff");

            var operation = Field(window, "_sessionVerification");
            var second = Request("second", cancelB.Token);
            cancelA.Cancel();
            await ExpectAsync<OperationCanceledException>(() => first.WaitAsync(TimeoutForTest));
            Check(ReferenceEquals(operation, Field(window, "_sessionVerification")) && !second.IsCompleted && browser.IsVisible,
                "canceling the first waiter leaves the same popup active for the other request");
            startup.TrySetResult("done");
            Check(await second.WaitAsync(TimeoutForTest) && firstProbes == 0 && secondProbes == 1 && navCount == navigationBefore + 1,
                "shared verification confirms remaining requests only and performs one verification navigation");

            SetField(window, "_cachedCookieImportTask", import.Task);
            var last = Request("last", cancelLast.Token);
            Check(browser.IsVisible && !finish.IsEnabled && !import.Task.IsCompleted,
                "foreground popup also precedes a pending cached-Cookie import");
            cancelLast.Cancel();
            await ExpectAsync<OperationCanceledException>(() => last.WaitAsync(TimeoutForTest));
            await UntilAsync(() => !browser.IsVisible);
            Check(Field(window, "_sessionVerification") is null && !import.Task.IsCompleted,
                "canceling the last waiter hides the orphan popup without waiting for background work");
        }
        finally
        {
            cancelA.Cancel(); cancelB.Cancel(); cancelLast.Cancel();
            startup.TrySetResult("done");
            import.TrySetResult();
            browser.Close();
            window.Close();
        }
    }

    private static async Task CheckQueuePersistenceAsync()
    {
        using var unusedHttp = Client(_ => throw new InvalidOperationException("Unexpected network request."));
        var window = Setup(unusedHttp, 1, "queue-persistence");
        try
        {
            var settings = (AppSettings)Field(window, "_settings")!;
            settings.PersistDownloadQueue = true;
            var item = Enqueue(window, "batch", Source("batch"));
            var originalWrite = Field(window, "_downloadQueueWriteTask");
            ((DispatcherTimer)Field(window, "_downloadQueueSaveTimer")!).Interval = TimeSpan.FromMinutes(1);
            for (var i = 0; i < 20; i++)
            {
                Put(item, "Title", $"burst-{i}");
                Call(window, "ScheduleDownloadQueueSave");
            }
            Check(ReferenceEquals(originalWrite, Field(window, "_downloadQueueWriteTask")) && !File.Exists(AppPaths.DownloadQueueFile),
                "automatic state changes are coalesced instead of writing each transition");
            Call(window, "DownloadQueueSaveTimer_OnTick", null, EventArgs.Empty);
            await ((Task)Field(window, "_downloadQueueWriteTask")!).WaitAsync(TimeoutForTest);
            Check(ReadQueueTitle() == "burst-19", "one deferred write captures the newest queue snapshot");

            Put(item, "Title", "queued write");
            var pendingWrite = (Task)Call(window, "SaveDownloadQueueAsync")!;
            settings.PersistDownloadQueue = false;
            var delete = (Task)Call(window, "SaveDownloadQueueAsync")!;
            await Task.WhenAll(pendingWrite, delete).WaitAsync(TimeoutForTest);
            Check(!File.Exists(AppPaths.DownloadQueueFile), "turning persistence off cannot be undone by an earlier pending write");

            settings.PersistDownloadQueue = true;
            pendingWrite = (Task)Call(window, "SaveDownloadQueueAsync")!;
            Put(item, "Title", "final close snapshot");
            Call(window, "ScheduleDownloadQueueSave");
            window.Close();
            Check(pendingWrite.IsCompletedSuccessfully && ReadQueueTitle() == "final close snapshot",
                "window close commits the latest snapshot after older writes without waiting for the UI timer");
        }
        finally { if (Field(window, "_isWindowClosing") is false) window.Close(); }
    }

    private static string? ReadQueueTitle()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(AppPaths.DownloadQueueFile));
        return json.RootElement[0].GetProperty("Title").GetString();
    }
}
