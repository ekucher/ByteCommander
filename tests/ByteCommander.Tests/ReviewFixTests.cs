using System.Reflection;
using System.Text;
using System.Xml.Linq;
using ByteCommander.Services;
using ByteCommander.Viewer;

namespace ByteCommander.Tests;

/// <summary>Регресійні тести на знахідки другого код-рев'ю.</summary>
[Collection(WorkspaceCollection.Name)]
public sealed class ReviewFixTests(TestWorkspace ws)
{
    string WriteFile(string name, byte[] bytes) => ws.WriteFile(nameof(ReviewFixTests), name, bytes);

    static TextRowSource OpenIndexed(string path, Encoding enc)
    {
        var src = new TextRowSource(path, enc);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!src.IsComplete && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(src.IsComplete);
        return src;
    }

    [Fact]
    public void Find_MatchAcrossSoftBreak_IsFoundAndHighlightedOnBothRows()
    {
        // Перший «рядок» — рівно MaxLineBytes байтів; «NEEDLE» розрізане м'яким розривом: NEE | DLE.
        var text = new string('x', TextRowSource.MaxLineBytes - 3) + "NEEDLE" + new string('y', 10);
        using var src = OpenIndexed(WriteFile("span.txt", Encoding.ASCII.GetBytes(text)), new UTF8Encoding(false));

        var m = src.Find("needle", 0, 0, default);

        Assert.Equal(new TextMatch(0, TextRowSource.MaxLineBytes - 3, 6, SpillOver: 3), m);
        Assert.Equal(new[] { (TextRowSource.MaxLineBytes - 3, 3) }, src.GetHighlights(0, m!.Value).ToArray());
        Assert.Equal(new[] { (0, 3) }, src.GetHighlights(1, m.Value).ToArray());
        Assert.Null(src.Find("needle", 0, m.Value.Column + 1, default));
    }

    [Fact]
    public void Utf16_SoftBreak_DoesNotSplitSurrogatePair()
    {
        // Емодзі (сурогатна пара) стоїть рівно на межі 16 КБ: розрив має перенестись перед ним.
        var enc = new UnicodeEncoding(false, true);
        var text = new string('a', TextRowSource.MaxLineBytes / 2 - 1) + "😀" + "tail";
        using var src = OpenIndexed(WriteFile("surrogate.txt", enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray()), enc);

        var rows = src.GetRows(0, 10);
        Assert.Equal(TextRowSource.MaxLineBytes / 2 - 1, rows[0].Length);
        Assert.Equal("😀tail", rows[1]);
        Assert.Equal(text, string.Concat(rows));
    }

    [Fact]
    public void Find_BeyondIndexedRows_ReportsTrueRowNumber()
    {
        // Пошук не залежить від фонового індексу: номер рядка правильний навіть до завершення індексації.
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append(i == 199_990 ? "MARKER\n" : "row\n");
        using var src = new TextRowSource(WriteFile("early.txt", Encoding.ASCII.GetBytes(sb.ToString())), new UTF8Encoding(false));

        Assert.Equal(new TextMatch(199_990, 0, 6), src.Find("marker", 0, 0, default));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("\n", 1)]
    [InlineData("a\r\nb\r\n", 2)]
    public void CountLines_MatchesVirtualViewerRowCount(string text, int expected)
    {
        Assert.Equal(expected, TextEncodings.CountLines(text));
        using var src = OpenIndexed(WriteFile($"count{text.Length}{expected}.txt", Encoding.UTF8.GetBytes(text)), new UTF8Encoding(false));
        Assert.Equal(expected, src.RowCount);
    }

    [Fact]
    public void WriteViaTemp_KeepMetadata_PreservesAttributesAndReplacesContent()
    {
        var p = WriteFile("hidden.txt", Encoding.UTF8.GetBytes("old"));
        File.SetAttributes(p, FileAttributes.Hidden);
        var created = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetCreationTimeUtc(p, created);

        Util.WriteViaTemp(p, tmp => File.WriteAllText(tmp, "new"), keepDestinationMetadata: true);

        Assert.Equal("new", File.ReadAllText(p));
        Assert.True(File.GetAttributes(p).HasFlag(FileAttributes.Hidden));
        Assert.Equal(created, File.GetCreationTimeUtc(p));
        Assert.True(TestWorkspace.NoTempFiles(Path.GetDirectoryName(p)!));
    }

[Fact]
    public void WriteViaTemp_KeepMetadata_OnHardLinkedFile_UpdatesAllNames()
    {
        // Заміна файлу (File.Replace) розірвала б жорстке посилання: друге ім'я лишилося б зі старим вмістом.
        var a = WriteFile("hard-a.txt", Encoding.UTF8.GetBytes("old"));
        var b = Path.Combine(Path.GetDirectoryName(a)!, "hard-b.txt");
        if (File.Exists(b)) File.Delete(b);
        TestWorkspace.CreateHardLink(b, a);
        File.SetAttributes(a, FileAttributes.Hidden);

        Util.WriteViaTemp(a, tmp => File.WriteAllText(tmp, "new content"), keepDestinationMetadata: true);

        Assert.Equal("new content", File.ReadAllText(a));
        Assert.Equal("new content", File.ReadAllText(b));
        Assert.True(File.GetAttributes(a).HasFlag(FileAttributes.Hidden));
        Assert.Equal(2, ByteCommander.Services.NativeMethods.HardLinkCount(a));
        Assert.True(TestWorkspace.NoTempFiles(Path.GetDirectoryName(a)!));
    }

