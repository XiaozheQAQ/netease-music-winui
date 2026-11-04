using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace winuitest.Services;

internal static class TrayNative
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

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

    internal static void SetRoundedCorners(nint window)
    {
        var preference = 2;
        DwmSetWindowAttribute(window, 33, ref preference, sizeof(int));
    }

    internal static bool ForegroundBelongsTo(nint window)
    {
        for (var current = GetForegroundWindow(); current != 0; current = GetWindow(current, 4))
            if (current == window) return true;
        return false;
    }

    internal static void PositionMenu(Window window, Rect anchor, int widthDip, int heightDip)
    {
        var middle = new Point { X = anchor.Left + (anchor.Right - anchor.Left) / 2, Y = anchor.Top + (anchor.Bottom - anchor.Top) / 2 };
        var monitor = MonitorFromPoint(middle, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        if (GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0) dpi = monitorDpi;
        var scale = Math.Max(1, dpi) / 96d;
        var gap = (int)Math.Round(8 * scale);
        var width = Math.Min((int)Math.Round(widthDip * scale), info.Work.Right - info.Work.Left - 2 * gap);
        var height = Math.Min((int)Math.Round(heightDip * scale), info.Work.Bottom - info.Work.Top - 2 * gap);
        var x = anchor.Right - width;
        var y = anchor.Top - height - gap;
        if (anchor.Bottom <= info.Work.Top + gap) y = anchor.Bottom + gap;
        else if (anchor.Left >= info.Work.Right - gap) { x = anchor.Left - width - gap; y = middle.Y - height / 2; }
        else if (anchor.Right <= info.Work.Left + gap) { x = anchor.Right + gap; y = middle.Y - height / 2; }
        x = Math.Clamp(x, info.Work.Left + gap, Math.Max(info.Work.Left + gap, info.Work.Right - width - gap));
        y = Math.Clamp(y, info.Work.Top + gap, Math.Max(info.Work.Top + gap, info.Work.Bottom - height - gap));
        window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
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
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo information);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, uint type, out uint dpiX, out uint dpiY);
}
