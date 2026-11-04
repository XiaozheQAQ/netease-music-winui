using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using winuitest.Models;

namespace winuitest;

// Linked only into native-check builds. Uses the existing paused fixture track;
// no media requests, clipboard writes or synthetic input reach other processes.
public sealed partial class MainWindow
{
    private async Task RunLyricPresentationChecksAsync()
    {
        RequireLyricPresentation(_player.CurrentTrack is not null && !_player.IsPlaying, "paused fixture track");
        var wasOpen = _immersiveOpen;
        var wasRendering = _lyricsRendering;
        OpenNowPlaying();
        SetLyricsRendering(false);
        try
        {
            NowPlaying.UpdateLayout();
            await Task.Delay(60);
            RequireLyricPresentation(NowPlaying.IsLoaded && NowPlaying.Visibility == Visibility.Visible, "visible lyric view");

            NowPlaying.SetLyrics(new[] { new LyricLine { TimeMs = 0, Text = "纯音乐，请欣赏" } });
            NowPlaying.UpdateLayout();
            var pane = FindLyricPresentationElement<Grid>(NowPlaying, "LyricsPane");
            var empty = FindLyricPresentationElement<StackPanel>(NowPlaying, "EmptyLyricsPanel");
            var message = FindLyricPresentationElement<TextBlock>(NowPlaying, "EmptyLyricsText");
            var stack = FindLyricPresentationElement<StackPanel>(NowPlaying, "LyricsStack");
            RequireLyricPresentation(empty.Visibility == Visibility.Visible && empty.VerticalAlignment == VerticalAlignment.Center,
                "instrumental panel visibility and alignment");
            RequireLyricPresentation(message.Text == "纯音乐，请欣赏" && stack.Children.Count == 0, "instrumental display");
            var center = empty.TransformToVisual(pane).TransformPoint(new Point(0, empty.ActualHeight / 2)).Y;
            RequireLyricPresentation(pane.ActualHeight > 0 && empty.ActualHeight > 0
                && Math.Abs(center - pane.ActualHeight / 2) <= 1.5, "instrumental actual vertical center");

            NowPlaying.SetLyrics(new[]
            {
                new LyricLine { TimeMs = 4000, DurationMs = 1000, Text = "First phrase" },
                new LyricLine { TimeMs = 10000, DurationMs = 1000, Text = "Next phrase" }
            });
            NowPlaying.UpdateLayout();
            RequireLyricPresentation(empty.Visibility == Visibility.Collapsed && stack.Children.OfType<Button>().Count() == 2,
                "normal lyrics return after instrumental");
            var intro = FindLyricPresentationElement<Border>(stack, "LyricGapRow0");
            var interlude = FindLyricPresentationElement<Border>(stack, "LyricGapRow1");
            var dots = LyricPresentationDots(intro);
            var stableHeight = stack.ActualHeight;
            var correction = _library.GetLyricOffsetMs(_player.CurrentTrack!.Id);
            void At(double milliseconds) => NowPlaying.UpdateLyricsPosition(TimeSpan.FromMilliseconds(milliseconds + correction));

            At(2000);
            RequireLyricPresentation(intro.Opacity > .99 && interlude.Opacity == 0
                && dots[0].Opacity > dots[1].Opacity && dots[1].Opacity > dots[2].Opacity,
                "intro dots brighten in source-time order");
            RequireLyricPresentation(!intro.IsHitTestVisible && !interlude.IsHitTestVisible, "gap rows do not intercept lyric input");
            var paused = dots.Select(dot => dot.Opacity).ToArray();
            var pausedRow = intro.Opacity;
            await Task.Delay(90);
            RequireLyricPresentation(intro.Opacity == pausedRow && dots.Select(dot => dot.Opacity).SequenceEqual(paused),
                "paused gap does not advance from wall time");
            At(2000);
            RequireLyricPresentation(dots.Select(dot => dot.Opacity).SequenceEqual(paused), "same lyric clock is deterministic");
            At(3600);
            var advanced = dots.Select(dot => dot.Opacity).ToArray();
            At(1000);
            RequireLyricPresentation(dots[0].Opacity < advanced[0] && dots[1].Opacity < advanced[1]
                && dots[2].Opacity < advanced[2], "backward seek rewinds each dot");
            At(4200);
            RequireLyricPresentation(intro.Opacity == 0 && interlude.Opacity == 0, "next line hides intro dots");
            At(7500);
            var middleDots = LyricPresentationDots(interlude);
            RequireLyricPresentation(interlude.Opacity > .99 && middleDots[0].Opacity > middleDots[1].Opacity
                && middleDots[1].Opacity > middleDots[2].Opacity, "explicit interlude uses its own source interval");
            At(10000);
            NowPlaying.UpdateLayout();
            RequireLyricPresentation(interlude.Opacity == 0 && Math.Abs(stack.ActualHeight - stableHeight) <= .5
                && intro.ActualHeight == 48 && interlude.ActualHeight == 48, "next line hides dots without changing layout");

            NowPlaying.SetLyrics(new[]
            {
                new LyricLine { TimeMs = 0, Text = "Untimed ending" },
                new LyricLine { TimeMs = 10000, Text = "Next start only" }
            });
            RequireLyricPresentation(!stack.Children.OfType<Border>().Any(row => row.Name.StartsWith("LyricGapRow", StringComparison.Ordinal)),
                "plain LRC does not invent an interlude or retain old gap rows");

            var repeat = FindLyricPresentationElement<Button>(NowPlaying, "RepeatButton");
            foreach (var mode in new[] { (false, RepeatMode.Off), (false, RepeatMode.All), (false, RepeatMode.One), (true, RepeatMode.All) })
            {
                NowPlaying.UpdatePlaybackModes(mode.Item1, mode.Item2);
                RequireLyricPresentation(repeat.Background is SolidColorBrush { Color.A: 0 }, "transparent repeat background: " + mode);
            }

            var artist = FindLyricPresentationElement<Button>(NowPlaying, "TrackArtistButton");
            artist.ApplyTemplate();
            RequireLyricPresentation(artist.IsEnabled && VisualStateManager.GoToState(artist, "PointerOver", false), "artist native hover state");
            await Task.Delay(100);
            var presenter = FindLyricPresentationElement<ContentPresenter>(artist, "ContentPresenter");
            RequireLyricPresentation(artist.Background is SolidColorBrush { Color.A: 0 }
                && presenter.Background is SolidColorBrush { Color.A: 0 }, "artist hover has no background block");
            VisualStateManager.GoToState(artist, "Normal", false);
            RequireLyricPresentation(!_player.IsPlaying, "presentation checks leave playback paused");
        }
        finally
        {
            NowPlaying.SetLyrics(_lyrics);
            NowPlaying.UpdatePlaybackModes(_player.Shuffle, _player.Repeat);
            UpdateNowPlaying();
            NowPlaying.UpdateLyricsPosition(_player.GetLyricPosition());
            if (!wasOpen) CloseNowPlaying();
            SetLyricsRendering(wasRendering);
        }
    }

    private static Ellipse[] LyricPresentationDots(Border row)
    {
        var dots = (row.Child as StackPanel)?.Children.OfType<Ellipse>().ToArray() ?? Array.Empty<Ellipse>();
        RequireLyricPresentation(dots.Length == 3, "three dots per gap row");
        return dots;
    }

    private static T FindLyricPresentationElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        T? Find(DependencyObject current)
        {
            if (current is T match && match.Name == name) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                if (Find(VisualTreeHelper.GetChild(current, index)) is { } child) return child;
            return null;
        }
        return Find(root) ?? throw new InvalidOperationException("Missing lyric presentation element: " + name);
    }

    private static void RequireLyricPresentation(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Lyric presentation assertion failed: " + check);
    }
}
