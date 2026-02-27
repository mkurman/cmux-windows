using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cmux.Converters;

/// <summary>Bool to Visibility (Visible/Collapsed).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Bool to Brush (parameter: "trueColor,falseColor").</summary>
public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var colors = (parameter as string)?.Split(',');
        var trueColor = colors?.Length > 0 ? colors[0] : "#FF818CF8";
        var falseColor = colors?.Length > 1 ? colors[1] : "#FF3B3B4F";
        
        var colorStr = value is true ? trueColor : falseColor;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorStr));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Int > 0 to Visibility.</summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Non-null/non-empty string to Visible, otherwise Collapsed.</summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Compares two values for equality (used for tab selection highlighting).</summary>
public class EqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Inverts a boolean.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is false;
}

/// <summary>Converts a hex color string (e.g. #FF818CF8) to a SolidColorBrush.</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF818CF8"));
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return fallback;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch
        {
            return fallback;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
            return brush.Color.ToString();
        return "#FF818CF8";
    }
}
