using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ByteCommander.Viewer;

/// <summary>Збіг пошуку: рядок, позиція в ньому (для hex — байт у рядку) і довжина.</summary>
public readonly record struct TextMatch(long Row, int Column, int Length);

/// <summary>Джерело рядків для віртуального переглядача: читає з диска лише те, що видно.</summary>
public interface IRowSource : IDisposable
{
    /// <summary>Скільки рядків відомо зараз (під час фонової індексації число росте).</summary>
    long RowCount { get; }
    bool IsComplete { get; }
    bool ShowLineNumbers { get; }
    /// <summary>Викликається з фонового потоку, коли змінилась кількість рядків.</summary>
    event Action? RowCountChanged;

    IReadOnlyList<string> GetRows(long first, int count);

    /// <summary>Пошук від позиції (рядок, колонка) включно до кінця файлу.</summary>
    TextMatch? Find(string query, long fromRow, int fromColumn, CancellationToken ct, Action<double>? progress = null);

    /// <summary>Ділянки рядка, які треба підсвітити для збігу (колонка, довжина в символах відображення).</summary>
    IEnumerable<(int Column, int Length)> GetHighlights(long row, TextMatch match);
}

/// <summary>
/// Розбиває байтовий потік на рядки: кінець рядка — LF (CR перед ним відкидається), або «м’який» розрив,
/// якщо рядок довший за <see cref="TextRowSource.MaxLineBytes"/> — щоб багатомегабайтний рядок
/// (мініфікований JSON, лог в один рядок) не читався з диска цілком при кожному перемальовуванні.
/// Цим самим правилом користуються і індексатор, і читання, і пошук — тому номери рядків збігаються.
/// </summary>
internal sealed class LineScanner
{
    readonly Stream stream;
    readonly long end;
    readonly int unit;          // 1 — однобайтові кодування / UTF-8, 2 — UTF-16
    readonly bool bigEndian;
    readonly bool utf8;
    readonly int maxLine;
    readonly byte[] buf;
    long bufStart;              // зміщення buf[0] у файлі
    int bufLen;
    long pos;                   // початок наступного рядка
    int lastOffset, lastLength;

    public LineScanner(Stream stream, long start, long end, int unit, bool bigEndian, bool utf8, int maxLineBytes)
    {
        this.stream = stream;
        this.end = end;
        this.unit = unit;
        this.bigEndian = bigEndian;
        this.utf8 = utf8;
        maxLine = maxLineBytes - maxLineBytes % unit;
        buf = new byte[Math.Max(1 << 20, maxLine * 2 + 16)];
        pos = start;
        bufStart = start;
    }

    public long Position => pos;

    /// <summary>Байти вмісту останнього прочитаного рядка (без переводу рядка). Дійсні до наступного виклику.</summary>
    public ReadOnlySpan<byte> LastContent => buf.AsSpan(lastOffset, lastLength);

    public bool Next(out long lineStart, out int contentLength)
    {
        lineStart = pos;
        contentLength = 0;
        if (pos >= end) return false;

        EnsureWindow();
        int rel = (int)(pos - bufStart);
        int avail = (int)Math.Min(bufLen - rel, end - pos);
        int window = Math.Min(avail, maxLine + unit);
        var span = buf.AsSpan(rel, window);

        int nl = FindNewline(span);
        int next;
        if (nl >= 0)
        {
            contentLength = nl;
            if (contentLength >= unit && IsCarriageReturn(span, contentLength - unit)) contentLength -= unit;
            next = nl + unit;
        }
        else if (avail <= maxLine)
        {
            contentLength = avail;     // останній рядок без переводу
            next = avail;
        }
        else
        {
            int cut = maxLine;         // м’який розрив
            if (utf8)
                while (cut > 0 && (span[cut] & 0xC0) == 0x80) cut--;   // не різати UTF-8 символ
            if (cut == 0) cut = maxLine;
            contentLength = cut;
            next = cut;
        }

        lastOffset = rel;
        lastLength = contentLength;
        pos += next;
        return true;
    }

