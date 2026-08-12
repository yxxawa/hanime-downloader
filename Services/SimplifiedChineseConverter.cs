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
            var len = LCMapStringEx("zh-Hans", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, 0, 0, 0);
            var buf = new char[len];
            LCMapStringEx("zh-Hans", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buf, len, 0, 0, 0);
            // cchDest=0 时返回值含终止 NUL，TrimEnd 去掉尾部空字符。
            return new string(buf).TrimEnd('\0');
        }
        catch { return text; }
    }
}
