using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<MusicAlbum> _artistAlbums = new();
    private ScrollViewer? _artistSongScroller;
    private ScrollViewer? _artistAlbumScroller;
    private long _artistId;
    private long _artistAlbumId;
    private long _albumOriginArtistId;
    private string _albumOriginArtistName = "";
    private string _artistName = "";
    private int _artistSongsOffset;
    private int _artistAlbumsOffset;
    private int _artistNavigationGeneration;
    private CancellationTokenSource? _trackNavigationRequest;
    private bool _artistUiReady;
    private bool _artistAlbumsVisible;
    private bool _artistAlbumsLoaded;
    private bool _artistSongsMore;
    private bool _artistAlbumsMore;
    private bool _artistPaging;
    private bool _artistPagingFailed;

    private void InitializeArtistUi()
    {
        if (_artistUiReady) return;
        _artistUiReady = true;
        ArtistSongs.ItemsSource = _tracks;
        ArtistAlbumGrid.ItemsSource = _artistAlbums;
        ArtistSongs.ContainerContentChanging += TrackContainer_Changing;
        ArtistView.LayoutUpdated += ArtistItems_LayoutUpdated;
        Closed += (_, _) =>
        {
            _trackNavigationRequest?.Cancel();
            _trackNavigationRequest?.Dispose();
            ArtistView.LayoutUpdated -= ArtistItems_LayoutUpdated;
            if (_artistSongScroller is not null) _artistSongScroller.ViewChanged -= ArtistScroller_ViewChanged;
            if (_artistAlbumScroller is not null) _artistAlbumScroller.ViewChanged -= ArtistScroller_ViewChanged;
        };
        RefreshArtistLinks();
    }

    private void RefreshArtistLinks()
    {
        if (_player is null) return;
        PlayerArtistButton.IsEnabled = _player.CurrentTrack is { Id: > 0 };
        PlayerArtistButton.IsHitTestVisible = PlayerArtistButton.IsEnabled;
        if (ArtistSongs is not null && ArtistSongs.Visibility == Visibility.Visible)
            UpdatePlayingTrackIndicators(ArtistSongs);
    }

    private Task OpenCurrentTrackArtistAsync(FrameworkElement anchor) => _player.CurrentTrack is Track track
        ? OpenTrackArtistAsync(track, anchor, requireCurrentTrack: true) : Task.CompletedTask;

    private async void TrackArtist_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Track track } anchor) await OpenTrackArtistAsync(track, anchor);
    }

    private async void TrackAlbum_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: Track track }) await OpenTrackAlbumAsync(track);
    }

    private CancellationToken BeginTrackNavigation()
    {
        _trackNavigationRequest?.Cancel();
        _trackNavigationRequest?.Dispose();
        _trackNavigationRequest = CancellationTokenSource.CreateLinkedTokenSource(
            _accountLifetime.Token, _pageRequest?.Token ?? CancellationToken.None);
        _artistNavigationGeneration++;
        return _trackNavigationRequest.Token;
    }

    private static List<ArtistReference> GetValidTrackArtists(Track track) =>
        (track.Artists ?? new List<ArtistReference>())
            .Where(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
            .DistinctBy(artist => artist.Id).ToList();

    private async Task OpenTrackArtistAsync(Track track, FrameworkElement anchor, bool requireCurrentTrack = false)
    {
        if (_closed || track.Id <= 0) return;
        var token = BeginTrackNavigation();
        var generation = _artistNavigationGeneration;
        var sourcePage = _pageRequest;
        bool IsCurrent() => !_closed && !token.IsCancellationRequested && generation == _artistNavigationGeneration
            && ReferenceEquals(sourcePage, _pageRequest) && (!requireCurrentTrack || _player.CurrentTrack?.Id == track.Id);
        try
        {
            var artists = GetValidTrackArtists(track);
            if (artists.Count == 0)
            {
                artists = await _client.GetTrackArtistsAsync(track.Id, token);
                if (!IsCurrent()) return;
                artists = artists.Where(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
                    .DistinctBy(artist => artist.Id).ToList();
                track.Artists = artists;
            }
            if (!IsCurrent()) return;
            if (artists.Count == 0) { ShowToast("暂无歌手资料"); return; }
            if (artists.Count == 1)
            {
                CloseNowPlaying();
                await OpenArtistAsync(artists[0].Id, artists[0].Name);
                return;
            }
            if (!anchor.IsLoaded || anchor.XamlRoot is null) return;
            var menu = new MenuFlyout();
            foreach (var artist in artists)
            {
                var item = new MenuFlyoutItem { Text = artist.Name };
                item.Click += async (_, _) =>
                {
                    if (!IsCurrent()) return;
                    CloseNowPlaying();
                    await OpenArtistAsync(artist.Id, artist.Name);
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(anchor);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrent()) ShowError("无法打开歌手", error.Message);
        }
    }

    private async Task OpenTrackAlbumAsync(Track track)
    {
        if (_closed || track.Id <= 0) return;
        var token = BeginTrackNavigation();
        var generation = _artistNavigationGeneration;
        var sourcePage = _pageRequest;
        bool IsCurrent() => !_closed && !token.IsCancellationRequested && generation == _artistNavigationGeneration
            && ReferenceEquals(sourcePage, _pageRequest);
        try
        {
            if (track.AlbumId <= 0)
            {
                var metadata = await _client.GetTrackAsync(track.Id, token);
                if (!IsCurrent()) return;
                if (metadata.Id != track.Id) throw new InvalidOperationException("歌曲资料不匹配。");
                track.AlbumId = metadata.AlbumId;
                if (GetValidTrackArtists(track).Count == 0) track.Artists = GetValidTrackArtists(metadata);
                if (string.IsNullOrWhiteSpace(track.Album)) track.Album = metadata.Album;
            }
            if (!IsCurrent()) return;
            if (track.AlbumId <= 0) { ShowToast("暂无专辑资料"); return; }
            // The album may have been opened from an unrelated search/playlist.
            // Never reuse the last visited artist as its return destination.
            var artist = GetValidTrackArtists(track).FirstOrDefault();
            CloseNowPlaying();
            await OpenAlbumAsync(track.AlbumId, artist);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrent()) ShowError("无法打开专辑", error.Message); }
    }

    private bool IsCurrentArtist(CancellationToken token, long id) => !_closed && !token.IsCancellationRequested
        && _page == "artist" && _artistId == id && _pageRequest?.Token == token;

    private async Task OpenArtistAsync(long id, string name, bool albums = false)
    {
        if (_closed || id <= 0) return;
        InitializeArtistUi();
        var token = BeginPage("artist");
        _artistId = id;
        _artistName = name;
        _artistSongsOffset = _artistAlbumsOffset = 0;
        _artistSongsMore = _artistAlbumsMore = _artistAlbumsLoaded = _artistPaging = _artistPagingFailed = false;
        SetArtistAppendLoading(false);
        _artistAlbumsVisible = false;
        _tracks.Clear();
        _artistAlbums.Clear();
        ArtistSongs.SelectedItems.Clear();
        ArtistPageTitle.Text = name;
        ArtistPageSubtitle.Text = "";
        ArtistPageArtwork.Source = null;
        SetArtistTab(false);
        _artistSongScroller?.ChangeView(null, 0, null, true);
        _artistAlbumScroller?.ChangeView(null, 0, null, true);
        SetPageLoading(true);
        try
        {
            var profileTask = _client.GetArtistAsync(id, token);
            var songsTask = _client.GetArtistSongsAsync(id, 0, 50, token);
            await Task.WhenAll(profileTask, songsTask);
            if (!IsCurrentArtist(token, id)) return;
            var profile = await profileTask;
            var songs = await songsTask;
            _artistName = string.IsNullOrWhiteSpace(profile.Name) ? name : profile.Name;
            ArtistPageTitle.Text = _artistName;
            SetImage(ArtistPageArtwork, profile.ArtworkUrl);
            ToolTipService.SetToolTip(ArtistPageTitle, string.IsNullOrWhiteSpace(profile.Description) ? _artistName : profile.Description);
            AddArtistSongs(songs);
            UpdateArtistEmptyState();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentArtist(token, id))
            {
                _artistPagingFailed = true;
                ShowError("歌手作品加载失败", error.Message);
            }
        }
        finally
        {
            if (IsCurrentArtist(token, id))
            {
                SetPageLoading(false);
                UpdateArtistEmptyState();
            }
        }
        if (albums && IsCurrentArtist(token, id)) await SelectArtistAlbumsAsync();
    }

    private Task RefreshArtistAsync() => OpenArtistAsync(_artistId, _artistName, _artistAlbumsVisible);

    private void AddArtistSongs(ArtistSongPage result)
    {
        _artistSongsOffset += result.Tracks.Count;
        var existing = _tracks.Select(track => track.Id).ToHashSet();
        var added = 0;
        foreach (var track in result.Tracks)
            if (existing.Add(track.Id)) { _tracks.Add(track); added++; }
        _artistSongsMore = added > 0 && result.More;
        ArtistPageSubtitle.Text = result.Total > 0 ? $"{result.Total:N0} 首单曲" : $"{_tracks.Count:N0} 首单曲";
        RefreshArtistLinks();
    }

    private void SetArtistTab(bool albums)
    {
        _artistAlbumsVisible = albums;
        ArtistSongsTab.IsChecked = !albums;
        ArtistAlbumsTab.IsChecked = albums;
        ArtistSongs.Visibility = albums ? Visibility.Collapsed : Visibility.Visible;
        ArtistAlbumGrid.Visibility = albums ? Visibility.Visible : Visibility.Collapsed;
        UpdateArtistEmptyState();
        if (albums) ResizeArtistCards();
    }

    private void ArtistSongsTab_Click(object sender, RoutedEventArgs args)
    {
        SetArtistTab(false);
        TryAppendArtistItems();
    }

    private async void ArtistAlbumsTab_Click(object sender, RoutedEventArgs args) => await SelectArtistAlbumsAsync();

    private async Task SelectArtistAlbumsAsync()
    {
        if (_page != "artist" || _closed) return;
        SetArtistTab(true);
        if (!_artistAlbumsLoaded && !_artistPaging) await LoadArtistAlbumsAsync();
    }

    private async Task LoadArtistAlbumsAsync()
    {
        if (_closed || _page != "artist" || _pageRequest is null || _artistPaging) return;
        var token = _pageRequest.Token;
        var id = _artistId;
        _artistPaging = true;
        SetArtistAppendLoading(true);
        try
        {
            var result = await _client.GetArtistAlbumsAsync(id, _artistAlbumsOffset, 30, token);
            if (!IsCurrentArtist(token, id)) return;
            _artistAlbumsLoaded = true;
            _artistAlbumsOffset += result.Albums.Count;
            var existing = _artistAlbums.Select(album => album.Id).ToHashSet();
            var added = 0;
            foreach (var album in result.Albums)
                if (existing.Add(album.Id)) { _artistAlbums.Add(album); added++; }
            _artistAlbumsMore = added > 0 && result.More;
            ResizeArtistCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentArtist(token, id))
            {
                _artistPagingFailed = true;
                ShowError("专辑加载失败", error.Message + " 点击右上角刷新重试。");
            }
        }
        finally
        {
            if (IsCurrentArtist(token, id))
            {
                _artistPaging = false;
                SetArtistAppendLoading(false);
                UpdateArtistEmptyState();
            }
        }
    }

    private async Task LoadMoreArtistSongsAsync()
    {
        if (_closed || _page != "artist" || _pageRequest is null || _artistPaging) return;
        var token = _pageRequest.Token;
        var id = _artistId;
        _artistPaging = true;
        SetArtistAppendLoading(true);
        try
        {
            var result = await _client.GetArtistSongsAsync(id, _artistSongsOffset, 50, token);
            if (IsCurrentArtist(token, id)) AddArtistSongs(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsCurrentArtist(token, id))
            {
                _artistPagingFailed = true;
                ShowError("更多单曲加载失败", error.Message + " 点击右上角刷新重试。");
            }
        }
        finally
        {
            if (IsCurrentArtist(token, id))
            {
                _artistPaging = false;
                SetArtistAppendLoading(false);
                UpdateArtistEmptyState();
            }
        }
    }

    private void SetArtistAppendLoading(bool loading)
    {
        ArtistAppendProgress.IsIndeterminate = loading;
        ArtistAppendProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading) ArtistEmptyText.Visibility = Visibility.Collapsed;
    }

    private void UpdateArtistEmptyState()
    {
        if (ArtistEmptyText is null) return;
        var count = _artistAlbumsVisible ? _artistAlbums.Count : _tracks.Count;
        ArtistEmptyText.Text = _artistPagingFailed ? "暂时无法载入作品" : _artistAlbumsVisible ? "暂无专辑" : "暂无单曲";
        ArtistEmptyText.Visibility = !_pageLoading && !_artistPaging && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ArtistView_Loaded(object sender, RoutedEventArgs args) { InitializeArtistUi(); AttachArtistScrollers(); }
    private void ArtistView_SizeChanged(object sender, SizeChangedEventArgs args) { ResizeArtistCards(); AttachArtistScrollers(); }
    private void ArtistItems_Loaded(object sender, RoutedEventArgs args) { AttachArtistScrollers(); ResizeArtistCards(); }
    private void ArtistItems_SizeChanged(object sender, SizeChangedEventArgs args) { AttachArtistScrollers(); ResizeArtistCards(); TryAppendArtistItems(); }
    private void ArtistItems_LayoutUpdated(object? sender, object args)
    {
        if (_page != "artist" || ArtistView.Visibility != Visibility.Visible) return;
        AttachArtistScrollers();
        TryAppendArtistItems();
    }
    private void AttachArtistScrollers()
    {
        if (_artistSongScroller is null && FindTrackListScroller(ArtistSongs) is { } songs)
        {
            _artistSongScroller = songs;
            songs.ViewChanged += ArtistScroller_ViewChanged;
        }
        if (_artistAlbumScroller is null && FindTrackListScroller(ArtistAlbumGrid) is { } albums)
        {
            _artistAlbumScroller = albums;
            albums.ViewChanged += ArtistScroller_ViewChanged;
        }
    }
    private void ArtistScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => TryAppendArtistItems();
    private void ResizeArtistCards() { if (ArtistAlbumGrid is not null) ResizePlaylistCards(ArtistAlbumGrid); }
    private void TryAppendArtistItems()
    {
        if (_closed || _page != "artist" || _pageLoading || _artistPaging || _artistPagingFailed || _inspectorOpen
            || ArtistView.Visibility != Visibility.Visible) return;
        if (_artistAlbumsVisible && !_artistAlbumsLoaded) { _ = LoadArtistAlbumsAsync(); return; }
        var scroller = _artistAlbumsVisible ? _artistAlbumScroller : _artistSongScroller;
        var more = _artistAlbumsVisible ? _artistAlbumsMore : _artistSongsMore;
        if (!more || scroller is null || scroller.ViewportHeight <= 1) return;
        if (scroller.ScrollableHeight - scroller.VerticalOffset > Math.Clamp(scroller.ViewportHeight * .45, 96, 240)) return;
        if (_artistAlbumsVisible) _ = LoadArtistAlbumsAsync();
        else _ = LoadMoreArtistSongsAsync();
    }

    private async void ArtistAlbumGrid_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is MusicAlbum album) await OpenAlbumAsync(album.Id);
    }

    private async void ArtistBack_Click(object sender, RoutedEventArgs args)
    {
        if (_albumOriginArtistId > 0) await OpenArtistAsync(_albumOriginArtistId, _albumOriginArtistName, albums: true);
    }

    private Task OpenAlbumAsync(long id)
    {
        var artist = _page == "artist" && _artistId > 0
            ? new ArtistReference { Id = _artistId, Name = _artistName }
            : _page == "album" && id == _artistAlbumId && _albumOriginArtistId > 0
                ? new ArtistReference { Id = _albumOriginArtistId, Name = _albumOriginArtistName } : null;
        return OpenAlbumAsync(id, artist);
    }

    private async Task OpenAlbumAsync(long id, ArtistReference? originArtist)
    {
        if (_closed || id <= 0) return;
        var token = BeginPage("album");
        _artistAlbumId = id;
        _albumOriginArtistId = originArtist?.Id ?? 0;
        _albumOriginArtistName = originArtist?.Name ?? "";
        AlbumArtistBackButton.Visibility = _albumOriginArtistId > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTracks("专辑", "", Array.Empty<Track>());
        SetPageLoading(true);
        try
        {
            var album = await _client.GetAlbumAsync(id, token);
            token.ThrowIfCancellationRequested();
            if (_closed || _page != "album" || _pageRequest?.Token != token) return;
            ShowTracks(album.Title, $"{album.Artist} · {album.Tracks.Count} 首歌曲", album.Tracks);
            SetImage(CollectionCover, album.ArtworkUrl);
            CollectionCoverBorder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested && !_closed) ShowError("专辑加载失败", error.Message); }
        finally { if (!token.IsCancellationRequested && !_closed && _pageRequest?.Token == token) SetPageLoading(false); }
    }
}
