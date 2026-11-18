using System.Text;
using System.Xml.Linq;
using ByteCommander.Viewer;

namespace ByteCommander.Tests;

[Collection(WorkspaceCollection.Name)]
public sealed class TextRowSourceTests(TestWorkspace ws)
{
    string WriteFile(string name, byte[] bytes)
    {
        var dir = Path.Combine(ws.Root, nameof(TextRowSourceTests));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    static TextRowSource Open(string path, Encoding enc)
    {
        var src = new TextRowSource(path, enc);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!src.IsComplete && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(src.IsComplete, "індексація не завершилась");
        return src;
    }

    [Fact]
    public void SplitsLines_CrLfAndLf_NoTrailingEmptyRow()
    {
        var p = WriteFile("mixed.txt", Encoding.UTF8.GetBytes("перший\r\nдругий\nтретій\n"));
        using var src = Open(p, new UTF8Encoding(false));

        Assert.Equal(3, src.RowCount);
        Assert.Equal(new[] { "перший", "другий", "третій" }, src.GetRows(0, 10));
    }

    [Fact]
    public void LastLineWithoutNewline_IsIncluded()
    {
        var p = WriteFile("tail.txt", Encoding.UTF8.GetBytes("a\nb"));
        using var src = Open(p, new UTF8Encoding(false));
        Assert.Equal(new[] { "a", "b" }, src.GetRows(0, 10));
    }

    [Fact]
    public void EmptyFile_HasNoRows()
    {
        var p = WriteFile("empty.txt", Array.Empty<byte>());
        using var src = Open(p, new UTF8Encoding(false));
        Assert.Equal(0, src.RowCount);
        Assert.Empty(src.GetRows(0, 10));
    }

    [Fact]
    public void Utf8Bom_IsSkipped()
    {
        var p = WriteFile("bom.txt", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Привіт")).ToArray());
        using var src = Open(p, new UTF8Encoding(true));
        Assert.Equal("Привіт", Assert.Single(src.GetRows(0, 10)));
    }

    [Fact]
    public void Utf16LittleEndian_IsSplitOnTwoByteNewline()
    {
        var enc = new UnicodeEncoding(false, true);
        var p = WriteFile("u16.txt", enc.GetPreamble().Concat(enc.GetBytes("один\r\nдва\nтри")).ToArray());
        using var src = Open(p, enc);
        Assert.Equal(new[] { "один", "два", "три" }, src.GetRows(0, 10));
    }

    [Fact]
    public void Windows1251_IsDecoded()
    {
        var enc = Encoding.GetEncoding(1251);
        var p = WriteFile("w1251.txt", enc.GetBytes("Їжак\nҐанок"));
        using var src = Open(p, enc);
        Assert.Equal(new[] { "Їжак", "Ґанок" }, src.GetRows(0, 10));
    }

    [Fact]
    public void Tabs_AreExpandedToStopsOfFour()
    {
        var p = WriteFile("tabs.txt", Encoding.UTF8.GetBytes("a\tb\n\tc"));
        using var src = Open(p, new UTF8Encoding(false));
        Assert.Equal(new[] { "a   b", "    c" }, src.GetRows(0, 10));
    }

    [Fact]
    public void VeryLongLine_IsSoftWrapped_WithoutBreakingUtf8()
    {
        // 3-байтові символи: розрив на 16 КБ мав би розрізати символ, якщо не вирівнювати межу.
        var line = new string('€', 20_000);
        var p = WriteFile("long.txt", Encoding.UTF8.GetBytes(line + "\nend"));
        using var src = Open(p, new UTF8Encoding(false));

        var rows = src.GetRows(0, 100);
        Assert.Equal("end", rows[^1]);
        var joined = string.Concat(rows.Take(rows.Count - 1));
        Assert.Equal(line, joined);
        Assert.All(rows.Take(rows.Count - 1), r => Assert.True(Encoding.UTF8.GetByteCount(r) <= TextRowSource.MaxLineBytes));
        Assert.DoesNotContain('�', joined);
    }

    [Fact]
    public void RowsFarFromStart_AreReadViaCheckpoints()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append("рядок ").Append(i).Append('\n');
        var p = WriteFile("many.txt", Encoding.UTF8.GetBytes(sb.ToString()));
        using var src = Open(p, new UTF8Encoding(false));

        Assert.Equal(5000, src.RowCount);
        Assert.Equal(new[] { "рядок 1000", "рядок 1001", "рядок 1002" }, src.GetRows(1000, 3));
        Assert.Equal(new[] { "рядок 4999" }, src.GetRows(4999, 5));
        Assert.Empty(src.GetRows(5000, 5));
    }

    [Fact]
    public void Find_ReturnsRowAndColumn_AndContinuesAfterMatch()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3000; i++) sb.Append(i == 1500 || i == 2500 ? "тут ПОМИЛКА тут\n" : "все добре\n");
        var p = WriteFile("find.txt", Encoding.UTF8.GetBytes(sb.ToString()));
        using var src = Open(p, new UTF8Encoding(false));

