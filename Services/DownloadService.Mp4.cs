using Hanime1Downloader.CSharp.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hanime1Downloader.CSharp.Services;

public sealed partial class DownloadService
{
    private sealed record ResumeMetadata(string RequestUrl, string ResponseUrl, string EntityTag, long? TotalBytes);

    internal static string GetResumeMetadataPath(string temporaryPath) => temporaryPath + ".resume.tmp";

    private async Task DownloadSingleAttemptAsync(
        Uri uri,
        string finalPath,
        string tmpPath,
        IProgress<DownloadProgressInfo>? progress,
        int attempt,
        CancellationToken cancellationToken)
    {
        var metadataPath = GetResumeMetadataPath(tmpPath);
        RejectReparsePoint(tmpPath);
        RejectReparsePoint(metadataPath);
        var existingBytes = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
        var resume = existingBytes > 0 ? TryReadResumeMetadata(metadataPath, uri, existingBytes) : null;
        var speedWindow = new SpeedWindow();
        var startedAt = DateTime.UtcNow;

        AppLogger.Info("download", $"[attempt {attempt}] 下载: {uri} resume={resume is not null} existing={existingBytes}");
        if (attempt == 1)
        {
            ReportProgress(progress, speedWindow, resume is null ? 0 : existingBytes, resume?.TotalBytes, startedAt);
        }

        using var request = CreateRequest(HttpMethod.Get, uri, resume is null ? null : existingBytes, null);
        if (resume is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(resume.EntityTag));
        }
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (resume is not null &&
            (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
             (response.StatusCode == HttpStatusCode.PartialContent && !CanAppend(response, uri, resume, existingBytes))))
        {
            // 416 的长度不是内容身份；同长的不同版本也可能得到 416。校验不过时发起完整请求。
            AppLogger.Info("download", $"断点未通过校验，重新下载: {uri}");
            response.Dispose();
            File.Delete(metadataPath);
            using var restart = CreateRequest(HttpMethod.Get, uri, null, null);
            using var restarted = await _httpClient.SendAsync(restart, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await WriteMediaResponseAsync(restarted, uri, tmpPath, metadataPath, 0, speedWindow, startedAt, progress, cancellationToken);
        }
        else
        {
            var appendAt = resume is not null && response.StatusCode == HttpStatusCode.PartialContent ? existingBytes : 0;
            await WriteMediaResponseAsync(response, uri, tmpPath, metadataPath, appendAt, speedWindow, startedAt, progress, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        RejectReparsePoint(finalPath);
        File.Move(tmpPath, finalPath, overwrite: false);
        TryDelete(metadataPath);
    }

    private static ResumeMetadata? TryReadResumeMetadata(string path, Uri uri, long existingBytes)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllText(path));
            return metadata is not null &&
                   string.Equals(metadata.RequestUrl, uri.AbsoluteUri, StringComparison.Ordinal) &&
                   (metadata.TotalBytes is null || existingBytes <= metadata.TotalBytes) &&
                   EntityTagHeaderValue.TryParse(metadata.EntityTag, out var tag) && IsStrongEntityTag(tag)
                ? metadata
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Info("download", $"读取续传校验信息失败，将重新下载: {ex.Message}");
            return null;
        }
    }

    private static bool IsStrongEntityTag(EntityTagHeaderValue? tag) => tag is { IsWeak: false } && tag.Tag != "*";

    private static bool CanAppend(HttpResponseMessage response, Uri uri, ResumeMetadata resume, long existingBytes)
    {
        var tag = response.Headers.ETag;
        var range = response.Content.Headers.ContentRange;
        if (!IsStrongEntityTag(tag) || tag!.Tag != resume.EntityTag ||
            !string.Equals(GetEffectiveRequestUri(response, uri).AbsoluteUri, resume.ResponseUrl, StringComparison.Ordinal) ||
            range?.To is not long end || range.Length is not long total || end >= total ||
            (resume.TotalBytes is long previousTotal && previousTotal != total))
        {
            return false;
        }

        try
        {
            ValidatePartialResponse(response, existingBytes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task WriteMediaResponseAsync(
        HttpResponseMessage response,
        Uri uri,
        string tmpPath,
        string metadataPath,
        long appendAt,
        SpeedWindow speedWindow,
        DateTime startedAt,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        EnsureMediaResponse(response);
        long? totalBytes;
        long? responseEnd;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidatePartialResponse(response, appendAt);
            var range = response.Content.Headers.ContentRange!;
            if (range.To is not long end || range.Length is not long total || end >= total)
            {
                throw new InvalidDataException("部分响应没有有效的文件总长度，无法确认下载完整性。");
            }
            totalBytes = total;
            responseEnd = end + 1;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            responseEnd = totalBytes = response.Content.Headers.ContentLength;
        }
        else
        {
            throw new InvalidDataException($"媒体服务器返回了意外的状态码 {(int)response.StatusCode}。");
        }

        var tag = response.Headers.ETag;
        var metadata = appendAt == 0 && IsStrongEntityTag(tag)
            ? new ResumeMetadata(uri.AbsoluteUri, GetEffectiveRequestUri(response, uri).AbsoluteUri, tag!.Tag, totalBytes)
            : null;
        await ReadMediaStreamAsync(response, tmpPath, metadataPath, metadata, appendAt, speedWindow, startedAt,
            progress, cancellationToken, totalBytes, responseEnd);
    }

    private async Task ReadMediaStreamAsync(
        HttpResponseMessage response,
        string tmpPath,
        string metadataPath,
        ResumeMetadata? metadata,
        long bytesReceived,
        SpeedWindow speedWindow,
        DateTime startedAt,
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken,
        long? totalBytes,
        long? responseEnd)
    {
        var append = bytesReceived > 0;
        var keepPartialFile = append;
        var lastReportedBytes = bytesReceived;
        var lastReportedAt = Environment.TickCount64;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (!append)
            {
                // 先移除旧标识，截断数据后再保存新标识，避免旧字节被当作新资源的断点。
                File.Delete(metadataPath);
            }
            await using var output = new FileStream(tmpPath, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (append && output.Length != bytesReceived)
            {
                throw new IOException("临时文件长度在续传前发生变化，已拒绝追加数据。");
            }
            CheckRemainingDownloadSpace(tmpPath, totalBytes, bytesReceived);
            if (metadata is not null)
            {
                await AtomicFile.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata));
            }

            var buffer = new byte[BufferSize];
            int bytesRead;
            while ((bytesRead = await ReadWithIdleTimeoutAsync(input, buffer, cancellationToken)) > 0)
            {
                if (responseEnd is long limit && bytesRead > limit - bytesReceived)
                {
                    throw new InvalidDataException("响应正文超出服务器声明的媒体范围，已拒绝写入。");
                }
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesReceived += bytesRead;
                keepPartialFile = true;
                speedWindow.Add(bytesRead);

                var now = Environment.TickCount64;
                if (bytesReceived == totalBytes || bytesReceived - lastReportedBytes >= 512 * 1024 || now - lastReportedAt >= 150)
                {
                    ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
                    lastReportedBytes = bytesReceived;
                    lastReportedAt = now;
                }
            }

            await output.FlushAsync(cancellationToken);
            if (totalBytes is long expectedLength && bytesReceived != expectedLength)
            {
                throw new IOException($"下载流提前结束：收到 {bytesReceived} 字节，预期 {expectedLength} 字节。");
            }
            if (bytesReceived == 0)
            {
                throw new IOException("媒体服务器没有返回可保存的数据。");
            }
            ReportProgress(progress, speedWindow, bytesReceived, totalBytes, startedAt);
        }
        catch
        {
            if (!keepPartialFile && File.Exists(tmpPath) && new FileInfo(tmpPath).Length == 0)
            {
                TryDelete(tmpPath);
                TryDelete(metadataPath);
            }
            throw;
        }
    }

    private static void CheckRemainingDownloadSpace(string tmpPath, long? totalBytes, long bytesReceived)
    {
        if (totalBytes is not long total || total <= bytesReceived) return;
        long available;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(tmpPath)!);
            if (!drive.IsReady) return;
            available = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLogger.Info("download", $"磁盘空间预检跳过: {ex.Message}");
            return;
        }

        const long margin = 64L * 1024 * 1024;
        var remaining = total - bytesReceived;
        if (available - margin < remaining)
        {
            throw new InsufficientDownloadSpaceException(available, remaining);
        }
    }
}

internal sealed class InsufficientDownloadSpaceException(long availableBytes, long remainingBytes)
    : InvalidOperationException("磁盘空间不足。")
{
    public long AvailableBytes { get; } = availableBytes;
    public long RemainingBytes { get; } = remainingBytes;
}
