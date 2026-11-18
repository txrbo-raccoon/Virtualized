using Virtualized.Qemu;

namespace Virtualized.Tests;

public class SwtpmTests
{
    [Fact]
    public void RunsATpm2ThatExitsWithQemu()
    {
        var args = Swtpm.BuildArguments("/vms/a/tpm", "/vms/a/swtpm.sock", "/vms/a/swtpm.log");

        Assert.Equal("socket", args[0]);
        Assert.Contains("--tpm2", args);
        Assert.Contains("--terminate", args);
        Assert.Equal("dir=/vms/a/tpm", args[args.ToList().IndexOf("--tpmstate") + 1]);
        Assert.Equal("type=unixio,path=/vms/a/swtpm.sock", args[args.ToList().IndexOf("--ctrl") + 1]);
    }

    [Fact]
    public void SocketGoesNextToTheVmWhenThePathIsShortEnough()
    {
        var id = Guid.NewGuid();

        Assert.Equal(Path.Combine("/home/me/.config/Virtualized/vms/x", "swtpm.sock"), Swtpm.SocketPath("/home/me/.config/Virtualized/vms/x", id));
    }

    [Fact]
    public void LongVmPathsUseTheTempFolderForTheSocket()
    {
        var id = Guid.NewGuid();
        var deep = "/Users/someone/Library/Application Support/Virtualized/vms/" + new string('a', 64);

        var socket = Swtpm.SocketPath(deep, id);

        Assert.StartsWith(Path.GetTempPath(), socket);
        Assert.EndsWith($"sqemu-{id:N}.sock", socket);
    }
}
