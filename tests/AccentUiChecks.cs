using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using winuitest.Models;

namespace winuitest;

public sealed partial class MainWindow
{
    // Run on the native fixture's realized track list, while its paused current song is visible.
    private async Task RunAccentUiChecksAsync(List<string> checks, CancellationToken token)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Accent UI: " + message);
        }

        static T? Find<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
        {
            if (parent is T match && predicate(match)) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find(VisualTreeHelper.GetChild(parent, i), predicate) is { } child) return child;
            return null;
        }

        static Color BrushColor(Brush? brush) => brush is SolidColorBrush solid ? solid.Color
            : throw new InvalidOperationException("Accent UI: missing realized solid brush.");

        async Task SettleAsync()
        {
            Root.UpdateLayout();
            await Task.Delay(70, token);
        }

        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 180 };
        var toggle = new ToggleSwitch { IsOn = true };
        var host = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        host.Children.Add(slider);
        host.Children.Add(toggle);
        var previousAccent = _library.AccentColor;
        var previousTheme = Root.RequestedTheme;
        Root.Children.Add(host);

        (Color Slider, Color Toggle, Color Navigation, Color Title) ReadActual()
        {
            var rail = Find<Rectangle>(toggle, rect => rect.Name == "SwitchKnobBounds")
                ?? throw new InvalidOperationException("Accent UI: native toggle rail missing.");
            var indicator = Find<Rectangle>(MainNavigation, rect => rect.Name == "SelectionIndicator")
                ?? throw new InvalidOperationException("Accent UI: native navigation indicator missing.");
            var current = _tracks.First(track => track.Id == _player.CurrentTrack?.Id);
            var row = (ListViewItem)TrackList.ContainerFromItem(current);
            var title = Find<TextBlock>(row, text => Equals(text.Tag, "TrackTitle"))!;
            return (BrushColor(slider.Foreground), BrushColor(rail.Fill), BrushColor(indicator.Fill), BrushColor(title.Foreground));
        }

        void AssertCustom(string hex)
        {
            var palette = CreateAccentColors(ParseAccent(hex), Root.ActualTheme == ElementTheme.Dark);
            var expected = palette["LumaAccentBrush"];
            var colors = ReadActual();
            Require(colors.Slider == expected, $"realized Slider {colors.Slider} expected {expected}");
            Require(colors.Toggle == expected, $"realized ToggleSwitch {colors.Toggle} expected {expected}");
            Require(colors.Navigation == expected, $"realized NavigationView {colors.Navigation} expected {expected}");
            Require(colors.Title == palette["LumaAccentTextBrush"], "current track foreground stale");
        }

        async Task PickAsync(string hex, string action)
        {
            CustomAccent_Click(CustomAccentButton, new RoutedEventArgs());
            var dialog = _accentDialog ?? throw new InvalidOperationException("Accent UI: color dialog did not open.");
            for (var i = 0; i < 40 && (!dialog.IsLoaded || Find<Button>(dialog, button => button.Name == action) is null); i++)
                await Task.Delay(25, token);
            var picker = Find<ColorPicker>(dialog, _ => true)!;
            picker.Color = ParseAccent(hex);
            var button = Find<Button>(dialog, element => element.Name == action)!;
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            for (var i = 0; i < 60 && _accentDialogOpen; i++) await Task.Delay(25, token);
            Require(!_accentDialogOpen, "color dialog did not close");
            await SettleAsync();
        }

        try
        {
            SetAccentPreference("");
            Root.RequestedTheme = ElementTheme.Dark;
            await SettleAsync();
            var original = ReadActual();
            if (_accentHighContrast)
            {
                SetAccentPreference("#078278");
                await SettleAsync();
                Require(ReadActual() == original, "High Contrast system colors changed");
                checks.Add("accent-high-contrast-system-colors");
                return;
            }
            var preset = AccentPresetPanel.Children.OfType<ToggleButton>().First(button => Equals(button.Tag, "#078278"));
            var provider = (IToggleProvider)new ToggleButtonAutomationPeer(preset).GetPattern(PatternInterface.Toggle);
            provider.Toggle();
            await SettleAsync();
            Require(_library.AccentColor == "#078278" && preset.IsChecked == true, "preset activation or persistence failed");
            AssertCustom("#078278");
            checks.Add("accent-preset-realized-native-controls");

            await PickAsync("#BA7019", "CloseButton");
            Require(_library.AccentColor == "#078278", "cancel committed the picker draft");
            await PickAsync("#BA7019", "PrimaryButton");
            Require(_library.AccentColor == "#BA7019", "custom color was not saved");
            AssertCustom("#BA7019");
            Root.RequestedTheme = ElementTheme.Light;
            await SettleAsync();
            AssertCustom("#BA7019");
            Root.RequestedTheme = ElementTheme.Dark;
            await SettleAsync();
            AssertCustom("#BA7019");
            checks.Add("accent-custom-dialog-theme-switch-and-current-track");

            await PickAsync("#BA7019", "SecondaryButton");
            Require(_library.AccentColor == "" && ReadActual() == original, "default palette was not restored");
            checks.Add("accent-picker-cancel-and-default-restore");
        }
        finally
        {
            _accentDialog?.Hide();
            Root.Children.Remove(host);
            SetAccentPreference(previousAccent);
            Root.RequestedTheme = previousTheme;
        }
    }
}
