using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Virtualized.Qemu;

/// <summary>
/// Starts a long-lived process that must outlive the app: its stdout and stderr go straight to a log
/// file (no pipes owned by the app), it gets no console window, and it isn't tied to the app's lifetime.
/// </summary>
public static class ProcessLauncher
{
    public static Process Start(string executable, IReadOnlyList<string> arguments, string logFile, string workingDirectory) =>
        OperatingSystem.IsWindows()
            ? _StartWindows(executable, arguments, logFile, workingDirectory)
            : _StartUnix(executable, arguments, logFile, workingDirectory);

    private static Process _StartUnix(string executable, IReadOnlyList<string> arguments, string logFile, string workingDirectory)
    {
        // "exec" keeps the PID, so the returned process is QEMU itself. setsid (Linux) moves it to its own
        // session so a Ctrl+C in the terminal that started the app doesn't reach it; macOS has no setsid.
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        var script = $"exec {(setsid is null ? "" : $"\"{setsid}\" ")}\"$0\" \"$@\" >\"$SQEMU_LOG\" 2>&1 </dev/null";

        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = workingDirectory };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["SQEMU_LOG"] = logFile;

        return Process.Start(startInfo)!;
    }

    private static Process _StartWindows(string executable, IReadOnlyList<string> arguments, string logFile, string workingDirectory)
    {
        using var log = File.OpenHandle(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _MakeInheritable(log);
        _MakeInheritable(nul);

        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdInput = nul.DangerousGetHandle(),
            hStdOutput = log.DangerousGetHandle(),
            hStdError = log.DangerousGetHandle(),
        };
        var commandLine = new StringBuilder(WindowsCommandLine.Build(executable, arguments));

        // Breaking away from the app's job object (an IDE or debugger may put it in one that kills children
        // on close) isn't always allowed; then the process is started inside the job instead.
        const uint baseFlags = CreateNoWindow | CreateNewProcessGroup;
        if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, baseFlags | CreateBreakawayFromJob,
                IntPtr.Zero, workingDirectory, ref startup, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorAccessDenied
                || !CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, baseFlags,
                    IntPtr.Zero, workingDirectory, ref startup, out info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't start {Path.GetFileName(executable)}.");
            }
        }

        CloseHandle(info.hThread);
        try
        {
            // Opening the Process's own handle while ours still pins the process object keeps HasExited and
            // ExitCode working even if QEMU exits immediately (e.g. on a bad option).
            var process = Process.GetProcessById(info.dwProcessId);
            _ = process.Handle;
            return process;
        }
        finally
        {
            CloseHandle(info.hProcess);
        }
    }

    private static void _MakeInheritable(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Quotes arguments the way the Microsoft C runtime (and so QEMU on Windows) splits them back apart.</summary>
public static class WindowsCommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return argument;

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are only special right before a quote: double them, then escape the quote.
            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            result.Append(c);
        }

        // Before the closing quote, trailing backslashes have to be doubled too.
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}
