# Session regression checks

Run from the repository root on Windows with the .NET 9 SDK/runtime:

```powershell
dotnet run --project Tests/SessionRegression/SessionRegression.csproj
```

This offline console test links the production session policy, cookie bridge, API and account sources.
Only browser and HTTP boundaries are stubbed. It checks trusted com/me redirects, custom-host isolation,
original cookie domains/path/security/expiry, dynamic request/link origins, bounded browser recovery, and final challenge escalation.
No real cookies, account credentials, browser profile, or network responses are stored here.
These checks do not replace a real WebView2/network integration test.

## Cookie capture and page readiness (2026-11-01)

The current suite passes **152 offline assertions**, and `../BrowserVerification` passes
**25 real WPF/WebView2 assertions** with intercepted responses. Release publish succeeds.

Two defects were reproduced before fixing them:

- A ready page with a valid clearance could not complete verification while a slow image kept
  `NavigationCompleted` pending. Cookie capture now runs independently of page loading, and
  completion uses the current navigation's `DOMContentLoaded` event plus document readiness,
  a non-challenge page, a successful HTTP status, and the applicable Cookie. Delayed Cookie
  writes are captured on subsequent checks. Stale documents and HTTP 403 are still rejected,
  including a 403 document whose image is held open. On older browser runtimes without the
  navigation timing response status, the native completed-navigation status is the fallback.
- A browser snapshot with more than 20 Cookies on one domain could lose its clearance when
  converted to a default .NET CookieContainer. Container limits now accommodate the supplied
  snapshot; Cookie domain, path, security, and expiry validation stay intact. The two new
  offline assertions cover both clearance detection and the outgoing Cookie header.

Session recovery now navigates to the blocked request's path and query, resolved against the
verified trusted origin. Homepage readiness is no longer substituted for the page the user
needs to verify. Status messages distinguish missing Cookies, a still-running challenge, a
pending document, and an HTTP rejection despite an already captured Cookie.

Read-only local diagnostics showed a saved clearance and a visible search HTTP 403 in the
running app. Cookie values were not printed or copied into this repository. This supports
separating capture from server rejection; it does not identify why the server rejected that
request. No real-site acceptance test was performed for this revision. The historical counts
and behaviors below describe earlier revisions.

## First search and manual verification completion (2026-11-01)

At that revision, the suite passed **150 offline assertions**. Five new assertions verify that searches,
details, and account requests without an applicable clearance request verification immediately,
without entering HTTP retries, page recovery, or navigation, and that the same search can retry
after a Cookie is available. The real-browser checks in `../BrowserVerification` add **14 passing
WPF/WebView2 assertions** for the Cookie manager and verification window.

Starting a search or manual verification cancels and awaits startup session reuse, preventing
old startup work from importing cached Cookies or resetting the client afterward. Startup
reuse now honors cancellation while waiting for browser initialization, the navigation lock,
and page/Cookie readiness. Queue URL refresh runs in the background after verification so it
does not delay the user's original request or replace its status message.

The button is now **verification complete**, independent of the automatic-probe lock. It reads
the current page and Cookie, completes verification, and cancels pending probes without waiting
for all endpoints. It does not accept a challenge page or missing/expired Cookie. Automatic
completion still requires original-request confirmation. This supersedes the historical
manual-recheck-only behavior documented below.

## Issue #12: challenge recovery (2026-11-01)

At that revision, the suite passed **145 assertions** (50 added for this fix). Before the fix, the new
public-search reproduction threw `CloudflareChallengeException` even though the stubbed browser
had a healthy response ready after context recovery. The previous suite explicitly expected
recovery to be skipped, so those expectations now require bounded recovery before escalation.
The full Release application also builds with zero warnings and errors.

The confirmed defect is ending the fallback chain on a blocked response. Receiving HTML is not
proof that a request was accepted. Direct HTTP now stops retrying on a challenge, enters the
existing cooldown, and lets its caller try browser recovery. The response is retained as evidence
instead of being discarded with `return null`: if context recovery and navigation both fail,
its URL and challenge must still reach the existing verification handler.

An initial page-fetch challenge also triggers context recovery, without an extra direct HTTP
probe. A challenge after that one retry falls back to one full navigation. The account GET path
uses the same response classification, including plain HTTP 403. Persistent challenges still
raise `CloudflareChallengeException`; cancellation remains cancellation; transport errors without
an observed challenge remain transport errors. Challenge responses are never cached.

The new checks cover challenge HTML with HTTP 200 and 403, plain 403, recovery and navigation
success/failure, final blocked-request URLs, HTTP cooldown, cache behavior, account/favorites,
both details paths, cancellation at each stage, and concurrent requests with one canceled caller.
The command at the top is the CI regression guard; all responses are synthetic and no real-site
Cloudflare acceptance or visible verification-window behavior was tested in this run.

