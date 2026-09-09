using Hanime1Downloader.CSharp.Models;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Hanime1Downloader.CSharp.Views;

public partial class FilterDialog : Window
{
    private const string SearchOptionsResourcePrefix = "Hanime1Downloader.CSharp.Assets.SearchOptions.";
    private readonly IReadOnlyList<MultiSelectDialog.OptionGroup> _tagGroups;
    private static readonly Lazy<IReadOnlyList<MultiSelectDialog.OptionGroup>> CachedTagGroups =
        new(BuildTagGroups, LazyThreadSafetyMode.ExecutionAndPublication);
    private bool _isInitializing;

    public SearchFilterOptions FilterOptions { get; }

    public FilterDialog(SearchFilterOptions currentOptions)
    {
        _isInitializing = true;
        InitializeComponent();
        FilterOptions = new SearchFilterOptions
        {
            Genre = currentOptions.Genre,
            Sort = currentOptions.Sort,
            Date = currentOptions.Date,
            Duration = currentOptions.Duration,
            Tags = currentOptions.Tags.ToList(),
            Broad = currentOptions.Broad
        };

        _tagGroups = CachedTagGroups.Value;

        GenreCombo.ItemsSource = CreateOptions(("全部", ""), ("里番", "裏番"), ("泡面番", "泡麵番"), ("Motion Anime", "Motion Anime"), ("3DCG", "3DCG"), ("2.5D", "2.5D"), ("2D动画", "2D動畫"), ("AI生成", "AI生成"), ("MMD", "MMD"), ("Cosplay", "Cosplay"));
        SortCombo.ItemsSource = CreateOptions(("默认", ""), ("最新上市", "最新上市"), ("最新上传", "最新上傳"), ("本日排行", "本日排行"), ("本周排行", "本週排行"), ("本月排行", "本月排行"), ("观看次数", "觀看次數"), ("点赞比例", "讚好比例"), ("时长最长", "時長最長"), ("他们在看", "他們在看"));
        DateCombo.ItemsSource = CreateOptions(("全部", ""), ("过去 24 小时", "過去 24 小時"), ("过去 2 天", "過去 2 天"), ("过去 1 周", "過去 1 週"), ("过去 1 个月", "過去 1 個月"), ("过去 3 个月", "過去 3 個月"), ("过去 1 年", "過去 1 年"));
        DurationCombo.ItemsSource = CreateOptions(("全部", ""), ("1 分钟 +", "1 分鐘 +"), ("5 分钟 +", "5 分鐘 +"), ("10 分钟 +", "10 分鐘 +"), ("20 分钟 +", "20 分鐘 +"), ("30 分钟 +", "30 分鐘 +"), ("60 分钟 +", "60 分鐘 +"), ("0 - 10 分钟", "0 - 10 分鐘"), ("0 - 20 分钟", "0 - 20 分鐘"));

        GenreCombo.SelectedValue = FilterOptions.Genre;
        SortCombo.SelectedValue = FilterOptions.Sort;
        DateCombo.SelectedValue = FilterOptions.Date;
        DurationCombo.SelectedValue = FilterOptions.Duration;
        BroadCheckBox.IsChecked = FilterOptions.Broad;
        _isInitializing = false;
        Loaded += (_, _) => GenreCombo.Focus();
        RefreshSummaryTexts();
    }

