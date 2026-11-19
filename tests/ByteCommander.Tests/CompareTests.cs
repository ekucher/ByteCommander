using System.Text;
using ByteCommander.Compare;

namespace ByteCommander.Tests;

public sealed class TextDiffTests
{
    static string[] L(params string[] lines) => lines;

    static int Lcs(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return dp[0, 0];
    }

    [Fact]
    public void Script_IsMinimal_AndValid_OnRandomInputs()
    {
        // Перевірка проти еталонного LCS: Майєрс дає мінімальний сценарій, і він справді перетворює a на b.
        var rnd = new Random(12345);
        for (int iter = 0; iter < 500; iter++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 5)).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 5)).ToArray();
            var ops = TextDiff.Script(a, b, default, out bool approx);
            Assert.False(approx);

            int i = 0, j = 0, equal = 0;
            var rebuilt = new List<int>();
            foreach (var op in ops)
            {
                switch (op)
                {
                    case TextDiff.Op.Equal: Assert.Equal(a[i], b[j]); rebuilt.Add(b[j]); i++; j++; equal++; break;
                    case TextDiff.Op.Delete: i++; break;
                    case TextDiff.Op.Insert: rebuilt.Add(b[j]); j++; break;
                }
            }
            Assert.Equal(a.Length, i);
            Assert.Equal(b, rebuilt);
            Assert.Equal(Lcs(a, b), equal);
        }
    }

    [Fact]
    public void Identical_HasNoHunks()
    {
        var r = TextDiff.Compare(L("a", "b", "c"), L("a", "b", "c"), new DiffOptions());
        Assert.True(r.Identical);
        Assert.All(r.Rows, row => Assert.Equal(DiffKind.Equal, row.Kind));
    }

    [Fact]
    public void ChangedDeletedInserted_AreAlignedSideBySide()
    {
        var r = TextDiff.Compare(
            L("перший", "другий", "третій", "четвертий"),
            L("перший", "ДРУГИЙ", "четвертий", "п'ятий"),
            new DiffOptions());

        Assert.Equal(new[] { DiffKind.Equal, DiffKind.Changed, DiffKind.Deleted, DiffKind.Equal, DiffKind.Inserted },
            r.Rows.Select(x => x.Kind));
        Assert.Equal(2, r.HunkStarts.Count);
        Assert.Equal((1, 1, 1, 5), (r.ChangedLines, r.DeletedLines, r.InsertedLines, r.Rows.Count));
        var changed = r.Rows[1];
        Assert.Equal((1, 1), (changed.LeftLine, changed.RightLine));
        Assert.Null(r.Rows[2].RightLine);
        Assert.Null(r.Rows[4].LeftLine);
    }

    [Fact]
    public void InlineHighlight_CoversOnlyChangedMiddle()
    {
        var r = TextDiff.Compare(L("int x = 10;"), L("int x = 25;"), new DiffOptions());
        var row = Assert.Single(r.Rows);
        Assert.Equal(DiffKind.Changed, row.Kind);
        Assert.Equal("10", row.LeftText.Substring(row.HighlightLeft.Start, row.HighlightLeft.Length));
        Assert.Equal("25", row.RightText.Substring(row.HighlightRight.Start, row.HighlightRight.Length));
        Assert.Equal(new TextSpan(8, 2), row.HighlightLeft);
    }

    [Theory]
    [InlineData(true, false, "a  b", "  a b  ", true)]
    [InlineData(false, false, "a  b", "a b", false)]
    [InlineData(false, true, "Hello", "HELLO", true)]
    [InlineData(false, false, "Hello", "HELLO", false)]
    public void Options_IgnoreWhitespaceAndCase(bool ws, bool ic, string l, string r, bool identical) =>
        Assert.Equal(identical, TextDiff.Compare(L(l), L(r), new DiffOptions(ws, ic)).Identical);

    [Fact]
    public void LargeFilesWithFewChanges_AreFast()
    {
        var left = Enumerable.Range(0, 200_000).Select(i => $"line {i}").ToList();
        var right = left.ToList();
        right[1000] = "changed";
        right.Insert(150_000, "inserted");
        right.RemoveAt(199_000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = TextDiff.Compare(left, right, new DiffOptions());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{sw.Elapsed}");
        Assert.Equal(3, r.HunkStarts.Count);
        Assert.False(r.Approximate);
    }

    [Fact]
    public void TooManyDifferences_FallsBackToReplacementBlock()
    {
        var left = Enumerable.Range(0, TextDiff.MaxEditDistance + 10).Select(i => $"L{i}").ToList();
        var right = Enumerable.Range(0, TextDiff.MaxEditDistance + 10).Select(i => $"R{i}").ToList();
        left.Insert(0, "same start");
        right.Insert(0, "same start");

        var r = TextDiff.Compare(left, right, new DiffOptions());
        Assert.True(r.Approximate);
        Assert.Equal(DiffKind.Equal, r.Rows[0].Kind);
        Assert.Single(r.HunkStarts);
    }
}

