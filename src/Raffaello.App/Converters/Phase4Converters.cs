using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Raffaello.App.Converters;

/// <summary>[phase4] File path -> image loaded into memory (the PNG is not locked, so a later lookup can overwrite it).</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public static readonly PathToImageConverter Instance = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string p || p.Length == 0 || !File.Exists(p)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(p, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
