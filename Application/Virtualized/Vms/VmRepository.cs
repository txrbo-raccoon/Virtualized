using System.Text.Json;
using System.Text.Json.Serialization;

namespace Virtualized.Vms;

/// <summary>A VM folder whose vm.json could not be read; listed so the problem is visible instead of the VM vanishing.</summary>
public sealed record VmLoadError(string Directory, string Message);

public sealed record VmList(IReadOnlyList<VmDefinition> Vms, IReadOnlyList<VmLoadError> Errors);

/// <summary>
/// Stores each VM in its own folder (&lt;root&gt;/&lt;id&gt;/vm.json, next to its disks and runtime files).
/// </summary>
public sealed class VmRepository(string rootDirectory)
{
    public const string DefinitionFileName = "vm.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string RootDirectory { get; } = rootDirectory;

    public event Action? Changed;

    public string GetDirectory(Guid id) => Path.Combine(RootDirectory, id.ToString("N"));

    public async Task<VmList> ListAsync(CancellationToken ct = default)
    {
        var vms = new List<VmDefinition>();
        var errors = new List<VmLoadError>();
        if (!Directory.Exists(RootDirectory))
            return new VmList(vms, errors);

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var file = Path.Combine(directory, DefinitionFileName);
            if (!File.Exists(file))
                continue;

            try
            {
                vms.Add(await _ReadAsync(file, ct));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                errors.Add(new VmLoadError(directory, ex.Message));
            }
        }

        return new VmList(vms.OrderBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), errors);
    }

    public async Task<VmDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var file = Path.Combine(GetDirectory(id), DefinitionFileName);
        return File.Exists(file) ? await _ReadAsync(file, ct) : null;
    }

    /// <summary>Writes vm.json through a temporary file so a crash mid-write can't leave it truncated.</summary>
    public async Task SaveAsync(VmDefinition vm, CancellationToken ct = default)
    {
        var directory = GetDirectory(vm.Id);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, DefinitionFileName);
        var temporary = file + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, vm, JsonOptions, ct);
        }

        File.Move(temporary, file, overwrite: true);
        Changed?.Invoke();
    }

    /// <summary>
    /// Deletes the VM's folder, including its disks. Right after a VM stops, Windows can still hold its
    /// files for a moment (the exiting process, antivirus scans), so the delete is retried briefly.
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        var directory = GetDirectory(id);
        for (var attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(200);
            }
        }

        Changed?.Invoke();
    }

    private static async Task<VmDefinition> _ReadAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var vm = await JsonSerializer.DeserializeAsync<VmDefinition>(stream, JsonOptions, ct)
                 ?? throw new InvalidDataException("vm.json is empty.");

        if (vm.SchemaVersion > VmDefinition.CurrentSchemaVersion)
            throw new InvalidDataException($"vm.json was written by a newer Virtualized (schema {vm.SchemaVersion}).");

        return vm;
    }
}
