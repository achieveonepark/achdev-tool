using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AchDevTool.Converters;

/// <summary>True when the bound collection has no items — drives "empty state" placeholders.</summary>
public sealed class CollectionEmptyConverter : IValueConverter
{
    public static readonly CollectionEmptyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int count => count == 0,
            ICollection c => c.Count == 0,
            IEnumerable e => !e.Cast<object?>().Any(),
            _ => true,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
