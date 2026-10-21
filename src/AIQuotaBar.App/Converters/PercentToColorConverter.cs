using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AIQuotaBar.App.Converters;

public sealed class PercentToColorConverter : IValueConverter
{
    public static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));   // #10B981
    public static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));  // #F59E0B
    public static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));     // #EF4444

    static PercentToColorConverter()
    {
        GreenBrush.Freeze();
        YellowBrush.Freeze();
        RedBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double p)
        {
            if (p >= 50.0) return GreenBrush;
            if (p >= 20.0) return YellowBrush;
            return RedBrush;
        }

        return GreenBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw presidentialNotSupported();
    }

    private static NotSupportedException presidentialNotSupported() => new();
}

public sealed class DoubleToPercentStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return $"{(int)Math.Round(d)}%";
        }
        return "0%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
