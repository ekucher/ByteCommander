using System.Buffers;
using System.Globalization;
using System.Text;
using System.IO.Compression;

namespace ByteCommander.Services;

public enum ConflictChoice { Overwrite, OverwriteAll, Skip, SkipAll, Rename, Cancel }

/// <summary>Стан операції; пише робочий потік, читає UI-таймер.</summary>
public sealed class ProgressState
{
    public long TotalBytes;
    public long DoneBytes;
    public int TotalFiles;
    public int DoneFiles;
    public volatile string Phase = "";
    public volatile string Current = "";
}

/// <summary>Копіювання, переміщення, видалення, пакування в ZIP з прогресом і вирішенням конфліктів.</summary>
public sealed class FileOperation
{
    readonly ProgressState st;
    readonly Func<string, string, ConflictChoice> ask;
    ConflictChoice? remembered;

    const FileAttributes CopyableAttrs = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
                                         FileAttributes.Archive | FileAttributes.NotContentIndexed;

    static readonly EnumerationOptions Flat = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    public List<string> Errors { get; } = new();

    public FileOperation(ProgressState state, Func<string, string, ConflictChoice> askConflict)
    {
        st = state;
        ask = askConflict;
    }

    // =====================================================================
    // Копіювання / переміщення
    // =====================================================================

