using System.Text;
using System.Windows;
using System.Windows.Input;
using ByteCommander.Services;

namespace ByteCommander.Compare;

/// <summary>
/// Порівняння двох файлів поруч (як у Beyond Compare): рядки вирівняні, відмінності підсвічені,
/// змінена частина рядка виділена; перехід між відмінностями, перенесення блоку в інший бік і збереження.
/// </summary>
public partial class TextCompareWindow : Window
{
    /// <summary>Більші файли порівнюються лише побайтно (без порядкового вигляду).</summary>
    const long MaxTextBytes = 64L * 1024 * 1024;

    public static readonly DependencyProperty HOffsetProperty =
        DependencyProperty.Register(nameof(HOffset), typeof(double), typeof(TextCompareWindow), new PropertyMetadata(0.0));

    /// <summary>Горизонтальний зсув тексту в пікселях — спільний для обох сторін.</summary>
    public double HOffset { get => (double)GetValue(HOffsetProperty); set => SetValue(HOffsetProperty, value); }

    /// <summary>Одна сторона порівняння: файл, його рядки й формат, щоб зберегти назад так само.</summary>
    sealed class Side
    {
        public required string Path { get; set; }
        public List<string> Lines { get; set; } = new();
        public Encoding Encoding { get; set; } = new UTF8Encoding(false);
        public string NewLine { get; set; } = "\r\n";
        public bool TrailingNewLine { get; set; }
        public bool Dirty { get; set; }

