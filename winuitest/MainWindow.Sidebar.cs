using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly Dictionary<long, NavigationViewItem> _sidebarPlaylistItems = new();
    private int _sidebarNavigationVersion;

    private void InitializeSidebarNavigation()
    {
        MainNavigation.Loaded += (_, _) =>
        {
            if (MainNavigation.SettingsItem is NavigationViewItem item)
            {
                item.Content = "设置";
                item.Icon = new FilledIcon { Kind = "Settings" };
            }
            // Settings and the native selection model exist only after the
            // template loads. Restore the current route, not the startup route.
            SynchronizeSidebarSelection(_page);
        };
    }

    private void SynchronizeSidebarSelection(string page)
    {
        ++_sidebarNavigationVersion;
        var target = page switch
        {
            "discover" => DiscoverNav,
            "favorites" => FavoritesNav,
            "recent" => RecentNav,
            "cloud" => CloudNav,
            "playlist" => FindSidebarPlaylistItem(_playlistId),
            "settings" => MainNavigation.SettingsItem as NavigationViewItem,
            _ => null
        };
        if (target is not null)
        {
            // Do not clear first: NavigationView needs both containers for its
            // native indicator transition, including interrupted navigation.
            if (!ReferenceEquals(MainNavigation.SelectedItem, target)) MainNavigation.SelectedItem = target;
            return;
        }

        // Clear the container first so NavigationView also deselects its internal
        // SelectionModel. Setting SelectedItem=null alone can leave its previous
        // index/indicator alive when a pending template selection completes.
        if (MainNavigation.SelectedItem is NavigationViewItem selected && selected.IsSelected) selected.IsSelected = false;
        foreach (var item in MainNavigation.MenuItems.OfType<NavigationViewItem>())
            if (item.IsSelected) item.IsSelected = false;
        if (MainNavigation.SettingsItem is NavigationViewItem settings && settings.IsSelected) settings.IsSelected = false;
        if (MainNavigation.SelectedItem is not null) MainNavigation.SelectedItem = null;
    }

    private void QueueSidebarNavigation(string? route)
    {
        if (_closed || string.IsNullOrEmpty(route)) return;
        var version = ++_sidebarNavigationVersion;
        // ItemInvoked precedes the native selection transaction's completion.
        // Navigate afterwards so a non-menu destination cannot be reselected by
        // the original invocation. A newer route supersedes this queued action.
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed || version != _sidebarNavigationVersion) return;
            await NavigateSidebarAsync(route);
        });
    }

    private async Task NavigateSidebarAsync(string route)
    {
        if (route.StartsWith("playlist:", StringComparison.Ordinal) && long.TryParse(route[9..], out var playlistId))
        {
            if (_page != "playlist" || _playlistId != playlistId) await OpenPlaylistAsync(playlistId);
            return;
        }
        if (route == _page) return;
        if (route == "settings") BeginPage("settings");
        else if (route == "cloud") await OpenCloudAsync();
        else if (route.StartsWith("mood:", StringComparison.Ordinal)) await SearchAsync(route[5..]);
        else if (route == "discover") await LoadHomeAsync();
        else if (route == "search") await SearchAsync("");
        else if (route is "favorites" or "recent") OpenLibrary(route);
    }

    private void UpdateSidebarPlaylists(IEnumerable<MusicPlaylist> playlists)
    {
        var items = playlists.DistinctBy(playlist => playlist.Id).ToList();
        var ids = items.Select(playlist => playlist.Id).ToHashSet();
        foreach (var id in _sidebarPlaylistItems.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            MainNavigation.MenuItems.Remove(_sidebarPlaylistItems[id]);
            _sidebarPlaylistItems.Remove(id);
        }

        // Keep one native navigation/selection/keyboard model. The main menu's
        // own ScrollViewer measures available space above the fixed settings row;
        // there is no separate footer list or estimated window-height budget.
        var insertionIndex = MainNavigation.MenuItems.IndexOf(CloudNav) + 1;
        foreach (var playlist in items)
        {
            if (!_sidebarPlaylistItems.TryGetValue(playlist.Id, out var item))
            {
                item = new NavigationViewItem
                {
                    Tag = $"playlist:{playlist.Id}",
                    Icon = new FilledIcon { Kind = "Playlist" },
                    Content = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                };
                _sidebarPlaylistItems.Add(playlist.Id, item);
            }
            ((TextBlock)item.Content).Text = playlist.Title;
            ToolTipService.SetToolTip(item, playlist.Title);
            AutomationProperties.SetName(item, playlist.Title);

            // Reuse unchanged controls so a background sync keeps native focus,
            // selection and scroll anchoring instead of rebuilding the menu.
            var currentIndex = MainNavigation.MenuItems.IndexOf(item);
            if (currentIndex != insertionIndex)
            {
                if (currentIndex >= 0) MainNavigation.MenuItems.RemoveAt(currentIndex);
                MainNavigation.MenuItems.Insert(insertionIndex, item);
            }
            insertionIndex++;
        }

        if (_page == "playlist") SynchronizeSidebarSelection(_page);
    }

    private NavigationViewItem? FindSidebarPlaylistItem(long id) =>
        _sidebarPlaylistItems.TryGetValue(id, out var item) ? item : null;

}
