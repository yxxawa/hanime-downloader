using Hanime1Downloader.CSharp.Services;
using System.Diagnostics;
using System.Windows;

namespace Hanime1Downloader.CSharp;

public partial class MainWindow
{
    private string? _pendingUpdateUrl;

    /// <summary>启动后后台检查 GitHub Release；失败或已是最新时静默。</summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        var current = UpdateChecker.GetCurrentVersion();
        var result = await UpdateChecker.CheckAsync(current);
        if (result is null || !result.HasUpdate || result.LatestVersion is null)
        {
            return;
        }

        _pendingUpdateUrl = result.ReleaseUrl;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.ToolTip = "发现新版本 " + result.TagName + "（当前 v" + current + "），点击打开下载页面";
        StatusText.Text = "发现新版本 " + result.TagName + "（当前 v" + current + "），可点击工具栏「有新版本」下载。";
        LogInfo("update", "发现新版本: " + result.TagName + "（当前 v" + current + "）-> " + result.ReleaseUrl);
    }

    private void UpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingUpdateUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_pendingUpdateUrl) { UseShellExecute = true });
            StatusText.Text = "已打开新版本下载页面。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("update", "打开下载页面失败", ex);
        }
    }
}
