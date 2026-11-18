using Virtualized.Updates;

namespace Virtualized.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    public void ParsesReleaseTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseTag(tag));

    [Fact]
    public void TagWithoutBuildNumberIsNotNewerThanTheSameVersion() =>
        Assert.False(UpdateService.ParseTag("v1.0") > UpdateService.ParseTag("1.0.0"));

    [Fact]
    public void ReadsTheHashFromSha256sumOutput() =>
        Assert.Equal("abc123", UpdateService.ParseChecksum("abc123  virtualized_setup_w64_1-0-0.exe\n"));

    [Fact]
    public void EmptyChecksumFileIsRejected() =>
        Assert.Throws<InvalidDataException>(() => UpdateService.ParseChecksum(" \n"));
}
