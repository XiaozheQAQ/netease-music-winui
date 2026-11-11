using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    private async Task RunSidebarPreviewChecksAsync()
    {
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Sidebar preview: " + detail);
        }
        NavigationViewItem[] VisiblePlaylists() => MainNavigation.MenuItems.OfType<NavigationViewItem>()
            .Where(item => item.Tag is string route && route.StartsWith("playlist:", StringComparison.Ordinal)).ToArray();
        async Task InvokeToggleAsync(bool expanded)
        {
            var toggle = _sidebarPlaylistToggle ?? throw new InvalidOperationException("Missing sidebar toggle");
            Require(MainNavigation.MenuItems.Contains(toggle) && !toggle.SelectsOnInvoked, "native toggle must not select a page");
            var button = _sidebarPlaylistToggleButton ?? throw new InvalidOperationException("Missing sidebar command button");
            Require(ReferenceEquals(toggle.Content, button) && toggle.Tag is null, "command belongs to a nonrouting native row");
            var invoke = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Require(invoke is not null, "toggle exposes the native Invoke pattern");
            invoke!.Invoke();
            var elapsed = Stopwatch.StartNew();
            while (_sidebarPlaylistsExpanded != expanded)
            {
                if (_closed || elapsed.ElapsedMilliseconds > 1500) throw new TimeoutException("Sidebar toggle invocation");
                await Task.Delay(16);
            }
            Root.UpdateLayout();
        }

        var originalPlaylists = _sidebarPlaylists.ToArray();
        var originalExpanded = _sidebarPlaylistsExpanded;
        var originalPage = _page;
        var originalPlaylistId = _playlistId;
        var historyCount = _browseVisits.Count;
        var historyIndex = _browseIndex;
        var historyVersion = _browseVersion;
        // Only exercise the sidebar's route projection. No account mutation or
        // network request is required to create a large signed-in menu.
        var playlists = Enumerable.Range(1, 12).Select(index => new MusicPlaylist
        {
            Id = index == 12 ? 888 : 90000 + index,
            Title = "测试歌单 " + index
        }).ToArray();
        try
        {
            _sidebarPlaylistsExpanded = false;
            _page = "search";
            UpdateSidebarPlaylists(playlists);
            Root.UpdateLayout();
            Require(VisiblePlaylists().Select(item => (string)item.Tag).SequenceEqual(new[]
                { "playlist:90001", "playlist:90002", "playlist:90003", "playlist:90004" }), "four-item default preview");
            Require(MainNavigation.SelectedItem is null, "preview does not select a page");
            Require(_sidebarPlaylistToggleButton is { IsEnabled: true }
                && AutomationProperties.GetAutomationId(_sidebarPlaylistToggleButton) == "SidebarPlaylistsToggle", "accessible toggle");
            var first = _sidebarPlaylistItems[90001];
            var toggleSlot = MainNavigation.MenuItems.IndexOf(_sidebarPlaylistToggle!);
            Require(toggleSlot == MainNavigation.MenuItems.IndexOf(CloudNav) + SidebarPlaylistPreviewCount + 1,
                "toggle stays immediately after the four-item preview");
            Require(_sidebarPlaylistToggleButton!.Focus(FocusState.Keyboard), "toggle accepts native keyboard focus");

            await InvokeToggleAsync(true);
            Require(VisiblePlaylists().Length == 12 && ReferenceEquals(first, _sidebarPlaylistItems[90001]), "expand includes items beyond the former ten-item cap and reuses existing containers");
            Require(MainNavigation.MenuItems.IndexOf(_sidebarPlaylistToggle!) == toggleSlot, "expanded collapse command stays at the same slot");
            Require(_sidebarPlaylistToggleButton.FocusState != FocusState.Unfocused, "expansion preserves keyboard focus on the toggle");
            Require(MainNavigation.SelectedItem is null, "expand does not select its command item");
            var unchangedOrder = MainNavigation.MenuItems.ToArray();
            SynchronizeSidebarSelection("artist");
            Require(MainNavigation.MenuItems.SequenceEqual(unchangedOrder), "unrelated route does not rebuild expanded menu");

            _playlistId = 888;
            _page = "playlist";
            SynchronizeSidebarSelection(_page);
            var active = _sidebarPlaylistItems[888];
            Require(ReferenceEquals(MainNavigation.SelectedItem, active) && active.IsSelected, "native active playlist selection");
            await InvokeToggleAsync(false);
            Require(VisiblePlaylists().Select(item => (string)item.Tag).SequenceEqual(new[]
                { "playlist:90001", "playlist:90002", "playlist:90003", "playlist:888" }), "collapse retains the active playlist within four rows");
            Require(ReferenceEquals(MainNavigation.SelectedItem, active) && active.IsSelected, "collapse preserves the same selected container");
            Require(MainNavigation.MenuItems.IndexOf(_sidebarPlaylistToggle!) == toggleSlot, "collapsed command keeps its slot with the active playlist retained");
            Root.UpdateLayout();
            Require(active.Focus(FocusState.Keyboard), "preview playlist accepts native keyboard focus");

            var updated = playlists.Select(playlist => new MusicPlaylist { Id = playlist.Id, Title = playlist.Title + " 已同步" }).ToArray();
            UpdateSidebarPlaylists(updated);
            Require(ReferenceEquals(_sidebarPlaylistItems[888], active)
                && ((TextBlock)active.Content).Text.EndsWith("已同步", StringComparison.Ordinal)
                && active.FocusState != FocusState.Unfocused && active.IsSelected, "sync updates titles without replacing selection or keyboard focus");

            UpdateSidebarPlaylists(updated.Take(2).Concat(updated.Take(1)));
            Root.UpdateLayout();
            Require(VisiblePlaylists().Length == 2 && !_sidebarPlaylistsExpanded
                && !MainNavigation.MenuItems.Contains(_sidebarPlaylistToggle!), "small and deduplicated collections need no toggle");
            Require(MainNavigation.SelectedItem is null && CloudNav.FocusState != FocusState.Unfocused, "removing the focused playlist clears selection and keeps focus in the menu");
            UpdateSidebarPlaylists(Array.Empty<MusicPlaylist>());
            Require(VisiblePlaylists().Length == 0 && MainNavigation.SelectedItem is null, "logout removes the preview");
            Require(_browseVisits.Count == historyCount && _browseIndex == historyIndex && _browseVersion == historyVersion,
                "expand, collapse, and sync must never create navigation history");
        }
        finally
        {
            _page = originalPage;
            _playlistId = originalPlaylistId;
            _sidebarPlaylistsExpanded = originalExpanded;
            UpdateSidebarPlaylists(originalPlaylists);
            Root.UpdateLayout();
        }
    }
}
