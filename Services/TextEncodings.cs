using System.Text;

namespace ByteCommander.Services;

/// <summary>
/// Усе про кодування текстових файлів в одному місці: перелік кодувань для меню, назви,
/// розпізнавання BOM, евристика «UTF-8 чи Windows-1251», читання й запис тексту у файл.
/// </summary>
public static class TextEncodings
{
    // Кодові сторінки (1251, 866, KOI8-U, OEM) у .NET доступні лише після реєстрації провайдера.
    // Реєструємо тут, щоб модуль не залежав від того, чи зробив це код запуску (тести, інші точки входу).
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Кодування за номером кодової сторінки (зокрема однобайтові: 1251, 866, 21866, OEM).</summary>
    public static Encoding CodePage(int codePage) => Encoding.GetEncoding(codePage);

    public sealed record Choice(string Name, Func<Encoding> Create)
    {
        /// <summary>Це кодування (з урахуванням BOM): так меню знає, який пункт позначити.</summary>
        public bool Matches(Encoding e)
        {
            var mine = Create();
            return mine.CodePage == e.CodePage && mine.GetPreamble().Length == e.GetPreamble().Length;
        }
    }

    const int Utf16BigEndianCodePage = 1201;

    /// <summary>Кодування, які знає переглядач (у цьому порядку в меню). Єдине джерело назв.</summary>
    public static readonly IReadOnlyList<Choice> Choices = new Choice[]
    {
        new("UTF-8", () => new UTF8Encoding(false)),
        new("UTF-8 BOM", () => new UTF8Encoding(true)),
        new("Windows-1251", () => CodePage(1251)),
        new("DOS-866", () => CodePage(866)),
        new("KOI8-U", () => CodePage(21866)),
        new("UTF-16 LE", () => new UnicodeEncoding(false, true)),
        new("UTF-16 BE", () => new UnicodeEncoding(true, true)),
    };

    /// <summary>
    /// Кирилічні кодування без BOM, у яких варто шукати текст, якщо він не знайшовся як UTF-8
    /// (типові для старих файлів у Windows і DOS).
    /// </summary>
    public static IReadOnlyList<Encoding> LegacyCyrillic => legacy.Value;
    static readonly Lazy<Encoding[]> legacy = new(() => new[] { CodePage(1251), CodePage(866) });

    public static bool IsUtf16(Encoding e) => e is UnicodeEncoding;
    public static bool IsUtf16BigEndian(Encoding e) => e.CodePage == Utf16BigEndianCodePage;

    public static string NameOf(Encoding e) => Choices.FirstOrDefault(c => c.Matches(e))?.Name ?? e.WebName;

    public static bool HasUtf8Bom(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;

    /// <summary>UTF-16 BOM: LE (FF FE) чи BE (FE FF); null — немає.</summary>
    public static bool? Utf16BomBigEndian(ReadOnlySpan<byte> head) =>
        head.Length < 2 ? null
        : head[0] == 0xFF && head[1] == 0xFE ? false
        : head[0] == 0xFE && head[1] == 0xFF ? true
        : null;

    /// <summary>Чи починається файл з UTF-8 BOM (читає лише перші 3 байти).</summary>
    public static bool FileHasUtf8Bom(string path)
    {
        Span<byte> b = stackalloc byte[3];
        using var fs = Util.OpenShared(path);
        return HasUtf8Bom(b[..fs.ReadAtLeast(b, b.Length, throwOnEndOfStream: false)]);
    }

    /// <summary>
    /// Скільки байтів на початку — BOM, який треба пропустити при читанні цим кодуванням.
    /// UTF-8 BOM пропускається навіть для UTF-8 без BOM (щоб у тексті не з’являвся U+FEFF).
    /// </summary>
    public static int PreambleLength(ReadOnlySpan<byte> head, Encoding encoding)
    {
        var pre = encoding.GetPreamble();
        if (pre.Length > 0 && head.Length >= pre.Length && head[..pre.Length].SequenceEqual(pre)) return pre.Length;
        if (encoding is UTF8Encoding && HasUtf8Bom(head)) return 3;
        return 0;
    }

    /// <summary>Кодування за BOM; без BOM — UTF-8, якщо байти валідні як UTF-8, інакше Windows-1251.</summary>
    /// <param name="truncated">Фрагмент обрізано — останній UTF-8 символ може бути неповним.</param>
    public static Encoding Detect(ReadOnlySpan<byte> head, bool truncated)
    {
        if (HasUtf8Bom(head)) return new UTF8Encoding(true);
        if (Utf16BomBigEndian(head) is bool bigEndian) return new UnicodeEncoding(bigEndian, true);
        try
        {
            int len = truncated ? Math.Max(0, head.Length - 4) : head.Length;
            new UTF8Encoding(false, true).GetCharCount(head[..len]);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return CodePage(1251);
        }
    }

    /// <summary>Двійковий файл: є нульові байти в перших 8 КБ (UTF-16 з BOM — не двійковий).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (Utf16BomBigEndian(head) != null) return false;
        return head[..Math.Min(head.Length, 8192)].IndexOf((byte)0) >= 0;
    }

