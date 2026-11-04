using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace winuitest.Services;

/// <summary>Owned HMENU tree. Windows provides placement, keyboard navigation, dismissal and animation.</summary>
internal sealed class TrayPopupMenu : IDisposable
{
    private const uint CommandStart = 100;
    private const uint OutputStart = 1000;
    private const uint Disabled = 0x1;
    private readonly Dictionary<uint, string> _outputs = new();
    private nint _root;
    private nint _navigation;
    private nint _audio;
    private TrayMenuAppearance? _appearance;
    private string _playPauseText;

    internal nint Handle => _root;
    internal nint AudioHandle => _audio;
    internal ElementTheme Theme { get; }

    internal TrayPopupMenu(TrayState state, IReadOnlyList<TrayAudioOutput>? outputs, bool outputsFailed)
    {
        Theme = state.Theme;
        _playPauseText = state.IsPlaying ? "暂停\tSpace" : "播放\tSpace";
        _root = Create();
        try
        {
            AddCommand(_root, TrayCommand.ShowWindow, "打开 Luma Music");
            TrayNative.SetMenuDefaultItem(_root, Id(TrayCommand.ShowWindow), false);
            Separator(_root);
            AddCommand(_root, TrayCommand.PlayPause, _playPauseText, state.CanControl && !state.IsLoading);
            AddCommand(_root, TrayCommand.Previous, "上一首\tCtrl+←", state.CanControl);
            AddCommand(_root, TrayCommand.Next, "下一首\tCtrl+→", state.CanControl);
            Separator(_root);
            _navigation = AddSubmenu("前往");
            AddCommand(_navigation, TrayCommand.NowPlaying, "全屏歌词", state.CanControl);
            AddCommand(_navigation, TrayCommand.Playlists, "我的歌单");
            AddCommand(_navigation, TrayCommand.Favorites, "喜欢的歌曲");
            AddCommand(_navigation, TrayCommand.Settings, "设置");
            _audio = AddSubmenu("输出设备");
            PopulateAudio(outputs, outputsFailed, state.SelectedAudioOutputId);
            Separator(_root);
            AddCommand(_root, TrayCommand.Exit, "退出 Luma Music");
        }
        catch { Dispose(); throw; }
    }

    internal void Refresh(TrayState state)
    {
        if (_root == 0) return;
        var text = state.IsPlaying ? "暂停\tSpace" : "播放\tSpace";
        if (text != _playPauseText)
        {
            var memory = Marshal.StringToHGlobalUni(text);
            try
            {
                // Keep owner-draw data and the native hover/keyboard selection.
                var info = new TrayNative.MenuItemInfo
                {
                    Size = (uint)Marshal.SizeOf<TrayNative.MenuItemInfo>(), Mask = 0x40, Text = memory
                };
                if (TrayNative.SetMenuItemInfo(_root, Id(TrayCommand.PlayPause), false, ref info))
                {
                    _playPauseText = text;
                    _appearance?.UpdateText(_root, Id(TrayCommand.PlayPause), text);
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        TrayNative.EnableMenuItem(_root, Id(TrayCommand.PlayPause), state.CanControl && !state.IsLoading ? 0u : Disabled);
        TrayNative.EnableMenuItem(_root, Id(TrayCommand.Previous), state.CanControl ? 0u : Disabled);
        TrayNative.EnableMenuItem(_root, Id(TrayCommand.Next), state.CanControl ? 0u : Disabled);
        TrayNative.EnableMenuItem(_navigation, Id(TrayCommand.NowPlaying), state.CanControl ? 0u : Disabled);
        UpdateAudioCheck(state.SelectedAudioOutputId);
    }

    internal void PopulateAudio(IReadOnlyList<TrayAudioOutput>? outputs, bool failed, string selected)
    {
        if (_audio == 0) return;
        for (var position = TrayNative.GetMenuItemCount(_audio) - 1; position >= 0; position--)
            if (!TrayNative.DeleteMenu(_audio, (uint)position, 0x400)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _appearance?.ReleaseMenu(_audio);
        _outputs.Clear();
        AddOutput("", "系统默认输出");
        if (outputs is null)
            Append(_audio, Disabled, 0, failed ? "暂时无法获取设备" : "正在查找设备…");
        else
            foreach (var output in outputs.Where(output => !string.IsNullOrEmpty(output.Id)).DistinctBy(output => output.Id))
                AddOutput(output.Id, MenuText(output.Name));
        UpdateAudioCheck(selected);
        _appearance?.Attach(_audio);
    }

    internal void PrepareAppearance(nint owner)
    {
        // High Contrast keeps Windows' own menu renderer and system palette.
        if (_appearance is not null || TrayMenuAppearance.IsHighContrast) return;
        _appearance = new TrayMenuAppearance(Theme == ElementTheme.Dark, Math.Max(96, TrayNative.GetDpiForWindow(owner)));
        _appearance.Attach(_root);
    }

    internal bool HandleMessage(uint message, nint parameter) => _appearance?.HandleMessage(message, parameter) == true;

    internal TrayCommand? GetCommand(uint id) => id >= CommandStart && id < CommandStart + (uint)Enum.GetValues<TrayCommand>().Length
        ? (TrayCommand)(id - CommandStart) : null;

    internal bool TryGetOutput(uint id, out string? output) => _outputs.TryGetValue(id, out output);

    private void UpdateAudioCheck(string selected)
    {
        var checkedId = _outputs.FirstOrDefault(pair => string.Equals(pair.Value, selected, StringComparison.OrdinalIgnoreCase)).Key;
        if (checkedId == 0) checkedId = OutputStart;
        TrayNative.CheckMenuRadioItem(_audio, OutputStart, OutputStart + (uint)_outputs.Count - 1, checkedId, 0);
    }

    private void AddOutput(string id, string name)
    {
        var command = OutputStart + (uint)_outputs.Count;
        _outputs.Add(command, id);
        Append(_audio, 0, command, name);
    }

    private nint AddSubmenu(string text)
    {
        var menu = Create();
        try { Append(_root, 0x10, (nuint)menu, text); }
        catch { TrayNative.DestroyMenu(menu); throw; }
        return menu;
    }

    private static nint Create()
    {
        var menu = TrayNative.CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return menu;
    }

    private static uint Id(TrayCommand command) => CommandStart + (uint)command;
    private static void Separator(nint menu) => Append(menu, 0x800, 0, null);
    private static void AddCommand(nint menu, TrayCommand command, string text, bool enabled = true) => Append(menu, enabled ? 0u : Disabled, Id(command), text);

    private static void Append(nint menu, uint flags, nuint command, string? text)
    {
        if (!TrayNative.AppendMenu(menu, flags, command, text)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static string MenuText(string text)
    {
        text = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 64) text = text[..(char.IsHighSurrogate(text[63]) ? 63 : 64)] + "…";
        return text.Replace("&", "&&", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_root != 0) TrayNative.DestroyMenu(_root); // Also owns both attached submenus.
        _root = _audio = _navigation = 0;
        _appearance?.Dispose();
        _appearance = null;
        _outputs.Clear();
    }
}
