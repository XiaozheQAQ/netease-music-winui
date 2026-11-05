using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private sealed record BrowseRoute(string Page, string Query = "", long Id = 0, string Name = "", bool Albums = false, long OriginId = 0)
    {
        // Display names can change when an artist's full profile arrives.
        public bool Matches(BrowseRoute other) => Page == other.Page && Query == other.Query && Id == other.Id
            && Albums == other.Albums && OriginId == other.OriginId;
    }

    private sealed class BrowseVisit(BrowseRoute route)
    {
        public BrowseRoute Route { get; } = route;
        public double Offset { get; set; }
        public int LastVisibleIndex { get; set; } = -1;
    }

    private const int HistoryLimit = 100;
    private readonly List<BrowseVisit> _browseVisits = new() { new(new BrowseRoute("discover")) };
    private int _browseIndex;
    private long _browseVersion;
    private BrowseVisit? _enteringVisit;
    private BrowseVisit? _restoringVisit;
    private KeyboardAccelerator? _historyBackShortcut;
    private KeyboardAccelerator? _historyForwardShortcut;

    private void InitializeNavigationHistory()
    {
        _historyBackShortcut = RegisterShortcut(VirtualKey.Left, VirtualKeyModifiers.Menu, "Alt + ←", "返回上一页", () => MoveInHistoryAsync(-1));
        _historyForwardShortcut = RegisterShortcut(VirtualKey.Right, VirtualKeyModifiers.Menu, "Alt + →", "前进到下一页", () => MoveInHistoryAsync(1));
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        HistoryBackButton.IsEnabled = _browseIndex > 0;
        HistoryForwardButton.IsEnabled = _browseIndex + 1 < _browseVisits.Count;
        if (_historyBackShortcut is not null) _historyBackShortcut.IsEnabled = HistoryBackButton.IsEnabled;
        if (_historyForwardShortcut is not null) _historyForwardShortcut.IsEnabled = HistoryForwardButton.IsEnabled;
    }

    private ScrollViewer? HistoryScroller(BrowseRoute route) => route.Page switch
    {
        "discover" => HomeView,
        "cloud" => CloudView,
        "settings" => SettingsView,
        "artist" => route.Albums ? _artistAlbumScroller : _artistSongScroller,
        _ => _trackListScroller
    };

    private void SaveVisitPosition()
    {
        var visit = _browseVisits[_browseIndex];
        // Rapid back/forward must not overwrite a saved position with zero
        // while the intermediate destination is still loading or measuring.
        if (ReferenceEquals(visit, _restoringVisit) || _pageLoading || visit.Route.Page != _page) return;
        if (HistoryScroller(visit.Route) is not { IsLoaded: true, ViewportHeight: > 0 } scroller) return;
        visit.Offset = scroller.VerticalOffset;
        visit.LastVisibleIndex = visit.Route.Page switch
        {
            "artist" when visit.Route.Albums => (ArtistAlbumGrid.ItemsPanelRoot as ItemsWrapGrid)?.LastVisibleIndex ?? -1,
            "artist" => (ArtistSongs.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex ?? -1,
            "search" => (TrackList.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex ?? -1,
            _ => -1
        };
    }

    private void RecordBrowseRoute(BrowseRoute route)
    {
        ++_browseVersion;
        if (_enteringVisit is { } entering && entering.Route.Matches(route))
        {
            _enteringVisit = null;
            return;
        }
        SaveVisitPosition();
        _enteringVisit = _restoringVisit = null;
        if (_browseVisits[_browseIndex].Route.Matches(route)) return;
        if (_browseIndex + 1 < _browseVisits.Count)
            _browseVisits.RemoveRange(_browseIndex + 1, _browseVisits.Count - _browseIndex - 1);
        _browseVisits.Add(new BrowseVisit(route));
        if (_browseVisits.Count > HistoryLimit) _browseVisits.RemoveAt(0);
        _browseIndex = _browseVisits.Count - 1;
        UpdateHistoryButtons();
    }

    private void RecordArtistTabRoute(bool albums)
    {
        if (_page != "artist" || _artistId <= 0) return;
        var route = new BrowseRoute("artist", Id: _artistId, Name: _artistName, Albums: albums);
        if (!_browseVisits[_browseIndex].Route.Matches(route)) RecordBrowseRoute(route);
    }

    private async void HistoryBack_Click(object sender, RoutedEventArgs args) => await MoveInHistoryAsync(-1);
    private async void HistoryForward_Click(object sender, RoutedEventArgs args) => await MoveInHistoryAsync(1);

    private async Task MoveInHistoryAsync(int delta)
    {
        var index = _browseIndex + delta;
        if (_closed || index < 0 || index >= _browseVisits.Count) return;
        SaveVisitPosition();
        var visit = _browseVisits[index];
        _browseIndex = index;
        _enteringVisit = _restoringVisit = visit;
        UpdateHistoryButtons();
        CloseNowPlaying();
        // Existing routes synchronously BeginPage and cancel the previous page
        // request before their first await. The cursor is usable during loading.
        var version = _browseVersion;
        try
        {
            var load = ReopenBrowseRouteAsync(visit.Route);
            version = _browseVersion;
            await load;
            if (version == _browseVersion && !_closed) await RestoreVisitPositionAsync(visit, version);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && version == _browseVersion) ShowError("无法恢复页面", error.Message); }
        finally
        {
            if (version == _browseVersion) _enteringVisit = _restoringVisit = null;
        }
    }

    private Task ReopenBrowseRouteAsync(BrowseRoute route)
    {
        switch (route.Page)
        {
            case "discover": return LoadHomeAsync();
            case "search": return SearchAsync(route.Query);
            case "playlist" when route.Id > 0: return OpenPlaylistAsync(route.Id);
            case "artist" when route.Id > 0: return OpenArtistAsync(route.Id, route.Name, route.Albums);
            case "album" when route.Id > 0: return OpenAlbumAsync(route.Id, route.OriginId > 0
                ? new ArtistReference { Id = route.OriginId, Name = route.Name } : null);
            case "cloud": return OpenCloudAsync();
            case "favorites": case "recent": OpenLibrary(route.Page); break;
            case "new": AllNewSongs_Click(HistoryBackButton, new RoutedEventArgs()); break;
            default: BeginPage(route.Page, route: route); break;
        }
        return Task.CompletedTask;
    }

    private async Task RestoreVisitPositionAsync(BrowseVisit visit, long version)
    {
        var route = visit.Route;
        if (_pageRequest is null) return;
        var token = _pageRequest.Token;
        bool Current() => !_closed && !token.IsCancellationRequested && version == _browseVersion;
        // Search and artist results are paginated. Rebuild only as far as the
        // previous viewport needed; history never holds tracks or artwork.
        while (Current() && visit.Offset > 0 && visit.LastVisibleIndex >= 0)
        {
            if ((route.Page == "artist" && _artistPaging) || (route.Page == "search" && _loadingMore))
            {
                await Task.Delay(20, token);
                continue;
            }
            if (route.Page == "search" && _tracks.Count <= visit.LastVisibleIndex && CanAppendSearchResults)
                await SearchAsync(route.Query, append: true);
            else if (route.Page == "artist" && !route.Albums && _tracks.Count <= visit.LastVisibleIndex && _artistSongsMore && !_artistPagingFailed)
                await LoadMoreArtistSongsAsync();
            else if (route.Page == "artist" && route.Albums && _artistAlbums.Count <= visit.LastVisibleIndex && _artistAlbumsMore && !_artistPagingFailed)
                await LoadArtistAlbumsAsync();
            else break;
        }
        // A skeleton can be finishing its exit after the data task returns.
        for (var retry = 0; Current() && retry < 35; retry++)
        {
            Root.UpdateLayout();
            if (!_pageLoading && CurrentPageView.Visibility == Visibility.Visible
                && HistoryScroller(route) is { IsLoaded: true, ViewportHeight: > 0 } scroller)
            {
                if (ReferenceEquals(scroller, _trackListScroller)) _resetCollectionScroll = false;
                await SettleHistoryOffsetAsync(scroller, visit.Offset, Current, token);
                return;
            }
            await Task.Delay(16, token);
        }
    }

    private async Task SettleHistoryOffsetAsync(ScrollViewer scroller, double offset, Func<bool> current,
        System.Threading.CancellationToken token)
    {
        // Realizing a distant row replaces the panel's estimated positions and
        // may move the ScrollViewer anchor after ChangeView initially succeeds.
        // Verify the offset against the settled extent instead of treating the
        // first value as final. Actual pointer input always takes precedence.
        var interrupted = false;
        PointerEventHandler pointer = (_, _) => interrupted = true;
        scroller.AddHandler(UIElement.PointerWheelChangedEvent, pointer, true);
        scroller.AddHandler(UIElement.PointerPressedEvent, pointer, true);
        try
        {
            double previousExtent = -1;
            var settledFrames = 0;
            for (var frame = 0; frame < 45 && current() && !interrupted; frame++)
            {
                scroller.UpdateLayout();
                var extent = scroller.ScrollableHeight;
                var target = Math.Clamp(offset, 0, extent);
                if (Math.Abs(scroller.VerticalOffset - target) > 0.75)
                {
                    scroller.ChangeView(null, target, null, true);
                    settledFrames = 0;
                }
                else if (Math.Abs(extent - previousExtent) > 0.75 ||
                    (ReferenceEquals(scroller, _trackListScroller) && Math.Abs(_collectionHeaderReveal - (_collectionHeaderCompact ? 1 : 0)) > 0.005))
                    settledFrames = 0;
                else if (++settledFrames >= (offset > 0 ? 7 : 3)) return;
                previousExtent = extent;
                await Task.Delay(16, token);
            }
        }
        finally
        {
            scroller.RemoveHandler(UIElement.PointerWheelChangedEvent, pointer);
            scroller.RemoveHandler(UIElement.PointerPressedEvent, pointer);
        }
    }
}
