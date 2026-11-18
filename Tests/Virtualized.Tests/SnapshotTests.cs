using Virtualized.Qemu;
using Virtualized.Vms;

namespace Virtualized.Tests;

public class SnapshotTests
{
    private static readonly VmSnapshot Existing = new("before update", DateTime.UtcNow, 0);

    [Theory]
    [InlineData("before update", "already")]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    [InlineData("42", "numbers")]
    public void RejectsUnusableNames(string name, string reason)
    {
        Assert.Contains(reason, SnapshotService.ValidateName(name, [Existing]));
    }

    [Theory]
    [InlineData("after update")]
    [InlineData("v2")]
    [InlineData(" clean install ")]
    public void AcceptsGoodNames(string name)
    {
        Assert.Null(SnapshotService.ValidateName(name, [Existing]));
    }

    [Fact]
    public void TooLongNamesAreRejected()
    {
        Assert.NotNull(SnapshotService.ValidateName(new string('a', SnapshotService.MaxNameLength + 1), []));
    }

    [Fact]
    public void ParsesQemuImgInfo()
    {
        const string json = """
            {
                "snapshots": [
                    { "icount": 0, "vm-state-size": 0, "date-sec": 1794863627, "date-nsec": 500000000, "vm-clock-sec": 0, "vm-clock-nsec": 0, "id": "1", "name": "offline" },
                    { "vm-state-size": 46560451, "date-sec": 1794863652, "date-nsec": 0, "vm-clock-sec": 15, "vm-clock-nsec": 253000000, "id": "2", "name": "live" }
                ],
                "virtual-size": 1073741824, "filename": "disk0.qcow2", "format": "qcow2"
            }
            """;

        var snapshots = QemuImgSnapshots.ParseInfo(json);

        Assert.Equal(["offline", "live"], snapshots.Select(s => s.Name));
        Assert.Equal(0, snapshots[0].VmStateSize);
        Assert.Equal(46560451, snapshots[1].VmStateSize);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1794863627).AddMilliseconds(500), snapshots[0].CreatedUtc);
    }

    [Fact]
    public void ImagesWithoutSnapshotsHaveNoSnapshotsField()
    {
        Assert.Empty(QemuImgSnapshots.ParseInfo("""{ "virtual-size": 1, "format": "qcow2" }"""));
    }

    [Theory]
    [InlineData("whpx", false)]
    [InlineData("tcg", true)]
    [InlineData("kvm", true)]
    public void WhpxCantSaveMemory(string accelerator, bool expected)
    {
        Assert.Equal(expected, SnapshotService.SupportsMemorySnapshots(accelerator));
    }
}
