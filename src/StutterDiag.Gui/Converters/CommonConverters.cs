using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace StutterDiag.Gui.Converters;

/// <summary>bool → Visibility. Pass ConverterParameter="Invert" to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (IsInvert(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object? p) =>
        p is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>null / empty string / empty collection → Collapsed. "Invert" flips.</summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasContent = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection c => c.Count > 0,
            sbyte or byte or short or ushort or int or uint or long or ulong => System.Convert.ToInt64(value) != 0,
            _ => true
        };
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            hasContent = !hasContent;
        return hasContent ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Health status string ("Ok" / "Degraded" / "Unavailable" / "Failed" / "Unknown") → brush.
/// Falls back to a neutral brush. Resolves theme brushes by key when the app supplies them.
/// </summary>
public sealed class HealthStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = (value as string ?? string.Empty).Trim();
        var key = status.ToLowerInvariant() switch
        {
            "ok" => "Brush.Good",
            "degraded" => "Brush.Warn",
            "unavailable" => "Brush.Muted",
            "failed" => "Brush.Bad",
            _ => "Brush.Muted"
        };

        if (Application.Current?.TryFindResource(key) is Brush themed)
            return themed;

        return status.ToLowerInvariant() switch
        {
            "ok" => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            "degraded" => new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00)),
            "failed" => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
            _ => new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Correlation score string ("High" / "Medium" / "Low" / "NoEvidence") → brush.
/// HIGH is the "worth investigating" colour; NO EVIDENCE is the reassuring one.
/// This is a visual cue about temporal proximity, not a statement of cause.
/// </summary>
public sealed class CorrelationScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = (value as string ?? string.Empty).Trim().ToLowerInvariant();
        var key = score switch
        {
            "high" => "Brush.Bad",
            "medium" => "Brush.Warn",
            "low" => "Brush.Muted",
            "noevidence" => "Brush.Good",
            _ => "Brush.Muted"
        };
        if (Application.Current?.TryFindResource(key) is Brush themed) return themed;

        return score switch
        {
            "high" => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
            "medium" => new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00)),
            "noevidence" => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            _ => new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>True when this row's value looks like missing data (drives grey text on the System page).</summary>
public sealed class UnavailableToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        bool missing = string.IsNullOrWhiteSpace(s)
                       || s.Equals("Unavailable", StringComparison.OrdinalIgnoreCase)
                       || s.StartsWith("Not available", StringComparison.OrdinalIgnoreCase);

        var key = missing ? "Brush.Muted" : "Brush.Text";
        if (Application.Current?.TryFindResource(key) is Brush themed) return themed;
        return missing
            ? new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
            : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>MultiBinding: true when all provided values are equal (used for nav highlight).</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var first = values[0];
        for (int i = 1; i < values.Length; i++)
            if (!Equals(first, values[i])) return false;
        return true;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