    void EnsureWindow()
    {
        long need = Math.Min(end, pos + maxLine + unit * 2);
        if (pos >= bufStart && need <= bufStart + bufLen) return;
        stream.Position = pos;
        bufStart = pos;
        bufLen = 0;
        int toRead = (int)Math.Min(buf.Length, end - pos);
        while (bufLen < toRead)
        {
            int n = stream.Read(buf, bufLen, toRead - bufLen);
            if (n <= 0) break;
            bufLen += n;
        }
    }

    int FindNewline(ReadOnlySpan<byte> span)
    {
        if (unit == 1) return span.IndexOf((byte)'\n');
        for (int i = 0; i + 1 < span.Length; i += 2)
        {
            bool lf = bigEndian ? span[i] == 0 && span[i + 1] == 0x0A : span[i] == 0x0A && span[i + 1] == 0;
            if (lf) return i;
        }
        return -1;
    }

    bool IsCarriageReturn(ReadOnlySpan<byte> span, int i) =>
        unit == 1 ? span[i] == (byte)'\r'
        : bigEndian ? span[i] == 0 && span[i + 1] == 0x0D
        : span[i] == 0x0D && span[i + 1] == 0;
}

/// <summary>Текстовий файл будь-якого розміру: фонова індексація рядків + читання видимих рядків з диска.</summary>
public sealed class TextRowSource : IRowSource
{
    public const int MaxLineBytes = 16 * 1024;
    internal const int CheckpointEvery = 256;
    const int TabSize = 4;

    readonly string path;
    readonly Encoding encoding;
    readonly int unit;
    readonly bool bigEndian, utf8;
    readonly long dataStart, length;
    readonly List<long> checkpoints = new();   // початок рядків 0, 256, 512, …
    readonly object gate = new();
    readonly FileStream reader;
    readonly CancellationTokenSource cts = new();
    long rowCount;
    volatile bool complete;
    (long first, int count, IReadOnlyList<string> rows)? cache;

    public TextRowSource(string path, Encoding encoding)
    {
        this.path = path;
        this.encoding = encoding;
        unit = encoding is UnicodeEncoding ? 2 : 1;
        bigEndian = encoding.CodePage == 1201;
        utf8 = encoding is UTF8Encoding;
        reader = OpenShared(path);
        length = reader.Length;
        dataStart = PreambleLength(reader, encoding);
        checkpoints.Add(dataStart);
        Task.Run(Index);
    }