    [Fact]
    public void WriteViaTemp_KeepMetadata_ShorterContent_TruncatesHardLinkedFile()
    {
        var a = WriteFile("hard-short.txt", Encoding.UTF8.GetBytes("a much longer original text"));
        var b = Path.Combine(Path.GetDirectoryName(a)!, "hard-short-b.txt");
        if (File.Exists(b)) File.Delete(b);
        TestWorkspace.CreateHardLink(b, a);

        Util.WriteViaTemp(a, tmp => File.WriteAllText(tmp, "short"), keepDestinationMetadata: true);

        Assert.Equal("short", File.ReadAllText(b));
    }

    [Fact]
    public void WriteViaTemp_WhenWriteFails_LeavesOriginalIntact()
    {
        var p = WriteFile("keep.txt", Encoding.UTF8.GetBytes("original"));

        Assert.Throws<IOException>(() => Util.WriteViaTemp(p, tmp =>
        {
            File.WriteAllText(tmp, "half-written");
            throw new IOException("Недостатньо місця на диску");
        }, keepDestinationMetadata: true));

        Assert.Equal("original", File.ReadAllText(p));
        Assert.True(TestWorkspace.NoTempFiles(Path.GetDirectoryName(p)!));
    }
}

public sealed class TextEncodingsTests
{
    [Fact]
    public void EveryMenuChoice_HasMatchingName()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var choice in TextEncodings.Choices)
            Assert.Equal(choice.Name, TextEncodings.NameOf(choice.Create()));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, "UTF-8 BOM")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "UTF-16 LE")]
    [InlineData(new byte[] { 0xD0, 0x9F, 0xD1, 0x80 }, "UTF-8")]          // «Пр» у UTF-8
    [InlineData(new byte[] { 0xCF, 0xF0, 0xE8, 0xE2 }, "Windows-1251")]   // «Прив» у 1251
    public void Detect(byte[] head, string expected)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(expected, TextEncodings.NameOf(TextEncodings.Detect(head, truncated: false)));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE }, false)]
    [InlineData(new byte[] { 0xFE, 0xFF }, true)]
    public void Utf16Bom_IsRecognised(byte[] head, bool bigEndian)
    {
        Assert.Equal(bigEndian, TextEncodings.Utf16BomBigEndian(head));
        Assert.False(TextEncodings.LooksBinary(head.Concat(new byte[] { 0x41, 0x00 }).ToArray()));
        Assert.Equal(bigEndian ? "UTF-16 BE" : "UTF-16 LE", TextEncodings.NameOf(TextEncodings.Detect(head, truncated: false)));
    }

    [Fact]
    public void PreambleLength_SkipsUtf8BomEvenForUtf8WithoutBom()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF, 0x41 };
        Assert.Equal(3, TextEncodings.PreambleLength(bom, new UTF8Encoding(false)));
        Assert.Equal(3, TextEncodings.PreambleLength(bom, new UTF8Encoding(true)));
        Assert.Equal(0, TextEncodings.PreambleLength(new byte[] { 0x41 }, new UTF8Encoding(true)));
    }
}

public sealed class ThemeKeysTests
{
    [Fact]
    public void EveryStyleKeyConstant_IsDefinedInControlsXaml()
    {
        var constants = typeof(StyleKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(constants);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FileManager.csproj"))) dir = dir.Parent;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = XDocument.Load(Path.Combine(dir!.FullName, "Themes", "Controls.xaml")).Root!.Elements()
            .Select(e => (string?)e.Attribute(x + "Key")).ToHashSet();
        foreach (var c in constants) Assert.Contains(c, keys);
    }

    [Fact]
    public void EveryThemeKeyConstant_ExistsInBothPalettes()
    {
        var constants = typeof(ThemeKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(constants);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var palette in new[] { "Colors.Dark.xaml", "Colors.Light.xaml" })
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FileManager.csproj"))) dir = dir.Parent;
            var keys = XDocument.Load(Path.Combine(dir!.FullName, "Themes", palette)).Root!.Elements()
                .Select(e => (string?)e.Attribute(x + "Key")).ToHashSet();
            foreach (var c in constants) Assert.Contains(c, keys);
        }
    }
}
