// Virtual-key members keep their SDK VK_* names (minus the prefix) so they match the ExplorerPolicy grammar,
// which puts a few of them (e.g. Decimal) on CA1720's type-name list.
#pragma warning disable CA1720 // identifiers should not contain type names

using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;

using ClubShell.Contracts.Pcs;

namespace ClubShell.Windows.Hooks;

/// <summary>
/// Keyboard modifiers, as a set of flags. Matches the <c>Ctrl|Alt|Shift|Win</c> tokens used in the
/// <see cref="ExplorerPolicy.BlockedKeyCombos"/> grammar.
/// </summary>
[Flags]
public enum Modifiers
{
    /// <summary>No modifier held.</summary>
    None = 0,

    /// <summary>Either Control key.</summary>
    Ctrl = 1,

    /// <summary>Either Alt (Menu) key.</summary>
    Alt = 2,

    /// <summary>Either Shift key.</summary>
    Shift = 4,

    /// <summary>Either Windows key.</summary>
    Win = 8,
}

/// <summary>
/// Windows virtual keys, named as in the SDK <c>VK_*</c> constants but without the <c>VK_</c> prefix
/// (the grammar used by <see cref="ExplorerPolicy.BlockedKeyCombos"/>). The backing value is the Win32
/// virtual-key code, so a raw <c>vkCode</c> from a keyboard hook casts directly to this enum.
/// </summary>
public enum VirtualKey
{
    /// <summary>No key / wildcard (matches any key under a set of modifiers).</summary>
    None = 0,

    LButton = 0x01,
    RButton = 0x02,
    Cancel = 0x03,
    MButton = 0x04,
    XButton1 = 0x05,
    XButton2 = 0x06,
    Back = 0x08,
    Tab = 0x09,
    Clear = 0x0C,
    Return = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Menu = 0x12,
    Pause = 0x13,
    Capital = 0x14,
    Escape = 0x1B,
    Space = 0x20,
    Prior = 0x21,
    Next = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Snapshot = 0x2C,
    Insert = 0x2D,
    Delete = 0x2E,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    LWin = 0x5B,
    RWin = 0x5C,
    Apps = 0x5D,
    Sleep = 0x5F,
    Numpad0 = 0x60,
    Numpad1 = 0x61,
    Numpad2 = 0x62,
    Numpad3 = 0x63,
    Numpad4 = 0x64,
    Numpad5 = 0x65,
    Numpad6 = 0x66,
    Numpad7 = 0x67,
    Numpad8 = 0x68,
    Numpad9 = 0x69,
    Multiply = 0x6A,
    Add = 0x6B,
    Separator = 0x6C,
    Subtract = 0x6D,
    Decimal = 0x6E,
    Divide = 0x6F,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    F13 = 0x7C,
    F14 = 0x7D,
    F15 = 0x7E,
    F16 = 0x7F,
    F17 = 0x80,
    F18 = 0x81,
    F19 = 0x82,
    F20 = 0x83,
    F21 = 0x84,
    F22 = 0x85,
    F23 = 0x86,
    F24 = 0x87,
    NumLock = 0x90,
    Scroll = 0x91,
    LShift = 0xA0,
    RShift = 0xA1,
    LControl = 0xA2,
    RControl = 0xA3,
    LMenu = 0xA4,
    RMenu = 0xA5,
    BrowserBack = 0xA6,
    BrowserForward = 0xA7,
    BrowserRefresh = 0xA8,
    BrowserStop = 0xA9,
    BrowserSearch = 0xAA,
    BrowserFavorites = 0xAB,
    BrowserHome = 0xAC,
    VolumeMute = 0xAD,
    VolumeDown = 0xAE,
    VolumeUp = 0xAF,
    MediaNextTrack = 0xB0,
    MediaPrevTrack = 0xB1,
    MediaStop = 0xB2,
    MediaPlayPause = 0xB3,
    LaunchMail = 0xB4,
    LaunchMediaSelect = 0xB5,
    LaunchApp1 = 0xB6,
    LaunchApp2 = 0xB7,
    Oem1 = 0xBA,
    OemPlus = 0xBB,
    OemComma = 0xBC,
    OemMinus = 0xBD,
    OemPeriod = 0xBE,
    Oem2 = 0xBF,
    Oem3 = 0xC0,
    Oem4 = 0xDB,
    Oem5 = 0xDC,
    Oem6 = 0xDD,
    Oem7 = 0xDE,
}

