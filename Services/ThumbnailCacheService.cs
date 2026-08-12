using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace Hanime1Downloader.CSharp.Services;

public static class ThumbnailCacheService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = 16
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };

    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> InsertionOrder = new();
    private const int MaxCacheSize = 500;
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    public static Task<BitmapSource?> GetAsync(string url, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return Task.FromResult<BitmapSource?>(null);

        // FIFO 淘汰：按插入顺序移除最旧的一半。
        while (InsertionOrder.Count >= MaxCacheSize && InsertionOrder.TryDequeue(out var oldestKey))
        {
            Cache.TryRemove(oldestKey, out _);
        }

        var key = $"{decodePixelWidth}|{url}";
        InsertionOrder.Enqueue(key);
        var lazy = Cache.GetOrAdd(key, _ => new Lazy<Task<BitmapSource?>>(() => LoadAsync(url, decodePixelWidth), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private static async Task<BitmapSource?> LoadAsync(string url, int decodePixelWidth)
    {
        await DownloadGate.WaitAsync();
        try
        {
            var bytes = await HttpClient.GetByteArrayAsync(url);
            using var stream = new System.IO.MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 失败不中毒缓存：移除条目，下次请求会重试而不是永远拿到 null。
            Cache.TryRemove($"{decodePixelWidth}|{url}", out _);
            return null;
        }
        finally { DownloadGate.Release(); }
    }
}
