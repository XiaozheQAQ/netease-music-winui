using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using winuitest.Models;
using winuitest.Controls;
using winuitest.Services;

namespace winuitest;

public sealed partial class MainWindow : Window
{
    private readonly NeteaseMusicClient _client = new();
    private readonly LibraryStore _library = new(App.DataDirectory);
    private readonly PlaybackService _player;
    private readonly ObservableCollection<Track> _tracks = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly MotionCoordinator _motion;
    private bool _lyricsRendering;
    private bool _immersiveOpen;
    private int _lyricsTransition;
    private bool _inspectorOpen;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly UISettings _uiSettings = new();
    private bool _sessionReady;
    private bool _themeReady;
    private HomeFeed? _home;
    private CancellationTokenSource? _pageRequest;
    private CancellationTokenSource? _lyricsRequest;
    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private string _page = "discover";
    private string _query = "";
    private long _playlistId;
    private long _displayedTrackId;
    private long _rememberedTrackId;
    private int _lyricIndex = -1;
    private string _queueSignature = "";
    private int _searchTotal;
    private int _nextSearchOffset;
    private bool _updatingSeek;
    private bool _loadingMore;
    private bool _loaded;
    private bool _closed;
    private bool _queueVisible;
    private bool _lyricsFullScreen;
    private KeyboardAccelerator? _exitLyricsShortcut;
    private readonly Dictionary<UIElement, bool> _shellHitTesting = new();
    private double _lastVolume = 0.7;

