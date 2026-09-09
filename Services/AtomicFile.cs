using System.Collections.Concurrent;
using System.IO;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 原子文件写入：先写唯一临时文件再重命名，避免中途崩溃/断电截断目标文件。
/// 同一路径的并发写入用信号量串行化，避免共用临时文件互相踩。
/// </summary>
public static class AtomicFile
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task WriteAllTextAsync(string path, string content)
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var tmp = CreateTempPath(path);
            try
            {
                await File.WriteAllTextAsync(tmp, content);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public static void WriteAllText(string path, string content)
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var tmp = CreateTempPath(path);
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string CreateTempPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }
}
