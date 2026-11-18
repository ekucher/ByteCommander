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
    public void RotationByRename_ReportsReset()
    {
        // Найпоширеніша ротація (NLog, log4net, logrotate create): старий файл перейменовують, новий створюють.
        // Дескриптор переглядача лишається на старому файлі — розмір не зменшився, але файл за шляхом уже інший.
        var p = WriteFile("renamed.log", Lines(0, 100));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);

        var old = p + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(p, old);
        File.WriteAllText(p, Lines(0, 500));   // новий файл навіть більший за старий

        Assert.Equal(SourceChange.Reset, src.Poll());
    }

    [Fact]
    public void DeletedFile_ReportsDeleted_ThenResetWhenRecreated()
    {
        var p = WriteFile("deleted.log", Lines(0, 20));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);

        File.Delete(p);
        // На NTFS з POSIX-видаленням ім'я зникає одразу, навіть поки файл відкритий нами.
        if (!File.Exists(p)) Assert.Equal(SourceChange.Deleted, src.Poll());

        File.WriteAllText(p, Lines(0, 5));
        Assert.Equal(SourceChange.Reset, src.Poll());
    }

    [Fact]
    public void HexSource_RotationByRename_ReportsReset()
    {
        var p = ws.WriteFile(nameof(FollowTests), "renamed.bin", new byte[64]);
        using var src = new HexRowSource(p);
        var old = p + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(p, old);
        File.WriteAllBytes(p, new byte[128]);

        Assert.Equal(SourceChange.Reset, src.Poll());
    }

    [Fact]
    public void AfterRenameRotation_SourceKeepsReadingTheOriginalFile()
    {
        // Регресія: індексація й пошук відкривали файл за шляхом, а розмір брали з дескриптора старого файлу —
        // після ротації джерело змішувало два різні файли й «знаходило» мільйони порожніх рядків.
        var p = WriteFile("consistent.log", Lines(0, 300));
        using var src = new TextRowSource(p, new UTF8Encoding(false));
        WaitIndexed(src);

        var old = p + ".1";
        if (File.Exists(old)) File.Delete(old);
        File.Move(p, old);
        File.WriteAllText(p, "зовсім інший новий файл\n");

        Assert.Equal(300, src.RowCount);
        Assert.Equal(new[] { "рядок 0", "рядок 1" }, src.GetRows(0, 2));
        Assert.Equal(new[] { "рядок 299" }, src.GetRows(299, 5));
        Assert.Equal(new TextMatch(299, 0, 9), src.Find("рядок 299", 0, 0, default));
        Assert.Null(src.Find("зовсім інший", 0, 0, default));
    }

    [Fact]
    public void LineScanner_WhenFileIsShorterThanExpected_Stops()
    {
        // Захист від зациклення: якщо файл коротший, ніж вважає джерело (обрізали посеред читання),
        // сканер має закінчити, а не видавати нескінченні порожні рядки.
        var p = WriteFile("short.txt", "a\nb\n");
        using var handle = Util.OpenSharedHandle(p);
        var sc = new LineScanner(handle, 0, end: 1_000_000, CodeUnitLayout.From(new UTF8Encoding(false)), TextRowSource.MaxLineBytes);

        int rows = 0;
        while (sc.Next(out _, out _) && rows < 100) rows++;

        Assert.Equal(2, rows);
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
