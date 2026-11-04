using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace winuitest.Services;

/// <summary>Colors an HMENU using documented owner-draw messages; USER32 still owns interaction.</summary>
internal sealed class TrayMenuAppearance : IDisposable
{
    private sealed class Entry : IDisposable
    {
        internal readonly nint Menu;
        internal readonly uint Position, Type;
        internal readonly nint Data = Marshal.AllocHGlobal(Marshal.SizeOf<TrayNative.AccessibleMenuInfo>());
        internal nint AccessibleText;
        internal string Text = "";

        internal Entry(nint menu, uint position, uint type, string text)
        {
            Menu = menu; Position = position; Type = type;
            SetText(text);
        }

        internal void SetText(string text)
        {
            var name = text.Replace("&&", "&", StringComparison.Ordinal).Replace('\t', ' ');
            var memory = Marshal.StringToHGlobalUni(name);
            if (AccessibleText != 0) Marshal.FreeHGlobal(AccessibleText);
            AccessibleText = memory;
            Text = text;
            // MSAAMENUINFO is the first (and only) structure in itemData. Native
            // menu accessibility continues to expose names, states and actions.
            Marshal.StructureToPtr(new TrayNative.AccessibleMenuInfo
            {
                Signature = 0xAA0DF00D, TextLength = (uint)name.Length, Text = memory
            }, Data, false);
        }

        public void Dispose()
        {
            if (AccessibleText != 0) Marshal.FreeHGlobal(AccessibleText);
            Marshal.FreeHGlobal(Data);
        }
    }

    private readonly Dictionary<nint, Entry> _entries = new();
    private readonly double _scale;
    private readonly uint _text, _muted;
    private nint _background, _hover, _line, _foreground, _font, _boldFont;
    private readonly int _rowHeight;

    internal static bool IsHighContrast
    {
        get
        {
            var value = new TrayNative.HighContrast { Size = (uint)Marshal.SizeOf<TrayNative.HighContrast>() };
            return TrayNative.SystemParametersInfo(0x42, value.Size, ref value, 0) && (value.Flags & 1) != 0;
        }
    }

    internal TrayMenuAppearance(bool dark, uint dpi)
    {
        _scale = Math.Max(1, dpi) / 96d;
        _text = Color(dark ? 0xF5F5F7u : 0x242429u);
        _muted = Color(dark ? 0x9999A5u : 0x85858Du);
        try
        {
            _background = Brush(dark ? 0x29292Fu : 0xF8F8FAu);
            _hover = Brush(dark ? 0x3B3B43u : 0xE8E8ECu);
            _line = Brush(dark ? 0x45454Eu : 0xDCDCE2u);
            _foreground = Brush(dark ? 0xF5F5F7u : 0x242429u);
            var metrics = new TrayNative.NonClientMetrics { Size = (uint)Marshal.SizeOf<TrayNative.NonClientMetrics>() };
            var font = TrayNative.SystemParametersInfoForDpi(0x29, metrics.Size, ref metrics, 0, dpi)
                ? metrics.MenuFont : new TrayNative.LogFont { Height = -Px(14), Weight = 400, CharacterSet = 1, FaceName = "Segoe UI" };
            _rowHeight = Math.Max(Px(32), Math.Abs(font.Height) + Px(14));
            _font = TrayNative.CreateFontIndirect(ref font);
            font.Weight = 600;
            _boldFont = TrayNative.CreateFontIndirect(ref font);
            if (_font == 0 || _boldFont == 0) throw new Win32Exception();
        }
        catch { Dispose(); throw; }
    }

