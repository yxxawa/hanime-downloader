using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;
using System.Collections.ObjectModel;
using System.Windows;

namespace Hanime1Downloader.CSharp;

public partial class MainWindow
{
    private const string AccountFavoritesFolder = "账号收藏夹";
    private bool _startupAccountFavoritesSyncAttempted;
    private bool _accountFavoritesSyncInProgress;
    private HanimeAccountService? _accountService;
    private string _accountServiceSiteHost = string.Empty;

    private bool IsAccountFavoritesMode =>
        string.Equals(_settings.FavoritesMode, AppSettings.AccountFavoritesMode, StringComparison.OrdinalIgnoreCase);

    private string GetCurrentDefaultFavoritesFolder()
    {
        return IsAccountFavoritesMode ? AccountFavoritesFolder : DefaultFavoritesFolder;
    }

    private HanimeAccountService GetAccountService()
    {
        if (_accountService is not null &&
            string.Equals(_accountServiceSiteHost, _settings.SiteHost, StringComparison.OrdinalIgnoreCase))
        {
            return _accountService;
        }

        _cloudflareWindow ??= new CloudflareWindow(_settings.SiteHost) { Owner = this };
        _accountServiceSiteHost = _settings.SiteHost;
        _accountService = new HanimeAccountService(_cloudflareWindow, _settings.SiteHost);
        return _accountService;
    }

    private void ApplyFavoritesModeUi()
    {
        var localVisibility = IsAccountFavoritesMode ? Visibility.Collapsed : Visibility.Visible;
        NewFavoriteFolderButton.Visibility = localVisibility;
        DeleteFavoriteFolderButton.Visibility = localVisibility;
        RenameFavoriteFolderButton.Visibility = localVisibility;
        RefreshFavoritesButton.Visibility = IsAccountFavoritesMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshFavoritesButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SyncAccountFavoritesAsync();
    }
    private bool TryEnsureLocalFavoritesMode()
    {
        if (!IsAccountFavoritesMode)
        {
            return true;
        }

        StatusText.Text = "账号同步模式不支持本地收藏夹管理，请使用主界面的收藏 / 取消收藏操作。";
        return false;
    }

    private void ReloadFavoritesForCurrentMode()
    {
        LoadFavorites();
        RefreshFavoriteFolders(GetCurrentDefaultFavoritesFolder());
        RefreshFavoritesView();
        UpdateFavoriteButtonState();
    }

    private async Task SyncAccountFavoritesOnStartupAsync()
    {
        if (_startupAccountFavoritesSyncAttempted || !IsAccountFavoritesMode)
        {
            return;
        }

        _startupAccountFavoritesSyncAttempted = true;
        await SyncAccountFavoritesAsync();
    }

    private async Task<HanimeAccountIdentity> BindAccountAsync(string email, string password)
    {
        var accountService = GetAccountService();
        var identity = await accountService.EnsureLoggedInAsync(email, password);
        await SyncVerifiedSessionAsync();
        return identity;
    }

