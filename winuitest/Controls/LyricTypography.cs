using Microsoft.UI.Xaml.Media;

namespace winuitest.Controls;

/// <summary>
/// Unmodified, locally bundled rounded CJK faces. Explicit files and weights keep
/// timed glyph masks, ordinary lyrics and subtitles on the same real font family.
/// See Assets/Fonts/PROVENANCE-ResourceHanRounded.txt for coverage and licensing.
/// </summary>
public static class LyricTypography
{
    public static FontFamily PrimaryFamily { get; } = new(
        "ms-appx:///Assets/Fonts/ResourceHanRoundedCN-Heavy.ttf#Resource Han Rounded CN");

    public static FontFamily SecondaryFamily { get; } = new(
        "ms-appx:///Assets/Fonts/ResourceHanRoundedCN-Bold.ttf#Resource Han Rounded CN");
}
