using System.IO.Compression;
using System.Text;
using ByteCommander.Services;
using static ByteCommander.Tests.TestWorkspace;

namespace ByteCommander.Tests;

[Collection(WorkspaceCollection.Name)]
public sealed class FileOperationTests(TestWorkspace ws)
{
    // ---------------------------------------------------------------- копіювання / перезапис

    [Fact]
    public void Overwrite_WhenSourceIsLocked_KeepsExistingDestination()
    {
        var d = ws.Fresh(nameof(Overwrite_WhenSourceIsLocked_KeepsExistingDestination));
        var src = Path.Combine(d, "src", "a.txt");
        var dst = Path.Combine(d, "dst", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(src, "NEW");
        File.WriteAllText(dst, "OLD");

        var op = Op(ConflictChoice.Overwrite);
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
            op.Transfer(new[] { src }, Path.GetDirectoryName(dst) + "\\", false, default);

        Assert.Equal("OLD", File.ReadAllText(dst));
        Assert.Single(op.Errors);
        Assert.True(NoTempFiles(d));
    }

    [Fact]
    public void Overwrite_ReadOnlyDestination_ReplacesContentAndKeepsTimestamp()
    {
        var d = ws.Fresh(nameof(Overwrite_ReadOnlyDestination_ReplacesContentAndKeepsTimestamp));
        var src = Path.Combine(d, "src", "a.txt");
        var dstDir = Path.Combine(d, "dst");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        Directory.CreateDirectory(dstDir);
        File.WriteAllText(src, "NEW");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, stamp);
        var dst = Path.Combine(dstDir, "a.txt");
        File.WriteAllText(dst, "OLD");
        File.SetAttributes(dst, FileAttributes.ReadOnly);

        var op = Op(ConflictChoice.Overwrite);
        op.Transfer(new[] { src }, dstDir + "\\", false, default);

        Assert.Empty(op.Errors);
        Assert.Equal("NEW", File.ReadAllText(dst));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(dst));
        Assert.True(NoTempFiles(d));
    }

    [Fact]
    public void CopyIntoSameDirectory_CreatesCopyName()
    {
        var d = ws.Fresh(nameof(CopyIntoSameDirectory_CreatesCopyName));
        var src = Path.Combine(d, "звіт.txt");
        File.WriteAllText(src, "X");

        Op(ConflictChoice.Cancel).Transfer(new[] { src }, d + "\\", false, default);

        Assert.Equal("X", File.ReadAllText(Path.Combine(d, "звіт - копія.txt")));
    }

    [Fact]
    public void CopyDirectoryIntoItself_Throws()
    {
        var d = ws.Fresh(nameof(CopyDirectoryIntoItself_Throws));
        var dir = Path.Combine(d, "a");
        Directory.CreateDirectory(dir);

        Assert.Throws<IOException>(() => Op(ConflictChoice.Cancel).Transfer(new[] { dir }, Path.Combine(dir, "sub") + "\\", false, default));
    }

    // ---------------------------------------------------------------- переміщення між томами

    [Fact]
    public void CrossVolumeMove_OfDirectory_MovesTree()
    {
        var d = ws.Fresh(nameof(CrossVolumeMove_OfDirectory_MovesTree));
        var src = Path.Combine(d, "tree");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "f.txt"), "F");

        var op = Op(ConflictChoice.Overwrite);
        op.Transfer(new[] { src }, ws.ViaOtherVolume(Path.Combine(d, "moved")) + "\\", true, default);

        Assert.Empty(op.Errors);
        Assert.False(Directory.Exists(src));
        Assert.Equal("F", File.ReadAllText(Path.Combine(d, "moved", "tree", "sub", "f.txt")));
    }

    [Fact]
    public void CrossVolumeMove_OfJunction_IsRefusedAndTargetIntact()
    {
        var d = ws.Fresh(nameof(CrossVolumeMove_OfJunction_IsRefusedAndTargetIntact));
        var target = Path.Combine(d, "real");
        var link = Path.Combine(d, "link");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(d, "dest"));
        File.WriteAllText(Path.Combine(target, "precious.txt"), "DATA");
        CreateJunction(link, target);
        try
        {
            var op = Op(ConflictChoice.Overwrite);
            op.Transfer(new[] { link }, ws.ViaOtherVolume(Path.Combine(d, "dest")) + "\\", true, default);

            Assert.Single(op.Errors);
            Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
            Assert.True(Directory.Exists(link));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void CrossVolumeCopy_OfJunction_CopiesContent()
    {
        var d = ws.Fresh(nameof(CrossVolumeCopy_OfJunction_CopiesContent));
        var target = Path.Combine(d, "real");
        var link = Path.Combine(d, "link");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(d, "dest"));
        File.WriteAllText(Path.Combine(target, "precious.txt"), "DATA");
        CreateJunction(link, target);
        try
        {
            var op = Op(ConflictChoice.Overwrite);
            op.Transfer(new[] { link }, ws.ViaOtherVolume(Path.Combine(d, "dest")) + "\\", false, default);

            Assert.Empty(op.Errors);
            Assert.Equal("DATA", File.ReadAllText(Path.Combine(d, "dest", "link", "precious.txt")));
            Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // ---------------------------------------------------------------- видалення

    [Fact]
    public void DeletePermanent_DirectoryWithJunction_DoesNotTouchJunctionTarget()
    {
        var d = ws.Fresh(nameof(DeletePermanent_DirectoryWithJunction_DoesNotTouchJunctionTarget));
        var target = Path.Combine(d, "real");
        var victim = Path.Combine(d, "victim");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "DATA");
        File.WriteAllText(Path.Combine(victim, "junk.txt"), "x");
        CreateJunction(Path.Combine(victim, "link"), target);

        var op = Op(ConflictChoice.Cancel);
        op.DeletePermanent(new[] { victim }, default);

        Assert.Empty(op.Errors);
        Assert.False(Directory.Exists(victim));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    // ---------------------------------------------------------------- ZIP

    [Fact]
    public void Unpack_CancelOnConflict_KeepsExistingFile()
    {
        var d = ws.Fresh(nameof(Unpack_CancelOnConflict_KeepsExistingFile));
        var zip = Path.Combine(d, "a.zip");
        var outDir = Path.Combine(d, "out");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("keep.txt").Open()))
            w.Write("FROMZIP");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "keep.txt"), "KEEP");

        Assert.Throws<OperationCanceledException>(() => Op(ConflictChoice.Cancel).Unpack(new[] { zip }, outDir, default));

        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(outDir, "keep.txt")));
        Assert.True(NoTempFiles(d));
    }

    [Fact]
    public void Unpack_Overwrite_ReplacesFile()
    {
        var d = ws.Fresh(nameof(Unpack_Overwrite_ReplacesFile));
        var zip = Path.Combine(d, "a.zip");
        var outDir = Path.Combine(d, "out");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("x/keep.txt").Open()))
            w.Write("FROMZIP");
        Directory.CreateDirectory(Path.Combine(outDir, "x"));
        File.WriteAllText(Path.Combine(outDir, "x", "keep.txt"), "OLD");

        var op = Op(ConflictChoice.Overwrite);
        op.Unpack(new[] { zip }, outDir, default);

        Assert.Empty(op.Errors);
        Assert.Equal("FROMZIP", File.ReadAllText(Path.Combine(outDir, "x", "keep.txt")));
        Assert.True(NoTempFiles(d));
    }

    [Fact]
    public void Unpack_Cp866EntryNames_AreDecoded()
    {
        var d = ws.Fresh(nameof(Unpack_Cp866EntryNames_AreDecoded));
        var zip = Path.Combine(d, "cp866.zip");
        var outDir = Path.Combine(d, "out");
        // Так пишуть архіви Провідник і старі архіватори: OEM-кодування, без прапорця UTF-8.
        // (Українських «і/ї/є/ґ» у CP866 немає — тому лише спільні з російською літери.)
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create, Encoding.GetEncoding(866)))
        using (var w = new StreamWriter(z.CreateEntry("Папка/Документ.txt").Open()))
            w.Write("1");

        // Кодування імен береться з OEM-кодової сторінки поточної культури (uk-UA → 866, en-US → 437),
        // тому фіксуємо культуру — інакше результат залежав би від мови Windows (напр. CI-раннера).
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
        var op = Op(ConflictChoice.Overwrite);
        try
        {
            op.Unpack(new[] { zip }, outDir, default);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }

        Assert.Empty(op.Errors);
        Assert.True(File.Exists(Path.Combine(outDir, "Папка", "Документ.txt")));
    }

    [Fact]
    public void Unpack_ZipSlipEntry_IsSkipped()
    {
        var d = ws.Fresh(nameof(Unpack_ZipSlipEntry_IsSkipped));
        var zip = Path.Combine(d, "evil.zip");
        var outDir = Path.Combine(d, "out");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("../escaped.txt").Open()))
            w.Write("x");

        var op = Op(ConflictChoice.Overwrite);
        op.Unpack(new[] { zip }, outDir, default);

        Assert.Single(op.Errors);
        Assert.False(File.Exists(Path.Combine(d, "escaped.txt")));
    }

    [Fact]
    public void Pack_CancelOverExistingArchive_KeepsOldArchive()
    {
        var d = ws.Fresh(nameof(Pack_CancelOverExistingArchive_KeepsOldArchive));
        var src = Path.Combine(d, "a.txt");
        File.WriteAllText(src, "A");
        var zip = Path.Combine(d, "out.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Op(ConflictChoice.Overwrite).Pack(new[] { src }, zip, cts.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(zip));
        Assert.True(NoTempFiles(d));
    }

    [Fact]
    public void Pack_Overwrite_ProducesValidArchive()
    {
        var d = ws.Fresh(nameof(Pack_Overwrite_ProducesValidArchive));
        var src = Path.Combine(d, "a.txt");
        File.WriteAllText(src, "A");
        var zip = Path.Combine(d, "out.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });

        Op(ConflictChoice.Overwrite).Pack(new[] { src }, zip, default);

        using var z = ZipFile.OpenRead(zip);
        Assert.Equal("a.txt", Assert.Single(z.Entries).FullName);
        Assert.True(NoTempFiles(d));
    }
}
