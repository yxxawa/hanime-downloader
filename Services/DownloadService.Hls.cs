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

    private async Task<string> DownloadHlsAsync(
        Uri playlistUri,
        string outputPath,
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

        var workDirectory = outputPath + ".hls";
        if (Directory.Exists(workDirectory) &&
            (File.GetAttributes(workDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"拒绝写入重解析目录: {workDirectory}");
        }

        Directory.CreateDirectory(workDirectory);
        var mergePath = Path.Combine(workDirectory, "merged.tmp");
        RejectReparsePoint(mergePath);
        var playlistFingerprint = CreatePlaylistFingerprint(playlistUri, playlist);
        var totalBytes = 0L;
        var startedAt = DateTime.UtcNow;
        var speedWindow = new SpeedWindow();
        var completed = false;
        var transferredBytes = 0L;
        var lastProgressTick = 0L;
        var progressSync = new object();
        void TrackBytes(long count, int networkBytes)
        {
            lock (progressSync)
            {
                transferredBytes += count;
                if (networkBytes > 0) speedWindow.Add(networkBytes);
                var now = Environment.TickCount64;
                if (lastProgressTick == 0 || now - lastProgressTick >= 150)
                {
                    ReportProgress(progress, speedWindow, transferredBytes, null, startedAt);
                    lastProgressTick = now;
                }
            }
        }
        void OnBytesRead(int count) => TrackBytes(count, count);
        void OnCachedBytes(long count) => TrackBytes(count, 0);
        string SegmentPath(int index) => Path.Combine(workDirectory, $"segment_{playlistFingerprint}_{index:D6}.bin");

        try
        {
            string? initPath = null;
            if (playlist.InitSegment is { } init)
            {
                initPath = Path.Combine(workDirectory, $"init_{playlistFingerprint}.bin");
                totalBytes = await GetOrDownloadSegmentAsync(initPath, init.Uri, init.ByteRange, init.Key,
                    playlist.MediaSequence, cancellationToken, OnBytesRead, OnCachedBytes);
            }

            // 长列表只启动固定数量的工人；完成后按播放列表顺序从磁盘流式合并。
            var parallelism = Math.Min(HlsSegmentConcurrency, playlist.Segments.Count);
            AppLogger.Info("hls", $"HLS 分片下载: segments={playlist.Segments.Count}, concurrency={parallelism}");
            using var segmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var segmentToken = segmentCancellation.Token;
            Exception? segmentFailure = null;
            var nextIndex = -1;
            async Task DownloadWorkerAsync()
            {
                try
                {
                    while (true)
                    {
                        segmentToken.ThrowIfCancellationRequested();
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= playlist.Segments.Count) return;
                        var segment = playlist.Segments[index];
                        var length = await GetOrDownloadSegmentAsync(SegmentPath(index), segment.Uri, segment.ByteRange,
                            segment.Key, segment.Sequence, segmentToken, OnBytesRead, OnCachedBytes).ConfigureAwait(false);
                        Interlocked.Add(ref totalBytes, length);
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref segmentFailure, ex, null) is null)
                        segmentCancellation.Cancel();
                    throw;
                }
            }

            var workers = new Task[parallelism];
            for (var index = 0; index < workers.Length; index++) workers[index] = Task.Run(DownloadWorkerAsync);
            try
            {
                await Task.WhenAll(workers);
            }
            catch when (segmentFailure is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(segmentFailure).Throw();
                throw;
            }

            var finalPath = await IdentifyHlsOutputAsync(outputPath, initPath ?? SegmentPath(0), cancellationToken);
            RejectReparsePoint(finalPath);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new InvalidOperationException($"目标文件已存在，拒绝覆盖: {finalPath}");
            }

            await using (var output = OpenHlsFile(mergePath, FileMode.Create, FileAccess.Write))
            {
                var writtenBytes = 0L;
                var lastMergeReport = Environment.TickCount64;
                ReportProgress(progress, speedWindow, 0, totalBytes, startedAt);
                async Task MergePartAsync(string path)
                {
                    RejectReparsePoint(path);
                    await using var input = OpenHlsFile(path, FileMode.Open, FileAccess.Read);
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    writtenBytes += input.Position;
                    var now = Environment.TickCount64;
                    if (now - lastMergeReport >= 150)
                    {
                        ReportProgress(progress, speedWindow, writtenBytes, totalBytes, startedAt);
                        lastMergeReport = now;
                    }
                }

                if (initPath is not null) await MergePartAsync(initPath).ConfigureAwait(false);
                for (var index = 0; index < playlist.Segments.Count; index++)
                    await MergePartAsync(SegmentPath(index)).ConfigureAwait(false);

                if (writtenBytes != totalBytes)
                    throw new IOException("HLS 分片长度在合并前发生变化，未保存成品。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, speedWindow, totalBytes, totalBytes, startedAt);
            RejectReparsePoint(finalPath);
            File.Move(mergePath, finalPath, overwrite: false);
            completed = true;
            return finalPath;
        }
        finally
        {
            if (completed)
            {
                TryDeleteDirectory(workDirectory);
            }
            else
            {
                TryDelete(mergePath);
            }
        }
    }

    private async Task<M3u8Playlist> LoadPlaylistAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        var (bytes, effectiveUri) = await ReadResponseWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, playlistUri, null, null),
            EnsurePlaylistResponse,
            cancellationToken);
        var content = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        // 相对变体、分片、密钥和初始化段都以实际返回播放列表的位置为基准。
        return M3u8PlaylistParser.Parse(content, ValidateHttpUrl(effectiveUri.AbsoluteUri));
    }

    private async Task<long> GetOrDownloadSegmentAsync(
        string segmentPath,
        Uri segmentUri,
        M3u8ByteRange? byteRange,
        M3u8Key? key,
        long sequence,
        CancellationToken cancellationToken,
        Action<int> onBytesRead,
        Action<long> onCachedBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectReparsePoint(segmentPath);
        if (File.Exists(segmentPath) && new FileInfo(segmentPath).Length is > 0 and var length)
        {
            onCachedBytes(length);
            return length;
        }

        var temporaryPath = segmentPath + ".tmp";
        RejectReparsePoint(temporaryPath);
        try
        {
            long savedLength;
            if (key is null && byteRange is null)
            {
                savedLength = await StreamPlainHlsSegmentAsync(segmentUri, temporaryPath, cancellationToken, onBytesRead);
            }
            else
            {
                var bytes = await DownloadSegmentBytesAsync(segmentUri, byteRange, cancellationToken, onBytesRead);
                if (key is not null)
                    bytes = await DecryptSegmentAsync(bytes, key, sequence, cancellationToken);
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
                savedLength = bytes.LongLength;
            }

            if (savedLength <= 0) throw new InvalidDataException("HLS 分片没有数据。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, segmentPath, overwrite: true);
            return savedLength;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task<long> StreamPlainHlsSegmentAsync(Uri segmentUri, string temporaryPath, CancellationToken cancellationToken,
        Action<int> onBytesRead)
    {
        var uri = ValidateHttpUrl(segmentUri.AbsoluteUri);
        return await _retryPolicy.ExecuteAsync(async (_, token) =>
        {
            using var request = CreateRequest(HttpMethod.Get, uri, null, null);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            EnsureMediaResponse(response);
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = OpenHlsFile(temporaryPath, FileMode.Create, FileAccess.Write);
            // Idle-timeout reads may still unwind after cancellation; do not return their buffer to a shared pool.
            var buffer = new byte[64 * 1024];
            long written = 0;
            int count;
            while ((count = await ReadWithIdleTimeoutAsync(input, buffer, token)) > 0)
            {
                if (response.Content.Headers.ContentLength is long declared && count > declared - written)
                    throw new IOException("HLS 分片正文超过声明的长度。");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                written += count;
                onBytesRead(count);
            }
            if (response.Content.Headers.ContentLength is long expected && written != expected)
                throw new IOException($"HLS 分片响应提前结束：收到 {written} 字节，预期 {expected} 字节。");
            await output.FlushAsync(token);
            return written;
        }, cancellationToken);
    }

    private static FileStream OpenHlsFile(string path, FileMode mode, FileAccess access) =>
        new(path, mode, access, access == FileAccess.Read ? FileShare.Read : FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<byte[]> DownloadSegmentBytesAsync(
        Uri segmentUri,
        M3u8ByteRange? byteRange,
        CancellationToken cancellationToken,
        Action<int>? onBytesRead = null)
    {
        var validatedSegmentUri = ValidateHttpUrl(segmentUri.AbsoluteUri);
        return await _retryPolicy.ExecuteAsync(async (_, token) =>
        {
            using var request = CreateRequest(HttpMethod.Get, validatedSegmentUri,
                byteRange is null ? null : byteRange.Offset ?? 0,
                byteRange is null ? null : (byteRange.Offset ?? 0) + byteRange.Length - 1);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
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

            var bytes = await ReadBufferedResponseAsync(response, token, onBytesRead);
            if (byteRange is null)
            {
                return bytes;
            }

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (bytes.LongLength != byteRange.Length)
                {
                    throw new IOException($"HLS 分片响应提前结束: {segmentUri}");
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
        }, cancellationToken);
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
        var (bytes, _) = await ReadResponseWithRetryAsync(
            () => CreateRequest(HttpMethod.Get, validatedKeyUri, null, null),
            _ => { },
            cancellationToken);
        return bytes;
    }

    internal static string GetTransportStreamOutputPath(string outputPath) => Path.ChangeExtension(outputPath, ".ts");

    private static async Task<string> IdentifyHlsOutputAsync(string outputPath, string firstPartPath, CancellationToken cancellationToken)
    {
        RejectReparsePoint(firstPartPath);
        await using var input = OpenHlsFile(firstPartPath, FileMode.Open, FileAccess.Read);
        var length = input.Length;
        var prefix = new byte[(int)Math.Min(length, 5 * 188)];
        await input.ReadExactlyAsync(prefix, cancellationToken);
        return GetHlsOutputPath(outputPath, prefix, length);
    }

    private static string GetHlsOutputPath(string outputPath, ReadOnlySpan<byte> firstBytes, long length)
    {
        const int packetSize = 188;
        if (firstBytes.Length >= packetSize && length % packetSize == 0)
        {
            var isTransportStream = true;
            for (var offset = 0; offset < Math.Min(firstBytes.Length, 5 * packetSize); offset += packetSize)
            {
                if (firstBytes[offset] != 0x47)
                {
                    isTransportStream = false;
                    break;
                }
            }
            if (isTransportStream) return GetTransportStreamOutputPath(outputPath);
        }

        if (firstBytes.Length >= 8)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(firstBytes);
            var box = firstBytes.Slice(4, 4);
            if ((size is 0 or 1 || (size >= 8 && size <= length)) &&
                (box.SequenceEqual("ftyp"u8) || box.SequenceEqual("moov"u8) || box.SequenceEqual("styp"u8) ||
                 box.SequenceEqual("moof"u8) || box.SequenceEqual("sidx"u8)))
            {
                return Path.ChangeExtension(outputPath, Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetExtension(outputPath) : ".mp4");
            }
        }

        throw new InvalidDataException("无法识别 HLS 的媒体容器，未保存为 MP4 或 TS。");
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
