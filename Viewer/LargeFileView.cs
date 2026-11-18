using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ByteCommander.Viewer;

/// <summary>
/// Віртуальний перегляд файлу: на екран малюються лише видимі рядки, які <see cref="IRowSource"/> читає з диска.
/// Розмір файлу не обмежений; моноширинний шрифт дозволяє обрізати рядки по колонках при горизонтальній прокрутці.
/// </summary>
public sealed class LargeFileView : Grid
{
    readonly RowsPresenter presenter;
    readonly ScrollBar vbar = new() { Orientation = Orientation.Vertical, Minimum = 0, SmallChange = 1 };
    readonly ScrollBar hbar = new() { Orientation = Orientation.Horizontal, Minimum = 0, SmallChange = 1 };
    IRowSource? source;
    long topRow;
    int leftCol;
    int maxCols = 1;
    TextMatch? match;

    /// <summary>Змінилась кількість рядків / стан індексації — для рядка стану.</summary>
    public event Action? StatusChanged;

    public LargeFileView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        presenter = new RowsPresenter(this);
        Children.Add(presenter);
        SetColumn(vbar, 1);
        Children.Add(vbar);
        SetRow(hbar, 1);
        Children.Add(hbar);

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        vbar.ValueChanged += (_, e) => { topRow = (long)e.NewValue; presenter.InvalidateVisual(); };
        hbar.ValueChanged += (_, e) => { leftCol = (int)e.NewValue; presenter.InvalidateVisual(); };
        SizeChanged += (_, _) => UpdateScrollBars();
        MouseDown += (_, _) => Focus();
        // Рядки малюються вручну (OnRender) — при зміні теми перемальовуємо самі.
        Loaded += (_, _) => Services.ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => Services.ThemeManager.Changed -= OnThemeChanged;
    }

    void OnThemeChanged() => presenter.InvalidateVisual();

    public IRowSource? Source => source;
    public TextMatch? Match => match;
    public long TopRow => topRow;

    public void SetSource(IRowSource? newSource)
    {
        if (source != null) source.RowCountChanged -= OnRowCountChanged;
        source?.Dispose();
        source = newSource;
        topRow = 0;
        leftCol = 0;
        maxCols = 1;
        match = null;
        if (source != null) source.RowCountChanged += OnRowCountChanged;
        vbar.Value = 0;
        hbar.Value = 0;
        UpdateScrollBars();
        presenter.InvalidateVisual();
        StatusChanged?.Invoke();
    }

    void OnRowCountChanged() => Dispatcher.InvokeAsync(() =>
    {
        UpdateScrollBars();
        presenter.InvalidateVisual();
        StatusChanged?.Invoke();
    });

    internal int VisibleRows => Math.Max(1, (int)(presenter.ActualHeight / presenter.LineHeight));
    internal int VisibleCols => Math.Max(1, (int)((presenter.ActualWidth - presenter.GutterWidth) / presenter.CharWidth));

    void UpdateScrollBars()
    {
        long rows = source?.RowCount ?? 0;
        int vis = VisibleRows;
        vbar.Maximum = Math.Max(0, rows - vis + 1);
        vbar.ViewportSize = vis;
        vbar.LargeChange = Math.Max(1, vis - 1);
        vbar.Visibility = rows > vis ? Visibility.Visible : Visibility.Collapsed;
        int cols = VisibleCols;
        hbar.Maximum = Math.Max(0, maxCols - cols + 2);
        hbar.ViewportSize = cols;
        hbar.LargeChange = Math.Max(1, cols - 4);
        hbar.Visibility = maxCols > cols ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ReportWidth(int cols)
    {
        if (cols <= maxCols) return;
        maxCols = cols;
        Dispatcher.InvokeAsync(UpdateScrollBars);
    }

    public void ScrollTo(long row, int? column = null)
    {
        long rows = source?.RowCount ?? 0;
        vbar.Value = Math.Clamp(row, 0, Math.Max(0, rows - VisibleRows + 1));
        if (column is int c) hbar.Value = Math.Clamp(c, 0, hbar.Maximum);
    }

    /// <summary>Показати збіг: прокрутити так, щоб він був на екрані, і підсвітити.</summary>
    public void ShowMatch(TextMatch m)
    {
        match = m;
        int vis = VisibleRows;
        if (m.Row < topRow || m.Row >= topRow + vis - 1) ScrollTo(Math.Max(0, m.Row - 3));
        var cols = source!.GetHighlights(m.Row, m).ToList();
        if (cols.Count > 0)
        {
            int first = cols.Min(c => c.Column), last = cols.Max(c => c.Column + c.Length);
            ReportWidth(last + 1);
            if (first < leftCol || last > leftCol + VisibleCols) hbar.Value = Math.Max(0, first - 10);
        }
        presenter.InvalidateVisual();
    }

    public void ClearMatch()
    {
        match = null;
        presenter.InvalidateVisual();
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        int lines = e.Delta / 120 * 3;
        if (Keyboard.Modifiers == ModifierKeys.Shift) hbar.Value = Math.Clamp(hbar.Value - lines * 4, 0, hbar.Maximum);
        else vbar.Value = Math.Clamp(vbar.Value - lines, 0, vbar.Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        int vis = VisibleRows;
        switch (e.Key)
        {
            case Key.Down: vbar.Value = Math.Min(vbar.Maximum, vbar.Value + 1); break;
            case Key.Up: vbar.Value = Math.Max(0, vbar.Value - 1); break;
            case Key.PageDown: vbar.Value = Math.Min(vbar.Maximum, vbar.Value + vis - 1); break;
            case Key.PageUp: vbar.Value = Math.Max(0, vbar.Value - vis + 1); break;
            case Key.Home when ctrl: vbar.Value = 0; hbar.Value = 0; break;
            case Key.End when ctrl: vbar.Value = vbar.Maximum; break;
            case Key.Home: hbar.Value = 0; break;
            case Key.End: hbar.Value = hbar.Maximum; break;
            case Key.Right: hbar.Value = Math.Min(hbar.Maximum, hbar.Value + (ctrl ? 16 : 1)); break;
            case Key.Left: hbar.Value = Math.Max(0, hbar.Value - (ctrl ? 16 : 1)); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Малює видимі рядки. Окремий елемент, щоб скролбари лишались звичайними контролами Grid.</summary>
    sealed class RowsPresenter : FrameworkElement
    {
        readonly LargeFileView owner;
        readonly Typeface typeface;
        const double FontSize = 13;

        public RowsPresenter(LargeFileView owner)
        {
            this.owner = owner;
            var family = (FontFamily?)Application.Current?.TryFindResource("MonoFont") ?? new FontFamily("Consolas");
            typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var probe = Text("M", Brushes.Black);
            CharWidth = probe.WidthIncludingTrailingWhitespace;
            LineHeight = Math.Ceiling(probe.Height + 2);
            ClipToBounds = true;
        }

        public double CharWidth { get; }
        public double LineHeight { get; }
        public double GutterWidth { get; private set; } = 8;

        FormattedText Text(string s, Brush brush) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

        Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Res(ThemeKeys.InputBrush), null, new Rect(RenderSize));
            var src = owner.source;
            if (src == null) return;

            int visible = owner.VisibleRows + 1;
            long top = owner.topRow;
            var rows = src.GetRows(top, visible);
            var fg = Res(ThemeKeys.TextBrush);
            var dim = Res(ThemeKeys.TextDimBrush);
            var hl = Res(ThemeKeys.SelectionBrush);

            if (src.ShowLineNumbers)
            {
                int digits = Math.Max(3, (top + visible).ToString().Length);
                GutterWidth = (digits + 2) * CharWidth;
                dc.DrawRectangle(Res(ThemeKeys.PanelBrush), null, new Rect(0, 0, GutterWidth - CharWidth / 2, RenderSize.Height));
            }
            else GutterWidth = CharWidth;

            int left = owner.leftCol;
            int cols = owner.VisibleCols + 1;
            int widest = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                double y = i * LineHeight;
                long row = top + i;
                var line = rows[i];
                widest = Math.Max(widest, line.Length);

                if (src.ShowLineNumbers)
                {
                    var num = Text((row + 1).ToString(), dim);
                    dc.DrawText(num, new Point(GutterWidth - CharWidth - num.Width, y));
                }
                if (owner.match is { } m)
                {
                    foreach (var (col, len) in src.GetHighlights(row, m))
                    {
                        double x = GutterWidth + (col - left) * CharWidth;
                        if (x + len * CharWidth > GutterWidth)
                            dc.DrawRectangle(hl, null, new Rect(Math.Max(GutterWidth, x), y, len * CharWidth, LineHeight));
                    }
                }
                if (left < line.Length)
                    dc.DrawText(Text(line.Substring(left, Math.Min(cols, line.Length - left)), fg), new Point(GutterWidth, y + 1));
            }
            owner.ReportWidth(widest);
        }
    }
}
