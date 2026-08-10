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
    private const int MaxCacheSize = 500;
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    public static Task<BitmapSource?> GetAsync(string url, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return Task.FromResult<BitmapSource?>(null);

        if (Cache.Count >= MaxCacheSize)
        {
            var keysToRemove = Cache.Keys.Take(MaxCacheSize / 2).ToList();
            foreach (var k in keysToRemove)
                Cache.TryRemove(k, out _);
        }

        var key = $"{decodePixelWidth}|{url}";
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
        catch { return null; }
        finally { DownloadGate.Release(); }
    }
}
