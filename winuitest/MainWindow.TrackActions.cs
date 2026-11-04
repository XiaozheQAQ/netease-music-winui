using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private bool _playlistMutationBusy;
    private bool _trackDialogOpen;

    private static T? FindTrackAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static bool IsTrackButton(DependencyObject? child, DependencyObject row)
    {
        for (var current = child; current is not null && !ReferenceEquals(current, row); current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private async void TrackRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: Track track } row
            || IsTrackButton(args.OriginalSource as DependencyObject, row)) return;
        args.Handled = true;
        await PlayTrackFromListAsync(track, FindTrackAncestor<ListView>(row));
    }

    private async void TrackList_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key != VirtualKey.Enter || sender is not ListView list) return;
        // Enter on a focused row plays it. Native buttons retain their own Enter behavior.
        if (FindTrackAncestor<ButtonBase>(args.OriginalSource as DependencyObject) is not null) return;
        var track = FindTrackAncestor<ListViewItem>(args.OriginalSource as DependencyObject)?.Content as Track
            ?? list.SelectedItems.OfType<Track>().FirstOrDefault();
        if (track is null) return;
        args.Handled = true;
        await PlayTrackFromListAsync(track, list);
    }

    private async Task PlayTrackFromListAsync(Track track, ListView? list)
    {
        if (ReferenceEquals(list, QueueList)) await PlayTracksWithIntentAsync(track, null);
        else await PlayTracksWithIntentAsync(track,
            ReferenceEquals(list, HomeSongs) ? _home?.NewSongs ?? new List<Track>() : _tracks.ToList(), expandInBackground: true);
    }

    private void TrackRow_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || sender is not FrameworkElement { Tag: Track track } row) return;
        args.Handled = true;
        ShowTrackActions(row, FindTrackAncestor<ListView>(row), track,
            args.TryGetPosition(row, out var point) ? point : null);
    }

    private void TrackList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.Handled || sender is not ListView list) return;
        var container = FindTrackAncestor<ListViewItem>(args.OriginalSource as DependencyObject);
        var track = container?.Content as Track ?? list.SelectedItems.OfType<Track>().FirstOrDefault();
        if (track is null) return;
        args.Handled = true;
        FrameworkElement anchor = container ?? (FrameworkElement)list;
        ShowTrackActions(anchor, list, track, args.TryGetPosition(anchor, out var point) ? point : null);
    }

    private void TrackActions_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: Track track } button) return;
        ShowTrackActions(button, FindTrackAncestor<ListView>(button), track);
    }

    private Track[] CaptureTrackSelection(ListView? list, Track invoked)
    {
        if (list is null) return new[] { invoked };
        // Right-clicking a selected item keeps Ctrl/Shift selection intact.
        // Right-clicking another item targets just that item.
        if (!list.SelectedItems.Contains(invoked))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(invoked);
        }
        var selected = list.SelectedItems.OfType<Track>().Select(track => track.Id).ToHashSet();
        return list.Items.OfType<Track>().Where(track => selected.Contains(track.Id)).DistinctBy(track => track.Id).ToArray();
    }

    private void ShowTrackActions(FrameworkElement anchor, ListView? list, Track invoked, Point? point = null)
    {
        var selection = CaptureTrackSelection(list, invoked);
        if (selection.Length == 0) return;
        var menu = new MenuFlyout();
        var play = new MenuFlyoutItem { Text = selection.Length > 1 ? $"播放所选 {selection.Length} 首" : "播放", Icon = new FilledIcon { Kind = "Play" } };
        play.Click += async (_, _) =>
        {
            if (selection.Length == 1) await PlayTrackFromListAsync(invoked, list);
            else
            {
                await PlayTracksWithIntentAsync(selection[0], selection);
            }
        };
        menu.Items.Add(play);
        var enqueue = new MenuFlyoutItem { Text = "加入播放列表", Icon = new FilledIcon { Kind = "Queue" } };
        enqueue.Click += async (_, _) => await RunResolvedTrackActionAsync(selection, ready =>
        {
            _player.AddToQueue(ready);
            ShowToast($"已加入 {ready.Length} 首歌曲");
            return Task.CompletedTask;
        });
        menu.Items.Add(enqueue);
        var copyLink = new MenuFlyoutItem { Text = selection.Length > 1 ? "复制所选播放地址" : "复制播放地址", Icon = new SymbolIcon(Symbol.Copy) };
        copyLink.Click += (_, _) => CopyTrackLinks(selection);
        menu.Items.Add(copyLink);
        menu.Items.Add(new MenuFlyoutSeparator());

        var addToPlaylist = new MenuFlyoutSubItem { Text = "加入歌单", Icon = new FilledIcon { Kind = "Playlist" } };
        var uid = _account?.Id ?? 0;
        var writable = _cloudPlaylists.Where(playlist => playlist.IsOwnedBy(uid)).ToList();
        if (uid <= 0)
        {
            var login = new MenuFlyoutItem { Text = "登录网易云音乐" };
            login.Click += async (_, _) => await ShowLoginAsync();
            addToPlaylist.Items.Add(login);
        }
        else if (writable.Count == 0)
        {
            var refresh = new MenuFlyoutItem { Text = "同步我的歌单" };
            refresh.Click += async (_, _) => await SyncCloudPlaylistsAsync();
            addToPlaylist.Items.Add(refresh);
        }
        else foreach (var playlist in writable)
        {
            var target = new MenuFlyoutItem { Text = playlist.Title, IsEnabled = !_playlistMutationBusy };
            target.Click += async (_, _) => await ChangePlaylistTracksAsync(playlist.Id, playlist.Title, uid, selection, remove: false);
            addToPlaylist.Items.Add(target);
        }
        menu.Items.Add(addToPlaylist);

        if (ReferenceEquals(list, TrackList) && _page == "playlist" && _activePlaylist is { } active && active.IsOwnedBy(uid))
        {
            var remove = new MenuFlyoutItem { Text = "从歌单删除", Icon = new FilledIcon { Kind = "Close" }, IsEnabled = !_playlistMutationBusy };
            remove.Click += async (_, _) => await ChangePlaylistTracksAsync(active.Id, active.Title, uid, selection, remove: true);
            menu.Items.Add(remove);
        }
        if (ReferenceEquals(list, QueueList))
        {
            var remove = new MenuFlyoutItem { Text = "从播放列表移除", Icon = new FilledIcon { Kind = "Close" } };
            remove.Click += (_, _) =>
            {
                var count = selection.Count(track => track.Id != _player.CurrentTrack?.Id);
                _player.RemoveFromQueue(selection);
                ShowToast(count == 0 ? "当前歌曲将在切歌后可移除" : $"已移除 {count} 首歌曲");
            };
            menu.Items.Add(remove);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var allFavorite = selection.All(track => _library.IsFavorite(track.Id));
        var favorite = new MenuFlyoutItem { Text = allFavorite ? "取消喜欢" : "加入喜欢的歌曲", Icon = new FilledIcon { Kind = "Heart" } };
        favorite.Click += async (_, _) => await RunResolvedTrackActionAsync(selection, ready =>
        {
            _library.SetFavorites(ready, !allFavorite);
            UpdateFavorite();
            UpdateTrackHearts(TrackList);
            UpdateTrackHearts(HomeSongs);
            if (_page == "favorites") OpenLibrary("favorites");
            ShowToast(_library.LastSaveError ?? (allFavorite ? "已取消喜欢" : "已加入喜欢的歌曲"));
            return Task.CompletedTask;
        });
        menu.Items.Add(favorite);
        if (point is Point location) menu.ShowAt(anchor, new FlyoutShowOptions { Position = location });
        else menu.ShowAt(anchor);
    }

    private async Task ChangePlaylistTracksAsync(long playlistId, string title, long userId, Track[] selection, bool remove)
    {
        if (_playlistMutationBusy || _closed || _account?.Id != userId) return;
        _playlistMutationBusy = true;
        try
        {
            if (remove && selection.Length > 1)
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                    Title = "从歌单删除", Content = $"从「{title}」中删除选中的 {selection.Length} 首歌曲？",
                    PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                ContentDialogResult result;
                _trackDialogOpen = true;
                try { result = await confirm.ShowAsync(); }
                finally { _trackDialogOpen = false; }
                if (result != ContentDialogResult.Primary || _account?.Id != userId || _closed) return;
            }
            var ids = selection.Select(track => track.Id).ToArray();
            if (remove) await _client.RemovePlaylistTracksAsync(playlistId, ids, _accountLifetime.Token);
            else await _client.AddPlaylistTracksAsync(playlistId, ids, _accountLifetime.Token);
            if (_closed || _account?.Id != userId) return;
            ShowToast(remove ? $"已从歌单删除 {ids.Length} 首歌曲" : $"已加入「{title}」");
            if (_page == "playlist" && _playlistId == playlistId)
            {
                // Refresh the lightweight ID index after a mutation so loaded
                // virtual pages cannot retain stale offsets or deleted tracks.
                await OpenPlaylistAsync(playlistId);
            }
            _ = SyncCloudPlaylistsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closed && _account?.Id == userId)
            {
                // A multi-batch upstream write may partially succeed. Reload the
                // authoritative list before presenting the failure; never retry.
                var errorPageRequest = _pageRequest;
                if (_page == "playlist" && _playlistId == playlistId)
                {
                    var refresh = OpenPlaylistAsync(playlistId);
                    errorPageRequest = _pageRequest;
                    await refresh;
                }
                if (_closed || _account?.Id != userId) return;
                if (ReferenceEquals(_pageRequest, errorPageRequest)) ShowError("歌单更新未完成", error.Message);
                _ = SyncCloudPlaylistsAsync();
            }
        }
        finally { _playlistMutationBusy = false; }
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TrackSelectionLabel is null) return;
        var count = TrackList.SelectedItems.Count;
        TrackSelectionLabel.Text = $"已选择 {count} 首";
        TrackSelectionLabel.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlayingTrackIndicators()
    {
        UpdatePlayingTrackIndicators(TrackList);
        UpdatePlayingTrackIndicators(HomeSongs);
        UpdatePlayingTrackIndicators(QueueList);
    }

    private void UpdatePlayingTrackIndicators(DependencyObject element)
    {
        if (element is FrameworkElement { Tag: Track track } row)
        {
            var brush = ThemeBrush(track.Id == _player.CurrentTrack?.Id ? "LumaAccentTextBrush" : "LumaTextPrimaryBrush");
            SetTrackTitleBrush(row, brush);
            return;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            UpdatePlayingTrackIndicators(VisualTreeHelper.GetChild(element, index));
    }

    private static void SetTrackTitleBrush(DependencyObject element, Brush brush)
    {
        if (element is TextBlock { Tag: "TrackTitle" } title) title.Foreground = brush;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            SetTrackTitleBrush(VisualTreeHelper.GetChild(element, index), brush);
    }
}
