using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Microsoft.Web.WebView2.Core;

namespace Hanime1Downloader.CSharp.Views;

public partial class CloudflareWindow
{
    /// <summary>Records monotonic, per-window phases without logging URLs, cookies, or page content.</summary>
    private sealed class VerificationTiming(CloudflareWindow window, CloudflareChallengeObservation? observation) : IDisposable
    {
        private static int _nextId;
        private readonly int _id = Interlocked.Increment(ref _nextId);
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private long? _shownAt;
        private CoreWebView2? _core;
        private Uri? _target;
        private ulong? _navigationId;
        private long? _navigationAt;
        private bool _waitingForRender;

        public void WindowShown()
        {
            if (!window.IsVisible || _shownAt is not null) return;
            var now = Stopwatch.GetTimestamp();
            _shownAt = now;
            Write("shown", $"verify_to_show_ms={Elapsed(_startedAt, now)} " +
                $"headers_to_detect_ms={Elapsed(observation?.HeadersObservedAt, observation?.DetectedAt)} " +
                $"detect_to_show_ms={Elapsed(observation?.DetectedAt, now)}");
            _waitingForRender = true;
            CompositionTarget.Rendering += OnRender;
        }

        private void OnRender(object? sender, EventArgs args)
        {
            if (!window.IsVisible) return;
            StopRenderTracking();
            Write("render_tick", $"show_to_render_ms={Elapsed(_shownAt, Stopwatch.GetTimestamp())}");
        }

        public void TrackNavigation(CoreWebView2 core, Uri target)
        {
            _core = core;
            _target = target;
            core.NavigationStarting += OnNavigationStarting;
            core.DOMContentLoaded += OnDomReady;
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (_navigationId is null && !args.IsRedirected && _target is not null && IsSameDocumentRequest(_target, args.Uri))
            {
                _navigationId = args.NavigationId;
                _navigationAt = Stopwatch.GetTimestamp();
            }
        }

        private void OnDomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (_navigationId != args.NavigationId || window._navigationId != args.NavigationId ||
                !Uri.TryCreate(_core?.Source, UriKind.Absolute, out var source) || !window._sessionContext.IsTrustedOrigin(source))
                return;

            var now = Stopwatch.GetTimestamp();
            Write("dom_ready", $"nav_to_dom_ms={Elapsed(_navigationAt, now)} show_to_dom_ms={Elapsed(_shownAt, now)}");
            StopNavigationTracking();
        }

        private void Write(string phase, string elapsed) =>
            AppLogger.CloudflareTiming($"id={_id} phase={phase} source={observation?.Source ?? "verification"} {elapsed}");

        private static string Elapsed(long? start, long? end) => start is long from && end is long to
            ? Stopwatch.GetElapsedTime(from, to).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)
            : "-";

        private void StopRenderTracking()
        {
            if (!_waitingForRender) return;
            _waitingForRender = false;
            CompositionTarget.Rendering -= OnRender;
        }

        private void StopNavigationTracking()
        {
            if (_core is null) return;
            _core.NavigationStarting -= OnNavigationStarting;
            _core.DOMContentLoaded -= OnDomReady;
            _core = null;
        }

        public void Dispose()
        {
            StopRenderTracking();
            StopNavigationTracking();
        }
    }
}
