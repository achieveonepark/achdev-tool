using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace AchDevTool.Converters;

/// <summary>Converts a "data:image/png;base64,...." string (as produced by the icon
/// extraction services) into an Avalonia Bitmap for an Image control.</summary>
public sealed class Base64ImageConverter : IValueConverter
{
    public static readonly Base64ImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string dataUri || dataUri.Length == 0)
        {
            return null;
        }

        try
        {
            var comma = dataUri.IndexOf(',');
            var base64 = comma >= 0 ? dataUri[(comma + 1)..] : dataUri;
            var bytes = System.Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
