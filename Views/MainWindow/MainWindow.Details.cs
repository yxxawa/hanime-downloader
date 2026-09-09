using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppThemeService = Hanime1Downloader.CSharp.Services.AppTheme;

namespace Hanime1Downloader.CSharp;

public partial class MainWindow
{

    private async void TitleText_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var title = TitleText.Text;
        if (string.IsNullOrWhiteSpace(title) || title == "请选择左侧视频查看详情。") return;
        try { Clipboard.SetDataObject(title, false); } catch { return; }
        var pos = e.GetPosition(TitleText);
        Canvas.SetLeft(TitleCopiedHint, pos.X);
        TitleCopiedHint.Visibility = Visibility.Visible;
        _titleCopiedHintCts?.Cancel();
        _titleCopiedHintCts?.Dispose();
        _titleCopiedHintCts = new CancellationTokenSource();
        var cts = _titleCopiedHintCts;
        try { await Task.Delay(1500, cts.Token); } catch (OperationCanceledException) { return; }
        TitleCopiedHint.Visibility = Visibility.Collapsed;
    }

    private async Task LoadDetailsAsync(VideoSummary summary, bool isRetry = false)
    {
        if (_apiClient is null) InitSessionWithoutCf();

        if (_isSearching)
        {
            return;
        }

        if (string.Equals(_currentDetailsVideoId, summary.VideoId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelPendingDetailsLoad();
        var cancellationTokenSource = new CancellationTokenSource();
        _detailsLoadCancellationTokenSource = cancellationTokenSource;
        var requestVersion = Interlocked.Increment(ref _detailsRequestVersion);
        var requestedVideoId = summary.VideoId;
        _isLoadingDetails = true;
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        SourcesList.ItemsSource = null;
        SourcesSummaryText.Text = "正在读取详情与视频源...";
        StatusText.Text = $"正在读取 {requestedVideoId} 的详情...";
        ResetDetailsView("正在加载视频信息...");

        try
        {
            var apiClient = _apiClient;
            if (apiClient is null)
            {
                StatusText.Text = "浏览会话尚未初始化。";
                return;
            }

            LogInfo("details", $"开始读取详情: {requestedVideoId}");
            var details = await GetOrLoadVideoDetailsAsync(requestedVideoId, cancellationToken: cancellationTokenSource.Token);
            if (cancellationTokenSource.IsCancellationRequested ||
                requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (details is null)
            {
                SourcesSummaryText.Text = "详情读取失败，未拿到视频源信息。";
                StatusText.Text = "读取详情失败。";
                return;
            }

            ApplyVideoDetails(details);
            if (!string.IsNullOrWhiteSpace(details.CoverUrl))
            {
                var matched = _favoriteFolders.Values.SelectMany(f => f)
                    .FirstOrDefault(s => string.Equals(s.VideoId, details.VideoId, StringComparison.OrdinalIgnoreCase));
                if (matched is not null && string.IsNullOrWhiteSpace(matched.CoverUrl))
                {
                    matched.CoverUrl = details.CoverUrl;
                    _ = PrimeThumbnailAsync(matched);
                }
            }
            StatusText.Text = $"已读取 {details.VideoId} 的详情。";
            LogInfo("details", $"详情读取完成: {details.VideoId}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsCloudflareSessionError(ex))
        {
            if (requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SourcesSummaryText.Text = "当前会话失效，正在等待 Cloudflare 重验...";
            var restored = await EnsureVerifiedSessionAsync("检测到 Cloudflare 会话失效，正在自动打开验证窗口...", cancellationTokenSource.Token);
            if (restored && !isRetry && !cancellationTokenSource.IsCancellationRequested)
            {
                SourcesSummaryText.Text = "会话已恢复，正在重新读取详情与视频源...";
                await LoadDetailsAsync(summary, isRetry: true);
                return;
            }

            ResetDetailsView("详情加载失败，请重新选择视频。");
            SourcesSummaryText.Text = "详情加载失败，视频源未读取完成。";
            StatusText.Text = $"读取详情失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            if (requestVersion != Volatile.Read(ref _detailsRequestVersion) ||
                !string.Equals((GetSelectedVideoSummary()?.VideoId) ?? requestedVideoId, requestedVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ResetDetailsView("详情加载失败，请重新选择视频。");
            SourcesSummaryText.Text = "详情加载失败，视频源未读取完成。";
            StatusText.Text = $"读取详情失败: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_detailsLoadCancellationTokenSource, cancellationTokenSource))
            {
                _detailsLoadCancellationTokenSource = null;
                _isLoadingDetails = false;
                DownloadButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
                QueueSourceButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private VideoSummary? GetSelectedVideoSummary()
    {
        return ResultsList.SelectedItem as VideoSummary
            ?? FavoritesList.SelectedItem as VideoSummary
            ?? RelatedList.SelectedItem as VideoSummary;
    }

    private void CancelPendingDetailsLoad()
    {
        Interlocked.Increment(ref _detailsRequestVersion);
        _detailsLoadCancellationTokenSource?.Cancel();
        _detailsLoadCancellationTokenSource = null;
        _isLoadingDetails = false;
    }

    private void ResetDetailsView(string titleText = "请选择左侧视频查看详情。")
    {
        _currentDetails = null;
        _currentDetailsVideoId = null;
        UpdateCurrentDetailsDownloadedBadge();
        TitleText.Text = titleText;
        UploadDateText.Text = "-";
        LikesText.Text = "-";
        ViewsText.Text = "-";
        DurationText.Text = "-";
        TagsText.Text = "-";
        UrlText.Text = string.Empty;
        RelatedList.ItemsSource = null;
        SourcesSummaryText.Text = "未加载视频源";
        SourcesList.ItemsSource = null;
        SourcesList.SelectedItem = null;
        ViewDescriptionButton.IsEnabled = false;
        PreviewCoverButton.IsEnabled = false;
        CopyUrlButton.IsEnabled = false;
        OpenVideoPageButton.IsEnabled = false;
        UpdateFavoriteButtonState();
        QueueSourceButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
    }

    private void ApplyVideoDetails(VideoDetails details)
    {
        _currentDetails = details;
        _currentDetailsVideoId = details.VideoId;
        TitleText.Text = SimplifiedChineseConverter.ToSimplified(details.Title);
        UploadDateText.Text = string.IsNullOrWhiteSpace(details.UploadDate) ? "-" : SimplifiedChineseConverter.ToSimplified(details.UploadDate);
        LikesText.Text = string.IsNullOrWhiteSpace(details.Likes) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Likes);
        ViewsText.Text = string.IsNullOrWhiteSpace(details.Views) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Views);
        DurationText.Text = string.IsNullOrWhiteSpace(details.Duration) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Duration);
        TagsText.Text = details.Tags.Count == 0 ? "-" : string.Join(" / ", details.Tags.Select(SimplifiedChineseConverter.ToSimplified));
        UrlText.Text = details.Url;
        RelatedList.ItemsSource = details.RelatedVideos;
        ApplyDownloadedFlags(details.RelatedVideos);
        UpdateCurrentDetailsDownloadedBadge();
        PrimeThumbnails(details.RelatedVideos);
        SourcesSummaryText.Text = BuildSourcesSummaryText(details);
        SourcesList.ItemsSource = details.Sources;
        SourcesList.SelectedItem = details.Sources.FirstOrDefault();
        ViewDescriptionButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Description);
        PreviewCoverButton.IsEnabled = !string.IsNullOrWhiteSpace(details.CoverUrl);
        CopyUrlButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Url);
        OpenVideoPageButton.IsEnabled = !string.IsNullOrWhiteSpace(details.Url);
        UpdateFavoriteButtonState(details.VideoId);
        QueueSourceButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
        DownloadButton.IsEnabled = SourcesList.SelectedItem is VideoSource;
        ApplyVideoDetailsVisibility();
    }

    private async Task ShowVideoDetailsDialogAsync(VideoSummary summary)
    {
        if (_apiClient is null)
        {
            InitSessionWithoutCf();
        }

        var details = await GetOrLoadVideoDetailsAsync(summary.VideoId, GetActiveVideoDetailsLoadOptions());
        if (details is null)
        {
            StatusText.Text = "读取详情失败。";
            return;
        }

        var titleText = SimplifiedChineseConverter.ToSimplified(details.Title);
        var uploadDateText = string.IsNullOrWhiteSpace(details.UploadDate) ? "-" : SimplifiedChineseConverter.ToSimplified(details.UploadDate);
        var likesText = string.IsNullOrWhiteSpace(details.Likes) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Likes);
        var viewsText = string.IsNullOrWhiteSpace(details.Views) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Views);
        var durationText = string.IsNullOrWhiteSpace(details.Duration) ? "-" : SimplifiedChineseConverter.ToSimplified(details.Duration);
        var tagsText = details.Tags.Count == 0 ? "-" : string.Join(" / ", details.Tags.Select(SimplifiedChineseConverter.ToSimplified));

        var window = new Window
        {
            Owner = this,
            Title = $"视频信息 - {titleText}",
            Width = 680,
            Height = 420,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppThemeService.GetBrush("ThemeSurfaceBrush")
        };

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.PreviewMouseLeftButtonDown += (_, e) => TryStartDialogDrag(window, e);

        var headerBorder = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.SizeAll
        };

        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "信息",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppThemeService.GetBrush("ThemeTextBrush")
        });
        header.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppThemeService.GetBrush("ThemeTextMutedBrush"),
            FontSize = 10,
            Text = "基础信息"
        });
        DockPanel.SetDock(header.Children[^1], Dock.Left);

        var coverButton = new Button
        {
            Width = 64,
            Height = 24,
            Content = "查看封面",
            IsEnabled = !string.IsNullOrWhiteSpace(details.CoverUrl),
            Visibility = _settings.VideoDetailsVisibility.Cover ? Visibility.Visible : Visibility.Collapsed
        };
        coverButton.Click += async (_, _) =>
        {
            try
            {
                await ShowCoverPreviewAsync(details);
            }
            catch (Exception ex)
            {
                HandleUiActionError("cover", "查看封面失败", ex);
            }
        };
        DockPanel.SetDock(coverButton, Dock.Right);
        header.Children.Add(coverButton);
        headerBorder.Child = header;
        root.Children.Add(headerBorder);

        var infoBorder = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Background = AppThemeService.GetBrush("ThemeSurfaceAltBrush"),
            BorderBrush = AppThemeService.GetBrush("ThemeBorderAltBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };
        Grid.SetRow(infoBorder, 1);

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var visibility = _settings.VideoDetailsVisibility;
        var visibleRows = new List<(string Label, string Value)>();
        if (visibility.Title)
        {
            visibleRows.Add(("标题", titleText));
        }
        if (visibility.UploadDate)
        {
            visibleRows.Add(("上传时间", uploadDateText));
        }
        if (visibility.Likes)
        {
            visibleRows.Add(("点赞", likesText));
        }
        if (visibility.Views)
        {
            visibleRows.Add(("观看", viewsText));
        }
        if (visibility.Duration)
        {
            visibleRows.Add(("时长", durationText));
        }
        if (visibility.Tags)
        {
            visibleRows.Add(("标签", tagsText));
        }
        if (visibleRows.Count == 0)
        {
            visibleRows.Add(("提示", "当前设置未启用详情字段"));
        }

        var infoGrid = new Grid();
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < visibleRows.Count; i++)
        {
            infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddDetailRow(infoGrid, i, visibleRows[i].Label, visibleRows[i].Value);
        }
        scrollViewer.Content = infoGrid;
        infoBorder.Child = scrollViewer;
        root.Children.Add(infoBorder);

        window.Content = root;
        window.ShowDialog();
        StatusText.Text = $"已查看 {details.VideoId} 的详情。";
    }

    private static void TryStartDialogDrag(Window window, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button || current is ScrollBar)
            {
                return;
            }
        }

        window.DragMove();
        e.Handled = true;
    }

    private static void AddDetailRow(Grid grid, int row, string label, string value, bool wrap = true)
    {
        var labelText = new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 2, 10, 2),
            Foreground = AppThemeService.GetBrush("ThemeTextSubtleBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(labelText, row);
        grid.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = AppThemeService.GetBrush("ThemeTextBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
    }

    private void ApplyVideoDetailsVisibility()
    {
        TitleText.Visibility = _settings.VideoDetailsVisibility.Title ? Visibility.Visible : Visibility.Collapsed;
        UploadDateLabel.Visibility = _settings.VideoDetailsVisibility.UploadDate ? Visibility.Visible : Visibility.Collapsed;
        UploadDateText.Visibility = _settings.VideoDetailsVisibility.UploadDate ? Visibility.Visible : Visibility.Collapsed;
        LikesLabel.Visibility = _settings.VideoDetailsVisibility.Likes ? Visibility.Visible : Visibility.Collapsed;
        LikesText.Visibility = _settings.VideoDetailsVisibility.Likes ? Visibility.Visible : Visibility.Collapsed;
        ViewsLabel.Visibility = _settings.VideoDetailsVisibility.Views ? Visibility.Visible : Visibility.Collapsed;
        ViewsText.Visibility = _settings.VideoDetailsVisibility.Views ? Visibility.Visible : Visibility.Collapsed;
        DurationLabel.Visibility = _settings.VideoDetailsVisibility.Duration ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Visibility = _settings.VideoDetailsVisibility.Duration ? Visibility.Visible : Visibility.Collapsed;
        TagsLabel.Visibility = _settings.VideoDetailsVisibility.Tags ? Visibility.Visible : Visibility.Collapsed;
        TagsText.Visibility = _settings.VideoDetailsVisibility.Tags ? Visibility.Visible : Visibility.Collapsed;
        PreviewCoverButton.Visibility = _settings.VideoDetailsVisibility.Cover ? Visibility.Visible : Visibility.Collapsed;
        RelatedList.Visibility = _settings.VideoDetailsVisibility.RelatedVideos ? Visibility.Visible : Visibility.Collapsed;
    }

    private string BuildSourcesSummaryText(VideoDetails details)
    {
        var basicLoaded = details.LoadOptions.HasFlag(VideoDetailsLoadOptions.Basic);
        var sourcesLoaded = details.LoadOptions.HasFlag(VideoDetailsLoadOptions.Sources);

        if (!basicLoaded)
        {
            return "正在读取详情...";
        }

        if (!sourcesLoaded)
        {
            return "详情已加载，未请求视频源。";
        }

        if (details.Sources.Count == 0)
        {
            return "详情已加载，但当前没有解析到可用视频源。";
        }

        var sourceTypes = details.Sources
            .Select(source => source.TypeText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        var typeSuffix = sourceTypes.Count == 0 ? string.Empty : $"，类型 {string.Join("/", sourceTypes)}";
        return $"详情已加载，共 {details.Sources.Count} 个视频源{typeSuffix}";
    }

    private async void PreviewCoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetails is null)
        {
            StatusText.Text = "当前详情没有可用封面。";
            return;
        }

        try
        {
            await ShowCoverPreviewAsync(_currentDetails);
        }
        catch (Exception ex)
        {
            HandleUiActionError("cover", "查看封面失败", ex);
        }
    }

    private async Task ShowCoverPreviewAsync(VideoDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.CoverUrl) || !Uri.TryCreate(details.CoverUrl, UriKind.Absolute, out _))
        {
            StatusText.Text = "当前详情没有可用封面。";
            return;
        }

        StatusText.Text = "正在加载封面...";
        var bitmap = await ThumbnailCacheService.GetAsync(details.CoverUrl, 1280);
        if (bitmap is null)
        {
            StatusText.Text = "封面加载失败。";
            return;
        }

        var image = new System.Windows.Controls.Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = 640,
            MaxHeight = 480
        };
        var window = new Window
        {
            Style = null,
            Owner = this,
            ShowInTaskbar = false,
            Title = details.Title,
            Width = 720,
            Height = 560,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Black,
            Content = new System.Windows.Controls.Border
            {
                Background = Brushes.Black,
                Padding = new Thickness(12),
                Child = image
            }
        };
        window.ClearValue(Window.IconProperty);
        window.ShowDialog();
        StatusText.Text = "已打开封面预览。";
    }

    private void ViewDescriptionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentDetails is null)
        {
            MessageBox.Show(this, "请先选择一个视频。", "暂无内容", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var description = string.IsNullOrWhiteSpace(_currentDetails.Description)
            ? "暂无简介。"
            : SimplifiedChineseConverter.ToSimplified(_currentDetails.Description);
        var window = new Window
        {
            Owner = this,
            Title = $"简介 - {SimplifiedChineseConverter.ToSimplified(_currentDetails.Title)}",
            Width = 760,
            Height = 520,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppThemeService.GetBrush("ThemeSurfaceBrush"),
            Content = new System.Windows.Controls.Border
            {
                Padding = new Thickness(16),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = description,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = AppThemeService.GetBrush("ThemeTextBrush"),
                        LineHeight = 24
                    }
                }
            }
        };
        window.ShowDialog();
    }

    private void CopyVideoUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlText.Text))
        {
            StatusText.Text = "当前详情没有可复制的链接。";
            return;
        }

        try { Clipboard.SetText(UrlText.Text); } catch { return; }
        StatusText.Text = "已复制视频链接。";
    }

    private void OpenVideoPageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlText.Text))
        {
            StatusText.Text = "当前详情没有可打开的链接。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UrlText.Text,
                UseShellExecute = true
            });
            StatusText.Text = "已打开视频页面。";
        }
        catch (Exception ex)
        {
            HandleUiActionError("details", "打开视频页面失败", ex);
        }
    }

    private VideoDetailsLoadOptions GetActiveVideoDetailsLoadOptions()
    {
        return VideoDetailsLoadPolicy.ForVisibility(_settings.VideoDetailsVisibility);
    }

    private async Task<VideoDetails?> GetOrLoadVideoDetailsAsync(string videoId, VideoDetailsLoadOptions? requestedLoadOptions = null, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        var loadOptions = requestedLoadOptions ?? GetActiveVideoDetailsLoadOptions();
        var cacheKey = $"{videoId}:{(int)loadOptions}";

        if (forceRefresh)
        {
            foreach (var key in _videoDetailsCache.Keys.Where(key => key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _videoDetailsCache.Remove(key);
            }

            foreach (var key in _videoDetailsInFlight.Keys.Where(key => key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _videoDetailsInFlight.Remove(key);
            }
        }

        if (_videoDetailsCache.TryGetValue(cacheKey, out var cached))
        {
            LogInfoThrottled("details", $"[details-cache] 命中详情缓存: {cacheKey}", TimeSpan.FromSeconds(3));
            return cached;
        }

        var cachedSuperset = _videoDetailsCache
            .Where(pair => pair.Key.StartsWith($"{videoId}:", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault(details => (details.LoadOptions & loadOptions) == loadOptions);
        if (cachedSuperset is not null)
        {
            LogInfoThrottled("details", $"[details-cache-superset] 复用详情缓存: {videoId} / {loadOptions}", TimeSpan.FromSeconds(3));
            return cachedSuperset;
        }

        if (_videoDetailsInFlight.TryGetValue(cacheKey, out var existingTask))
        {
            LogInfoThrottled("details", $"[details-inflight] 复用详情任务: {cacheKey}", TimeSpan.FromSeconds(3));
            try { return await existingTask.WaitAsync(cancellationToken); }
            catch (Exception) when (existingTask.IsFaulted || existingTask.IsCanceled)
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
        }

        var apiClient = _apiClient;
        if (apiClient is null)
        {
            return null;
        }

        // Use CancellationToken.None so the shared task is not tied to any single caller's token
        var detailsTask = apiClient.GetDetailsAsync(videoId, loadOptions, CancellationToken.None, forceRefresh);
        _videoDetailsInFlight[cacheKey] = detailsTask;
        try
        {
            var details = await detailsTask.WaitAsync(cancellationToken);
            if (details is not null)
            {
                StoreVideoDetailsCache(cacheKey, details);
            }
            return details;
        }
        catch
        {
            if (_videoDetailsInFlight.TryGetValue(cacheKey, out var inFlight) && ReferenceEquals(inFlight, detailsTask))
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
            throw;
        }
        finally
        {
            if (_videoDetailsInFlight.TryGetValue(cacheKey, out var inFlight) && ReferenceEquals(inFlight, detailsTask) && detailsTask.IsCompletedSuccessfully)
            {
                _videoDetailsInFlight.Remove(cacheKey);
            }
        }
    }

    /// <summary>详情缓存按插入顺序限制条数，避免长时间浏览导致内存持续增长。</summary>
    private void StoreVideoDetailsCache(string cacheKey, VideoDetails details)
    {
        if (_videoDetailsCache.ContainsKey(cacheKey))
        {
            _videoDetailsCache[cacheKey] = details;
            return;
        }

        _videoDetailsCache[cacheKey] = details;
        _videoDetailsCacheOrder.Enqueue(cacheKey);
        while (_videoDetailsCacheOrder.Count > VideoDetailsCacheMaxEntries &&
               _videoDetailsCacheOrder.TryDequeue(out var oldestKey))
        {
            if (!string.Equals(oldestKey, cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                _videoDetailsCache.Remove(oldestKey);
            }
        }
    }

    private async Task PlayVideoSummaryAsync(VideoSummary summary)
    {
        try
        {
            if (_apiClient is null)
            {
                InitSessionWithoutCf();
            }

            // 播放只需要视频源：走 download 页轻量加载。
            var details = await GetOrLoadVideoDetailsAsync(summary.VideoId, VideoDetailsLoadOptions.Basic | VideoDetailsLoadOptions.Sources);
            if (details is null)
            {
                StatusText.Text = "读取详情失败。";
                return;
            }

            var source = SelectSourceByQuality(details.Sources);
            if (source is null)
            {
                StatusText.Text = "当前详情没有可用视频源。";
                return;
            }

            _currentDetails = details;
            _currentDetailsVideoId = details.VideoId;

            var title = details.Title;
            StatusText.Text = $"正在打开播放窗口: {title} ({source.QualityText})";
            var player = new Views.PlayerWindow(_settings) { Owner = this };
            await player.OpenAsync(title, source.Url, source.Type, details.VideoId);

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                LogError("player", "保存播放器窗口设置失败", ex);
                StatusText.Text = $"正在播放: {title} ({source.QualityText})，但播放器设置保存失败。";
                return;
            }

            StatusText.Text = $"正在播放: {title} ({source.QualityText})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"播放失败: {ex.Message}";
        }
    }

    private async void PlaySourceInline_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDetails || sender is not FrameworkElement { Tag: VideoSource source })
        {
            return;
        }

        try
        {
            var title = _currentDetails?.Title ?? GetSelectedVideoSummary()?.Title ?? source.QualityText;
            StatusText.Text = $"正在打开播放窗口: {title} ({source.QualityText})";
            var player = new Views.PlayerWindow(_settings) { Owner = this };
            await player.OpenAsync(title, source.Url, source.Type, _currentDetailsVideoId);

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                LogError("player", "保存播放器窗口设置失败", ex);
                StatusText.Text = $"正在播放: {title} ({source.QualityText})，但播放器设置保存失败。";
                return;
            }

            StatusText.Text = $"正在播放: {title} ({source.QualityText})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"播放失败: {ex.Message}";
        }
    }

    private void SourcesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = SourcesList.SelectedItem is VideoSource;
        QueueSourceButton.IsEnabled = hasSelection && !_isLoadingDetails;
        DownloadButton.IsEnabled = hasSelection && !_isLoadingDetails;
    }

    private void SourcesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isLoadingDetails || SourcesList.SelectedItem is not VideoSource)
        {
            return;
        }

        QueueSourceButton_OnClick(sender, new RoutedEventArgs());
    }
}
