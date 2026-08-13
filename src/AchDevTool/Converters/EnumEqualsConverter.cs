using System.Globalization;
using Avalonia.Data.Converters;

namespace AchDevTool.Converters;

/// <summary>Compares a bound value's string form against ConverterParameter — used to drive
/// "is this the active tab/category" Classes/IsVisible bindings for enums.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null &&
           string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
