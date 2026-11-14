using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Microsoft.Win32;

namespace Hanime1Downloader.CSharp.Views;

public partial class SettingsDialog : Window
{
    private static readonly Regex HostPattern = new(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly SiteOption[] PresetSites =
    [
        new("hanime1.me", "hanime1.me", false), new("hanime1.com", "hanime1.com", false),
        new("hanimeone.me", "hanimeone.me", false), new("javchu.com（暂不支持下载）", "javchu.com", false)
    ];

    private readonly ObservableCollection<SiteOption> _siteOptions = [];
    private readonly Func<string, string, Task<HanimeAccountIdentity>>? _bindAccountAsync;
    private readonly string _originalSite;
    private bool _ready;
    private bool _closed;
    private bool _editingAccount;
    private bool _isBindingAccount;
    private string _boundEmail;
    private string _boundUserId;
    private string _boundUserName;
    private string? _releaseUrl;

    public AppSettings Settings { get; }
    public bool AccountBindingChanged { get; private set; }

    public SettingsDialog(AppSettings currentSettings, Func<string, string, Task<HanimeAccountIdentity>>? bindAccountAsync = null)
    {
        _bindAccountAsync = bindAccountAsync;
        _originalSite = NormalizeSiteHost(currentSettings.SiteHost) ?? PresetSites[0].Value;
        _boundEmail = currentSettings.AccountEmail;
        _boundUserId = currentSettings.AccountUserId;
        _boundUserName = currentSettings.AccountUserName;
        Settings = new AppSettings
        {
            DownloadPath = currentSettings.DownloadPath,
            FileNamingRule = currentSettings.FileNamingRule,
            ShowListCovers = currentSettings.ShowListCovers,
            LargeSearchResultCovers = currentSettings.LargeSearchResultCovers,
            FavoritesMode = currentSettings.FavoritesMode,
            AccountEmail = currentSettings.AccountEmail,
            AccountUserId = currentSettings.AccountUserId,
            AccountUserName = currentSettings.AccountUserName,
            ThemeMode = currentSettings.ThemeMode,
            DefaultQuality = currentSettings.DefaultQuality,
            SiteHost = currentSettings.SiteHost,
            CustomSiteHosts = currentSettings.CustomSiteHosts.ToList(),
            PersistDownloadQueue = currentSettings.PersistDownloadQueue,
            MaxConcurrentDownloads = currentSettings.MaxConcurrentDownloads,
            MaxRetries = currentSettings.MaxRetries,
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

        InitializeComponent();
        Closed += (_, _) => { _closed = true; AccountPasswordBox.Clear(); };
        VersionText.Text = "v" + UpdateChecker.GetCurrentVersion();
        foreach (var option in PresetSites) _siteOptions.Add(option);
        foreach (var host in Settings.CustomSiteHosts.Append(_originalSite).Select(NormalizeSiteHost).OfType<string>())
            GetOrAddSiteOption(host);
        SiteCombo.ItemsSource = _siteOptions;
        SiteCombo.SelectedValue = _originalSite;
        QualityCombo.ItemsSource = new Choice[] { new("最高画质", "highest"), new("720p", "720"), new("480p", "480"), new("最低画质", "lowest") };
        ThemeModeCombo.ItemsSource = new Choice[] { new("浅色", "light"), new("深色", "dark") };
        MaxConcurrentDownloadsCombo.ItemsSource = Enumerable.Range(1, 3).Select(n => new Choice(n.ToString(), n.ToString())).ToArray();
        MaxRetriesCombo.ItemsSource = Enumerable.Range(0, 9).Select(n => new Choice(n.ToString(), n.ToString())).ToArray();

        DownloadPathBox.Text = Settings.DownloadPath;
        NamingEditor.RuleText = Settings.FileNamingRule;
        QualityCombo.SelectedValue = Settings.DefaultQuality;
        ThemeModeCombo.SelectedValue = Settings.ThemeMode;
        MaxConcurrentDownloadsCombo.SelectedValue = Math.Clamp(Settings.MaxConcurrentDownloads, 1, 3).ToString();
        MaxRetriesCombo.SelectedValue = Math.Clamp(Settings.MaxRetries, 0, 8).ToString();
        FavoritesAccountRadio.IsChecked = Settings.FavoritesMode == AppSettings.AccountFavoritesMode;
        FavoritesLocalRadio.IsChecked = FavoritesAccountRadio.IsChecked != true;
        AccountEmailBox.Text = Settings.AccountEmail;
        ShowListCoversCheckBox.IsChecked = Settings.ShowListCovers;
        LargeSearchResultCoversCheckBox.IsChecked = Settings.LargeSearchResultCovers;
        PersistQueueCheckBox.IsChecked = Settings.PersistDownloadQueue;
        SetDetailsControls(Settings.VideoDetailsVisibility);
        _ready = true;
        UpdateDownloadPathHint();
        UpdateSiteAndAccountUi();
    }

    private bool IsAccountMode => FavoritesAccountRadio.IsChecked == true;
    private bool HasBoundAccount => !string.IsNullOrWhiteSpace(_boundUserId) && !string.IsNullOrWhiteSpace(_boundEmail);
    private string? SelectedSite => (SiteCombo.SelectedItem as SiteOption)?.Value;
    private bool IsOriginalSite => string.Equals(SelectedSite, _originalSite, StringComparison.OrdinalIgnoreCase);
    private string EffectiveDownloadPath => string.IsNullOrWhiteSpace(DownloadPathBox.Text) ? AppSettings.DefaultDownloadPath : DownloadPathBox.Text.Trim();

    private void SettingsPages_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ReferenceEquals(e.Source, SettingsPages)) SetFeedback(SettingsFeedbackText, "保存后应用设置。");
    }

