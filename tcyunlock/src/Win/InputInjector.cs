using System.Runtime.InteropServices;

namespace TC.Tools.Unlock.Win;

/// <summary>One key press, already resolved to a virtual-key + scan code.</summary>
public readonly record struct KeyStroke(ushort Vk, ushort Scan, bool Shift, bool Extended);

/// <summary>
/// A fully pre-validated injection. §5.3 requires that an unsupported character
/// aborts the WHOLE injection ("不得部分输入"), so planning is separated from
/// sending: <see cref="InputInjector.Plan"/> either maps every character or
/// fails, and only then does <see cref="InputInjector.Send"/> touch SendInput.
/// </summary>
public sealed class InjectPlan
{
    public List<KeyStroke> Strokes { get; } = new();
    public string? Unsupported { get; init; }
    public bool Ok => Unsupported is null;

    public static InjectPlan Fail(string detail) => new() { Unsupported = detail };
}

public static class InputInjector
{
    private const ushort VK_TAB = 0x09;
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_NEXT = 0x22;
    private const ushort VK_END = 0x23;
    private const ushort VK_HOME = 0x24;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_DIVIDE = 0x6F;
    private const ushort VK_NUMLOCK = 0x90;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_RMENU = 0xA5;

    private static bool IsExtended(ushort vk) => vk switch
    {
        VK_PRIOR or VK_NEXT or VK_END or VK_HOME or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN
            or VK_INSERT or VK_DELETE or VK_DIVIDE or VK_NUMLOCK or VK_RCONTROL or VK_RMENU => true,
        _ => false,
    };

    /// <summary>
    /// Maps every character of the password to a keystroke. The password is
    /// never included in any error text; only the character index is reported.
    /// </summary>
    public static InjectPlan Plan(string password)
    {
        var plan = new InjectPlan();
        for (int i = 0; i < password.Length; i++)
        {
            char c = password[i];

            // Surrogate pairs (emoji etc.) cannot be typed by a keyboard layout.
            if (char.IsSurrogate(c))
                return InjectPlan.Fail($"character #{i} is a surrogate (not typeable by a keyboard layout)");

            short scan = Native.VkKeyScanW(c);
            if (scan == -1)
                return InjectPlan.Fail($"character #{i} (U+{(int)c:X4}) has no key on the current keyboard layout");

            int vk = scan & 0xFF;
            int state = (scan >> 8) & 0xFF;

            // Bit 0 = Shift, bit 1 = Ctrl, bit 2 = Alt (AltGr sets Ctrl+Alt).
            // We only reproduce Shift exactly; Ctrl/Alt combinations are refused
            // rather than guessed, because a wrong guess types a different char.
            if ((state & 0x06) != 0)
                return InjectPlan.Fail($"character #{i} (U+{(int)c:X4}) requires Ctrl/Alt (AltGr), unsupported");

            uint vsc = Native.MapVirtualKeyW((uint)vk, Native.MAPVK_VK_TO_VSC);
            if (vsc == 0)
                return InjectPlan.Fail($"character #{i} (U+{(int)c:X4}) has no scan code");

            plan.Strokes.Add(new KeyStroke((ushort)vk, (ushort)vsc, (state & 0x01) != 0, IsExtended((ushort)vk)));
        }

        // Trailing Enter, exactly as §5.3 requires.
        uint returnScan = Native.MapVirtualKeyW(Native.VK_RETURN, Native.MAPVK_VK_TO_VSC);
        plan.Strokes.Add(new KeyStroke(Native.VK_RETURN, (ushort)returnScan, false, false));
        return plan;
    }

    /// <summary>Sends a pre-validated plan. Returns false + detail on failure.</summary>
    public static bool Send(InjectPlan plan, int keyDelayMs, out string detail)
    {
        detail = string.Empty;
        if (!plan.Ok)
        {
            detail = plan.Unsupported ?? "unsupported";
            return false;
        }

        var inputs = new List<Native.INPUT>(plan.Strokes.Count * 4);
        foreach (KeyStroke s in plan.Strokes)
        {
            if (keyDelayMs > 0 && inputs.Count > 0)
            {
                // Small gap between keys: some logon UIs drop burst-injected input.
                Thread.Sleep(keyDelayMs);
            }

            if (s.Shift)
            {
                inputs.Add(Key(Native.VK_SHIFT, (ushort)Native.MapVirtualKeyW(Native.VK_SHIFT, Native.MAPVK_VK_TO_VSC), false, false));
            }

            inputs.Add(Key(s.Vk, s.Scan, false, s.Extended));
            inputs.Add(Key(s.Vk, s.Scan, true, s.Extended));

            if (s.Shift)
            {
                inputs.Add(Key(Native.VK_SHIFT, (ushort)Native.MapVirtualKeyW(Native.VK_SHIFT, Native.MAPVK_VK_TO_VSC), true, false));
            }
        }

        int size = Marshal.SizeOf<Native.INPUT>();
        if (size != 40 && size != 28)
        {
            detail = $"unexpected sizeof(INPUT)={size}";
            return false;
        }

        uint sent = Native.SendInput((uint)inputs.Count, inputs.ToArray(), size);
        if (sent != inputs.Count)
        {
            int err = Marshal.GetLastWin32Error();
            detail = $"SendInput sent {sent}/{inputs.Count} events, Win32 error {err}";
            return false;
        }

        detail = $"sent {plan.Strokes.Count} keys ({inputs.Count} events)";
        return true;
    }

    private static Native.INPUT Key(ushort vk, ushort scan, bool up, bool extended)
    {
        uint flags = 0;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;

        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
    }
}
