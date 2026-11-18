using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ByteCommander;

public sealed class SearchHit
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public string SizeText { get; init; } = "";
    public string DateText { get; init; } = "";
}

/// <summary>Пошук файлів (Alt+F7): маски імен, текст усередині файлів, рекурсивно, у фоні.</summary>
public sealed class SearchWindow : Window
{
    readonly MainWindow main;
    readonly TextBox where = new(), mask = new() { Text = "*" }, needle = new();
    readonly CheckBox subdirs = new() { Content = "У підкаталогах", IsChecked = true, Margin = new Thickness(0, 0, 18, 0) };
    readonly Button go;
    readonly ListView results = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock status = new() { Margin = new Thickness(2, 8, 0, 0) };
    readonly ObservableCollection<SearchHit> hits = new();
    readonly ConcurrentQueue<SearchHit> pending = new();
    readonly DispatcherTimer flush;
    readonly Stopwatch sw = new();
    CancellationTokenSource? cts;
    int scanned;

    public SearchWindow(MainWindow main, string startDir)
    {
        this.main = main;
        Owner = main;
        Title = "Пошук файлів";
        Width = 960;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Util.ApplyWindowTheme(this);
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 13;
        UseLayoutRounding = true;
        where.Text = startDir;
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int r, string label, FrameworkElement input, string? hint = null)
        {
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(l, r);
            Grid.SetRow(input, r);
            Grid.SetColumn(input, 1);
            input.Margin = new Thickness(0, 4, 0, 4);
            if (hint != null) input.ToolTip = hint;
            form.Children.Add(l);
            form.Children.Add(input);
        }
        Row(0, "Де шукати:", where);
        Row(1, "Ім’я / маска:", mask, "Наприклад: *.cs; *.xaml  або просто частина імені");
        Row(2, "Містить текст:", needle, "Необов’язково. Без урахування регістру; UTF-8, UTF-16, а також Windows-1251 / CP866");

