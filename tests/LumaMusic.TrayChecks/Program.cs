using System.Runtime.InteropServices;
using winuitest.Services;

// No tray icon, visible window, input simulation, audio or network is involved.
// Exercise the production WM_MEASUREITEM/WM_DRAWITEM handlers on a memory DC.
internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Run(false, 96);
        Run(true, 96);
        var small = MeasureAtDpi(96);
        var large = MeasureAtDpi(144);
        Check(large.Height > small.Height * 1.3 && large.Width > small.Width * 1.3, "DPI-aware menu measurement");
        var before = GetGuiResources(GetCurrentProcess(), 0);
        for (var index = 0; index < 20; index++) Run(index % 2 == 0, 96, quiet: true);
        var after = GetGuiResources(GetCurrentProcess(), 0);
        Check(after <= before + 1, $"GDI objects released ({before} → {after})");
        Console.WriteLine($"PASSED: {_checks} offline native menu checks");
    }

    private static void Run(bool dark, uint dpi, bool quiet = false)
    {
        var root = TrayNative.CreatePopupMenu();
        var child = TrayNative.CreatePopupMenu();
        Check(root != 0 && child != 0, "native HMENU allocation", quiet);
        using var appearance = new TrayMenuAppearance(dark, dpi);
        try
        {
            TrayNative.AppendMenu(root, 0, 100, "打开 Luma Music");
            TrayNative.SetMenuDefaultItem(root, 100, false);
            TrayNative.AppendMenu(root, 0, 101, "播放\tSpace");
            TrayNative.AppendMenu(root, 1, 102, "上一首\tCtrl+←");
            TrayNative.AppendMenu(root, 0x800, 0, null);
            TrayNative.AppendMenu(root, 0x10, (nuint)child, "输出设备");
            TrayNative.AppendMenu(child, 0, 1000, "系统默认输出");
            TrayNative.AppendMenu(child, 0, 1001, "耳机 && 扬声器");
            TrayNative.CheckMenuRadioItem(child, 1000, 1001, 1000, 0);
            appearance.Attach(root);

            var opening = Item(root, 0);
            var play = Item(root, 1);
            var unavailable = Item(root, 2);
            var separator = Item(root, 3);
            var submenu = Item(root, 4);
            var output = Item(child, 0);
            Check((play.Type & 0x100) != 0 && (unavailable.State & 3) != 0 && (output.State & 8) != 0,
                "owner draw preserves native disabled and selected output states", quiet);
            Check(submenu.Submenu == child && TrayNative.GetMenuItemCount(child) == 2 && (output.Type & 0x100) != 0,
                "submenu identity and appearance remain intact", quiet);
            var accessibility = Marshal.PtrToStructure<TrayNative.AccessibleMenuInfo>((nint)play.Data);
            Check(accessibility.Signature == 0xAA0DF00D && Marshal.PtrToStringUni(accessibility.Text) == "播放 Space",
                "MSAA accessible menu name", quiet);
            var escaped = Marshal.PtrToStructure<TrayNative.AccessibleMenuInfo>((nint)Item(child, 1).Data);
            Check(Marshal.PtrToStringUni(escaped.Text) == "耳机 & 扬声器", "escaped ampersand accessible name", quiet);
            var caption = Marshal.StringToHGlobalUni("暂停\tSpace");
            try
            {
                var update = new TrayNative.MenuItemInfo { Size = (uint)Marshal.SizeOf<TrayNative.MenuItemInfo>(), Mask = 0x40, Text = caption };
                Check(TrayNative.SetMenuItemInfo(root, 101, false, ref update) && (Item(root, 1).Type & 0x100) != 0
                    && Item(root, 1).Data == play.Data, "native caption update preserves owner-draw data", quiet);
            }
            finally { Marshal.FreeHGlobal(caption); }
            appearance.UpdateText(root, 101, "暂停\tSpace");
            accessibility = Marshal.PtrToStructure<TrayNative.AccessibleMenuInfo>((nint)play.Data);
            Check(Marshal.PtrToStringUni(accessibility.Text) == "暂停 Space", "live playback name refresh", quiet);

            using var target = new MemoryTarget(640, 80);
            var background = Color(dark ? 0x29292Fu : 0xF8F8FAu);
            var foreground = Color(dark ? 0xF5F5F7u : 0x242429u);
            var muted = Color(dark ? 0x9999A5u : 0x85858Du);
            var hover = Color(dark ? 0x3B3B43u : 0xE8E8ECu);
            Draw(appearance, target, opening, 0x1000);
            Check(GetPixel(target.Dc, 2, 2) == background, "resting palette background", quiet);
            Check(Contains(target, foreground, 30, 280), "foreground matches palette", quiet);
            Draw(appearance, target, play, 1);
            Check(GetPixel(target.Dc, 12, 12) == hover && GetPixel(target.Dc, 1, 1) == background,
                "rounded hover and outer background", quiet);
            Draw(appearance, target, unavailable, 0x4);
            Check(Contains(target, muted, 30, 280), "disabled text contrast", quiet);
            Draw(appearance, target, output, 0x8);
            Check(GetPixel(target.Dc, 16, 20) == foreground, "checked output dot", quiet);
            Draw(appearance, target, separator, 0);
            Check(GetPixel(target.Dc, 15, 20) == Color(dark ? 0x45454Eu : 0xDCDCE2u), "separator palette", quiet);
            Draw(appearance, target, submenu, 1);
            Check(GetPixel(target.Dc, 12, 12) == hover, "submenu item hover palette", quiet);
            Check(GetCurrentObject(target.Dc, 6) == target.Font, "drawing restores original GDI font", quiet);

            // Device enumeration replaces only the child's entries, never the HMENU.
            for (var index = TrayNative.GetMenuItemCount(child) - 1; index >= 0; index--) TrayNative.DeleteMenu(child, (uint)index, 0x400);
            appearance.ReleaseMenu(child);
            TrayNative.AppendMenu(child, 0, 1000, "新的默认设备");
            appearance.Attach(child);
            Check((Item(child, 0).Type & 0x100) != 0 && Item(root, 4).Submenu == child,
                "audio device rebuild preserves owner draw", quiet);
        }
        finally { TrayNative.DestroyMenu(root); }
    }

    private static TrayNative.MeasureItem MeasureAtDpi(uint dpi)
    {
        var menu = TrayNative.CreatePopupMenu();
        using var appearance = new TrayMenuAppearance(true, dpi);
        try
        {
            TrayNative.AppendMenu(menu, 0, 1, "播放\tSpace");
            appearance.Attach(menu);
            var value = new TrayNative.MeasureItem { ControlType = 1, ItemId = 1, Data = Item(menu, 0).Data };
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrayNative.MeasureItem>());
            try
            {
                Marshal.StructureToPtr(value, memory, false);
                Check(appearance.HandleMessage(0x2C, memory), "WM_MEASUREITEM handled");
                return Marshal.PtrToStructure<TrayNative.MeasureItem>(memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { TrayNative.DestroyMenu(menu); }
    }

    private static TrayNative.MenuItemInfo Item(nint menu, uint position)
    {
        var value = new TrayNative.MenuItemInfo { Size = (uint)Marshal.SizeOf<TrayNative.MenuItemInfo>(), Mask = 0x100 | 0x20 | 0x1 | 0x4 | 0x2 };
        if (!TrayNative.GetMenuItemInfo(menu, position, true, ref value)) throw new InvalidOperationException("GetMenuItemInfo failed");
        return value;
    }

    private static void Draw(TrayMenuAppearance appearance, MemoryTarget target, TrayNative.MenuItemInfo info, uint state)
    {
        var value = new TrayNative.DrawItem
        {
            ControlType = 1, ItemId = info.Id, Action = 1, State = state, Dc = target.Dc,
            Bounds = new TrayNative.Rect(0, 0, 400, 40), Data = info.Data
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrayNative.DrawItem>());
        try
        {
            Marshal.StructureToPtr(value, memory, false);
            if (!appearance.HandleMessage(0x2B, memory)) throw new InvalidOperationException("WM_DRAWITEM unhandled");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static bool Contains(MemoryTarget target, uint color, int left, int right)
    {
        for (var y = 5; y < 36; y++)
            for (var x = left; x < right; x++)
                if (GetPixel(target.Dc, x, y) == color) return true;
        return false;
    }

    private static void Check(bool condition, string label, bool quiet = false)
    {
        if (!condition) throw new InvalidOperationException(label);
        if (!quiet) { _checks++; Console.WriteLine("PASS " + label); }
    }

    private static uint Color(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16);

    private sealed class MemoryTarget : IDisposable
    {
        internal readonly nint Dc, Font;
        private readonly nint _bitmap, _oldBitmap;

        internal MemoryTarget(int width, int height)
        {
            var screen = TrayNative.GetDC(0);
            try
            {
                Dc = CreateCompatibleDC(screen);
                _bitmap = CreateCompatibleBitmap(screen, width, height);
                if (Dc == 0 || _bitmap == 0) throw new InvalidOperationException("Memory GDI allocation failed");
                _oldBitmap = TrayNative.SelectObject(Dc, _bitmap);
                Font = GetCurrentObject(Dc, 6);
            }
            finally { TrayNative.ReleaseDC(0, screen); }
        }

        public void Dispose()
        {
            TrayNative.SelectObject(Dc, _oldBitmap);
            TrayNative.DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
    }

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint GetCurrentObject(nint dc, uint type);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
}
