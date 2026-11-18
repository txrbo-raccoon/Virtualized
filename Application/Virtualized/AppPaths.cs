namespace Virtualized;

public static class AppPaths
{
    /// <summary>%AppData%\Virtualized on Windows, ~/.config/Virtualized on Linux, ~/Library/Application Support/Virtualized on macOS.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtualized");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>One subfolder per VM, holding its vm.json, disks and runtime files.</summary>
    public static string VmsDirectory => Path.Combine(DataDirectory, "vms");
}
