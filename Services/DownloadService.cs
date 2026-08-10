using Hanime1Downloader.CSharp.Models;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Hanime1Downloader.CSharp.Services;

public sealed class DownloadService(HttpClient httpClient, string siteHost = "hanime1.me")
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly Uri _referrer = new($"https://{siteHost}/");

    public async Task<DownloadProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        var targetUri = ValidateHttpUrl(url);
        using var request = CreateRequest(HttpMethod.Get, targetUri, 0, 0);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ValidatePartialResponse(response, 0);
        response.EnsureSuccessStatusCode();
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
        CancellationToken cancellationToken = default)
    {
        var targetUri = ValidateHttpUrl(url);
        var finalPath = ValidateOutputPath(outputPath);
        var tmpPath = finalPath + ".tmp";
        RejectReparsePoint(finalPath);
        RejectReparsePoint(tmpPath);

        var existingBytes = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : 0L;
        var requestedResume = existingBytes > 0;

        using var request = CreateRequest(HttpMethod.Get, targetUri, requestedResume ? existingBytes : null, null);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var isResume = requestedResume && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidatePartialResponse(response, isResume ? existingBytes : 0);
        }

        if (requestedResume && response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
        {
            response.EnsureSuccessStatusCode();
        }
        else if (!isResume)
        {
            response.EnsureSuccessStatusCode();
        }

        var contentLength = response.Content.Headers.ContentLength;
        var contentRange = response.Content.Headers.ContentRange;
        long? totalBytes;
        if (contentRange?.Length is long rangeLength)
        {
            totalBytes = rangeLength;
            if (isResume && rangeLength < existingBytes)
            {
                throw new InvalidDataException("服务器返回的总长度小于本地临时文件，已拒绝续传。");
            }
        }
        else if (contentLength is long length)
        {
            totalBytes = isResume ? checked(existingBytes + length) : length;
        }
        else
        {
            totalBytes = null;
        }

        var fileMode = isResume ? FileMode.Append : FileMode.Create;
        var bytesReceived = isResume ? existingBytes : 0L;
        var startedAt = DateTime.UtcNow;
        var lastReportedBytes = bytesReceived;
        var lastReportedAt = Environment.TickCount64;
        var keepPartialFile = requestedResume;
        var moved = false;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                tmpPath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                bytesReceived += bytesRead;
                keepPartialFile = bytesReceived > 0;

                var now = Environment.TickCount64;
                var shouldReport = bytesReceived == totalBytes ||
                                   bytesReceived - lastReportedBytes >= 512 * 1024 ||
                                   now - lastReportedAt >= 150;
                if (shouldReport)
                {
                    var elapsedSeconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001d);
                    progress?.Report(new DownloadProgressInfo
                    {
                        BytesReceived = bytesReceived,
                        TotalBytes = totalBytes,
                        BytesPerSecond = bytesReceived / elapsedSeconds
                    });
                    lastReportedBytes = bytesReceived;
                    lastReportedAt = now;
                }
            }

            if (totalBytes.HasValue && bytesReceived != totalBytes.Value)
            {
                throw new InvalidDataException($"下载长度校验失败：收到 {bytesReceived} 字节，预期 {totalBytes.Value} 字节。");
            }

            if (bytesReceived != lastReportedBytes)
            {
                var elapsedSeconds = Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001d);
                progress?.Report(new DownloadProgressInfo
                {
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes,
                    BytesPerSecond = bytesReceived / elapsedSeconds
                });
            }
        }
        finally
        {
            if (!moved && !keepPartialFile && File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }
        }

        RejectReparsePoint(finalPath);
        File.Move(tmpPath, finalPath, overwrite: true);
        moved = true;
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

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, long? rangeFrom, long? rangeTo)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = _referrer;
        if (rangeFrom.HasValue || rangeTo.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(rangeFrom, rangeTo);
        }
        return request;
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
}