        public static Side Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var enc = TextEncodings.Detect(bytes, truncated: false);
            int skip = TextEncodings.PreambleLength(bytes, enc);
            var text = enc.GetString(bytes, skip, bytes.Length - skip);
            var side = new Side { Path = path, Encoding = enc, NewLine = text.Contains("\r\n") ? "\r\n" : "\n" };
            var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0 && text.Length > 0)
            {
                lines.RemoveAt(lines.Count - 1);
                side.TrailingNewLine = true;
            }
            if (text.Length == 0) lines.Clear();
            side.Lines = lines;
            return side;
        }

        public void Save()
        {
            var text = string.Join(NewLine, Lines) + (TrailingNewLine && Lines.Count > 0 ? NewLine : "");
            var pre = Encoding.GetPreamble();
            var body = Encoding.GetBytes(text);
            Util.WriteViaTemp(Path, tmp =>
            {
                using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write);
                fs.Write(pre);
                fs.Write(body);
            }, keepDestinationMetadata: true);
            Dirty = false;
        }
    }

    Side left, right;
    TextDiffResult? diff;
    List<DiffRow> shown = new();
    CancellationTokenSource? computeCts;
    bool binary;

    public TextCompareWindow(string leftPath, string rightPath)
    {
        InitializeComponent();
        left = new Side { Path = leftPath };
        right = new Side { Path = rightPath };
        Loaded += async (_, _) => await LoadAsync();
        SizeChanged += (_, _) => UpdateHBar();
    }

    // =====================================================================
    // Завантаження й порівняння
    // =====================================================================

    async Task LoadAsync()
    {
        StatusText.Text = "Завантаження…";
        UpdateTitle();
        try
        {
            var lp = left.Path;
            var rp = right.Path;
            binary = await Task.Run(() => IsBinaryOrHuge(lp) || IsBinaryOrHuge(rp));
            if (binary)
            {
                await ShowBinaryAsync();
                return;
            }
            (left, right) = await Task.Run(() => (Side.Load(lp), Side.Load(rp)));
            BinaryPanel.Visibility = Visibility.Collapsed;
            TextPanel.Visibility = Visibility.Visible;
            await RecomputeAsync(selectFirst: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Помилка: " + ex.Message;
        }
    }

    static bool IsBinaryOrHuge(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length > MaxTextBytes) return true;
        using var fs = Util.OpenShared(path);
        var head = new byte[(int)Math.Min(fi.Length, 8192)];
        int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return TextEncodings.LooksBinary(head.AsSpan(0, n));
    }

    async Task ShowBinaryAsync()
    {
        TextPanel.Visibility = Visibility.Collapsed;
        BinaryPanel.Visibility = Visibility.Visible;
        HBar.Visibility = Visibility.Collapsed;
        BinaryText.Text = "Порівняння…";
        var lp = left.Path;
        var rp = right.Path;
        long? at = await Task.Run(() => FolderComparer.FirstDifference(lp, rp));
        long ls = new FileInfo(lp).Length, rs = new FileInfo(rp).Length;
        var sizes = $"Розміри: {Util.FormatSize(ls)} ({ls:N0} Б) / {Util.FormatSize(rs)} ({rs:N0} Б)";
        BinaryText.Text = at is long off
            ? $"Файли відрізняються.\nПерша відмінність — байт {off:N0} (0x{off:X}).\n{sizes}"
            : $"Файли однакові (побайтно).\n{sizes}";
        StatusText.Text = "Двійкові або дуже великі файли — порівнюються побайтно.";
        UpdateHeaders();
    }

    DiffOptions Options => new(IgnoreWhitespaceBox.IsChecked == true, IgnoreCaseBox.IsChecked == true);

    async Task RecomputeAsync(bool selectFirst = false, int? keepHunk = null)
    {
        computeCts?.Cancel();
        var cts = computeCts = new CancellationTokenSource();
        StatusText.Text = "Порівняння…";
        var (l, r, o) = (left.Lines, right.Lines, Options);
        TextDiffResult result;
        try
        {
            result = await Task.Run(() => TextDiff.Compare(l, r, o, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != computeCts) return;
        diff = result;
        ApplyView();
        UpdateHeaders();
        UpdateStatus();
        if (keepHunk is int h && diff.HunkStarts.Count > 0) GoToHunk(Math.Min(h, diff.HunkStarts.Count - 1));
        else if (selectFirst && diff.HunkStarts.Count > 0) GoToHunk(0);
        Rows.Focus();
    }

    void ApplyView()
    {
        if (diff == null) return;
        shown = OnlyDiffsBox.IsChecked == true ? diff.Rows.Where(r => r.Kind != DiffKind.Equal).ToList() : diff.Rows.ToList();
        Rows.ItemsSource = shown;
        UpdateHBar();
    }

    void UpdateHBar()
    {
        if (diff == null || binary) return;
        // Моноширинний шрифт: ширина найдовшого рядка ≈ символи × ширина символу.
        int longest = diff.Rows.Count == 0 ? 0 : diff.Rows.Max(r => Math.Max(r.LeftText.Length, r.RightText.Length));
        double charWidth = 7.8;
        double half = Math.Max(100, Rows.ActualWidth / 2 - 70);
        double max = Math.Max(0, longest * charWidth - half + 20);
        HBar.Maximum = max;
        HBar.ViewportSize = half;
        HBar.LargeChange = half * 0.8;
        HBar.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HBar.Value > max) HBar.Value = max;
    }

    void HBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => HOffset = e.NewValue;

    void UpdateTitle() =>
        Title = $"Порівняння: {Path.GetFileName(left.Path)} ↔ {Path.GetFileName(right.Path)}";

    void UpdateHeaders()
    {
        string Header(Side s) => (s.Dirty ? "● " : "") + s.Path + (binary ? "" : $"   ·   {TextEncodings.NameOf(s.Encoding)}");
        LeftHeader.Text = Header(left);
        RightHeader.Text = Header(right);
        UpdateTitle();
    }

    void UpdateStatus()
    {
        if (diff == null) return;
        if (diff.Identical)
        {
            bool optionsOn = IgnoreWhitespaceBox.IsChecked == true || IgnoreCaseBox.IsChecked == true;
            StatusText.Text = optionsOn ? "Відмінностей немає (з урахуванням вибраних «ігнорувати…»)" : "Файли однакові";
            return;
        }
        var parts = new List<string> { Util.Count(diff.HunkStarts.Count, "відмінність", "відмінності", "відмінностей") };
        if (diff.ChangedLines > 0) parts.Add($"змінено {Util.Count(diff.ChangedLines, "рядок", "рядки", "рядків")}");
        if (diff.DeletedLines > 0) parts.Add($"лише зліва {diff.DeletedLines}");
        if (diff.InsertedLines > 0) parts.Add($"лише справа {diff.InsertedLines}");
        if (diff.Approximate) parts.Add("відмінностей дуже багато — середину показано як одну заміну");
        int cur = CurrentHunk();
        StatusText.Text = (cur >= 0 ? $"{cur + 1} / {diff.HunkStarts.Count}  ·  " : "") + string.Join("  ·  ", parts)
                          + "    F8 / Shift+F8 — наступна / попередня,  Alt+→ / Alt+← — перенести блок";
    }

    // =====================================================================
    // Навігація
    // =====================================================================

    int CurrentHunk() => Rows.SelectedItem is DiffRow r ? r.Hunk : -1;

    static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    int IndexOfRow(DiffRow row)
    {
        if (diff == null) return -1;
        for (int i = 0; i < diff.Rows.Count; i++) if (ReferenceEquals(diff.Rows[i], row)) return i;
        return -1;
    }

    void GoToHunk(int hunk)
    {
        if (diff == null || hunk < 0 || hunk >= diff.HunkStarts.Count) return;
        var row = diff.Rows[diff.HunkStarts[hunk]];
        Rows.SelectedItem = row;
        // Показати блок ближче до верху, з кількома рядками контексту над ним (прокрутка — по рядках).
        int idx = shown.IndexOf(row);
        if (FindScrollViewer(Rows) is { } sv) sv.ScrollToVerticalOffset(Math.Max(0, idx - 5));
        else Rows.ScrollIntoView(row);
        UpdateStatus();
    }

    void Step(int dir)
    {
        if (diff == null || diff.HunkStarts.Count == 0) return;
        int cur = CurrentHunk();
        if (cur < 0)
        {
            // Курсор на однаковому рядку: найближчий блок нижче / вище за ним.
            int rowIdx = Rows.SelectedItem is DiffRow sel ? IndexOfRow(sel) : -1;
            var starts = diff.HunkStarts;
            int next = -1;
            if (dir > 0)
            {
                for (int i = 0; i < starts.Count && next < 0; i++) if (starts[i] > rowIdx) next = i;
            }
            else
            {
                for (int i = starts.Count - 1; i >= 0 && next < 0; i--) if (starts[i] < rowIdx) next = i;
            }
            if (next >= 0) GoToHunk(next);
            return;
        }
        GoToHunk(Math.Clamp(cur + dir, 0, diff.HunkStarts.Count - 1));
    }

    // =====================================================================
    // Перенесення блоку й збереження
    // =====================================================================

    async Task CopyBlockAsync(bool toRight)
    {
        if (diff == null || binary) return;
        int hunk = CurrentHunk();
        if (hunk < 0)
        {
            StatusText.Text = "Спершу перейдіть до відмінності (F8), яку треба перенести.";
            return;
        }
        var rows = diff.Rows.Where(r => r.Hunk == hunk).ToList();
        var (from, to) = toRight ? (left, right) : (right, left);
        int? Line(DiffRow r, bool fromSide) => (fromSide ? toRight : !toRight) ? r.LeftLine : r.RightLine;

        var newLines = rows.Where(r => Line(r, true) != null).Select(r => from.Lines[Line(r, true)!.Value]).ToList();
        var targetIdx = rows.Where(r => Line(r, false) != null).Select(r => Line(r, false)!.Value).ToList();
        int start;
        if (targetIdx.Count > 0) start = targetIdx.Min();
        else
        {
            // З цього боку блоку немає — вставляємо перед наступним спільним рядком.
            int after = IndexOfRow(rows[^1]) + 1;
            start = after < diff.Rows.Count ? Line(diff.Rows[after], false) ?? to.Lines.Count : to.Lines.Count;
        }
        to.Lines.RemoveRange(start, targetIdx.Count);
        to.Lines.InsertRange(start, newLines);
        to.Dirty = true;
        await RecomputeAsync(keepHunk: hunk);
    }

    bool SaveAll()
    {
        try
        {
            if (left.Dirty) left.Save();
            if (right.Dirty) right.Save();
            UpdateHeaders();
            StatusText.Text = $"Збережено о {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Не вдалося зберегти:\n" + ex.Message);
            return false;
        }
    }

    bool ConfirmDiscard()
    {
        if (!left.Dirty && !right.Dirty) return true;
        int r = Dialogs.Ask(this, "Незбережені зміни", "Зберегти змінені файли?", new[] { "Зберегти", "Не зберігати", "Скасувати" }, 0, 2);
        return r == 1 || (r == 0 && SaveAll());
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        else computeCts?.Cancel();
    }

    // =====================================================================
    // Клавіші й кнопки
    // =====================================================================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        Action? a = (key, m) switch
        {
            (Key.F8, ModifierKeys.None) or (Key.N, ModifierKeys.Control) or (Key.Down, ModifierKeys.Alt) => () => Step(1),
            (Key.F8, ModifierKeys.Shift) or (Key.P, ModifierKeys.Control) or (Key.Up, ModifierKeys.Alt) => () => Step(-1),
            (Key.Right, ModifierKeys.Alt) => () => _ = CopyBlockAsync(toRight: true),
            (Key.Left, ModifierKeys.Alt) => () => _ = CopyBlockAsync(toRight: false),
            (Key.S, ModifierKeys.Control) => () => SaveAll(),
            (Key.F5, ModifierKeys.None) => () => Reload_Click(this, new RoutedEventArgs()),
            (Key.Escape, ModifierKeys.None) => Close,
            _ => null,
        };
        if (a == null) return;
        e.Handled = true;
        a();
    }

    void Next_Click(object sender, RoutedEventArgs e) => Step(1);
    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    async void CopyRight_Click(object sender, RoutedEventArgs e) => await CopyBlockAsync(toRight: true);
    async void CopyLeft_Click(object sender, RoutedEventArgs e) => await CopyBlockAsync(toRight: false);
    void Save_Click(object sender, RoutedEventArgs e) => SaveAll();
    async void Options_Click(object sender, RoutedEventArgs e) => await RecomputeAsync(keepHunk: 0);

    void OnlyDiffs_Click(object sender, RoutedEventArgs e)
    {
        var sel = Rows.SelectedItem;
        ApplyView();
        if (sel != null && shown.Contains(sel)) { Rows.SelectedItem = sel; Rows.ScrollIntoView(sel); }
    }

    async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (left, right) = (right, left);
        if (binary) await ShowBinaryAsync();
        else await RecomputeAsync(selectFirst: true);
    }

    async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        await LoadAsync();
    }

    void Hex_Click(object sender, RoutedEventArgs e)
    {
        new ViewerWindow(left.Path, ViewerMode.View) { Owner = this }.Show();
        new ViewerWindow(right.Path, ViewerMode.View) { Owner = this }.Show();
    }
}
