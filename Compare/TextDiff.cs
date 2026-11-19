using System.Text;
using ByteCommander.Services;

namespace ByteCommander.Compare;

/// <summary>Частина рядка: початок і довжина в символах.</summary>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>Рядок порівняння поруч: однаковий, змінений (є з обох боків), видалений (лише зліва), доданий (лише справа).</summary>
public enum DiffKind { Equal, Changed, Deleted, Inserted }

public sealed record DiffOptions(bool IgnoreWhitespace = false, bool IgnoreCase = false);

/// <summary>Один рядок вигляду «поруч». Індекси рядків — від 0; null — з цього боку рядка немає.</summary>
public sealed class DiffRow
{
    public required DiffKind Kind { get; init; }
    public int? LeftLine { get; init; }
    public int? RightLine { get; init; }
    public string LeftText { get; init; } = "";
    public string RightText { get; init; } = "";
    /// <summary>Змінена частина лівого рядка (для <see cref="DiffKind.Changed"/>).</summary>
    public TextSpan HighlightLeft { get; init; }
    /// <summary>Змінена частина правого рядка (для <see cref="DiffKind.Changed"/>).</summary>
    public TextSpan HighlightRight { get; init; }
    /// <summary>Номер блоку відмінностей, до якого належить рядок; -1 для однакових.</summary>
    public int Hunk { get; init; } = -1;

    public string LeftNumber => LeftLine is int l ? (l + 1).ToString() : "";
    public string RightNumber => RightLine is int r ? (r + 1).ToString() : "";
}

public sealed class TextDiffResult
{
    public required IReadOnlyList<DiffRow> Rows { get; init; }
    /// <summary>Індекс першого рядка кожного блоку відмінностей у <see cref="Rows"/>.</summary>
    public required IReadOnlyList<int> HunkStarts { get; init; }
    public int ChangedLines { get; init; }
    public int DeletedLines { get; init; }
    public int InsertedLines { get; init; }
    /// <summary>
    /// Відмінностей забагато для точного алгоритму — середина показана як одна заміна
    /// (усе зліва видалено, усе справа додано). Спільні початок і кінець файлів при цьому точні.
    /// </summary>
    public bool Approximate { get; init; }
    public bool Identical => HunkStarts.Count == 0;
    /// <summary>Скільки рядків було зліва й справа, коли рахували порівняння (щоб не застосувати застаріле).</summary>
    public int LeftLineCount { get; init; }
    public int RightLineCount { get; init; }
}

/// <summary>
/// Порядкове порівняння текстів алгоритмом Майєрса (O((N+M)·D), той самий підхід, що в git diff):
/// мінімальна кількість вставок/видалень, з точним спільним початком і кінцем.
/// </summary>
public static class TextDiff
{
    /// <summary>Скільки відмінних рядків алгоритм шукає точно; далі — наближення, щоб не з’їсти пам’ять.</summary>
    public const int MaxEditDistance = 4000;

    internal enum Op : byte { Equal, Delete, Insert }

    public static TextDiffResult Compare(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions options, CancellationToken ct = default)
    {
        // Рядки → числа: однакові (з урахуванням опцій) рядки отримують однаковий номер.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] Encode(IReadOnlyList<string> lines)
        {
            var r = new int[lines.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var key = Key(lines[i], options);
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                r[i] = id;
            }
            return r;
        }
        var a = Encode(left);
        var b = Encode(right);
        var script = Script(a, b, ct, out bool approximate);
        return BuildRows(script, left, right, approximate);
    }

