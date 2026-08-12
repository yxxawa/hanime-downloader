using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// Shared retry policy for media requests. Cancellation from the caller always wins over retries.
/// </summary>
public sealed class DownloadRetryPolicy
{
    public DownloadRetryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null)
    {
        MaxAttempts = Math.Clamp(maxAttempts, 1, 8);
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(350);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
    }

    public int MaxAttempts { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaxDelay { get; }

    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts || !ShouldRetry(ex, cancellationToken))
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }

                lastException = ex;
                var delay = ComputeDelay(attempt);
                AppLogger.Info("download-retry", $"第 {attempt} 次请求失败，{delay.TotalMilliseconds:0}ms 后重试: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }

        ExceptionDispatchInfo.Capture(lastException ?? new InvalidOperationException("下载重试没有得到结果。")).Throw();
        throw new InvalidOperationException();
    }

    public static bool ShouldRetry(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is TimeoutException or IOException)
        {
            return true;
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode is null || IsTransientStatus(httpException.StatusCode.Value);
        }

        return false;
    }

    public static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
            (int)statusCode == 425 ||
            (int)statusCode >= 500;
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 6);
        var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        milliseconds = Math.Min(milliseconds, MaxDelay.TotalMilliseconds);
        // A small bounded jitter prevents several queue workers from retrying simultaneously.
        milliseconds += Random.Shared.Next(0, Math.Max(1, (int)Math.Min(250, milliseconds * 0.2)));
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxDelay.TotalMilliseconds + 250));
    }
}
