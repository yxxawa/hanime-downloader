using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    private static int _exit;
    private static int _requests;
    private static bool _challengePage;
    private static bool _holdImage;
    private static bool _holdDocument;
    private static int _responseStatus = 200;
    private static CoreWebView2Deferral? _documentDeferral;
    private static CoreWebView2WebResourceRequestedEventArgs? _documentRequest;
    private static CoreWebView2Deferral? _imageDeferral;
    private static CoreWebView2WebResourceRequestedEventArgs? _imageRequest;
    private static string _host = "";
    private static CloudflareWindow _window = null!;
    private static CoreWebView2 _core = null!;
    private static Button Finish => (Button)_window.FindName("FinishButton");

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { Console.WriteLine(ex); _exit = 1; }
            finally
            {
                ReleaseImage();
                ReleaseDocument();
                if (_window is not null)
                {
                    var source = (TaskCompletionSource<bool>?)typeof(CloudflareWindow).GetField("_verificationCompletionSource", Private)!.GetValue(_window);
                    source?.TrySetResult(false);
                    _window.Close();
                }
                app.Shutdown();
            }
        };
        app.Run();
        return _exit;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
        _passed++;
    }

    private static Task Call(string method) => (Task)typeof(CloudflareWindow).GetMethod(method, Private)!.Invoke(_window, null)!;

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for browser test state.");
            await Task.Delay(25);
        }
    }

    private static void AddClearance(DateTime expires)
    {
        var cookie = _core.CookieManager.CreateCookie("cf_clearance", "synthetic-test-value", _host, "/");
        cookie.IsSecure = true;
        cookie.Expires = expires;
        _core.CookieManager.AddOrUpdateCookie(cookie);
    }

    private static async Task RunAsync()
    {
        // A unique reserved host isolates browser storage; every request is intercepted locally.
        _host = $"verification-{Guid.NewGuid():N}.invalid";
        _window = new CloudflareWindow(_host);
        await Call("EnsureInitializedAsync").WaitAsync(TimeSpan.FromSeconds(30));
        _core = ((WebView2)_window.FindName("Browser")).CoreWebView2;
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += (_, e) =>
        {
            Interlocked.Increment(ref _requests);
            var uri = new Uri(e.Request.Uri);
            if (uri.Host != _host) throw new Exception("Unexpected external request: " + uri.Host);
            if (_holdDocument && uri.AbsolutePath == "/next-page")
            {
                _documentRequest = e;
                _documentDeferral = e.GetDeferral();
                return;
            }
            if (_holdImage && uri.AbsolutePath == "/pending-image")
            {
                _imageRequest = e;
                _imageDeferral = e.GetDeferral();
                return;
            }
            var html = _challengePage
                ? "<html><title>Just a moment...</title><script>window._cf_chl_opt={};</script><body>Challenge</body></html>"
                : "<html><title>Ready</title><body><a href='/watch?v=1'>Test</a></body></html>";
            if (_holdImage) html = html.Replace("</body>", "<img src='/pending-image'></body>");
            e.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), _responseStatus, _responseStatus == 200 ? "OK" : "Forbidden", "Content-Type: text/html\r\nCache-Control: no-store\r\n");
        };
        _core.CookieManager.DeleteAllCookies();
        Check(!await _window.HasValidSessionCookiesAsync(), "fresh WebView2 has no usable clearance");
        AddClearance(DateTime.UtcNow.AddMinutes(-1));
        Check(!await _window.HasValidSessionCookiesAsync(), "expired native browser Cookie is rejected");
        var before = _requests;
        CloudflareChallengeException? blocked = null;
        try { await new HanimeApiClient(_window).SearchAsync("first").WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (CloudflareChallengeException ex) { blocked = ex; }
        Check(blocked?.RequestUrl == $"https://{_host}/search?query=first&page=1" && before == _requests,
            "first real-browser search requests verification without issuing network requests");

        // Cancellation releases the startup navigation lock instead of waiting 15 seconds.
        using (var stopReuse = new CancellationTokenSource())
        {
            var reuse = _window.TryReuseSessionAsync(stopReuse.Token);
            await UntilAsync(() => _requests > before);
            stopReuse.Cancel();
            var canceled = false;
            try { await reuse.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "startup session reuse can be interrupted promptly by a user operation");
        }

        // The real WPF button must remain usable while the automatic probe is blocked.
        AddClearance(DateTime.UtcNow.AddMinutes(10));
        _challengePage = false;
        var probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verification = _window.VerifyAsync(confirmRecoveryAsync: async token =>
        {
            probeStarted.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { probeCanceled.TrySetResult(true); throw; }
            return false;
        }, isRecoveryConfirmed: () => false);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(!verification.IsCompleted && Finish.IsEnabled, "manual completion stays enabled during an unfinished automatic probe");
        var watch = Stopwatch.StartNew();
        Finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await verification.WaitAsync(TimeSpan.FromSeconds(3)), "clicking the real WPF button completes verification without waiting for the probe");
        await probeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(watch.Elapsed < TimeSpan.FromSeconds(3) && !_window.IsVisible && _window.CookieHeader.Contains("cf_clearance="),
            "manual completion exports Cookie, cancels the probe, and closes the window promptly");

        // A late result from a canceled verification cannot complete a different verification.
        var staleProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldVerification = _window.VerifyAsync(confirmRecoveryAsync: _ => { probeStarted.TrySetResult(true); return staleProbe.Task; });
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Call("CompleteVerificationManuallyAsync");
        Check(await oldVerification.WaitAsync(TimeSpan.FromSeconds(3)), "manual completion also interrupts a probe that ignores cancellation");
        _core.CookieManager.DeleteAllCookies();
        using (var cancelEmpty = new CancellationTokenSource())
        {
            var emptyVerification = _window.VerifyAsync(cancellationToken: cancelEmpty.Token, confirmRecoveryAsync: _ => Task.FromResult(true));
            await UntilAsync(() => _core.DocumentTitle == "Ready" && !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
            staleProbe.TrySetResult(true);
            await Call("CompleteVerificationManuallyAsync");
            await Task.Delay(100);
            Check(!emptyVerification.IsCompleted, "missing Cookie and late old probe cannot falsely complete a new verification");
            cancelEmpty.Cancel();
            bool accepted = false;
            try { accepted = await emptyVerification.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            Check(!accepted, "canceling verification never reports success");
        }

        // Even a valid Cookie cannot manually confirm a visible challenge page.
        AddClearance(DateTime.UtcNow.AddMinutes(10));
        _challengePage = true;
        using (var cancelChallenge = new CancellationTokenSource())
        {
            var challenged = _window.VerifyAsync(cancellationToken: cancelChallenge.Token, confirmRecoveryAsync: _ => Task.FromResult(true));
            await UntilAsync(() => _core.DocumentTitle.StartsWith("Just a moment"));
            await Call("CompleteVerificationManuallyAsync");
            Check(!challenged.IsCompleted, "manual completion still requires the browser challenge to be finished");
            cancelChallenge.Cancel();
            try { await challenged; } catch (OperationCanceledException) { }
        }

        // Automatic completion still waits for confirmation of the original request.
        _challengePage = false;
        var acceptedProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        probeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool confirmed = false;
        var automatic = _window.VerifyAsync(confirmRecoveryAsync: _ => { probeStarted.TrySetResult(true); return acceptedProbe.Task; }, isRecoveryConfirmed: () => confirmed);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(!automatic.IsCompleted, "automatic completion still waits for original-request confirmation");
        confirmed = true;
        acceptedProbe.SetResult(true);
        Check(await automatic.WaitAsync(TimeSpan.FromSeconds(3)), "automatic completion resumes once the original request is confirmed");
        Check(!Finish.IsEnabled, "completion disables the finish button to prevent duplicate submission");
        await CheckPendingImageAsync();
        await CheckVerificationTargetAsync();
        await CheckDelayedCookieAsync();
        await CheckPendingDocumentAsync();
        await CheckForbiddenPageAsync();
        await CheckForbiddenPageWithPendingImageAsync();
        Console.WriteLine($"TOTAL: {_passed} real WPF/WebView2 assertions passed. All responses were intercepted; no live-site requests.");
    }
    private static void ReleaseImage()
    {
        if (_imageDeferral is null) return;
        _imageRequest!.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(), 204, "No Content", "");
        _imageDeferral.Complete();
        _imageDeferral = null;
        _imageRequest = null;
        _holdImage = false;
    }

    private static async Task CheckPendingImageAsync()
    {
        _holdImage = true;
        var domReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs e) => domReady.TrySetResult(true);
        _core.DOMContentLoaded += OnDomReady;
        try
        {
            var verification = _window.VerifyAsync(confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
            await domReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await UntilAsync(() => _imageDeferral is not null);
            Check((bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!,
                "test page is visible while a slow image still blocks full navigation completion");
            await Call("CompleteVerificationManuallyAsync");
            Check(await verification.WaitAsync(TimeSpan.FromSeconds(3)),
                "manual capture completes with a ready page and valid Cookie without waiting for slow images");
        }
        finally
        {
            _core.DOMContentLoaded -= OnDomReady;
            ReleaseImage();
        }
    }

    private static async Task CheckVerificationTargetAsync()
    {
        var target = $"https://{_host}/search?query=actual-failure&page=2";
        var verification = _window.VerifyAsync(requestUrl: target, confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
        await UntilAsync(() => _core.Source == target && !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
        Check(_core.Source == target, "verification opens the exact failed path and query instead of the homepage");
        await Call("CompleteVerificationManuallyAsync");
        Check(await verification.WaitAsync(TimeSpan.FromSeconds(3)), "the actual failed page can be manually confirmed after recovery");
        var guarded = _window.VerifyAsync(requestUrl: "https://untrusted.invalid/search", confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
        await UntilAsync(() => _core.Source == $"https://{_host}/" && !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
        Check(_core.Source == $"https://{_host}/", "an unrelated verification URL cannot navigate outside the configured site");
        await Call("CompleteVerificationManuallyAsync");
        await guarded.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task CheckDelayedCookieAsync()
    {
        _core.CookieManager.DeleteAllCookies();
        var verification = _window.VerifyAsync(confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
        await UntilAsync(() => !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
        await Call("CompleteVerificationManuallyAsync");
        Check(!verification.IsCompleted, "a ready document without a Cookie remains pending");
        AddClearance(DateTime.UtcNow.AddMinutes(10));
        await Call("CompleteVerificationManuallyAsync");
        Check(await verification.WaitAsync(TimeSpan.FromSeconds(3)), "Cookie written after DOM readiness is captured without another navigation");
    }

    private static void ReleaseDocument()
    {
        if (_documentDeferral is null) return;
        var html = "<html><title>Ready</title><body>New document</body></html>";
        _documentRequest!.Response = _core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html");
        _documentDeferral.Complete();
        _documentDeferral = null;
        _documentRequest = null;
        _holdDocument = false;
    }

    private static async Task CheckPendingDocumentAsync()
    {
        _holdDocument = true;
        var verification = _window.VerifyAsync(requestUrl: $"https://{_host}/next-page", confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
        try
        {
            await UntilAsync(() => _documentDeferral is not null);
            await Call("CompleteVerificationManuallyAsync");
            Check(!verification.IsCompleted, "the previous healthy DOM cannot confirm a new document still in transit");
            ReleaseDocument();
            await UntilAsync(() => !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
            await Call("CompleteVerificationManuallyAsync");
            Check(await verification.WaitAsync(TimeSpan.FromSeconds(3)), "the new document can be confirmed after its own DOM becomes ready");
        }
        finally { ReleaseDocument(); }
    }

    private static async Task CheckForbiddenPageAsync()
    {
        _responseStatus = 403;
        using var stop = new CancellationTokenSource();
        try
        {
            var verification = _window.VerifyAsync(cancellationToken: stop.Token, confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
            await UntilAsync(() => !(bool)typeof(CloudflareWindow).GetField("_navigationPending", Private)!.GetValue(_window)!);
            await Call("CompleteVerificationManuallyAsync");
            var status = ((TextBlock)_window.FindName("StatusText")).Text;
            Check(!verification.IsCompleted && status.Contains("403") && status.Contains("Cookie"),
                "captured Cookie with HTTP 403 is reported as a rejected request, not a missing Cookie");
            stop.Cancel();
            try { await verification; } catch (OperationCanceledException) { }
        }
        finally { _responseStatus = 200; }
    }

    private static async Task CheckForbiddenPageWithPendingImageAsync()
    {
        _responseStatus = 403;
        _holdImage = true;
        using var stop = new CancellationTokenSource();
        var domReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs e) => domReady.TrySetResult(true);
        _core.DOMContentLoaded += OnDomReady;
        Task<bool>? verification = null;
        try
        {
            verification = _window.VerifyAsync(cancellationToken: stop.Token,
                confirmRecoveryAsync: token => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false));
            await domReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await UntilAsync(() => _imageDeferral is not null);
            await Call("CompleteVerificationManuallyAsync");
            var status = ((TextBlock)_window.FindName("StatusText")).Text;
            Check(!verification.IsCompleted && status.Contains("403"),
                "a forbidden document cannot be manually accepted while its images are still loading");
        }
        finally
        {
            stop.Cancel();
            if (verification is not null)
                try { await verification.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            _core.DOMContentLoaded -= OnDomReady;
            _responseStatus = 200;
            ReleaseImage();
        }
    }

}
