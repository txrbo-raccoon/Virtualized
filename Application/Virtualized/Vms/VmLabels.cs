namespace Virtualized.Vms;

public static class VmLabels
{
    public static string Describe(GuestOs os) => os switch
    {
        GuestOs.Linux => "Linux",
        GuestOs.Windows10 => "Windows 10",
        GuestOs.Windows11 => "Windows 11",
        _ => "Other",
    };

    public static string Icon(GuestOs os) => os switch
    {
        GuestOs.Linux => "terminal",
        GuestOs.Windows10 or GuestOs.Windows11 => "desktop_windows",
        _ => "computer",
    };

    public static string Memory(int mib) => mib % 1024 == 0 ? $"{mib / 1024} GiB" : $"{mib / 1024.0:0.#} GiB";

    public static Cerberus.UI.Color StatusColor(VmStatus status) => status switch
    {
        VmStatus.Running => Cerberus.UI.Color.Success,
        VmStatus.Starting or VmStatus.Suspended => Cerberus.UI.Color.Info,
        VmStatus.Paused or VmStatus.Stopping => Cerberus.UI.Color.Warning,
        VmStatus.Crashed => Cerberus.UI.Color.Error,
        _ => Cerberus.UI.Color.Default,
    };

    public static string StatusIcon(VmStatus status) => status switch
    {
        VmStatus.Running => "play_circle",
        VmStatus.Starting => "hourglass_top",
        VmStatus.Paused => "pause_circle",
        VmStatus.Suspended => "bedtime",
        VmStatus.Crashed => "error",
        VmStatus.Stopping => "power_settings_new",
        _ => "stop_circle",
    };
}
