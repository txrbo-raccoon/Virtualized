using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Virtualized.Qemu;

namespace Virtualized.Vms;

public enum VmStatus
{
    Stopped,
    Starting,
    Running,

    /// <summary>Paused by the user, or by QEMU after a disk error (the VM's error says which).</summary>
    Paused,

    /// <summary>The guest put itself to sleep (ACPI S3); Resume wakes it up.</summary>
    Suspended,

    /// <summary>The guest OS crashed (reported through pvpanic) or QEMU hit an internal error. Kept paused so the crash screen stays visible.</summary>
    Crashed,

    Stopping,
}

/// <summary>Result of applying a QMP event to a VM's state: the new status and what to do with its error message.</summary>
public readonly record struct VmEventOutcome(VmStatus Status, string? Error = null, bool ClearError = false);

/// <summary>What the UI needs to show about a VM that isn't in its vm.json.</summary>
public sealed record VmRuntimeInfo(VmStatus Status, VmRuntimeState? State = null, string? Error = null);

/// <summary>
/// Starts and stops QEMU processes and tracks them. QEMU runs detached from the app; run.json in the
/// VM's folder records how to find it again, so VMs keep running when the app closes and are picked
/// up again (<see cref="ReattachAllAsync"/>) when it reopens.
/// </summary>
public sealed partial class VmManager(VmRepository repository, QemuEnvironment qemu) : IAsyncDisposable
{
    public const string RuntimeFileName = "run.json";
    public const string LogFileName = "qemu.log";
    public const string PreviousLogFileName = "qemu.previous.log";

    /// <summary>Records which firmware build the VM's UEFI variable store was created from.</summary>
    public const string EfiVarsSourceFileName = "efivars.source";

    private static readonly TimeSpan QmpConnectTimeout = TimeSpan.FromSeconds(10);
    private const int MaxStartAttempts = 3;

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _errors = new();

    public event Action<Guid>? StatusChanged;

    public VmRuntimeInfo GetInfo(Guid id)
    {
        _errors.TryGetValue(id, out var error);
        return _sessions.TryGetValue(id, out var session)
            ? new VmRuntimeInfo(session.Status, session.State, error)
            : new VmRuntimeInfo(VmStatus.Stopped, null, error);
    }

    public bool IsActive(Guid id) => _sessions.ContainsKey(id);

    public string GetLogPath(Guid id) => Path.Combine(repository.GetDirectory(id), LogFileName);

    /// <summary>Finds VMs left running by a previous session of the app and reconnects to them.</summary>
    public async Task ReattachAllAsync()
    {
        var list = await repository.ListAsync();
        await Task.WhenAll(list.Vms.Select(vm => _ReattachAsync(vm.Id)));
    }

