using System.Collections;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;

internal static partial class Program
{
    private static string[] MenuHeaders(ItemsControl menu) => menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
    private static MenuItem MenuEntry(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
    private static Array TypedSelection(params object[] items)
    {
        var array = Array.CreateInstance(items[0].GetType(), items.Length);
        for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i);
        return array;
    }
    private static VideoSummary Summary(string id) => new() { VideoId = id, Title = "Title " + id, Url = Host + "watch?v=" + id };

    private static async Task CheckSubmenuPopupAsync(ContextMenu menu, string theme)
    {
        var anchor = new Border();
        var host = new Window { Content = anchor, Width = 360, Height = 210, ShowInTaskbar = false };
        try
        {
            host.Show();
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var download = MenuEntry(menu, "下载");
            download.IsSubmenuOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = download.Template.FindName("PART_Popup", download) as Popup;
            Check(popup is { IsOpen: true, Child: Border surface } && Equals(surface.Background, surface.FindResource("ThemeSurfaceBrush")),
                "grouped download opens a real themed submenu popup");
            CheckMenuAlignment(menu, download, (Border)popup!.Child, theme);
        }
        finally
        {
            menu.IsOpen = false;
            host.Close();
        }
    }

    private static async Task CheckContextMenusAsync()
    {
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.Headers.Range is not null ? Partial(0, [1], 4) : Response(HttpStatusCode.OK, Bytes("ONLY"));
        });
        var window = Setup(client, 1, "context-menus");
        try
        {
            var picked = Summary("picked");
            var results = (ContextMenu)Call(window, "BuildVideoContextMenu", new[] { picked }, false, false)!;
            Check(MenuHeaders(results).SequenceEqual(new[] { "播放视频", "下载", "添加到收藏夹" }) &&
                  MenuHeaders(MenuEntry(results, "下载")).SequenceEqual(new[] { "立即下载所选视频", "仅加入队列" }),
                "video menu groups distinct download modes and removes the duplicate details action");
            await CheckSubmenuPopupAsync(results, "light");
            AppTheme.Apply(Application.Current, AppTheme.Dark);
            try
            {
                var darkMenu = (ContextMenu)Call(window, "BuildVideoContextMenu", new[] { picked }, false, false)!;
                await CheckSubmenuPopupAsync(darkMenu, "dark");
            }
            finally { AppTheme.Apply(Application.Current, AppTheme.Light); }
            var related = (ContextMenu)Call(window, "BuildVideoContextMenu", new[] { picked }, false, true)!;
            Check(MenuHeaders(related).Contains("查看详情"), "related-video menu retains details because selection alone does not load them");
            var favorites = (ContextMenu)Call(window, "BuildVideoContextMenu", new[] { picked }, true, false)!;
            Check(MenuHeaders(favorites).Contains("从当前收藏夹移除") && !MenuHeaders(favorites).Contains("查看详情"),
                "favorites menu names the removal scope and does not repeat the selection action");
            SetField(window, "_isDownloadingQueue", true);
            var active = (ContextMenu)Call(window, "BuildVideoContextMenu", new[] { picked, Summary("other") }, false, false)!;
            Check(MenuHeaders(active).SequenceEqual(new[] { "加入下载队列", "添加到收藏夹" }),
                "while a queue is running the video menu offers one enqueue action and hides single-video commands");
            SetField(window, "_isDownloadingQueue", false);

            var a = Enqueue(window, "other-a", Source("other-a"));
            var b = Enqueue(window, "other-b", Source("other-b"));
            var c = Enqueue(window, "other-c", Source("other-c"));
            var queueMenu = (ContextMenu)Call(window, "BuildQueueContextMenu", (object)TypedSelection(b))!;
            Check(MenuHeaders(MenuEntry(queueMenu, "调整顺序")).SequenceEqual(new[] { "上移一位", "下移一位" }),
                "queue reorder hides actions with an identical outcome at the boundaries");
            var stateType = b.GetType().GetProperty("QueueState")!.PropertyType;
            Put(b, "QueueState", Enum.Parse(stateType, "Error"));
            var mixed = (ContextMenu)Call(window, "BuildQueueContextMenu", (object)TypedSelection(a, b))!;
            Check(MenuHeaders(mixed).First() == "下载 / 重试所选项" && !MenuHeaders(mixed).Contains("重新解析并重试"),
                "mixed waiting and failed selections have one contextual start/retry action");
            SetField(window, "_isDownloadingQueue", true);
            SetField(window, "_queueRunSelectionOnly", true);
            Put(a, "IsDownloading", true);
            var activeQueue = (ContextMenu)Call(window, "BuildQueueContextMenu", (object)TypedSelection(a, b))!;
            Check(MenuHeaders(activeQueue).SequenceEqual(new[] { "停止并移出队列" }),
                "queue context menu never pauses the whole queue or schedules work outside an active selection");
            Put(a, "IsDownloading", false);
            SetField(window, "_isDownloadingQueue", false);

            var waitingQueue = (IList)Field(window, "_downloadQueue")!;
            var source = Source("picked");
            Cache(window, "picked", source);
            var existingPicked = Enqueue(window, "picked", source);
            await ((Task)Call(window, "QueueVideosForDownloadAsync", new[] { picked }, true)!).WaitAsync(TimeoutForTest);
            Check(waitingQueue.Count == 3 && waitingQueue.Contains(a) && waitingQueue.Contains(b) && waitingQueue.Contains(c) &&
                  requests.SequenceEqual(new[] { "/picked-720.mp4", "/picked-720.mp4" }) &&
                  File.ReadAllText((string)Get(existingPicked, "TargetPath")!) == "ONLY",
                "immediate download reuses the selected queued source and does not start unrelated work");
            await ((Task)Call(window, "StartQueueDownloadAsync", (object)TypedSelection(existingPicked))!).WaitAsync(TimeoutForTest);
            Check(waitingQueue.Count == 3 && requests.Count == 2, "a stale explicit selection never falls back to starting the entire queue");

            var history = (IList)Field(window, "_historyItems")!;
            var record = history[0]!;
            var single = (ContextMenu)Call(window, "BuildHistoryContextMenu", (object)TypedSelection(record))!;
            var multi = (ContextMenu)Call(window, "BuildHistoryContextMenu", (object)TypedSelection(record, record))!;
            Check(MenuHeaders(single).SequenceEqual(new[] { "打开所在文件夹", "移除历史记录" }) &&
                  MenuHeaders(multi).SequenceEqual(new[] { "移除 2 条历史记录" }),
                "history menu distinguishes one-file navigation from batch record removal");
            var fullPath = (string)Get(record, "FullPath")!;
            await ((Task)Call(window, "RemoveHistoryRecordsAsync", (object)TypedSelection(record))!).WaitAsync(TimeoutForTest);
            Check(history.Count == 0 && File.Exists(fullPath), "removing history does not delete the downloaded file");
        }
        finally
        {
            SetField(window, "_isDownloadingQueue", false);
            window.Close();
        }
    }
}
