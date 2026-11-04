using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using winuitest.Controls;

namespace winuitest;

public sealed partial class MainWindow
{
    private bool _pageLoading;
    private bool _cloudLoading;
    private int _loadingTransition;

    private UIElement CurrentPageView => _page switch
    {
        "discover" => HomeView,
        "cloud" => CloudView,
        "settings" => SettingsView,
        "artist" => ArtistView,
        _ => TracksView
    };

    private void ResetPageLoading()
    {
        _loadingTransition++;
        _pageLoading = false;
        PageSkeleton.IsActive = false;
        _motion.Cancel(PageSkeleton, false);
    }

    private async void SetPageLoading(bool loading)
    {
        if (_pageLoading == loading) return;
        var transition = ++_loadingTransition;
        _pageLoading = loading;
        if (loading)
        {
            PageSkeleton.Kind = _page switch
            {
                "discover" => SkeletonKind.Discovery,
                "cloud" => SkeletonKind.Playlists,
                _ => SkeletonKind.Tracks
            };
            // Only one body is visible: a skeleton never overlays stale content
            // or an empty-state message, and the shared page background stays put.
            foreach (var page in new UIElement[] { HomeView, TracksView, CloudView, SettingsView, ArtistView })
                _motion.Cancel(page, false);
            EmptyState.Visibility = Visibility.Collapsed;
            _motion.Cancel(PageSkeleton, true);
            PageSkeleton.IsActive = true;
        }
        else
        {
            UpdateEmptyState();
            if (_page == "cloud") CloudEmpty.Visibility = _cloudPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_startupReady)
            {
                PageSkeleton.IsActive = false;
                _motion.Cancel(PageSkeleton, false);
                _motion.Cancel(CurrentPageView, true);
                return;
            }
            if (await _motion.HideAsync(PageSkeleton, offset: 0, durationMs: 140) && !_closed && transition == _loadingTransition)
            {
                PageSkeleton.IsActive = false;
                _ = _motion.RevealAsync(CurrentPageView, offset: 6, durationMs: 320);
            }
        }
    }

    private void SetCloudLoading(bool loading)
    {
        _cloudLoading = loading;
        if (_page == "cloud") SetPageLoading(loading);
    }

    private void UpdateResponsiveLayout()
    {
        if (PlayerLayout is null || Root.ActualWidth <= 0) return;
        var compact = Root.ActualWidth < 1040;
        var inset = compact ? 24 : 36;
        SidebarColumn.Width = new GridLength(compact ? 200 : 220);
        MainNavigation.OpenPaneLength = SidebarColumn.Width.Value;
        PageToolbar.Margin = new Thickness(inset, 0, inset, 12);
        SearchColumn.Width = new GridLength(Math.Clamp((Root.ActualWidth - SidebarColumn.Width.Value) * .46, 220, 320));
        HomeContent.Margin = new Thickness(inset, 0, inset, 32);
        CloudContent.Margin = HomeContent.Margin;
        TracksView.Margin = new Thickness(inset, 0, inset, 20);
        ArtistView.Margin = TracksView.Margin;
        Inspector.Padding = new Thickness(inset, 0, inset, 20);
        PageSkeleton.Margin = TracksView.Margin;
        SettingsView.Margin = new Thickness(inset, 0, inset, 24);
        Notice.Margin = new Thickness(inset, 0, inset, 14);
        var playerInset = compact ? 12 : 16;
        var playerGap = compact ? 12 : 16;
        PlayerLayout.Margin = new Thickness(playerInset, 0, playerInset, 0);
        PlayerLayout.ColumnSpacing = playerGap;
        var playerWidth = Math.Max(0, Root.ActualWidth - playerInset * 2 - playerGap * 2);
        // Equal side columns keep transport centered on the whole window,
        // including when the title is long or the quality badge is visible.
        var transportMinimum = Math.Min(240, Math.Max(202, playerWidth * .35));
        PlayerTransportColumn.MinWidth = transportMinimum;
        var sideBudget = Math.Max(0, (playerWidth - transportMinimum) / 2);
        var playerSideWidth = Math.Min(Math.Clamp(playerWidth * .25, 240, 340), sideBudget);
        PlayerInfoColumn.Width = new GridLength(playerSideWidth);
        PlayerToolsColumn.Width = new GridLength(playerSideWidth);
        PlayerCoverBorder.Width = PlayerCoverBorder.Height = compact ? 44 : 52;
        // Two 32px buttons, a 36px quality label and three 6px gaps are reserved.
        // Very small work areas retain the mute button and keyboard volume controls.
        VolumeSlider.Width = Math.Clamp(playerSideWidth - 118, 40, compact ? 68 : 88);
        VolumeSlider.Visibility = playerSideWidth >= 158 ? Visibility.Visible : Visibility.Collapsed;
        CollectionCoverBorder.Width = CollectionCoverBorder.Height = Root.ActualHeight < 690 ? 104 : 120;
        PageTitle.FontSize = compact ? 28 : 32;
        var shortWindow = Root.ActualHeight < 700;
        PlayerBarRow.Height = new GridLength(shortWindow ? 92 : 96);
        AccountButton.Margin = shortWindow ? new Thickness(12, 4, 12, 8) : new Thickness(12, 12, 12, 18);
        AccountButton.Padding = new Thickness(8, shortWindow ? 8 : 12, 8, shortWindow ? 8 : 12);
        SettingsPageTitle.FontSize = PageTitle.FontSize;
        HomePageTitle.FontSize = CloudTitle.FontSize = PageTitle.FontSize;
        QueuePageTitle.FontSize = PageTitle.FontSize;
        UpdateTrackHeadingWidth();
    }

    private void SquareArtwork_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is Border border && args.NewSize.Width > 0 && Math.Abs(border.Height - args.NewSize.Width) > .5)
            border.Height = args.NewSize.Width;
    }
}