        go = new Button { Content = "Знайти", MinWidth = 120, IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
        go.Click += (_, _) => StartOrStop();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(subdirs);
        actions.Children.Add(go);

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Ім’я", Width = 260, DisplayMemberBinding = new Binding(nameof(SearchHit.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = "Каталог", Width = 400, DisplayMemberBinding = new Binding(nameof(SearchHit.Folder)) });
        gv.Columns.Add(new GridViewColumn { Header = "Розмір", Width = 90, DisplayMemberBinding = new Binding(nameof(SearchHit.SizeText)) });
        gv.Columns.Add(new GridViewColumn { Header = "Змінено", Width = 130, DisplayMemberBinding = new Binding(nameof(SearchHit.DateText)) });
        results.View = gv;
        results.ItemsSource = hits;
        results.MouseDoubleClick += (_, _) => GoToSelected();
        results.KeyDown += Results_KeyDown;

        var resultsHost = new Border
        {
            Child = results,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(2),
        };
        resultsHost.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        resultsHost.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        results.Margin = new Thickness(0);

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(form);
        top.Children.Add(actions);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(status);
        root.Children.Add(resultsHost);
        Content = root;

        status.Text = "Enter — шукати · у результатах: Enter — перейти до файлу, F3 — перегляд, F4 — редагування";

        flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        flush.Tick += (_, _) => Flush();
        Closing += (_, _) => cts?.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (cts != null) cts.Cancel(); else Close(); e.Handled = true; } };
        Loaded += (_, _) =>
        {
            mask.Focus();
            mask.SelectAll();
        };
    }

    void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (results.SelectedItem is not SearchHit h) return;
        switch (e.Key)
        {
            case Key.Enter:
                GoToSelected();
                e.Handled = true;
                break;
            case Key.F3 when !h.IsDirectory:
                new ViewerWindow(h.FullPath, false) { Owner = this }.Show();
                e.Handled = true;
                break;
            case Key.F4 when !h.IsDirectory:
                new ViewerWindow(h.FullPath, true) { Owner = this }.Show();
                e.Handled = true;
                break;
        }
    }

    void GoToSelected()
    {
        if (results.SelectedItem is not SearchHit h) return;
        main.GoToFile(h.FullPath);
        main.Activate();
    }

    void StartOrStop()
    {
        if (cts != null)
        {
            cts.Cancel();
            return;
        }
        var root = where.Text.Trim().Trim('"');
        if (!Directory.Exists(root))
        {
            Dialogs.Error(this, $"Каталог «{root}» не існує.");
            return;
        }
        var masks = Util.SplitMasks(mask.Text);
        var text = needle.Text;
        bool recurse = subdirs.IsChecked == true;

        hits.Clear();
        while (pending.TryDequeue(out _)) { }
        scanned = 0;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        go.Content = "Зупинити";
        sw.Restart();
        flush.Start();

        Task.Run(() => Worker(root, masks, text, recurse, token), token).ContinueWith(t =>
        {
            flush.Stop();
            Flush();
            cts?.Dispose();
            cts = null;
            go.Content = "Знайти";
            var prefix = t.IsFaulted ? "Помилка: " + t.Exception?.GetBaseException().Message + ". "
                : token.IsCancellationRequested ? "Зупинено. " : "Готово. ";
            status.Text = prefix + StatusLine();
            if (hits.Count > 0 && results.SelectedIndex < 0) results.SelectedIndex = 0;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    string StatusLine() =>
        $"Знайдено: {hits.Count:N0}  ·  переглянуто: {scanned:N0}  ·  {sw.Elapsed.TotalSeconds:0.0} с";

    void Flush()
    {
        int n = 0;
        while (n < 2000 && pending.TryDequeue(out var h))
        {
            hits.Add(h);
            n++;
        }
        if (cts != null) status.Text = "Пошук…  " + StatusLine();
    }

    void Worker(string root, string[] masks, string text, bool recurse, CancellationToken ct)
    {
        var opts = recurse
            ? Util.DeepScan
            : new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref scanned);
            bool isDir = fsi is DirectoryInfo;
            if (!Util.MatchAny(masks, fsi.Name)) continue;
            if (text.Length > 0 && (isDir || !FileContains(fsi.FullName, text, ct))) continue;
            pending.Enqueue(new SearchHit
            {
                Name = fsi.Name,
                Folder = Path.GetDirectoryName(fsi.FullName) ?? "",
                FullPath = fsi.FullName,
                IsDirectory = isDir,
                SizeText = isDir ? "<DIR>" : Util.FormatSize(((FileInfo)fsi).Length),
                DateText = fsi.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
            });
        }
    }

    // Кодування для файлів без BOM, у яких текст не знайшовся як UTF-8 (типові для кирилиці у Windows).
    static readonly Lazy<Encoding[]> LegacyEncodings = new(() => new[] { Encoding.GetEncoding(1251), Encoding.GetEncoding(866) });

    /// <summary>
    /// Чи містить файл текст (без урахування регістру). Спершу UTF-8 / UTF-16 за BOM;
    /// якщо BOM немає, а шуканий текст не-ASCII, — ще Windows-1251 і CP866.
    /// </summary>
    internal static bool FileContains(string path, string text, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > 256L * 1024 * 1024) return false;
            if (Scan(path, text, Encoding.UTF8, detectBom: true, ct, out bool hadBom)) return true;
            if (hadBom || text.All(c => c < 128)) return false;
            foreach (var enc in LegacyEncodings.Value)
                if (Scan(path, text, enc, detectBom: false, ct, out _)) return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return false;
    }

    static bool Scan(string path, string text, Encoding encoding, bool detectBom, CancellationToken ct, out bool hadBom)
    {
        using var sr = new StreamReader(path, encoding, detectBom, bufferSize: 1 << 16);
        var buf = new char[1 << 16];
        string carry = "";
        int n;
        bool first = true;
        hadBom = false;
        while ((n = sr.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (first)
            {
                // StreamReader змінює CurrentEncoding лише якщо знайшов BOM (UTF-8 з BOM, UTF-16, UTF-32)
                hadBom = detectBom && (sr.CurrentEncoding is not UTF8Encoding || HasUtf8Bom(path));
                first = false;
            }
            var chunk = carry + new string(buf, 0, n);
            if (chunk.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            int keep = Math.Min(chunk.Length, text.Length - 1);
            carry = chunk[^keep..];
        }
        return false;
    }

    static bool HasUtf8Bom(string path)
    {
        Span<byte> b = stackalloc byte[3];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return fs.Read(b) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
    }
}