### Variant check

Impact is medium (recoverable operations fail); confidence is high for the confirmed code paths.
The root-level exact search matched the direct-HTTP block once. Generalizing to non-null response
returns found eight candidates: five affected returns (three API fetch routes and two account GET
routes), plus three safe returns of already parsed/validated details. These were the searches:

```text
if \(result.Status == 403.*\r?\n(?:[^\r\n]*\r?\n){0,6}\s*return result;
if \(\w+ is not null\)\r?\n\s*\{\r?\n(?:[^\r\n]*\r?\n){0,2}\s*return (?:\w+|ValidateHtml\(\w+\));
```

An initial exact expression using `[^}]+` found nothing because interpolated log strings contain
braces; bounded line matching found the real branch. The verification probe is not affected:
`VerificationRequestPolicy.IsAccepted` rejects challenges and mismatched responses. Account POSTs
were reviewed separately and are not automatically replayed by this GET recovery change.

The issue's claim that an exception necessarily prevents a verification window is **not established**
(informational, high confidence about this snapshot's code): search and details already catch
Cloudflare errors and call `EnsureVerifiedSessionAsync`. The reported TLS-fingerprint explanation
is also unverified; neither the issue nor these offline checks establish the server's rejection
reason. The older live/integration observations below remain historical, not validation of this fix.

## Live verification status (2026-10-26, Asia/Shanghai)

The patched Debug assembly was exercised with real WebView2 and the existing configured
`hanime1.com` profile. No production session fields were overridden and no manual verification
was triggered. User settings stayed unchanged. No media files were downloaded.

- The normal redirect to `hanime1.me` is accepted; session reuse reaches a non-challenge page.
- Cookie export retains both aliases with original domains; the raw header includes only one
  matching clearance cookie. Outgoing Chromium network events confirm `cf_clearance` is sent
  to the actual `.me` request origin (no cookie values are recorded).
- With the browser HTTP cache disabled, the real search returns HTTP 200 and 59 results;
  output links use `.me`, including for an API client created before the redirect.
- **End-to-end verification is NOT fully passing:** another search and two detail requests
  return Cloudflare challenges. The detail requests demonstrably include `cf_clearance`
  with no browser cookie-blocking reasons. The reason for server rejection is not established;
  it must not be explained away as a missing cookie, nor hidden by suppressing verification.
- Some initial reuse attempts time out on a real challenge page; they correctly do not report
  success solely because an old unexpired cookie exists.

At that stage, the offline suite passed 59 assertions. Debug and Release builds succeed with zero warnings
and errors. This evidence validates the local host/session-routing repair, not a guarantee
that every request will be accepted by the site. That host/session-routing repair did not suppress verification prompts or change retry limits. A full search/detail acceptance test remains open with a server-accepted session.


## Automatic verification completion (2026-10-26)

Verification now continues automatically after a non-challenge page has an applicable clearance
cookie **and all original failed requests return a matching, non-challenge 2xx response**.
The existing button is now a recheck action, not a manual success override. Homepage or login
redirects cannot confirm a failed search/detail request. New concurrent failures invalidate an
older confirmation; cancellation cannot report success. Popup suppression and business retry
limits are not used as a workaround.

The persistent offline suite now passes **95 assertions**, including blocked-URL propagation,
strict response matching, shared-request confirmation, late joiners, and cancellation.

A separate controlled integration run exercised the compiled application with real WPF and
WebView2. **All 16 assertions passed**: two concurrent searches shared a verification window;
a normal homepage without clearance did not close it; clearance with original HTTP 403 did
not close it; manual recheck did not bypass the failure; one recovered endpoint could not mask
a second blocked endpoint; both endpoints recovering hid the window automatically and resumed
the original search; cancellation hid the window without reporting success.

That integration run used intercepted responses and a synthetic cookie on an isolated `.invalid`
host, with separate settings and browser storage. It made **zero real-site requests** and took
no CAPTCHA actions. It validates the automatic-close/resume control flow, **not** server-side
acceptance of a real Cloudflare session. The live-site caveats above still apply.


## Response logging regression (2026-10-26)

Page-context fetch, direct HTTP fallback, and fetch after context recovery now log receipt of a
response rather than declaring success before validation. Each receipt includes its HTTP status
and `blocked` flag (HTTP 403 or a recognized challenge page). Receiving HTTP 200 alone is not
reported as successful verification. The existing confirmed non-challenge 2xx retry-success
message remains valid and unchanged.

Twelve additional offline cases cover all three routes with ordinary 200, plain 403,
challenge 403, and challenge 200 responses. They check truthful receipt logs and unchanged
routing/Cloudflare exception propagation. The suite passes **95 assertions**. This patch changes
logging only; it does not change fallback policy, validation, verification windows, or retries.