    private void ChooseTagsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new MultiSelectDialog("选择标签", "从 Han1meViewer 的标签分类中选择搜索标签。", _tagGroups, FilterOptions.Tags)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        FilterOptions.Tags = dialog.SelectedKeys;
        RefreshSummaryTexts();
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        GenreCombo.SelectedIndex = 0;
        SortCombo.SelectedIndex = 0;
        DateCombo.SelectedIndex = 0;
        DurationCombo.SelectedIndex = 0;
        FilterOptions.Tags.Clear();
        BroadCheckBox.IsChecked = false;
        RefreshSummaryTexts();
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        SyncQuickFilterValues();
        DialogResult = true;
    }

    private void QuickFilterControl_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SyncQuickFilterValues();
        RefreshSummaryTexts();
    }

    private void SyncQuickFilterValues()
    {
        FilterOptions.Genre = GenreCombo.SelectedValue as string ?? string.Empty;
        FilterOptions.Sort = SortCombo.SelectedValue as string ?? string.Empty;
        FilterOptions.Date = DateCombo.SelectedValue as string ?? string.Empty;
        FilterOptions.Duration = DurationCombo.SelectedValue as string ?? string.Empty;
        FilterOptions.Broad = BroadCheckBox.IsChecked == true;
    }

    private void ClearGenreButton_OnClick(object sender, RoutedEventArgs e)
    {
        GenreCombo.SelectedIndex = 0;
        RefreshSummaryTexts();
    }

    private void ClearSortButton_OnClick(object sender, RoutedEventArgs e)
    {
        SortCombo.SelectedIndex = 0;
        RefreshSummaryTexts();
    }

    private void ClearDateButton_OnClick(object sender, RoutedEventArgs e)
    {
        DateCombo.SelectedIndex = 0;
        RefreshSummaryTexts();
    }

    private void ClearDurationButton_OnClick(object sender, RoutedEventArgs e)
    {
        DurationCombo.SelectedIndex = 0;
        RefreshSummaryTexts();
    }

    private void ClearTagsButton_OnClick(object sender, RoutedEventArgs e)
    {
        FilterOptions.Tags.Clear();
        RefreshSummaryTexts();
    }

    private void RefreshSummaryTexts()
    {
        TagsSummaryText.Text = BuildSummaryText(FilterOptions.Tags, "未选择标签，可点击右侧按钮按分类选择。", 10);

        var activeCount = 0;
        if (!string.IsNullOrWhiteSpace(FilterOptions.Genre)) activeCount++;
        if (!string.IsNullOrWhiteSpace(FilterOptions.Sort)) activeCount++;
        if (!string.IsNullOrWhiteSpace(FilterOptions.Date)) activeCount++;
        if (!string.IsNullOrWhiteSpace(FilterOptions.Duration)) activeCount++;
        if (FilterOptions.Tags.Count > 0) activeCount++;
        if (FilterOptions.Broad) activeCount++;

        FilterStateText.Text = activeCount == 0 ? "当前未启用额外筛选" : $"当前已启用 {activeCount} 组筛选条件";
    }

    private static string BuildSummaryText(IReadOnlyList<string> values, string emptyText, int previewCount)
    {
        if (values.Count == 0)
        {
            return emptyText;
        }

        var visibleValues = values.Take(previewCount).ToList();
        return values.Count > previewCount
            ? $"已选 {values.Count} 项：{string.Join("、", visibleValues)} 等"
            : $"已选 {values.Count} 项：{string.Join("、", visibleValues)}";
    }

    private static IReadOnlyList<MultiSelectDialog.OptionGroup> BuildTagGroups()
    {
        var json = ReadEmbeddedSearchOptionJson("tags.json");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var data = JsonSerializer.Deserialize<Dictionary<string, List<SearchOptionAsset>>>(json) ?? [];
        return data
            .Select(pair => new MultiSelectDialog.OptionGroup
            {
                Title = pair.Key switch
                {
                    "video_attributes" => "视频属性",
                    "character_relationships" => "人物关系",
                    "characteristics" => "人物特点",
                    "appearance_and_figure" => "外貌体型",
                    "story_location" => "故事场景",
                    "story_plot" => "剧情设定",
                    "sex_positions" => "体位玩法",
                    _ => pair.Key
                },
                Options = pair.Value
                    .Where(option => !string.IsNullOrWhiteSpace(option.SearchKey))
                    .Select(option => new MultiSelectDialog.OptionItem
                    {
                        Label = ReadLocalizedText(option.Lang, option.SearchKey),
                        SearchKey = option.SearchKey
                    })
                    .ToList()
            })
            .Where(group => group.Options.Count > 0)
            .ToList();
    }

    private static string ReadEmbeddedSearchOptionJson(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SearchOptionsResourcePrefix + fileName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadLocalizedText(Dictionary<string, string>? lang, string fallback)
    {
        if (lang is null)
        {
            return fallback;
        }

        if (lang.TryGetValue("zh-rCN", out var simplified) && !string.IsNullOrWhiteSpace(simplified))
        {
            return simplified;
        }

        if (lang.TryGetValue("zh-rTW", out var traditional) && !string.IsNullOrWhiteSpace(traditional))
        {
            return traditional;
        }

        if (lang.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english))
        {
            return english;
        }

        return fallback;
    }

    private static List<FilterOptionItem> CreateOptions(params (string Label, string Value)[] options)
    {
        return options.Select(option => new FilterOptionItem { Label = option.Label, Value = option.Value }).ToList();
    }

    private sealed class FilterOptionItem
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
    }

    private sealed class SearchOptionAsset
    {
        [JsonPropertyName("lang")]
        public Dictionary<string, string>? Lang { get; init; }

        [JsonPropertyName("search_key")]
        public required string SearchKey { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
