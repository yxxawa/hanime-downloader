using Hanime1Downloader.CSharp.Services;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Hanime1Downloader.CSharp;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(AppContext.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppTheme.Apply(this, AppTheme.ReadSavedThemeMode());
        TitleBarTheme.RegisterWindowAutoApply();
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now}] DispatcherUnhandledException:\n{args.Exception}\n\n");
            }
            catch
            {
            }
            AppLogger.Error("crash", "DispatcherUnhandledException", args.Exception);

            var summary = args.Exception.Message;
            if (summary.Length > 300)
            {
                summary = summary[..300] + "…";
            }
            var result = MessageBox.Show(
                $"程序遇到未处理的错误：\n\n{summary}\n\n选择[是]继续运行，[否]退出程序。",
                "Hanime1 下载工具 - 错误",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (result == MessageBoxResult.Yes)
            {
                args.Handled = true;
            }
            // 选择"否"→ args.Handled = false，进程退出（不再静默消失）。
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now}] UnhandledException:\n{args.ExceptionObject}\n\n");
            }
            catch
            {
            }
            AppLogger.Error("crash", "UnhandledException", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                File.AppendAllText(CrashLogPath, $"[{DateTime.Now}] UnobservedTaskException:\n{args.Exception}\n\n");
            }
            catch
            {
            }
            AppLogger.Error("crash", "UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
