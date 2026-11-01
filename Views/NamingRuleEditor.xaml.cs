using System.Windows;
using System.Windows.Controls;
using Hanime1Downloader.CSharp.Services;

namespace Hanime1Downloader.CSharp.Views;

public partial class NamingRuleEditor : UserControl
{
    public sealed record Preset(string Label, string Value);
    private DownloadNamingContext _context = new("示例视频标题", "123456", 1080, "示例作者", "2026-11-01");
    public string RuleText { get => TemplateBox.Text.Trim(); set => TemplateBox.Text = value; }
    public DownloadNamingContext PreviewContext { get => _context; set { _context = value; UpdatePreview(); } }

    public NamingRuleEditor()
    {
        InitializeComponent();
        PresetCombo.ItemsSource = new[]
        {
            new Preset("选择预设，或在下方自定义", ""),
            new Preset("作者目录 / 发布日期 - 标题 - 画质", DownloadNaming.AuthorRule),
            new Preset("标题 - 画质", DownloadNaming.SimpleRule),
            new Preset("标题 + 视频 ID（原默认）", DownloadNaming.DefaultRule),
            new Preset("标题", "{title}"),
            new Preset("标题 + 画质", "{title}_{quality}"),
            new Preset("标题 + 画质 + 时间", "{title}_{quality}_{timestamp}"),
            new Preset("视频 ID + 画质", "{videoId}_{quality}"),
            new Preset("标题 + 时间", "{title}_{timestamp}"),
            new Preset("时间", "{timestamp}")
        };
        PresetCombo.SelectedIndex = 0;
        RuleText = DownloadNaming.DefaultRule;
    }

    private void PresetCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset { Value.Length: > 0 } preset) RuleText = preset.Value;
    }
    private void TemplateBox_OnTextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void TokenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var start = TemplateBox.SelectionStart;
        TemplateBox.SelectedText = token;
        TemplateBox.CaretIndex = start + token.Length;
        TemplateBox.Focus();
    }
    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        var valid = DownloadNaming.TryValidate(RuleText, out var error);
        ErrorText.Text = error;
        ErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        PreviewText.Text = valid ? DownloadNaming.BuildRelativePath(RuleText, _context) : "";
    }
}
