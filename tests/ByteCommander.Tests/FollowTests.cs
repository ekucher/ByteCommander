using System.Text;
using ByteCommander.Viewer;

namespace ByteCommander.Tests;

/// <summary>Стеження за файлом у реальному часі (Ctrl+End у переглядачі, як у Far): дочитування дописаного.</summary>
[Collection(WorkspaceCollection.Name)]
public sealed class FollowTests(TestWorkspace ws)
{
    string WriteFile(string name, string text) => ws.WriteFile(nameof(FollowTests), name, Encoding.UTF8.GetBytes(text));

    static void WaitIndexed(IRowSource src)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!src.IsComplete && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(src.IsComplete, "індексація не завершилась");
    }

    static string Lines(int from, int to) => string.Concat(Enumerable.Range(from, to - from).Select(i => $"рядок {i}\n"));

    [Fact]
    public void AppendedLines_WhileWriterKeepsFileOpen_AreIndexedAcrossCheckpoints()
    {
        var p = WriteFile("log.txt", "");
        // Як справжній логер: файл відкритий на запис увесь час, дані дописуються й скидаються на диск.
        using var writer = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        void Append(string s) { var b = Encoding.UTF8.GetBytes(s); writer.Write(b); writer.Flush(true); }

        Append(Lines(0, 300));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);
        Assert.Equal(300, src.RowCount);

        Append(Lines(300, 700));
        Assert.Equal(SourceChange.Grew, src.Poll());
        WaitIndexed(src);

        Assert.Equal(700, src.RowCount);
        Assert.Equal(new[] { "рядок 255", "рядок 256", "рядок 257" }, src.GetRows(255, 3));
        Assert.Equal(new[] { "рядок 511", "рядок 512" }, src.GetRows(511, 2));
        Assert.Equal(new[] { "рядок 699" }, src.GetRows(699, 5));
        Assert.Equal(SourceChange.Unchanged, src.Poll());
    }

    [Fact]
    public void UnfinishedLastLine_IsRereadWhenRestIsAppended()
    {
        var p = WriteFile("partial.txt", "перший\nнедопи");
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);
        Assert.Equal(new[] { "перший", "недопи" }, src.GetRows(0, 10));

        File.AppendAllText(p, "саний\nтретій\n");
        Assert.Equal(SourceChange.Grew, src.Poll());
        WaitIndexed(src);

        Assert.Equal(3, src.RowCount);
        Assert.Equal(new[] { "перший", "недописаний", "третій" }, src.GetRows(0, 10));
    }

    [Fact]
    public void Search_FindsTextInAppendedPart()
    {
        var p = WriteFile("search.txt", Lines(0, 50));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);

        File.AppendAllText(p, "ERROR: щось зламалось\n");
        src.Poll();
        WaitIndexed(src);

        Assert.Equal(new TextMatch(50, 0, 5), src.Find("error", 0, 0, default));
    }

    [Fact]
    public void TruncatedFile_ReportsReset()
    {
        var p = WriteFile("rotate.txt", Lines(0, 100));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);

        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.SetLength(10);   // ротація логу: файл обрізали

        Assert.Equal(SourceChange.Reset, src.Poll());
    }

    [Fact]
    public void UnchangedFile_ReportsUnchanged()
    {
        var p = WriteFile("still.txt", Lines(0, 10));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);
        Assert.Equal(SourceChange.Unchanged, src.Poll());
        Assert.Equal(10, src.RowCount);
    }

    [Fact]
    public void HexSource_GrowsWithFile()
    {
        var p = ws.WriteFile(nameof(FollowTests), "bin.dat", new byte[20]);
        using var src = new HexRowSource(p);
        Assert.Equal(2, src.RowCount);
        int raised = 0;
        src.RowCountChanged += () => raised++;

        File.AppendAllBytes(p, new byte[30]);

        Assert.Equal(SourceChange.Grew, src.Poll());
        Assert.Equal(4, src.RowCount);
        Assert.Equal(50, src.Length);
        Assert.Equal(1, raised);
        Assert.Equal(4, src.GetRows(0, 10).Count);
    }
}
