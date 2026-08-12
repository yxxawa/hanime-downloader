using Hanime1Downloader.CSharp.Models;
using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Hanime1Downloader.CSharp.Views;

public partial class SettingsDialog : Window
{
    private static readonly Regex SiteHostRegex = new(@"^[a-z0-9.-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] PresetSiteHosts = ["hanime1.me", "hanime1.com", "hanimeone.me", "javchu.com"];
    private readonly ObservableCollection<SiteOption> _siteOptions = [];
    public AppSettings Settings { get; }

    public SettingsDialog(AppSettings currentSettings)
    {
        InitializeComponent();
        Settings = new AppSettings
        {
            DownloadPath = currentSettings.DownloadPath,
            FileNamingRule = currentSettings.FileNamingRule,
            ShowListCovers = currentSettings.ShowListCovers,
            CompactMode = currentSettings.CompactMode,
            ThemeMode = currentSettings.ThemeMode,
            DefaultQuality = currentSettings.DefaultQuality,
            SiteHost = currentSettings.SiteHost,
            CustomSiteHosts = currentSettings.CustomSiteHosts.ToList(),
            PersistDownloadQueue = currentSettings.PersistDownloadQueue,
            MaxConcurrentDownloads = currentSettings.MaxConcurrentDownloads,
            MaxRetries = currentSettings.MaxRetries,
            SpeedLimitKBps = currentSettings.SpeedLimitKBps,
            SearchHistory = currentSettings.SearchHistory.ToList(),
            VideoDetailsVisibility = new VideoDetailsVisibilitySettings
            {
                Title = currentSettings.VideoDetailsVisibility.Title,
                UploadDate = currentSettings.VideoDetailsVisibility.UploadDate,
                Likes = currentSettings.VideoDetailsVisibility.Likes,
                Views = currentSettings.VideoDetailsVisibility.Views,
                Duration = currentSettings.VideoDetailsVisibility.Duration,
                Tags = currentSettings.VideoDetailsVisibility.Tags,
                Cover = currentSettings.VideoDetailsVisibility.Cover,
                RelatedVideos = currentSettings.VideoDetailsVisibility.RelatedVideos
            }
        };

        NamingRuleCombo.ItemsSource = new List<NamingRuleOption>
        {
            new NamingRuleOption { Label = "标题", Value = "{title}" },
            new() { Label = "标题 + 画质", Value = "{title}_{quality}" },
            new() { Label = "标题 + 视频ID", Value = "{title}_{videoId}" },
            new() { Label = "标题 + 画质 + 时间", Value = "{title}_{quality}_{timestamp}" },
            new() { Label = "视频ID + 画质", Value = "{videoId}_{quality}" },
            new() { Label = "标题 + 时间", Value = "{title}_{timestamp}" },
            new() { Label = "时间", Value = "{timestamp}" }
        };

        InitializeSiteOptions();
        var normalizedCurrentSiteHost = NormalizeSiteHost(Settings.SiteHost);
        SiteCombo.ItemsSource = _siteOptions;
        SiteCombo.Loaded += (_, _) => AttachSiteComboEditorHandlers();

        QualityCombo.ItemsSource = new List<NamingRuleOption>
        {
            new() { Label = "最高画质", Value = "highest" },
            new() { Label = "720p", Value = "720" },
            new() { Label = "480p", Value = "480" },
            new() { Label = "最低画质", Value = "lowest" }
        };

        ThemeModeCombo.ItemsSource = new List<NamingRuleOption>
        {
            new() { Label = "浅色", Value = "light" },
            new() { Label = "深色", Value = "dark" }
        };

        MaxConcurrentDownloadsCombo.ItemsSource = new List<NamingRuleOption>
        {
            new() { Label = "1", Value = "1" },
            new() { Label = "2", Value = "2" },
            new() { Label = "3", Value = "3" }
        };

        MaxRetriesCombo.ItemsSource = Enumerable.Range(0, 9)
            .Select(count => new NamingRuleOption { Label = count == 0 ? "不重试" : count.ToString(), Value = count.ToString() })
            .ToList();

        DownloadPathBox.Text = Settings.DownloadPath;
        NamingRuleCombo.SelectedValue = Settings.FileNamingRule;
        SiteCombo.SelectedValue = normalizedCurrentSiteHost;
        SiteCombo.Text = normalizedCurrentSiteHost;
        QualityCombo.SelectedValue = Settings.DefaultQuality;
        ThemeModeCombo.SelectedValue = Settings.ThemeMode;
        ShowListCoversCheckBox.IsChecked = Settings.ShowListCovers;
        CompactModeCheckBox.IsChecked = Settings.CompactMode;
        PersistQueueCheckBox.IsChecked = Settings.PersistDownloadQueue;
        MaxConcurrentDownloadsCombo.SelectedValue = Math.Clamp(Settings.MaxConcurrentDownloads, 1, 3).ToString();
        MaxRetriesCombo.SelectedValue = Math.Clamp(Settings.MaxRetries, 0, 8).ToString();
        SpeedLimitBox.Text = Math.Max(0, Settings.SpeedLimitKBps).ToString();
        ShowTitleCheckBox.IsChecked = Settings.VideoDetailsVisibility.Title;
        ShowUploadDateCheckBox.IsChecked = Settings.VideoDetailsVisibility.UploadDate;
        ShowLikesCheckBox.IsChecked = Settings.VideoDetailsVisibility.Likes;
        ShowViewsCheckBox.IsChecked = Settings.VideoDetailsVisibility.Views;
        ShowDurationCheckBox.IsChecked = Settings.VideoDetailsVisibility.Duration;
        ShowTagsCheckBox.IsChecked = Settings.VideoDetailsVisibility.Tags;
        ShowCoverCheckBox.IsChecked = Settings.VideoDetailsVisibility.Cover;
        ShowRelatedVideosCheckBox.IsChecked = Settings.VideoDetailsVisibility.RelatedVideos;
        if (NamingRuleCombo.SelectedIndex < 0)
        {
            NamingRuleCombo.SelectedIndex = 0;
        }

        UpdateDownloadPathHint();
        UpdateNamingPreview();
        UpdateSiteHint();
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            FolderName = DownloadPathBox.Text
        };
        if (dialog.ShowDialog() == true)
        {
            DownloadPathBox.Text = dialog.FolderName;
        }
    }

