using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ByteCommander.Services;
using ByteCommander.Viewer;

namespace ByteCommander;

/// <summary>Переглядач (F3, лише читання) чи редактор (F4).</summary>
public enum ViewerMode { View, Edit }

/// <summary>
/// Переглядач (F3) і простий редактор (F4).
/// Невеликі текстові файли — у звичайному TextBox (виділення, копіювання, редагування);
/// великі файли й hex — у віртуальному <see cref="LargeFileView"/>, який читає з диска лише видиме.
/// </summary>
public sealed class ViewerWindow : Window
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp" };

    /// <summary>До цього розміру текст відкривається в TextBox (і в редакторі); більші — віртуально.</summary>
    const int MaxTextBoxBytes = 8 * 1024 * 1024;

    /// <summary>Що зараз показано: текстове поле, віртуальний перегляд чи зображення.</summary>
    enum Display { Text, Large, Image }

    string path;
    readonly bool edit;
    readonly TextBox text;
    readonly LargeFileView large = new();
    readonly TextBlock info = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Grid content = new();
    readonly TextBox findBox = new() { Width = 320 };
    readonly Border findBar;
    readonly Button encButton, wrapButton, hexButton, saveButton;
    Encoding enc = new UTF8Encoding(false);
    bool encChosen, hex, hexDecided, dirty, loading;
    Display display;
    CancellationTokenSource? findCts;
    string baseInfo = "";

    public ViewerWindow(string path, ViewerMode mode)
    {
        this.path = path;
        edit = mode == ViewerMode.Edit;
        Width = 1040;
        Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Util.ApplyWindowTheme(this);
        UseLayoutRounding = true;

        text = new TextBox
        {
            FontFamily = Util.Resource<FontFamily>(StyleKeys.MonoFont),
            FontSize = 13,
            AcceptsReturn = true,
            AcceptsTab = true,
            IsReadOnly = !edit,
            IsUndoEnabled = edit,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 4, 4),
            IsInactiveSelectionHighlightEnabled = true,
        };
        text.TextChanged += (_, _) =>
        {
            if (!loading && edit && !dirty)
            {
                dirty = true;
                UpdateTitle();
            }
        };
        large.StatusChanged += UpdateLargeInfo;

        // Панель інструментів
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 6) };
        encButton = ToolButton("", "Кодування");
        encButton.Click += (_, _) => ShowEncodingMenu();
        wrapButton = ToolButton("", "Перенос рядків");
        wrapButton.Click += (_, _) => ToggleWrap();
        hexButton = ToolButton("", "Hex (F4)");
        hexButton.Click += (_, _) => ToggleHex();
        var findButton = ToolButton("", "Пошук (Ctrl+F)");
        findButton.Click += (_, _) => ShowFind();
        saveButton = ToolButton("", "Зберегти (Ctrl+S)");
        saveButton.Click += (_, _) => Save();
        if (edit) bar.Children.Add(saveButton);
        bar.Children.Add(encButton);
        bar.Children.Add(wrapButton);
        if (!edit) bar.Children.Add(hexButton);
        bar.Children.Add(findButton);

        // Рядок пошуку
        var findPanel = new StackPanel { Orientation = Orientation.Horizontal };
        findPanel.Children.Add(new TextBlock { Text = "Знайти:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        findPanel.Children.Add(findBox);
        var findHint = new TextBlock
        {
            Text = "Enter / F3 — далі, Esc — закрити (у hex: текст або байти, напр. «4D 5A»)",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        findHint.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextDimBrush);
        findPanel.Children.Add(findHint);
        findBar = new Border { Padding = new Thickness(10, 6, 10, 0), Child = findPanel, Visibility = Visibility.Collapsed };
        findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindNext();
                e.Handled = true;
            }
        };
        findBox.TextChanged += (_, _) =>
        {
            findBox.ClearValue(BorderBrushProperty);
            if (display == Display.Large) large.ClearMatch();
        };

        info.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextDimBrush);
        var status = new Border { Padding = new Thickness(12, 5, 12, 7), Child = info };

        var host = new Border
        {
            Margin = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = content,
        };
        host.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.LineBrush);
        host.SetResourceReference(Border.BackgroundProperty, ThemeKeys.InputBrush);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(findBar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(status);
        dock.Children.Add(findBar);
        dock.Children.Add(host);
        Content = dock;

        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            findCts?.Cancel();
            large.SetSource(null);
        };
        Loaded += (_, _) => Load();
        UpdateTitle();
    }

    static Button ToolButton(string glyph, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Util.Glyph(glyph, 13));
        sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0) });
        return new Button { Content = sp, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
    }

    void UpdateTitle() =>
        Title = (dirty ? "● " : "") + Path.GetFileName(path) + (edit ? " — редактор" : " — перегляд") + "  ·  " + Path.GetDirectoryName(path);

    // ------------------------------------------------------------------ завантаження

    void Load()
    {
        try
        {
            if (!edit && ImageExt.Contains(Path.GetExtension(path)) && TryShowImage()) return;

            var fi = new FileInfo(path);
            if (edit && fi.Length > MaxTextBoxBytes)
            {
                Dialogs.Error(this, $"Файл завеликий для вбудованого редактора (понад {Util.FormatSize(MaxTextBoxBytes)}). Відкрийте його на перегляд (F3).");
                Close();
                return;
            }

            var head = ReadHead((int)Math.Min(fi.Length, 64 * 1024));
            if (!edit && !hexDecided)
            {
                hex = TextEncodings.LooksBinary(head);
                hexDecided = true;
            }
            if (!encChosen) enc = TextEncodings.Detect(head, truncated: fi.Length > head.Length);

            if (!edit && (hex || fi.Length > MaxTextBoxBytes)) ShowLarge(fi);
            else ShowSmallText(fi);
        }
        catch (Exception ex)
        {
            loading = false;
            info.Text = "Помилка: " + ex.Message;
        }
        hexButton.Visibility = edit || display == Display.Image ? Visibility.Collapsed : Visibility.Visible;
        encButton.Visibility = display == Display.Image || hex ? Visibility.Collapsed : Visibility.Visible;
        wrapButton.Visibility = display == Display.Text ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowSmallText(FileInfo fi)
    {
        SetContent(Display.Text, text);
        var bytes = ReadHead((int)fi.Length);
        loading = true;
        text.Text = Decode(bytes);
        loading = false;
        text.CaretIndex = 0;
        text.ScrollToHome();
        info.Text = $"{Util.FormatSize(fi.Length)}  ·  {TextEncodings.NameOf(enc)}  ·  {TextEncodings.CountLines(text.Text):N0} рядків";
        text.Focus();
    }

    void ShowLarge(FileInfo fi)
    {
        SetContent(Display.Large, large);
        baseInfo = $"{Util.FormatSize(fi.Length)}  ·  {(hex ? "HEX" : TextEncodings.NameOf(enc))}";
        large.SetSource(hex ? new HexRowSource(path) : new TextRowSource(path, enc));
        large.Focus();
    }

    void UpdateLargeInfo()
    {
        if (display != Display.Large || large.Source is not { } src) return;
        info.Text = $"{baseInfo}  ·  {src.RowCount:N0} рядків" + (src.IsComplete ? "" : "  ·  індексація…");
    }

    void SetContent(Display m, UIElement element)
    {
        // Новий вміст (hex / кодування / інший файл) — незавершений пошук по старому вже не актуальний.
        findCts?.Cancel();
        if (display == Display.Large && m != Display.Large) large.SetSource(null);
        display = m;
        if (content.Children.Count == 1 && content.Children[0] == element) return;
        content.Children.Clear();
        content.Children.Add(element);
    }

    byte[] ReadHead(int count)
    {
        var buf = new byte[count];
        using var fs = Util.OpenShared(path);
        int read = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
        return read < count ? buf[..read] : buf;
    }

    bool TryShowImage()
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(path))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            SetContent(Display.Image, img);
            info.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight} px  ·  {Util.FormatSize(new FileInfo(path).Length)}  ·  ← → — попереднє / наступне зображення";
            UpdateTitle();
            return true;
        }
        catch
        {
            return false;
        }
    }

    void NextImage(int dir)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder == null) return;
        List<string> list;
        try
        {
            list = Directory.EnumerateFiles(folder).Where(f => ImageExt.Contains(Path.GetExtension(f))).ToList();
        }
        catch
        {
            return;
        }
        list.Sort(NativeMethods.StrCmpLogicalW);
        if (list.Count == 0) return;
        int i = list.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        i = ((i < 0 ? 0 : i + dir) % list.Count + list.Count) % list.Count;
        path = list[i];
        if (!TryShowImage()) info.Text = "Не вдалося відкрити " + Path.GetFileName(path);
    }

    string Decode(byte[] b)
    {
        int skip = TextEncodings.PreambleLength(b, enc);
        return enc.GetString(b, skip, b.Length - skip);
    }

    // ------------------------------------------------------------------ дії

    void ShowEncodingMenu()
    {
        var m = new ContextMenu();
        foreach (var choice in TextEncodings.Choices)
        {
            m.Items.Add(Util.MakeMenuItem(TextEncodings.NameOf(enc) == choice.Name ? "\uE73E" : "", choice.Name, null, () =>
            {
                enc = choice.Create();
                encChosen = true;
                if (!dirty) Load();
            }));
        }
        m.PlacementTarget = encButton;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void ToggleWrap() => text.TextWrapping = text.TextWrapping == TextWrapping.NoWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    void ToggleHex()
    {
        if (edit || display == Display.Image) return;
        hex = !hex;
        hexDecided = true;
        Load();
    }

    void ShowFind()
    {
        findBar.Visibility = Visibility.Visible;
        if (display == Display.Text && text.SelectionLength > 0 && text.SelectionLength < 200) findBox.Text = text.SelectedText;
        findBox.Focus();
        findBox.SelectAll();
    }

    void FindNext()
    {
        var q = findBox.Text;
        if (q.Length == 0) return;
        if (display == Display.Large) _ = FindNextLarge(q);
        else if (display == Display.Text) FindNextText(q);
    }

    void FindNextText(string q)
    {
        var t = text.Text;
        int start = Math.Min(text.SelectionStart + (text.SelectionLength > 0 ? 1 : 0), t.Length);
        int idx = t.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = t.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            findBox.SetResourceReference(BorderBrushProperty, ThemeKeys.DangerBrush);
            return;
        }
        findBox.ClearValue(BorderBrushProperty);
        text.Select(idx, q.Length);
        text.ScrollToLine(Math.Max(0, text.GetLineIndexFromCharacterIndex(idx) - 3));
        var r = text.GetRectFromCharacterIndex(idx);
        if (!r.IsEmpty && (r.Left < 0 || r.Left > text.ViewportWidth - 40))
            text.ScrollToHorizontalOffset(Math.Max(0, text.HorizontalOffset + r.Left - 80));
    }

    async Task FindNextLarge(string q)
    {
        if (large.Source is not { } src) return;
        findCts?.Cancel();
        var cts = findCts = new CancellationTokenSource();
        var (row, col) = large.Match is { } m ? (m.Row, m.Column + 1) : (large.TopRow, 0);
        void Progress(double p) => Dispatcher.InvokeAsync(() => { if (findCts == cts) info.Text = $"Пошук «{q}»…  {p:P0}  ·  Esc — зупинити"; });
        try
        {
            var found = await Task.Run(() => src.Find(q, row, col, cts.Token, Progress));
            bool wrapped = false;
            if (found == null && (row > 0 || col > 0))
            {
                found = await Task.Run(() => src.Find(q, 0, 0, cts.Token, Progress));
                wrapped = true;
            }
            if (cts.IsCancellationRequested || large.Source != src) return;
            UpdateLargeInfo();
            if (found is { } f)
            {
                // Пошук іде по файлу незалежно від індексу; прокрутка ж обмежена вже проіндексованими рядками.
                while (large.Source == src && !src.IsComplete && src.RowCount <= f.Row + 1)
                {
                    info.Text = $"Знайдено в рядку {f.Row + 1:N0} — чекаю індексацію ({src.RowCount:N0})…  ·  Esc — зупинити";
                    await Task.Delay(100, cts.Token);
                }
                if (large.Source != src) return;
                findBox.ClearValue(BorderBrushProperty);
                large.ShowMatch(f);
                if (wrapped) info.Text += "  ·  пошук продовжено з початку";
            }
            else
            {
                findBox.SetResourceReference(BorderBrushProperty, ThemeKeys.DangerBrush);
                info.Text += $"  ·  «{q}» не знайдено";
            }
        }
        catch (OperationCanceledException)
        {
            UpdateLargeInfo();
        }
        finally
        {
            if (findCts == cts) findCts = null;
        }
    }

    bool Save()
    {
        if (!edit) return true;
        try
        {
            var pre = enc.GetPreamble();
            var body = enc.GetBytes(text.Text);
            // Через тимчасовий файл: збій посеред запису (місце на диску, знімний носій) не знищить оригінал.
            // File.Replace зберігає атрибути, дату створення й права наявного файлу.
            Util.WriteViaTemp(path, tmp =>
            {
                using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write);
                fs.Write(pre);
                fs.Write(body);
            }, keepDestinationMetadata: true);
            dirty = false;
            UpdateTitle();
            info.Text = $"Збережено о {DateTime.Now:HH:mm:ss}  ·  {TextEncodings.NameOf(enc)}  ·  {Util.FormatSize(pre.Length + body.Length)}";
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Не вдалося зберегти файл:\n" + ex.Message);
            return false;
        }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!dirty) return;
        int r = Dialogs.Ask(this, "Незбережені зміни", $"Зберегти зміни у «{Path.GetFileName(path)}»?",
            new[] { "Зберегти", "Не зберігати", "Скасувати" }, 0, 2);
        if (r == 2 || (r == 0 && !Save())) e.Cancel = true;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var m = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                if (findCts != null) findCts.Cancel();
                else if (findBar.Visibility == Visibility.Visible)
                {
                    findBar.Visibility = Visibility.Collapsed;
                    if (display == Display.Large) large.Focus(); else text.Focus();
                }
                else Close();
                e.Handled = true;
                break;
            case Key.F3 when m == ModifierKeys.None:
                if (findBox.Text.Length > 0 && findBar.Visibility == Visibility.Visible) FindNext();
                else if (!edit) Close();
                e.Handled = true;
                break;
            case Key.F4 when m == ModifierKeys.None && !edit:
                ToggleHex();
                e.Handled = true;
                break;
            case Key.F when m == ModifierKeys.Control:
                ShowFind();
                e.Handled = true;
                break;
            case Key.S when m == ModifierKeys.Control && edit:
            case Key.F2 when m == ModifierKeys.None && edit:
                Save();
                e.Handled = true;
                break;
            case Key.Right or Key.PageDown or Key.Space when display == Display.Image:
                NextImage(1);
                e.Handled = true;
                break;
            case Key.Left or Key.PageUp or Key.Back when display == Display.Image:
                NextImage(-1);
                e.Handled = true;
                break;
        }
    }
}
