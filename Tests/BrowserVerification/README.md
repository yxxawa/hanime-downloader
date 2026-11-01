# Browser verification regression checks

From the repository root on Windows with .NET 9 and WebView2 Runtime installed:

```powershell
dotnet run --project Tests/BrowserVerification/BrowserVerification.csproj --configuration Release
```

This test uses the production WPF window, WebView2, Cookie manager, and API client. It builds
the app into a separate `artifacts/browser-verification-app` directory so a running normal
application does not lock the test build. A unique reserved `.invalid` host isolates browser
storage from user sessions. Every resource request is intercepted with synthetic HTML; no
real-site requests, credentials, or CAPTCHA actions are involved.

The current **25 checks** passed on 2026-11-01. They cover missing and expired Cookies, first-request
escalation without network retries, cancellation of startup reuse, the real WPF finish-button
click while an automatic probe is pending, Cookie export, cancellation of that probe, a probe
that ignores cancellation, stale results, a visible challenge, cancellation, and automatic
completion after the original request is confirmed.

The capture regression was reproduced before the fix: valid Cookie plus a ready document still
could not complete while an image request was held open. The fix tracks `DOMContentLoaded` by
navigation ID, captures Cookies independently of full load completion, and verifies the current
document's HTTP status. A new document still in transit cannot reuse a previous healthy DOM.
The extended checks include delayed Cookie writes, the exact failed verification URL, an
untrusted target, ordinary 403 and 403 with a pending image. Both 403 cases remain rejected and
are reported separately from missing Cookies. The image and document requests are held with
WebView2 deferrals, so the tests exercise real browser event ordering instead of assigning
production readiness fields.

Manual completion validates the current page and applicable Cookie only, then lets the actual
operation retry. It intentionally does not wait for all automatic endpoint probes. Automatic
completion retains the stricter original-request confirmation. The test does not instantiate
the full main window or claim real-site acceptance of a Cookie.
