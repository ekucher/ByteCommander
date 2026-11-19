using ByteCommander.Compare;

namespace ByteCommander.Tests;

[Collection(WorkspaceCollection.Name)]
public sealed class FolderCompareFixTests(TestWorkspace ws)
{
    [Fact]
    public void Junctions_WithDifferentContent_AreNotReportedSame()
    {
        var d = ws.Fresh(nameof(Junctions_WithDifferentContent_AreNotReportedSame));
        string l = Path.Combine(d, "L"), r = Path.Combine(d, "R"), t1 = Path.Combine(d, "T1"), t2 = Path.Combine(d, "T2");
        foreach (var x in new[] { l, r, t1, t2 }) Directory.CreateDirectory(x);
        File.WriteAllText(Path.Combine(t1, "a.txt"), "one");
        File.WriteAllText(Path.Combine(t2, "b.txt"), "two!");
        TestWorkspace.CreateJunction(Path.Combine(l, "lnk"), t1);
        TestWorkspace.CreateJunction(Path.Combine(r, "lnk"), t2);
        var time = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var side in new[] { l, r })
        {
            var f = Path.Combine(side, "f.txt");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, time);
        }

        var root = FolderComparer.Compare(l, r, new FolderCompareOptions());
        var lnk = root.Children.Single(c => c.Name == "lnk");
        Assert.True(lnk.IsLink);
        Assert.Equal(EntryStatus.NotCompared, lnk.Status);
        Assert.Empty(lnk.Children);
        Assert.NotEqual(EntryStatus.Same, root.Status);
        Assert.Equal((1, 0, 0, 0, 1), FolderComparer.Count(root));
    }

    [Fact]
    public void ParentOfLink_IsNotSame()
    {
        var d = ws.Fresh(nameof(ParentOfLink_IsNotSame));
        string l = Path.Combine(d, "L"), r = Path.Combine(d, "R"), t = Path.Combine(d, "T");
        Directory.CreateDirectory(Path.Combine(l, "p"));
        Directory.CreateDirectory(Path.Combine(r, "p"));
        Directory.CreateDirectory(t);
        TestWorkspace.CreateJunction(Path.Combine(l, "p", "lnk"), t);
        TestWorkspace.CreateJunction(Path.Combine(r, "p", "lnk"), t);
        var p = FolderComparer.Compare(l, r, new FolderCompareOptions()).Children.Single();
        Assert.False(p.IsLink);
        Assert.Equal(EntryStatus.NotCompared, p.Status);
    }

    [Fact]
    public void EveryStatus_HasDescriptor()
    {
        foreach (var s in Enum.GetValues<EntryStatus>())
        {
            var info = EntryStatusInfo.Of(s);
            Assert.False(string.IsNullOrWhiteSpace(info.Mark));
            Assert.False(string.IsNullOrWhiteSpace(info.Text));
            Assert.NotEqual(CountBucket.None, info.Bucket);
        }
    }

    [Fact]
    public void FilesEqual_AndFirstDifference()
    {
        var d = ws.Fresh(nameof(FilesEqual_AndFirstDifference));
        string a = Path.Combine(d, "a"), b = Path.Combine(d, "b"), c = Path.Combine(d, "c");
        File.WriteAllBytes(a, [1, 2, 3]);
        File.WriteAllBytes(b, [1, 2, 3]);
        File.WriteAllBytes(c, [1, 9, 3]);
        Assert.True(Util.FilesEqual(a, b));
        Assert.False(Util.FilesEqual(a, c));
        Assert.Equal(1L, Util.FirstDifference(a, c));
    }
}
