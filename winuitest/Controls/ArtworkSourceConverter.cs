using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;

namespace winuitest.Controls;

/// <summary>Keeps incomplete artwork metadata from failing compiled image bindings.</summary>
public sealed class ArtworkSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            && uri.Scheme != Uri.UriSchemeFile && uri.Scheme != "ms-appx") return null;
        if ((uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.Host)) return null;
        try { return new BitmapImage(uri); }
        catch (Exception error) when (error is ArgumentException or FormatException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
}