    private async Task SyncAccountFavoritesAsync()
    {
        if (!IsAccountFavoritesMode || _accountFavoritesSyncInProgress)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AccountEmail))
        {
            StatusText.Text = "同步账号模式需要在设置中填写账号邮箱。";
            return;
        }

        _accountFavoritesSyncInProgress = true;
        var syncSiteHost = _settings.SiteHost;
        try
        {
            StatusText.Text = "正在同步账号收藏夹...";
            var accountService = GetAccountService();
            var account = await accountService.EnsureLoggedInAsync(
                _settings.AccountEmail,
                string.Empty);

            // 登录响应里的 Set-Cookie 已进入 WebView2；导出后同时更新站点 Cookie 缓存与 HttpClient。
            await SyncVerifiedSessionAsync();

            var videos = await accountService.FetchFavoriteVideosAsync(account.UserId);
            if (!IsAccountFavoritesMode ||
                !string.Equals(syncSiteHost, _settings.SiteHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _favoriteFolders.Clear();
            if (!string.IsNullOrWhiteSpace(account.Email))
            {
                _settings.AccountEmail = account.Email;
            }
            _settings.AccountUserId = account.UserId;
            _settings.AccountUserName = account.UserName;
            SaveSettings();
            ApplyDownloadedFlags(videos);
            _favoriteFolders[AccountFavoritesFolder] = new ObservableCollection<VideoSummary>(videos);
            RefreshFavoriteFolders(AccountFavoritesFolder);
            RefreshFavoritesView();
            PrimeThumbnails(videos);

            StatusText.Text = $"账号收藏夹已同步：{videos.Count} 个视频。";
            LogInfo("favorites-account", $"账号收藏夹同步完成: user={account.UserId}, count={videos.Count}");
        }
        catch (Exception ex)
        {
            LogError("favorites-account", "账号收藏夹同步失败", ex);
            StatusText.Text = $"账号收藏夹同步失败: {ex.Message}";
        }
        finally
        {
            _accountFavoritesSyncInProgress = false;
        }
    }
    private async Task ToggleAccountFavoriteAsync(VideoSummary summary)
    {
        if (IsVideoFavorited(summary.VideoId))
        {
            var result = MessageBox.Show(this, "该视频已在账号收藏中，要取消收藏吗？", "取消账号收藏", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await RemoveSelectedAccountFavoritesAsync([summary]);
            }
            return;
        }

        await AddVideosToAccountFavoritesAsync([summary]);
    }

    private async Task AddVideosToAccountFavoritesAsync(IReadOnlyList<VideoSummary> videos)
    {
        await SetAccountFavoriteAsync(videos, shouldBeFavorite: true);
    }

    private async Task RemoveSelectedAccountFavoritesAsync(IReadOnlyList<VideoSummary> videos)
    {
        await SetAccountFavoriteAsync(videos, shouldBeFavorite: false);
    }

    private async Task SetAccountFavoriteAsync(IReadOnlyList<VideoSummary> videos, bool shouldBeFavorite)
    {
        if (!IsAccountFavoritesMode || videos.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AccountUserId))
        {
            StatusText.Text = "请先到设置中绑定同步账号。";
            return;
        }

        var accountService = GetAccountService();
        var targetVideoId = videos.Count == 1
            ? videos[0].VideoId
            : _currentDetails?.VideoId ?? GetSelectedVideoSummary()?.VideoId;
        var changedCount = 0;
        Exception? failure = null;

        foreach (var video in videos)
        {
            var wasFolderPresent = _favoriteFolders.TryGetValue(AccountFavoritesFolder, out var favorites);
            if (favorites is null)
            {
                favorites = [];
                _favoriteFolders[AccountFavoritesFolder] = favorites;
                wasFolderPresent = false;
            }

            var existing = favorites.FirstOrDefault(item =>
                string.Equals(item.VideoId, video.VideoId, StringComparison.OrdinalIgnoreCase));
            var originalIndex = existing is null ? -1 : favorites.IndexOf(existing);
            var localChanged = false;

            // 先更新 UI 状态，避免按钮等待网络响应。
            if (shouldBeFavorite && existing is null)
            {
                favorites.Insert(0, video);
                localChanged = true;
            }
            else if (!shouldBeFavorite && existing is not null)
            {
                favorites.RemoveAt(originalIndex);
                localChanged = true;
            }

            if (localChanged)
            {
                changedCount++;
                RefreshAccountFavoritesImmediate(video.VideoId, !wasFolderPresent);
            }

            try
            {
                var success = await accountService.SetFavoriteAsync(
                    video.VideoId,
                    shouldBeFavorite,
                    _settings.AccountUserId);
                if (!success)
                {
                    throw new InvalidOperationException($"收藏接口未确认视频 {video.VideoId} 的操作结果。");
                }
            }
            catch (Exception ex)
            {
                if (localChanged)
                {
                    if (shouldBeFavorite)
                    {
                        var added = favorites.FirstOrDefault(item =>
                            string.Equals(item.VideoId, video.VideoId, StringComparison.OrdinalIgnoreCase));
                        if (added is not null)
                        {
                            favorites.Remove(added);
                        }
                    }
                    else if (existing is not null && originalIndex >= 0)
                    {
                        favorites.Insert(Math.Min(originalIndex, favorites.Count), existing);
                    }

                    RefreshAccountFavoritesImmediate(video.VideoId, refreshFolders: true);
                }

                failure = ex;
                LogError("favorites-account", "调用账号收藏接口失败", ex);
                break;
            }
        }

        UpdateFavoriteButtonState(targetVideoId);
        if (failure is not null)
        {
            StatusText.Text = $"账号收藏操作失败: {failure.Message}";
            return;
        }

        StatusText.Text = shouldBeFavorite
            ? $"已通过账号接口收藏 {changedCount} 个视频。"
            : $"已通过账号接口取消收藏 {changedCount} 个视频。";
    }

    private void RefreshAccountFavoritesImmediate(string videoId, bool refreshFolders)
    {
        if (refreshFolders)
        {
            RefreshFavoriteFolders(AccountFavoritesFolder);
            RefreshFavoritesView();
        }
        else
        {
            _favoritesView?.Refresh();
            FavoritesList.Items.Refresh();
        }

        UpdateFavoriteButtonState(videoId);
    }
}
