using Cerberus.UI;
using Virtualized;
using Virtualized.Qemu;
using Virtualized.Settings;
using Virtualized.Updates;
using Virtualized.Vms;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.Cerberus;

internal class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(ArchiveFileProvider.FromEmbeddedWebRoot(), args);

        appBuilder.RootComponents.Add<App>("#app");
        appBuilder.Services.AddCerberusUI();
        appBuilder.Services.AddSingleton<SettingsStore>();
        appBuilder.Services.AddSingleton<QemuLocator>();
        appBuilder.Services.AddSingleton<QemuEnvironment>();
        appBuilder.Services.AddSingleton(new VmRepository(AppPaths.VmsDirectory));
        appBuilder.Services.AddSingleton<VmManager>();
        appBuilder.Services.AddSingleton<VmService>();
        appBuilder.Services.AddSingleton<SnapshotService>();
        appBuilder.Services.AddSingleton<UpdateService>();

        var app = appBuilder.Build();

        app.MainWindow
            .UseCerberusFrame()
            .SetTitle("Virtualized")
            .SetIconFile(Path.Combine(AppContext.BaseDirectory, "appicon.ico"))
            .SetSize(1280, 720)
            .SetMinSize(800, 500)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(false);

        AppDomain.CurrentDomain.UnhandledException += (sender, error) => { };

        app.Run();
    }
}