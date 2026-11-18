using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ByteCommander.Services;

namespace ByteCommander;

/// <summary>Переглядач (F3: текст / hex / зображення) і простий редактор (F4).</summary>
public sealed class ViewerWindow : Window
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp" };

    const int MaxTextBytes = 8 * 1024 * 1024;
    const int MaxHexBytes = 1024 * 1024;

    string path;
    readonly bool edit;
    readonly TextBox text;
    readonly TextBlock info = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Grid content = new();
    readonly TextBox findBox = new() { Width = 320 };
    readonly Border findBar;
    readonly Button encButton, wrapButton, hexButton, saveButton;
    Encoding enc = new UTF8Encoding(false);
    bool encChosen, hex, hexDecided, dirty, loading, imageMode;

    public ViewerWindow(string path, bool edit)
    {
        this.path = path;
        this.edit = edit;
        Width = 1040;
        Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Util.Res("BgBrush");
        Foreground = Util.Res("TextBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 13;
        UseLayoutRounding = true;

        text = new TextBox
        {
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
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
        findPanel.Children.Add(new TextBlock
        {
            Text = "Enter / F3 — далі, Esc — закрити",
            Foreground = Util.Res("TextDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        });
        findBar = new Border { Padding = new Thickness(10, 6, 10, 0), Child = findPanel, Visibility = Visibility.Collapsed };
        findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindNext();
                e.Handled = true;
            }
        };

        info.Foreground = Util.Res("TextDimBrush");
        var status = new Border { Padding = new Thickness(12, 5, 12, 7), Child = info };

        content.Children.Add(text);
        var host = new Border
        {
            Margin = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(8),
            BorderBrush = Util.Res("LineBrush"),
            BorderThickness = new Thickness(1),
            Background = Util.Res("InputBrush"),
            ClipToBounds = true,
            Child = content,
        };

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
        Loaded += (_, _) =>
        {
            Load();
            if (!imageMode) text.Focus();
        };
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
            ShowTextMode();

            var fi = new FileInfo(path);
            if (edit && fi.Length > MaxTextBytes)
            {
                Dialogs.Error(this, "Файл завеликий для вбудованого редактора (понад 8 МБ).");
                Close();
                return;
            }

            var bytes = ReadHead((int)Math.Min(fi.Length, MaxTextBytes));
            if (!edit && !hexDecided)
            {
                hex = LooksBinary(bytes);
                hexDecided = true;
            }

            loading = true;
            if (hex)
            {
                text.Text = HexDump(bytes, Math.Min(bytes.Length, MaxHexBytes));
            }
            else
            {
                if (!encChosen) enc = Detect(bytes, truncated: fi.Length > bytes.Length);
                text.Text = Decode(bytes);
            }
            loading = false;
            text.CaretIndex = 0;
            text.ScrollToHome();

            long shown = hex ? Math.Min(bytes.Length, MaxHexBytes) : bytes.Length;
            int lines = 1;
            foreach (var c in text.Text) if (c == '\n') lines++;
            info.Text = $"{Util.FormatSize(fi.Length)}  ·  {(hex ? "HEX" : EncodingName(enc))}  ·  {lines:N0} рядків" +
                        (fi.Length > shown ? $"  ·  показано перші {Util.FormatSize(shown)}" : "");
        }
        catch (Exception ex)
        {
            loading = false;
            info.Text = "Помилка: " + ex.Message;
        }
        hexButton.Visibility = edit || imageMode ? Visibility.Collapsed : Visibility.Visible;
    }

    byte[] ReadHead(int count)
    {
        var buf = new byte[count];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < count) Array.Resize(ref buf, read);
        return buf;
    }

    void ShowTextMode()
    {
        imageMode = false;
        if (!content.Children.Contains(text))
        {
            content.Children.Clear();
            content.Children.Add(text);
        }
        encButton.Visibility = wrapButton.Visibility = Visibility.Visible;
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
            content.Children.Clear();
            content.Children.Add(img);
            imageMode = true;
            encButton.Visibility = wrapButton.Visibility = hexButton.Visibility = Visibility.Collapsed;
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
        int i = list.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (list.Count == 0) return;
        i = ((i < 0 ? 0 : i + dir) % list.Count + list.Count) % list.Count;
        path = list[i];
        if (!TryShowImage()) info.Text = "Не вдалося відкрити " + Path.GetFileName(path);
    }

    static bool LooksBinary(byte[] b)
    {
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return false;
        int n = Math.Min(b.Length, 8192);
        for (int i = 0; i < n; i++) if (b[i] == 0) return true;
        return false;
    }

    static Encoding Detect(byte[] b, bool truncated)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new UTF8Encoding(true);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return new UnicodeEncoding(false, true);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return new UnicodeEncoding(true, true);
        try
        {
            // Хвіст обрізаного файлу може розрізати UTF-8 символ — не враховуємо останні байти.
            int len = truncated ? Math.Max(0, b.Length - 4) : b.Length;
            new UTF8Encoding(false, true).GetString(b, 0, len);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251);
        }
    }

    string Decode(byte[] b)
    {
        int skip = 0;
        var pre = enc.GetPreamble();
        if (pre.Length > 0 && b.Length >= pre.Length && b.AsSpan(0, pre.Length).SequenceEqual(pre)) skip = pre.Length;
        else if (enc is UTF8Encoding && b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) skip = 3;
        return enc.GetString(b, skip, b.Length - skip);
    }

    static string EncodingName(Encoding e) => e switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
        UnicodeEncoding => e.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE",
        _ => e.CodePage switch { 1251 => "Windows-1251", 866 => "DOS-866", 21866 => "KOI8-U", _ => e.WebName },
    };

    static string HexDump(byte[] b, int len)
    {
        var sb = new StringBuilder(len * 4 + 64);
        for (int off = 0; off < len; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < len) sb.Append(b[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && off + i < len; i++)
            {
                var c = b[off + i];
                sb.Append(c >= 32 && c < 127 ? (char)c : '·');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ дії

    void ShowEncodingMenu()
    {
        var m = new ContextMenu();
        void Add(string name, Func<Encoding> make)
        {
            m.Items.Add(Util.MakeMenuItem(EncodingName(enc) == name ? "" : "", name, null, () =>
            {
                enc = make();
                encChosen = true;
                hex = false;
                hexDecided = true;
                if (!dirty) Load();
            }));
        }
        Add("UTF-8", () => new UTF8Encoding(false));
        Add("UTF-8 BOM", () => new UTF8Encoding(true));
        Add("Windows-1251", () => Encoding.GetEncoding(1251));
        Add("DOS-866", () => Encoding.GetEncoding(866));
        Add("KOI8-U", () => Encoding.GetEncoding(21866));
        Add("UTF-16 LE", () => new UnicodeEncoding(false, true));
        m.PlacementTarget = encButton;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    void ToggleWrap() => text.TextWrapping = text.TextWrapping == TextWrapping.NoWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    void ToggleHex()
    {
        if (edit || imageMode) return;
        hex = !hex;
        hexDecided = true;
        Load();
    }

    void ShowFind()
    {
        findBar.Visibility = Visibility.Visible;
        if (text.SelectionLength > 0 && text.SelectionLength < 200) findBox.Text = text.SelectedText;
        findBox.Focus();
        findBox.SelectAll();
    }

    void FindNext()
    {
        var q = findBox.Text;
        if (q.Length == 0 || imageMode) return;
        var t = text.Text;
        int start = Math.Min(text.SelectionStart + (text.SelectionLength > 0 ? 1 : 0), t.Length);
        int idx = t.IndexOf(q, start, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = t.IndexOf(q, 0, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            findBox.BorderBrush = Util.Res("DangerBrush");
            return;
        }
        findBox.ClearValue(BorderBrushProperty);
        text.Select(idx, q.Length);
        text.ScrollToLine(Math.Max(0, text.GetLineIndexFromCharacterIndex(idx) - 3));
        var r = text.GetRectFromCharacterIndex(idx);
        if (!r.IsEmpty && (r.Left < 0 || r.Left > text.ViewportWidth - 40))
            text.ScrollToHorizontalOffset(Math.Max(0, text.HorizontalOffset + r.Left - 80));
    }

    bool Save()
    {
        if (!edit) return true;
        try
        {
            var pre = enc.GetPreamble();
            var body = enc.GetBytes(text.Text);
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0);
                fs.Write(pre);
                fs.Write(body);
            }
            dirty = false;
            UpdateTitle();
            info.Text = $"Збережено о {DateTime.Now:HH:mm:ss}  ·  {EncodingName(enc)}  ·  {Util.FormatSize(pre.Length + body.Length)}";
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
                if (findBar.Visibility == Visibility.Visible)
                {
                    findBar.Visibility = Visibility.Collapsed;
                    text.Focus();
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
            case Key.Right or Key.PageDown or Key.Space when imageMode:
                NextImage(1);
                e.Handled = true;
                break;
            case Key.Left or Key.PageUp or Key.Back when imageMode:
                NextImage(-1);
                e.Handled = true;
                break;
        }
    }
}