    /// <param name="paused">Start with the CPUs stopped (status Paused), e.g. to load a snapshot first.</param>
    public async Task StartAsync(VmDefinition vm, bool paused = false, CancellationToken ct = default)
    {
        var installation = qemu.Installation ?? throw new InvalidOperationException("QEMU wasn't found.");
        var session = new Session(vm.Id) { Status = VmStatus.Starting };
        if (!_sessions.TryAdd(vm.Id, session))
            throw new InvalidOperationException($"{vm.Name} is already running.");

        _errors.TryRemove(vm.Id, out _);
        _Raise(vm.Id);
        try
        {
            var directory = repository.GetDirectory(vm.Id);
            await _PrepareFirmwareAsync(vm, installation, directory, ct);
            await _PrepareUnattendAsync(vm, directory, ct);

            for (var attempt = 1; ; attempt++)
            {
                var ports = _UsedPorts(vm);
                var qmpPort = PortAllocator.GetFreePort(ports);
                var vncPort = PortAllocator.GetFreePort([.. ports, qmpPort]);

                // swtpm has to be listening before QEMU starts; it exits by itself once QEMU disconnects.
                var swtpm = vm.Tpm ? await _StartSwtpmAsync(vm, installation, directory, ct) : null;
                var context = new QemuLaunchContext(directory, qmpPort, vncPort, HostInfo.CpuVendor,
                    vm.Tpm ? Swtpm.SocketPath(directory, vm.Id) : null, paused);
                var command = QemuCommandBuilder.Build(vm, installation, context);

                _RotateLog(directory);
                // Not the VM's folder: Windows won't delete a folder that is a process's working directory,
                // which made deleting a VM right after stopping it fail. QEMU only gets absolute paths.
                var process = ProcessLauncher.Start(command.Executable, command.Arguments, GetLogPath(vm.Id), Path.GetTempPath());
                var state = new VmRuntimeState(process.Id, process.StartTime.ToUniversalTime(), qmpPort, vncPort);
                await state.SaveAsync(_RuntimePath(vm.Id));
                session.State = state;
                session.Process = process;

                if (await _ConnectAsync(session, process, ct))
                    break;

                // QEMU exited during startup. If it never connected to swtpm, swtpm is still waiting for it.
                if (swtpm is { HasExited: false })
                    swtpm.Kill();

                // A port taken between allocation and QEMU binding it is worth another try with fresh
                // ports; anything else is reported with QEMU's own message.
                var errors = _ReadLogErrors(vm.Id);
                File.Delete(_RuntimePath(vm.Id));
                if (attempt < MaxStartAttempts && _IsPortConflict(errors, qmpPort, vncPort))
                    continue;

                throw new VmStartException(_FailureMessage(vm.Id, process, "QEMU exited right after starting"));
            }
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(vm.Id, out _);
            _errors[vm.Id] = ex.Message;
            _Raise(vm.Id);
            throw;
        }
    }

    /// <summary>
    /// Asks the guest OS to shut down, like pressing the power button. The guest may ignore it, so the
    /// VM stays Running until QEMU reports the SHUTDOWN that actually ends it.
    /// </summary>
    public async Task ShutDownAsync(Guid id)
    {
        var session = _GetSession(id);
        await session.Qmp!.ExecuteAsync("system_powerdown");
    }

