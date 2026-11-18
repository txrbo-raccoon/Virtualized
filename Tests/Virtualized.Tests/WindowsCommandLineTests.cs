using System.Runtime.InteropServices;
using Virtualized.Qemu;

namespace Virtualized.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\Program Files\qemu", "\"C:\\Program Files\\qemu\"")]
    [InlineData(@"trailing\ space\", "\"trailing\\ space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"no\spaces\", @"no\spaces\")]
    public void QuotesLikeTheCRuntimeExpects(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));
    }

    /// <summary>Round-trips through Windows' own parser, which follows the same rules as the C runtime.</summary>
    [Fact]
    public void WindowsParsesBackTheSameArguments()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string[] arguments =
        [
            "-name", "My VM,process=sqemu-1",
            "-drive", @"if=none,id=cd0,media=cdrom,file=C:\Users\me\My ISOs\debian 13.iso",
            "", @"ends with backslash\", "a \"quoted\" word", @"\\server\share\x", @"back\\""slash",
        ];

        var parsed = _Parse(WindowsCommandLine.Build(@"C:\Program Files\qemu\qemu-system-x86_64.exe", arguments));

        Assert.Equal([@"C:\Program Files\qemu\qemu-system-x86_64.exe", .. arguments], parsed);
    }

    private static string[] _Parse(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
