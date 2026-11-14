using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Windows.Media.Imaging;
using Hanime1Downloader.CSharp;
using Hanime1Downloader.CSharp.Models;
using Hanime1Downloader.CSharp.Services;
using Hanime1Downloader.CSharp.Views;

internal static partial class Program
{
    private static int _passes;
    private static readonly DateTime Time = new(2026, 11, 1, 12, 34, 56);
    private static readonly DownloadNamingContext Context = new("[作者名] 视频标题", "12345", 1080, UploadDate: "2025-06-07", NamingTime: Time);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception("FAIL: " + message);
        _passes++;
        Console.WriteLine("PASS: " + message);
    }
    private static string Build(string rule, DownloadNamingContext? context = null) => DownloadNaming.BuildRelativePath(rule, context ?? Context);
    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    private static void Run()
    {
        Check(Build(DownloadNaming.AuthorRule) == @"作者名\2025-06-07-视频标题-1080p.mp4", "author directory, release date, clean title, quality render in requested order");
        Check(Build(DownloadNaming.SimpleRule) == "视频标题-1080p.mp4", "simple preset omits the author prefix");
        Check(Build("{name}-{quality}.MP4") == "视频标题-1080p.MP4", "simple filename preserves explicit uppercase MP4 extension without duplication");
        Check(Build("{quality}-{name}-{date}") == "1080p-视频标题-2025-06-07.mp4", "fields can be freely reordered");
        Check(Build("{AUTHOR}/{DATE}/{name}-{quality}") == @"作者名\2025-06-07\视频标题-1080p.mp4", "case-insensitive tokens and forward separators work");
        Check(Build("{title}_{videoId}") == "[作者名] 视频标题_12345.mp4", "legacy default preserves the complete original title");
        Check(Build("{downloadDate}-{timestamp}") == "2026-11-01-20261101_123456.mp4", "download date is distinct from release date and uses frozen queue time");
        Check(Build("{author}-{date}", Context with { Author = "手动作者", UploadDate = "2024-02-29" }) == "手动作者-2024-02-29.mp4", "explicit metadata overrides are honored");
        Check(Build("{author}-{date}-{quality}", new("No prefix", null, 0)) == "未知作者-未知日期-unknown.mp4", "missing metadata is explicit, not invented");
        Check(Build("{date}", Context with { UploadDate = "2025-02-30" }) == "未知日期.mp4", "invalid upload dates cannot look valid");
        Check(Build("{name}", Context with { Title = "【作者】标题" }) == "标题.mp4", "full-width author prefix is supported");
        Check(Build("{name}", Context with { Title = "no prefix" }) == "no prefix.mp4", "titles without a prefix remain intact");
        Check(Build("{title}", Context with { Title = "CON" }) == "_CON.mp4", "reserved Windows filename is sanitized");
        Check(Build("{title}", Context with { Title = new string('x', 500) }).Length == 184, "long names respect the existing filename length policy");
        Check(Build("{title}", Context with { Title = "../a\\b:c?d" }).Count(c => c is '\\' or '/') == 0, "video metadata cannot create directories");
        Check(Build(@"{author}\{name}", Context with { Author = "..\\outside" }).Split('\\').Length == 2, "author input cannot add extra path components");
        foreach (var rule in new[] { "", "plain name", "{unknown}", "{title", "title}", "{{title}}", @"..\{title}", @"{author}\..\{title}", @"C:\{title}", @"\\server\{title}", @"\{title}", @"{author}\\{title}", "{title}\n", "{title}:stream", "{title}?", @"a\b\c\d\e\f\{title}" })
        {
            Check(!DownloadNaming.TryValidate(rule, out var error) && error.Length > 0, "invalid rule rejected: " + rule.Replace("\n", "\\n"));
            var threw = false;
            try { Build(rule); } catch (ArgumentException) { threw = true; }
            Check(threw, "renderer enforces the same validation");
        }
        foreach (var rule in new[] { "{title}", "{title}_{quality}", "{title}_{videoId}", "{title}_{quality}_{timestamp}", "{videoId}_{quality}", "{title}_{timestamp}", "{timestamp}" })
            Check(DownloadNaming.TryValidate(rule, out _) && Build(rule).EndsWith(".mp4"), "legacy preset remains supported: " + rule);
        var root = Path.Combine(Path.GetTempPath(), "hanime-naming-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var relative = Build(DownloadNaming.AuthorRule);
        var target = DownloadPathGuard.EnsureWithinDirectory(root, Path.Combine(root, relative));
        Check(Path.GetRelativePath(root, target) == relative, "subfolder target stays inside the download directory");
        var output = (string)typeof(DownloadService).GetMethod("ValidateOutputPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [target])!;
        Check(output == target && Directory.Exists(Path.GetDirectoryName(target)), "download output preparation creates the author directory");
        CheckQueueRecordsAndResumeGuards(root);
        CheckEditor();
        Console.WriteLine($"TOTAL: {_passes} naming/model/WPF assertions passed. No application settings or real downloads were changed.");
    }

    private static void CheckQueueRecordsAndResumeGuards(string root)
    {
        var recordType = typeof(MainWindow).GetNestedType("DownloadQueueRecord", BindingFlags.NonPublic)!;
        var record = Activator.CreateInstance(recordType, true)!;
        var expected = new Dictionary<string, object?>
        {
            ["Title"] = Context.Title, ["VideoId"] = Context.VideoId, ["NamingRule"] = DownloadNaming.AuthorRule,
            ["NamingAuthor"] = "Override", ["NamingDate"] = "2024-01-02", ["UploadDate"] = Context.UploadDate, ["NamingTime"] = Time
        };
        foreach (var pair in expected) recordType.GetProperty(pair.Key)!.SetValue(record, pair.Value);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(record, recordType), recordType)!;
        foreach (var pair in expected) Check(Equals(recordType.GetProperty(pair.Key)!.GetValue(restored), pair.Value), "queue roundtrip preserves " + pair.Key);
        var legacy = JsonSerializer.Deserialize("{\"Title\":\"Old\",\"VideoId\":\"1\",\"TargetPath\":\"old.mp4\"}", recordType)!;
        Check(recordType.GetProperty("NamingRule")!.GetValue(legacy) is null, "old queue files load without a per-item rule");
        var itemType = typeof(MainWindow).GetNestedType("DownloadQueueItem", BindingFlags.NonPublic)!;
        var item = Activator.CreateInstance(itemType, true)!;
        var guard = typeof(MainWindow).GetMethod("CanChangeQueueNaming", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool CanChange() => (bool)guard.Invoke(null, [item])!;
        var path = Path.Combine(root, "video.mp4");
        itemType.GetProperty("TargetPath")!.SetValue(item, path);
        Check(CanChange(), "untouched queue item can change naming");
        itemType.GetProperty("IsDownloading")!.SetValue(item, true);
        Check(!CanChange(), "active download cannot change naming");
        itemType.GetProperty("IsDownloading")!.SetValue(item, false);
        File.WriteAllText(path + ".tmp", "test resume data");
        Check(!CanChange(), "MP4 resume file blocks naming changes");
        var hlsPath = Path.Combine(root, "hls.mp4");
        itemType.GetProperty("TargetPath")!.SetValue(item, hlsPath);
        Directory.CreateDirectory(hlsPath + ".hls");
        Check(!CanChange(), "HLS segment directory blocks naming changes");
        var donePath = Path.Combine(root, "done.mp4");
        itemType.GetProperty("TargetPath")!.SetValue(item, donePath);
        File.WriteAllText(donePath, "test output");
        Check(!CanChange(), "completed media is not renamed");
    }

    private static void CheckEditor()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load("App.xaml").Root!.Element(ns + "Application.Resources")!;
        var dictionary = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "converters", "clr-namespace:Hanime1Downloader.CSharp.Converters;assembly=Hanime1Downloader.CSharp"),
            resources.Elements());
        foreach (var node in dictionary.Descendants().Where(n => n.Name.NamespaceName == "clr-namespace:Hanime1Downloader.CSharp.Converters"))
            node.Name = XName.Get(node.Name.LocalName, "clr-namespace:Hanime1Downloader.CSharp.Converters;assembly=Hanime1Downloader.CSharp");
        var resourceXaml = dictionary.ToString().Replace("pack://application:,,,/Assets/app.ico", "pack://application:,,,/Hanime1Downloader.CSharp;component/Assets/app.ico");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resourceXaml);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Hanime1Downloader.CSharp;component/Themes/LightTheme.xaml", UriKind.Relative) });
        var editor = new NamingRuleEditor { RuleText = DownloadNaming.AuthorRule, PreviewContext = Context };
        var preview = (TextBlock)editor.FindName("PreviewText");
        Check(preview.Text == Build(DownloadNaming.AuthorRule), "WPF preview uses the production renderer");
        editor.RuleText = "{quality}-{name}.MP4";
        Check(preview.Text == Build("{quality}-{name}.MP4"), "typing a reordered rule updates the preview immediately");
        editor.RuleText = "{invalid}";
        Check(((TextBlock)editor.FindName("ErrorText")).Visibility == Visibility.Visible, "invalid fields show inline validation");
        var preset = (ComboBox)editor.FindName("PresetCombo");
        preset.SelectedValue = DownloadNaming.SimpleRule;
        Check(editor.RuleText == DownloadNaming.SimpleRule, "presets remain editable starting points");
        var templateBox = (TextBox)editor.FindName("TemplateBox");
        templateBox.CaretIndex = 0;
        typeof(NamingRuleEditor).GetMethod("TokenButton_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [new Button { Tag = "{date}-" }, new RoutedEventArgs()]);
        Check(editor.RuleText == "{date}-{name}-{quality}", "insertion button inserts at the current cursor instead of a fixed ordering");
        var settings = new AppSettings { FileNamingRule = @"{author}\{quality}-{name}" };
        var dialog = new SettingsDialog(settings);
        var settingsEditor = (NamingRuleEditor)dialog.FindName("NamingEditor");
        Check(settingsEditor.RuleText == settings.FileNamingRule, "opening settings preserves custom rules absent from the presets");
        var queueDialog = new QueueNamingDialog(DownloadNaming.AuthorRule, Context, 1, "Override", "2024-01-02");
        var queueEditor = (NamingRuleEditor)queueDialog.FindName("RuleEditor");
        Check(((TextBlock)queueEditor.FindName("PreviewText")).Text.StartsWith(@"Override\2024-01-02"), "per-video metadata preview uses author and date overrides");
        var batch = new QueueNamingDialog(DownloadNaming.SimpleRule, Context, 2, null, null);
        Check(((StackPanel)batch.FindName("MetadataPanel")).Visibility == Visibility.Collapsed, "batch rule changes do not replace every video's author or date");
        Capture((FrameworkElement)queueDialog.Content, "artifacts/naming-dialog-preview.png", 538, 522);
        Capture(settingsEditor, "artifacts/naming-settings-preview.png", 475, 330);
        var theme = app.Resources.MergedDictionaries[0];
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("/Hanime1Downloader.CSharp;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        Capture((FrameworkElement)queueDialog.Content, "artifacts/naming-dialog-dark.png", 538, 522);
        app.Resources.MergedDictionaries[0] = theme;
        var savedRule = @"{quality}\{author}-{name}.MP4";
        settingsEditor.RuleText = savedRule;
        dialog.Loaded += (_, _) => typeof(SettingsDialog).GetMethod("ConfirmButton_OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
        Check(dialog.ShowDialog() == true && dialog.Settings.FileNamingRule == savedRule, "the settings save action preserves a custom reordered rule");
        Check(settings.FileNamingRule == @"{author}\{quality}-{name}", "dialog editing does not mutate the caller's settings until applied");
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(dialog.Settings))!;
        Check(savedSettings.FileNamingRule == savedRule, "custom default rule survives settings JSON persistence");
        dialog.Close(); queueDialog.Close(); batch.Close();
        CheckSettingsWorkflow(app);
        app.Shutdown();
    }
    private static void Capture(FrameworkElement element, string path, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle((Brush)Application.Current.FindResource("ThemeDialogBackgroundBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
