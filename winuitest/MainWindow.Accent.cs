using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace winuitest;

public sealed partial class MainWindow
{
    private static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("默认玫红", ""), ("珊瑚", "#E26546"), ("琥珀", "#BC890E"),
        ("青绿", "#078278"), ("海蓝", "#2478C8"), ("紫罗兰", "#8F62CE")
    };
    private readonly Dictionary<(ResourceDictionary Dictionary, string Key), Color> _originalAccentColors = new();
    private readonly AccessibilitySettings _accentAccessibility = new();
    private bool _accentHighContrast;
    private bool _accentContrastSubscribed;
    private bool _updatingAccentResources;
    private bool _syncingAccentButtons;
    private bool _accentDialogOpen;
    private ContentDialog? _accentDialog;

    private void InitializeAccentSettings()
    {
        _accentHighContrast = _accentAccessibility.HighContrast;
        try
        {
            _accentAccessibility.HighContrastChanged += AccentHighContrastChanged;
            _accentContrastSubscribed = true;
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490))
        {
            // Some unpackaged desktop hosts expose HighContrast but no event source.
            // UISettings.ColorValuesChanged and fresh reads cover this host.
        }
        Closed += (_, _) =>
        {
            if (_accentContrastSubscribed)
            {
                _accentAccessibility.HighContrastChanged -= AccentHighContrastChanged;
                _accentContrastSubscribed = false;
            }
            _accentDialog?.Hide();
        };
        foreach (var (name, hex) in AccentPresets)
        {
            var color = ParseAccent(hex);
            var button = new ToggleButton
            {
                Tag = hex, Width = 32, Height = 32, MinHeight = 0, MinWidth = 0,
                CornerRadius = new CornerRadius(16), Padding = new Thickness(3),
                Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(color) }
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Checked += (_, _) => { if (!_syncingAccentButtons) SetAccentPreference(hex); };
            button.Unchecked += (_, _) => { if (!_syncingAccentButtons) RefreshAccentSettings(); };
            AccentPresetPanel.Children.Add(button);
        }
        if (_library.AccentColor.Length > 0) UpdateAccentResources(_library.AccentColor);
        RefreshAccentSettings();
    }

    private void AccentHighContrastChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        _accentHighContrast = sender.HighContrast;
        UpdateWindowTheme();
    });

    private bool IsAccentHighContrast => _accentContrastSubscribed ? _accentHighContrast
        : (_accentHighContrast = _accentAccessibility.HighContrast);

    private void RefreshAccentSettings()
    {
        var selected = _library.AccentColor;
        var preset = false;
        _syncingAccentButtons = true;
        try
        {
            foreach (var element in AccentPresetPanel.Children)
            {
                if (element is not ToggleButton { Tag: string hex } button) continue;
                button.IsChecked = string.Equals(selected, hex, StringComparison.OrdinalIgnoreCase);
                preset |= button.IsChecked == true;
            }
        }
        finally { _syncingAccentButtons = false; }
        CustomAccentButton.Content = preset ? "自定义…" : selected + "…";
        ToolTipService.SetToolTip(CustomAccentButton, "选择自定义主色");
    }

    private async void CustomAccent_Click(object sender, RoutedEventArgs args)
    {
        if (_closed || _accentDialogOpen || _loginDialogOpen || _shortcutsDialogOpen || _trackDialogOpen) return;
        _accentDialogOpen = true;
        try
        {
            var picker = new ColorPicker
            {
                Color = ParseAccent(_library.AccentColor), IsAlphaEnabled = false, IsHexInputVisible = true,
                IsMoreButtonVisible = true, HorizontalAlignment = HorizontalAlignment.Center
            };
            _accentDialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "自定义主色",
                PrimaryButtonText = "应用", SecondaryButtonText = "恢复默认", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new ScrollViewer { Content = picker, HorizontalScrollMode = ScrollMode.Disabled, MaxHeight = 440 }
            };
            var result = await _accentDialog.ShowAsync();
            if (_closed || result == ContentDialogResult.None) return;
            var color = picker.Color;
            SetAccentPreference(result == ContentDialogResult.Secondary ? "" : $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        }
        finally { _accentDialog = null; _accentDialogOpen = false; }
    }

    private void SetAccentPreference(string hex)
    {
        if (_library.AccentColor != hex)
        {
            _library.AccentColor = hex;
            UpdateAccentResources(_library.AccentColor);
            UpdateWindowTheme();
            if (_library.LastSaveError is { } error) ShowError("主色未能保存", error);
        }
        RefreshAccentSettings();
    }

    private void UpdateAccentResources(string hex)
    {
        var app = Application.Current.Resources;
        var selected = ParseAccent(hex);
        var restore = hex.Length == 0;
        _updatingAccentResources = true;
        try
        {
            var changes = new List<(ResourceDictionary Dictionary, Dictionary<string, Color> Colors)>();
            foreach (var name in new[] { "Light", "Dark", "Default" })
            {
                var dark = name != "Light";
                var colors = CreateAccentColors(selected, dark);
                if (app.ThemeDictionaries.TryGetValue(name, out var palette) && palette is ResourceDictionary dictionary)
                    changes.Add((dictionary, colors));
                // WinUI control keys (SliderTrackValueFill, NavigationView indicators,
                // AccentButtonBackground, etc.) alias shared brushes in these dictionaries.
                foreach (var merged in app.MergedDictionaries)
                    if (merged.ThemeDictionaries.TryGetValue(name, out var native) && native is ResourceDictionary controls)
                        changes.Add((controls, colors));
            }
            // Save the entire original palette first: dictionary aliases may point
            // to the same brush and must not snapshot an already-modified color.
            foreach (var (dictionary, colors) in changes)
                foreach (var key in colors.Keys)
                    if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush or Color)
                        _originalAccentColors.TryAdd((dictionary, key), value is SolidColorBrush brush ? brush.Color : (Color)value);
            foreach (var (dictionary, colors) in changes) SetPaletteColors(dictionary, colors, restore);
            // Brush.Color updates its existing consumers directly. Value-type Color
            // resources such as gradient stops require a ThemeResource lookup. Resolve
            // both theme assignments in one UI turn, before the next rendered frame.
            if (Root.IsLoaded && !IsAccentHighContrast)
            {
                var requested = Root.RequestedTheme;
                Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                Root.RequestedTheme = requested;
            }
        }
        finally { _updatingAccentResources = false; }
    }

    private void SetPaletteColors(ResourceDictionary palette, Dictionary<string, Color> colors, bool restore)
    {
        foreach (var (key, color) in colors)
        {
            if (!palette.TryGetValue(key, out var value) || value is not (SolidColorBrush or Color)) continue;
            var identity = (palette, key);
            var oldColor = value is SolidColorBrush oldBrush ? oldBrush.Color : (Color)value;
            var result = restore ? _originalAccentColors[identity] : color;
            if (oldColor.Equals(result)) continue;
            if (value is SolidColorBrush brush) brush.Color = result;
            else palette[key] = result;
        }
    }

    private static Dictionary<string, Color> CreateAccentColors(Color selected, bool dark)
    {
        var paper = dark ? Color.FromArgb(255, 41, 41, 47) : Color.FromArgb(255, 246, 246, 248);
        var fill = EnsureContrast(selected, paper, dark, 3.0);
        var text = EnsureContrast(fill, paper, dark, 4.5);
        var onFill = Contrast(fill, Colors.Black) > Contrast(fill, Colors.White) ? Colors.Black : Colors.White;
        return new Dictionary<string, Color>
        {
            ["SystemAccentColor"] = fill,
            ["SystemAccentColorLight1"] = Mix(fill, Colors.White, .12),
            ["SystemAccentColorLight2"] = dark ? fill : Mix(fill, Colors.White, .40),
            ["SystemAccentColorLight3"] = dark ? text : Mix(fill, Colors.White, .68),
            ["SystemAccentColorDark1"] = dark ? Mix(fill, Colors.Black, .15) : fill,
            ["SystemAccentColorDark2"] = dark ? Mix(fill, Colors.Black, .30) : text,
            ["SystemAccentColorDark3"] = Mix(dark ? fill : text, Colors.Black, .48),
            ["LumaAccentBrush"] = fill, ["LumaAccentTextBrush"] = text, ["LumaOnAccentBrush"] = onFill,
            ["LumaLyricCurrentBrush"] = text, ["LumaLyricCompletedBrush"] = text,
            ["LumaHeroStartColor"] = Mix(paper, selected, dark ? .27 : .14),
            ["LumaHeroMidColor"] = Mix(paper, selected, dark ? .19 : .23),
            ["LumaHeroEndColor"] = Mix(paper, selected, dark ? .11 : .32),
            ["AccentFillColorDefaultBrush"] = fill, ["AccentFillColorSecondaryBrush"] = fill,
            ["AccentFillColorTertiaryBrush"] = fill, ["AccentFillColorSelectedTextBackgroundBrush"] = fill,
            ["AccentTextFillColorPrimaryBrush"] = text, ["AccentTextFillColorSecondaryBrush"] = text,
            ["AccentTextFillColorTertiaryBrush"] = text, ["SystemFillColorAttentionBrush"] = fill,
            ["TextOnAccentFillColorPrimaryBrush"] = onFill, ["TextOnAccentFillColorSelectedTextBrush"] = onFill,
            ["TextOnAccentFillColorSecondaryBrush"] = Color.FromArgb(0xDC, onFill.R, onFill.G, onFill.B),
            ["SystemControlHighlightAccentBrush"] = fill
        };
    }

    private static Color ParseAccent(string hex)
    {
        if (hex.Length != 7 || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            rgb = 0xD83B5A;
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static Color EnsureContrast(Color color, Color surface, bool lighten, double target)
    {
        if (Contrast(color, surface) >= target) return color;
        var end = lighten ? Colors.White : Colors.Black;
        for (var step = 1; step <= 100; step++)
        {
            var result = Mix(color, end, step / 100d);
            if (Contrast(result, surface) >= target) return result;
        }
        return end;
    }

    private static Color Mix(Color first, Color second, double amount)
    {
        byte Blend(byte start, byte end) => (byte)Math.Round(start + (end - start) * amount);
        return Color.FromArgb(255, Blend(first.R, second.R), Blend(first.G, second.G), Blend(first.B, second.B));
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            var s = channel / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        static double Luminance(Color value) => .2126 * Linear(value.R) + .7152 * Linear(value.G) + .0722 * Linear(value.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