    internal static string Key(string line, DiffOptions o)
    {
        if (o.IgnoreWhitespace)
        {
            var sb = new StringBuilder(line.Length);
            bool space = false;
            foreach (var ch in line)
            {
                if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            line = sb.ToString();
        }
        return o.IgnoreCase ? line.ToUpperInvariant() : line;
    }

    /// <summary>Сценарій редагування a → b: послідовність Equal / Delete / Insert.</summary>
    internal static List<Op> Script(int[] a, int[] b, CancellationToken ct, out bool approximate)
    {
        var (pre, suf) = CommonEnds<int>(a, b);
        var ops = new List<Op>(a.Length + b.Length);
        for (int i = 0; i < pre; i++) ops.Add(Op.Equal);
        ops.AddRange(Myers(a, pre, a.Length - suf, b, pre, b.Length - suf, ct, out approximate));
        for (int i = 0; i < suf; i++) ops.Add(Op.Equal);
        return ops;
    }

    static List<Op> Myers(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, CancellationToken ct, out bool approximate)
    {
        approximate = false;
        int n = aHi - aLo, m = bHi - bLo;
        var ops = new List<Op>(n + m);
        if (n == 0 || m == 0)
        {
            for (int i = 0; i < n; i++) ops.Add(Op.Delete);
            for (int j = 0; j < m; j++) ops.Add(Op.Insert);
            return ops;
        }

        int max = n + m;
        int off = max + 1;
        var v = new int[2 * max + 3];               // v[k] — найдальший x на діагоналі k
        var trace = new List<int[]>();               // знімки v перед кожним кроком d (лише k ∈ [-d, d])
        int limit = Math.Min(max, MaxEditDistance);
        for (int d = 0; d <= limit; d++)
        {
            if ((d & 63) == 0) ct.ThrowIfCancellationRequested();
            var snap = new int[2 * d + 1];
            Array.Copy(v, off - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[aLo + x] == b[bLo + y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, d, n, m);
            }
        }

        // Забагато відмінностей — наближення: блок заміни.
        approximate = true;
        for (int i = 0; i < n; i++) ops.Add(Op.Delete);
        for (int j = 0; j < m; j++) ops.Add(Op.Insert);
        return ops;
    }

    static List<Op> Backtrack(List<int[]> trace, int dEnd, int n, int m)
    {
        var ops = new List<Op>(n + m);
        int x = n, y = m;
        for (int d = dEnd; d > 0; d--)
        {
            var snap = trace[d];                     // стан після кроку d-1
            int Get(int k) => snap[k + d];
            int k = x - y;
            bool down = k == -d || (k != d && Get(k - 1) < Get(k + 1));
            int prevK = down ? k + 1 : k - 1;
            int prevX = Get(prevK);
            int prevY = prevX - prevK;
            int startX = down ? prevX : prevX + 1;
            while (x > startX) { ops.Add(Op.Equal); x--; y--; }
            ops.Add(down ? Op.Insert : Op.Delete);
            x = prevX;
            y = prevY;
        }
        while (x > 0) { ops.Add(Op.Equal); x--; y--; }
        ops.Reverse();
        return ops;
    }

    static TextDiffResult BuildRows(List<Op> ops, IReadOnlyList<string> left, IReadOnlyList<string> right, bool approximate)
    {
        var rows = new List<DiffRow>(Math.Max(left.Count, right.Count));
        var hunks = new List<int>();
        int i = 0, j = 0, p = 0, changed = 0, deleted = 0, inserted = 0;
        var dels = new List<int>();
        var ins = new List<int>();
        while (p < ops.Count)
        {
            if (ops[p] == Op.Equal)
            {
                rows.Add(new DiffRow { Kind = DiffKind.Equal, LeftLine = i, RightLine = j, LeftText = left[i], RightText = right[j] });
                i++; j++; p++;
                continue;
            }

            // Блок відмінностей: усі видалення й вставки до наступного однакового рядка.
            dels.Clear();
            ins.Clear();
            while (p < ops.Count && ops[p] != Op.Equal)
            {
                if (ops[p] == Op.Delete) dels.Add(i++);
                else ins.Add(j++);
                p++;
            }
            int hunk = hunks.Count;
            hunks.Add(rows.Count);
            int pairs = Math.Min(dels.Count, ins.Count);
            for (int t = 0; t < pairs; t++)
            {
                var (hl, hr) = InlineDifference(left[dels[t]], right[ins[t]]);
                rows.Add(new DiffRow
                {
                    Kind = DiffKind.Changed, LeftLine = dels[t], RightLine = ins[t],
                    LeftText = left[dels[t]], RightText = right[ins[t]],
                    HighlightLeft = hl, HighlightRight = hr,
                    Hunk = hunk,
                });
                changed++;
            }
            for (int t = pairs; t < dels.Count; t++)
            {
                rows.Add(new DiffRow { Kind = DiffKind.Deleted, LeftLine = dels[t], LeftText = left[dels[t]], Hunk = hunk });
                deleted++;
            }
            for (int t = pairs; t < ins.Count; t++)
            {
                rows.Add(new DiffRow { Kind = DiffKind.Inserted, RightLine = ins[t], RightText = right[ins[t]], Hunk = hunk });
                inserted++;
            }
        }
        return new TextDiffResult
        {
            Rows = rows, HunkStarts = hunks, ChangedLines = changed, DeletedLines = deleted, InsertedLines = inserted,
            Approximate = approximate, LeftLineCount = left.Count, RightLineCount = right.Count,
        };
    }

    /// <summary>
    /// Довжини спільного початку й спільного кінця двох послідовностей (кінець не заходить на початок):
    /// усе, що між ними, — змінена середина.
    /// </summary>
    internal static (int Prefix, int Suffix) CommonEnds<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : IEquatable<T>
    {
        int pre = a.CommonPrefixLength(b);
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf].Equals(b[b.Length - 1 - suf])) suf++;
        return (pre, suf);
    }

    /// <summary>Змінена середина рядка: усе між спільним початком і спільним кінцем.</summary>
    internal static (TextSpan Left, TextSpan Right) InlineDifference(string l, string r)
    {
        var (pre, suf) = CommonEnds<char>(l, r);
        return (new TextSpan(pre, l.Length - pre - suf), new TextSpan(pre, r.Length - pre - suf));
    }

    // =====================================================================
    // Перенесення блоку
    // =====================================================================

    /// <summary>
    /// Перенести блок відмінностей <paramref name="hunk"/> в інший бік: рядки блоку з боку-джерела замінюють
    /// рядки цього блоку з боку-цілі (якщо там їх немає — вставляються перед наступним спільним рядком).
    /// Перенесені рядки отримують переважний перенос рядка цілі; решта рядків цілі зберігає свої переноси,
    /// а файл — свій стан «кінцевий перенос рядка». Порожня ціль переймає переноси й кінцевий перенос джерела.
    /// </summary>
    /// <exception cref="InvalidOperationException">Порівняння пораховане для інших рядків (застаріле).</exception>
    public static void CopyHunk(TextDiffResult diff, int hunk, TextLines left, TextLines right, bool toRight)
    {
        if (diff.LeftLineCount != left.Count || diff.RightLineCount != right.Count)
            throw new InvalidOperationException("Порівняння застаріле — рядки вже змінено.");
        if (hunk < 0 || hunk >= diff.HunkStarts.Count) throw new ArgumentOutOfRangeException(nameof(hunk));

        var (from, to) = toRight ? (left, right) : (right, left);
        int? Src(DiffRow r) => toRight ? r.LeftLine : r.RightLine;
        int? Dst(DiffRow r) => toRight ? r.RightLine : r.LeftLine;

        int first = diff.HunkStarts[hunk], end = first;
        while (end < diff.Rows.Count && diff.Rows[end].Hunk == hunk) end++;
        var src = new List<int>();
        var dst = new List<int>();
        for (int i = first; i < end; i++)
        {
            if (Src(diff.Rows[i]) is int s) src.Add(s);
            if (Dst(diff.Rows[i]) is int d) dst.Add(d);
        }
        // З боку-цілі блоку немає — вставляємо перед наступним спільним рядком (або в кінець).
        int start = dst.Count > 0 ? dst.Min() : end < diff.Rows.Count ? Dst(diff.Rows[end]) ?? to.Count : to.Count;

        bool targetWasEmpty = to.Count == 0;
        string ending = to.PredominantEnding ?? from.PredominantEnding ?? TextLines.CrLf;
        bool trailing = targetWasEmpty ? from.TrailingNewLine : to.TrailingNewLine;
        string lastEnding = !targetWasEmpty && to.TrailingNewLine ? to.Endings[^1] : ending;

        to.Lines.RemoveRange(start, dst.Count);
        to.Endings.RemoveRange(start, dst.Count);
        to.Lines.InsertRange(start, src.Select(i => from.Lines[i]));
        to.Endings.InsertRange(start, Enumerable.Repeat(ending, src.Count));

        int n = to.Count;
        if (n == 0) return;
        // Рядок перед вставкою був останнім (без переносу), а тепер за ним є інші — йому потрібен перенос.
        if (start > 0 && start - 1 < n - 1 && to.Endings[start - 1].Length == 0) to.Endings[start - 1] = ending;
        // Останній рядок: стан «кінцевий перенос» файлу не змінюється від перенесення блоку.
        if (!trailing) to.Endings[n - 1] = "";
        else if (start + src.Count == n) to.Endings[n - 1] = lastEnding;
    }

    // =====================================================================
    // Відмінності формату при однаковому вмісті
    // =====================================================================

    /// <summary>
    /// Чим відрізняються файли з однаковими рядками: кодування, BOM, переноси рядків, кінцевий перенос.
    /// Порожній список — формат той самий. Рядки мають бути однакові (порівняння без «ігнорувати…»).
    /// </summary>
    public static List<string> FormatDifferences(Encoding leftEncoding, TextLines left, Encoding rightEncoding, TextLines right)
    {
        static string Only(bool onLeft) => onLeft ? "є лише зліва" : "є лише справа";
        var parts = new List<string>();
        if (leftEncoding.CodePage != rightEncoding.CodePage)
            parts.Add($"кодування ({TextEncodings.NameOf(leftEncoding)} ↔ {TextEncodings.NameOf(rightEncoding)})");
        bool leftBom = leftEncoding.GetPreamble().Length > 0, rightBom = rightEncoding.GetPreamble().Length > 0;
        if (leftBom != rightBom) parts.Add($"BOM ({Only(leftBom)})");

        int n = Math.Min(left.Count, right.Count);
        bool endingsDiffer = false;
        for (int i = 0; i < n && !endingsDiffer; i++)
        {
            string l = left.Endings[i], r = right.Endings[i];
            // Відсутній перенос в останньому рядку — це «кінцевий перенос», а не інший тип переносу.
            if (l.Length == 0 || r.Length == 0) continue;
            endingsDiffer = l != r;
        }
        if (endingsDiffer)
        {
            string? ls = left.EndingsSummary, rs = right.EndingsSummary;
            parts.Add(ls != rs ? $"переноси рядків ({ls} ↔ {rs})" : $"переноси рядків (по-різному {ls})");
        }
        if (left.TrailingNewLine != right.TrailingNewLine)
            parts.Add($"кінцевий перенос рядка ({Only(left.TrailingNewLine)})");
        return parts;
    }
}
