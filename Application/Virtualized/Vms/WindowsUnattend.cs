using System.Xml.Linq;

namespace Virtualized.Vms;

/// <summary>
/// The "Windows 11 compatibility" answer file. Windows Setup reads autounattend.xml from the root of a
/// removable drive; this one only sets the LabConfig registry values that make Setup skip its hardware
/// checks (the same approach Rufus uses), so the rest of the installation stays interactive.
/// </summary>
public static class WindowsUnattend
{
    public const string FileName = "autounattend.xml";

    /// <summary>
    /// TPM and Secure Boot because the host may not provide them; CPU because the model used under WHPX
    /// (Skylake) predates Windows 11's supported list; RAM in case the VM was given less than 4 GiB.
    /// </summary>
    public static readonly string[] BypassedChecks = ["BypassTPMCheck", "BypassSecureBootCheck", "BypassCPUCheck", "BypassRAMCheck"];

    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public static string Build(string architecture)
    {
        var commands = BypassedChecks.Select((check, i) =>
            new XElement(Unattend + "RunSynchronousCommand",
                new XAttribute(Wcm + "action", "add"),
                new XElement(Unattend + "Order", i + 1),
                new XElement(Unattend + "Path", $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {check} /t REG_DWORD /d 1 /f")));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Unattend + "unattend",
                new XElement(Unattend + "settings",
                    new XAttribute("pass", "windowsPE"),
                    new XElement(Unattend + "component",
                        new XAttribute("name", "Microsoft-Windows-Setup"),
                        new XAttribute("processorArchitecture", architecture == "aarch64" ? "arm64" : "amd64"),
                        new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                        new XAttribute("language", "neutral"),
                        new XAttribute("versionScope", "nonSxS"),
                        new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
                        new XElement(Unattend + "RunSynchronous", commands)))));

        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString();
    }

    /// <summary>XDocument writes the encoding of its writer into the declaration; this keeps it utf-8.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}
