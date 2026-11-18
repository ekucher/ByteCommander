using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ByteCommander.Services;

namespace ByteCommander.Compare;

/// <summary>Рядок дерева порівняння каталогів.</summary>
public sealed class FolderRow
{
    public required FolderEntry Entry { get; init; }
    public int Depth { get; init; }
    public bool Expanded { get; init; }

    public EntryStatus Status => Entry.Status;
    public string Name => Entry.Name;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string Expander => Entry.IsDirectory && Entry.Children.Count > 0 ? (Expanded ? "" : "") : "";
    public ImageSource? Icon => ShellIcons.Get((Entry.Left ?? Entry.Right)?.FullName ?? Entry.Name, Entry.IsDirectory);
    public string LeftSize => Size(Entry.Left);
    public string RightSize => Size(Entry.Right);
    public string LeftDate => Date(Entry.Left);
    public string RightDate => Date(Entry.Right);

    public string StatusMark => Entry.Status switch
    {
        EntryStatus.Same => "=",
        EntryStatus.LeftOnly => "◀",
        EntryStatus.RightOnly => "▶",
        _ => Entry.LeftNewer ? "≠ ◀" : Entry.RightNewer ? "≠ ▶" : "≠",
    };

    public string StatusText => (Entry.Status switch
    {
        EntryStatus.Same => "Однакові",
        EntryStatus.LeftOnly => "Лише зліва",
        EntryStatus.RightOnly => "Лише справа",
        _ when Entry.IsDirectory => "Є відмінності всередині",
        _ => "Різні" + (Entry.LeftNewer ? " — лівий новіший" : Entry.RightNewer ? " — правий новіший" : ""),
    }) + (Entry.Error != null ? $" ({Entry.Error})" : "");

    static string Size(FileSystemInfo? f) => f switch
    {
        null => "",
        FileInfo fi => Util.FormatSize(fi.Length),
        _ => "<DIR>",
    };

    static string Date(FileSystemInfo? f) => f == null ? "" : f.LastWriteTime.ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// Порівняння каталогів (як у Beyond Compare): дерево з усіх файлів обох боків, статуси, фільтр відмінностей,
/// копіювання вибраного в будь-який бік, відкриття порівняння файлів.
/// </summary>
public partial class FolderCompareWindow : Window
{
    FolderEntry? root;
    readonly Dictionary<string, bool> expanded = new(StringComparer.OrdinalIgnoreCase);
    List<FolderRow> rows = new();
    CancellationTokenSource? scanCts;

