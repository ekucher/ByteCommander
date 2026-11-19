using System.Text;
using ByteCommander.Compare;
using ByteCommander.Services;

namespace ByteCommander.Tests;

/// <summary>
/// Порівняння текстів: перенесення блоку (без застарілого diff), відмінності лише формату,
/// збереження байт у байт (кодування, BOM, переноси), непредставні символи, спільні допоміжні методи.
/// </summary>
public sealed class TextCompareFixTests : IDisposable
{
    static readonly Encoding Utf8 = new UTF8Encoding(false);
    static readonly Encoding Utf8Bom = new UTF8Encoding(true);
    static Encoding Cp1251 => TextEncodings.CodePage(1251);

    readonly string dir = Path.Combine(Path.GetTempPath(), "ByteCommander.Tests", "tc-" + Guid.NewGuid().ToString("N")[..8]);

    public TextCompareFixTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static TextDiffResult Diff(TextLines l, TextLines r) => TextDiff.Compare(l.Lines, r.Lines, new DiffOptions());

    // ------------------------------------------------------------------ TextLines

    [Fact]
    public void Parse_KeepsEachLineEnding()
    {
        var t = TextLines.Parse("a\r\nb\nc\rd");
        Assert.Equal(new[] { "a", "b", "c", "d" }, t.Lines);
        Assert.Equal(new[] { "\r\n", "\n", "\r", "" }, t.Endings);
        Assert.False(t.TrailingNewLine);
        Assert.Equal("змішані", t.EndingsSummary);
        Assert.Equal("a\r\nb\nc\rd", t.Join());
    }

    [Theory]
    [InlineData("", 0, false)]
    [InlineData("\n", 1, true)]
    [InlineData("x", 1, false)]
    [InlineData("x\r\n\r\n", 2, true)]
    public void Parse_EdgeCases(string text, int lines, bool trailing)
    {
        var t = TextLines.Parse(text);
        Assert.Equal(lines, t.Count);
        Assert.Equal(trailing, t.TrailingNewLine);
        Assert.Equal(text, t.Join());
    }

    // ------------------------------------------------------------------ 1. перенесення блоку

    [Fact]
    public void CopyHunk_OnlyLeftBlock_IsInsertedOnce_ThenFilesIdentical()
    {
        var left = TextLines.Parse("a\nX\nY\nb\n");
        var right = TextLines.Parse("a\nb\n");
        var d = Diff(left, right);
        TextDiff.CopyHunk(d, 0, left, right, toRight: true);
        Assert.Equal("a\nX\nY\nb\n", right.Join());
        Assert.True(Diff(left, right).Identical);
    }

    [Fact]
    public void CopyHunk_WithStaleDiff_Throws_InsteadOfInsertingTwice()
    {
        var left = TextLines.Parse("a\nX\nb\n");
        var right = TextLines.Parse("a\nb\n");
        var d = Diff(left, right);
        TextDiff.CopyHunk(d, 0, left, right, toRight: true);
        Assert.Throws<InvalidOperationException>(() => TextDiff.CopyHunk(d, 0, left, right, toRight: true));
        Assert.Equal("a\nX\nb\n", right.Join());
    }

    [Fact]
    public void CopyHunk_OnlyLeftBlock_CopiedLeftward_RemovesIt()
    {
        var left = TextLines.Parse("a\nX\nb\n");
        var right = TextLines.Parse("a\nb\n");
        TextDiff.CopyHunk(Diff(left, right), 0, left, right, toRight: false);
        Assert.Equal("a\nb\n", left.Join());
    }

    [Fact]
    public void CopyHunk_ChangedAndExtraLines_ReplaceTargetBlock()
    {
        var left = TextLines.Parse("a\n1\n2\n3\nz\n");
        var right = TextLines.Parse("a\none\nz\n");
        var d = Diff(left, right);
        Assert.Single(d.HunkStarts);
        TextDiff.CopyHunk(d, 0, left, right, toRight: true);
        Assert.Equal("a\n1\n2\n3\nz\n", right.Join());

        var other = TextLines.Parse("a\nB\nz\n");
        TextDiff.CopyHunk(Diff(other, left), 0, other, left, toRight: true);
        Assert.Equal("a\nB\nz\n", left.Join());
    }