    public MainWindow()
    {
        // Set the native identity before XAML construction and the first activation.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Luma.ico"));
        _motion = new MotionCoordinator(_uiSettings);
        InitializeComponent();
        _motion.AttachPressFeedback(Root);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StartupDragRegion);
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(1120 * scale), workArea.Width - 40), Math.Min((int)(740 * scale), workArea.Height - 40)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(940 * scale), workArea.Width - 40);
            presenter.PreferredMinimumHeight = Math.Min((int)(620 * scale), workArea.Height - 40);
        }
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _player = new PlaybackService(_client, DispatcherQueue)
        {
            Volume = _library.Volume,
            Shuffle = _library.Shuffle,
            Repeat = _library.Repeat
        };
        _client.PreferredQuality = _library.PlaybackQuality;
        VolumeSlider.Value = _player.Volume * 100;
        _player.StateChanged += Player_StateChanged;
        _player.Error += (_, message) => ShowError("暂时无法播放", message);
        TrackList.ItemsSource = _tracks;
        TrackList.ContainerContentChanging += TrackContainer_Changing;
        HomeSongs.ContainerContentChanging += TrackContainer_Changing;
        _toastTimer.Tick += async (_, _) => { _toastTimer.Stop(); await _motion.HideAsync(Toast, offset: 8, durationMs: 260); };
        _sessionTimer.Tick += (_, _) => SaveSession();
        ThemeSelector.SelectedIndex = (int)_library.Theme;
        QualitySelector.SelectedItem = QualitySelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == _library.PlaybackQuality);
        _themeReady = true;
        ApplyTheme();
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        _uiSettings.ColorValuesChanged += SystemColorsChanged;
        MainNavigation.Loaded += (_, _) => { if (MainNavigation.SettingsItem is NavigationViewItem item) { item.Content = "设置"; item.Icon = new FilledIcon { Kind = "Settings" }; } };
        SearchBox.QueryIcon = new FilledIcon { Kind = "Search", FontSize = 16 };
        Closed += Window_Closed;
        InitializeShortcuts();
        NowPlaying.PlayPauseRequested += (_, _) => PlayPause_Click(PlayPauseButton, new RoutedEventArgs());
        NowPlaying.PreviousRequested += async (_, _) => await _player.PreviousAsync();
        NowPlaying.NextRequested += async (_, _) => await _player.NextAsync();
        NowPlaying.FavoriteRequested += (_, _) => PlayerFavorite_Click(PlayerFavorite, new RoutedEventArgs());
        NowPlaying.SeekRequested += (_, seconds) => SeekTo(seconds);
        NowPlaying.VolumeRequested += (_, volume) => VolumeSlider.Value = volume * 100;
        NowPlaying.LyricCorrectionRequested += (_, offsetMs) =>
        {
            if (_player.CurrentTrack is not Track current) return;
            _library.SetLyricOffsetMs(current.Id, offsetMs);
            UpdateNowPlaying();
            NowPlaying.UpdateLyricsPosition(_player.GetLyricPosition());
            HighlightLyric();
        };
        NowPlaying.CloseRequested += (_, _) => CloseNowPlaying();
        NowPlaying.FullScreenRequested += (_, _) => SetLyricsFullScreen(!_lyricsFullScreen);
        NowPlaying.ShuffleRequested += (_, _) => Shuffle_Click(ShuffleButton, new RoutedEventArgs());
        NowPlaying.RepeatRequested += (_, _) => Repeat_Click(RepeatButton, new RoutedEventArgs());
        SelectNav("discover");
        TodayLabel.Text = DateTime.Now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        Player_StateChanged(this, EventArgs.Empty);
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await InitializeStartupAsync();
    }

    private CancellationToken BeginPage(string page, CancellationToken cancellationToken = default)
    {
        _pageRequest?.Cancel();
        _pageRequest?.Dispose();
        _pageRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _page = page;
        _inspectorOpen = false;
        _motion.Cancel(Inspector, visible: false);
        _motion.Cancel(PageBody, visible: true);
        if (_exitLyricsShortcut is not null) _exitLyricsShortcut.IsEnabled = _immersiveOpen || _inspectorOpen;
        Notice.IsOpen = false;
        ResetPageLoading();
        var destination = page == "discover" ? (UIElement)HomeView : page == "cloud" ? CloudView : page == "settings" ? SettingsView : TracksView;
        foreach (var view in new UIElement[] { HomeView, TracksView, CloudView, SettingsView })
        {
            if (!_startupReady) _motion.Cancel(view, ReferenceEquals(view, destination));
            else if (!ReferenceEquals(view, destination)) _ = _motion.HideAsync(view, offset: -6, durationMs: 200);
        }
        if (_startupReady) _ = _motion.RevealAsync(destination, offset: 10, durationMs: 360);
        RefreshButton.Visibility = page == "settings" ? Visibility.Collapsed : Visibility.Visible;
        CollectionCoverBorder.Visibility = Visibility.Collapsed;
        ResetSearchPaging();
        SelectNav(page);
        return _pageRequest.Token;
    }

    private void SelectNav(string page)
    {
        MainNavigation.SelectedItem = page switch { "discover" => DiscoverNav, "favorites" => FavoritesNav, "recent" => RecentNav, "cloud" => CloudNav, "playlist" => FindSidebarPlaylistItem(_playlistId), "settings" => MainNavigation.SettingsItem, _ => null };
        Breadcrumb.Text = page switch { "favorites" or "recent" => "资料库", "playlist" => "歌单", "cloud" => "网易云音乐", "settings" => "设置", "search" => "搜索", _ => "现在就听" };
    }

    private async Task LoadHomeAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        var token = BeginPage("discover", cancellationToken);
        if (_home is not null && !refresh) return;
        SetPageLoading(true);
        try
        {
            var home = await _client.DiscoverAsync(token);
            token.ThrowIfCancellationRequested();
            _home = home;
            PlaylistGrid.ItemsSource = home.Playlists.Take(6).ToList();
            HomeSongs.ItemsSource = home.NewSongs.Take(5).ToList();
            SyncTrackSelection();
            SetImage(HeroArtwork, home.NewSongs.FirstOrDefault()?.ArtworkUrl);
            HeroButton.IsEnabled = home.NewSongs.Count > 0;
            ResizeCards();
            if (home.Playlists.Count == 0 && home.NewSongs.Count == 0) ShowError("暂无发现内容", "可以先搜索歌曲，或点击右上角刷新重试。");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && !_startupReady) ShowError("连接暂时不可用", "请检查网络后点击右上角刷新重试。");
        }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowError("音源暂时不可用", ex.Message + " 点击右上角刷新重试。"); }
        finally { if (!_closed && _pageRequest?.Token == token) SetPageLoading(false); }
    }

    private void ShowTracks(string title, string description, IEnumerable<Track> tracks, string eyebrow = "YOUR MUSIC")
    {
        PageTitle.Text = title;
        PageDescription.Text = description;
        CollectionEyebrow.Text = eyebrow;
        _tracks.Clear();
        foreach (var track in tracks) _tracks.Add(track);
        SyncTrackSelection();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = !_pageLoading && _tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayAllButton.IsEnabled = _tracks.Count > 0;
        EmptyTitle.Text = _page switch { "favorites" => "暂无收藏", "recent" => "暂无播放记录", "playlist" => "暂无歌曲", "search" when _query.Length > 0 => "未找到歌曲", _ => "搜索音乐" };
        EmptyDescription.Text = _page switch { "favorites" => "点击爱心收藏歌曲", "recent" => "播放过的歌曲会显示在这里", "playlist" => "稍后刷新试试", "search" when _query.Length > 0 => "试试其他关键词", _ => "输入歌曲或歌手" };
    }

    private async Task SearchAsync(string keywords, bool append = false)
    {
        if (_closed) return;
        keywords = keywords.Trim();
        if (append && (!CanAppendSearchResults || !string.Equals(keywords, _query, StringComparison.Ordinal))) return;
        if (keywords.Length == 0)
        {
            BeginPage("search");
            _query = "";
            ShowTracks("搜索", "", Array.Empty<Track>(), "FIND YOUR SOUND");
            SearchBox.Focus(FocusState.Keyboard);
            return;
        }
        var token = append ? _pageRequest!.Token : BeginPage("search");
        if (!append)
        {
            _query = keywords;
            SearchBox.Text = keywords;
            ShowTracks("“" + keywords + "”", "", Array.Empty<Track>(), "SEARCH RESULTS");
            _trackListScroller?.ChangeView(null, 0, null, true);
            SetPageLoading(true);
        }
        _loadingMore = true;
        SetSearchAppendLoading(append);
        try
        {
            var result = await _client.SearchAsync(keywords, _nextSearchOffset, 30, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(token, keywords)) return;
            _searchTotal = Math.Max(0, result.Total);
            _nextSearchOffset += result.Tracks.Count;
            var existing = _tracks.Select(track => track.Id).ToHashSet();
            var added = 0;
            foreach (var track in result.Tracks)
            {
                if (!existing.Add(track.Id)) continue;
                _tracks.Add(track);
                added++;
            }
            // An empty or repeated page must stop even if the source overstates its total.
            _searchHasMore = added > 0 && _nextSearchOffset < _searchTotal;
            SyncTrackSelection();
            PageDescription.Text = $"共 {_searchTotal:N0} 首歌曲";
            UpdateEmptyState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentSearch(token, keywords))
            {
                _searchPagingFailed = true;
                _searchHasMore = false;
                ShowError(append ? "更多歌曲加载失败" : "搜索失败", ex.Message + " 点击右上角刷新重试。");
                if (!append) PageDescription.Text = "搜索未完成，点击右上角刷新重试。";
            }
        }
        finally
        {
            if (IsCurrentSearch(token, keywords))
            {
                _loadingMore = false;
                SetSearchAppendLoading(false);
                if (!append) SetPageLoading(false);
                QueueSearchViewportCheck();
            }
        }
    }

    private async Task OpenPlaylistAsync(long id)
    {
        var token = BeginPage("playlist");
        _playlistId = id;
        SelectSidebarPlaylist(id);
        ShowTracks("歌单", "", Array.Empty<Track>(), "CURATED PLAYLIST");
        SetPageLoading(true);
        try
        {
            var playlist = await _client.GetPlaylistAsync(id, token);
            token.ThrowIfCancellationRequested();
            ShowTracks(playlist.Title, playlist.Subtitle + $" · 已加载 {playlist.Tracks.Count} 首", playlist.Tracks, "CURATED PLAYLIST");
            SetImage(CollectionCover, playlist.ArtworkUrl);
            CollectionCoverBorder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowError("歌单加载失败", ex.Message); }
        finally { if (!token.IsCancellationRequested && !_closed) SetPageLoading(false); }
    }

    private void OpenLibrary(string page)
    {
        BeginPage(page);
        if (page == "favorites") ShowTracks("喜欢的歌曲", $"{_library.Favorites.Count} 首歌曲", _library.Favorites, "MADE OF YOUR FAVORITES");
        else ShowTracks("最近播放", "最近 100 首", _library.Recent, "RECENTLY PLAYED");
    }

    private async void MainNavigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked) { BeginPage("settings"); return; }
        var page = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        if (page is null) return;
        if (page.StartsWith("playlist:", StringComparison.Ordinal) && long.TryParse(page[9..], out var playlistId)) { await OpenPlaylistAsync(playlistId); return; }
        if (page == "cloud") { await OpenCloudAsync(); return; }
        if (page.StartsWith("mood:", StringComparison.Ordinal)) { await SearchAsync(page[5..]); return; }
        if (page == "discover") await LoadHomeAsync();
        else if (page == "search") await SearchAsync("");
        else OpenLibrary(page);
    }
    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await SearchAsync(args.QueryText);
    private async void Mood_Click(object sender, RoutedEventArgs e) => await SearchAsync((string)((Button)sender).Tag);
    private async void PlaylistGrid_ItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is MusicPlaylist playlist) await OpenPlaylistAsync(playlist.Id); }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case "discover": await LoadHomeAsync(true); break;
            case "search": await SearchAsync(_query); break;
            case "playlist": await OpenPlaylistAsync(_playlistId); break;
            case "favorites": case "recent": OpenLibrary(_page); break;
            case "settings": break;
            case "cloud": await SyncCloudPlaylistsAsync(); break;
            default: AllNewSongs_Click(sender, e); break;
        }
    }
    private void AllNewSongs_Click(object sender, RoutedEventArgs e)
    {
        BeginPage("new");
        ShowTracks("新歌速递", "", _home?.NewSongs ?? new List<Track>(), "FRESH FINDS");
    }
    private async void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (_home?.NewSongs.FirstOrDefault() is Track track) await _player.PlayAsync(track, _home.NewSongs);
        else ShowToast("暂无新歌");
    }
    private async void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track)
            await _player.PlayAsync(track, ReferenceEquals(sender, HomeSongs) ? _home?.NewSongs : _tracks);
    }
    private async void PlayAll_Click(object sender, RoutedEventArgs e) { if (_tracks.FirstOrDefault() is Track track) await _player.PlayAsync(track, _tracks); }
    private async void QueueList_ItemClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is Track track) await _player.PlayAsync(track); }
    private void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Track track }) return;
        if (track.Id == _player.CurrentTrack?.Id) { ShowToast("正在播放的歌曲将在切歌后可移除。"); return; }
        _player.RemoveFromQueue(track);
        ShowToast("已移出待播清单");
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await _player.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _player.NextAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.CurrentTrack is null && _player.Queue.FirstOrDefault() is Track queued) await _player.PlayAsync(queued);
        else if (_player.CurrentTrack is null) Hero_Click(sender, e);
        else _player.TogglePlayPause();
    }

    private void FavoriteTrack_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Track track)
        {
            ToggleFavorite(track);
            if (((Button)sender).Content is FontIcon icon) SetHeart(icon, _library.IsFavorite(track.Id));
        }
    }
    private void FavoriteButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Track track, Content: FontIcon icon }) SetHeart(icon, _library.IsFavorite(track.Id));
    }
    private void TrackContainer_Changing(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        args.RegisterUpdateCallback((_, updated) => UpdateTrackHearts(updated.ItemContainer));
    }
    private void PlayerFavorite_Click(object sender, RoutedEventArgs e) { if (_player.CurrentTrack is Track track) ToggleFavorite(track); }
    private void ToggleFavorite(Track track)
    {
        bool favorite = _library.ToggleFavorite(track);
        UpdateFavorite();
        UpdateTrackHearts(TrackList);
        UpdateTrackHearts(HomeSongs);
        ShowToast(_library.LastSaveError ?? (favorite ? "已添加到喜欢的歌曲" : "已取消收藏"));
        if (_page == "favorites") OpenLibrary("favorites");
    }
    private void UpdateFavorite()
    {
        PlayerFavorite.IsEnabled = _player.CurrentTrack is not null;
        SetHeart(PlayerFavoriteIcon, _player.CurrentTrack is Track track && _library.IsFavorite(track.Id));
        UpdateNowPlaying();
    }
    private void SetHeart(FontIcon icon, bool selected)
    {
        icon.Glyph = selected ? FilledIcons.Heart : FilledIcons.HeartOff;
        icon.Foreground = ThemeBrush(selected ? "LumaAccentTextBrush" : "LumaTextTertiaryBrush");
    }
    private void UpdateTrackHearts(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is Button { Tag: Track track, Content: FontIcon icon } button && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "收藏歌曲") SetHeart(icon, _library.IsFavorite(track.Id));
            else UpdateTrackHearts(child);
        }
    }
    private void TrackMenu_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not Track track) return;
        var menu = new MenuFlyout();
        var add = new MenuFlyoutItem { Text = "加入待播清单", Icon = new FilledIcon { Glyph = FilledIcons.Add } };
        add.Click += (_, _) => { _player.AddToQueue(track); ShowToast("已加入待播清单"); };
        var favorite = new MenuFlyoutItem { Text = _library.IsFavorite(track.Id) ? "取消收藏" : "收藏歌曲", Icon = new FilledIcon { Glyph = FilledIcons.HeartOff } };
        favorite.Click += (_, _) => ToggleFavorite(track);
        menu.Items.Add(add);
        menu.Items.Add(favorite);
        menu.ShowAt(button);
    }

    private void Player_StateChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        var track = _player.CurrentTrack;
        PlayPauseIcon.Glyph = _player.IsLoading ? FilledIcons.Loading : _player.IsPlaying ? FilledIcons.Pause : FilledIcons.Play;
        PlayPauseButton.IsEnabled = !_player.IsLoading;
        ShuffleIcon.Foreground = ThemeBrush(_player.Shuffle ? "LumaAccentTextBrush" : "LumaTextTertiaryBrush");
        RepeatIcon.Foreground = ThemeBrush(_player.Repeat != RepeatMode.Off ? "LumaAccentTextBrush" : "LumaTextTertiaryBrush");
        RepeatIcon.Glyph = _player.Repeat == RepeatMode.One ? FilledIcons.RepeatOne : FilledIcons.Repeat;
        ToolTipService.SetToolTip(RepeatButton, _player.Repeat switch { RepeatMode.All => "列表循环", RepeatMode.One => "单曲循环", _ => "循环关闭" });
        PreviewBadge.Visibility = _player.IsPreview || !string.IsNullOrEmpty(_player.Quality) ? Visibility.Visible : Visibility.Collapsed;
        PreviewBadge.Text = _player.IsPreview ? "试听" : _player.Quality switch { "standard" => "标准", "higher" => "较高", "exhigh" => "极高", "lossless" => "无损", "hires" => "Hi-Res", _ => "" };
        NowPlaying.UpdatePlaybackModes(_player.Shuffle, _player.Repeat);
        if (track is not null && track.Id != _displayedTrackId)
        {
            _displayedTrackId = track.Id;
            PlayerTitle.Text = track.Title;
            PlayerArtist.Text = track.Artist;
            SetImage(PlayerArtwork, track.ArtworkUrl);
            SyncTrackSelection();
            if (_startupReady) _ = LoadLyricsAsync(track.Id);
            UpdateFavorite();
            SaveSession();
        }
        if (track is not null && _player.IsPlaying && _rememberedTrackId != track.Id)
        {
            _rememberedTrackId = track.Id;
            _library.Remember(track);
        }
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, _player.Duration.TotalSeconds);
        SeekSlider.Value = Math.Clamp(_player.Position.TotalSeconds, 0, SeekSlider.Maximum);
        SeekSlider.IsEnabled = track is not null && !_player.IsLoading && _player.Duration.TotalSeconds > 0;
        _updatingSeek = false;
        PositionLabel.Text = FormatTime(_player.Position);
        DurationLabel.Text = FormatTime(_player.Duration);
        var signature = string.Join(",", _player.Queue.Select(item => item.Id));
        if (signature != _queueSignature) { _queueSignature = signature; QueueList.ItemsSource = _player.Queue.ToList(); QueueCountLabel.Text = $"{_player.Queue.Count} 首歌曲"; SaveSession(); }
        QueueList.SelectedItem = _player.Queue.FirstOrDefault(item => item.Id == track?.Id);
        QueueEmptyState.Visibility = _inspectorOpen && _queueVisible && _player.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightLyric();
        UpdateNowPlaying();
        RefreshTray();
    }

    private async Task LoadLyricsAsync(long id)
    {
        _lyricsRequest?.Cancel();
        _lyricsRequest?.Dispose();
        _lyricsRequest = new CancellationTokenSource();
        var token = _lyricsRequest.Token;
        _lyrics = Array.Empty<LyricLine>();
        _lyricIndex = -1;
        LyricsPanel.Children.Clear();
        LyricsPanel.Children.Add(new TextBlock { Text = "加载歌词…", Foreground = ThemeBrush("LumaTextTertiaryBrush"), TextWrapping = TextWrapping.Wrap });
        NowPlaying.SetLyrics(Array.Empty<LyricLine>(), "加载歌词…");
        try
        {
            var lyrics = await _client.GetLyricsAsync(id, token);
            token.ThrowIfCancellationRequested();
            _lyrics = lyrics;
            NowPlaying.SetLyrics(lyrics);
            LyricsPanel.Children.Clear();
            foreach (var line in lyrics)
            {
                var text = new TextBlock { Text = line.Text, FontSize = 19, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("LumaLyricMutedBrush") };
                var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 3, 0, 3), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Tag = line.TimeMs };
                button.Click += (_, _) => SeekTo(LyricTiming.ToPlaybackPosition(TimeSpan.FromMilliseconds(line.TimeMs),
                    _player.LyricOffset, _library.GetLyricOffsetMs(id), _player.Duration).TotalSeconds);
                LyricsPanel.Children.Add(button);
            }
            if (lyrics.Count == 0) LyricsPanel.Children.Add(new TextBlock { Text = "暂无歌词", FontSize = 18, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("LumaTextTertiaryBrush") });
            HighlightLyric();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && !_closed)
            {
                LyricsPanel.Children.Clear();
                LyricsPanel.Children.Add(new TextBlock { Text = "暂时无法加载歌词，重新播放可重试。", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("LumaTextTertiaryBrush") });
                NowPlaying.SetLyrics(Array.Empty<LyricLine>(), "暂时无法加载歌词，重新播放可重试。");
            }
        }
    }

    private void HighlightLyric()
    {
        if (_lyrics.Count == 0) return;
        int index = -1;
        var lyricClock = LyricTiming.ToLyricPosition(_player.GetLyricPosition(), _library.GetLyricOffsetMs(_player.CurrentTrack?.Id ?? 0));
        for (int i = 0; i < _lyrics.Count && _lyrics[i].TimeMs <= lyricClock.TotalMilliseconds; i++) index = i;
        if (index == _lyricIndex) return;
        if (_lyricIndex >= 0 && LyricsPanel.Children[_lyricIndex] is Button old && old.Content is TextBlock oldText) { oldText.Foreground = ThemeBrush("LumaLyricMutedBrush"); oldText.FontWeight = Microsoft.UI.Text.FontWeights.Normal; }
        _lyricIndex = index;
        if (index >= 0 && LyricsPanel.Children[index] is Button current && current.Content is TextBlock currentText)
        {
            currentText.Foreground = ThemeBrush("LumaLyricCurrentBrush");
            currentText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            if (Inspector.Visibility == Visibility.Visible && !_queueVisible && current.ActualHeight > 0)
            {
                var point = current.TransformToVisual(LyricsPanel).TransformPoint(new Point(0, 0));
                LyricsScroller.ChangeView(null, Math.Max(0, point.Y - LyricsScroller.ViewportHeight / 3), null);
            }
        }
    }

    private void SeekTo(double seconds)
    {
        NowPlaying.NotifySeek();
        _player.Seek(seconds);
    }
    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { if (!_updatingSeek && _player is not null) SeekTo(e.NewValue); }
    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_player is null) return;
        _player.Volume = e.NewValue / 100;
        _library.Volume = _player.Volume;
        VolumeIcon.Glyph = e.NewValue <= 0 ? FilledIcons.VolumeMute : FilledIcons.Volume;
    }
    private void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0) { _lastVolume = VolumeSlider.Value / 100; VolumeSlider.Value = 0; }
        else VolumeSlider.Value = _lastVolume * 100;
    }
    private void Shuffle_Click(object sender, RoutedEventArgs e) { _player.Shuffle = !_player.Shuffle; _library.Shuffle = _player.Shuffle; ShowToast(_player.Shuffle ? "已开启随机播放" : "已关闭随机播放"); }
    private void Repeat_Click(object sender, RoutedEventArgs e) { _player.Repeat = (RepeatMode)(((int)_player.Repeat + 1) % 3); _library.Repeat = _player.Repeat; }
    private void SetInspector(bool queue, bool toggle = false)
    {
        if (_closed) return;
        if (!queue) { OpenNowPlaying(); return; }
        _inspectorOpen = !(toggle && _inspectorOpen);
        _queueVisible = true;
        if (_exitLyricsShortcut is not null) _exitLyricsShortcut.IsEnabled = _immersiveOpen || _inspectorOpen;
        QueueCountLabel.Text = $"{_player.Queue.Count} 首歌曲";
        QueueEmptyState.Visibility = _inspectorOpen && _player.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = _motion.TransitionAsync(PageBody, !_inspectorOpen, offset: 0, durationMs: 240);
        _ = _motion.TransitionAsync(Inspector, _inspectorOpen, offset: 10, durationMs: _inspectorOpen ? 340 : 280);
        if (_inspectorOpen) QueueList.Focus(FocusState.Programmatic);
        else
        {
            QueueToggleButton.Focus(FocusState.Programmatic);
            QueueSearchViewportCheck();
        }
    }
    private void ToggleLyrics_Click(object sender, RoutedEventArgs e) => OpenNowPlaying();
    private void ToggleQueue_Click(object sender, RoutedEventArgs e) => SetInspector(true, true);
    private void LyricsTab_Click(object sender, RoutedEventArgs e) => SetInspector(false);
    private void QueueTab_Click(object sender, RoutedEventArgs e) => SetInspector(true);
    private void CloseInspector_Click(object sender, RoutedEventArgs e) { if (_inspectorOpen) SetInspector(_queueVisible, true); }
    private void UpdateNowPlaying()
    {
        NowPlaying.UpdatePlayback(_player.CurrentTrack, _player.IsPlaying, _player.IsLoading, _player.Position, _player.Duration,
            _player.IsPreview, _player.CurrentTrack is Track track && _library.IsFavorite(track.Id), _player.Volume,
            _player.LyricOffset.TotalSeconds, _library.GetLyricOffsetMs(_player.CurrentTrack?.Id ?? 0));
    }
    private void OpenNowPlaying()
    {
        if (_immersiveOpen) return;
        _immersiveOpen = true;
        _lyricsTransition++;
        _shellHitTesting.Clear();
        foreach (var child in Shell.Children)
        {
            if (ReferenceEquals(child, NowPlaying) || ReferenceEquals(child, Toast)) continue;
            _shellHitTesting[child] = child.IsHitTestVisible;
            child.IsHitTestVisible = false;
        }
        // Lyrics stay synchronized in LoadLyricsAsync. Reversing a close must
        // retain those visuals and the scroll position instead of rebuilding them.
        UpdateNowPlaying();
        _ = _motion.SlideAsync(NowPlaying, true, Math.Max(600, Root.ActualHeight), 560);
        SetTitleBar(NowPlaying.TitleBarRegion);
        UpdateCaptionTheme();
        _exitLyricsShortcut!.IsEnabled = true;
        NowPlaying.Focus(FocusState.Programmatic);
        SetLyricsRendering(true);
        UpdateCaptionInset();
    }
    private async void CloseNowPlaying()
    {
        if (!_immersiveOpen) return;
        _immersiveOpen = false;
        var transition = ++_lyricsTransition;
        if (_lyricsFullScreen) SetLyricsFullScreen(false);
        foreach (var item in _shellHitTesting) item.Key.IsHitTestVisible = item.Value;
        _shellHitTesting.Clear();
        if (_exitLyricsShortcut is not null) _exitLyricsShortcut.IsEnabled = _immersiveOpen || _inspectorOpen;
        SetTitleBar(DragRegion);
        PlayerInfoButton.Focus(FocusState.Programmatic);
        if (await _motion.SlideAsync(NowPlaying, false, Math.Max(600, Root.ActualHeight), 480) && !_closed && transition == _lyricsTransition)
        {
            SetLyricsRendering(false);
            UpdateCaptionTheme();
        }
    }
    private void SetLyricsRendering(bool enabled)
    {
        if (_lyricsRendering == enabled) return;
        _lyricsRendering = enabled;
        if (enabled) CompositionTarget.Rendering += LyricsFrame;
        else CompositionTarget.Rendering -= LyricsFrame;
    }
    private void LyricsFrame(object? sender, object e)
    {
        if (!_closed && NowPlaying.Visibility == Visibility.Visible) NowPlaying.UpdateLyricsPosition(_player.GetLyricPosition());
    }
    private void UpdateCaptionInset()
    {
        var scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d);
        NowPlaying.SetCaptionInset(AppWindow.TitleBar.RightInset / scale);
    }
    private void SetLyricsFullScreen(bool fullScreen)
    {
        if (_lyricsFullScreen == fullScreen) return;
        try
        {
            if (fullScreen)
            {
                SetTitleBar(null);
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                SetTitleBar(NowPlaying.Visibility == Visibility.Visible ? NowPlaying.TitleBarRegion : DragRegion);
            }
            _lyricsFullScreen = fullScreen;
            NowPlaying.SetFullScreen(fullScreen);
            UpdateCaptionInset();
        }
        catch (Exception ex) { ShowError("无法切换全屏", ex.Message); }
    }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateResponsiveLayout(); ResizeCards(); if (_loaded) UpdateCaptionInset(); }
    private async Task RestorePlaybackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = _library.LastSession;
            if (session.CurrentTrack is null && _library.Recent.FirstOrDefault() is Track recent)
                session = new PlaybackSessionSnapshot { CurrentTrack = recent, Queue = _library.Recent.Take(100).ToList() };
            if (session.CurrentTrack is not null && _player.CurrentTrack is null) await _player.RestoreSessionAsync(session, cancellationToken);
        }
        finally
        {
            _sessionReady = true;
            if (!_closed) _sessionTimer.Start();
        }
    }
    private void SaveSession()
    {
        if (!_sessionReady || _player.CurrentTrack is null) return;
        var position = _player.GetLyricPosition() - _player.LyricOffset;
        _library.SavePlaybackSession(_player.CurrentTrack, _player.Queue, position < TimeSpan.Zero ? TimeSpan.Zero : position);
    }
    private void SyncTrackSelection()
    {
        var id = _player.CurrentTrack?.Id;
        TrackList.SelectedItem = _tracks.FirstOrDefault(item => item.Id == id);
        HomeSongs.SelectedItem = (HomeSongs.ItemsSource as IEnumerable<Track>)?.FirstOrDefault(item => item.Id == id);
    }
    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string preference } || !Enum.TryParse<ThemePreference>(preference, out var theme)) return;
        _library.Theme = theme;
        ApplyTheme();
    }
    private void ApplyTheme()
    {
        Root.RequestedTheme = _library.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => IsSystemDark() ? ElementTheme.Dark : ElementTheme.Light
        };
        UpdateWindowTheme();
    }
    private bool IsSystemDark()
    {
        var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
        return foreground.R * 299 + foreground.G * 587 + foreground.B * 114 > 128000;
    }
    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed && _library.Theme == ThemePreference.System) ApplyTheme();
    });
    private void Root_ActualThemeChanged(FrameworkElement sender, object args) { if (_themeReady) UpdateWindowTheme(); }
    private Brush ThemeBrush(string key)
    {
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[Root.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
        return (Brush)palette[key];
    }
    private void UpdateWindowTheme()
    {
        UpdateCaptionTheme();
        UpdateFavorite();
        UpdateTrackHearts(TrackList);
        UpdateTrackHearts(HomeSongs);
        foreach (var child in LyricsPanel.Children)
        {
            if (child is Button { Content: TextBlock text }) text.Foreground = ThemeBrush("LumaLyricMutedBrush");
            else if (child is TextBlock status) status.Foreground = ThemeBrush("LumaTextTertiaryBrush");
        }
        _lyricIndex = -1;
        HighlightLyric();
        Player_StateChanged(this, EventArgs.Empty);
    }
    private void UpdateCaptionTheme()
    {
        var immersive = NowPlaying.Visibility == Visibility.Visible;
        AppWindow.TitleBar.ButtonForegroundColor = immersive ? Colors.White : ((SolidColorBrush)ThemeBrush("LumaTextPrimaryBrush")).Color;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = immersive ? Windows.UI.Color.FromArgb(150, 255, 255, 255) : ((SolidColorBrush)ThemeBrush("LumaTextTertiaryBrush")).Color;
    }
    private void AnimatePage(UIElement element)
    {
        _ = _motion.RevealAsync(element, offset: 10, durationMs: 360);
    }
    private void ResizeCards()
    {
        if (Root is null || HeroArtworkBorder is null) return;
        var width = Math.Max(1, Root.ActualWidth - SidebarColumn.Width.Value - HomeContent.Margin.Left - HomeContent.Margin.Right);
        ResizePlaylistCards(PlaylistGrid);
        ResizePlaylistCards(CloudPlaylistGrid);
        HeroArtworkBorder.Visibility = width < 650 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ShowError(string title, string message) { if (_closed) return; Notice.Title = title; Notice.Message = message; Notice.IsOpen = true; if (NowPlaying.Visibility == Visibility.Visible) NowPlaying.ShowMessage(message); }
    private void ShowToast(string text) { if (_closed) return; ToastText.Text = text; _motion.Show(Toast, offset: 10, durationMs: 300); _toastTimer.Stop(); _toastTimer.Start(); }
    private static string FormatTime(TimeSpan value) => $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private static void SetImage(Image image, string? url) { image.Source = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? new BitmapImage(uri) : null; }
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _tray?.Dispose();
        _startupLifetime.Cancel();
        StartupScreen.Stop();
        _pageRequest?.Cancel(); _pageRequest?.Dispose();
        _lyricsRequest?.Cancel(); _lyricsRequest?.Dispose();
        _accountLifetime.Cancel();
        _cloudRequest?.Cancel();
        _loginRequest?.Cancel();
        _toastTimer.Stop();
        SetLyricsRendering(false);
        _motion.Dispose();
        _sessionTimer.Stop();
        _uiSettings.ColorValuesChanged -= SystemColorsChanged;
        SaveSession();
        _library.Volume = _player.Volume; _library.Shuffle = _player.Shuffle; _library.Repeat = _player.Repeat;
        _player.Dispose();
        _client.Dispose();
    }
}
