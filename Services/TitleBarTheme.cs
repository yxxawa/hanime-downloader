using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hanime1Downloader.CSharp.Services;

/// <summary>
/// 系统标题栏深色模式：通过 DWM API 将非客户区（标题栏）设为深色/浅色。
/// WPF 的 Background 只影响客户区，标题栏颜色必须走 DwmSetWindowAttribute。
/// </summary>
public static class TitleBarTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;      // Windows 11 / 10 20H1+
    private const int DwmwaUseImmersiveDarkModeLegacy = 19; // 旧版 Windows 10

    private static bool _currentIsDark;

    /// <summary>切换标题栏深色模式并应用到当前所有窗口；之后新建的窗口由类级事件自动应用。</summary>
    public static void SetDark(bool isDark)
    {
        _currentIsDark = isDark;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTo(window);
        }
    }

    /// <summary>为所有窗口（含动态创建的）注册 Loaded 时自动应用主题（Loaded 时 hwnd 已就绪）。</summary>
    public static void RegisterWindowAutoApply()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTo((Window)sender)));
    }

    private static void ApplyTo(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = _currentIsDark ? 1 : 0;
            // 新版属性失败时回退旧版（旧版 Windows 10）。
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // 标题栏美化失败不影响功能。
        }
    }
}
