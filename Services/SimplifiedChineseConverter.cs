using System.Runtime.InteropServices;

namespace Hanime1Downloader.CSharp.Services;

public static class SimplifiedChineseConverter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string? lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc, char[]? lpDestStr, int cchDest, nint lpVersionInfo, nint lpReserved, nint sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    public static string ToSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            // 先按 2 倍长度预分配，绝大多数简繁映射是 1:1，可省掉「先探长度再转换」的一次调用。
            var buf = new char[Math.Max(8, text.Length * 2 + 1)];
            var written = LCMapStringEx("zh-Hans", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buf, buf.Length, 0, 0, 0);
            if (written <= 0)
            {
                // 缓冲区不够时回退到两段式调用。
                var len = LCMapStringEx("zh-Hans", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, 0, 0, 0);
                if (len <= 0)
                {
                    return text;
                }

                buf = new char[len];
                written = LCMapStringEx("zh-Hans", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buf, len, 0, 0, 0);
                if (written <= 0)
                {
                    return text;
                }
            }

            // cchDest>0 时返回值包含终止 NUL。
            var length = written <= buf.Length && buf[written - 1] == '\0' ? written - 1 : written;
            return new string(buf, 0, Math.Max(0, length));
        }
        catch { return text; }
    }
}
