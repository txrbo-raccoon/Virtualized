namespace Virtualized.Shared;

public enum ConsoleScale
{
    /// <summary>The VM's screen is scaled to fit the window.</summary>
    Fit,

    /// <summary>One VM pixel per screen pixel; the console scrolls when the VM's screen is larger.</summary>
    Actual,
}

/// <summary>An X11 keysym and the matching KeyboardEvent.code, which QEMU uses to pick the scancode.</summary>
public readonly record struct VncKey(int Keysym, string Code);

public sealed record KeyCombo(string Label, IReadOnlyList<VncKey> Keys);

/// <summary>
/// Key combinations the host OS would intercept if typed (Ctrl+Alt+Del, the Windows key, Alt+Tab...),
/// so the console sends them to the VM on request instead.
/// </summary>
public static class ConsoleKeys
{
    private static readonly VncKey Ctrl = new(0xffe3, "ControlLeft");
    private static readonly VncKey Alt = new(0xffe9, "AltLeft");
    private static readonly VncKey Super = new(0xffeb, "MetaLeft");
    private static readonly VncKey Tab = new(0xff09, "Tab");
    private static readonly VncKey Backspace = new(0xff08, "Backspace");
    private static readonly VncKey PrintScreen = new(0xff61, "PrintScreen");

    private static VncKey F(int n) => new(0xffbe + n - 1, $"F{n}");

    public static readonly IReadOnlyList<KeyCombo> Windows =
    [
        new("Windows key", [Super]),
        new("Alt+Tab", [Alt, Tab]),
        new("Alt+F4", [Alt, F(4)]),
        new("Print Screen", [PrintScreen]),
    ];

    /// <summary>Ctrl+Alt+F1…F7 switch Linux virtual terminals (F1/F7 is usually the desktop).</summary>
    public static readonly IReadOnlyList<KeyCombo> LinuxTerminals =
        [.. Enumerable.Range(1, 7).Select(n => new KeyCombo($"Ctrl+Alt+F{n}", [Ctrl, Alt, F(n)]))];

    public static readonly KeyCombo CtrlAltBackspace = new("Ctrl+Alt+Backspace", [Ctrl, Alt, Backspace]);
}
