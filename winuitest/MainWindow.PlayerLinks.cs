using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Foundation;

namespace winuitest;

public sealed partial class MainWindow
{
    private void InitializePlayerLinks()
    {
        PlayerInfoHost.LayoutUpdated += (_, _) => UpdatePlayerArtistBounds();
    }

    private void UpdatePlayerArtistBounds()
    {
        if (_closed || _immersiveOpen || !PlayerArtistSlot.IsLoaded || PlayerArtistSlot.ActualWidth <= 0) return;
        var origin = PlayerArtistSlot.TransformToVisual(PlayerInfoHost).TransformPoint(new Point());
        if (Math.Abs(Canvas.GetLeft(PlayerArtistButton) - origin.X) > 0.1 || double.IsNaN(Canvas.GetLeft(PlayerArtistButton)))
            Canvas.SetLeft(PlayerArtistButton, origin.X);
        if (Math.Abs(Canvas.GetTop(PlayerArtistButton) - origin.Y) > 0.1 || double.IsNaN(Canvas.GetTop(PlayerArtistButton)))
            Canvas.SetTop(PlayerArtistButton, origin.Y);
        if (Math.Abs(PlayerArtistButton.MaxWidth - PlayerArtistSlot.ActualWidth) > 0.1)
            PlayerArtistButton.MaxWidth = PlayerArtistSlot.ActualWidth;
    }

    private async void PlayerArtist_Click(object sender, RoutedEventArgs args) =>
        await OpenCurrentTrackArtistAsync(PlayerArtistButton);

    private void PlayerArtist_PointerEntered(object sender, PointerRoutedEventArgs args) =>
        VisualStateManager.GoToState(PlayerInfoButton, "PointerOver", true);

    private void PlayerArtist_PointerExited(object sender, PointerRoutedEventArgs args) =>
        VisualStateManager.GoToState(PlayerInfoButton, PlayerInfoButton.IsPointerOver ? "PointerOver" : "Normal", true);
}
