using System.IO;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>原子文件写入：先写临时文件再重命名，避免中途崩溃/断电截断目标文件。</summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content)
    {
        var tmp = path + ".atomic-tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllText(string path, string content)
    {
        var tmp = path + ".atomic-tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