    private void SiteCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        SetFeedback(AccountFeedbackText, string.Empty);
        UpdateSiteAndAccountUi();
    }

    private void FavoritesMode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SetFeedback(AccountFeedbackText, string.Empty);
        UpdateSiteAndAccountUi();
    }

    private void UpdateSiteAndAccountUi()
    {
        var selected = SelectedSite;
        SiteHintText.Text = selected == "javchu.com"
            ? "此站点暂不支持下载。"
            : IsOriginalSite ? $"当前使用：{selected}" : $"保存后切换至：{selected}";
        var canEdit = !_isBindingAccount;
        SiteCombo.IsEnabled = CustomSiteBox.IsEnabled = AddCustomSiteButton.IsEnabled = canEdit;
        RemoveCustomSiteButton.IsEnabled = canEdit && SiteCombo.SelectedItem is SiteOption { IsCustom: true };
        FavoritesLocalRadio.IsEnabled = FavoritesAccountRadio.IsEnabled = canEdit;
        SaveSettingsButton.IsEnabled = canEdit;

        AccountOptionsPanel.Visibility = Visible(IsAccountMode);
        AccountSiteMismatchPanel.Visibility = Visible(!IsOriginalSite);
        BoundAccountPanel.Visibility = Visible(IsOriginalSite && HasBoundAccount && !_editingAccount);
        AccountForm.Visibility = Visible(IsOriginalSite && (!HasBoundAccount || _editingAccount));
        AccountForm.IsEnabled = canEdit;
        CancelAccountEditButton.Visibility = Visible(HasBoundAccount);
        AccountStatusText.Text = HasBoundAccount
            ? $"已绑定 · {(string.IsNullOrWhiteSpace(_boundUserName) ? _boundEmail : _boundUserName)}（ID: {_boundUserId}）"
            : string.Empty;
        UpdateBindButton();
    }

    private void AccountCredential_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateBindButton();
    }

    private void UpdateBindButton()
    {
        BindAccountButton.IsEnabled = !_isBindingAccount && _bindAccountAsync is not null && IsAccountMode && IsOriginalSite &&
            !string.IsNullOrWhiteSpace(AccountEmailBox.Text) && !string.IsNullOrWhiteSpace(AccountPasswordBox.Password);
    }

    private void UseLocalFavorites_OnClick(object sender, RoutedEventArgs e) => FavoritesLocalRadio.IsChecked = true;

    private void EditAccount_OnClick(object sender, RoutedEventArgs e)
    {
        _editingAccount = true;
        SetFeedback(AccountFeedbackText, string.Empty);
        UpdateSiteAndAccountUi();
        AccountEmailBox.Focus();
        AccountEmailBox.SelectAll();
    }

    private void CancelAccountEdit_OnClick(object sender, RoutedEventArgs e)
    {
        _editingAccount = false;
        AccountEmailBox.Text = _boundEmail;
        AccountPasswordBox.Clear();
        SetFeedback(AccountFeedbackText, string.Empty);
        UpdateSiteAndAccountUi();
        EditAccountButton.Focus();
    }

    private void AccountPasswordBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (BindAccountButton.IsEnabled) BindAccountButton_OnClick(sender, e);
    }

    private async void BindAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!BindAccountButton.IsEnabled || _bindAccountAsync is null) return;
        var email = AccountEmailBox.Text.Trim();
        var password = AccountPasswordBox.Password;
        _isBindingAccount = true;
        UpdateSiteAndAccountUi();
        SetFeedback(AccountFeedbackText, "正在绑定账号…");
        try
        {
            var identity = await _bindAccountAsync(email, password);
            if (_closed) return;
            _boundEmail = string.IsNullOrWhiteSpace(identity.Email) ? email : identity.Email;
            _boundUserId = identity.UserId;
            _boundUserName = identity.UserName;
            AccountEmailBox.Text = _boundEmail;
            AccountPasswordBox.Clear();
            _editingAccount = false;
            AccountBindingChanged = true;
            SetFeedback(AccountFeedbackText, "账号已绑定。点击「保存设置」应用收藏来源。");
        }
        catch (Exception ex)
        {
            if (!_closed) SetFeedback(AccountFeedbackText, "绑定失败：" + ex.Message, isError: true);
        }
        finally
        {
            _isBindingAccount = false;
            if (!_closed) UpdateSiteAndAccountUi();
        }
    }

    private async void CheckUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        _releaseUrl = null;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        SetFeedback(UpdateStatusText, "正在检查…");
        try
        {
            var result = await UpdateChecker.CheckAsync(UpdateChecker.GetCurrentVersion());
            if (_closed) return;
            if (result is null)
                SetFeedback(UpdateStatusText, "暂时无法检查更新，请稍后重试。", isError: true);
            else if (!result.HasUpdate)
                SetFeedback(UpdateStatusText, "已是最新版本。");
            else
            {
                _releaseUrl = result.ReleaseUrl;
                OpenReleaseButton.Visibility = Visibility.Visible;
                SetFeedback(UpdateStatusText, $"发现新版本 {result.TagName}。");
            }
        }
        catch (Exception ex)
        {
            if (!_closed) SetFeedback(UpdateStatusText, "检查失败：" + ex.Message, isError: true);
        }
        finally { if (!_closed) CheckUpdateButton.IsEnabled = true; }
    }

    private void OpenReleaseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_releaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
        {
            SetFeedback(UpdateStatusText, "发布页地址无效。", isError: true);
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { SetFeedback(UpdateStatusText, "打开发布页失败：" + ex.Message, isError: true); }
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择下载文件夹" };
            if (Directory.Exists(EffectiveDownloadPath)) dialog.InitialDirectory = Path.GetFullPath(EffectiveDownloadPath);
            if (dialog.ShowDialog(this) == true) DownloadPathBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            SetFeedback(DownloadPathHintText, "无法打开文件夹选择器：" + ex.Message, isError: true);
        }
    }

    private void ResetDownloadPath_OnClick(object sender, RoutedEventArgs e) => DownloadPathBox.Text = AppSettings.DefaultDownloadPath;
    private void ResetNaming_OnClick(object sender, RoutedEventArgs e) => NamingEditor.RuleText = DownloadNaming.DefaultRule;
    private void ResetDetails_OnClick(object sender, RoutedEventArgs e) => SetDetailsControls(new VideoDetailsVisibilitySettings());

    private void DownloadPathBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateDownloadPathHint();
    }

    private void UpdateDownloadPathHint()
    {
        var path = EffectiveDownloadPath;
        var error = ValidateDownloadPath(path);
        SetFeedback(DownloadPathHintText, error ?? (Directory.Exists(path) ? "此目录已存在。" : "目录尚不存在，下载时会创建。"), isError: error is not null);
    }

    private void CustomSiteBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddCustomSite_OnClick(sender, e);
    }

    private void AddCustomSite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBindingAccount) return;
        var host = NormalizeSiteHost(CustomSiteBox.Text);
        if (host is null)
        {
            SetFeedback(CustomSiteFeedbackText, "请输入有效域名或 HTTP/HTTPS 链接，不含账号或自定义端口。", isError: true);
            CustomSiteBox.Focus();
            return;
        }
        var alreadyExists = _siteOptions.Any(option => option.Value == host);
        SiteCombo.SelectedItem = GetOrAddSiteOption(host);
        CustomSiteBox.Clear();
        SetFeedback(CustomSiteFeedbackText, alreadyExists ? $"已选中 {host}。" : $"已添加 {host}，保存后生效。");
    }

    private void RemoveCustomSite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBindingAccount || SiteCombo.SelectedItem is not SiteOption { IsCustom: true } selected) return;
        var fallback = _siteOptions.FirstOrDefault(option => option != selected && option.Value == _originalSite) ?? PresetSites[0];
        SiteCombo.SelectedItem = fallback;
        _siteOptions.Remove(selected);
        SetFeedback(CustomSiteFeedbackText, $"已从列表移除 {selected.Value}，保存后生效。");
    }

    private SiteOption GetOrAddSiteOption(string host)
    {
        var existing = _siteOptions.FirstOrDefault(option => option.Value == host);
        if (existing is not null) return existing;
        var option = new SiteOption(host, host, true);
        _siteOptions.Add(option);
        return option;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isBindingAccount) return;
        var downloadPath = EffectiveDownloadPath;
        if (ValidateDownloadPath(downloadPath) is { } pathError)
        {
            ShowValidation(DownloadPage, pathError, DownloadPathBox);
            return;
        }
        if (!DownloadNaming.TryValidate(NamingEditor.RuleText, out var namingError))
        {
            ShowValidation(NamingPage, namingError, NamingEditor.FindName("TemplateBox") as Control);
            return;
        }
        if (!string.IsNullOrWhiteSpace(CustomSiteBox.Text))
        {
            ShowValidation(SitePage, "有尚未添加的站点。请点击「添加并选中」，或清空输入框。", CustomSiteBox);
            return;
        }
        if (SelectedSite is not { } siteHost)
        {
            ShowValidation(SitePage, "请先选择一个站点。", SiteCombo);
            return;
        }
        if (IsAccountMode && !IsOriginalSite)
        {
            ShowValidation(SitePage, "切换站点时，请先选择本地收藏。保存后可在新站点绑定。", FavoritesLocalRadio);
            return;
        }
        if (IsAccountMode && (!HasBoundAccount || (_editingAccount && !string.Equals(AccountEmailBox.Text.Trim(), _boundEmail, StringComparison.OrdinalIgnoreCase))))
        {
            ShowValidation(SitePage, "请先绑定账号，或取消尚未完成的账号更换。", AccountEmailBox);
            return;
        }

        Settings.DownloadPath = downloadPath;
        Settings.FileNamingRule = NamingEditor.RuleText;
        Settings.SiteHost = siteHost;
        Settings.CustomSiteHosts = _siteOptions.Where(option => option.IsCustom).Select(option => option.Value).ToList();
        Settings.DefaultQuality = QualityCombo.SelectedValue as string ?? "highest";
        Settings.ShowListCovers = ShowListCoversCheckBox.IsChecked == true;
        Settings.LargeSearchResultCovers = LargeSearchResultCoversCheckBox.IsChecked == true;
        Settings.FavoritesMode = IsAccountMode ? AppSettings.AccountFavoritesMode : AppSettings.LocalFavoritesMode;
        Settings.AccountEmail = IsOriginalSite ? (HasBoundAccount ? _boundEmail : AccountEmailBox.Text.Trim()) : string.Empty;
        Settings.AccountUserId = IsOriginalSite && HasBoundAccount ? _boundUserId : string.Empty;
        Settings.AccountUserName = IsOriginalSite && HasBoundAccount ? _boundUserName : string.Empty;
        Settings.ThemeMode = ThemeModeCombo.SelectedValue as string ?? "light";
        Settings.PersistDownloadQueue = PersistQueueCheckBox.IsChecked == true;
        Settings.MaxConcurrentDownloads = ParseNumber(MaxConcurrentDownloadsCombo, 1, 1, 3);
        Settings.MaxRetries = ParseNumber(MaxRetriesCombo, 3, 0, 8);
        Settings.VideoDetailsVisibility = new VideoDetailsVisibilitySettings
        {
            Title = ShowTitleCheckBox.IsChecked == true, UploadDate = ShowUploadDateCheckBox.IsChecked == true,
            Likes = ShowLikesCheckBox.IsChecked == true, Views = ShowViewsCheckBox.IsChecked == true,
            Duration = ShowDurationCheckBox.IsChecked == true, Tags = ShowTagsCheckBox.IsChecked == true,
            Cover = ShowCoverCheckBox.IsChecked == true, RelatedVideos = ShowRelatedVideosCheckBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void ShowValidation(TabItem page, string message, Control? field)
    {
        SettingsPages.SelectedItem = page;
        SetFeedback(SettingsFeedbackText, message, isError: true);
        if (field is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!_closed) { field.BringIntoView(); field.Focus(); } }));
    }

    private void SetDetailsControls(VideoDetailsVisibilitySettings settings)
    {
        ShowTitleCheckBox.IsChecked = settings.Title;
        ShowUploadDateCheckBox.IsChecked = settings.UploadDate;
        ShowLikesCheckBox.IsChecked = settings.Likes;
        ShowViewsCheckBox.IsChecked = settings.Views;
        ShowDurationCheckBox.IsChecked = settings.Duration;
        ShowTagsCheckBox.IsChecked = settings.Tags;
        ShowCoverCheckBox.IsChecked = settings.Cover;
        ShowRelatedVideosCheckBox.IsChecked = settings.RelatedVideos;
    }

    private static string? ValidateDownloadPath(string path)
    {
        try { return File.Exists(Path.GetFullPath(path)) ? "该位置是文件，请选择文件夹。" : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return "下载目录无效：" + ex.Message; }
    }

    private static string? NormalizeSiteHost(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns)
            return null;
        try
        {
            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            return host.Length <= 253 && HostPattern.IsMatch(host) ? host : null;
        }
        catch (UriFormatException) { return null; }
    }

    private static void SetFeedback(TextBlock textBlock, string message, bool isError = false)
    {
        textBlock.Text = message;
        textBlock.Visibility = Visible(message.Length > 0);
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ThemeErrorTextBrush" : "ThemeTextMutedBrush");
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static int ParseNumber(ComboBox combo, int fallback, int min, int max) =>
        int.TryParse(combo.SelectedValue as string, out var value) ? Math.Clamp(value, min, max) : fallback;
    private sealed record Choice(string Label, string Value);
    private sealed record SiteOption(string Label, string Value, bool IsCustom);
}
