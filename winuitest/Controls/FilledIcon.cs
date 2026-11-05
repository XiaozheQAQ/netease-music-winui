using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace winuitest.Controls;

/// <summary>Consistent, locally bundled Fluent filled icons with native WinUI rendering.</summary>
public sealed class FilledIcon : FontIcon
{
    public FilledIcon()
    {
        FontFamily = FilledIcons.FontFamily;
        FontSize = 20;
        Glyph = FilledIcons.Music;
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(FilledIcon),
        new PropertyMetadata("Music", OnKindChanged));

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((FilledIcon)sender).Glyph = FilledIcons.Get(args.NewValue as string);
}

/// <summary>
/// Glyph mapping from Microsoft Fluent System Icons, filled 20px optical size.
/// See Assets/Fonts/PROVENANCE.txt for the pinned upstream revision and license.
/// </summary>
public static class FilledIcons
{
    public static FontFamily FontFamily { get; } = new(
        "ms-appx:///Assets/Fonts/FluentSystemIcons-Filled.ttf#FluentSystemIcons-Filled");

    public const string Play = "\uF60F";
    public const string Pause = "\uF5AB";
    public const string Previous = "\uF632";
    public const string Next = "\uF573";
    public const string Rewind = "\uF67E";
    public const string FastForward = "\uF3FD";
    public const string Shuffle = "\uEF36";
    public const string Repeat = "\uF171";
    public const string RepeatOne = "\uEF33";
    public const string Heart = "\uF47D";
    // Selection uses opacity and accent color rather than changing the icon's weight.
    public const string HeartOff = Heart;
    public const string Home = "\uF487";
    public const string Search = "\uF699";
    public const string Clock = "\uF2DD";
    public const string Playlist = "\uE770";
    public const string Music = "\uE862";
    public const string Settings = "\uF6B2";
    public const string Person = "\uF5C7";
    public const string ChevronRight = "\uF2B0";
    public const string ChevronDown = "\uF2A3";
    public const string FullScreen = "\uE691";
    public const string ExitFullScreen = "\uE694";
    public const string More = "\uE832";
    public const string Close = "\uF369";
    public const string Refresh = "\uF13D";
    public const string Volume = "\uEB4B";
    public const string VolumeMute = "\uEB53";
    public const string Lyrics = "\uF819";
    public const string Queue = "\uECD7";
    public const string Add = "\uF109";
    public const string Palette = "\uF2F5";
    public const string Loading = "\uF712";
    public const string Keyboard = "\uF4C2";
    public const string Album = "\uE00F";
    public const string Copy = "\uF32B";

    public static string Get(string? kind) => kind switch
    {
        nameof(Play) => Play, nameof(Pause) => Pause,
        nameof(Previous) => Previous, nameof(Next) => Next,
        nameof(Rewind) => Rewind, nameof(FastForward) => FastForward,
        nameof(Shuffle) => Shuffle, nameof(Repeat) => Repeat, nameof(RepeatOne) => RepeatOne,
        nameof(Heart) => Heart, nameof(HeartOff) => HeartOff,
        nameof(Home) => Home, nameof(Search) => Search, nameof(Clock) => Clock,
        nameof(Playlist) => Playlist, nameof(Music) => Music, nameof(Settings) => Settings,
        nameof(Person) => Person, nameof(ChevronRight) => ChevronRight, nameof(ChevronDown) => ChevronDown,
        nameof(FullScreen) => FullScreen, nameof(ExitFullScreen) => ExitFullScreen,
        nameof(More) => More, nameof(Close) => Close, nameof(Refresh) => Refresh,
        nameof(Volume) => Volume, nameof(VolumeMute) => VolumeMute,
        nameof(Lyrics) => Lyrics, nameof(Queue) => Queue, nameof(Add) => Add,
        nameof(Palette) => Palette, nameof(Loading) => Loading,
        nameof(Keyboard) => Keyboard, nameof(Album) => Album, nameof(Copy) => Copy,
        _ => Music
    };
}
