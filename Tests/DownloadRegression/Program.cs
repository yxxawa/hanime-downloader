using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hanime1Downloader.CSharp;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static partial class Program
{
    private const string Host = "https://example.invalid/";
    private const string Video = Host + "video.mp4";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly string Root = Directory.CreateTempSubdirectory("hanime-download-regression-").FullName;
    private static int _passed;

    [STAThread]
    private static int Main()
    {
        try
        {
            // Keep the actual WPF app, but isolate settings, queue, logs, and media from the user's profile.
            var storage = (Lazy<string>)typeof(AppPaths).GetField("DataDirectoryLazy", PrivateStatic)!.GetValue(null)!;
            typeof(Lazy<string>).GetField("_factory", PrivateInstance)!.SetValue(storage, (Func<string>)(() => Root));
            if (AppPaths.DataDirectory != Root) throw new InvalidOperationException("Test storage is not isolated.");
            CheckAtomicSave();
            CheckMp4Async().GetAwaiter().GetResult();
            CheckDiskSpaceAsync().GetAwaiter().GetResult();
            CheckHlsAsync().GetAwaiter().GetResult();
            CheckStreamingHlsAsync().GetAwaiter().GetResult();
            if (!CheckWpfQueue()) return 1;
            Console.WriteLine($"TOTAL: {_passed} offline download and WPF queue assertions passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // Root is the exact directory created by CreateTempSubdirectory for this test run.
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(Root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS: " + message);
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void CheckAtomicSave()
    {
        var path = Path.Combine(Root, "atomic.json");
        var context = new HoldingContext();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                var pending = AtomicFile.WriteAllTextAsync(path, new string('a', 8 * 1024 * 1024));
                AtomicFile.WriteAllText(path, "newer");
                pending.GetAwaiter().GetResult();
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        var completedWithoutUi = Task.WhenAny(done.Task, Task.Delay(3000)).GetAwaiter().GetResult() == done.Task;
        if (!completedWithoutUi)
        {
            // Release an old implementation's continuation instead of leaving a stuck test thread.
            while (context.Work.TryDequeue(out var callback)) callback();
            thread.Join(1000);
        }
        Check(completedWithoutUi && done.Task.IsCompletedSuccessfully && File.ReadAllText(path) == "newer",
            "synchronous close-time save can follow async save on a UI thread without pumping it");
    }

    private sealed class HoldingContext : SynchronizationContext
    {
        public readonly ConcurrentQueue<Action> Work = new();
        public override void Post(SendOrPostCallback callback, object? state) => Work.Enqueue(() => callback(state));
    }

    private sealed class Mock(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Mock((request, _) => Task.FromResult(send(request))));
    private static DownloadService Service(HttpClient client) => new(client, retryPolicy: new DownloadRetryPolicy(1));
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, string type = "video/mp4", string? tag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        return response;
    }

    private static HttpResponseMessage Partial(long offset, byte[] bytes, long total, string? tag = null)
    {
        var response = Response(HttpStatusCode.PartialContent, bytes, tag: tag);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + bytes.Length - 1, total);
        return response;
    }

    private static async Task SeedInterruptedDownloadAsync(string path, string content = "OLD-")
    {
        using var client = Client(_ =>
        {
            var response = Response(HttpStatusCode.OK, Bytes(content), tag: "\"old\"");
            response.Content.Headers.ContentLength = 8;
            return response;
        });
        await ExpectAsync<IOException>(() => Service(client).DownloadAsync(Video, path));
        if (File.Exists(path) || File.ReadAllText(path + ".tmp") != content)
            throw new InvalidOperationException("Fixture did not leave an interrupted download.");
    }

    private static async Task CheckMp4Async()
    {
        var path = Path.Combine(Root, "resume.mp4");
        await SeedInterruptedDownloadAsync(path);
        var resumed = false;
        using (var client = Client(request =>
        {
            resumed = request.Headers.Range?.ToString() == "bytes=4-" && request.Headers.IfRange?.EntityTag?.Tag == "\"old\"";
            return Partial(4, Bytes("TAIL"), 8, "\"old\"");
        }))
        {
            await Service(client).DownloadAsync(Video, path);
        }
        Check(resumed && File.ReadAllText(path) == "OLD-TAIL" && !File.Exists(path + ".tmp.resume.tmp"),
            "same-resource MP4 resumes with If-Range and removes its validator on completion");

        foreach (var (name, status) in new[] { ("changed-206", HttpStatusCode.PartialContent), ("changed-416", HttpStatusCode.RequestedRangeNotSatisfiable) })
        {
            path = Path.Combine(Root, name + ".mp4");
            await SeedInterruptedDownloadAsync(path);
            var requests = new List<string?>();
            using var client = Client(request =>
            {
                requests.Add(request.Headers.Range?.ToString());
                if (request.Headers.Range is null) return Response(HttpStatusCode.OK, Bytes("NEW-DATA"), tag: "\"new\"");
                if (status == HttpStatusCode.PartialContent) return Partial(4, Bytes("DATA"), 8, "\"new\"");
                var response = Response(status, [], tag: "\"new\"");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4);
                return response;
            });
            await Service(client).DownloadAsync(Video, path);
            Check(requests.SequenceEqual(new[] { "bytes=4-", null }) && File.ReadAllText(path) == "NEW-DATA",
                $"{name} restarts instead of publishing stale or mixed data");
        }

        path = Path.Combine(Root, "legacy.mp4");
        await File.WriteAllTextAsync(path + ".tmp", "LEGACY");
        using (var client = Client(request => request.Headers.Range is null
            ? Response(HttpStatusCode.OK, Bytes("FRESH")) : Response(HttpStatusCode.BadRequest, [])))
        {
            var service = Service(client);
            await service.DownloadAsync(Video, path);
            Check(File.ReadAllText(path) == "FRESH", "legacy partial without a validator is replaced, never appended");
            await ExpectAsync<InvalidOperationException>(() => service.DownloadAsync(Video, path));
            Check(File.ReadAllText(path) == "FRESH", "download service does not overwrite an existing result");
        }
    }

    private static async Task CheckDiskSpaceAsync()
    {
        const long MiB = 1024 * 1024;
        var path = Path.Combine(Root, "space.mp4");
        await using (var partial = File.Create(path + ".tmp")) partial.SetLength(16 * MiB);
        await File.WriteAllTextAsync(path + ".tmp.resume.tmp", JsonSerializer.Serialize(new
        {
            RequestUrl = Video, ResponseUrl = Video, EntityTag = "\"same\"", TotalBytes = (long?)null
        }));
        var total = 0L;
        using var client = Client(request =>
        {
            if (request.Headers.Range?.FromFirst() != 16 * MiB) return Response(HttpStatusCode.BadRequest, []);
            var free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
            // Whole file plus margin exceeds free space, but the remainder plus margin fits.
            total = free - 64 * MiB + 8 * MiB;
            return Partial(16 * MiB, [1], total, "\"same\"");
        });
        await ExpectAsync<IOException>(() => Service(client).DownloadAsync(Video, path));
        Check(total > 16 * MiB && new FileInfo(path + ".tmp").Length == 16 * MiB + 1,
            "space preflight permits a verified resume if the remaining bytes fit");
    }

    private static long? FromFirst(this RangeHeaderValue header) => header.Ranges.FirstOrDefault()?.From;

    private static async Task CheckHlsAsync()
    {
        const string Cdn = "https://cdn.example.invalid/";
        var requests = new ConcurrentQueue<string>();
        var ts = new byte[188 * 3];
        ts[0] = ts[188] = ts[376] = 0x47;
        using var client = Client(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Enqueue(url);
            return url switch
            {
                Host + "entry/master.m3u8" => Playlist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nvideo/index.m3u8\n", Cdn + "redirect/master.m3u8"),
                Cdn + "redirect/video/index.m3u8" => Playlist("#EXTM3U\n#EXTINF:1,\nseg.ts\n#EXT-X-ENDLIST\n", Cdn + "media/index.m3u8"),
                Cdn + "media/seg.ts" => Response(HttpStatusCode.OK, ts, "video/mp2t"),
                _ => Response(HttpStatusCode.NotFound, [])
            };
        });
        var service = Service(client);
        var logicalPath = Path.Combine(Root, "stream.mp4");
        var probe = await service.ProbeAsync(Host + "entry/master.m3u8");
        var finalPath = await service.DownloadAsync(Host + "entry/master.m3u8", logicalPath, preloadedPlaylist: probe.Playlist);
        Check(requests.SequenceEqual(new[] { Host + "entry/master.m3u8", Cdn + "redirect/video/index.m3u8", Cdn + "media/seg.ts" }),
            "preloaded master and variant resolve against their effective redirect URLs");
        Check(finalPath == Path.ChangeExtension(logicalPath, ".ts") && File.ReadAllBytes(finalPath).SequenceEqual(ts) && !File.Exists(logicalPath),
            "MPEG-TS HLS is saved as TS, not falsely labeled MP4");

        byte[] init = [0, 0, 0, 12, .. "ftyp"u8, .. "isom"u8];
        byte[] fragment = [0, 0, 0, 8, .. "moof"u8];
        using var mp4Client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/movie.m3u8" => Playlist("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\nfrag.m4s\n#EXT-X-ENDLIST\n", Host + "movie.m3u8"),
            "/init.mp4" => Response(HttpStatusCode.OK, init),
            "/frag.m4s" => Response(HttpStatusCode.OK, fragment),
            _ => Response(HttpStatusCode.NotFound, [])
        });
        logicalPath = Path.Combine(Root, "movie.MP4");
        finalPath = await Service(mp4Client).DownloadAsync(Host + "movie.m3u8", logicalPath);
        Check(finalPath == logicalPath && File.ReadAllBytes(finalPath).SequenceEqual(init.Concat(fragment)),
            "fragmented MP4 retains the MP4 extension and merges init before media");
    }

    private static HttpResponseMessage Playlist(string content, string effectiveUrl)
    {
        var response = Response(HttpStatusCode.OK, Bytes(content), "application/vnd.apple.mpegurl");
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, effectiveUrl);
        return response;
    }

    private static bool CheckWpfQueue()
    {
        // The test executable is the entry assembly, but the real app owns its pack resources.
        typeof(Application).GetFields(PrivateStatic).Single(field => field.Name.Equals("_resourceAssembly", StringComparison.OrdinalIgnoreCase))
            .SetValue(null, typeof(MainWindow).Assembly);
        var app = new App();
        app.InitializeComponent();
        typeof(Application).GetFields(PrivateInstance).Single(field => field.Name.Equals("_startupUri", StringComparison.OrdinalIgnoreCase))
            .SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var succeeded = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await CheckQualityCollisionAsync();
                await CheckHlsHistoryAsync();
                await CheckPauseAsync();
                await CheckRecoveryFailureAsync();
                await CheckSharedVerificationAsync();
                await CheckContextMenusAsync();
                await CheckQueueDetailNamingAsync();
                await CheckQueuePersistenceAsync();
                succeeded = true;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                app.Shutdown();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return succeeded;
    }

    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window);
    private static void SetField(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
    private static object? Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethods(PrivateInstance)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    private static object? Get(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item);
    private static void Put(object item, string name, object value) => item.GetType().GetProperty(name)!.SetValue(item, value);
    private static Task Start(MainWindow window) => (Task)Call(window, "StartQueueDownloadAsync", (object?)null)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static VideoSource Source(string id, int quality = 720) => new() { Quality = quality, Type = "mp4", Url = Host + $"{id}-{quality}.mp4" };
    private static TimeSpan TimeoutForTest => TimeSpan.FromSeconds(10);

    private static MainWindow Setup(HttpClient client, int concurrency, string folder)
    {
        var window = new MainWindow { Left = 0, Top = 0, Width = 1000, Height = 700 };
        var settings = (AppSettings)Field(window, "_settings")!;
        settings.PersistDownloadQueue = false;
        settings.DownloadPath = Path.Combine(Root, folder);
        settings.MaxConcurrentDownloads = concurrency;
        settings.MaxRetries = 0;
        SetField(window, "_apiClient", new HanimeApiClient(null!, client)); // All lookups are served by the seeded details cache.
        SetField(window, "_downloadService", Service(client));
        return window;
    }

    private static void Cache(MainWindow window, string id, params VideoSource[] sources)
    {
        const VideoDetailsLoadOptions options = VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Meta | VideoDetailsLoadOptions.Sources;
        var cache = (Dictionary<string, VideoDetails>)Field(window, "_videoDetailsCache")!;
        cache[$"{id}:{(int)options}"] = new VideoDetails
        {
            VideoId = id, Title = "Title " + id, Url = Host + "watch?v=" + id, Sources = [.. sources], LoadOptions = options
        };
    }

    private static object Enqueue(MainWindow window, string id, VideoSource source)
    {
        var item = Activator.CreateInstance(typeof(MainWindow).GetNestedType("DownloadQueueItem", BindingFlags.NonPublic)!)!;
        Put(item, "VideoId", id); Put(item, "Title", "Title " + id); Put(item, "Url", source.Url);
        Put(item, "Quality", source.Quality); Put(item, "Type", source.Type);
        Put(item, "TargetPath", (string)Call(window, "CreateQueueTargetPath", item)!);
        ((IList)Field(window, "_downloadQueue")!).Add(item);
        return item;
    }

    private static async Task CheckQualityCollisionAsync()
    {
        using var client = Client(request => request.Headers.Range is not null
            ? Partial(0, [1], 4)
            : Response(HttpStatusCode.OK, Bytes(request.RequestUri!.AbsolutePath.EndsWith("720.mp4", StringComparison.Ordinal) ? "0720" : "1080")));
        var window = Setup(client, 2, "qualities");
        try
        {
            var low = Source("abc", 720);
            var high = Source("abc", 1080);
            Cache(window, "abc", low, high);
            var first = Enqueue(window, "abc", low);
            var second = Enqueue(window, "abc", high);
            var occupied = (string)Get(first, "TargetPath")!;
            if (!Equals(occupied, Get(second, "TargetPath"))) throw new InvalidOperationException("Collision fixture is invalid.");
            await File.WriteAllTextAsync(occupied, "prior");
            await Start(window).WaitAsync(TimeoutForTest);
            Check(File.ReadAllText(occupied) == "prior" && File.ReadAllText((string)Get(first, "TargetPath")!) == "0720" &&
                  File.ReadAllText((string)Get(second, "TargetPath")!) == "1080" &&
                  Get(first, "QueueState")?.ToString() == "Completed" && Get(second, "QueueState")?.ToString() == "Completed",
                "same-name concurrent qualities download independently without replacing an existing file");
        }
        finally { window.Close(); }
    }

    private static async Task CheckHlsHistoryAsync()
    {
        var ts = new byte[188 * 2];
        ts[0] = ts[188] = 0x47;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/streams/playlist" => Playlist("#EXTM3U\n#EXTINF:1,\none.ts\n#EXT-X-ENDLIST\n", Host + "streams/playlist"),
            "/streams/one.ts" => Response(HttpStatusCode.OK, ts, "video/mp2t"),
            _ => Response(HttpStatusCode.NotFound, [])
        });
        var window = Setup(client, 1, "hls-history");
        try
        {
            var source = new VideoSource { Quality = 720, Type = "mp4", Url = Host + "streams/playlist" };
            Cache(window, "stream", source);
            var item = Enqueue(window, "stream", source);
            await Start(window).WaitAsync(TimeoutForTest);
            var history = ((IList)Field(window, "_historyItems")!).Cast<object>().Single();
            var actualPath = Path.ChangeExtension((string)Get(item, "TargetPath")!, ".ts");
            Check(Equals(Get(history, "FullPath"), actualPath) && File.ReadAllBytes(actualPath).SequenceEqual(ts),
                "extensionless HLS is detected by probe and download history points at the real TS file");
        }
        finally { window.Close(); }
    }

    private static async Task CheckPauseAsync()
    {
        var a = Signal(); var b = Signal(); var release = Signal();
        using var client = new HttpClient(new Mock(async (request, token) =>
        {
            if (request.Headers.Range is not null) return Partial(0, [1], 4);
            if (request.RequestUri!.AbsolutePath.StartsWith("/a-", StringComparison.Ordinal))
            {
                a.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            else
            {
                b.TrySetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
            }
            return Response(HttpStatusCode.OK, [1, 2, 3, 4]);
        }));
        var window = Setup(client, 2, "pause");
        var settings = (AppSettings)Field(window, "_settings")!;
        settings.PersistDownloadQueue = true;
        ((DispatcherTimer)Field(window, "_downloadQueueSaveTimer")!).Interval = TimeSpan.FromMinutes(1);
        var sourceA = Source("a"); var sourceB = Source("b");
        Cache(window, "a", sourceA); Cache(window, "b", sourceB);
        var first = Enqueue(window, "a", sourceA); var second = Enqueue(window, "b", sourceB);
        var run = Start(window);
        try
        {
            await Task.WhenAll(a.Task, b.Task).WaitAsync(TimeoutForTest);
            Call(window, "DownloadQueueButton_OnClick", window, new RoutedEventArgs());
            var deadline = DateTime.UtcNow + TimeoutForTest;
            while (Get(first, "IsDownloading") is true && DateTime.UtcNow < deadline) await Task.Delay(10);
            Check(Get(first, "IsDownloading") is false && Get(second, "IsDownloading") is true && !run.IsCompleted &&
                  Field(window, "_isDownloadingQueue") is true && Field(window, "DownloadQueueButton") is Button { IsEnabled: false },
                "pause does not allow restart while another canceled worker is still draining");
            release.TrySetResult();
            await run.WaitAsync(TimeoutForTest);
            Check(Field(window, "_isDownloadingQueue") is false && Get(second, "QueueState")?.ToString() == "Paused",
                "queue becomes resumable after all workers stop");
            using var pausedSnapshot = JsonDocument.Parse(await File.ReadAllTextAsync(AppPaths.DownloadQueueFile));
            Check(pausedSnapshot.RootElement.GetArrayLength() == 2,
                "pause flushes pending persistence without waiting for the background timer");
        }
        finally
        {
            release.TrySetResult();
            await run.WaitAsync(TimeoutForTest);
            settings.PersistDownloadQueue = false;
            window.Close();
        }
    }

    private static async Task CheckRecoveryFailureAsync()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath.StartsWith("/a-", StringComparison.Ordinal)
            ? Response(HttpStatusCode.Forbidden, [])
            : request.Headers.Range is not null ? Partial(0, [1], 4) : Response(HttpStatusCode.OK, Bytes("NEXT")));
        var window = Setup(client, 1, "recovery");
        var browser = new CloudflareWindow();
        try
        {
            var a = Source("a"); var b = Source("b");
            Cache(window, "a", a); Cache(window, "b", b);
            var first = Enqueue(window, "a", a); var second = Enqueue(window, "b", b);
            var api = new HanimeApiClient(browser, client);
            var inFlight = (ConcurrentDictionary<string, Lazy<Task<BrowserFetchResult>>>)typeof(HanimeApiClient)
                .GetField("_htmlInFlight", PrivateInstance)!.GetValue(api)!;
            foreach (var route in new[] { "watch?v=a", "download?v=a" })
            {
                var url = new Uri(browser.SessionBaseUri, route).AbsoluteUri;
                inFlight[url] = new(() => Task.FromResult(new BrowserFetchResult
                {
                    Url = url, Status = 200, Html = "<html><title>Video</title><body>No source available</body></html>"
                }));
            }
            SetField(window, "_apiClient", api);
            await Start(window).WaitAsync(TimeoutForTest);
            Check(Get(first, "QueueState")?.ToString() == "Error" && Get(second, "QueueState")?.ToString() == "Completed" &&
                  File.ReadAllText((string)Get(second, "TargetPath")!) == "NEXT",
                "CDN recovery failure is isolated and the next queued video still downloads");
            ((AppSettings)Field(window, "_settings")!).MaxRetries = 8;
            Check(((DownloadRetryPolicy)Call(window, "CreateDownloadRetryPolicy")!).MaxAttempts == 9,
                "eight retries allow nine total attempts");
        }
        finally { browser.Close(); window.Close(); }
    }
}
