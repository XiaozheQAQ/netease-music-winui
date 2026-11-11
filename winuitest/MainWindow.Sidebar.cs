using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using winuitest.Controls;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private const int SidebarPlaylistPreviewCount = 4;
    private const string SidebarPlaylistToggleRoute = "sidebar:playlists";
    private readonly Dictionary<long, NavigationViewItem> _sidebarPlaylistItems = new();
    private readonly List<MusicPlaylist> _sidebarPlaylists = new();
    private NavigationViewItem? _sidebarPlaylistToggle;
    private Button? _sidebarPlaylistToggleButton;
    private TextBlock? _sidebarPlaylistToggleLabel;
    private FilledIcon? _sidebarPlaylistToggleIcon;
    private bool _sidebarPlaylistsExpanded;
    private int _sidebarPlaylistDataVersion;
    private int _sidebarPlaylistRenderedVersion = -1;
    private bool _sidebarPlaylistRenderedExpanded;
    private long _sidebarPlaylistRenderedActiveId;
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
        RefreshSidebarPlaylistPreview(page);
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
            if (route == SidebarPlaylistToggleRoute)
            {
                _sidebarPlaylistsExpanded = !_sidebarPlaylistsExpanded;
                SynchronizeSidebarSelection(_page);
                return;
            }
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
        _sidebarPlaylists.Clear();
        _sidebarPlaylists.AddRange(playlists.Where(playlist => playlist.Id > 0).DistinctBy(playlist => playlist.Id));
        ++_sidebarPlaylistDataVersion;
        if (_sidebarPlaylists.Count <= SidebarPlaylistPreviewCount) _sidebarPlaylistsExpanded = false;
        SynchronizeSidebarSelection(_page);
    }

    private void RefreshSidebarPlaylistPreview(string page)
    {
        var activeId = !_sidebarPlaylistsExpanded && page == "playlist" ? _playlistId : 0;
        if (_sidebarPlaylistRenderedVersion == _sidebarPlaylistDataVersion
            && _sidebarPlaylistRenderedExpanded == _sidebarPlaylistsExpanded
            && _sidebarPlaylistRenderedActiveId == activeId) return;
        var items = _sidebarPlaylistsExpanded
            ? _sidebarPlaylists.ToList()
            : _sidebarPlaylists.Take(SidebarPlaylistPreviewCount).ToList();
        // Keep the currently open cloud playlist visible without expanding the
        // whole library. The preview retains its fixed, predictable footprint.
        if (!_sidebarPlaylistsExpanded && page == "playlist"
            && items.All(playlist => playlist.Id != _playlistId)
            && _sidebarPlaylists.FirstOrDefault(playlist => playlist.Id == _playlistId) is { } active)
        {
            if (items.Count == SidebarPlaylistPreviewCount) items.RemoveAt(items.Count - 1);
            items.Add(active);
        }

        NavigationViewItem? focusedItem = null;
        for (var focused = Root.XamlRoot is null ? null : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (focused is NavigationViewItem navigationItem) { focusedItem = navigationItem; break; }
        var focusState = focusedItem?.FocusState ?? FocusState.Unfocused;
        var removedFocus = false;
        var ids = items.Select(playlist => playlist.Id).ToHashSet();
        foreach (var id in _sidebarPlaylistItems.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            removedFocus |= ReferenceEquals(focusedItem, _sidebarPlaylistItems[id]);
            MainNavigation.MenuItems.Remove(_sidebarPlaylistItems[id]);
            _sidebarPlaylistItems.Remove(id);
        }

        // Keep one native navigation/selection/keyboard model. The main menu's
        // own ScrollViewer measures available space above the fixed settings row;
        // there is no separate footer list or estimated window-height budget.
        var insertionIndex = MainNavigation.MenuItems.IndexOf(CloudNav) + 1;
        void PlaceItem(NavigationViewItem item)
        {
            var currentIndex = MainNavigation.MenuItems.IndexOf(item);
            if (currentIndex != insertionIndex)
            {
                if (currentIndex >= 0) MainNavigation.MenuItems.RemoveAt(currentIndex);
                MainNavigation.MenuItems.Insert(insertionIndex, item);
            }
            insertionIndex++;
        }
        var showToggle = _sidebarPlaylists.Count > SidebarPlaylistPreviewCount;
        if (showToggle)
        {
            if (_sidebarPlaylistToggle is null)
            {
                _sidebarPlaylistToggleLabel = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                _sidebarPlaylistToggleIcon = new FilledIcon { Kind = "ChevronDown", FontSize = 18, RenderTransformOrigin = new Point(.5, .5) };
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                content.Children.Add(_sidebarPlaylistToggleIcon);
                content.Children.Add(_sidebarPlaylistToggleLabel);
                _sidebarPlaylistToggleButton = new Button
                {
                    Name = "SidebarPlaylistsToggle",
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7)
                };
                _sidebarPlaylistToggleButton.Click += (_, _) => QueueSidebarNavigation(SidebarPlaylistToggleRoute);
                _sidebarPlaylistToggle = new NavigationViewItem
                {
                    Name = "SidebarPlaylistsToggleItem",
                    SelectsOnInvoked = false,
                    IsTabStop = false,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = _sidebarPlaylistToggleButton
                };
                // Arrow navigation can focus the native wrapper. Transfer that
                // focus to its real command, which also exposes UIA Invoke.
                _sidebarPlaylistToggle.GotFocus += (_, args) =>
                {
                    if (args.OriginalSource is NavigationViewItem { Content: Button command } row
                        && ReferenceEquals(row, _sidebarPlaylistToggle)) command.Focus(row.FocusState);
                };
                AutomationProperties.SetAutomationId(_sidebarPlaylistToggleButton, "SidebarPlaylistsToggle");
            }
            _sidebarPlaylistToggleLabel!.Text = _sidebarPlaylistsExpanded ? "收起歌单" : "展开全部";
            var toggleDescription = _sidebarPlaylistsExpanded ? "收起歌单" : $"展开全部 {_sidebarPlaylists.Count} 个歌单";
            AutomationProperties.SetName(_sidebarPlaylistToggleButton!, toggleDescription);
            ToolTipService.SetToolTip(_sidebarPlaylistToggleButton!, toggleDescription);
            _sidebarPlaylistToggleIcon!.RenderTransform = new RotateTransform { Angle = _sidebarPlaylistsExpanded ? 180 : 0 };
        }
        var displayedCount = 0;
        foreach (var playlist in items)
        {
            if (!_sidebarPlaylistItems.TryGetValue(playlist.Id, out var item))
            {
                item = new NavigationViewItem
                {
                    Name = $"SidebarPlaylist{playlist.Id}",
                    Tag = $"playlist:{playlist.Id}",
                    Icon = new FilledIcon { Kind = "Playlist" },
                    Content = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                };
                AutomationProperties.SetAutomationId(item, $"SidebarPlaylist{playlist.Id}");
                _sidebarPlaylistItems.Add(playlist.Id, item);
            }
            ((TextBlock)item.Content).Text = playlist.Title;
            ToolTipService.SetToolTip(item, playlist.Title);
            AutomationProperties.SetName(item, playlist.Title);

            // Reuse unchanged controls so a background sync keeps native focus,
            // selection and scroll anchoring instead of rebuilding the menu.
            PlaceItem(item);
            // A fixed slot keeps collapse reachable and preserves the focused
            // command when additional playlists are inserted below it.
            if (++displayedCount == SidebarPlaylistPreviewCount && showToggle) PlaceItem(_sidebarPlaylistToggle!);
        }

        if (!showToggle && _sidebarPlaylistToggle is not null)
        {
            removedFocus |= ReferenceEquals(focusedItem, _sidebarPlaylistToggle);
            MainNavigation.MenuItems.Remove(_sidebarPlaylistToggle);
        }
        // Account refresh/removal can remove a keyboard-focused playlist. Keep
        // focus inside the same native menu instead of dropping it to the page.
        if (removedFocus)
        {
            Control? focusTarget = page == "playlist" ? FindSidebarPlaylistItem(_playlistId) : null;
            focusTarget ??= _sidebarPlaylists.Count > SidebarPlaylistPreviewCount ? _sidebarPlaylistToggleButton : CloudNav;
            focusTarget?.Focus(focusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
        }
        _sidebarPlaylistRenderedVersion = _sidebarPlaylistDataVersion;
        _sidebarPlaylistRenderedExpanded = _sidebarPlaylistsExpanded;
        _sidebarPlaylistRenderedActiveId = activeId;
    }

    private NavigationViewItem? FindSidebarPlaylistItem(long id) =>
        _sidebarPlaylistItems.TryGetValue(id, out var item) ? item : null;

}