        var first = src.Find("помилка", 0, 0, default);
        Assert.Equal(new TextMatch(1500, 4, 7), first);
        var second = src.Find("помилка", first!.Value.Row, first.Value.Column + 1, default);
        Assert.Equal(new TextMatch(2500, 4, 7), second);
        Assert.Null(src.Find("помилка", 2500, 5, default));
        Assert.Equal(new[] { (4, 7) }, src.GetHighlights(1500, first.Value).ToArray());
    }

    [Fact]
    public void Find_CanBeCancelled()
    {
        var p = WriteFile("cancel.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 2_000_000))));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => src.Find("немає", 0, 0, cts.Token));
    }
}

[Collection(WorkspaceCollection.Name)]
public sealed class HexRowSourceTests(TestWorkspace ws)
{
    string WriteFile(string name, byte[] bytes)
    {
        var dir = Path.Combine(ws.Root, nameof(HexRowSourceTests));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void FormatsRows_AndCountsPartialLastRow()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        bytes[16] = (byte)'A';
        using var src = new HexRowSource(WriteFile("small.bin", bytes));

        Assert.Equal(2, src.RowCount);
        var rows = src.GetRows(0, 10);
        Assert.Equal("00000000  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  ················", rows[0]);
        Assert.StartsWith("00000010  41 11 12 13 ", rows[1]);
        Assert.EndsWith("A···", rows[1]);
    }

    [Fact]
    public void FindsHexBytes_AcrossRowBoundary_AndHighlightsBothRows()
    {
        var bytes = new byte[64];
        bytes[14] = 0x4D; bytes[15] = 0x5A; bytes[16] = 0x90;   // збіг перетинає межу рядків 0 і 1
        using var src = new HexRowSource(WriteFile("pattern.bin", bytes));

        var m = src.Find("4D 5A 90", 0, 0, default);
        Assert.Equal(new TextMatch(0, 14, 3), m);
        Assert.Equal(4, src.GetHighlights(0, m!.Value).Count());   // 2 байти × (hex + ascii)
        Assert.Equal(2, src.GetHighlights(1, m.Value).Count());    // 1 байт × (hex + ascii)
        Assert.Null(src.Find("4D 5A 90", 0, 15, default));
    }

    [Fact]
    public void FindsText_InLargeFile_BeyondFirstBuffer()
    {
        var bytes = new byte[3 * 1024 * 1024];
        var marker = Encoding.UTF8.GetBytes("NEEDLE");
        marker.CopyTo(bytes, 2_500_000);
        using var src = new HexRowSource(WriteFile("big.bin", bytes));

        var m = src.Find("NEEDLE", 0, 0, default);
        Assert.Equal(2_500_000L, m!.Value.Row * HexRowSource.BytesPerRow + m.Value.Column);
    }

    [Theory]
    [InlineData("4D 5A", new byte[] { 0x4D, 0x5A })]
    [InlineData("4d5a90", new byte[] { 0x4D, 0x5A, 0x90 })]
    [InlineData("MZ", new byte[] { 0x4D, 0x5A })]
    [InlineData("cafe", new byte[] { 0xCA, 0xFE })]
    public void ParsesQuery(string q, byte[] expected) => Assert.Equal(expected, HexRowSource.ParseQuery(q));
}

public sealed class ThemeTests
{
    static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FileManager.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    static HashSet<string> Keys(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(RepoFile(file)).Root!.Elements().Select(e => (string?)e.Attribute(x + "Key") ?? "").ToHashSet();
    }

    [Fact]
    public void DarkAndLightPalettes_DefineTheSameKeys()
    {
        var dark = Keys(@"Themes\Colors.Dark.xaml");
        var light = Keys(@"Themes\Colors.Light.xaml");
        Assert.Equal(dark.OrderBy(k => k), light.OrderBy(k => k));
    }

    [Fact]
    public void Styles_ReferenceOnlyDefinedColors_AndDynamically()
    {
        var keys = Keys(@"Themes\Colors.Dark.xaml");
        foreach (var file in new[] { @"Themes\Controls.xaml", @"Controls\FilePanel.xaml", @"MainWindow.xaml" })
        {
            var xaml = File.ReadAllText(RepoFile(file));
            // Кольори мають підтягуватись динамічно, інакше зміна теми не дійде до цього елемента.
            Assert.DoesNotMatch(@"StaticResource \w+Brush\}", xaml);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xaml, @"DynamicResource (\w+)\}"))
                Assert.Contains(m.Groups[1].Value, keys);
        }
    }
}
