using System.Text.Json;

namespace Virtualized.Settings;

public sealed record AppSettings
{
    /// <summary>Folder holding the qemu-system-* binaries; null means detect automatically.</summary>
    public string? QemuDirectory { get; init; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore()
    {
        Current = _Load();
    }

    public AppSettings Current { get; private set; }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        await using var stream = File.Create(AppPaths.SettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        Current = settings;
    }

    private static AppSettings _Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // A corrupt settings file falls back to defaults rather than keeping the app from starting.
        }

        return new AppSettings();
    }
}
