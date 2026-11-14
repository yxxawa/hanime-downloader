using System.Diagnostics;
using System.Windows.Media;
using Hanime1Downloader.CSharp.Services;

internal static partial class Program
{
    private static async Task CheckHeaderFastPathAsync()
    {
        await _window.FetchHtmlAsync("fast-context");
        var headerOnly = await _window.TryFetchHtmlInPageAsync("header-only");
        Check(headerOnly is { Status: 200, Html.Length: 0, Challenge.Source: "browser-fetch-headers" },
            "real browser fetch detects the actual response header without relying on body markers or HTTP 403");

        // A real JS Response with an open ReadableStream makes r.text() impossible to complete.
        // Only the one test URL is shimmed; real browser and verification navigation remain intercepted by WebView2.
        await _core.ExecuteScriptAsync("""
            (() => {
              const original = window.fetch.bind(window);
              window.fastFetchCalls = 0;
              window.fastBodyCanceled = false;
              window.fetch = (input, init) => {
                const url = new URL(input, location.href);
                if (url.searchParams.get('query') !== 'header-fast') return original(input, init);
                window.fastFetchCalls++;
                const body = new ReadableStream({ cancel() { window.fastBodyCanceled = true; } });
                const response = new Response(body, { status: 200, headers: { 'cf-mitigated': 'challenge', 'content-type': 'text/html' } });
                Object.defineProperty(response, 'url', { value: url.href });
                return Promise.resolve(response);
              };
            })();
            """);
        CloudflareChallengeException? blocked = null;
        var started = Stopwatch.GetTimestamp();
        try { await new HanimeApiClient(_window).SearchAsync("header-fast").WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (CloudflareChallengeException ex) { blocked = ex; }
        Check(blocked?.Observation is { Source: "browser-fetch-headers", HeadersObservedAt: not null } &&
              blocked.RequestUrl == $"https://{_host}/search?query=header-fast&page=1" &&
              await _core.ExecuteScriptAsync("window.fastFetchCalls === 1 && window.fastBodyCanceled") == "true",
            $"open response body does not delay detection or trigger another request ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");

        _holdDocument = true;
        using var stop = new CancellationTokenSource();
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRender(object? sender, EventArgs args) { if (_window.IsVisible) rendered.TrySetResult(); }
        CompositionTarget.Rendering += OnRender;
        Task<bool>? verification = null;
        try
        {
            verification = _window.VerifyAsync(cancellationToken: stop.Token, showImmediately: true,
                requestUrl: blocked!.RequestUrl, observation: blocked.Observation);
            await UntilAsync(() => _documentDeferral is not null);
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(_window.IsVisible && !verification.IsCompleted && _documentDeferral is not null,
                "verification window reaches a render tick while the exact challenged navigation is still pending");
        }
        finally
        {
            CompositionTarget.Rendering -= OnRender;
            stop.Cancel();
            if (verification is not null)
                try { await verification.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            ReleaseDocument();
        }
    }

    private static async Task CheckBusinessNavigationAsync()
    {
        _challengePage = true;
        _holdImage = true;
        using var stop = new CancellationTokenSource();
        try
        {
            var navigation = _window.FetchHtmlAsync("watch?v=slow-image", stop.Token);
            await UntilAsync(() => _imageDeferral is not null);
            CloudflareChallengeException? blocked = null;
            try { await navigation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (CloudflareChallengeException ex) { blocked = ex; }
            Check(blocked?.Observation is { Source: "navigation-dom" } && blocked.RequestUrl == $"https://{_host}/watch?v=slow-image" &&
                  _imageDeferral is not null && (bool)typeof(Hanime1Downloader.CSharp.Views.CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!,
                "business navigation reports a challenge at DOM readiness without waiting for the slow image");
        }
        finally
        {
            stop.Cancel();
            _challengePage = false;
            ReleaseImage();
        }

        _holdImage = true;
        using var stopHealthy = new CancellationTokenSource();
        try
        {
            var navigation = _window.FetchHtmlAsync("watch?v=healthy-slow-image", stopHealthy.Token);
            await UntilAsync(() => _imageDeferral is not null);
            var result = await navigation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.Status == 200 && result.Title == "Ready" && result.Url.EndsWith("?v=healthy-slow-image", StringComparison.Ordinal) && _imageDeferral is not null,
                "healthy slow-image page returns the current DOM without a false challenge");
        }
        finally
        {
            stopHealthy.Cancel();
            ReleaseImage();
        }

        _responseStatus = 403;
        try
        {
            CloudflareChallengeException? blocked = null;
            try { await _window.FetchHtmlAsync("watch?v=plain-forbidden").WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (CloudflareChallengeException ex) { blocked = ex; }
            Check(blocked is { Observation: null } && blocked.Message.Contains("403", StringComparison.Ordinal),
                "ordinary top-level 403 still follows the existing rejection policy, not the confirmed challenge fast path");
        }
        finally { _responseStatus = 200; }

        _holdDocument = true;
        using var stopOld = new CancellationTokenSource();
        try
        {
            var navigation = _window.FetchHtmlAsync("next-page", stopOld.Token);
            await UntilAsync(() => _documentDeferral is not null);
            _core.Navigate($"https://{_host}/new-navigation");
            var rejected = false;
            try { await navigation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (InvalidOperationException ex) when (ex is not CloudflareChallengeException) { rejected = true; }
            Check(rejected, "a superseded business navigation cannot return another document or trigger a false challenge");
        }
        finally
        {
            stopOld.Cancel();
            ReleaseDocument();
        }
    }
}
