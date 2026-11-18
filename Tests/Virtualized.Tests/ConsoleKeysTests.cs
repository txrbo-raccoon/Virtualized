using Virtualized.Shared;

namespace Virtualized.Tests;

public class ConsoleKeysTests
{
    [Fact]
    public void FunctionKeysUseX11Keysyms()
    {
        // XK_F1 is 0xffbe and the F keys are consecutive; XK_F7 is 0xffc4.
        var first = ConsoleKeys.LinuxTerminals[0].Keys;
        var last = ConsoleKeys.LinuxTerminals[^1].Keys;

        Assert.Equal(new VncKey(0xffbe, "F1"), first[^1]);
        Assert.Equal(new VncKey(0xffc4, "F7"), last[^1]);
        Assert.Equal(["ControlLeft", "AltLeft", "F1"], first.Select(k => k.Code));
    }

    [Fact]
    public void WindowsCombosIncludeTheWindowsKey()
    {
        Assert.Contains(ConsoleKeys.Windows, c => c.Keys is [{ Keysym: 0xffeb, Code: "MetaLeft" }]);
    }
}