    /// <summary>Stops QEMU immediately, like pulling the plug. Unsaved data in the guest is lost.</summary>
    public async Task ForceStopAsync(Guid id)
    {
        var session = _GetSession(id);
        session.Status = VmStatus.Stopping;
        _Raise(id);

        try
        {
            if (session.Qmp is { IsConnected: true } qmp)
                await qmp.ExecuteAsync("quit").WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is QmpException or TimeoutException)
        {
            // QEMU often closes the socket before replying to quit; the exit check below is what counts.
        }

        if (session.Process is { } process && !await _WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
            process.Kill();

        await _EndSessionAsync(session, error: null);
    }

    private async Task _ReattachAsync(Guid id)
    {
        if (_sessions.ContainsKey(id))
            return;

        var path = _RuntimePath(id);
        VmRuntimeState? state;
        try
        {
            state = await VmRuntimeState.LoadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            state = null;
        }

        if (state is null)
            return;

        if (state.FindProcess() is not { } process)
        {
            File.Delete(path);
            return;
        }

        var session = new Session(id) { Status = VmStatus.Starting, State = state, Process = process };
        if (!_sessions.TryAdd(id, session))
            return;

        _Raise(id);
        if (!await _ConnectAsync(session, process, CancellationToken.None))
            await _EndSessionAsync(session, error: "Couldn't reconnect to the running VM.");
    }

    /// <summary>Connects QMP, retrying while QEMU opens its sockets. False if QEMU exits or never answers.</summary>
    private async Task<bool> _ConnectAsync(Session session, Process process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + QmpConnectTimeout;
        while (true)
        {
            if (_HasExited(process))
                return false;

            var qmp = new QmpClient();
            qmp.EventReceived += (name, data) => _OnQmpEvent(session, name, data);
            qmp.Disconnected += () => _ = _OnQmpDisconnectedAsync(session);
            try
            {
                await qmp.ConnectAsync("127.0.0.1", session.State!.QmpPort, ct);
                session.Qmp = qmp;

                var status = await qmp.ExecuteAsync("query-status", ct: ct);
                // A VM that is paused with errors in its log (e.g. found frozen when reattaching) is shown as
                // crashed; a freshly started one has an empty log, so this only matters on reattach.
                var errors = _ReadLogErrorList(session.VmId);
                session.KnownLogErrors = errors.Count;
                var outcome = ClassifyUnexpectedStop(status?["status"]?.GetValue<string>(), errors);
                session.Status = outcome.Status;
                if (outcome.Error is not null)
                    _errors[session.VmId] = outcome.Error;
                _Raise(session.VmId);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or QmpException or IOException)
            {
                await qmp.DisposeAsync();
                if (DateTime.UtcNow > deadline)
                    return false;
                await Task.Delay(150, ct);
            }
        }
    }

    private void _OnQmpEvent(Session session, string name, JsonNode? data)
    {
        _Apply(session, ApplyEvent(session.Status, name, data));

        // STOP doesn't say why. One the app didn't ask for may be QEMU freezing the VM after an accelerator
        // failure, which query-status still reports as plain "paused"; the log tells them apart.
        if (name == "STOP" && !Interlocked.Exchange(ref session.PauseRequested, false) && session.Status == VmStatus.Paused)
            _ = _CheckUnexpectedStopAsync(session);
    }

    private async Task _CheckUnexpectedStopAsync(Session session)
    {
        try
        {
            // QEMU writes the error right around the event; give it a moment to reach the log.
            await Task.Delay(300);
            var status = await session.Qmp!.ExecuteAsync("query-status");
            var newErrors = _ReadLogErrorList(session.VmId).Skip(session.KnownLogErrors).ToList();
            var outcome = ClassifyUnexpectedStop(status?["status"]?.GetValue<string>(), newErrors);
            if (session.Status == VmStatus.Paused)
                _Apply(session, outcome);
        }
        catch (QmpException)
        {
            // QEMU is gone; the disconnection handler takes it from here.
        }
    }

    /// <summary>
    /// Decides what a STOP the app didn't request means, from QEMU's run state and the errors it logged
    /// since the VM was started (or since the app reattached to it).
    /// </summary>
    internal static VmEventOutcome ClassifyUnexpectedStop(string? runState, IReadOnlyList<string> newErrors)
    {
        var outcome = MapRunState(runState);
        if (outcome.Status == VmStatus.Paused && outcome.Error is null && newErrors.Count > 0)
            return new(VmStatus.Crashed, $"QEMU stopped the VM: {string.Join("\n", newErrors)}");

        return outcome;
    }

    private void _Apply(Session session, VmEventOutcome outcome)
    {
        var changed = outcome.Status != session.Status;
        session.Status = outcome.Status;

        if (outcome.Error is not null)
        {
            _errors[session.VmId] = outcome.Error;
            changed = true;
        }
        else if (outcome.ClearError && _errors.TryRemove(session.VmId, out _))
        {
            changed = true;
        }

        if (changed)
            _Raise(session.VmId);
    }

    /// <summary>How a QMP event changes a running VM's status. Events not listed leave it as it is.</summary>
    internal static VmEventOutcome ApplyEvent(VmStatus current, string name, JsonNode? data) => name switch
    {
        // QEMU sends STOP after GUEST_PANICKED and after a disk error too; those keep their own status.
        "STOP" => new(current is VmStatus.Crashed or VmStatus.Suspended ? current : VmStatus.Paused),
        "RESUME" or "WAKEUP" => new(VmStatus.Running, ClearError: true),
        "SUSPEND" => new(VmStatus.Suspended),
        "GUEST_PANICKED" => new(VmStatus.Crashed, "The guest operating system crashed. Restart or force stop the VM."),
        "BLOCK_IO_ERROR" when data?["action"]?.GetValue<string>() == "stop" =>
            new(VmStatus.Paused, data["nospace"]?.GetValue<bool>() == true
                ? "Paused because the host disk is full. Free some space, then resume."
                : $"Paused after a disk error: {data["reason"]?.GetValue<string>()}"),
        // POWERDOWN only means the power button was pressed; SHUTDOWN is the guest actually turning off.
        "SHUTDOWN" => new(VmStatus.Stopping),
        _ => new(current),
    };

    /// <summary>
    /// Marks the next STOP as caused by the app (snapshots pause the VM while they save or load), so it
    /// isn't checked as a possible crash.
    /// </summary>
    internal void ExpectPause(Guid id) => _GetSession(id).PauseRequested = true;

    /// <summary>Runs a raw QMP command on a running VM. Used by snapshots, tests and diagnostics.</summary>
    internal Task<JsonNode?> ExecuteQmpAsync(Guid id, string command, object? arguments = null) =>
        _GetSession(id).Qmp!.ExecuteAsync(command, arguments);

    /// <summary>Pauses the guest; its memory stays as it is until it is resumed.</summary>
    public async Task PauseAsync(Guid id)
    {
        var session = _GetSession(id);
        session.PauseRequested = true;
        await session.Qmp!.ExecuteAsync("stop");
    }

    /// <summary>Continues a paused VM, or wakes up a suspended one.</summary>
    public async Task ResumeAsync(Guid id)
    {
        var session = _GetSession(id);
        await session.Qmp!.ExecuteAsync(session.Status == VmStatus.Suspended ? "system_wakeup" : "cont");
    }

    /// <summary>Restarts the guest like the reset button. A crashed or paused VM is also set running again.</summary>
    public async Task ResetAsync(Guid id)
    {
        var session = _GetSession(id);
        await session.Qmp!.ExecuteAsync("system_reset");
        if (session.Status is VmStatus.Paused or VmStatus.Crashed)
            await session.Qmp.ExecuteAsync("cont");
    }

    /// <summary>
    /// Puts an ISO in the running VM's CD drive, or empties it when <paramref name="isoPath"/> is null.
    /// Forced because guests often lock the tray while a disc is in use.
    /// </summary>
    public async Task ChangeMediaAsync(Guid id, string? isoPath)
    {
        var qmp = _GetSession(id).Qmp!;
        if (isoPath is null)
        {
            await qmp.ExecuteAsync("eject", new Dictionary<string, object>
            {
                ["id"] = QemuCommandBuilder.CdromDeviceId,
                ["force"] = true,
            });
            return;
        }

        await qmp.ExecuteAsync("blockdev-change-medium", new Dictionary<string, object>
        {
            ["id"] = QemuCommandBuilder.CdromDeviceId,
            ["filename"] = isoPath,
            ["format"] = "raw",
            ["read-only-mode"] = "read-only",
            ["force"] = true,
        });
    }

    private async Task _OnQmpDisconnectedAsync(Session session)
    {
        // The QMP socket closes when QEMU exits. Give the process a moment to finish before checking.
        var process = session.Process;
        if (process is not null && !await _WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
        {
            await _EndSessionAsync(session, "Lost the connection to QEMU, but the process is still running.", deleteRuntime: false);
            return;
        }

        // A guest that shuts itself down sends SHUTDOWN first (status Stopping); exiting from any other
        // state with a failure code is a crash worth reporting.
        var crashed = session.Status is not VmStatus.Stopping
                      && _ExitCode(process) is not 0;
        await _EndSessionAsync(session, crashed ? _FailureMessage(session.VmId, process, "QEMU stopped unexpectedly") : null);
    }

    /// <summary>Null when unknown: the exit code of a process the app didn't start isn't available on Linux and macOS.</summary>
    private static int? _ExitCode(Process? process)
    {
        try
        {
            return process?.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool _HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Ends a session once. Both a stop request and the QMP disconnection it causes get here; whichever
    /// comes second waits for the first to finish, so callers can rely on the cleanup being done.
    /// </summary>
    private async Task _EndSessionAsync(Session session, string? error, bool deleteRuntime = true)
    {
        if (!_sessions.TryRemove(new KeyValuePair<Guid, Session>(session.VmId, session)))
        {
            await session.Ended.Task;
            return;
        }

        try
        {
            await _CleanUpSessionAsync(session, error, deleteRuntime);
        }
        finally
        {
            session.Ended.TrySetResult();
        }
    }

    private async Task _CleanUpSessionAsync(Session session, string? error, bool deleteRuntime)
    {
        if (deleteRuntime)
            File.Delete(_RuntimePath(session.VmId));
        if (session.Qmp is { } qmp)
            await qmp.DisposeAsync();
        // A problem reported while running (a crash, a disk error) no longer applies once the VM is off.
        if (error is not null)
            _errors[session.VmId] = error;
        else
            _errors.TryRemove(session.VmId, out _);

        _Raise(session.VmId);
    }

    /// <summary>
    /// UEFI variables are per VM and kept as qcow2 (not raw) so that internal snapshots also cover
    /// them; a raw pflash device would make QEMU refuse to take snapshots.
    /// </summary>
    private static async Task _PrepareFirmwareAsync(VmDefinition vm, QemuInstallation installation, string directory, CancellationToken ct)
    {
        if (vm.Firmware != FirmwareType.Uefi)
            return;

        var firmware = QemuCommandBuilder.SelectFirmware(vm, installation);
        var varsPath = Path.Combine(directory, QemuLaunchContext.EfiVarsFileName);
        var sourcePath = Path.Combine(directory, EfiVarsSourceFileName);

        // The variable store has to match the firmware build. When that changes (Secure Boot turned on or
        // off, a different QEMU), it's recreated; the guest's own boot entries are lost then, but OSes
        // fall back to the default \EFI\BOOT loader. Stores created before this was tracked are kept.
        var source = $"{firmware.Name}|{firmware.VarsTemplatePath}";
        var recorded = File.Exists(sourcePath) ? await File.ReadAllTextAsync(sourcePath, ct) : null;
        if (ShouldRecreateEfiVars(File.Exists(varsPath), recorded, source))
        {
            File.Delete(varsPath);
            await QemuImg.ConvertToQcow2Async(installation.QemuImgPath!, firmware.VarsTemplatePath!, firmware.VarsFormat ?? "raw", varsPath, ct);
        }

        if (recorded != source)
            await File.WriteAllTextAsync(sourcePath, source, ct);
    }

    /// <summary>
    /// Whether the UEFI variable store has to be (re)created from the current firmware's template: it's
    /// missing, or it was made from a different build. A store with no recorded source predates tracking
    /// and is assumed to match.
    /// </summary>
    internal static bool ShouldRecreateEfiVars(bool varsExist, string? recordedSource, string currentSource) =>
        !varsExist || (recordedSource is not null && recordedSource != currentSource);

    /// <summary>Writes the Windows 11 compatibility answer file the VM's USB drive will show, when it needs one.</summary>
    private static async Task _PrepareUnattendAsync(VmDefinition vm, string directory, CancellationToken ct)
    {
        if (!QemuCommandBuilder.NeedsUnattendDrive(vm))
            return;

        var folder = Path.Combine(directory, QemuLaunchContext.UnattendFolderName);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, WindowsUnattend.FileName), WindowsUnattend.Build(vm.Architecture), ct);
    }

    private static Task<Process> _StartSwtpmAsync(VmDefinition vm, QemuInstallation installation, string directory, CancellationToken ct)
    {
        var swtpm = installation.SwtpmPath
                    ?? throw new QemuConfigurationException("The VM has a TPM, but swtpm wasn't found. Install swtpm or turn the TPM off.");
        return Swtpm.StartAsync(swtpm, directory, vm.Id, ct);
    }

    private HashSet<int> _UsedPorts(VmDefinition vm)
    {
        var ports = vm.PortForwards.Select(p => p.HostPort).ToHashSet();
        foreach (var other in _sessions.Values)
        {
            if (other.State is { } state)
            {
                ports.Add(state.QmpPort);
                ports.Add(state.VncWebSocketPort);
            }
        }

        return ports;
    }

    private static bool _IsPortConflict(string log, int qmpPort, int vncPort) =>
        (log.Contains(qmpPort.ToString()) || log.Contains(vncPort.ToString()))
        && (log.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
            || log.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase)
            || log.Contains("Failed to find an available port", StringComparison.OrdinalIgnoreCase));

    private static void _RotateLog(string directory)
    {
        var log = Path.Combine(directory, LogFileName);
        if (File.Exists(log))
            File.Move(log, Path.Combine(directory, PreviousLogFileName), overwrite: true);
    }

    /// <summary>The errors QEMU reported in its log, or "" if it wrote none (e.g. it was killed).</summary>
    private string _ReadLogErrors(Guid id) => string.Join("\n", _ReadLogErrorList(id).TakeLast(5));

    private List<string> _ReadLogErrorList(Guid id)
    {
        try
        {
            using var stream = new FileStream(GetLogPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ExtractErrors(reader.ReadToEnd()).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// QEMU reports problems as "&lt;path to qemu-system-*&gt;: message"; other lines (e.g. "VNC server
    /// running on ...") are informational. Returns the messages without the program path, skipping warnings.
    /// </summary>
    internal static IEnumerable<string> ExtractErrors(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => QemuMessage().Match(line))
            .Where(match => match.Success && !match.Groups["message"].Value.StartsWith("warning: ", StringComparison.Ordinal))
            .Select(match => match.Groups["message"].Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"qemu-system-[\w.-]+?(?:\.exe)?: (?<message>.+)$")]
    private static partial System.Text.RegularExpressions.Regex QemuMessage();

    private string _FailureMessage(Guid id, Process? process, string fallback)
    {
        var errors = _ReadLogErrors(id);
        if (errors.Length > 0)
            return errors;

        return _ExitCode(process) is { } code ? $"{fallback} (exit code {code})." : $"{fallback}.";
    }

    private static async Task<bool> _WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Maps QEMU's run state (from query-status, e.g. when reattaching) to a status and, for the
    /// states that mean something went wrong, the message to show.
    /// </summary>
    internal static VmEventOutcome MapRunState(string? qmpStatus) => qmpStatus switch
    {
        "running" or null => new(VmStatus.Running),
        "shutdown" => new(VmStatus.Stopping),
        "suspended" => new(VmStatus.Suspended),
        "guest-panicked" => new(VmStatus.Crashed, "The guest operating system crashed. Restart or force stop the VM."),
        "internal-error" => new(VmStatus.Crashed, "QEMU hit an internal error. Check qemu.log, then restart or force stop the VM."),
        "io-error" => new(VmStatus.Paused, "Paused after a disk error. Check that the host disk has free space, then resume."),
        // paused, prelaunch, debug, watchdog, the migration and snapshot states...
        _ => new(VmStatus.Paused),
    };

    private Session _GetSession(Guid id) =>
        _sessions.TryGetValue(id, out var session) && session.Qmp is not null
            ? session
            : throw new InvalidOperationException("The VM isn't running.");

    private string _RuntimePath(Guid id) => Path.Combine(repository.GetDirectory(id), RuntimeFileName);

    private void _Raise(Guid id) => StatusChanged?.Invoke(id);

    /// <summary>Drops the QMP connections without stopping any VM; they keep running for the next session.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Qmp is { } qmp)
                await qmp.DisposeAsync();
        }
    }

    private sealed class Session(Guid vmId)
    {
        public Guid VmId { get; } = vmId;

        public VmStatus Status { get; set; }

        public VmRuntimeState? State { get; set; }

        public Process? Process { get; set; }

        public QmpClient? Qmp { get; set; }

        /// <summary>Set by PauseAsync so the STOP it causes isn't mistaken for an unexpected one.</summary>
        public bool PauseRequested;

        /// <summary>Errors already in qemu.log when the app connected; only later ones signal a new problem.</summary>
        public int KnownLogErrors { get; set; }

        /// <summary>Completes when the session's cleanup has finished.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class VmStartException(string message) : Exception(message);
