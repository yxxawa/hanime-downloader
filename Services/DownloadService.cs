using Hanime1Downloader.CSharp.Models;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 媒体下载服务：MP4（断点续传 + 重试 + 416 兜底 + 限速 + 瞬时速度）与 HLS/m3u8（变体选择、
/// 分片下载、AES-128 解密、合并）统一入口。所有媒体 URL 强制通过 <see cref="ValidateHttpUrl"/>
/// 的安全校验（HTTP/HTTPS、无凭据、拒绝本机/私有网络地址、拒绝重解析点）。
/// </summary>
public sealed class DownloadService
{
    private const int BufferSize = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _referrer;
    private readonly DownloadRetryPolicy _retryPolicy;
    private readonly Dictionary<Uri, byte[]> _keyCache = [];
    private int _speedLimitKBps;

    public DownloadService(
        HttpClient httpClient,
        string siteHost = "hanime1.me",
        DownloadRetryPolicy? retryPolicy = null,
        int speedLimitKBps = 0)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        var normalizedHost = (siteHost ?? "hanime1.me").Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            normalizedHost = "hanime1.me";
        }

        _referrer = new Uri($"https://{normalizedHost}/");
        _retryPolicy = retryPolicy ?? new DownloadRetryPolicy();
        SpeedLimitKBps = speedLimitKBps;
    }

    /// <summary>下载限速（KB/s，0 = 不限速），可从设置对话框热更新。</summary>
    public int SpeedLimitKBps
    {
        get => _speedLimitKBps;
        set => _speedLimitKBps = Math.Max(0, value);
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
            _ = M3u8PlaylistParser.Parse(playlistText, uri);
            return new DownloadProbeResult
            {
                ContentType = playlistResponse.Content.Headers.ContentType?.MediaType ?? "application/vnd.apple.mpegurl",
                ContentLength = null,
                IsPartial = playlistResponse.StatusCode == HttpStatusCode.PartialContent
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
        CancellationToken cancellationToken = default)
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
            await DownloadHlsAsync(uri, finalPath, tmpPath, progress, cancellationToken);
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

    private async Task DownloadSingleAttemptAsync(
        Uri uri,
        string finalPath,
        string tmpPath,
        IProgress<DownloadProgressInfo>? progress,
        int attempt,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
        var requestedResume = existingBytes > 0;
        var speedWindow = new SpeedWindow();
        var throttleStopwatch = new System.Diagnostics.Stopwatch();
        throttleStopwatch.Start();
        var startedAt = DateTime.UtcNow;

        AppLogger.Info("download", attempt > 1
            ? $"[attempt {attempt}] 下载: {uri} resume={requestedResume} existing={existingBytes}"
            : $"下载: {uri} resume={requestedResume} existing={existingBytes}");

        using var request = CreateRequest(HttpMethod.Get, uri, requestedResume ? existingBytes : null, null);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var isResume = false;
        var fileMode = FileMode.Create;
        var bytesReceived = 0L;

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var advertisedLength = response.Content.Headers.ContentRange?.Length;
            if (requestedResume && advertisedLength == existingBytes)
            {
                // 本地临时文件已完整：直接收尾。
                ReportProgress(progress, speedWindow, existingBytes, existingBytes, startedAt);
                RejectReparsePoint(finalPath);
                File.Move(tmpPath, finalPath, overwrite: true);
                return;
            }

            if (requestedResume && advertisedLength is long length && existingBytes > length)
            {
                // 服务器文件已变化：删除临时文件从头下载。
                AppLogger.Info("download", $"服务器拒绝续传(416)，删除临时文件从头下载: {tmpPath}");
                TryDelete(tmpPath);
                existingBytes = 0;
                requestedResume = false;
                response.Dispose();
                using var restartRequest = CreateRequest(HttpMethod.Get, uri, null, null);
                var restartResponse = await _httpClient.SendAsync(restartRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                restartResponse.EnsureSuccessStatusCode();
                EnsureMediaResponse(restartResponse);
                using var restarted = restartResponse;
                await ReadMediaStreamAsync(restarted, tmpPath, fileMode, bytesReceived, speedWindow, throttleStopwatch, startedAt, progress, cancellationToken);
                RejectReparsePoint(finalPath);
                File.Move(tmpPath, finalPath, overwrite: true);
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidatePartialResponse(response, requestedResume ? existingBytes : 0);
            isResume = requestedResume;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            EnsureMediaResponse(response);
            if (requestedResume)
            {
                AppLogger.Info("download", $"服务器未接受 Range，重新下载: {uri}");
            }
            isResume = false;
        }

        EnsureMediaResponse(response);

        long? totalBytes;
        if (response.Content.Headers.ContentRange?.Length is long rangeLength)
        {
            totalBytes = rangeLength;
            if (isResume && rangeLength < existingBytes)
            {
                throw new InvalidDataException("服务器返回的总长度小于本地临时文件，已拒绝续传。");
            }
        }
        else if (response.Content.Headers.ContentLength is long length)
        {
            totalBytes = isResume ? checked(existingBytes + length) : length;
        }
        else
        {
            totalBytes = null;
        }

        if (isResume)
        {
            fileMode = FileMode.Append;
            bytesReceived = existingBytes;
        }

        await ReadMediaStreamAsync(response, tmpPath, fileMode, bytesReceived, speedWindow, throttleStopwatch, startedAt, progress, cancellationToken, totalBytes);
        RejectReparsePoint(finalPath);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    /// <summary>读取响应流写入临时文件，附带进度报告（瞬时速度）、限速与长度校验。</summary>
    private async Task ReadMediaStreamAsync(
        HttpResponseMessage response,
        string tmpPath,
        FileMode fileMode,
        long bytesReceived,
        SpeedWindow speedWindow,
        System.Diagnostics.Stopwatch throttleStopwatch,
        DateTime startedAt,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken,
        long? totalBytes = null)
    {
        var keepPartialFile = bytesReceived > 0;
        var lastReportedBytes = bytesReceived;
        var lastReportedAt = Environment.TickCount64;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                tmpPath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesReceived += bytesRead;
                keepPartialFile = bytesReceived > 0;
                speedWindow.Add(bytesRead);

                await ThrottleAsync(bytesRead, SpeedLimitKBps, throttleStopwatch, cancellationToken);

                var now = Environment.TickCount64;
                var shouldReport = bytesReceived == totalBytes ||
                                   bytesReceived - lastReportedBytes >= 512 * 1024 ||
                                   now - lastReportedAt >= 150;
                if (shouldReport)
                {
                    ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
                    lastReportedBytes = bytesReceived;
                    lastReportedAt = now;
                }
            }

            await output.FlushAsync(cancellationToken);
            if (totalBytes is long expectedLength && bytesReceived != expectedLength)
            {
                throw new InvalidDataException($"下载长度校验失败：收到 {bytesReceived} 字节，预期 {expectedLength} 字节。");
            }

            ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
        }
        catch
        {
            // 从未收到任何字节且不是续传 → 删除空临时文件；否则保留以便下次续传。
            if (!keepPartialFile && File.Exists(tmpPath) && new FileInfo(tmpPath).Length == 0)
            {
                TryDelete(tmpPath);
            }
            throw;
        }
    }

    private async Task DownloadHlsAsync(
        Uri playlistUri,
        string finalPath,
        string tmpPath,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var playlist = await LoadPlaylistAsync(playlistUri, cancellationToken);
        while (playlist.Variants.Count > 0)
        {
            var selected = M3u8PlaylistParser.SelectBestVariant(playlist.Variants);
            AppLogger.Info("hls", $"选择 HLS 变体: {selected.Uri}");
            playlistUri = selected.Uri;
            playlist = await LoadPlaylistAsync(playlistUri, cancellationToken);
        }

        if (playlist.Segments.Count == 0)
        {
            throw new InvalidDataException("HLS 播放列表没有媒体分片。");
        }

        if (!playlist.IsEndList)
        {
            AppLogger.Info("hls", "HLS 播放列表没有 ENDLIST，将按当前快照合并。");
        }

        var workDirectory = finalPath + ".hls";
        if (Directory.Exists(workDirectory) &&
            (File.GetAttributes(workDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"拒绝写入重解析目录: {workDirectory}");
        }

        Directory.CreateDirectory(workDirectory);
        var playlistFingerprint = CreatePlaylistFingerprint(playlistUri, playlist);
        var totalBytes = 0L;
        var startedAt = DateTime.UtcNow;
        var speedWindow = new SpeedWindow();
        var throttleStopwatch = new System.Diagnostics.Stopwatch();
        throttleStopwatch.Start();
        var completed = false;

        try
        {
            var initPath = Path.Combine(workDirectory, $"init_{playlistFingerprint}.bin");
            byte[]? initBytes = null;
            if (playlist.InitSegment is not null)
            {
                initBytes = await GetOrDownloadSegmentAsync(
                    initPath,
                    playlist.InitSegment.Uri,
                    playlist.InitSegment.ByteRange,
                    playlist.InitSegment.Key,
                    playlist.MediaSequence,
                    cancellationToken);
            }

            await using (var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                if (initBytes is not null)
                {
                    await output.WriteAsync(initBytes, cancellationToken);
                    totalBytes += initBytes.LongLength;
                    speedWindow.Add(initBytes.Length);
                    ReportProgress(progress, speedWindow, totalBytes, null, startedAt);
                }

                for (var index = 0; index < playlist.Segments.Count; index++)
                {
                    var segment = playlist.Segments[index];
                    var segmentPath = Path.Combine(workDirectory, $"segment_{playlistFingerprint}_{index:D6}.bin");
                    var bytes = await GetOrDownloadSegmentAsync(
                        segmentPath,
                        segment.Uri,
                        segment.ByteRange,
                        segment.Key,
                        segment.Sequence,
                        cancellationToken);
                    await output.WriteAsync(bytes, cancellationToken);
                    totalBytes += bytes.LongLength;
                    speedWindow.Add(bytes.Length);
                    await ThrottleAsync(bytes.Length, SpeedLimitKBps, throttleStopwatch, cancellationToken);
                    ReportProgress(progress, speedWindow, totalBytes, null, startedAt);
                }

                await output.FlushAsync(cancellationToken);
            }

            ReportProgress(progress, speedWindow, totalBytes, totalBytes, startedAt);
            RejectReparsePoint(finalPath);
            File.Move(tmpPath, finalPath, overwrite: true);
            completed = true;
        }
        finally
        {
            if (completed)
            {
                TryDeleteDirectory(workDirectory);
            }
            else if (File.Exists(tmpPath) && new FileInfo(tmpPath).Length == 0)
            {
                TryDelete(tmpPath);
            }
        }
    }

    private async Task<M3u8Playlist> LoadPlaylistAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, playlistUri, null, null),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsurePlaylistResponse(response);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return M3u8PlaylistParser.Parse(content, playlistUri);
    }

    private async Task<byte[]> GetOrDownloadSegmentAsync(
        string segmentPath,
        Uri segmentUri,
        M3u8ByteRange? byteRange,
        M3u8Key? key,
        long sequence,
        CancellationToken cancellationToken)
    {
        if (File.Exists(segmentPath))
        {
            var existing = await File.ReadAllBytesAsync(segmentPath, cancellationToken);
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var bytes = await DownloadSegmentBytesAsync(segmentUri, byteRange, cancellationToken);
        if (key is not null)
        {
            bytes = await DecryptSegmentAsync(bytes, key, sequence, cancellationToken);
        }

        var temporaryPath = segmentPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, segmentPath, overwrite: true);
        return bytes;
    }

    private async Task<byte[]> DownloadSegmentBytesAsync(
        Uri segmentUri,
        M3u8ByteRange? byteRange,
        CancellationToken cancellationToken)
    {
        var validatedSegmentUri = ValidateHttpUrl(segmentUri.AbsoluteUri);
        using var response = await SendWithRetryAsync(
            () => CreateRequest(
                HttpMethod.Get,
                validatedSegmentUri,
                byteRange is null ? null : byteRange.Offset ?? 0,
                byteRange is null ? null : (byteRange.Offset ?? 0) + byteRange.Length - 1),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureMediaResponse(response);

        if (byteRange is not null && response.StatusCode == HttpStatusCode.PartialContent)
        {
            var contentRange = response.Content.Headers.ContentRange;
            var expectedStart = byteRange.Offset ?? 0;
            if (contentRange?.From != expectedStart || contentRange.To is null || contentRange.To - expectedStart + 1 != byteRange.Length)
            {
                throw new InvalidDataException($"HLS 分片 Content-Range 校验失败: {segmentUri}");
            }
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (byteRange is null)
        {
            return bytes;
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (bytes.LongLength != byteRange.Length)
            {
                throw new InvalidDataException($"HLS 分片响应长度与 BYTERANGE 不匹配: {segmentUri}");
            }
            return bytes;
        }

        // 部分 CDN 忽略 Range 返回完整资源：仅在完整响应包含所需区间时切片。
        var offset = byteRange.Offset ?? 0;
        if (offset < 0 || byteRange.Length > int.MaxValue || offset + byteRange.Length > bytes.LongLength)
        {
            throw new InvalidDataException($"HLS 分片没有返回所需的 BYTERANGE: {segmentUri}");
        }

        return bytes.AsSpan((int)offset, (int)byteRange.Length).ToArray();
    }

    private async Task<byte[]> DecryptSegmentAsync(
        byte[] encryptedBytes,
        M3u8Key key,
        long sequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!key.Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"暂不支持 HLS 加密方式: {key.Method}");
        }
        if (key.Uri is null)
        {
            throw new InvalidDataException("HLS AES-128 Key 缺少 URI。");
        }

        if (!_keyCache.TryGetValue(key.Uri, out var keyBytes))
        {
            keyBytes = await DownloadKeyBytesAsync(key.Uri, cancellationToken);
            if (keyBytes.Length < 16)
            {
                throw new InvalidDataException("HLS AES-128 Key 长度不足 16 字节。");
            }
            keyBytes = keyBytes[..16];
            _keyCache[key.Uri] = keyBytes;
        }

        var iv = key.Iv ?? CreateDefaultIv(sequence);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
    }

    private async Task<byte[]> DownloadKeyBytesAsync(Uri keyUri, CancellationToken cancellationToken)
    {
        var validatedKeyUri = ValidateHttpUrl(keyUri.AbsoluteUri);
        using var response = await SendWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, validatedKeyUri, null, null),
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        return await _retryPolicy.ExecuteAsync(async (_, token) =>
        {
            using var request = requestFactory();
            var response = await _httpClient.SendAsync(request, completionOption, token);
            if (DownloadRetryPolicy.IsTransientStatus(response.StatusCode))
            {
                var statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"媒体服务器返回 {(int)statusCode}。", null, statusCode);
            }

            return response;
        }, cancellationToken);
    }

    private static async Task ThrottleAsync(
        int bytesRead,
        int speedLimitKBps,
        System.Diagnostics.Stopwatch sinceLastWrite,
        CancellationToken cancellationToken)
    {
        if (speedLimitKBps <= 0)
        {
            return;
        }

        var allowedBytesPerMs = speedLimitKBps * 1024d / 1000d;
        var expectedMs = bytesRead / allowedBytesPerMs;
        var remainingMs = expectedMs - sinceLastWrite.Elapsed.TotalMilliseconds;
        if (remainingMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), cancellationToken);
        }
        sinceLastWrite.Restart();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, long? rangeFrom, long? rangeTo)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = _referrer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        if (rangeFrom.HasValue || rangeTo.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(rangeFrom, rangeTo);
        }
        return request;
    }

    public static Uri ValidateHttpUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("媒体地址不是有效的绝对 URL。", nameof(url));
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("媒体地址只允许使用 HTTP 或 HTTPS。", nameof(url));
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException("媒体地址不允许携带用户凭据。", nameof(url));
        }

        if (IsLocalOrPrivateHost(uri.Host))
        {
            throw new ArgumentException("媒体地址不能指向本机或私有网络地址。", nameof(url));
        }

        return uri;
    }

    private static string ValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("输出路径不能为空。", nameof(outputPath));
        }

        var fullPath = Path.GetFullPath(outputPath.Trim());
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(fullPath))
        {
            throw new ArgumentException("输出路径必须指向文件。", nameof(outputPath));
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("输出路径缺少父目录。", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"拒绝写入重解析文件: {path}");
        }
    }

    private static void ValidatePartialResponse(HttpResponseMessage response, long expectedFrom)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            return;
        }

        var range = response.Content.Headers.ContentRange;
        if (range is null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            !range.From.HasValue || range.From.Value != expectedFrom ||
            (range.To.HasValue && range.To.Value < range.From.Value))
        {
            throw new InvalidDataException("服务器返回了无效的 Content-Range，已拒绝写入文件。");
        }

        if (range.To.HasValue && response.Content.Headers.ContentLength is long contentLength)
        {
            var expectedLength = checked(range.To.Value - range.From.Value + 1);
            if (expectedLength != contentLength)
            {
                throw new InvalidDataException("Content-Range 与 Content-Length 不一致，已拒绝写入文件。");
            }
        }
    }

    private static void EnsureMediaResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is "text/html" or "application/xhtml+xml" or "text/plain")
        {
            throw new InvalidOperationException("媒体地址返回了网页内容，当前 Cloudflare 会话可能已失效，请重新验证。");
        }
    }

    private static void EnsurePlaylistResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            throw new InvalidOperationException("M3U8 地址返回了网页内容，当前 Cloudflare 会话可能已失效，请重新验证。");
        }
    }

    private static bool IsPlaylistMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("apple.mpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreatePlaylistFingerprint(Uri playlistUri, M3u8Playlist playlist)
    {
        var builder = new StringBuilder(playlistUri.AbsoluteUri);
        if (playlist.InitSegment is not null)
        {
            builder.Append("|init:").Append(playlist.InitSegment.Uri.AbsoluteUri)
                .Append(':').Append(playlist.InitSegment.ByteRange?.Offset)
                .Append(':').Append(playlist.InitSegment.ByteRange?.Length);
        }

        foreach (var segment in playlist.Segments)
        {
            builder.Append('|').Append(segment.Sequence)
                .Append(':').Append(segment.Uri.AbsoluteUri)
                .Append(':').Append(segment.ByteRange?.Offset)
                .Append(':').Append(segment.ByteRange?.Length)
                .Append(':').Append(segment.Key?.Uri?.AbsoluteUri)
                .Append(':').Append(segment.Key?.Method);
            if (segment.Key?.Iv is { Length: > 0 } iv)
            {
                builder.Append(':').Append(Convert.ToHexString(iv));
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static bool LooksLikePlaylistUrl(Uri uri)
    {
        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               uri.Query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReportProgress(
        IProgress<DownloadProgressInfo>? progress,
        SpeedWindow speedWindow,
        long bytesReceived,
        long? totalBytes,
        DateTime startedAt)
    {
        if (progress is null)
        {
            return;
        }

        var elapsedSeconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001d);
        progress.Report(new DownloadProgressInfo
        {
            BytesReceived = bytesReceived,
            TotalBytes = totalBytes,
            BytesPerSecond = bytesReceived / elapsedSeconds,
            InstantBytesPerSecond = speedWindow.BytesPerSecond
        });
    }

    private static byte[] CreateDefaultIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }

    private static bool IsLocalOrPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        return bytes.Length >= 2 &&
               ((bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("download", $"清理临时媒体文件失败: {path}", ex);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("hls", $"清理 HLS 临时目录失败: {path}", ex);
        }
    }

    /// <summary>4 秒滑动窗口瞬时速度（约 2MB 采样上限）。</summary>
    private sealed class SpeedWindow
    {
        private readonly Queue<(long Tick, int Bytes)> _samples = new();

        public void Add(int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

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

        public double BytesPerSecond
        {
            get
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