    private void DownloadPathBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDownloadPathHint();
    }

    private void NamingRuleCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNamingPreview();
    }

    private void SiteCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteCombo.SelectedItem is SiteOption option)
        {
            SiteCombo.Text = option.Value;
            SiteCombo.SelectedValue = option.Value;
        }

        UpdateSiteHint();
    }

    private void SiteCombo_OnEditChanged(object sender, RoutedEventArgs e)
    {
        UpdateSiteHint();
    }

    private void SiteCombo_OnEditChanged(object sender, KeyEventArgs e)
    {
        UpdateSiteHint();
    }

    private void AttachSiteComboEditorHandlers()
    {
        if (SiteCombo.Template.FindName("PART_EditableTextBox", SiteCombo) is not TextBox textBox)
        {
            return;
        }

        textBox.TextChanged -= SiteComboTextBox_OnTextChanged;
        textBox.TextChanged += SiteComboTextBox_OnTextChanged;
        textBox.PreviewMouseLeftButtonDown -= SiteComboTextBox_OnPreviewMouseLeftButtonDown;
        textBox.PreviewMouseLeftButtonDown += SiteComboTextBox_OnPreviewMouseLeftButtonDown;
        textBox.PreviewMouseLeftButtonUp -= SiteComboTextBox_OnPreviewMouseLeftButtonUp;
        textBox.PreviewMouseLeftButtonUp += SiteComboTextBox_OnPreviewMouseLeftButtonUp;
        textBox.PreviewGotKeyboardFocus -= SiteComboTextBox_OnPreviewGotKeyboardFocus;
        textBox.PreviewGotKeyboardFocus += SiteComboTextBox_OnPreviewGotKeyboardFocus;
    }

    private void SiteComboTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSiteHint();
    }

    private static void SiteComboTextBox_OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        textBox.Dispatcher.BeginInvoke(() => textBox.Select(textBox.CaretIndex, 0), DispatcherPriority.Background);
    }

    private static void SiteComboTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        textBox.Focus();
    }

    private static void SiteComboTextBox_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var charIndex = textBox.GetCharacterIndexFromPoint(e.GetPosition(textBox), true);
        textBox.CaretIndex = charIndex >= 0 ? charIndex : textBox.Text.Length;
        textBox.Select(textBox.CaretIndex, 0);
    }

    private void SiteOptionDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string siteHost)
        {
            return;
        }

        if (button.DataContext is not SiteOption { IsCustom: true })
        {
            e.Handled = true;
            return;
        }

        RemoveCustomSite(siteHost);
        e.Handled = true;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        var downloadPath = string.IsNullOrWhiteSpace(DownloadPathBox.Text) ? AppSettings.DefaultDownloadPath : DownloadPathBox.Text.Trim();
        if (!TryValidateDownloadPath(downloadPath, out var pathError))
        {
            MessageBox.Show(this, pathError, "保存设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var siteHost = NormalizeSiteHost(SiteCombo.Text);
        if (!TryValidateSiteHost(siteHost, out var siteError))
        {
            MessageBox.Show(this, siteError, "保存设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddCustomSiteIfNeeded(siteHost);
        Settings.DownloadPath = downloadPath;
        Settings.FileNamingRule = NamingRuleCombo.SelectedValue as string ?? "{title}";
        Settings.SiteHost = siteHost;
        Settings.DefaultQuality = QualityCombo.SelectedValue as string ?? "highest";
        Settings.ShowListCovers = ShowListCoversCheckBox.IsChecked == true;
        Settings.CompactMode = CompactModeCheckBox.IsChecked == true;
        Settings.ThemeMode = ThemeModeCombo.SelectedValue as string ?? "light";
        Settings.PersistDownloadQueue = PersistQueueCheckBox.IsChecked == true;
        Settings.MaxConcurrentDownloads = int.TryParse(MaxConcurrentDownloadsCombo.SelectedValue as string, out var maxConcurrentDownloads)
            ? Math.Clamp(maxConcurrentDownloads, 1, 3)
            : 1;
        Settings.MaxRetries = int.TryParse(MaxRetriesCombo.SelectedValue as string, out var maxRetries)
            ? Math.Clamp(maxRetries, 0, 8)
            : 3;
        Settings.SpeedLimitKBps = int.TryParse(SpeedLimitBox.Text.Trim(), out var speedLimitKbps) && speedLimitKbps > 0
            ? Math.Min(speedLimitKbps, 1_000_000)
            : 0;
        Settings.VideoDetailsVisibility.Title = ShowTitleCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.UploadDate = ShowUploadDateCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.Likes = ShowLikesCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.Views = ShowViewsCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.Duration = ShowDurationCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.Tags = ShowTagsCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.Cover = ShowCoverCheckBox.IsChecked == true;
        Settings.VideoDetailsVisibility.RelatedVideos = ShowRelatedVideosCheckBox.IsChecked == true;
        DialogResult = true;
    }

    private void UpdateDownloadPathHint()
    {
        var path = string.IsNullOrWhiteSpace(DownloadPathBox.Text) ? Settings.DownloadPath : DownloadPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            DownloadPathHintText.Text = "请输入有效的下载目录。";
            return;
        }

        DownloadPathHintText.Text = Directory.Exists(path)
            ? $"当前目录可用: {path}"
            : $"目录不存在，保存后下载时会尝试自动创建: {path}";
    }

    private void UpdateNamingPreview()
    {
        var template = NamingRuleCombo.SelectedValue as string ?? Settings.FileNamingRule;
        NamingPreviewText.Text = $"命名预览: {BuildFileNamePreview(template)}";
    }

    private void UpdateSiteHint()
    {
        var siteHost = NormalizeSiteHost(SiteCombo.Text);
        if (string.IsNullOrWhiteSpace(siteHost))
        {
            SiteHintText.Text = "可直接输入自定义站点域名，例如 hanime1.xxx。";
            return;
        }

        if (!TryValidateSiteHost(siteHost, out _))
        {
            SiteHintText.Text = "站点格式无效，请输入域名，例如 hanime1.me 或 hanime1.example.com。";
            return;
        }

        SiteHintText.Text = siteHost == "javchu.com"
            ? "当前站点仅保留访问入口提示，下载仍以 hanime 系站点流程为主，不建议作为默认下载站点。"
            : $"当前站点会用于验证、搜索详情和下载流程: {siteHost}";
    }

    private void InitializeSiteOptions()
    {
        _siteOptions.Clear();
        _siteOptions.Add(new SiteOption { Label = "hanime1.me", Value = "hanime1.me", IsCustom = false });
        _siteOptions.Add(new SiteOption { Label = "hanime1.com", Value = "hanime1.com", IsCustom = false });
        _siteOptions.Add(new SiteOption { Label = "hanimeone.me", Value = "hanimeone.me", IsCustom = false });
        _siteOptions.Add(new SiteOption { Label = "javchu.com（暂不支持下载）", Value = "javchu.com", IsCustom = false });

        foreach (var siteHost in Settings.CustomSiteHosts.Select(NormalizeSiteHost).Where(host => !string.IsNullOrWhiteSpace(host)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PresetSiteHosts.Contains(siteHost, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _siteOptions.Add(new SiteOption { Label = siteHost, Value = siteHost, IsCustom = true });
        }
    }

    private void AddCustomSiteIfNeeded(string siteHost)
    {
        if (string.IsNullOrWhiteSpace(siteHost) || PresetSiteHosts.Contains(siteHost, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (Settings.CustomSiteHosts.All(host => !string.Equals(host, siteHost, StringComparison.OrdinalIgnoreCase)))
        {
            Settings.CustomSiteHosts.Add(siteHost);
        }

        if (_siteOptions.All(option => !string.Equals(option.Value, siteHost, StringComparison.OrdinalIgnoreCase)))
        {
            _siteOptions.Add(new SiteOption { Label = siteHost, Value = siteHost, IsCustom = true });
        }
    }

    private void RemoveCustomSite(string siteHost)
    {
        siteHost = NormalizeSiteHost(siteHost);
        if (string.IsNullOrWhiteSpace(siteHost) || PresetSiteHosts.Contains(siteHost, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        for (var i = Settings.CustomSiteHosts.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Settings.CustomSiteHosts[i], siteHost, StringComparison.OrdinalIgnoreCase))
            {
                Settings.CustomSiteHosts.RemoveAt(i);
            }
        }

        var item = _siteOptions.FirstOrDefault(option => option.IsCustom && string.Equals(option.Value, siteHost, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            _siteOptions.Remove(item);
        }

        if (string.Equals(NormalizeSiteHost(SiteCombo.Text), siteHost, StringComparison.OrdinalIgnoreCase))
        {
            SiteCombo.SelectedIndex = -1;
            SiteCombo.Text = string.Empty;
            UpdateSiteHint();
        }
    }

    private static bool TryValidateDownloadPath(string path, out string errorMessage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "下载目录不能为空。";
                return false;
            }

            _ = Path.GetFullPath(path);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"下载目录无效: {ex.Message}";
            return false;
        }
    }

    private static string NormalizeSiteHost(string? value)
    {
        var host = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        host = host.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Trim().Trim('/').Trim();
        var slashIndex = host.IndexOf('/');
        if (slashIndex >= 0) host = host[..slashIndex];
        var queryIndex = host.IndexOfAny(['?', '#']);
        if (queryIndex >= 0) host = host[..queryIndex];
        return host.Trim().Trim('.').ToLowerInvariant();
    }

    private static bool TryValidateSiteHost(string siteHost, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(siteHost))
        {
            errorMessage = "站点不能为空。";
            return false;
        }

        if (!SiteHostRegex.IsMatch(siteHost) || !siteHost.Contains('.'))
        {
            errorMessage = "站点格式无效，请输入域名，例如 hanime1.me。";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static string BuildFileNamePreview(string? template)
    {
        var namingTemplate = string.IsNullOrWhiteSpace(template) ? "{title}" : template;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = namingTemplate
            .Replace("{title}", SanitizeFileName("示例视频标题"), StringComparison.OrdinalIgnoreCase)
            .Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase)
            .Replace("{videoId}", "123456", StringComparison.OrdinalIgnoreCase)
            .Replace("{quality}", "720p", StringComparison.OrdinalIgnoreCase);
        return $"{SanitizeFileName(fileName)}.mp4";
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? $"hanime_{DateTime.Now:yyyyMMdd_HHmmss}" : cleaned;
    }

    private sealed class NamingRuleOption
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
    }

    private sealed class SiteOption
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
        public required bool IsCustom { get; init; }

        public override string ToString() => Value;
    }
}
