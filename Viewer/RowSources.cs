using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;
using ByteCommander.Services;

namespace ByteCommander.Viewer;

/// <summary>
/// Збіг пошуку: рядок, позиція в ньому (для hex — байт у рядку) і довжина.
/// <paramref name="SpillOver"/> — скільки символів збігу продовжується на наступному рядку
/// (коли збіг перетинає «м’який» розрив довгого рядка).
/// </summary>
public readonly record struct TextMatch(long Row, int Column, int Length, int SpillOver = 0);

/// <summary>Що сталося з файлом з моменту попередньої перевірки (<see cref="IRowSource.Poll"/>).</summary>
public enum SourceChange
{
    Unchanged,
    /// <summary>Файл дописали — нові рядки дочитуються у фоні (прийде <see cref="IRowSource.RowCountChanged"/>).</summary>
    Grew,
    /// <summary>
    /// Файл за шляхом уже інший: його обрізали або підмінили новим (ротація логу перейменуванням —
    /// дескриптор лишився на старому файлі). Джерело треба створити заново.
    /// </summary>
    Reset,
    /// <summary>Файлу за шляхом немає (видалили; при ротації новий може з’явитися згодом).</summary>
    Deleted,
}

/// <summary>Спільна для джерел перевірка файлу на диску під час стеження.</summary>
internal static class FileProbe
{
    /// <summary>
    /// Порівнює відкритий файл із тим, що зараз лежить за <paramref name="path"/>.
    /// <see cref="SourceChange.Grew"/> — у <paramref name="now"/> новий розмір файлу.
    /// </summary>
    public static SourceChange Check(SafeFileHandle file, string path, long known, out long now)
    {
        now = known;
        try
        {
            now = RandomAccess.GetLength(file);
            if (!File.Exists(path)) return SourceChange.Deleted;
            bool? same = NativeMethods.IsSameFile(file, path);
            if (same == false || now < known) return SourceChange.Reset;
            return now > known ? SourceChange.Grew : SourceChange.Unchanged;
        }
        catch (ObjectDisposedException)
        {
            return SourceChange.Unchanged;
        }
    }
}

/// <summary>Джерело рядків для віртуального переглядача: читає з диска лише те, що видно.</summary>
public interface IRowSource : IDisposable
{
    /// <summary>Скільки рядків відомо зараз (під час фонової індексації число росте).</summary>
    long RowCount { get; }
    bool IsComplete { get; }
    bool ShowLineNumbers { get; }
    /// <summary>Розмір файлу в байтах, який зараз охоплює джерело.</summary>
    long Length { get; }
    /// <summary>Викликається з фонового потоку, коли змінилась кількість рядків.</summary>
    event Action? RowCountChanged;

    /// <summary>
    /// Перевірити файл на диску (для стеження, як «tail -f»): якщо його дописали — дочитати нове.
    /// Розмір береться з відкритого дескриптора, а не з каталогу: для файлу, який зараз пише інша
    /// програма, розмір у каталозі оновлюється із запізненням.
    /// </summary>
    SourceChange Poll();

    IReadOnlyList<string> GetRows(long first, int count);

    /// <summary>Пошук від позиції (рядок, колонка) включно до кінця файлу.</summary>
    TextMatch? Find(string query, long fromRow, int fromColumn, CancellationToken ct, Action<double>? progress = null);

    /// <summary>Ділянки рядка, які треба підсвітити для збігу (колонка, довжина в символах відображення).</summary>
    IEnumerable<(int Column, int Length)> GetHighlights(long row, TextMatch match);
}

/// <summary>Як у кодуванні виглядає одиниця коду: 1 байт (однобайтові, UTF-8) чи 2 (UTF-16 LE/BE).</summary>
internal readonly record struct CodeUnitLayout(int BytesPerCodeUnit, bool BigEndian, bool Utf8)
{
    public static CodeUnitLayout From(Encoding e) =>
        new(TextEncodings.IsUtf16(e) ? 2 : 1, TextEncodings.IsUtf16BigEndian(e), e is UTF8Encoding);
}

/// <summary>Звіт про прогрес не частіше, ніж раз на заданий інтервал (і перевірка скасування разом із ним).</summary>
internal sealed class Throttle(TimeSpan interval)
{
    readonly Stopwatch sw = Stopwatch.StartNew();

    public bool Due()
    {
        if (sw.Elapsed < interval) return false;
        sw.Restart();
        return true;
    }
}

