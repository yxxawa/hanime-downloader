using System.IO;
using System.Net;

namespace Hanime1Downloader.CSharp.Services;

public static class DownloadPathGuard
{
    public static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("下载目录不能为空。", nameof(directory));
        }

        var fullDirectory = Path.GetFullPath(directory.Trim());
        if (File.Exists(fullDirectory))
        {
            throw new IOException($"下载目录已被文件占用: {fullDirectory}");
        }

        Directory.CreateDirectory(fullDirectory);
        return fullDirectory;
    }

    public static string EnsureWithinDirectory(string directory, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new ArgumentException("目标路径不能为空。", nameof(candidatePath));
        }

        var fullDirectory = Path.GetFullPath(directory);
        var fullCandidate = Path.IsPathRooted(candidatePath)
            ? Path.GetFullPath(candidatePath)
            : Path.GetFullPath(Path.Combine(fullDirectory, candidatePath));
        var relative = Path.GetRelativePath(fullDirectory, fullCandidate);
        var outside = relative == "." ||
                      Path.IsPathRooted(relative) ||
                      relative.Equals("..", StringComparison.Ordinal) ||
                      relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                      relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        if (outside)
        {
            throw new ArgumentException("目标路径必须位于下载目录内。", nameof(candidatePath));
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullCandidate)))
        {
            throw new ArgumentException("目标路径必须指向文件。", nameof(candidatePath));
        }

        return fullCandidate;
    }

    public static string SanitizeFileName(string value, string fallback = "hanime")
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty)
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            cleaned = fallback;
        }

        var baseName = Path.GetFileNameWithoutExtension(cleaned);
        if (IsReservedWindowsName(baseName))
        {
            cleaned = "_" + cleaned;
        }

        const int maxFileNameLength = 180;
        if (cleaned.Length > maxFileNameLength)
        {
            cleaned = cleaned[..maxFileNameLength].TrimEnd('.', ' ');
        }

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static bool IsReservedWindowsName(string value)
    {
        var name = value.TrimEnd('.', ' ').ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
               name[3] is >= '1' and <= '9';
    }
}