    internal void Attach(nint menu)
    {
        var background = new TrayNative.MenuInfo
        {
            Size = (uint)Marshal.SizeOf<TrayNative.MenuInfo>(), Mask = 0x2, Background = _background
        };
        if (!TrayNative.SetMenuInfo(menu, ref background)) throw new Win32Exception(Marshal.GetLastWin32Error());
        for (uint position = 0; position < TrayNative.GetMenuItemCount(menu); position++)
        {
            var info = new TrayNative.MenuItemInfo
            {
                Size = (uint)Marshal.SizeOf<TrayNative.MenuItemInfo>(), Mask = 0x100 | 0x40 | 0x4
            };
            if (!TrayNative.GetMenuItemInfo(menu, position, true, ref info)) throw new Win32Exception();
            var memory = Marshal.AllocHGlobal(checked((int)(info.TextLength + 1) * 2));
            try
            {
                info.Text = memory;
                info.TextLength++;
                if (!TrayNative.GetMenuItemInfo(menu, position, true, ref info)) throw new Win32Exception();
                var entry = new Entry(menu, position, info.Type, Marshal.PtrToStringUni(memory) ?? "");
                _entries.Add(entry.Data, entry);
                info.Mask = 0x100 | 0x20;
                info.Type |= 0x100; // MFT_OWNERDRAW; retain separator/radio semantics.
                info.Data = (nuint)entry.Data;
                if (!TrayNative.SetMenuItemInfo(menu, position, true, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (info.Submenu != 0) Attach(info.Submenu);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }

    internal void ReleaseMenu(nint menu)
    {
        foreach (var entry in _entries.Values.Where(value => value.Menu == menu).ToArray())
        {
            _entries.Remove(entry.Data);
            entry.Dispose();
        }
    }

    internal void UpdateText(nint menu, uint id, string text)
    {
        var info = new TrayNative.MenuItemInfo { Size = (uint)Marshal.SizeOf<TrayNative.MenuItemInfo>(), Mask = 0x20 };
        if (TrayNative.GetMenuItemInfo(menu, id, false, ref info)
            && _entries.TryGetValue((nint)info.Data, out var entry) && entry.Text != text) entry.SetText(text);
    }

    internal bool HandleMessage(uint message, nint parameter)
    {
        if (parameter == 0) return false;
        if (message == 0x2C) // WM_MEASUREITEM
        {
            var item = Marshal.PtrToStructure<TrayNative.MeasureItem>(parameter);
            if (item.ControlType != 1 || !_entries.TryGetValue((nint)item.Data, out var entry)) return false;
            item.Height = (uint)(((entry.Type & 0x800) != 0) ? Px(11) : _rowHeight);
            var dc = TrayNative.GetDC(0);
            if (dc == 0) return false;
            var saved = TrayNative.SaveDC(dc);
            try
            {
                TrayNative.SelectObject(dc, _boldFont); // Reserve enough width for the default command.
                var parts = entry.Text.Split('\t', 2);
                TrayNative.GetTextExtentPoint32(dc, parts[0], parts[0].Length, out var label);
                var shortcutWidth = 0;
                if (parts.Length > 1)
                {
                    TrayNative.GetTextExtentPoint32(dc, parts[1], parts[1].Length, out var shortcut);
                    shortcutWidth = shortcut.Width + Px(28);
                }
                item.Width = (uint)Math.Clamp(label.Width + shortcutWidth + Px(60), Px(224), Px(520));
            }
            finally { TrayNative.RestoreDC(dc, saved); TrayNative.ReleaseDC(0, dc); }
            Marshal.StructureToPtr(item, parameter, false);
            return true;
        }
        if (message != 0x2B) return false; // WM_DRAWITEM
        var draw = Marshal.PtrToStructure<TrayNative.DrawItem>(parameter);
        if (draw.ControlType != 1 || !_entries.TryGetValue((nint)draw.Data, out var target)) return false;
        Draw(draw, target);
        return true;
    }

    private void Draw(TrayNative.DrawItem item, Entry entry)
    {
        var dc = item.Dc;
        var saved = TrayNative.SaveDC(dc);
        try
        {
            var bounds = item.Bounds;
            TrayNative.IntersectClipRect(dc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            TrayNative.FillRect(dc, ref bounds, _background);
            if ((entry.Type & 0x800) != 0)
            {
                var y = (bounds.Top + bounds.Bottom) / 2;
                var line = new TrayNative.Rect(bounds.Left + Px(12), y, bounds.Right - Px(12), y + 1);
                TrayNative.FillRect(dc, ref line, _line);
                return;
            }
            var disabled = (item.State & (0x2 | 0x4)) != 0;
            if ((item.State & 1) != 0 && !disabled)
            {
                TrayNative.SelectObject(dc, TrayNative.GetStockObject(8)); // NULL_PEN
                TrayNative.SelectObject(dc, _hover);
                TrayNative.RoundRect(dc, bounds.Left + Px(4), bounds.Top + Px(2), bounds.Right - Px(4), bounds.Bottom - Px(2), Px(8), Px(8));
            }
            TrayNative.SetBkMode(dc, 1);
            TrayNative.SetTextColor(dc, disabled ? _muted : _text);
            TrayNative.SelectObject(dc, (item.State & 0x1000) != 0 ? _boldFont : _font);
            var textBounds = new TrayNative.Rect(bounds.Left + Px(30), bounds.Top, bounds.Right - Px(24), bounds.Bottom);
            var parts = entry.Text.Split('\t', 2);
            const uint textFormat = 0x20 | 0x4 | 0x8000; // SINGLELINE | VCENTER | END_ELLIPSIS
            if (parts.Length > 1)
            {
                TrayNative.GetTextExtentPoint32(dc, parts[1], parts[1].Length, out var shortcut);
                var shortcutBounds = textBounds;
                TrayNative.DrawText(dc, parts[1], parts[1].Length, ref shortcutBounds, textFormat | 0x2);
                textBounds.Right -= shortcut.Width + Px(24);
            }
            TrayNative.DrawText(dc, parts[0], parts[0].Length, ref textBounds, textFormat);
            if ((item.State & 0x8) != 0)
            {
                // Native CheckMenuRadioItem retains the selected output state.
                // Render its dot in the same palette as the item text.
                var x = bounds.Left + Px(16);
                var y = (bounds.Top + bounds.Bottom) / 2;
                TrayNative.SelectObject(dc, TrayNative.GetStockObject(8));
                TrayNative.SelectObject(dc, _foreground);
                TrayNative.Ellipse(dc, x - Px(3), y - Px(3), x + Px(3), y + Px(3));
            }
            // USER32 supplies the submenu arrow and keeps its native placement.
        }
        finally { TrayNative.RestoreDC(dc, saved); }
    }

    private int Px(double value) => Math.Max(1, (int)Math.Round(value * _scale));
    private static uint Color(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16);
    private static nint Brush(uint rgb)
    {
        var brush = TrayNative.CreateSolidBrush(Color(rgb));
        return brush != 0 ? brush : throw new Win32Exception();
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Dispose();
        _entries.Clear();
        foreach (var value in new[] { _background, _hover, _line, _foreground, _font, _boldFont })
            if (value != 0) TrayNative.DeleteObject(value);
        _background = _hover = _line = _foreground = _font = _boldFont = 0;
    }
}