/// <summary>
/// Розбиває байтовий потік на рядки: кінець рядка — LF (CR перед ним відкидається), або «м’який» розрив,
/// якщо рядок довший за <see cref="TextRowSource.MaxLineBytes"/> — щоб багатомегабайтний рядок
/// (мініфікований JSON, лог в один рядок) не читався з диска цілком при кожному перемальовуванні.
/// Цим самим правилом користуються і індексатор, і читання, і пошук — тому номери рядків збігаються.
/// </summary>
internal sealed class LineScanner
{
    readonly SafeFileHandle file;
    readonly long end;
    readonly CodeUnitLayout layout;
    readonly int maxLine;
    readonly byte[] buf;
    long bufStart;              // зміщення buf[0] у файлі
    int bufLen;
    long pos;                   // початок наступного рядка
    int lastOffset, lastLength;

    /// <param name="file">Дескриптор файлу; читання позиційні (RandomAccess), тож кілька сканерів можуть працювати паралельно.</param>
    public LineScanner(SafeFileHandle file, long start, long end, CodeUnitLayout layout, int maxLineBytes)
    {
        this.file = file;
        this.end = end;
        this.layout = layout;
        maxLine = maxLineBytes - maxLineBytes % layout.BytesPerCodeUnit;
        buf = new byte[Math.Max(1 << 20, maxLine * 2 + 16)];
        pos = start;
        bufStart = start;
    }

    public long Position => pos;

    /// <summary>Байти вмісту останнього прочитаного рядка (без переводу рядка). Дійсні до наступного виклику.</summary>
    public ReadOnlySpan<byte> LastContent => buf.AsSpan(lastOffset, lastLength);

    /// <summary>Останній рядок закінчився не переводом рядка, а м’яким розривом — текст продовжується в наступному.</summary>
    public bool LastWasSoftBreak { get; private set; }

    /// <summary>Останній рядок закінчився переводом рядка (інакше — м’який розрив або кінець даних).</summary>
    public bool LastHadNewline { get; private set; }

    int Unit => layout.BytesPerCodeUnit;

    public bool Next(out long lineStart, out int contentLength)
    {
        lineStart = pos;
        contentLength = 0;
        LastWasSoftBreak = false;
        LastHadNewline = false;
        if (pos >= end) return false;

        EnsureWindow();
        int rel = (int)(pos - bufStart);
        int avail = (int)Math.Min(bufLen - rel, end - pos);
        // Файл виявився коротшим, ніж очікувалось (його обрізали посеред читання): це кінець даних,
        // а не порожній рядок — інакше сканер «читав» би нескінченні порожні рядки, не просуваючись.
        if (avail <= 0) return false;
        int window = Math.Min(avail, maxLine + Unit);
        var span = buf.AsSpan(rel, window);

        int nl = FindNewline(span);
        int next;
        if (nl >= 0)
        {
            contentLength = nl;
            if (contentLength >= Unit && IsCarriageReturn(span, contentLength - Unit)) contentLength -= Unit;
            next = nl + Unit;
            LastHadNewline = true;
        }
        else if (avail <= maxLine)
        {
            contentLength = avail;     // останній рядок без переводу
            next = avail;
        }
        else
        {
            int cut = SoftBreakPosition(span);
            contentLength = cut;
            next = cut;
            LastWasSoftBreak = true;
        }

        lastOffset = rel;
        lastLength = contentLength;
        pos += next;
        return true;
    }

    /// <summary>Де розірвати задовгий рядок, не розрізаючи символ (UTF-8 послідовність чи сурогатну пару UTF-16).</summary>
    int SoftBreakPosition(ReadOnlySpan<byte> span)
    {
        int cut = maxLine;
        if (layout.Utf8)
        {
            while (cut > 0 && (span[cut] & 0xC0) == 0x80) cut--;
        }
        else if (Unit == 2 && cut >= 2)
        {
            int prev = layout.BigEndian ? span[cut - 2] << 8 | span[cut - 1] : span[cut - 1] << 8 | span[cut - 2];
            if (prev is >= 0xD800 and <= 0xDBFF) cut -= 2;   // висока половина сурогатної пари лишається з низькою
        }
        return cut > 0 ? cut : maxLine;
    }

    void EnsureWindow()
    {
        long need = Math.Min(end, pos + maxLine + Unit * 2);
        if (pos >= bufStart && need <= bufStart + bufLen) return;
        bufStart = pos;
        int toRead = (int)Math.Min(buf.Length, end - pos);
        bufLen = ReadAt(file, buf.AsSpan(0, toRead), pos);
    }

