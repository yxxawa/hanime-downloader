# Custom download naming

Run from the repository root on Windows with the .NET 9 runtime and an SDK:

```powershell
dotnet run --project Tests/NamingRegression/NamingRegression.csproj --configuration Release
```

The suite uses production naming/path code, queue record types and WPF controls. It exercises
field order, author subfolders, explicit/automatic MP4 extension, legacy presets, missing data,
invalid rules, traversal attempts, metadata sanitization, queue JSON compatibility, resume-file
protection, live previews, preset selection, token insertion, settings save and settings JSON.
It also calls the real download output-preparation method to verify parent-folder creation.
There are **81 passing assertions**. Tests create only synthetic files in a unique temporary
folder; they do not change the user's saved settings, queue, cookies or media. Light/dark
preview PNGs and the test log are written to `artifacts`. The main window is not instantiated.

## Usage

- Settings > Download > Default naming rule: choose a preset, then edit the template in any order.
- Queue > right-click selected item(s) > Custom naming: apply a different rule to those items only.
- A single-item edit can override author and release date; a multi-item edit changes only the rule.
- Existing output, `.tmp` resume data, or `.hls` segments prevent per-item renaming. Pause the
  queue before editing. No existing downloads are moved, renamed, or deleted by this feature.
- New items retain the rule and naming time selected at enqueue. Metadata and quality are refreshed
  from full details before download. With queue persistence enabled, rules survive restart.

Examples:

```text
{author}\{date}-{name}-{quality}.MP4
{name}-{quality}.MP4
{quality}-{name}-{date}
{author}\{downloadDate}-{name}-{quality}
```

`{title}` keeps the full original title (legacy behavior). `{name}` removes one leading author
bracket. `{author}` uses the manually supplied author or the first `[author]` / `【author】`
prefix; this is a transparent title heuristic, not verified site-author metadata. Unknown authors
are labeled `未知作者` and can be corrected per item. `{date}` is the site's release/upload date
(or a per-item override), with `未知日期` when missing. `{downloadDate}` and `{timestamp}` use the
stored enqueue time so retries and restarts do not silently generate a different path. `{videoId}`
and `{quality}` remain supported. Slashes in metadata are sanitized; only separators in the
template create subfolders. Absolute paths, traversal components, unknown tokens and malformed
braces are rejected. This feature does not change network/session verification logic.