    public FolderCompareWindow(string left, string right)
    {
        InitializeComponent();
        LeftPath.Text = left;
        RightPath.Text = right;
        LeftPath.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = CompareAsync(); } };
        RightPath.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = CompareAsync(); } };
        Loaded += async (_, _) => await CompareAsync();
    }

    string LeftRoot => Util.NormalizeDir(LeftPath.Text);
    string RightRoot => Util.NormalizeDir(RightPath.Text);

    // =====================================================================
    // Сканування
    // =====================================================================

    async Task CompareAsync()
    {
        string l, r;
        try
        {
            (l, r) = (LeftRoot, RightRoot);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Некоректний шлях: " + ex.Message;
            return;
        }
        if (!Directory.Exists(l) || !Directory.Exists(r))
        {
            StatusText.Text = $"Каталог не існує: {(Directory.Exists(l) ? r : l)}";
            return;
        }

        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource();
        Title = $"Порівняння каталогів: {l} ↔ {r}";
        var options = new FolderCompareOptions(ByContent: ByContentBox.IsChecked == true);
        var progressTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        string current = "";
        progressTimer.Tick += (_, _) => StatusText.Text = "Порівняння…  " + current;
        progressTimer.Start();
        try
        {
            var result = await Task.Run(() => FolderComparer.Compare(l, r, options, cts.Token, p => current = p));
            if (cts != scanCts) return;
            root = result;
            Rebuild();
            UpdateStatus();
            List.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "Помилка: " + ex.Message;
        }
        finally
        {
            progressTimer.Stop();
        }
    }

    void UpdateStatus()
    {
        if (root == null) return;
        var (s, d, lo, ro) = FolderComparer.Count(root);
        StatusText.Text = root.Status == EntryStatus.Same
            ? $"Каталоги однакові  ·  {Util.Count(s, "файл", "файли", "файлів")}"
            : $"однакових {s}  ·  різних {d}  ·  лише зліва {lo}  ·  лише справа {ro}" +
              (ByContentBox.IsChecked == true ? "  ·  порівняно за вмістом" : "  ·  порівняно за розміром і датою");
    }

    // =====================================================================
    // Дерево
    // =====================================================================

    bool IsExpanded(FolderEntry e) =>
        expanded.TryGetValue(e.RelativePath, out var v) ? v : e.Status != EntryStatus.Same;   // відмінності розгорнуто одразу

    void Rebuild(FolderEntry? select = null)
    {
        if (root == null) return;
        var keep = select ?? (List.SelectedItem as FolderRow)?.Entry;
        bool onlyDiffs = OnlyDiffsBox.IsChecked == true;
        var list = new List<FolderRow>();
        void Add(FolderEntry e, int depth)
        {
            if (onlyDiffs && e.Status == EntryStatus.Same) return;
            bool open = e.IsDirectory && IsExpanded(e);
            list.Add(new FolderRow { Entry = e, Depth = depth, Expanded = open });
            if (open) foreach (var c in e.Children) Add(c, depth + 1);
        }
        foreach (var c in root.Children) Add(c, 0);
        rows = list;
        List.ItemsSource = rows;
        var again = keep == null ? null : rows.FirstOrDefault(x => ReferenceEquals(x.Entry, keep));
        if (again != null)
        {
            List.SelectedItem = again;
            List.ScrollIntoView(again);
        }
        else if (rows.Count > 0) List.SelectedIndex = 0;
    }

    void Toggle(FolderRow row, bool? open = null)
    {
        if (!row.Entry.IsDirectory || row.Entry.Children.Count == 0) return;
        expanded[row.Entry.RelativePath] = open ?? !row.Expanded;
        Rebuild(row.Entry);
    }

    void SetAll(bool open)
    {
        if (root == null) return;
        void Walk(FolderEntry e)
        {
            foreach (var c in e.Children.Where(c => c.IsDirectory))
            {
                expanded[c.RelativePath] = open;
                Walk(c);
            }
        }
        Walk(root);
        Rebuild();
    }

    void Expander_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderRow row)
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListViewItem { DataContext: FolderRow row })
            OpenRow(row);
    }

    void OpenRow(FolderRow row)
    {
        if (row.Entry.IsDirectory) Toggle(row);
        else OpenFiles(row);
    }

    void OpenFiles(FolderRow row)
    {
        if (row.Entry.Left is FileInfo l && row.Entry.Right is FileInfo r)
            new TextCompareWindow(l.FullName, r.FullName) { Owner = this }.Show();
        else StatusText.Text = "Файл є лише з одного боку — порівнювати нема з чим.";
    }

    void List_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ColName.Width = Math.Max(240, List.ActualWidth - 90 - 130 - 54 - 90 - 130 - 30);

    // =====================================================================
    // Копіювання
    // =====================================================================

    async Task CopyAsync(bool toRight)
    {
        if (root == null) return;
        var selected = List.SelectedItems.Cast<FolderRow>().Select(r => r.Entry).ToList();
        // Лише те, що є з боку-джерела і відрізняється; якщо вибрано і каталог, і щось усередині — досить каталогу.
        var picked = selected
            .Where(e => (toRight ? e.Left : e.Right) != null && e.Status != EntryStatus.Same)
            .Where(e => !selected.Any(p => p != e && p.IsDirectory && e.RelativePath.StartsWith(p.RelativePath + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (picked.Count == 0)
        {
            StatusText.Text = toRight ? "Немає вибраних відмінностей, які є зліва." : "Немає вибраних відмінностей, які є справа.";
            return;
        }
        string srcRoot = toRight ? LeftRoot : RightRoot, dstRoot = toRight ? RightRoot : LeftRoot;
        var what = Util.Describe(picked.Select(p => p.RelativePath).ToList());
        if (!Dialogs.Confirm(this, toRight ? "Копіювати праворуч" : "Копіювати ліворуч",
                $"Скопіювати {what} {(toRight ? "зліва направо" : "справа наліво")}?\nНаявні файли з того боку буде замінено.", "Копіювати"))
            return;

        var groups = picked.GroupBy(p => Path.GetDirectoryName(p.RelativePath) ?? "");
        var outcome = await OperationRunner.RunAsync(this, "Копіювання", (_, _) => ConflictChoice.Overwrite, (op, ct) =>
        {
            foreach (var g in groups)
            {
                var sources = g.Select(p => Path.Combine(srcRoot, p.RelativePath)).ToArray();
                op.Transfer(sources, Util.AsDirTarget(Path.Combine(dstRoot, g.Key)), move: false, ct);
            }
        });
        StatusText.Text = outcome.Canceled ? "Копіювання скасовано" : $"Скопійовано: {outcome.Summary}";
        await CompareAsync();
    }

    // =====================================================================
    // Клавіші й кнопки
    // =====================================================================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool inText = Keyboard.FocusedElement is TextBox;
        var row = List.SelectedItem as FolderRow;
        Action? a = (key, m) switch
        {
            (Key.F5, ModifierKeys.None) => () => _ = CompareAsync(),
            (Key.Right, ModifierKeys.Alt) => () => _ = CopyAsync(toRight: true),
            (Key.Left, ModifierKeys.Alt) => () => _ = CopyAsync(toRight: false),
            (Key.Escape, ModifierKeys.None) => Close,
            (Key.Enter, ModifierKeys.None) when !inText && row != null => () => OpenRow(row),
            (Key.Right, ModifierKeys.None) when !inText && row is { Entry.IsDirectory: true, Expanded: false } => () => Toggle(row, true),
            (Key.Left, ModifierKeys.None) when !inText && row is { Entry.IsDirectory: true, Expanded: true } => () => Toggle(row, false),
            _ => null,
        };
        if (a == null) return;
        e.Handled = true;
        a();
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => scanCts?.Cancel();

    async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();
    async void ByContent_Click(object sender, RoutedEventArgs e) => await CompareAsync();
    void OnlyDiffs_Click(object sender, RoutedEventArgs e) => Rebuild();
    async void CopyRight_Click(object sender, RoutedEventArgs e) => await CopyAsync(toRight: true);
    async void CopyLeft_Click(object sender, RoutedEventArgs e) => await CopyAsync(toRight: false);
    void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is FolderRow row) OpenRow(row);
    }

    async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        expanded.Clear();
        await CompareAsync();
    }
}