    /// <param name="dest">Каталог призначення або (для одного джерела) нове повне ім’я.</param>
    public void Transfer(IReadOnlyList<string> sources, string dest, bool move, CancellationToken ct)
    {
        st.Phase = "Підготовка…";
        bool destIsDir = Directory.Exists(dest) || dest.EndsWith('\\') || dest.EndsWith('/') || sources.Count > 1;
        dest = Path.GetFullPath(dest);

        var plan = new List<(string src, string dst, bool fast)>();
        foreach (var raw in sources)
        {
            var full = Path.GetFullPath(raw);
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            {
                Errors.Add($"{full}: корінь диска не можна {(move ? "перемістити" : "скопіювати")} цілком");
                continue;
            }
            var s = full.TrimEnd('\\', '/');
            bool isDir = Directory.Exists(s);
            if (!isDir && !File.Exists(s)) { Errors.Add($"{s}: не знайдено"); continue; }

            var d = destIsDir ? Path.Combine(dest, Path.GetFileName(s)) : dest;
            if (Util.PathEquals(s, d))
            {
                if (move) continue;                 // переміщення «на місце» — нічого робити
                d = UniqueName(d, " - копія");      // копія в той самий каталог
            }
            if (isDir && Util.IsSubPathOf(d, s))
                throw new IOException($"Неможливо {(move ? "перемістити" : "скопіювати")} каталог «{Path.GetFileName(s)}» сам у себе.");

            bool fast = move && Util.SameVolume(s, d) && !File.Exists(d) && !Directory.Exists(d);
            plan.Add((s, d, fast));
        }

        Directory.CreateDirectory(destIsDir ? dest : Path.GetDirectoryName(dest)!);

        st.Phase = "Підрахунок…";
        foreach (var p in plan)
        {
            if (p.fast) st.TotalFiles++;
            else Count(p.src, ct);
        }

        st.Phase = move ? "Переміщення" : "Копіювання";
        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (p.fast)
            {
                st.Current = p.src;
                try
                {
                    if (Directory.Exists(p.src)) Directory.Move(p.src, p.dst);
                    else File.Move(p.src, p.dst);
                    st.DoneFiles++;
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Count(p.src, ct); // не вдалося перейменувати — переходимо на копіювання+видалення
                }
            }

            if (Directory.Exists(p.src)) CopyDir(p.src, p.dst, move, ct, 0);
            else CopyFile(p.src, p.dst, move, ct);
        }
    }

    void Count(string path, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
            {
                st.TotalFiles++;
                st.TotalBytes += new FileInfo(path).Length;
                return;
            }
            foreach (var f in Util.ScanFiles(path, ct))
            {
                st.TotalFiles++;
                st.TotalBytes += f.Length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* підрахунок лише для прогресу */ }
    }

    bool CopyDir(string src, string dst, bool move, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        var di = new DirectoryInfo(src);
        if (File.Exists(dst)) { Errors.Add($"{dst}: вже існує файл з таким ім’ям"); return false; }
        // Посилання/з’єднання: вкладені не обходимо ніколи; саме вибране — можна скопіювати вміст,
        // але не переміщувати на інший диск: це видалило б справжні файли цілі посилання.
        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint) && (depth > 0 || move))
        {
            Errors.Add(depth > 0
                ? $"{src}: символьне посилання / точку з’єднання пропущено"
                : $"{src}: це посилання / точка з’єднання — переміщення на інший диск не підтримується (скопіюйте його вміст)");
            return false;
        }

        FileInfo[] files;
        DirectoryInfo[] dirs;
        try
        {
            Directory.CreateDirectory(dst);
            files = di.GetFiles("*", Flat);
            dirs = di.GetDirectories("*", Flat);
        }
        catch (Exception ex)
        {
            Errors.Add($"{src}: {ex.Message}");
            return false;
        }

        bool ok = true;
        foreach (var f in files) ok &= CopyFile(f.FullName, Path.Combine(dst, f.Name), move, ct);
        foreach (var d in dirs) ok &= CopyDir(d.FullName, Path.Combine(dst, d.Name), move, ct, depth + 1);

        try
        {
            Directory.SetLastWriteTimeUtc(dst, di.LastWriteTimeUtc);
            var target = new DirectoryInfo(dst);
            target.Attributes = FileAttributes.Directory | (di.Attributes & CopyableAttrs & ~FileAttributes.ReadOnly);
        }
        catch { /* атрибути каталогу — не критично */ }

        if (move && ok)
        {
            try
            {
                di.Attributes = FileAttributes.Directory;
                di.Delete(false);
            }
            catch (Exception ex)
            {
                Errors.Add($"{src}: {ex.Message}");
                ok = false;
            }
        }
        return ok;
    }

    bool CopyFile(string src, string dst, bool move, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        st.Current = src;
        long len = 0;
        try { len = new FileInfo(src).Length; } catch { }

        if (Directory.Exists(dst))
        {
            Errors.Add($"{dst}: вже існує каталог з таким ім’ям");
            SkipProgress(len);
            return false;
        }
        if (!ClaimTarget(src, ref dst, len)) return false;

        long before = st.DoneBytes;
        try
        {
            if (move && Util.SameVolume(src, dst))
            {
                // MoveFileEx атомарний: при збої наявний файл призначення лишається цілим.
                if (File.Exists(dst)) ClearReadOnly(dst);
                File.Move(src, dst, overwrite: true);
                st.DoneBytes += len;
            }
            else
            {
                WriteViaTemp(dst, tmp =>
                {
                    CopyContent(src, tmp, ct);
                    File.SetCreationTimeUtc(tmp, File.GetCreationTimeUtc(src));
                    File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));
                });
                File.SetAttributes(dst, File.GetAttributes(src) & CopyableAttrs);
                if (move)
                {
                    ClearReadOnly(src);
                    File.Delete(src);
                }
            }
            st.DoneFiles++;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Errors.Add($"{src}: {ex.Message}");
            st.DoneBytes = before + len;
            st.DoneFiles++;
            return false;
        }
    }

    void CopyContent(string src, string dst, CancellationToken ct)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        try { output.SetLength(input.Length); } catch { }
        Pump(input, output, ct);
        output.SetLength(output.Position);
    }

    void Pump(Stream input, Stream output, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int n;
            while ((n = input.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buf, 0, n);
                st.DoneBytes += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // =====================================================================
    // Видалення назавжди
    // =====================================================================

    public void DeletePermanent(IReadOnlyList<string> paths, CancellationToken ct)
    {
        st.Phase = "Підрахунок…";
        foreach (var p in paths)
        {
            if (File.Exists(p)) st.TotalFiles++;
            else
            {
                try
                {
                    st.TotalFiles += Util.ScanFiles(p, ct).Count();
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        st.Phase = "Видалення";
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(p)) DeleteDir(p, ct);
            else DeleteFile(p);
        }
    }

    void DeleteFile(string p)
    {
        st.Current = p;
        try
        {
            ClearReadOnly(p);
            File.Delete(p);
        }
        catch (Exception ex)
        {
            Errors.Add($"{p}: {ex.Message}");
        }
        st.DoneFiles++;
    }

    void DeleteDir(string p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var di = new DirectoryInfo(p);
        try
        {
            // Посилання/з’єднання видаляємо саме як посилання, не заходячи всередину.
            if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var f in di.GetFiles("*", Flat)) DeleteFile(f.FullName);
                foreach (var d in di.GetDirectories("*", Flat)) DeleteDir(d.FullName, ct);
            }
            di.Attributes = FileAttributes.Directory;
            di.Delete(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Errors.Add($"{p}: {ex.Message}");
        }
    }

    // =====================================================================
    // ZIP
    // =====================================================================

    public void Pack(IReadOnlyList<string> sources, string zipPath, CancellationToken ct)
    {
        st.Phase = "Підрахунок…";
        var entries = new List<(string full, string rel, bool dir)>();
        var all = Util.DeepScan;
        foreach (var raw in sources)
        {
            var s = Path.GetFullPath(raw).TrimEnd('\\');
            var baseDir = Path.GetDirectoryName(s)!;
            string Rel(string p) => Path.GetRelativePath(baseDir, p).Replace('\\', '/');
            if (Directory.Exists(s))
            {
                entries.Add((s, Rel(s) + "/", true));
                var di = new DirectoryInfo(s);
                foreach (var d in di.EnumerateDirectories("*", all)) entries.Add((d.FullName, Rel(d.FullName) + "/", true));
                foreach (var f in di.EnumerateFiles("*", all))
                {
                    entries.Add((f.FullName, Rel(f.FullName), false));
                    st.TotalBytes += f.Length;
                    st.TotalFiles++;
                }
            }
            else if (File.Exists(s))
            {
                entries.Add((s, Path.GetFileName(s), false));
                st.TotalBytes += new FileInfo(s).Length;
                st.TotalFiles++;
            }
        }

        if (!ClaimTarget("", ref zipPath, 0)) return;

        st.Phase = "Пакування";
        // Архів пишеться у тимчасовий файл: при збої/скасуванні наявний архів з тим самим ім’ям не втрачається.
        WriteViaTemp(zipPath, tmp =>
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    st.Current = e.full;
                    if (e.dir)
                    {
                        zip.CreateEntry(e.rel);
                        continue;
                    }
                    try
                    {
                        var entry = zip.CreateEntry(e.rel, CompressionLevel.Optimal);
                        var t = File.GetLastWriteTime(e.full);
                        if (t.Year >= 1980 && t.Year <= 2107) entry.LastWriteTime = t;
                        using var input = new FileStream(e.full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var output = entry.Open();
                        Pump(input, output, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Errors.Add($"{e.full}: {ex.Message}");
                    }
                    st.DoneFiles++;
                }
            }
        });
    }

    public void Unpack(IReadOnlyList<string> zips, string destDir, CancellationToken ct)
    {
        st.Phase = "Підрахунок…";
        foreach (var z in zips)
        {
            try
            {
                using var a = ZipFile.Open(z, ZipArchiveMode.Read, ZipNameEncoding);
                foreach (var e in a.Entries)
                {
                    st.TotalBytes += e.Length;
                    st.TotalFiles++;
                }
            }
            catch (Exception ex)
            {
                Errors.Add($"{z}: {ex.Message}");
            }
        }

        var root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
        Directory.CreateDirectory(root);
        st.Phase = "Розпакування";
        foreach (var z in zips)
        {
            ZipArchive archive;
            try { archive = ZipFile.Open(z, ZipArchiveMode.Read, ZipNameEncoding); }
            catch { continue; }

            using (archive)
            {
                foreach (var e in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    st.Current = e.FullName;
                    var target = Path.GetFullPath(Path.Combine(root, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Errors.Add($"{e.FullName}: небезпечний шлях в архіві — пропущено");
                        continue;
                    }
                    try
                    {
                        if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\') || e.Name.Length == 0)
                        {
                            Directory.CreateDirectory(target);
                            st.DoneFiles++;
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        if (!ClaimTarget(z + " → " + e.FullName, ref target, e.Length)) continue;
                        WriteViaTemp(target, tmp =>
                        {
                            using (var input = e.Open())
                            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                                Pump(input, output, ct);
                            File.SetLastWriteTime(tmp, e.LastWriteTime.DateTime);
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Errors.Add($"{e.FullName}: {ex.Message}");
                    }
                    st.DoneFiles++;
                }
            }
        }
    }

    // =====================================================================
    // Допоміжне
    // =====================================================================

    ConflictChoice Resolve(string src, string dst)
    {
        if (remembered is { } r) return r;
        var c = ask(src, dst);
        switch (c)
        {
            case ConflictChoice.OverwriteAll:
                remembered = ConflictChoice.Overwrite;
                return ConflictChoice.Overwrite;
            case ConflictChoice.SkipAll:
                remembered = ConflictChoice.Skip;
                return ConflictChoice.Skip;
            default:
                return c;
        }
    }

    /// <summary>
    /// Якщо <paramref name="dst"/> вже існує — питає користувача, що робити.
    /// Повертає false, якщо об’єкт пропущено (прогрес враховано); при «Перейменувати» змінює <paramref name="dst"/>;
    /// при «Скасувати» кидає <see cref="OperationCanceledException"/>. При «Замінити» нічого не чіпає — заміну робить запис.
    /// </summary>
    bool ClaimTarget(string src, ref string dst, long skipBytes)
    {
        if (!File.Exists(dst)) return true;
        switch (Resolve(src, dst))
        {
            case ConflictChoice.Skip:
                SkipProgress(skipBytes);
                return false;
            case ConflictChoice.Cancel:
                throw new OperationCanceledException();
            case ConflictChoice.Rename:
                dst = UniqueName(dst);
                return true;
            default:
                return true;
        }
    }

    void SkipProgress(long len)
    {
        st.DoneBytes += len;
        st.DoneFiles++;
    }

    static void ClearReadOnly(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Пише новий вміст у тимчасовий файл поруч із <paramref name="dst"/> і лише після успіху
    /// атомарно замінює ним призначення. При збої чи скасуванні наявний файл лишається недоторканим.
    /// </summary>
    static void WriteViaTemp(string dst, Action<string> write)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(dst)!, $".{Path.GetFileName(dst)}.{Guid.NewGuid():N}.bctmp");
        try
        {
            write(tmp);
            if (File.Exists(dst)) ClearReadOnly(dst);
            File.Move(tmp, dst, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Кодування імен для ZIP без прапорця UTF-8 (Провідник пише їх у OEM-кодуванні, для uk-UA — CP866).</summary>
    static Encoding ZipNameEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.GetEncoding(866); }
        }
    }

    /// <summary>"файл.txt" → "файл (2).txt"; із суфіксом: "файл - копія.txt", "файл - копія (2).txt".</summary>
    public static string UniqueName(string path, string suffix = "")
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        bool isDir = Directory.Exists(path);
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        for (int i = 1; i < 100000; i++)
        {
            string tag = suffix.Length > 0 ? (i == 1 ? suffix : $"{suffix} ({i})") : $" ({i + 1})";
            var candidate = Path.Combine(dir, stem + tag + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Не вдалося підібрати вільне ім’я.");
    }
}
