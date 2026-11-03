using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using winuitest.Models;
using winuitest.Controls;
using winuitest.Services;

namespace winuitest;

public sealed partial class MainWindow
{
    private readonly AccountSessionStore _accountStore = new(App.DataDirectory);
    private readonly CancellationTokenSource _accountLifetime = new();
    private readonly List<MusicPlaylist> _cloudPlaylists = new();
    private CancellationTokenSource? _cloudRequest;
    private CancellationTokenSource? _loginRequest;
    private Task _accountRestoreTask = Task.CompletedTask;
    private AccountProfile? _account;
    private bool _loginDialogOpen;
    private bool _accountBusy;

    private async Task RestoreAccountAndPlaybackAsync()
    {
        await _accountRestoreTask;
        if (!_closed) await RestorePlaybackAsync();
    }

    private async Task RestoreAccountAsync()
    {
        try
        {
            var cookie = await _accountStore.LoadAsync(_accountLifetime.Token);
            if (_closed || string.IsNullOrWhiteSpace(cookie)) return;
            var result = await _client.SetSessionAsync(cookie, _accountLifetime.Token);
            if (_closed) return;
            _account = result.Profile;
            if (_account is null) await _accountStore.ClearAsync();
            UpdateAccountUi();
            if (_account is not null) _ = SyncCloudPlaylistsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A temporary outage must not erase a previously saved account.
            if (!_closed) { SidebarAccountCaption.Text = "登录状态待验证"; SettingsAccountLabel.Text = "连接后重新登录以验证账号"; }
        }
    }

    private void UpdateAccountUi()
    {
        var membership = _account?.VipType switch { 11 => "黑胶 VIP", > 0 => "网易云会员", _ => "网易云音乐" };
        SidebarAccountName.Text = _account?.Nickname ?? "登录网易云音乐";
        SidebarAccountCaption.Text = _account is null ? "同步你的音乐" : membership;
        SidebarAvatar.DisplayName = _account?.Nickname ?? "";
        CloudAvatar.DisplayName = _account?.Nickname ?? "";
        SidebarAvatar.Visibility = CloudAvatar.Visibility = _account is null ? Visibility.Collapsed : Visibility.Visible;
        SidebarGuestBadge.Visibility = CloudGuestBadge.Visibility = _account is null ? Visibility.Visible : Visibility.Collapsed;
        var avatar = Uri.TryCreate(_account?.AvatarUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? new BitmapImage(uri) : null;
        SidebarAvatar.ProfilePicture = avatar;
        CloudAvatar.ProfilePicture = avatar;
        CloudTitle.Text = _account is null ? "我的歌单" : _account.Nickname + "的音乐";
        CloudSubtitle.Text = _account is null ? "登录网易云音乐，随时听你的收藏" : membership;
        CloudAction.Content = _account is null ? "扫码登录" : "同步歌单";
        SettingsAccountLabel.Text = _account is null ? "未登录" : $"{_account.Nickname} · {membership}";
        SettingsAccountButton.Content = _account is null ? "扫码登录" : "我的歌单";
        LogoutButton.Visibility = _account is null ? Visibility.Collapsed : Visibility.Visible;
        CloudEmptyText.Text = _account is null ? "登录后显示创建和收藏的歌单" : "还没有歌单";
        CloudEmpty.Visibility = !_cloudLoading && _cloudPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCloudSidebar();
    }

    private void UpdateCloudSidebar()
    {
        UpdateSidebarPlaylists(_cloudPlaylists.Take(10));
        UpdateResponsiveLayout();
    }

    private async Task OpenCloudAsync()
    {
        BeginPage("cloud");
        UpdateAccountUi();
        ResizeCards();
        if (_cloudLoading) SetPageLoading(true);
        if (_account is not null && !_cloudLoading && _cloudPlaylists.Count == 0) await SyncCloudPlaylistsAsync();
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) await ShowLoginAsync();
        else await OpenCloudAsync();
    }

    private async void CloudAction_Click(object sender, RoutedEventArgs e)
    {
        if (_account is null) await ShowLoginAsync();
        else await SyncCloudPlaylistsAsync();
    }

