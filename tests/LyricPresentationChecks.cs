using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using winuitest.Models;
using winuitest.Views.Controls;

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
            RequireLyricPresentation(empty.Visibility == Visibility.Collapsed && stack.Children.Count == 2,
                "normal lyrics return after instrumental");
            var intro = FindLyricPresentationElement<Border>(stack, "LyricGapRow0");
            var interlude = FindLyricPresentationElement<Border>(stack, "LyricGapRow1");
            var firstPhrase = ((StackPanel)stack.Children[0]).Children.OfType<Button>().Single();
            var nextPhrase = ((StackPanel)stack.Children[1]).Children.OfType<Button>().Single();
            var scroller = FindLyricPresentationElement<ScrollViewer>(NowPlaying, "LyricsScroller");
            var dots = LyricPresentationDots(intro);
            var correction = _library.GetLyricOffsetMs(_player.CurrentTrack!.Id);
            void At(double milliseconds) => NowPlaying.UpdateLyricsPosition(TimeSpan.FromMilliseconds(milliseconds + correction));
            static bool Collapsed(Border row) => row.Visibility == Visibility.Collapsed && row.Height == 0;
            double PhraseTop(Button phrase) => phrase.TransformToVisual(stack).TransformPoint(new Point()).Y;

            At(2000);
            RequireLyricPresentation(intro.Opacity > .99 && intro.ActualHeight == 48 && Collapsed(interlude)
                && dots[0].Opacity > dots[1].Opacity && dots[1].Opacity > dots[2].Opacity,
                "intro dots brighten in source-time order");
            RequireLyricPresentation(!intro.IsHitTestVisible && !interlude.IsHitTestVisible, "gap rows do not intercept lyric input");
            var paused = dots.Select(dot => dot.Opacity).ToArray();
            var pausedRow = intro.Opacity;
            await Task.Delay(90);
            RequireLyricPresentation(intro.Opacity == pausedRow && intro.ActualHeight == 48 && dots.Select(dot => dot.Opacity).SequenceEqual(paused),
                "paused gap does not advance from wall time");
            At(2000);
            RequireLyricPresentation(dots.Select(dot => dot.Opacity).SequenceEqual(paused), "same lyric clock is deterministic");
            At(3600);
            var advanced = dots.Select(dot => dot.Opacity).ToArray();
            At(1000);
            RequireLyricPresentation(dots[0].Opacity < advanced[0] && dots[1].Opacity < advanced[1]
                && dots[2].Opacity < advanced[2], "backward seek rewinds each dot");
            At(4200);
            RequireLyricPresentation(Collapsed(intro) && Collapsed(interlude), "finished and future gaps have no layout slot");
            var compactHeight = stack.ActualHeight;
            var compactNextTop = PhraseTop(nextPhrase);
            RequireLyricPresentation(Math.Abs(compactNextTop - PhraseTop(firstPhrase) - firstPhrase.ActualHeight - stack.Spacing) <= 1.5,
                "only normal lyric spacing remains");
            At(7500);
            var middleDots = LyricPresentationDots(interlude);
            RequireLyricPresentation(interlude.Opacity > .99 && middleDots[0].Opacity > middleDots[1].Opacity
                && middleDots[1].Opacity > middleDots[2].Opacity, "explicit interlude uses its own source interval");
            RequireLyricPresentation(Math.Abs(stack.ActualHeight - compactHeight - 48) <= 1.5
                && Math.Abs(PhraseTop(nextPhrase) - compactNextTop - 48) <= 1.5,
                "the active gap occupies exactly its rendered height");
            At(9660);
            At(9840);
            var halfway = PhraseTop(nextPhrase) - compactNextTop;
            At(9960);
            var lastPixels = PhraseTop(nextPhrase) - compactNextTop;
            if (_uiSettings.AnimationsEnabled)
                RequireLyricPresentation(halfway is > 20 and < 28 && lastPixels >= 0 && lastPixels < 5,
                    "the phrase rises progressively as the countdown ends");
            var previousOffset = scroller.VerticalOffset;
            At(10000); // A normal forward frame across the boundary, not a seek.
            RequireLyricPresentation(Collapsed(interlude) && Math.Abs(stack.ActualHeight - compactHeight) <= 1.5
                && Math.Abs(PhraseTop(nextPhrase) - compactNextTop) <= 1.5, "finishing a gap reclaims all space");
            if (_uiSettings.AnimationsEnabled)
                RequireLyricPresentation(Math.Abs(scroller.VerticalOffset - previousOffset) < Math.Max(24, scroller.ViewportHeight * .1),
                    "following the next phrase does not snap the scroll offset");
            var anchor = LyricViewportLayout.CurrentLineAnchor(scroller.ViewportHeight, PhraseTop(nextPhrase) - PhraseTop(firstPhrase),
                nextPhrase.ActualHeight, stack.Margin.Top);
            var expectedOffset = Math.Clamp(PhraseTop(nextPhrase) + stack.Margin.Top - anchor, 0, scroller.ScrollableHeight);
            for (var frame = 0; frame < 50 && Math.Abs(scroller.VerticalOffset - expectedOffset) > 2; frame++)
            {
                await Task.Delay(20);
                At(10000);
            }
            RequireLyricPresentation(Math.Abs(scroller.VerticalOffset - expectedOffset) <= 2, "next phrase settles on its post-collapse anchor");

            At(9920);
            At(7500);
            RequireLyricPresentation(Collapsed(intro) && interlude.ActualHeight == 48, "seeking backwards interrupts collapse");
            At(2000);
            RequireLyricPresentation(intro.ActualHeight == 48 && Collapsed(interlude), "only the latest seek occupies space");
            At(10400);
            RequireLyricPresentation(Collapsed(intro) && Collapsed(interlude) && Math.Abs(stack.ActualHeight - compactHeight) <= 1.5,
                "seeking out of all gaps immediately restores normal layout");
            At(9900);
            NowPlaying.Visibility = Visibility.Collapsed;
            At(7500);
            RequireLyricPresentation(Collapsed(interlude), "hidden view interrupts the countdown");
            NowPlaying.Visibility = Visibility.Visible;
            At(7500);
            RequireLyricPresentation(interlude.ActualHeight == 48, "reopening uses the current clock");

            NowPlaying.SetLyrics(new[]
            {
                new LyricLine { TimeMs = 0, Text = "Untimed ending" },
                new LyricLine { TimeMs = 10000, Text = "Next start only" }
            });
            RequireLyricPresentation(Collapsed(interlude) && stack.Children.Count == 2 && stack.Children.All(child => child is Button),
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
            var options = FindLyricPresentationElement<Button>(NowPlaying, "OptionsButton");
            options.Flyout.ShowAt(options);
            try
            {
                await Task.Delay(180);
                var menu = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                    .Select(popup => popup.Child).OfType<FlyoutPresenter>().FirstOrDefault();
                RequireLyricPresentation(menu is { ActualWidth: > 200, ActualHeight: > 80 and < 250 },
                    "compact lyric menu fits within four rows");
                var copy = FindLyricPresentationElement<Button>(menu!, "CopyTrackLinkButton");
                var volume = FindLyricPresentationElement<Slider>(menu!, "VolumeBar");
                RequireLyricPresentation(copy.ActualHeight >= 28 && volume.ActualWidth >= 75,
                    "compact menu preserves usable copy and volume targets");
            }
            finally { options.Flyout.Hide(); }
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
