# Session regression checks

Run from the repository root on Windows with the .NET 9 SDK/runtime:

```powershell
dotnet run --project Tests/SessionRegression/SessionRegression.csproj
```

This offline console test links the production session policy, cookie bridge, API and account sources.
Only browser and HTTP boundaries are stubbed. It checks trusted com/me redirects, custom-host isolation,
original cookie domains/path/security/expiry, dynamic request/link origins, and unchanged challenge escalation.
No real cookies, account credentials, browser profile, or network responses are stored here.
These checks do not replace a real WebView2/network integration test.

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
