using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Skua.App.Avalonia.Converters;

// Skua.WPF/Converters, on Avalonia's converter interfaces. The WPF
// Visibility converters have no counterpart: views bind IsVisible to the
// bool directly, or to !bool.

public sealed class EnumConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is Type type && value is not null ? Enum.Parse(type, value.ToString()!) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is Type type && value is not null ? (int)Enum.Parse(type, value.ToString()!) : 0;
}

public sealed class IntToBooleanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i != 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1 : 0;
}

public sealed class StringToFloatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float result);
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : 0f;
}

public sealed class StringToIntConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = int.TryParse(value?.ToString(), out int result);
        return result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && int.TryParse(s, out int result) ? result : 0;
}

public sealed class StringToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Empty;
}

public sealed class EqualToConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null && parameter is null)
            return true;
        if (value is null || parameter is null)
            return false;
        return value.Equals(parameter) || value.ToString() == parameter.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public sealed class GreaterThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null && parameter is null)
            return true;
        return ToInt(value, out int v) && ToInt(parameter, out int p) && v >= p;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;

    private static bool ToInt(object? o, out int result)
    {
        result = 0;
        switch (o)
        {
            case int i: result = i; return true;
            case string s: return int.TryParse(s, out result);
            case IConvertible c:
                try { result = System.Convert.ToInt32(c, CultureInfo.InvariantCulture); return true; }
                catch { return false; }
            default: return false;
        }
    }
}

public sealed class MultiValueEqualityConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(o => o?.Equals(values[0]) == true) || values.All(o => o is null);
}