    /// <summary>Рядки так, як їх показує переглядач: кількість LF, плюс останній рядок без LF (якщо є).</summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        int n = 0;
        foreach (var c in text) if (c == '\n') n++;
        return text[^1] == '\n' ? n : n + 1;
    }

    // =====================================================================
    // Читання й запис тексту (спільне для переглядача / редактора й порівняння)
    // =====================================================================

    /// <summary>Байти → текст у заданому кодуванні (BOM на початку пропускається).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        int skip = PreambleLength(bytes, encoding);
        return encoding.GetString(bytes[skip..]);
    }

    /// <summary>Байти всього файлу → текст і розпізнане кодування (з BOM чи без — так, як у файлі).</summary>
    public static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        var enc = Detect(bytes, truncated: false);
        return (Decode(bytes, enc), enc);
    }

    /// <summary>Текст → байти файлу: BOM кодування (якщо воно його має) і сам текст.</summary>
    public static byte[] Encode(string text, Encoding encoding)
    {
        var pre = encoding.GetPreamble();
        var bytes = new byte[pre.Length + encoding.GetByteCount(text)];
        pre.CopyTo(bytes, 0);
        encoding.GetBytes(text, 0, text.Length, bytes, pre.Length);
        return bytes;
    }

    /// <summary>
    /// Записати текст у файл у заданому кодуванні (з його BOM) через тимчасовий файл: збій посеред запису
    /// не знищить оригінал, а атрибути, дата створення, права й жорсткі посилання наявного файлу зберігаються.
    /// </summary>
    /// <returns>Розмір записаного файлу в байтах.</returns>
    public static long WriteFile(string path, string text, Encoding encoding)
    {
        var bytes = Encode(text, encoding);
        Util.WriteViaTemp(path, tmp =>
        {
            using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write);
            fs.Write(bytes);
        }, keepDestinationMetadata: true);
        return bytes.Length;
    }

    /// <summary>
    /// Перший символ тексту, якого немає в кодуванні (без «?» на заміну); null — усі символи представлені.
    /// Для пари сурогатів повертаються обидві половини (емодзі, рідкісні ієрогліфи).
    /// </summary>
    public static string? FirstUnencodable(string text, Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        try
        {
            strict.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            return ex.IsUnknownSurrogate() ? new string(new[] { ex.CharUnknownHigh, ex.CharUnknownLow }) : ex.CharUnknown.ToString();
        }
    }

    /// <summary>
    /// Кодування, у якому зберігати текст. Якщо всі символи є в поточному — воно ж. Інакше (символи стали б «?»)
    /// питаємо <paramref name="confirmUtf8"/> (аргумент — перший непредставний символ): згода — UTF-8
    /// (з BOM, якщо файл мав BOM), відмова — null, зберігати не треба.
    /// </summary>
    public static Encoding? EncodingForSave(string text, Encoding encoding, Func<string, bool> confirmUtf8)
    {
        // UTF-8 / UTF-16 представляють увесь Unicode; збій можливий лише на «розірваних» сурогатах — їх нема куди подіти.
        if (encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding) return encoding;
        var bad = FirstUnencodable(text, encoding);
        if (bad == null) return encoding;
        return confirmUtf8(bad) ? new UTF8Encoding(encoding.GetPreamble().Length > 0) : null;
    }
}

/// <summary>
/// Текст, розбитий на рядки, з власним закінченням кожного рядка (CRLF, LF чи CR; "" — останній рядок без переносу).
/// Зібраний назад (<see cref="Join"/>) дає рівно вихідний текст — змішані переноси й кінцевий перенос зберігаються.
/// </summary>
public sealed class TextLines
{
    public const string CrLf = "\r\n", Lf = "\n", Cr = "\r";

    public List<string> Lines { get; } = new();
    /// <summary>Закінчення кожного рядка (паралельно до <see cref="Lines"/>).</summary>
    public List<string> Endings { get; } = new();

    public int Count => Lines.Count;

    /// <summary>Файл закінчується переносом рядка (порожній файл — ні).</summary>
    public bool TrailingNewLine => Count > 0 && Endings[^1].Length > 0;

    public static TextLines Parse(string text)
    {
        var t = new TextLines();
        int start = 0;
        while (start < text.Length)
        {
            int rel = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (rel < 0) break;
            int i = start + rel;
            string ending = text[i] == '\n' ? Lf : i + 1 < text.Length && text[i + 1] == '\n' ? CrLf : Cr;
            t.Lines.Add(text[start..i]);
            t.Endings.Add(ending);
            start = i + ending.Length;
        }
        if (start < text.Length)
        {
            t.Lines.Add(text[start..]);
            t.Endings.Add("");
        }
        return t;
    }

    public string Join()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Count; i++) sb.Append(Lines[i]).Append(Endings[i]);
        return sb.ToString();
    }

    /// <summary>Найчастіше закінчення рядка у файлі (за рівності — CRLF, LF, CR); null — переносів немає.</summary>
    public string? PredominantEnding
    {
        get
        {
            int crlf = 0, lf = 0, cr = 0;
            foreach (var e in Endings)
            {
                if (e == CrLf) crlf++;
                else if (e == Lf) lf++;
                else if (e == Cr) cr++;
            }
            if (crlf + lf + cr == 0) return null;
            return crlf >= lf && crlf >= cr ? CrLf : lf >= cr ? Lf : Cr;
        }
    }

    /// <summary>Які переноси у файлі: «CRLF», «LF», «CR», «змішані»; null — переносів немає.</summary>
    public string? EndingsSummary
    {
        get
        {
            var kinds = Endings.Where(e => e.Length > 0).Distinct().Take(2).ToList();
            return kinds.Count switch { 0 => null, 1 => NameOf(kinds[0]), _ => "змішані" };
        }
    }

    public static string NameOf(string ending) => ending switch { CrLf => "CRLF", Lf => "LF", Cr => "CR", _ => "—" };
}