    private async Task SyncCloudPlaylistsAsync()
    {
        if (_account is null || _closed) return;
        _cloudRequest?.Cancel();
        _cloudRequest?.Dispose();
        _cloudRequest = CancellationTokenSource.CreateLinkedTokenSource(_accountLifetime.Token);
        var token = _cloudRequest.Token;
        var uid = _account.Id;
        SetCloudLoading(true);
        CloudAction.IsEnabled = false;
        CloudNotice.IsOpen = false;
        try
        {
            var playlists = new List<MusicPlaylist>();
            var ids = new HashSet<long>();
            var offset = 0;
            while (true)
            {
                var result = await _client.GetUserPlaylistsAsync(uid, offset, 50, token);
                token.ThrowIfCancellationRequested();
                if (_account?.Id != uid) return;
                var added = 0;
                foreach (var playlist in result.Playlists)
                    if (ids.Add(playlist.Id)) { playlists.Add(playlist); added++; }
                offset += result.Playlists.Count;
                if (!result.More || result.Playlists.Count == 0 || added == 0) break;
            }
            _cloudPlaylists.Clear();
            _cloudPlaylists.AddRange(playlists);
            CloudPlaylistGrid.ItemsSource = _cloudPlaylists.ToList();
            UpdateAccountUi();
            CloudSubtitle.Text += $" · {_cloudPlaylists.Count} 个歌单";
            ResizeCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !_closed)
            {
                CloudNotice.Title = "同步失败";
                CloudNotice.Message = ex.Message;
                CloudNotice.IsOpen = true;
                await RecoverExpiredAccountAsync(uid, token);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested && !_closed)
            {
                SetCloudLoading(false);
                CloudAction.IsEnabled = true;
            }
        }
    }

    private async Task RecoverExpiredAccountAsync(long uid, CancellationToken token)
    {
        try
        {
            var result = await _client.GetAccountStatusAsync(token);
            token.ThrowIfCancellationRequested();
            if (_closed || _account?.Id != uid || result.Profile is not null) return;
            ClearAccountUi();
            try { await _accountStore.ClearAsync(token); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            CloudNotice.Title = "登录已过期";
            CloudNotice.Message = "请重新扫码登录。";
            CloudNotice.IsOpen = true;
            await _player.ReloadCurrentAsync();
        }
        // Network failure is not proof of expiry; retain the account and encrypted credential.
        catch (Exception) { }
    }

    private void ClearAccountUi()
    {
        _account = null;
        _cloudPlaylists.Clear();
        CloudPlaylistGrid.ItemsSource = null;
        CloudNotice.IsOpen = false;
        SetCloudLoading(false);
        CloudAction.IsEnabled = true;
        UpdateAccountUi();
    }

    private async Task ShowLoginAsync()
    {
        if (_loginDialogOpen || _accountBusy || _closed) return;
        _loginDialogOpen = true;
        try
        {
            await _accountRestoreTask;
            if (_closed) return;
            if (_account is not null) { await OpenCloudAsync(); return; }
            var previousCookie = await _accountStore.LoadAsync(_accountLifetime.Token);
            if (_closed) return;
            var qrImage = new Image { Width = 208, Height = 208, Stretch = Stretch.Uniform };
            var qrBusy = new ProgressRing { Width = 32, Height = 32, IsActive = true };
            var qrContainer = new Grid { Width = 232, Height = 232, Background = new SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(12), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center };
            qrContainer.Children.Add(qrImage);
            qrContainer.Children.Add(qrBusy);
            var status = new TextBlock { Text = "正在获取二维码…", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MinHeight = 40, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var refresh = new Button { Content = "刷新二维码", HorizontalAlignment = HorizontalAlignment.Center };
            var content = new Grid { RowSpacing = 16, MinWidth = 280, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
            for (var row = 0; row < 4; row++) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = "使用网易云音乐 App 扫码", FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Grid.SetRow(qrContainer, 1);
            Grid.SetRow(status, 2);
            Grid.SetRow(refresh, 3);
            content.Children.Add(qrContainer);
            content.Children.Add(status);
            content.Children.Add(refresh);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "登录网易云音乐", Content = content, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            Task loginAttempt = Task.CompletedTask;
            async Task RunAfterPreviousAsync(Task previous, CancellationToken token)
            {
                // A refreshed QR must wait for cancellation rollback of the previous attempt.
                // Otherwise a late old attempt could clear the newly accepted login.
                try
                {
                    await previous;
                    token.ThrowIfCancellationRequested();
                    await RunQrLoginAsync(dialog, qrImage, qrBusy, status, refresh, previousCookie, token);
                }
                catch (OperationCanceledException) { }
            }
            void StartAttempt()
            {
                _loginRequest?.Cancel();
                _loginRequest?.Dispose();
                _loginRequest = CancellationTokenSource.CreateLinkedTokenSource(_accountLifetime.Token);
                loginAttempt = RunAfterPreviousAsync(loginAttempt, _loginRequest.Token);
            }
            refresh.Click += (_, _) => StartAttempt();
            dialog.Closing += (_, _) => _loginRequest?.Cancel();
            var shown = dialog.ShowAsync();
            StartAttempt();
            await shown;
            _loginRequest?.Cancel();
            await loginAttempt;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) ShowError("登录未完成", ex.Message); }
        finally
        {
            _loginRequest?.Cancel();
            _loginRequest?.Dispose();
            _loginRequest = null;
            _loginDialogOpen = false;
        }
    }

    private async Task RunQrLoginAsync(ContentDialog dialog, Image image, ProgressRing busy, TextBlock status, Button refresh, string? previousCookie, CancellationToken token)
    {
        busy.IsActive = true;
        busy.Visibility = Visibility.Visible;
        image.Source = null;
        image.Opacity = 1;
        refresh.IsEnabled = false;
        status.Text = "正在获取二维码…";
        var sessionMayHaveChanged = false;
        var persisted = false;
        var committed = false;
        try
        {
            var qr = await _client.CreateQrLoginAsync(token);
            token.ThrowIfCancellationRequested();
            if (!qr.QrImage.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("二维码加载失败，请刷新重试。");
            var bytes = Convert.FromBase64String(qr.QrImage[(qr.QrImage.IndexOf(',') + 1)..]);
            using (var stream = new InMemoryRandomAccessStream())
            {
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                token.ThrowIfCancellationRequested();
                image.Source = bitmap;
            }
            busy.IsActive = false;
            busy.Visibility = Visibility.Collapsed;
            refresh.IsEnabled = true;
            status.Text = "等待扫码";
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(2000, token);
                var result = await _client.CheckQrLoginAsync(qr.Key, token);
                token.ThrowIfCancellationRequested();
                if (result.Code == 800) { status.Text = "二维码已过期，请刷新"; image.Opacity = 0.25; return; }
                if (result.Code == 802) status.Text = "已扫码，请在手机上确认";
                else if (result.Code == 801) status.Text = "等待扫码";
                else if (result.Code == 803)
                {
                    if (string.IsNullOrWhiteSpace(result.Cookie)) throw new InvalidOperationException("未收到登录凭据，请重新扫码。");
                    refresh.IsEnabled = false;
                    status.Text = "正在同步账号…";
                    sessionMayHaveChanged = true;
                    var session = await _client.SetSessionAsync(result.Cookie, token);
                    token.ThrowIfCancellationRequested();
                    if (session.Profile is null) throw new InvalidOperationException("登录已失效，请重新扫码。");
                    string? persistenceError = null;
                    try { await _accountStore.SaveAsync(result.Cookie, token); persisted = true; }
                    catch (OperationCanceledException) { throw; }
                    catch { persistenceError = "本次登录成功，但未能保存登录状态。"; }
                    token.ThrowIfCancellationRequested();
                    if (_closed) return;
                    _account = session.Profile;
                    committed = true;
                    UpdateAccountUi();
                    dialog.Hide();
                    ShowToast(persistenceError ?? "已登录网易云音乐");
                    _ = SyncCloudPlaylistsAsync();
                    await _player.ReloadCurrentAsync();
                    return;
                }
            }
            status.Text = "二维码已过期，请刷新";
            image.Opacity = 0.25;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) { status.Text = ex.Message; image.Source = null; } }
        finally
        {
            if (sessionMayHaveChanged && !committed)
            {
                // Cancelling the parent request cannot cancel an upstream SDK request already
                // running. authResume invalidates that request's session revision in the child.
                try { await _client.ResetSessionAsync(); }
                catch (Exception) { }
                if (persisted)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(previousCookie)) await _accountStore.ClearAsync();
                        else await _accountStore.SaveAsync(previousCookie);
                    }
                    catch (Exception) { }
                }
            }
            if (!token.IsCancellationRequested)
            {
                busy.IsActive = false;
                busy.Visibility = Visibility.Collapsed;
                refresh.IsEnabled = true;
            }
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_accountBusy) return;
        _accountBusy = true;
        LogoutButton.IsEnabled = false;
        _cloudRequest?.Cancel();
        ClearAccountUi();
        string? failure = null;
        try
        {
            try { await _accountStore.ClearAsync(); }
            catch (Exception) { failure = "未能删除本机登录状态，请稍后重试。"; }
            try { await _client.LogoutAsync(_accountLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception) { failure ??= "本机已退出，网易云暂时无法确认退出状态。"; }
            if (_closed) return;
            if (failure is null) ShowToast("已退出登录");
            else ShowError("已退出本次登录", failure);
            await _player.ReloadCurrentAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("退出登录未完成", ex.Message); }
        finally { _accountBusy = false; if (!_closed) LogoutButton.IsEnabled = true; }
    }

    private async void QualitySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeReady || QualitySelector.SelectedItem is not ComboBoxItem { Tag: string quality }) return;
        _library.PlaybackQuality = quality;
        _client.PreferredQuality = quality;
        await _player.ReloadCurrentAsync();
    }
}
