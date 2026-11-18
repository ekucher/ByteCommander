using ByteCommander.Services;

namespace ByteCommander.Compare;

public enum EntryStatus { Same, Different, LeftOnly, RightOnly }

public sealed record FolderCompareOptions(bool ByContent = false);

/// <summary>Елемент дерева порівняння каталогів: файл або каталог з одного чи обох боків.</summary>
public sealed class FolderEntry
{
    public required string Name { get; init; }
    /// <summary>Шлях відносно коренів порівняння ("" для самого кореня).</summary>
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; init; }
    public FileSystemInfo? Left { get; init; }
    public FileSystemInfo? Right { get; init; }
    public EntryStatus Status { get; internal set; }
    /// <summary>Обидва файли є, різні, і лівий новіший / правий новіший (з допуском 2 с).</summary>
    public bool LeftNewer { get; internal set; }
    public bool RightNewer { get; internal set; }
    public string? Error { get; internal set; }
    public List<FolderEntry> Children { get; } = new();

    public long? LeftSize => (Left as FileInfo)?.Length;
    public long? RightSize => (Right as FileInfo)?.Length;
}

/// <summary>
/// Рекурсивне порівняння двох каталогів. Швидкий режим — розмір і дата зміни (допуск 2 с, як у FAT/мережевих
/// дисків); режим «за вмістом» — побайтно (дата не враховується). Символьні посилання/з’єднання не обходяться.
/// </summary>
public static class FolderComparer
{
    static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
    static readonly EnumerationOptions Flat = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

    public static FolderEntry Compare(string left, string right, FolderCompareOptions options,
        CancellationToken ct = default, Action<string>? progress = null)
    {
        var root = new FolderEntry
        {
            Name = "", RelativePath = "", IsDirectory = true,
            Left = new DirectoryInfo(left), Right = new DirectoryInfo(right),
        };
        Fill(root, options, ct, progress);
        return root;
    }

    static void Fill(FolderEntry dir, FolderCompareOptions o, CancellationToken ct, Action<string>? progress)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(dir.RelativePath);
        var byName = new Dictionary<string, (FileSystemInfo? l, FileSystemInfo? r)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            bool nested = dir.RelativePath.Length > 0;   // корінь, вибраний користувачем, обходимо навіть якщо це посилання
            if (dir.Left is DirectoryInfo ld && !(nested && IsLink(ld)))
                foreach (var e in ld.EnumerateFileSystemInfos("*", Flat)) byName[e.Name] = (e, null);
            if (dir.Right is DirectoryInfo rd && !(nested && IsLink(rd)))
                foreach (var e in rd.EnumerateFileSystemInfos("*", Flat))
                    byName[e.Name] = byName.TryGetValue(e.Name, out var x) ? (x.l, e) : (null, e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dir.Error = ex.Message;
        }

        foreach (var (name, (l, r)) in byName)
        {
            bool lDir = l is DirectoryInfo, rDir = r is DirectoryInfo;
            // Файл з одного боку і каталог з іншого — це різні об’єкти: показуємо обидва окремо.
            if (l != null && r != null && lDir != rDir)
            {
                dir.Children.Add(Single(name, dir.RelativePath, l, null, o, ct, progress));
                dir.Children.Add(Single(name, dir.RelativePath, null, r, o, ct, progress));
                continue;
            }
            dir.Children.Add(Single(name, dir.RelativePath, l, r, o, ct, progress));
        }

        dir.Children.Sort((x, y) => x.IsDirectory != y.IsDirectory
            ? (x.IsDirectory ? -1 : 1)
            : NativeMethods.StrCmpLogicalW(x.Name, y.Name));

        if (dir.Left != null && dir.Right != null)
            dir.Status = dir.Error == null && dir.Children.All(c => c.Status == EntryStatus.Same) ? EntryStatus.Same : EntryStatus.Different;
    }

    static FolderEntry Single(string name, string parentRel, FileSystemInfo? l, FileSystemInfo? r,
        FolderCompareOptions o, CancellationToken ct, Action<string>? progress)
    {
        bool isDir = (l ?? r) is DirectoryInfo;
        var e = new FolderEntry
        {
            Name = name, RelativePath = parentRel.Length == 0 ? name : Path.Combine(parentRel, name),
            IsDirectory = isDir, Left = l, Right = r,
            Status = l == null ? EntryStatus.RightOnly : r == null ? EntryStatus.LeftOnly : EntryStatus.Same,
        };
        if (isDir) Fill(e, o, ct, progress);
        else if (l is FileInfo lf && r is FileInfo rf) CompareFiles(e, lf, rf, o, ct);
        return e;
    }

    static void CompareFiles(FolderEntry e, FileInfo l, FileInfo r, FolderCompareOptions o, CancellationToken ct)
    {
        var dt = l.LastWriteTimeUtc - r.LastWriteTimeUtc;
        bool sameTime = dt.Duration() <= TimeTolerance;
        bool same;
        try
        {
            same = l.Length == r.Length && (o.ByContent ? ContentEquals(l.FullName, r.FullName, ct) : sameTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Error = ex.Message;
            same = false;
        }
        e.Status = same ? EntryStatus.Same : EntryStatus.Different;
        if (!same && !sameTime)
        {
            e.LeftNewer = dt > TimeSpan.Zero;
            e.RightNewer = dt < TimeSpan.Zero;
        }
    }

    /// <summary>Побайтне порівняння двох файлів однакового розміру.</summary>
    public static bool ContentEquals(string a, string b, CancellationToken ct = default) => FirstDifference(a, b, ct) == null;

    /// <summary>Зміщення першого відмінного байта (або довжина коротшого, якщо один — початок іншого); null — однакові.</summary>
    public static long? FirstDifference(string a, string b, CancellationToken ct = default)
    {
        const int Chunk = 1 << 20;
        using var fa = Util.OpenShared(a, FileOptions.SequentialScan);
        using var fb = Util.OpenShared(b, FileOptions.SequentialScan);
        var ba = new byte[Chunk];
        var bb = new byte[Chunk];
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int na = fa.ReadAtLeast(ba, Chunk, throwOnEndOfStream: false);
            int nb = fb.ReadAtLeast(bb, Chunk, throwOnEndOfStream: false);
            int common = Math.Min(na, nb);
            int diff = ba.AsSpan(0, common).CommonPrefixLength(bb.AsSpan(0, common));
            if (diff < common) return offset + diff;
            if (na != nb) return offset + common;
            if (na == 0) return null;
            offset += na;
        }
    }

    static bool IsLink(DirectoryInfo d)
    {
        try { return d.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>Скільки елементів якого статусу (без каталогів, що лише містять відмінності).</summary>
    public static (int same, int different, int leftOnly, int rightOnly) Count(FolderEntry root)
    {
        int s = 0, d = 0, l = 0, r = 0;
        void Walk(FolderEntry e)
        {
            foreach (var c in e.Children)
            {
                if (!c.IsDirectory || c.Status is EntryStatus.LeftOnly or EntryStatus.RightOnly)
                {
                    switch (c.Status)
                    {
                        case EntryStatus.Same: s++; break;
                        case EntryStatus.Different: d++; break;
                        case EntryStatus.LeftOnly: l++; break;
                        case EntryStatus.RightOnly: r++; break;
                    }
                }
                if (c.IsDirectory && c.Status is not (EntryStatus.LeftOnly or EntryStatus.RightOnly)) Walk(c);
            }
        }
        Walk(root);
        return (s, d, l, r);
    }
}
