using Virtualized.Settings;

namespace Virtualized.Qemu;

/// <summary>App-wide view of the QEMU installation in use, refreshed on startup and when settings change.</summary>
public sealed class QemuEnvironment(SettingsStore settings, QemuLocator locator)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public QemuDetection? Detection { get; private set; }

    public QemuInstallation? Installation => Detection?.Installation;

    public bool IsDetecting { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            IsDetecting = true;
            Changed?.Invoke();

            Detection = await Task.Run(() => locator.DetectAsync(settings.Current.QemuDirectory, ct), ct);
        }
        finally
        {
            IsDetecting = false;
            _refreshLock.Release();
            Changed?.Invoke();
        }
    }
}
