using System.Xml.Linq;
using Virtualized.Vms;

namespace Virtualized.Tests;

public class WindowsUnattendTests
{
    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";

    [Fact]
    public void SetsEveryLabConfigBypassInWindowsPE()
    {
        var document = XDocument.Parse(WindowsUnattend.Build("x86_64"));
        var settings = Assert.Single(document.Root!.Elements(Unattend + "settings"));
        var commands = settings.Descendants(Unattend + "RunSynchronousCommand").ToList();

        Assert.Equal("windowsPE", settings.Attribute("pass")!.Value);
        Assert.Equal(WindowsUnattend.BypassedChecks.Length, commands.Count);
        Assert.Equal(
            WindowsUnattend.BypassedChecks.Select(c => $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {c} /t REG_DWORD /d 1 /f"),
            commands.Select(c => c.Element(Unattend + "Path")!.Value));
        Assert.Equal(Enumerable.Range(1, commands.Count).Select(i => i.ToString()), commands.Select(c => c.Element(Unattend + "Order")!.Value));
    }

    [Theory]
    [InlineData("x86_64", "amd64")]
    [InlineData("aarch64", "arm64")]
    public void ComponentMatchesTheGuestArchitecture(string architecture, string expected)
    {
        var component = XDocument.Parse(WindowsUnattend.Build(architecture)).Descendants(Unattend + "component").Single();

        Assert.Equal(expected, component.Attribute("processorArchitecture")!.Value);
        Assert.Equal("Microsoft-Windows-Setup", component.Attribute("name")!.Value);
    }

    [Fact]
    public void DeclaresUtf8()
    {
        Assert.StartsWith("""<?xml version="1.0" encoding="utf-8"?>""", WindowsUnattend.Build("x86_64"));
    }

    [Fact]
    public void BypassesTheChecksAWhpxVmFails()
    {
        Assert.Contains("BypassTPMCheck", WindowsUnattend.BypassedChecks);
        Assert.Contains("BypassSecureBootCheck", WindowsUnattend.BypassedChecks);
        Assert.Contains("BypassCPUCheck", WindowsUnattend.BypassedChecks);
    }
}