    /// <summary>Прочитати до <paramref name="buffer"/>.Length байтів з позиції; менше — лише в кінці файлу.</summary>
    internal static int ReadAt(SafeFileHandle file, Span<byte> buffer, long offset)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(file, buffer[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    int FindNewline(ReadOnlySpan<byte> span)
    {
        if (Unit == 1) return span.IndexOf((byte)'\n');
        for (int i = 0; i + 1 < span.Length; i += 2)
        {
            bool lf = layout.BigEndian ? span[i] == 0 && span[i + 1] == 0x0A : span[i] == 0x0A && span[i + 1] == 0;
            if (lf) return i;
        }
        return -1;
    }

    bool IsCarriageReturn(ReadOnlySpan<byte> span, int i) =>
        Unit == 1 ? span[i] == (byte)'\r'
        : layout.BigEndian ? span[i] == 0 && span[i + 1] == 0x0D
        : span[i] == 0x0D && span[i + 1] == 0;
}

/// <summary>Текстовий файл будь-якого розміру: фонова індексація рядків + читання видимих рядків з диска.</summary>
public sealed class TextRowSource : IRowSource
{
    public const int MaxLineBytes = 16 * 1024;
    internal const int CheckpointEvery = 256;
    const int TabSize = 4;
    static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(150);

    readonly string path;
    readonly Encoding encoding;
    readonly CodeUnitLayout layout;
    readonly long dataStart;
    readonly List<long> checkpoints = new();   // початок рядків 0, 256, 512, …
    readonly object gate = new();
    readonly object cacheGate = new();
    // Один дескриптор на все джерело: індексація, читання рядків і пошук бачать той самий файл,
    // навіть якщо за шляхом його вже підмінили (ротація логу перейменуванням). Читання позиційні —
    // потоки не заважають один одному.
    readonly SafeFileHandle file;
    readonly CancellationTokenSource cts = new();
    long length;                               // скільки байтів файлу охоплює індекс (росте при стеженні)
    long rowCount;
    volatile bool complete;
    volatile bool indexing;
    // Звідки продовжити індексацію, коли файл допишуть: після останнього завершеного рядка.
    // Недописаний останній рядок (без переводу рядка) перечитується — до нього можуть дописати решту.
    long resumeRow, resumePos;
    (long first, int count, IReadOnlyList<string> rows)? cache;

    public TextRowSource(string path, Encoding encoding)
    {
        this.path = path;
        this.encoding = encoding;
        layout = CodeUnitLayout.From(encoding);
        file = Util.OpenSharedHandle(path);
        length = RandomAccess.GetLength(file);
        Span<byte> head = stackalloc byte[4];
        int n = LineScanner.ReadAt(file, head, 0);
        dataStart = TextEncodings.PreambleLength(head[..n], encoding);
        checkpoints.Add(dataStart);
        StartIndex(0, dataStart);
    }

    public Encoding Encoding => encoding;
    public long RowCount => Interlocked.Read(ref rowCount);
    public bool IsComplete => complete;
    public bool ShowLineNumbers => true;
    public long Length => Interlocked.Read(ref length);
    public event Action? RowCountChanged;

    LineScanner Scanner(long start) => new(file, start, Length, layout, MaxLineBytes);

    void StartIndex(long fromRow, long fromPos)
    {
        indexing = true;
        complete = false;
        Task.Run(() => Index(fromRow, fromPos));
    }

    /// <summary>Індексує рядки від <paramref name="fromPos"/> (початок рядка номер <paramref name="fromRow"/>) до кінця охопленого файлу.</summary>
    void Index(long fromRow, long fromPos)
    {
        long n = fromRow;   // номер рядка, який зараз читаємо
        try
        {
            var sc = Scanner(fromPos);
            var report = new Throttle(ReportEvery);
            long lastStart = fromPos;
            bool lastFinished = true;
            while (sc.Next(out var start, out _))
            {
                if (n % CheckpointEvery == 0)
                    lock (gate)
                        if (n / CheckpointEvery == checkpoints.Count) checkpoints.Add(start);
                n++;
                lastStart = start;
                lastFinished = sc.LastHadNewline || sc.LastWasSoftBreak;
                if (report.Due())
                {
                    cts.Token.ThrowIfCancellationRequested();
                    Interlocked.Exchange(ref rowCount, n);
                    RowCountChanged?.Invoke();
                }
            }
            lock (gate)
            {
                (resumeRow, resumePos) = lastFinished ? (n, sc.Position) : (n - 1, lastStart);
            }
            Interlocked.Exchange(ref rowCount, n);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        catch (IOException) { }

        lock (cacheGate) cache = null;   // останній рядок міг дописатися
        indexing = false;
        complete = true;
        RowCountChanged?.Invoke();
    }

    public SourceChange Poll()
    {
        var change = FileProbe.Check(file, path, Length, out long now);
        if (change != SourceChange.Grew) return change;
        if (indexing) return SourceChange.Unchanged;   // ще індексуємо — дочитаємо на наступній перевірці

        long row, pos;
        lock (gate) (row, pos) = (resumeRow, resumePos);
        Interlocked.Exchange(ref length, now);
        lock (cacheGate) cache = null;
        StartIndex(row, pos);
        return SourceChange.Grew;
    }

    /// <summary>Сканер, встановлений на початок рядка <paramref name="row"/> (або найближчого відомого).</summary>
    LineScanner SeekRow(long row, out long atRow)
    {
        long cp;
        int k;
        lock (gate)
        {
            k = (int)Math.Min(row / CheckpointEvery, checkpoints.Count - 1);
            cp = checkpoints[k];
        }
        var sc = Scanner(cp);
        atRow = (long)k * CheckpointEvery;
        while (atRow < row && sc.Next(out _, out _)) atRow++;
        return sc;
    }

    public IReadOnlyList<string> GetRows(long first, int count)
    {
        lock (cacheGate)
        {
            if (cache is { } c && c.first == first && c.count == count) return c.rows;
            var rows = new List<string>(count);
            try
            {
                var sc = SeekRow(first, out var at);
                if (at == first)
                    while (rows.Count < count && sc.Next(out _, out _))
                        rows.Add(ToDisplay(encoding.GetString(sc.LastContent)));
            }
            catch (ObjectDisposedException)
            {
                return rows;   // джерело вже закрили (заміна джерела) — показувати нічого
            }
            cache = (first, count, rows);
            return rows;
        }
    }

    /// <summary>Текст для відображення: табуляції розгорнуто до позицій, кратних 4, керівні символи — «·».</summary>
    internal static string ToDisplay(string s)
    {
        if (s.AsSpan().IndexOfAnyInRange('\0', '\x1F') < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            if (ch == '\t') sb.Append(' ', TabSize - sb.Length % TabSize);
            else if (ch < ' ') sb.Append('·');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public TextMatch? Find(string query, long fromRow, int fromColumn, CancellationToken ct, Action<double>? progress = null)
    {
        if (query.Length == 0) return null;
        LineScanner sc;
        long row;
        try
        {
            sc = SeekRow(fromRow, out row);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        if (row != fromRow) return null;
        var report = new Throttle(TimeSpan.FromMilliseconds(100));
        double span = Math.Max(1, Length - dataStart);

        // Хвіст попереднього рядка, якщо він закінчився м’яким розривом: збіг може перетинати межу.
        string? prevTail = null;
        int prevLength = 0;
        while (sc.Next(out var start, out _))
        {
            var line = ToDisplay(encoding.GetString(sc.LastContent));

            if (prevTail != null)
            {
                var joined = prevTail + line[..Math.Min(line.Length, query.Length - 1)];
                int j = joined.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                int col = prevLength - prevTail.Length + j;
                if (j >= 0 && j < prevTail.Length && (row - 1 != fromRow || col >= fromColumn))
                    return new TextMatch(row - 1, col, query.Length, SpillOver: j + query.Length - prevTail.Length);
            }

            int from = row == fromRow ? Math.Min(fromColumn, line.Length) : 0;
            int idx = line.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return new TextMatch(row, idx, query.Length);

            prevTail = sc.LastWasSoftBreak && query.Length > 1 ? line[^Math.Min(line.Length, query.Length - 1)..] : null;
            prevLength = line.Length;
            row++;
            if (report.Due())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((start - dataStart) / span);
            }
        }
        return null;
    }

    public IEnumerable<(int Column, int Length)> GetHighlights(long row, TextMatch match)
    {
        if (row == match.Row) yield return (match.Column, match.Length - match.SpillOver);
        else if (row == match.Row + 1 && match.SpillOver > 0) yield return (0, match.SpillOver);
    }

    public void Dispose()
    {
        cts.Cancel();
        file.Dispose();
    }
}

/// <summary>Hex-перегляд файлу будь-якого розміру: 16 байт на рядок, читаються лише видимі.</summary>
public sealed class HexRowSource : IRowSource
{
    public const int BytesPerRow = 16;
    // Колонки рядка: "00000000  4D 5A 90 00 03 00 00 00  04 00 00 00 FF FF 00 00  MZ..............."
    const int HexStart = 10, AsciiStart = HexStart + BytesPerRow * 3 + 2;

    readonly string path;
    readonly SafeFileHandle file;   // один дескриптор: читання позиційні, файл той самий навіть після ротації
    long length;

    public HexRowSource(string path)
    {
        this.path = path;
        file = Util.OpenSharedHandle(path);
        length = RandomAccess.GetLength(file);
    }

    public long RowCount => (Length + BytesPerRow - 1) / BytesPerRow;
    public bool IsComplete => true;
    public bool ShowLineNumbers => false;
    public long Length => Interlocked.Read(ref length);
    public event Action? RowCountChanged;

    public SourceChange Poll()
    {
        var change = FileProbe.Check(file, path, Length, out long now);
        if (change != SourceChange.Grew) return change;
        Interlocked.Exchange(ref length, now);
        RowCountChanged?.Invoke();
        return SourceChange.Grew;
    }

    public IReadOnlyList<string> GetRows(long first, int count)
    {
        var rows = new List<string>(count);
        long offset = first * BytesPerRow, total = Length;
        if (offset >= total) return rows;
        var bytes = new byte[(int)Math.Min((long)count * BytesPerRow, total - offset)];
        int read;
        try
        {
            read = LineScanner.ReadAt(file, bytes, offset);
        }
        catch (ObjectDisposedException)
        {
            return rows;
        }
        for (int r = 0; r * BytesPerRow < read; r++)
            rows.Add(FormatRow(offset + (long)r * BytesPerRow, bytes.AsSpan(r * BytesPerRow, Math.Min(BytesPerRow, read - r * BytesPerRow))));
        return rows;
    }

    internal static string FormatRow(long offset, ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(AsciiStart + BytesPerRow);
        sb.Append(offset.ToString("X8")).Append("  ");
        for (int i = 0; i < BytesPerRow; i++)
        {
            if (i < b.Length) sb.Append(b[i].ToString("X2")).Append(' ');
            else sb.Append("   ");
            if (i == 7) sb.Append(' ');
        }
        sb.Append(' ');
        foreach (var c in b) sb.Append(c >= 32 && c < 127 ? (char)c : '·');
        return sb.ToString();
    }

    static int HexColumn(int i) => HexStart + i * 3 + (i >= 8 ? 1 : 0);

    /// <summary>«4D 5A» / «4d5a90» — шукаємо байти; інакше — текст як UTF-8 (з урахуванням регістру).</summary>
    internal static byte[] ParseQuery(string query)
    {
        var compact = Regex.Replace(query, @"\s+", "");
        if (compact.Length >= 2 && compact.Length % 2 == 0 && Regex.IsMatch(compact, "^[0-9A-Fa-f]+$") &&
            (query.Contains(' ') || compact.Length >= 4))
            return Convert.FromHexString(compact);
        return Encoding.UTF8.GetBytes(query);
    }

    public TextMatch? Find(string query, long fromRow, int fromColumn, CancellationToken ct, Action<double>? progress = null)
    {
        var pattern = ParseQuery(query);
        if (pattern.Length == 0) return null;
        long start = fromRow * BytesPerRow + fromColumn, fileLength = Length;
        if (start >= fileLength) return null;
        var buf = new byte[(1 << 20) + pattern.Length];
        long bufOffset = start;
        int carry = 0;
        var report = new Throttle(TimeSpan.FromMilliseconds(100));
        long readPos = start;
        while (true)
        {
            int n = RandomAccess.Read(file, buf.AsSpan(carry), readPos);
            if (n <= 0) return null;
            readPos += n;
            int total = carry + n;
            int idx = buf.AsSpan(0, total).IndexOf(pattern);
            if (idx >= 0)
            {
                long at = bufOffset + idx;
                return new TextMatch(at / BytesPerRow, (int)(at % BytesPerRow), pattern.Length);
            }
            carry = Math.Min(pattern.Length - 1, total);
            Array.Copy(buf, total - carry, buf, 0, carry);
            bufOffset += total - carry;
            if (report.Due())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((double)bufOffset / Math.Max(1, fileLength));
            }
        }
    }

    public IEnumerable<(int Column, int Length)> GetHighlights(long row, TextMatch match)
    {
        long from = match.Row * BytesPerRow + match.Column, to = from + match.Length;
        long rowStart = row * BytesPerRow;
        for (long o = Math.Max(from, rowStart); o < Math.Min(to, rowStart + BytesPerRow); o++)
        {
            int i = (int)(o - rowStart);
            yield return (HexColumn(i), 2);
            yield return (AsciiStart + i, 1);
        }
    }

    public void Dispose() => file.Dispose();
}
