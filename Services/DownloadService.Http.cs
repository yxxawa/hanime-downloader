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
}