[Collection(WorkspaceCollection.Name)]
public sealed class FolderComparerTests(TestWorkspace ws)
{
    (string left, string right) Trees(string name)
    {
        var d = ws.Fresh(name);
        string l = Path.Combine(d, "L"), r = Path.Combine(d, "R");
        void Put(string root, string rel, string text, DateTime? time = null)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            File.SetLastWriteTimeUtc(p, time ?? new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        }
        var t0 = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Put(l, "same.txt", "abc"); Put(r, "same.txt", "abc");
        Put(l, "diff.txt", "old"); Put(r, "diff.txt", "new!!", t0.AddHours(1));
        Put(l, "sametime.txt", "aaa"); Put(r, "sametime.txt", "bbb");      // той самий розмір і час, інший вміст
        Put(l, "only-left.txt", "x");
        Put(r, "only-right.txt", "y");
        Put(l, @"sub\deep\a.txt", "1"); Put(r, @"sub\deep\a.txt", "1");
        Put(l, @"lonely\b.txt", "2");
        return (l, r);
    }

    static FolderEntry Find(FolderEntry root, string rel)
    {
        foreach (var c in root.Children)
        {
            if (string.Equals(c.RelativePath, rel, StringComparison.OrdinalIgnoreCase)) return c;
            if (c.IsDirectory && rel.StartsWith(c.RelativePath + "\\", StringComparison.OrdinalIgnoreCase)) return Find(c, rel);
        }
        throw new KeyNotFoundException(rel);
    }

    [Fact]
    public void QuickMode_UsesSizeAndTime()
    {
        var (l, r) = Trees(nameof(QuickMode_UsesSizeAndTime));
        var root = FolderComparer.Compare(l, r, new FolderCompareOptions(ByContent: false));

        Assert.Equal(EntryStatus.Same, Find(root, "same.txt").Status);
        var diff = Find(root, "diff.txt");
        Assert.Equal(EntryStatus.Different, diff.Status);
        Assert.True(diff.RightNewer);
        Assert.Equal(EntryStatus.Same, Find(root, "sametime.txt").Status);   // швидкий режим не читає вміст
        Assert.Equal(EntryStatus.LeftOnly, Find(root, "only-left.txt").Status);
        Assert.Equal(EntryStatus.RightOnly, Find(root, "only-right.txt").Status);
        Assert.Equal(EntryStatus.Same, Find(root, "sub").Status);
        Assert.Equal(EntryStatus.LeftOnly, Find(root, "lonely").Status);
        Assert.Equal(EntryStatus.LeftOnly, Find(root, @"lonely\b.txt").Status);
        Assert.Equal(EntryStatus.Different, root.Status);
        // однакові: same.txt, sametime.txt, sub\deep\a.txt; лише зліва: only-left.txt і каталог lonely
        Assert.Equal((3, 1, 2, 1), FolderComparer.Count(root));
    }

    [Fact]
    public void ContentMode_DetectsSameSizeSameTimeDifferentContent()
    {
        var (l, r) = Trees(nameof(ContentMode_DetectsSameSizeSameTimeDifferentContent));
        var root = FolderComparer.Compare(l, r, new FolderCompareOptions(ByContent: true));
        Assert.Equal(EntryStatus.Different, Find(root, "sametime.txt").Status);
        Assert.Equal(EntryStatus.Same, Find(root, "same.txt").Status);
    }

    [Fact]
    public void FirstDifference_ReportsOffset()
    {
        var d = ws.Fresh(nameof(FirstDifference_ReportsOffset));
        string a = Path.Combine(d, "a.bin"), b = Path.Combine(d, "b.bin"), c = Path.Combine(d, "c.bin");
        var bytes = new byte[3_000_000];
        File.WriteAllBytes(a, bytes);
        bytes[2_500_000] = 1;
        File.WriteAllBytes(b, bytes);
        File.WriteAllBytes(c, bytes.AsSpan(0, 2_000_000).ToArray());

        Assert.Equal(2_500_000L, FolderComparer.FirstDifference(a, b));
        Assert.Equal(2_000_000L, FolderComparer.FirstDifference(a, c));
        Assert.Null(FolderComparer.FirstDifference(a, a));
    }
}
