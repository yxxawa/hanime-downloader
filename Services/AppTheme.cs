using Hanime1Downloader.CSharp.Models;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Hanime1Downloader.CSharp.Services;

public static class AppTheme
{
    public const string Light = "light";
    public const string Dark = "dark";

    private const string ThemeDictionaryMarkerKey = "AppThemeDictionaryMarker";
    private static readonly string SettingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);

    public static string Normalize(string? themeMode)
    {
        return string.Equals(themeMode, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
    }

    public static string ReadSavedThemeMode()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return Light;
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath));
            return Normalize(settings?.ThemeMode);
        }
        catch
        {
            return Light;
        }
    }

    public static void Apply(Application? application, string? themeMode)
    {
        if (application is null)
        {
            return;
        }

        var targetSource = Normalize(themeMode) == Dark ? DarkThemeUri : LightThemeUri;
        var mergedDictionaries = application.Resources.MergedDictionaries;
        var existingDictionary = mergedDictionaries.FirstOrDefault(IsThemeDictionary);
        if (existingDictionary?.Source == targetSource)
        {
            return;
        }

        var themeDictionary = new ResourceDictionary
        {
            Source = targetSource
        };
        themeDictionary[ThemeDictionaryMarkerKey] = true;

        var isDark = Normalize(themeMode) == Dark;

        if (existingDictionary is null)
        {
            mergedDictionaries.Insert(0, themeDictionary);
        }
        else
        {
            mergedDictionaries[mergedDictionaries.IndexOf(existingDictionary)] = themeDictionary;
        }

        ApplySystemColors(application, isDark);
        TitleBarTheme.SetDark(isDark);
    }

    private static void ApplySystemColors(Application application, bool isDark)
    {
        if (isDark)
        {
            application.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x30));
            application.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
            application.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Color.FromRgb(0x25, 0x30, 0x41));
            application.Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
            application.Resources[SystemColors.ScrollBarBrushKey] = new SolidColorBrush(Color.FromRgb(0x1D, 0x24, 0x30));
            application.Resources[SystemColors.MenuBrushKey] = new SolidColorBrush(Color.FromRgb(0x1D, 0x24, 0x30));
            application.Resources[SystemColors.MenuTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
            application.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x16, 0x32, 0x4F));
            application.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
            application.Resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }
        else
        {
            application.Resources[SystemColors.WindowBrushKey] = new SolidColorBrush(Colors.White);
            application.Resources[SystemColors.WindowTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            application.Resources[SystemColors.ControlBrushKey] = new SolidColorBrush(Colors.White);
            application.Resources[SystemColors.ControlTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            application.Resources[SystemColors.ScrollBarBrushKey] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            application.Resources[SystemColors.MenuBrushKey] = new SolidColorBrush(Colors.White);
            application.Resources[SystemColors.MenuTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            application.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0xE8, 0xF3, 0xFF));
            application.Resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            application.Resources[SystemColors.GrayTextBrushKey] = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        }
    }

    public static Brush GetBrush(string resourceKey)
    {
        return (Brush)Application.Current.FindResource(resourceKey);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        if (dictionary.Contains(ThemeDictionaryMarkerKey))
        {
            return true;
        }

        var source = dictionary.Source?.OriginalString;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        return source.EndsWith("Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("/Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("/Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase);
    }
}