    [Fact]
    public void CopyHunk_SecondHunk_UsesCorrectInsertionPoint()
    {
        var left = TextLines.Parse("a\nb\nNEW\nc\n");
        var right = TextLines.Parse("A\nb\nc\n");
        var d = Diff(left, right);
        Assert.Equal(2, d.HunkStarts.Count);
        TextDiff.CopyHunk(d, 1, left, right, toRight: true);
        Assert.Equal("A\nb\nNEW\nc\n", right.Join());
    }

    [Fact]
    public void CopyHunk_CopiedLines_GetTargetPredominantEnding_OtherLinesKeepTheirs()
    {
        var left = TextLines.Parse("a\nX\nb\nc\n");
        var right = TextLines.Parse("a\r\nb\nc\r\n");          // переважно CRLF, змішані
        TextDiff.CopyHunk(Diff(left, right), 0, left, right, toRight: true);
        Assert.Equal("a\r\nX\r\nb\nc\r\n", right.Join());
    }

    [Fact]
    public void CopyHunk_AtEndOfTargetWithoutTrailingNewline_KeepsNoTrailingNewline()
    {
        var left = TextLines.Parse("a\r\nb\r\nc\r\n");
        var right = TextLines.Parse("a\r\nb");
        TextDiff.CopyHunk(Diff(left, right), 0, left, right, toRight: true);
        Assert.Equal("a\r\nb\r\nc", right.Join());
    }

    [Fact]
    public void CopyHunk_ReplacingLastLine_KeepsTargetTrailingNewline()
    {
        var left = TextLines.Parse("a\nB");
        var right = TextLines.Parse("a\r\nb\r\n");
        TextDiff.CopyHunk(Diff(left, right), 0, left, right, toRight: true);
        Assert.Equal("a\r\nB\r\n", right.Join());
    }

    [Theory]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\nb\n", "a\nb\n")]
    [InlineData("a\r\nb\r\n", "a\r\nb\r\n")]
    public void CopyHunk_IntoEmptyTarget_AdoptsSourceEndingsAndTrailingNewline(string source, string expected)
    {
        var left = TextLines.Parse(source);
        var right = TextLines.Parse("");
        TextDiff.CopyHunk(Diff(left, right), 0, left, right, toRight: true);
        Assert.Equal(expected, right.Join());
    }

    // ------------------------------------------------------------------ 2. відмінності формату

    [Fact]
    public void FormatDifferences_ListsBomEndingsAndTrailingNewline()
    {
        var parts = TextDiff.FormatDifferences(Utf8Bom, TextLines.Parse("a\r\nb\r\n"), Utf8, TextLines.Parse("a\nb"));
        Assert.Equal(new[] { "BOM (є лише зліва)", "переноси рядків (CRLF ↔ LF)", "кінцевий перенос рядка (є лише зліва)" }, parts);
    }

    [Fact]
    public void FormatDifferences_Encoding()
    {
        var parts = TextDiff.FormatDifferences(Cp1251, TextLines.Parse("ї\n"), Utf8, TextLines.Parse("ї\n"));
        Assert.Equal(new[] { "кодування (Windows-1251 ↔ UTF-8)" }, parts);
    }

    [Fact]
    public void FormatDifferences_MixedEndings()
    {
        var parts = TextDiff.FormatDifferences(Utf8, TextLines.Parse("a\r\nb\nc\r\n"), Utf8, TextLines.Parse("a\r\nb\r\nc\r\n"));
        Assert.Equal(new[] { "переноси рядків (змішані ↔ CRLF)" }, parts);
    }

    [Fact]
    public void FormatDifferences_SameFormat_IsEmpty()
    {
        Assert.Empty(TextDiff.FormatDifferences(Utf8, TextLines.Parse("a\nb"), new UTF8Encoding(false), TextLines.Parse("a\nb")));
        Assert.Empty(TextDiff.FormatDifferences(Utf8, TextLines.Parse(""), Utf8, TextLines.Parse("")));
    }

    // ------------------------------------------------------------------ 3. збереження байт у байт

