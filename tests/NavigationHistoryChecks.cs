using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;

namespace winuitest;

public sealed partial class MainWindow
{
    private async Task RunNavigationHistoryChecksAsync()
    {
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Navigation history: " + detail);
        }
        async Task Until(string phase, Func<bool> ready)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready())
            {
                if (_closed || elapsed.ElapsedMilliseconds > 3000) throw new TimeoutException(
                    $"History {phase}, page={_page}, index={_browseIndex}, saved offset={_browseVisits[_browseIndex].Offset}, actual offset={_trackListScroller?.VerticalOffset}, extent={_trackListScroller?.ScrollableHeight}");
                await Task.Delay(20);
            }
        }
        void Invoke(Button button)
        {
            Require(button.IsEnabled, "expected enabled " + button.Name);
            var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("Missing invoke pattern: " + button.Name);
            provider.Invoke();
        }
        void CheckClientHit(Button button)
        {
            var center = button.TransformToVisual(Root).TransformPoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var scale = Root.XamlRoot.RasterizationScale;
            var native = new HistoryHitPoint { X = (int)Math.Round(center.X * scale), Y = (int)Math.Round(center.Y * scale) };
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Require(HistoryClientToScreen(window, ref native), "client coordinates for " + button.Name);
            var point = (nint)((native.X & 0xffff) | (native.Y << 16));
            Require(HistorySendMessage(window, 0x0084, 0, point) == 1, button.Name + " center must be HTCLIENT");
        }

        Require(_page == "discover" && !HistoryBackButton.IsEnabled && !HistoryForwardButton.IsEnabled, "initial navigation state");
        await SearchAsync("history alpha");
        var alphaIndex = _browseIndex;
        await SearchAsync("history alpha");
        Require(_browseIndex == alphaIndex && !HistoryForwardButton.IsEnabled, "refresh does not duplicate a visit");
        await SearchAsync("history beta");
        Root.UpdateLayout();
        CheckClientHit(HistoryBackButton);
        CheckClientHit(HistoryForwardButton);
        Invoke(HistoryBackButton);
        await Until("back to alpha", () => _query == "history alpha" && _restoringVisit is null);
        Require(SearchBox.Text == "history alpha" && HistoryForwardButton.IsEnabled && MainNavigation.SelectedItem is null, "back restores query");
        Invoke(HistoryForwardButton);
        await Until("forward to beta", () => _query == "history beta" && _restoringVisit is null);
        Require(!HistoryForwardButton.IsEnabled, "forward reaches the newest entry");
        Require(SearchBox.Focus(FocusState.Programmatic), "search box focus");
        Require(!CanRunShortcut(_shortcutBindings[(VirtualKey.Left, VirtualKeyModifiers.Menu)]), "focused text keeps editing keys");

        await OpenPlaylistAsync(888);
        await Until("initial playlist layout", () => _tracks.Count == 240 && !_pageLoading && TracksView.Visibility == Visibility.Visible && _trackListScroller is { ScrollableHeight: > 2000 });
        await Task.Delay(160);
        _trackListScroller!.ChangeView(null, 1260, null, true);
        await Until("initial scroll to 1260", () => _trackListScroller!.VerticalOffset >= 1259);
        var playlistOffset = _trackListScroller!.VerticalOffset;
        await OpenArtistAsync(7001, "Luma Artist");
        Require(Math.Abs(_browseVisits[_browseIndex - 1].Offset - playlistOffset) <= 0.75,
            $"leaving playlist saved {_browseVisits[_browseIndex - 1].Offset}, expected {playlistOffset}");
        await SelectArtistAlbumsAsync();
        await OpenAlbumAsync(8001);
        await MoveInHistoryAsync(-1);
        Require(_page == "artist" && _artistId == 7001 && _artistAlbumsVisible, "artist album tab restored");
        await MoveInHistoryAsync(-1);
        Require(_page == "artist" && !_artistAlbumsVisible, "artist songs tab restored");
        await MoveInHistoryAsync(-1);
        Require(_page == "playlist" && _playlistId == 888, "playlist ID restored");
        await Until($"return to playlist offset {playlistOffset}", () => Math.Abs(_trackListScroller!.VerticalOffset - playlistOffset) <= 2);
        await Task.Delay(110);
        Require(Math.Abs(_trackListScroller!.VerticalOffset - playlistOffset) <= 2,
            $"playlist offset after layout settled {_trackListScroller.VerticalOffset}, expected {playlistOffset}");
        await MoveInHistoryAsync(1);
        await MoveInHistoryAsync(1);
        await MoveInHistoryAsync(1);
        Require(_page == "album" && _artistAlbumId == 8001 && _albumOriginArtistId == 7001 && AlbumArtistBackButton.Visibility == Visibility.Visible,
            "album ID and artist return destination restored");
        await MoveInHistoryAsync(-1);
        OpenLibrary("favorites");
        Require(!HistoryForwardButton.IsEnabled && ReferenceEquals(MainNavigation.SelectedItem, FavoritesNav), "new navigation clears forward history");

        BeginPage("settings");
        await OpenCloudAsync();
        await MoveInHistoryAsync(-1);
        Require(_page == "settings", "settings restored");
        await MoveInHistoryAsync(1);
        Require(_page == "cloud", "cloud library restored");
        await SearchAsync("history slow");
        await SearchAsync("history fast");
        var obsolete = MoveInHistoryAsync(-1);
        Require(_query == "history slow" && _pageLoading, "old history request pending");
        await MoveInHistoryAsync(1);
        await obsolete;
        Require(_page == "search" && _query == "history fast" && SearchBox.Text == _query && !HistoryForwardButton.IsEnabled,
            "obsolete response cannot replace the latest history destination");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HistoryHitPoint { public int X; public int Y; }

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HistoryClientToScreen(nint window, ref HistoryHitPoint point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint HistorySendMessage(nint window, uint message, nint wParam, nint lParam);
}
