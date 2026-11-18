using System.Text.Json.Nodes;
using Virtualized.Vms;

namespace Virtualized.Tests;

public class VmManagerTests
{
    [Fact]
    public void ExtractsQemuErrorsWithoutProgramPathOrWarnings()
    {
        const string log = """
            C:\Program Files\qemu\qemu-system-x86_64.exe: warning: Ignoring request for interrupt vector 0
            VNC server running on 127.0.0.1:5901
            C:\Program Files\qemu\qemu-system-x86_64.exe: -netdev user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22: Could not set up host forwarding rule 'tcp:127.0.0.1:2222-:22'
            """;

        Assert.Equal(
            ["-netdev user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22: Could not set up host forwarding rule 'tcp:127.0.0.1:2222-:22'"],
            VmManager.ExtractErrors(log));
    }

    [Fact]
    public void ExtractsErrorsFromUnixPaths()
    {
        Assert.Equal(
            ["-drive file=/vms/disk0.qcow2: Could not open '/vms/disk0.qcow2': No such file or directory"],
            VmManager.ExtractErrors("/usr/bin/qemu-system-aarch64: -drive file=/vms/disk0.qcow2: Could not open '/vms/disk0.qcow2': No such file or directory\n"));
    }

    [Fact]
    public void InformationalLinesAreNotErrors()
    {
        Assert.Empty(VmManager.ExtractErrors("VNC server running on 127.0.0.1:5901\r\n"));
    }

    [Theory]
    [InlineData(VmStatus.Running, "STOP", VmStatus.Paused)]
    [InlineData(VmStatus.Paused, "RESUME", VmStatus.Running)]
    [InlineData(VmStatus.Running, "SUSPEND", VmStatus.Suspended)]
    [InlineData(VmStatus.Suspended, "WAKEUP", VmStatus.Running)]
    [InlineData(VmStatus.Running, "SHUTDOWN", VmStatus.Stopping)]
    [InlineData(VmStatus.Running, "POWERDOWN", VmStatus.Running)]
    [InlineData(VmStatus.Running, "RESET", VmStatus.Running)]
    [InlineData(VmStatus.Paused, "SOMETHING_ELSE", VmStatus.Paused)]
    public void EventsChangeStatus(VmStatus current, string evt, VmStatus expected)
    {
        Assert.Equal(expected, VmManager.ApplyEvent(current, evt, null).Status);
    }

    [Fact]
    public void PanicMarksTheVmCrashedAndTheFollowingStopKeepsIt()
    {
        var panicked = VmManager.ApplyEvent(VmStatus.Running, "GUEST_PANICKED", JsonNode.Parse("""{"action": "pause"}"""));
        var stopped = VmManager.ApplyEvent(panicked.Status, "STOP", null);

        Assert.Equal(VmStatus.Crashed, panicked.Status);
        Assert.NotNull(panicked.Error);
        Assert.Equal(VmStatus.Crashed, stopped.Status);
    }

    [Fact]
    public void DiskFullPausesWithAnExplanation()
    {
        var data = JsonNode.Parse("""{"device": "", "qom-path": "/machine/peripheral/disk0", "operation": "write", "action": "stop", "nospace": true, "reason": "No space left on device"}""");

        var outcome = VmManager.ApplyEvent(VmStatus.Running, "BLOCK_IO_ERROR", data);

        Assert.Equal(VmStatus.Paused, outcome.Status);
        Assert.Contains("disk is full", outcome.Error);
    }

    [Fact]
    public void DiskErrorsTheGuestHandlesItselfDontPause()
    {
        var data = JsonNode.Parse("""{"device": "", "qom-path": "/x", "operation": "read", "action": "report", "reason": "Input/output error"}""");

        Assert.Equal(VmStatus.Running, VmManager.ApplyEvent(VmStatus.Running, "BLOCK_IO_ERROR", data).Status);
    }

    [Fact]
    public void ResumingClearsTheProblem()
    {
        Assert.True(VmManager.ApplyEvent(VmStatus.Paused, "RESUME", null).ClearError);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "a", true)]
    [InlineData(true, "a", false)]
    [InlineData(true, "b", true)]
    [InlineData(true, null, false)]
    public void EfiVarsAreRecreatedWhenMissingOrFromAnotherFirmware(bool exists, string? recorded, bool expected)
    {
        Assert.Equal(expected, VmManager.ShouldRecreateEfiVars(exists, recorded, "a"));
    }

    [Fact]
    public void UnrequestedStopWithNewLogErrorsIsACrash()
    {
        // What WHPX does when a vCPU fails: STOP, run state "paused", and an error in the log.
        var outcome = VmManager.ClassifyUnexpectedStop("paused", ["WHPX: Unexpected VP exit code 4"]);

        Assert.Equal(VmStatus.Crashed, outcome.Status);
        Assert.Contains("Unexpected VP exit code 4", outcome.Error);
    }

    [Fact]
    public void UnrequestedStopWithoutErrorsIsAPause()
    {
        var outcome = VmManager.ClassifyUnexpectedStop("paused", []);

        Assert.Equal(VmStatus.Paused, outcome.Status);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void LoggedErrorsDontTurnARunningVmIntoACrash()
    {
        Assert.Equal(VmStatus.Running, VmManager.ClassifyUnexpectedStop("running", ["something old"]).Status);
    }

    [Fact]
    public void RunStateErrorsWinOverLogErrors()
    {
        var outcome = VmManager.ClassifyUnexpectedStop("io-error", ["unrelated"]);

        Assert.Equal(VmStatus.Paused, outcome.Status);
        Assert.Contains("disk error", outcome.Error);
    }

    [Theory]
    [InlineData("running", VmStatus.Running, false)]
    [InlineData("paused", VmStatus.Paused, false)]
    [InlineData("prelaunch", VmStatus.Paused, false)]
    [InlineData("suspended", VmStatus.Suspended, false)]
    [InlineData("guest-panicked", VmStatus.Crashed, true)]
    [InlineData("internal-error", VmStatus.Crashed, true)]
    [InlineData("io-error", VmStatus.Paused, true)]
    [InlineData("shutdown", VmStatus.Stopping, false)]
    public void RunStatesMapToStatus(string runState, VmStatus expected, bool hasError)
    {
        var outcome = VmManager.MapRunState(runState);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(hasError, outcome.Error is not null);
    }
}
