using System;
using System.Runtime.InteropServices;

namespace winuitest.Services;

internal static class TrayNative
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint MessageFilter(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint WindowProcedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconIdentifier { internal uint Size; internal nint Window; internal uint Id; internal Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal Rect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal uint Size; internal Rect Monitor, Work; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PopupParameters { internal uint Size; internal Rect Exclude; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        internal uint Size, Mask, Type, State, Id;
        internal nint Submenu, CheckedBitmap, UncheckedBitmap;
        internal nuint Data;
        internal nint Text;
        internal uint TextLength;
        internal nint Bitmap;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuInfo
    {
        internal uint Size, Mask, Style, MaxHeight;
        internal nint Background;
        internal uint HelpId;
        internal nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MeasureItem
    {
        internal uint ControlType, ControlId, ItemId, Width, Height;
        internal nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawItem
    {
        internal uint ControlType, ControlId, ItemId, Action, State;
        internal nint Window, Dc;
        internal Rect Bounds;
        internal nuint Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct AccessibleMenuInfo
    {
        internal uint Signature, TextLength;
        internal nint Text;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LogFont
    {
        internal int Height, Width, Escapement, Orientation, Weight;
        internal byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FaceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NonClientMetrics
    {
        internal uint Size;
        internal int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        internal LogFont CaptionFont;
        internal int SmallCaptionWidth, SmallCaptionHeight;
        internal LogFont SmallCaptionFont;
        internal int MenuWidth, MenuHeight;
        internal LogFont MenuFont, StatusFont, MessageFont;
        internal int PaddedBorderWidth;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast { internal uint Size, Flags; internal nint Scheme; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Size { internal int Width, Height; }

    internal static uint TrackMenu(nint menu, nint owner, Rect anchor)
    {
        var point = new Point { X = anchor.Left + (anchor.Right - anchor.Left) / 2, Y = anchor.Top + (anchor.Bottom - anchor.Top) / 2 };
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromPoint(point, 2);
        var above = !GetMonitorInfo(monitor, ref info) || point.Y > info.Work.Top + (info.Work.Bottom - info.Work.Top) / 2;
        var alignRight = GetSystemMetrics(40) != 0; // SM_MENUDROPALIGNMENT honors the user's handedness.
        var x = alignRight ? anchor.Right : anchor.Left;
        var y = above ? anchor.Top : anchor.Bottom;
        // Native menus clamp to the monitor and avoid the tray icon. Animation is
        // Windows' own fade/slide and automatically honors reduced-motion settings.
        var flags = 0x100u | 0x2u | 0x40u | (alignRight ? 0x8u : 0u) | (above ? 0x20u | 0x2000u : 0x1000u);
        var parameters = new PopupParameters { Size = (uint)Marshal.SizeOf<PopupParameters>(), Exclude = anchor };
        return TrackPopupMenuEx(menu, flags, x, y, owner, ref parameters);
    }

    internal static bool ShowMenuOwner(nint owner, Rect anchor) =>
        SetWindowPos(owner, 0, anchor.Left, anchor.Top, 0, 0, 0x4 | 0x10 | 0x40);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] internal static extern int ShellNotifyIconGetRect(ref IconIdentifier identifier, out Rect rectangle);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo information);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool ModifyMenu(nint menu, uint id, uint flags, nuint newId, string text);
    [DllImport("user32.dll")] internal static extern uint EnableMenuItem(nint menu, uint id, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
    [DllImport("user32.dll")] internal static extern bool CheckMenuRadioItem(nint menu, uint first, uint last, uint selected, uint flags);
    [DllImport("user32.dll")] internal static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] internal static extern bool DeleteMenu(nint menu, uint position, uint flags);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, ref PopupParameters parameters);
    [DllImport("user32.dll")] internal static extern bool EndMenu();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetWindowsHookEx(int type, MessageFilter callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMenuItemInfo(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetMenuItemInfo(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetMenuInfo(nint menu, ref MenuInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SystemParametersInfoForDpi(uint action, uint parameter, ref NonClientMetrics value, uint flags, uint dpi);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rectangle, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(nint dc, string text, int length, ref Rect rectangle, uint format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFontIndirect(ref LogFont font);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int value);
    [DllImport("gdi32.dll")] internal static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll")] internal static extern int IntersectClipRect(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetTextExtentPoint32(nint dc, string text, int count, out Size size);
    [DllImport("gdi32.dll")] internal static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] internal static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
}
