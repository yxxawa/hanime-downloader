using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Hanime1Downloader.CSharp.Services;

namespace Hanime1Downloader.CSharp.Views;

public partial class QueueNamingDialog : Window
{
    private DownloadNamingContext? _context;
    public string NamingRule => RuleEditor.RuleText;
    public string? AuthorOverride => string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim();
    public string? DateOverride => string.IsNullOrWhiteSpace(DateBox.Text) ? null : DateBox.Text.Trim();

    public QueueNamingDialog(string rule, DownloadNamingContext context, int count, string? authorOverride, string? dateOverride)
    {
        InitializeComponent();
        _context = context;
        RuleEditor.RuleText = rule;
        ScopeText.Text = count == 1 ? $"仅修改此视频：{context.Title}" : $"仅修改选中的 {count} 个视频；预览第一项，作者和日期仍分别读取。";
        MetadataPanel.Visibility = count == 1 ? Visibility.Visible : Visibility.Collapsed;
        AuthorBox.Text = authorOverride ?? "";
        DateBox.Text = dateOverride ?? "";
        UpdatePreview();
    }
    private void Metadata_OnChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void UpdatePreview()
    {
        if (_context is null || AuthorBox is null || DateBox is null) return;
        RuleEditor.PreviewContext = _context with { Author = AuthorOverride, UploadDate = DateOverride ?? _context.UploadDate };
    }
    private void Apply_OnClick(object sender, RoutedEventArgs e)
    {
        if (!DownloadNaming.TryValidate(NamingRule, out var error))
        { MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (DateOverride is not null && !DateTime.TryParseExact(DateOverride, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        { MessageBox.Show(this, "发布日期请填写 yyyy-MM-dd，或留空自动读取。", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