    public Encoding Encoding => encoding;
    public long RowCount => Interlocked.Read(ref rowCount);
    public bool IsComplete => complete;
    public bool ShowLineNumbers => true;
    public event Action? RowCountChanged;

    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);

    static int PreambleLength(FileStream fs, Encoding enc)
    {
        Span<byte> head = stackalloc byte[4];
        fs.Position = 0;
        int n = fs.Read(head);
        var pre = enc.GetPreamble();
        if (pre.Length > 0 && n >= pre.Length && head[..pre.Length].SequenceEqual(pre)) return pre.Length;
        if (enc is UTF8Encoding && n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return 3;
        return 0;
    }

    LineScanner Scanner(Stream s, long start) => new(s, start, length, unit, bigEndian, utf8, MaxLineBytes);

    void Index()
    {
        try
        {
            using var fs = OpenShared(path);
            var sc = Scanner(fs, dataStart);
            long n = 0;
            var sw = Stopwatch.StartNew();
            while (sc.Next(out var start, out _))
            {
                if (n > 0 && n % CheckpointEvery == 0)
                    lock (gate) checkpoints.Add(start);
                n++;
                if (sw.ElapsedMilliseconds >= 150)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    Interlocked.Exchange(ref rowCount, n);
                    RowCountChanged?.Invoke();
                    sw.Restart();
                }
            }
            Interlocked.Exchange(ref rowCount, n);
            complete = true;
            RowCountChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException)
        {
            complete = true;
            RowCountChanged?.Invoke();
        }
    }

    /// <summary>Сканер, встановлений на початок рядка <paramref name="row"/> (або найближчого відомого).</summary>
    LineScanner SeekRow(Stream s, long row, out long atRow)
    {
        long cp;
        int k;
        lock (gate)
        {
            k = (int)Math.Min(row / CheckpointEvery, checkpoints.Count - 1);
            cp = checkpoints[k];
        }
        var sc = Scanner(s, cp);
        atRow = (long)k * CheckpointEvery;
        while (atRow < row && sc.Next(out _, out _)) atRow++;
        return sc;
    }

    public IReadOnlyList<string> GetRows(long first, int count)
    {
        lock (reader)
        {
            if (cache is { } c && c.first == first && c.count == count) return c.rows;
            var rows = new List<string>(count);
            var sc = SeekRow(reader, first, out var at);
            if (at == first)
                while (rows.Count < count && sc.Next(out _, out _))
                    rows.Add(ToDisplay(encoding.GetString(sc.LastContent)));
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
        using var fs = OpenShared(path);
        var sc = SeekRow(fs, fromRow, out var row);
        if (row != fromRow) return null;
        var sw = Stopwatch.StartNew();
        double span = Math.Max(1, length - dataStart);
        while (sc.Next(out var start, out _))
        {
            var line = ToDisplay(encoding.GetString(sc.LastContent));
            int from = row == fromRow ? Math.Min(fromColumn, line.Length) : 0;
            int idx = line.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return new TextMatch(row, idx, query.Length);
            row++;
            if (sw.ElapsedMilliseconds >= 100)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((start - dataStart) / span);
                sw.Restart();
            }
        }
        return null;
    }

    public IEnumerable<(int Column, int Length)> GetHighlights(long row, TextMatch match)
    {
        if (row == match.Row) yield return (match.Column, match.Length);
    }

    public void Dispose()
    {
        cts.Cancel();
        lock (reader) reader.Dispose();
    }
}

/// <summary>Hex-перегляд файлу будь-якого розміру: 16 байт на рядок, читаються лише видимі.</summary>
public sealed class HexRowSource : IRowSource
{
    public const int BytesPerRow = 16;
    // Колонки рядка: "00000000  4D 5A 90 00 03 00 00 00  04 00 00 00 FF FF 00 00  MZ..............."
    const int HexStart = 10, AsciiStart = HexStart + BytesPerRow * 3 + 2;

    readonly string path;
    readonly FileStream reader;
    readonly long length;

    public HexRowSource(string path)
    {
        this.path = path;
        reader = TextRowSource.OpenShared(path);
        length = reader.Length;
    }

    public long RowCount => (length + BytesPerRow - 1) / BytesPerRow;
    public bool IsComplete => true;
    public bool ShowLineNumbers => false;
    public event Action? RowCountChanged { add { } remove { } }

    public IReadOnlyList<string> GetRows(long first, int count)
    {
        var rows = new List<string>(count);
        long offset = first * BytesPerRow;
        if (offset >= length) return rows;
        var bytes = new byte[(int)Math.Min((long)count * BytesPerRow, length - offset)];
        lock (reader)
        {
            reader.Position = offset;
            int read = 0;
            while (read < bytes.Length)
            {
                int n = reader.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
        }
        for (int r = 0; r * BytesPerRow < bytes.Length; r++)
            rows.Add(FormatRow(offset + (long)r * BytesPerRow, bytes.AsSpan(r * BytesPerRow, Math.Min(BytesPerRow, bytes.Length - r * BytesPerRow))));
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
        using var fs = TextRowSource.OpenShared(path);
        long start = fromRow * BytesPerRow + fromColumn;
        if (start >= length) return null;
        var buf = new byte[(1 << 20) + pattern.Length];
        long bufOffset = start;
        int carry = 0;
        var sw = Stopwatch.StartNew();
        fs.Position = start;
        while (true)
        {
            int n = fs.Read(buf, carry, buf.Length - carry);
            if (n <= 0) return null;
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
            if (sw.ElapsedMilliseconds >= 100)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((double)bufOffset / Math.Max(1, length));
                sw.Restart();
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

    public void Dispose()
    {
        lock (reader) reader.Dispose();
    }
}
