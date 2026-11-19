using ByteCommander.Services;

namespace ByteCommander.Tests;

/// <summary>Тести на розпізнавання null-пристрою та нормалізацію шляхів у режимі --diff.</summary>
public sealed class DiffArgumentsTests
{
    [Theory]
    [InlineData("nul", true)]
    [InlineData("NUL", true)]
    [InlineData("Nul", true)]
    [InlineData("/dev/null", true)]
    [InlineData(@"\\.\nul", true)]
    [InlineData("NUL:", true)]
    [InlineData("C:\\nul.txt", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData("a.txt", false)]
    public void IsNullDevice_RecognizesNullDeviceOnly(string arg, bool expected)
    {
        Assert.Equal(expected, DiffArguments.IsNullDevice(arg));
    }

    [Fact]
    public void TryGetFullPath_ReturnsNull_ForInvalidPath()
    {
        Assert.Null(DiffArguments.TryGetFullPath("a\0b"));
    }

    [Fact]
    public void TryGetFullPath_ReturnsFullPath_ForRelativeFile()
    {
        var result = DiffArguments.TryGetFullPath("a.txt");

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath("a.txt"), result);
        Assert.True(Path.IsPathRooted(result));
    }
}
