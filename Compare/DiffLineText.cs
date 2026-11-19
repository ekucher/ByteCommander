using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ByteCommander.Compare;

/// <summary>
/// Рядок порівняння: текст, у якому змінена частина підсвічена кольором теми, з горизонтальним зсувом
/// (спільна горизонтальна прокрутка лівої й правої половин).
/// </summary>
public sealed class DiffLineText : TextBlock
{
    public static readonly DependencyProperty LineProperty = Register(nameof(Line), typeof(string), "");
    public static readonly DependencyProperty HighlightProperty = Register(nameof(Highlight), typeof(TextSpan), default(TextSpan));
    public static readonly DependencyProperty OffsetProperty = Register(nameof(Offset), typeof(double), 0.0);

    static DependencyProperty Register(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(DiffLineText),
            new PropertyMetadata(def, (d, _) => ((DiffLineText)d).Rebuild()));

    public string Line { get => (string)GetValue(LineProperty); set => SetValue(LineProperty, value); }
    /// <summary>Змінена частина рядка, яку треба підсвітити.</summary>
    public TextSpan Highlight { get => (TextSpan)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public double Offset { get => (double)GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

    public DiffLineText()
    {
        TextWrapping = TextWrapping.NoWrap;
    }

    void Rebuild()
    {
        Margin = new Thickness(-Offset, 0, 0, 0);
        Inlines.Clear();
        var s = Line ?? "";
        var h = Highlight;
        int start = Math.Clamp(h.Start, 0, s.Length);
        int len = Math.Clamp(h.Length, 0, s.Length - start);
        if (len == 0)
        {
            Inlines.Add(new Run(s));
            return;
        }
        if (start > 0) Inlines.Add(new Run(s[..start]));
        var mid = new Run(s.Substring(start, len));
        mid.SetResourceReference(TextElement.BackgroundProperty, ThemeKeys.DiffChangedTextBrush);
        Inlines.Add(mid);
        if (start + len < s.Length) Inlines.Add(new Run(s[(start + len)..]));
    }
}
