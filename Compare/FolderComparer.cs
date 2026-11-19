using ByteCommander.Services;

namespace ByteCommander.Compare;

/// <summary>Статус елемента. NotCompared — посилання (junction/symlink), вміст якого не порівнювався, або каталог, що містить такі.</summary>
public enum EntryStatus { Same, Different, LeftOnly, RightOnly, NotCompared }

/// <summary>Лічильник підсумку, до якого належить статус.</summary>
public enum CountBucket { None, Same, Different, LeftOnly, RightOnly, NotCompared }

/// <summary>Як статус показується (позначка, текст) і до якого лічильника входить.</summary>
public sealed record EntryStatusInfo(string Mark, string Text, string DirectoryText, CountBucket Bucket)
{
    static readonly Dictionary<EntryStatus, EntryStatusInfo> Map = new()
    {
        [EntryStatus.Same] = new("=", "Однакові", "Однакові", CountBucket.Same),
        [EntryStatus.Different] = new("≠", "Різні", "Є відмінності всередині", CountBucket.Different),
        [EntryStatus.LeftOnly] = new("◀", "Лише зліва", "Лише зліва", CountBucket.LeftOnly),
        [EntryStatus.RightOnly] = new("▶", "Лише справа", "Лише справа", CountBucket.RightOnly),
        [EntryStatus.NotCompared] = new("↪", LinkText, "Містить посилання, вміст яких не порівнюється", CountBucket.NotCompared),
    };

    public const string LinkText = "Посилання (junction/symlink) — вміст не порівнюється";

    public static EntryStatusInfo Of(EntryStatus s) => Map[s];
}

public sealed record FolderCompareOptions(bool ByContent = false);

/// <summary>Елемент дерева порівняння каталогів: файл або каталог з одного чи обох боків.</summary>
public sealed class FolderEntry
{
    public required string Name { get; init; }
    /// <summary>Шлях відносно коренів порівняння ("" для самого кореня).</summary>
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>Хоча б з одного боку це reparse-point (junction/symlink): вміст не обходиться.</summary>
    public bool IsLink { get; internal set; }
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
    static readonly EnumerationOptions ShallowListing = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

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
                foreach (var e in ld.EnumerateFileSystemInfos("*", ShallowListing)) byName[e.Name] = (e, null);
            if (dir.Right is DirectoryInfo rd && !(nested && IsLink(rd)))
                foreach (var e in rd.EnumerateFileSystemInfos("*", ShallowListing))
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
                dir.Children.Add(CreateEntry(name, dir.RelativePath, l, null, o, ct, progress));
                dir.Children.Add(CreateEntry(name, dir.RelativePath, null, r, o, ct, progress));
                continue;
            }
            dir.Children.Add(CreateEntry(name, dir.RelativePath, l, r, o, ct, progress));
        }

        dir.Children.Sort((x, y) => x.IsDirectory != y.IsDirectory
            ? (x.IsDirectory ? -1 : 1)
            : NativeMethods.StrCmpLogicalW(x.Name, y.Name));

        if (dir.Left != null && dir.Right != null)
            dir.Status = dir.Error != null || dir.Children.Any(c => c.Status is EntryStatus.Different or EntryStatus.LeftOnly or EntryStatus.RightOnly)
                ? EntryStatus.Different
                : dir.Children.Any(c => c.Status == EntryStatus.NotCompared) ? EntryStatus.NotCompared : EntryStatus.Same;
    }

    static FolderEntry CreateEntry(string name, string parentRel, FileSystemInfo? l, FileSystemInfo? r,
        FolderCompareOptions o, CancellationToken ct, Action<string>? progress)
    {
        bool isDir = (l ?? r) is DirectoryInfo;
        var e = new FolderEntry
        {
            Name = name, RelativePath = parentRel.Length == 0 ? name : Path.Combine(parentRel, name),
            IsDirectory = isDir, Left = l, Right = r,
            Status = l == null ? EntryStatus.RightOnly : r == null ? EntryStatus.LeftOnly : EntryStatus.Same,
        };
        if (isDir)
        {
            e.IsLink = l is DirectoryInfo ld && IsLink(ld) || r is DirectoryInfo rd && IsLink(rd);
            if (e.IsLink && l != null && r != null) e.Status = EntryStatus.NotCompared;   // вміст посилань не порівнюємо
            else Fill(e, o, ct, progress);
        }
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
            same = l.Length == r.Length && (o.ByContent ? Util.FilesEqual(l.FullName, r.FullName, ct) : sameTime);
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

    static bool IsLink(DirectoryInfo d)
    {
        try { return d.Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>Скільки елементів якого статусу (без каталогів, що лише містять відмінності).</summary>
    public static (int same, int different, int leftOnly, int rightOnly, int notCompared) Count(FolderEntry root)
    {
        var n = new int[Enum.GetValues<CountBucket>().Length];
        void Walk(FolderEntry e)
        {
            foreach (var c in e.Children)
            {
                var info = EntryStatusInfo.Of(c.Status);
                // Каталог рахується сам лише якщо його вміст не розкривається (лише з одного боку або посилання).
                bool leaf = !c.IsDirectory || c.IsLink || c.Status is EntryStatus.LeftOnly or EntryStatus.RightOnly;
                if (leaf && info.Bucket != CountBucket.None) n[(int)info.Bucket]++;
                if (c.IsDirectory && !c.IsLink && c.Status is not (EntryStatus.LeftOnly or EntryStatus.RightOnly)) Walk(c);
            }
        }
        Walk(root);
        return (n[(int)CountBucket.Same], n[(int)CountBucket.Different], n[(int)CountBucket.LeftOnly],
            n[(int)CountBucket.RightOnly], n[(int)CountBucket.NotCompared]);
    }
}
