using System.Runtime.InteropServices;
using System.Text;

namespace TC.Tools.Unlock.Win;

internal static class Native
{
    // ------------------------------------------------------------- desktops
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    public const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetUserObjectInformationW(
        IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    // ---------------------------------------------------------------- input
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint MAPVK_VK_TO_VSC = 0;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // The union must carry the largest member (MOUSEINPUT = 32 bytes on x64) so
    // that sizeof(INPUT) is exactly what SendInput validates against.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScanExW(char ch, IntPtr dwhkl);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WTSGetActiveConsoleSessionIdSafe();

    // ---------------------------------------------------- console (run --quiet)
    public const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Hides our own console window; used by `run --quiet` (autostart).</summary>
    internal static void HideConsoleWindow()
    {
        try
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) ShowWindow(handle, SW_HIDE);
        }
        catch (Exception) { }
    }
}

/// <summary>Result of the §5.2 lock check.</summary>
public readonly record struct DesktopLockInfo(bool Locked, string DesktopName, string Detail);

/// <summary>
/// §5.2: "OpenInputDesktop 失败或桌面名 != Default ⇒ 判定为锁定/安全桌面".
/// </summary>
public static class SessionDesktop
{
    public static DesktopLockInfo Query()
    {
        IntPtr h = Native.OpenInputDesktop(
            0, false, Native.DESKTOP_READOBJECTS | Native.DESKTOP_SWITCHDESKTOP);

        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return new DesktopLockInfo(true, "(unavailable)", $"OpenInputDesktop failed, Win32 error {err}");
        }

        try
        {
            var sb = new StringBuilder(256);
            if (!Native.GetUserObjectInformationW(h, Native.UOI_NAME, sb, (uint)(sb.Capacity * sizeof(char)), out _))
            {
                int err = Marshal.GetLastWin32Error();
                return new DesktopLockInfo(true, "(unknown)", $"GetUserObjectInformation failed, Win32 error {err}");
            }

            string name = sb.ToString();
            bool locked = !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
            return new DesktopLockInfo(locked, name, locked ? $"input desktop is '{name}'" : "input desktop is 'Default'");
        }
        finally
        {
            Native.CloseDesktop(h);
        }
    }
}
