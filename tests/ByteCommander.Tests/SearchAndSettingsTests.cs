using System.Text;
using System.Text.Json;
using System.Windows.Input;
using ByteCommander.Services;

namespace ByteCommander.Tests;

[Collection(WorkspaceCollection.Name)]
public sealed class ContentSearchTests(TestWorkspace ws)
{
    [Theory]
    [InlineData("u8.txt", "СВІТ")]
    [InlineData("w1251.txt", "світ")]
    [InlineData("cp866.txt", "привет")]
    public void FindsTextInEncoding(string file, string query)
    {
        var d = Prepare();
        Assert.True(SearchWindow.FileContains(Path.Combine(d, file), query, default));
    }

    [Theory]
    [InlineData("other.txt", "світ")]
    [InlineData("w1251.txt", "zzz")]
    public void NoFalsePositives(string file, string query)
    {
        var d = Prepare();
        Assert.False(SearchWindow.FileContains(Path.Combine(d, file), query, default));
    }

    string Prepare()
    {
        var d = Path.Combine(ws.Root, nameof(ContentSearchTests));
        if (Directory.Exists(d)) return d;
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "u8.txt"), "початок Привіт світ кінець", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(d, "w1251.txt"), Encoding.GetEncoding(1251).GetBytes("початок Привіт світ кінець"));
        File.WriteAllBytes(Path.Combine(d, "cp866.txt"), Encoding.GetEncoding(866).GetBytes("начало Привет мир"));
        File.WriteAllText(Path.Combine(d, "other.txt"), "nothing here");
        return d;
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void OldSettingsWithStringSortKey_AreReadIntoEnum()
    {
        var ps = JsonSerializer.Deserialize<PanelState>("""{"Tabs":["C:\\"],"ActiveTab":0,"SortBy":"Size","SortDesc":true}""")!;

        Assert.Equal(SortKey.Size, ps.SortBy);
        Assert.True(ps.SortDesc);
        Assert.Contains("\"SortBy\":\"Size\"", JsonSerializer.Serialize(ps));
    }
}

public sealed class UtilTests
{
    [Theory]
    [InlineData(1, "файл")]
    [InlineData(3, "файли")]
    [InlineData(5, "файлів")]
    [InlineData(11, "файлів")]
    [InlineData(21, "файл")]
    [InlineData(112, "файлів")]
    public void Plural_Ukrainian(long n, string expected) =>
        Assert.Equal(expected, Util.Plural(n, "файл", "файли", "файлів"));

    [Theory]
    [InlineData("*.*", "report.txt", true)]
    [InlineData("*.cs; *.xaml", "App.xaml", true)]
    [InlineData("report", "my-report.txt", true)]
    [InlineData("*.cs", "App.xaml", false)]
    public void Masks(string mask, string name, bool expected) =>
        Assert.Equal(expected, Util.MatchAny(Util.SplitMasks(mask), name));

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    public void IsSubPathOf(string child, string parent, bool expected) =>
        Assert.Equal(expected, Util.IsSubPathOf(child, parent));

    [Theory]
    [InlineData(Key.F5, ModifierKeys.None, "F5")]
    [InlineData(Key.P, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+P")]
    [InlineData(Key.Add, ModifierKeys.None, "Num +")]
    [InlineData(Key.Oem3, ModifierKeys.Control, "Ctrl+`")]
    public void HotkeyDisplay(Key key, ModifierKeys mods, string expected) =>
        Assert.Equal(expected, Hotkey.Of(key, mods).ToString());
}
