using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly Dictionary<long, NavigationViewItem> _sidebarPlaylistItems = new();

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

        if (_page == "playlist") SelectSidebarPlaylist(_playlistId);
    }

    private NavigationViewItem? FindSidebarPlaylistItem(long id) =>
        _sidebarPlaylistItems.TryGetValue(id, out var item) ? item : null;

    private void SelectSidebarPlaylist(long id) => MainNavigation.SelectedItem = FindSidebarPlaylistItem(id);
}