    public static TheoryData<string, byte[]> RoundTripFiles()
    {
        byte[] Cat(byte[] a, byte[] b) => a.Concat(b).ToArray();
        var cp1251 = TextEncodings.CodePage(1251);
        return new TheoryData<string, byte[]>
        {
            { "CRLF", Encoding.UTF8.GetBytes("один\r\nдва\r\n") },
            { "LF", Encoding.UTF8.GetBytes("one\ntwo\n") },
            { "mixed", Encoding.UTF8.GetBytes("a\r\nb\nc\rd\r\n\n") },
            { "no trailing newline", Encoding.UTF8.GetBytes("a\r\nb") },
            { "UTF-8 BOM", Cat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("Привіт, ї\r\nсвіт\r\n")) },
            { "UTF-8 no BOM", Encoding.UTF8.GetBytes("Привіт, ї\nсвіт") },
            { "UTF-16 LE BOM", Cat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("Привіт\r\nсвіт 😀\n")) },
            { "Windows-1251", cp1251.GetBytes("Привіт, ґанок ї є\r\nдругий рядок\r\n") },
            { "empty", Array.Empty<byte>() },
        };
    }

    [Theory]
    [MemberData(nameof(RoundTripFiles))]
    public void UnchangedFile_LoadAndSave_IsByteIdentical(string name, byte[] original)
    {
        var path = Path.Combine(dir, name + ".txt");
        File.WriteAllBytes(path, original);

        var (text, enc) = TextEncodings.Decode(File.ReadAllBytes(path));
        var lines = TextLines.Parse(text);
        var saveAs = TextEncodings.EncodingForSave(lines.Join(), enc, _ => throw new Xunit.Sdk.XunitException("не мало питати"));
        Assert.Same(enc, saveAs);
        long size = TextEncodings.WriteFile(path, lines.Join(), saveAs!);

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(original.Length, size);
    }

    [Fact]
    public void Windows1251_IsDetected()
    {
        var (text, enc) = TextEncodings.Decode(Cp1251.GetBytes("Привіт, ї"));
        Assert.Equal(1251, enc.CodePage);
        Assert.Equal("Привіт, ї", text);
    }

    [Fact]
    public void FirstUnencodable_FindsEmojiAndCjk_ButNotUkrainianIn1251()
    {
        Assert.Null(TextEncodings.FirstUnencodable("Привіт, ї ґ є", Cp1251));
        Assert.Equal("😀", TextEncodings.FirstUnencodable("ok 😀 x", Cp1251));
        Assert.Equal("中", TextEncodings.FirstUnencodable("ok 中", Cp1251));
        Assert.Null(TextEncodings.FirstUnencodable("😀 中", Utf8));
    }

    [Fact]
    public void EncodingForSave_AsksAndSwitchesToUtf8_OrCancels()
    {
        string? asked = null;
        var yes = TextEncodings.EncodingForSave("ї 😀", Cp1251, bad => { asked = bad; return true; });
        Assert.Equal("😀", asked);
        Assert.IsType<UTF8Encoding>(yes);
        Assert.Empty(yes!.GetPreamble());                       // у 1251 BOM не було — і в UTF-8 не буде

        Assert.Null(TextEncodings.EncodingForSave("ї 😀", Cp1251, _ => false));
        Assert.Same(Cp1251, TextEncodings.EncodingForSave("ї", Cp1251, _ => throw new Xunit.Sdk.XunitException("не мало питати")));
    }

    // ------------------------------------------------------------------ 6, 8. TextSpan і спільні початок / кінець

    [Theory]
    [InlineData("abc", "abXc", 2, 1)]
    [InlineData("aa", "aaa", 2, 0)]
    [InlineData("", "abc", 0, 0)]
    [InlineData("same", "same", 4, 0)]
    [InlineData("xab", "yab", 0, 2)]
    public void CommonEnds_PrefixAndSuffixDoNotOverlap(string a, string b, int prefix, int suffix) =>
        Assert.Equal((prefix, suffix), TextDiff.CommonEnds<char>(a, b));

    [Fact]
    public void CommonEnds_WorksForLineIds()
    {
        Assert.Equal((1, 2), TextDiff.CommonEnds<int>(new[] { 1, 2, 3, 4 }, new[] { 1, 9, 9, 3, 4 }));
    }

    [Theory]
    [InlineData("aXb", "aYYb", 1, 1, 1, 2)]
    [InlineData("ab", "aXb", 1, 0, 1, 1)]
    [InlineData("int x = 10;", "int x = 25;", 8, 2, 8, 2)]
    public void InlineDifference_ReturnsSpanPerSide(string l, string r, int ls, int ll, int rs, int rl)
    {
        var (left, right) = TextDiff.InlineDifference(l, r);
        Assert.Equal(new TextSpan(ls, ll), left);
        Assert.Equal(new TextSpan(rs, rl), right);
    }
}
