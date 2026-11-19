using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>
    /// Одна сторона порівняння: файл, його рядки з власними переносами й кодування (з BOM чи без),
    /// щоб незмінений файл зберігся байт у байт так само.
    /// </summary>
    sealed class Side
    {
        public required string Path { get; set; }
        public TextLines Text { get; private set; } = new();
        public Encoding Encoding { get; set; } = new UTF8Encoding(false);
        /// <summary>Байти файлу, як їх прочитано (поки сторону не змінено) — для перевірки «файли однакові».</summary>
        byte[] original = Array.Empty<byte>();
        public bool Dirty { get; private set; }

        public static Side Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var (text, enc) = TextEncodings.Decode(bytes);
            return new Side { Path = path, Encoding = enc, Text = TextLines.Parse(text), original = bytes };
        }

        public void MarkDirty() => Dirty = true;

        /// <summary>Поточний вміст у байтах: незмінений — як на диску, змінений — як буде збережено.</summary>
        public byte[] CurrentBytes() => Dirty ? TextEncodings.Encode(Text.Join(), Encoding) : original;

        /// <summary>Зберегти; false — користувач скасував (символів немає в кодуванні файлу).</summary>
        public bool Save(Func<string, bool> confirmUtf8)
        {
            var text = Text.Join();
            if (TextEncodings.EncodingForSave(text, Encoding, confirmUtf8) is not { } enc) return false;
            TextEncodings.WriteFile(Path, text, enc);
            Encoding = enc;
            Dirty = false;
            original = TextEncodings.Encode(text, enc);
            return true;
        }
    }

    Side left, right;
    TextDiffResult? diff;
    List<DiffRow> shown = new();
    CancellationTokenSource? computeCts;
    bool binary;
    /// <summary>Перша побайтна відмінність двійкових файлів (для «Відкрити обидва в hex»).</summary>
    long? firstDifference;
    /// <summary>
    /// Лічильник змін рядків (перенесення блоку, перечитування, обмін сторін) і версія, для якої пораховано
    /// <see cref="diff"/>. Поки вони різні, порівняння застаріле — переносити блок за ним не можна.
    /// </summary>
    int version, diffVersion = -1;
    double? charWidth;

    public TextCompareWindow(string leftPath, string rightPath)
    {
        InitializeComponent();
        Util.ApplyWindowTheme(this);
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
            version++;
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
        firstDifference = at;
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
        // Знімки рядків: фонове порівняння не бачить змін, зроблених тим часом у вікні.
        var (l, r, o, v) = (left.Text.Lines.ToArray(), right.Text.Lines.ToArray(), Options, version);
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
        diffVersion = v;
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
        double half = Math.Max(100, Rows.ActualWidth / 2 - 70);
        double max = Math.Max(0, longest * CharWidth() - half + 20);
        HBar.Maximum = max;
        HBar.ViewportSize = half;
        HBar.LargeChange = half * 0.8;
        HBar.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (HBar.Value > max) HBar.Value = max;
    }

    /// <summary>Ширина символу моноширинного шрифту списку (виміряна, з урахуванням DPI).</summary>
    double CharWidth()
    {
        if (charWidth is double w) return w;
        var typeface = new Typeface(Rows.FontFamily, Rows.FontStyle, Rows.FontWeight, Rows.FontStretch);
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Rows.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(Rows).PixelsPerDip);
        return (charWidth = probe.WidthIncludingTrailingWhitespace).Value;
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
            StatusText.Text = optionsOn ? "Відмінностей немає (з урахуванням вибраних «ігнорувати…»)" : IdenticalContentStatus();
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

    /// <summary>
    /// Рядки однакові: «Файли однакові» — лише якщо однакові й байти; інакше — чим саме відрізняється формат
    /// (кодування, BOM, переноси рядків, кінцевий перенос).
    /// </summary>
    string IdenticalContentStatus()
    {
        var parts = TextDiff.FormatDifferences(left.Encoding, left.Text, right.Encoding, right.Text);
        if (parts.Count > 0) return "Вміст однаковий, але файли відрізняються: " + string.Join(", ", parts);
        return left.CurrentBytes().AsSpan().SequenceEqual(right.CurrentBytes())
            ? "Файли однакові"
            : "Вміст однаковий, але файли відрізняються побайтно (неоднозначне декодування)";
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
        // Порівняння ще рахується після попередньої зміни: блоки й номери рядків у diff застарілі.
        if (diffVersion != version)
        {
            StatusText.Text = "Зачекайте — порівняння ще не завершено.";
            return;
        }
        int hunk = CurrentHunk();
        if (hunk < 0)
        {
            StatusText.Text = "Спершу перейдіть до відмінності (F8), яку треба перенести.";
            return;
        }
        TextDiff.CopyHunk(diff, hunk, left.Text, right.Text, toRight);
        (toRight ? right : left).MarkDirty();
        version++;
        await RecomputeAsync(keepHunk: hunk);
    }

    bool SaveAll()
    {
        try
        {
            bool saved = SaveSide(left) && SaveSide(right);
            UpdateHeaders();
            UpdateStatus();
            if (!saved)
            {
                StatusText.Text = "Збереження скасовано.";
                return false;
            }
            StatusText.Text = $"Збережено о {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Не вдалося зберегти:\n" + ex.Message);
            return false;
        }
    }

    bool SaveSide(Side s) => !s.Dirty || s.Save(bad => Dialogs.Ask(this, "Кодування",
        $"У «{Path.GetFileName(s.Path)}» є символи, яких немає в кодуванні {TextEncodings.NameOf(s.Encoding)} " +
        $"(наприклад, «{bad}»): збережені так, вони стали б знаками «?».\n\nЗберегти файл у UTF-8?",
        new[] { "Зберегти в UTF-8", "Скасувати" }, 0, 1) == 0);

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
        version++;
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
        // Завжди hex (навіть для зображень і великих текстів), одразу на першій відмінності.
        new ViewerWindow(left.Path, ViewerMode.Hex) { Owner = this, HighlightOffset = firstDifference }.Show();
        new ViewerWindow(right.Path, ViewerMode.Hex) { Owner = this, HighlightOffset = firstDifference }.Show();
    }
}