/// <summary>
/// A key combination: a single <see cref="VirtualKey"/> plus the modifiers that must be held with it.
/// A combo whose <see cref="Key"/> is <see cref="VirtualKey.None"/> is a wildcard that matches any key
/// pressed while exactly <see cref="Mods"/> are held (e.g. <c>Win+*</c> for "any Windows-key shortcut").
/// </summary>
/// <param name="Key">The main key, or <see cref="VirtualKey.None"/> for a modifier-only wildcard.</param>
/// <param name="Mods">The modifiers that must be held (excluding the main key when it is itself a modifier).</param>
public readonly record struct KeyCombo(VirtualKey Key, Modifiers Mods)
{
    /// <summary>Parses a combo such as <c>"Ctrl+Alt+Del"</c>; throws <see cref="FormatException"/> when invalid.</summary>
    public static KeyCombo Parse(string text) =>
        TryParse(text, out KeyCombo combo) ? combo : throw new FormatException($"'{text}' is not a valid key combination.");

    /// <summary>
    /// Attempts to parse a combo. Accepts the modifier tokens <c>Ctrl/Control</c>, <c>Alt/Menu</c>,
    /// <c>Shift</c> and <c>Win/Windows/Meta</c> joined with <c>+</c>, plus one key token (a <c>VK_*</c>
    /// name without the prefix, a letter, a digit, or a common alias such as <c>Del</c>, <c>Esc</c>,
    /// <c>Enter</c>, <c>PgUp</c>, <c>PgDn</c>, <c>PrintScreen</c>). A modifier-only string parses to a
    /// wildcard combo (<see cref="VirtualKey.None"/>).
    /// </summary>
    public static bool TryParse(string? text, out KeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        Modifiers mods = Modifiers.None;
        VirtualKey key = VirtualKey.None;
        bool keySet = false;
        foreach (string part in parts)
        {
            if (TryParseModifier(part, out Modifiers m))
            {
                mods |= m;
                continue;
            }

            if (keySet || !TryParseKey(part, out key))
            {
                return false;
            }

            keySet = true;
        }

        combo = new KeyCombo(key, mods);
        return true;
    }

    /// <summary>Canonical string form: modifiers in the fixed order Ctrl, Alt, Shift, Win, then the key name.</summary>
    public override string ToString()
    {
        StringBuilder sb = new();
        if ((Mods & Modifiers.Ctrl) != 0)
        {
            sb.Append("Ctrl+");
        }

        if ((Mods & Modifiers.Alt) != 0)
        {
            sb.Append("Alt+");
        }

        if ((Mods & Modifiers.Shift) != 0)
        {
            sb.Append("Shift+");
        }

        if ((Mods & Modifiers.Win) != 0)
        {
            sb.Append("Win+");
        }

        if (Key != VirtualKey.None)
        {
            sb.Append(Key.ToString());
        }
        else if (sb.Length > 0)
        {
            sb.Length--; // drop the trailing '+' of a modifier-only wildcard
        }

        return sb.ToString();
    }

    /// <summary>The modifier bit a key contributes when it is itself a modifier, or <see cref="Modifiers.None"/>.</summary>
    public static Modifiers ModifierOf(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LControl or VirtualKey.RControl => Modifiers.Ctrl,
        VirtualKey.Menu or VirtualKey.LMenu or VirtualKey.RMenu => Modifiers.Alt,
        VirtualKey.Shift or VirtualKey.LShift or VirtualKey.RShift => Modifiers.Shift,
        VirtualKey.LWin or VirtualKey.RWin => Modifiers.Win,
        _ => Modifiers.None,
    };

    private static bool TryParseModifier(string token, out Modifiers modifier)
    {
        switch (token.ToUpperInvariant())
        {
            case "CTRL":
            case "CTL":
            case "CONTROL":
            case "LCTRL":
            case "RCTRL":
            case "LCONTROL":
            case "RCONTROL":
                modifier = Modifiers.Ctrl;
                return true;
            case "ALT":
            case "MENU":
            case "LALT":
            case "RALT":
            case "LMENU":
            case "RMENU":
                modifier = Modifiers.Alt;
                return true;
            case "SHIFT":
            case "LSHIFT":
            case "RSHIFT":
                modifier = Modifiers.Shift;
                return true;
            case "WIN":
            case "WINDOWS":
            case "META":
            case "SUPER":
            case "CMD":
            case "LWIN":
            case "RWIN":
                modifier = Modifiers.Win;
                return true;
            default:
                modifier = Modifiers.None;
                return false;
        }
    }

    private static bool TryParseKey(string token, out VirtualKey key)
    {
        key = VirtualKey.None;
        string trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        switch (trimmed.ToUpperInvariant())
        {
            case "ESC":
                key = VirtualKey.Escape;
                return true;
            case "DEL":
                key = VirtualKey.Delete;
                return true;
            case "INS":
                key = VirtualKey.Insert;
                return true;
            case "ENTER":
                key = VirtualKey.Return;
                return true;
            case "PGUP":
                key = VirtualKey.Prior;
                return true;
            case "PGDN":
                key = VirtualKey.Next;
                return true;
            case "PRTSC":
            case "PRTSCN":
            case "PRTSCR":
            case "PRINTSCREEN":
            case "PRINTSCRN":
            case "SYSRQ":
                key = VirtualKey.Snapshot;
                return true;
            case "CAPSLOCK":
                key = VirtualKey.Capital;
                return true;
            case "WINKEY":
                key = VirtualKey.LWin;
                return true;
        }

        // A single digit is a top-row number key (D0..D9); avoids Enum.TryParse treating "1" as an enum value.
        if (trimmed.Length == 1 && char.IsAsciiDigit(trimmed[0]))
        {
            key = VirtualKey.D0 + (trimmed[0] - '0');
            return true;
        }

        // Reject bare numeric strings so they are not parsed as raw enum values.
        if (trimmed.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (Enum.TryParse(trimmed, ignoreCase: true, out VirtualKey parsed) && parsed != VirtualKey.None)
        {
            key = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Curated sets of blocked key combinations for kiosk lockdown, and translation of an
/// <see cref="ExplorerPolicy"/> into a concrete blocked set for <see cref="LowLevelKeyboardHook"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BlockedKeyCombos
{
    /// <summary>
    /// The default kiosk block set: the shortcuts that let a player escape the full-screen Shell. Covers
    /// Alt+Tab / Alt+Shift+Tab / Alt+Esc (task switching), Alt+F4 (close), Alt+Space (system menu),
    /// Ctrl+Esc and any Windows-key shortcut (Start), Ctrl+Shift+Esc (Task Manager), the lone Windows and
    /// Menu keys, F1 (Help) and PrintScreen.
    /// </summary>
    public static ImmutableHashSet<KeyCombo> Default { get; } = ImmutableHashSet.Create(
        new KeyCombo(VirtualKey.Tab, Modifiers.Alt),
        new KeyCombo(VirtualKey.Tab, Modifiers.Alt | Modifiers.Shift),
        new KeyCombo(VirtualKey.Escape, Modifiers.Alt),
        new KeyCombo(VirtualKey.F4, Modifiers.Alt),
        new KeyCombo(VirtualKey.Space, Modifiers.Alt),
        new KeyCombo(VirtualKey.Escape, Modifiers.Ctrl),
        new KeyCombo(VirtualKey.Escape, Modifiers.Ctrl | Modifiers.Shift),
        new KeyCombo(VirtualKey.None, Modifiers.Win),
        new KeyCombo(VirtualKey.LWin, Modifiers.None),
        new KeyCombo(VirtualKey.RWin, Modifiers.None),
        new KeyCombo(VirtualKey.Apps, Modifiers.None),
        new KeyCombo(VirtualKey.F1, Modifiers.None),
        new KeyCombo(VirtualKey.Snapshot, Modifiers.None));

    /// <summary>
    /// Combinations that a user-mode low-level keyboard hook <b>cannot</b> intercept, documented so callers
    /// do not assume they are covered:
    /// <list type="bullet">
    /// <item><description><c>Ctrl+Alt+Del</c> — the Secure Attention Sequence is handled by Winlogon and can
    /// only be constrained through the <c>DisableTaskMgr</c> / Ctrl+Alt+Del options policy (GPO / registry),
    /// never by a hook.</description></item>
    /// <item><description><c>Win+L</c> — workstation lock is dispatched below the hook chain; disable it via the
    /// <c>DisableLockWorkstation</c> registry value rather than here.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<KeyCombo> Unblockable { get; } = new[]
    {
        new KeyCombo(VirtualKey.Delete, Modifiers.Ctrl | Modifiers.Alt),
        new KeyCombo(VirtualKey.L, Modifiers.Win),
    };

    /// <summary>The three combos that together block every use of the Windows key (lone press and any Win+key shortcut).</summary>
    public static IReadOnlyList<KeyCombo> WindowsKey { get; } = new[]
    {
        new KeyCombo(VirtualKey.LWin, Modifiers.None),
        new KeyCombo(VirtualKey.RWin, Modifiers.None),
        new KeyCombo(VirtualKey.None, Modifiers.Win),
    };

    /// <summary>
    /// Builds the blocked set implied by an <see cref="ExplorerPolicy"/>: the boolean toggles
    /// (<see cref="ExplorerPolicy.DisableAltTab"/>, <see cref="ExplorerPolicy.DisableWinKey"/>,
    /// <see cref="ExplorerPolicy.DisableTaskManager"/>) plus every parseable entry of
    /// <see cref="ExplorerPolicy.BlockedKeyCombos"/>. Unparseable entries are skipped.
    /// </summary>
    public static ImmutableHashSet<KeyCombo> FromPolicy(ExplorerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        ImmutableHashSet<KeyCombo>.Builder builder = ImmutableHashSet.CreateBuilder<KeyCombo>();

        if (policy.DisableAltTab)
        {
            builder.Add(new KeyCombo(VirtualKey.Tab, Modifiers.Alt));
            builder.Add(new KeyCombo(VirtualKey.Tab, Modifiers.Alt | Modifiers.Shift));
            builder.Add(new KeyCombo(VirtualKey.Escape, Modifiers.Alt));
        }

        if (policy.DisableWinKey)
        {
            foreach (KeyCombo combo in WindowsKey)
            {
                builder.Add(combo);
            }
        }

        if (policy.DisableTaskManager)
        {
            builder.Add(new KeyCombo(VirtualKey.Escape, Modifiers.Ctrl | Modifiers.Shift));
        }

        foreach (string entry in policy.BlockedKeyCombos)
        {
            if (KeyCombo.TryParse(entry, out KeyCombo combo))
            {
                builder.Add(combo);
            }
        }

        return builder.ToImmutable();
    }
}
