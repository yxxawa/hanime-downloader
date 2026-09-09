namespace Hanime1Downloader.CSharp.Services;

public static class BrowserIdentity
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36 Edg/147.0.3912.60";

    public static string BuildUserAgent(string? browserVersion)
    {
        var normalizedVersion = NormalizeBrowserVersion(browserVersion);
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            return DefaultUserAgent;
        }

        var majorVersion = normalizedVersion.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{majorVersion}.0.0.0 Safari/537.36 Edg/{normalizedVersion}";
    }

    public static string BuildSecChUa(string? browserVersion)
    {
        var normalizedVersion = NormalizeBrowserVersion(browserVersion);
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            return "\"Microsoft Edge\";v=\"147\", \"Chromium\";v=\"147\", \"Not=A?Brand\";v=\"24\"";
        }

        var majorVersion = normalizedVersion.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        return $"\"Microsoft Edge\";v=\"{majorVersion}\", \"Chromium\";v=\"{majorVersion}\", \"Not=A?Brand\";v=\"24\"";
    }

    public static string NormalizeBrowserVersion(string? browserVersion)
    {
        if (string.IsNullOrWhiteSpace(browserVersion))
        {
            return string.Empty;
        }

        var normalizedVersion = browserVersion.Trim();
        var separatorIndex = normalizedVersion.IndexOf(' ');
        if (separatorIndex >= 0)
        {
            normalizedVersion = normalizedVersion[..separatorIndex];
        }

        return normalizedVersion;
    }
}
