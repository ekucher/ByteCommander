using System.Text;

namespace ByteCommander.Services;

/// <summary>
/// Усе про кодування текстових файлів в одному місці: перелік кодувань для меню, назви,
/// розпізнавання BOM і евристика «UTF-8 чи Windows-1251».
/// </summary>
public static class TextEncodings
{
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
        new("Windows-1251", () => Encoding.GetEncoding(1251)),
        new("DOS-866", () => Encoding.GetEncoding(866)),
        new("KOI8-U", () => Encoding.GetEncoding(21866)),
        new("UTF-16 LE", () => new UnicodeEncoding(false, true)),
        new("UTF-16 BE", () => new UnicodeEncoding(true, true)),
    };

    /// <summary>
    /// Кирилічні кодування без BOM, у яких варто шукати текст, якщо він не знайшовся як UTF-8
    /// (типові для старих файлів у Windows і DOS).
    /// </summary>
    public static IReadOnlyList<Encoding> LegacyCyrillic => legacy.Value;
    static readonly Lazy<Encoding[]> legacy = new(() => new[] { Encoding.GetEncoding(1251), Encoding.GetEncoding(866) });

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
            return Encoding.GetEncoding(1251);
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
}
