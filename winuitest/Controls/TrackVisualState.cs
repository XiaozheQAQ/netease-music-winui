using Microsoft.UI.Xaml;

namespace winuitest.Controls;

public static class TrackVisualState
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility BadgeVisibility(string? label) => string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
}
