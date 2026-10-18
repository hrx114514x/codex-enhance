using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace CodexEnhance;
internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    internal static void MakeIndependent(IntPtr window)
    {
        // A foreign owner implicitly attaches the two GUI threads' input queues.
        // Follow only geometry/visibility; never set Codex as owner or parent.
        SetWindowLongPtr(window, -8, IntPtr.Zero);
        SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x4000);
    }
    internal static bool ShouldShowNearHost(IntPtr host, bool hiddenByUser) =>
        !hiddenByUser && host != IntPtr.Zero && IsWindow(host) && IsWindowVisible(host) && !IsIconic(host);
    internal static void PlaceAboveHost(IntPtr window, IntPtr host)
    {
        if (window == IntPtr.Zero || host == IntPtr.Zero || IsAbove(window, host)) return;
        var previous = GetWindow(host, 3);
        SetWindowPos(window, previous, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x4000);
    }
    internal static IntPtr FindSingleHost()
    {
        var matches = new List<IntPtr>();
        EnumWindows((window, _) => {
            if (IsHostWindow(window)) matches.Add(window);
            return true;
        }, IntPtr.Zero);
        return matches.Count == 1 ? matches[0] : IntPtr.Zero;
    }
    internal static bool IsHostWindow(IntPtr window)
    {
        var title = new StringBuilder(256);
        // Native tray menus/dialogs must not become the geometry-following host.
        return IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero &&
            (GetWindowLongPtr(window, -20).ToInt64() & 0x80) == 0 &&
            GetWindowRect(window, out var bounds) && bounds.Width > 300 && bounds.Height > 200 &&
            GetWindowText(window, title, title.Capacity) > 0 && IsCodex(window);
    }
    internal static bool IsAbove(IntPtr window, IntPtr other)
    {
        if (window == IntPtr.Zero || other == IntPtr.Zero || window == other) return false;
        for (int i = 0; i < 1000 && (other = GetWindow(other, 3)) != IntPtr.Zero; i++) if (other == window) return true;
        return false;
    }
    internal static bool IsCodex(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return HostProcessIdentity.IsCodex(pid);
    }
}
