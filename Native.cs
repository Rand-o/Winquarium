using System.Runtime.InteropServices;

namespace AquariumSaver;

internal static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", CallingConvention = CallingConvention.Winapi)] public static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", CallingConvention = CallingConvention.Winapi)] public static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_CHILD = 0x40000000U, WS_VISIBLE = 0x10000000U;
    public const uint WM_DISPLAYCHANGE = 0x007E;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
}
