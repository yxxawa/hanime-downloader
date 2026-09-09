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

    private sealed class FavoriteVideoRecord
    {
        public required string VideoId { get; init; }
        public required string Title { get; init; }
        public required string Url { get; init; }
        public string CoverUrl { get; init; } = string.Empty;
    }

    private sealed class DownloadQueueRecord
    {
        public required string Title { get; init; }
        public string Url { get; init; } = string.Empty;
        public string Type { get; init; } = "mp4";
        public int Quality { get; init; }
        public required string VideoId { get; init; }
        public string TargetPath { get; init; } = string.Empty;
        public bool HasError { get; init; }
    }

    private sealed class QueueItemProcessResult
    {
        public required QueueItemOutcome Outcome { get; init; }

        public static QueueItemProcessResult Completed() => new() { Outcome = QueueItemOutcome.Completed };
        public static QueueItemProcessResult Paused() => new() { Outcome = QueueItemOutcome.Paused };
        public static QueueItemProcessResult Error() => new() { Outcome = QueueItemOutcome.Error };
        public static QueueItemProcessResult Removed() => new() { Outcome = QueueItemOutcome.Removed };
    }

    private sealed class DownloadHistoryItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _fileExists;

        public required string TimeText { get; init; }
        public required string FileName { get; init; }
        public required string Url { get; init; }
        public required string FullPath { get; init; }
        public required string RawText { get; init; }

        /// <summary>文件存在性只在创建/刷新时检查一次，避免每次绑定都做磁盘 IO。</summary>
        public bool FileExists
        {
            get => _fileExists;
            private set
            {
                if (_fileExists == value)
                {
                    return;
                }

                _fileExists = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FileExists)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FileStateText)));
            }
        }

        public string FileStateText => FileExists ? "文件存在" : "文件缺失";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshFileState()
        {
            FileExists = !string.IsNullOrWhiteSpace(FullPath) && File.Exists(FullPath);
        }

        public static DownloadHistoryItem Create(DateTime time, string fileName, string url, string fullPath)
        {
            var rawText = $"{time:yyyy-MM-dd HH:mm:ss} | {fileName} | {url} | {fullPath}";
            var item = new DownloadHistoryItem
            {
                TimeText = time.ToString("yyyy-MM-dd HH:mm:ss"),
                FileName = fileName,
                Url = url,
                FullPath = fullPath,
                RawText = rawText
            };
            item.RefreshFileState();
            return item;
        }

        public static DownloadHistoryItem FromRawText(string rawText)
        {
            var parts = rawText.Split(" | ", 4, StringSplitOptions.None);
            var item = new DownloadHistoryItem
            {
                TimeText = parts.ElementAtOrDefault(0) ?? string.Empty,
                FileName = parts.ElementAtOrDefault(1) ?? rawText,
                Url = parts.ElementAtOrDefault(2) ?? string.Empty,
                FullPath = parts.ElementAtOrDefault(3) ?? string.Empty,
                RawText = rawText
            };
            item.RefreshFileState();
            return item;
        }
    }

    private enum DownloadQueueState
    {
        Waiting,
        Resolving,
        Checking,
        Verifying,
        Downloading,
        Finalizing,
        Paused,
        Error,
        Completed
    }

    private enum QueueRunSummaryState
    {
        Idle,
        Running,
        Paused,
        Completed
    }

    private enum QueueItemOutcome
    {
        Completed,
        Paused,
        Error,
        Removed
    }

    private sealed class DownloadQueueItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _url = string.Empty;
        private string _type = string.Empty;
        private int _quality;
        private string _videoId = string.Empty;
        private string _targetPath = string.Empty;
        private bool _isDownloading;
        private bool _hasError;
        private DownloadQueueState _queueState = DownloadQueueState.Waiting;
        private string _queueStatusText = "等待中";
        private string _stageText = "等待";
        private double? _progressValue;
        private bool _isProgressIndeterminate;
        private bool _showProgress;

        public event PropertyChangedEventHandler? PropertyChanged;

        public required string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public required string Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        public required string Type
        {
            get => _type;
            set
            {
                if (SetField(ref _type, value))
                {
                    OnPropertyChanged(nameof(TypeText));
                }
            }
        }

        public int Quality
        {
            get => _quality;
            set
            {
                if (SetField(ref _quality, value))
                {
                    OnPropertyChanged(nameof(QualityText));
                }
            }
        }

        public string VideoId
        {
            get => _videoId;
            set => SetField(ref _videoId, value);
        }

        public string TargetPath
        {
            get => _targetPath;
            set => SetField(ref _targetPath, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetField(ref _isDownloading, value);
        }

        public bool HasError
        {
            get => _hasError;
            set => SetField(ref _hasError, value);
        }

        public DownloadQueueState QueueState
        {
            get => _queueState;
            set => SetField(ref _queueState, value);
        }

        public string QueueStatusText
        {
            get => _queueStatusText;
            set => SetField(ref _queueStatusText, value);
        }

        public string StageText
        {
            get => _stageText;
            set => SetField(ref _stageText, value);
        }

        public double? ProgressValue
        {
            get => _progressValue;
            set => SetField(ref _progressValue, value);
        }

        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            set => SetField(ref _isProgressIndeterminate, value);
        }

        public bool ShowProgress
        {
            get => _showProgress;
            set => SetField(ref _showProgress, value);
        }

        public string QualityText => Quality > 0 ? $"{Quality}p" : "未知清晰度";
        public string TypeText => Type.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? "M3U8" : "MP4";

        public override string ToString()
        {
            return $"[{QualityText}] {Title}";
        }

        private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
