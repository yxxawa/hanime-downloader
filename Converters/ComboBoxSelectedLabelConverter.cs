using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace Hanime1Downloader.CSharp.Converters;

public sealed class ComboBoxSelectedLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var prop = value.GetType().GetProperty("Label", BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(value) as string ?? value.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
