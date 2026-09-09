using Hanime1Downloader.CSharp.Models;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 媒体下载服务：MP4（断点续传 + 重试 + 416 兜底 + 瞬时速度）与 HLS/m3u8（变体选择、
/// 分片下载、AES-128 解密、合并）统一入口。所有媒体 URL 强制通过 <see cref="ValidateHttpUrl"/>
/// 的安全校验（HTTP/HTTPS、无凭据、拒绝本机/私有网络地址、拒绝重解析点）。
/// </summary>
public sealed partial class DownloadService
{
    private const int BufferSize = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _referrer;
    private readonly DownloadRetryPolicy _retryPolicy;
    private readonly ConcurrentDictionary<Uri, byte[]> _keyCache = new();
    /// <summary>HLS 分片并行下载并发数。</summary>
    private const int HlsSegmentConcurrency = 4;

    public DownloadService(
        HttpClient httpClient,
        string siteHost = "hanime1.me",
        DownloadRetryPolicy? retryPolicy = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var normalizedHost = (siteHost ?? "hanime1.me").Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            normalizedHost = "hanime1.me";
        }

        _referrer = new Uri($"https://{normalizedHost}/");
        _retryPolicy = retryPolicy ?? new DownloadRetryPolicy();
    }

    public async Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        var uri = ValidateHttpUrl(url);
        if (LooksLikePlaylistUrl(uri))
        {
            using var playlistResponse = await SendWithRetryAsync(
                () => CreateRequest(HttpMethod.Get, uri, null, null),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            playlistResponse.EnsureSuccessStatusCode();
            EnsurePlaylistResponse(playlistResponse);
            var playlistText = await playlistResponse.Content.ReadAsStringAsync(cancellationToken);
            var parsedPlaylist = M3u8PlaylistParser.Parse(playlistText, uri);
            return new DownloadProbeResult
            {
                ContentType = playlistResponse.Content.Headers.ContentType?.MediaType ?? "application/vnd.apple.mpegurl",
                ContentLength = null,
                IsPartial = playlistResponse.StatusCode == HttpStatusCode.PartialContent,
                Playlist = parsedPlaylist
            };
        }

        using var response = await SendWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, uri, 0, 0),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new DownloadProbeResult
            {
                ContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                ContentLength = response.Content.Headers.ContentRange?.Length,
                IsPartial = false
            };
        }

        response.EnsureSuccessStatusCode();
        EnsureMediaResponse(response);
        return new DownloadProbeResult
        {
            ContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            ContentLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength,
            IsPartial = response.StatusCode == HttpStatusCode.PartialContent
        };
    }

    public async Task DownloadAsync(
        string url,
        string outputPath,
        IProgress<DownloadProgressInfo>? progress = null,
        string? mediaType = null,
        CancellationToken cancellationToken = default,
        M3u8Playlist? preloadedPlaylist = null,
        int targetQuality = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var uri = ValidateHttpUrl(url);
        var finalPath = ValidateOutputPath(outputPath);
        var tmpPath = finalPath + ".tmp";
        RejectReparsePoint(finalPath);
        RejectReparsePoint(tmpPath);

        var isPlaylist = LooksLikePlaylistUrl(uri) || IsPlaylistMediaType(mediaType);
        if (isPlaylist)
        {
            await DownloadHlsAsync(uri, finalPath, tmpPath, progress, cancellationToken, preloadedPlaylist, targetQuality);
            return;
        }

        await _retryPolicy.ExecuteAsync(
            async (attempt, token) =>
            {
                await DownloadSingleAttemptAsync(uri, finalPath, tmpPath, progress, attempt, token);
                return true;
            },
            cancellationToken);
    }

    /// <summary>4 秒滑动窗口瞬时速度（约 2MB 采样上限）。</summary>
    /// <summary>滑动窗口瞬时速度。HLS 并行下载后会被多线程调用，因此内部加锁。</summary>
    private sealed class SpeedWindow
    {
        private readonly Queue<(long Tick, int Bytes)> _samples = new();
        private readonly object _sync = new();

        public void Add(int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (_sync)
            {
                _samples.Enqueue((Environment.TickCount64, bytes));
                var now = Environment.TickCount64;
                while (_samples.Count > 0 && now - _samples.Peek().Tick > 4000)
                {
                    _samples.Dequeue();
                }

                var totalBytes = _samples.Sum(sample => (long)sample.Bytes);
                while (_samples.Count > 0 && totalBytes > 2 * 1024 * 1024)
                {
                    totalBytes -= _samples.Dequeue().Bytes;
                }
            }
        }

        public double BytesPerSecond
        {
            get
            {
                lock (_sync)
                {
                    if (_samples.Count < 2)
                    {
                        return 0;
                    }

                    var now = Environment.TickCount64;
                    var windowBytes = _samples.Sum(sample => (long)sample.Bytes);
                    var windowMs = Math.Max(now - _samples.Peek().Tick, 1L);
                    return windowBytes * 1000d / windowMs;
                }
            }
        }
    }
}
