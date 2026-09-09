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

public sealed partial class DownloadService
{

    private async Task DownloadHlsAsync(
        Uri playlistUri,
        string finalPath,
        string tmpPath,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken,
        M3u8Playlist? preloadedPlaylist = null,
        int targetQuality = 0)
    {
        var playlist = preloadedPlaylist ?? await LoadPlaylistAsync(playlistUri, cancellationToken);
        while (playlist.Variants.Count > 0)
        {
            var selected = M3u8PlaylistParser.SelectBestVariant(playlist.Variants, targetQuality);
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

            // 分片并行下载，完成后按顺序合并到输出文件。
            var segmentPaths = new string[playlist.Segments.Count];
            var parallelism = Math.Clamp(HlsSegmentConcurrency, 1, 8);
            AppLogger.Info("hls", $"HLS 分片下载: segments={segmentPaths.Length}, concurrency={parallelism}");
            using (var gate = new SemaphoreSlim(parallelism, parallelism))
            {
                var downloadTasks = new Task[segmentPaths.Length];
                for (var index = 0; index < segmentPaths.Length; index++)
                {
                    var segmentIndex = index;
                    var segment = playlist.Segments[segmentIndex];
                    var segmentPath = Path.Combine(workDirectory, $"segment_{playlistFingerprint}_{segmentIndex:D6}.bin");
                    segmentPaths[segmentIndex] = segmentPath;
                    downloadTasks[segmentIndex] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var bytes = await GetOrDownloadSegmentAsync(
                                segmentPath,
                                segment.Uri,
                                segment.ByteRange,
                                segment.Key,
                                segment.Sequence,
                                cancellationToken).ConfigureAwait(false);
                            var received = Interlocked.Add(ref totalBytes, bytes.LongLength);
                            speedWindow.Add(bytes.Length);
                            ReportProgress(progress, speedWindow, received, null, startedAt);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(downloadTasks);
            }

            await using (var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var writtenBytes = 0L;
                if (initBytes is not null)
                {
                    await output.WriteAsync(initBytes, cancellationToken);
                    writtenBytes += initBytes.LongLength;
                    ReportProgress(progress, speedWindow, writtenBytes, totalBytes, startedAt);
                }

                for (var index = 0; index < segmentPaths.Length; index++)
                {
                    var bytes = await File.ReadAllBytesAsync(segmentPaths[index], cancellationToken);
                    await output.WriteAsync(bytes, cancellationToken);
                    writtenBytes += bytes.LongLength;
                    ReportProgress(progress, speedWindow, writtenBytes, totalBytes, startedAt);
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

    private static byte[] CreateDefaultIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }
}
