using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void CheckMenuAlignment(ContextMenu menu, MenuItem parent, Border submenu, string theme)
    {
        var rootRows = menu.Items.OfType<MenuItem>().ToArray();
        var childRows = parent.Items.OfType<MenuItem>().ToArray();
        var rootLabels = rootRows.Select(item => MenuLabel(item).TranslatePoint(new Point(), menu).X).ToArray();
        var childLabels = childRows.Select(item => MenuLabel(item).TranslatePoint(new Point(), submenu).X).ToArray();
        var rootHeights = rootRows.Select(item => item.ActualHeight).ToArray();
        var childHeights = childRows.Select(item => item.ActualHeight).ToArray();
        Console.WriteLine($"MENU {theme}: primary x={Measurements(rootLabels)} heights={Measurements(rootHeights)}; " +
            $"submenu x={Measurements(childLabels)} heights={Measurements(childHeights)}");
        SaveMenuPreview(menu, submenu, theme);

        Check(Spread(rootLabels.Concat(childLabels)) < 0.75 && Spread(rootHeights.Concat(childHeights)) < 0.75,
            $"{theme} menu aligns root and submenu labels and gives every row the same height");

        var arrow = (PathIcon(parent) ?? throw new InvalidOperationException("Submenu arrow template part is missing."));
        var width = parent.ActualWidth;
        Check(rootRows.All(item => Math.Abs(item.ActualWidth - width) < 0.75) &&
              arrow.IsVisible && arrow.TranslatePoint(new Point(), parent).X > width - 28 &&
              rootRows.Where(item => !item.HasItems).All(item => PathIcon(item) is { IsVisible: false }),
            $"{theme} rows fill the menu and only submenu arrows appear in the trailing column");
        var separator = menu.Items.OfType<Separator>().Single();
        var rule = separator.Template.FindName("MenuDivider", separator) as Border;
        Console.WriteLine($"DIVIDER {theme}: x={rule?.TranslatePoint(new Point(), menu).X:0.0}, width={rule?.ActualWidth:0.0}, separatorMargin={separator.Margin}");
        Check(rule is not null && Math.Abs(rule.TranslatePoint(new Point(), menu).X - rootLabels[0]) < 0.75,
            $"{theme} divider starts at the label column");
    }

    private static FrameworkElement? PathIcon(MenuItem row) => row.Template.FindName("MenuArrow", row) as FrameworkElement;

    private static FrameworkElement MenuLabel(MenuItem row)
    {
        var header = row.Header as string;
        return Descendants(row).OfType<ContentPresenter>().FirstOrDefault(p => Equals(p.Content, header)) ??
            (FrameworkElement?)Descendants(row).OfType<TextBlock>().FirstOrDefault(text => text.Text == header) ??
            throw new InvalidOperationException($"No header visual was found for {header}.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string Measurements(IEnumerable<double> values) =>
        string.Join(",", values.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)));

    private static double Spread(IEnumerable<double> values)
    {
        var all = values.ToArray();
        return all.Max() - all.Min();
    }

    private static void SaveMenuPreview(FrameworkElement menu, FrameworkElement submenu, string theme)
    {
        var directory = Environment.GetEnvironmentVariable("HANIME_MENU_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        const int padding = 8;
        static int Pixels(double length) => Math.Max(1, (int)Math.Ceiling(length));
        static RenderTargetBitmap Snapshot(FrameworkElement element)
        {
            var image = new RenderTargetBitmap(Pixels(element.ActualWidth), Pixels(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(element);
            return image;
        }

        var primary = Snapshot(menu);
        var child = Snapshot(submenu);
        var width = primary.PixelWidth + child.PixelWidth + 3 * padding;
        var height = Math.Max(primary.PixelHeight, child.PixelHeight) + 2 * padding;
        var canvas = new DrawingVisual();
        using (var drawing = canvas.RenderOpen())
        {
            drawing.DrawImage(primary, new Rect(padding, padding, primary.PixelWidth, primary.PixelHeight));
            drawing.DrawImage(child, new Rect(primary.PixelWidth + 2 * padding, padding, child.PixelWidth, child.PixelHeight));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(canvas);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, $"menu-{theme}.png"));
        png.Save(output);
    }
}
