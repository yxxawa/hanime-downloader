using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 封面缓存：内存 LRU + 磁盘缓存。
/// 1. 命中时只更新 LRU 顺序，不再「命中即入队」，避免热点封面被误驱逐后反复下载；
/// 2. 磁盘缓存按 URL 的 SHA-256 命名，重启/重新搜索后无需重复下载；
/// 3. 磁盘缓存总大小超上限时按最后访问时间清理。
/// </summary>
public static class ThumbnailCacheService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruOrder = new();
    private static readonly Dictionary<string, LinkedListNode<string>> LruNodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object LruSync = new();
    private const int MaxCacheSize = 500;
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    private static readonly string DiskCacheDirectory = Path.Combine(AppPaths.DataDirectory, "thumbcache");
    private const long DiskCacheMaxBytes = 200L * 1024 * 1024;
    private static long _diskCacheBytes = -1;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 16
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        // 部分 CDN 会拒绝没有浏览器 UA 的请求，导致封面直接 403。
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserIdentity.DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        return client;
    }

    public static Task<BitmapSource?> GetAsync(string url, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return Task.FromResult<BitmapSource?>(null);
        }

        var key = $"{decodePixelWidth}|{url}";
        if (Cache.TryGetValue(key, out var cached))
        {
            Touch(key);
            return cached.Value;
        }

        var lazy = new Lazy<Task<BitmapSource?>>(
            () => LoadAsync(url, decodePixelWidth),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = Cache.GetOrAdd(key, lazy);
        Touch(key);
        if (ReferenceEquals(actual, lazy))
        {
            TrimMemoryCache();
        }

        return actual.Value;
    }

    private static void Touch(string key)
    {
        lock (LruSync)
        {
            if (LruNodes.TryGetValue(key, out var node))
            {
                if (!ReferenceEquals(LruOrder.Last, node))
                {
                    LruOrder.Remove(node);
                    LruOrder.AddLast(node);
                }

                return;
            }

            LruNodes[key] = LruOrder.AddLast(key);
        }
    }

    private static void TrimMemoryCache()
    {
        while (true)
        {
            string? evictKey = null;
            lock (LruSync)
            {
                if (LruOrder.Count <= MaxCacheSize)
                {
                    return;
                }

                var first = LruOrder.First;
                if (first is null)
                {
                    return;
                }

                evictKey = first.Value;
                LruOrder.RemoveFirst();
                LruNodes.Remove(evictKey);
            }

            Cache.TryRemove(evictKey, out _);
        }
    }

    private static async Task<BitmapSource?> LoadAsync(string url, int decodePixelWidth)
    {
        await DownloadGate.WaitAsync();
        try
        {
            var bytes = await TryReadDiskCacheAsync(url);
            if (bytes is null)
            {
                bytes = await HttpClient.GetByteArrayAsync(url);
                await WriteDiskCacheAsync(url, bytes);
            }

            using var stream = new MemoryStream(bytes);
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
        finally
        {
            DownloadGate.Release();
        }
    }

    private static string GetDiskCachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(DiskCacheDirectory, hash + ".img");
    }

    private static async Task<byte[]?> TryReadDiskCacheAsync(string url)
    {
        try
        {
            var path = GetDiskCachePath(url);
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            try
            {
                // 更新访问时间，供磁盘清理按 LRU 淘汰。
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // 访问时间更新失败不影响读取。
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteDiskCacheAsync(string url, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheDirectory);
            var path = GetDiskCachePath(url);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            Interlocked.Add(ref _diskCacheBytes, bytes.LongLength);
            TrimDiskCache();
        }
        catch
        {
            // 磁盘缓存失败不影响本次显示。
        }
    }

    private static void TrimDiskCache()
    {
        try
        {
            if (_diskCacheBytes < 0)
            {
                _diskCacheBytes = Directory.EnumerateFiles(DiskCacheDirectory).Sum(file => new FileInfo(file).Length);
            }

            if (_diskCacheBytes <= DiskCacheMaxBytes)
            {
                return;
            }

            foreach (var file in new DirectoryInfo(DiskCacheDirectory)
                         .EnumerateFiles("*.img")
                         .OrderBy(file => file.LastWriteTimeUtc)
                         .ToList())
            {
                if (_diskCacheBytes <= DiskCacheMaxBytes * 8 / 10)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    _diskCacheBytes -= length;
                }
                catch
                {
                    // 单个文件删除失败继续处理下一个。
                }
            }
        }
        catch
        {
            // 目录不可用时不清理。
        }
    }
}
