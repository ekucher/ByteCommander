using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ByteCommander.Controls;
using ByteCommander.Models;
using ByteCommander.Services;

namespace ByteCommander;

public partial class MainWindow : Window
{
    readonly AppSettings settings;
    readonly List<AppCommand> commands = new();
    readonly Dictionary<Hotkey, AppCommand> keymap = new();
    readonly DispatcherTimer toastTimer;
    FilePanel active;
    int historyIndex;

    // Команди, на які посилаються меню й панель F-клавіш.
    AppCommand cmdPalette = null!, cmdRename = null!, cmdView = null!, cmdEdit = null!, cmdCopy = null!, cmdMove = null!,
        cmdMkDir = null!, cmdDelete = null!, cmdSearch = null!, cmdBookmarks = null!, cmdToggleBookmark = null!,
        cmdNewFile = null!, cmdPaste = null!, cmdRefresh = null!, cmdTerminal = null!, cmdExplorer = null!, cmdFolderProps = null!;

    FilePanel Other => active == LeftPanel ? RightPanel : LeftPanel;
    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public MainWindow()
    {
        InitializeComponent();
        settings = AppSettings.Load();
        FilePanel.ShowHidden = settings.ShowHidden;
        active = LeftPanel;
        historyIndex = settings.CommandHistory.Count;

        foreach (var p in new[] { LeftPanel, RightPanel })
        {
            p.Activated += (s, _) => SetActive((FilePanel)s!);
            p.PathChanged += (s, _) => { if (s == active) UpdatePrompt(); };
            p.BackgroundMenuRequested += ShowBackgroundMenu;
            p.RenameRequested += panel => { SetActive(panel); Rename(); };
            p.FilesDropped += (_, files, dir, move) => { var _t = Transfer(files, Util.AsDirTarget(dir), move); };
        }

        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        toastTimer.Tick += (_, _) =>
        {
            toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        BuildCommands();
        BuildFKeyBar();
        UpdateHiddenButton();
        RestoreWindowBounds();
    }

    // =====================================================================
    // Запуск / завершення
    // =====================================================================

    void RestoreWindowBounds()
    {
        if (settings.Window is { } b)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.IntersectsWith(new Rect(b.X, b.Y, b.Width, b.Height)) && b.Width > 400 && b.Height > 300)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = b.X;
                Top = b.Y;
                Width = b.Width;
                Height = b.Height;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var args = Environment.GetCommandLineArgs().Skip(1).Where(Directory.Exists).ToList();

        LeftPanel.Init(settings.Left, home);
        RightPanel.Init(settings.Right, sys);
        if (args.Count > 0) LeftPanel.Navigate(args[0]);
        if (args.Count > 1) RightPanel.Navigate(args[1]);

        var start = settings.RightPanelActive && args.Count == 0 ? RightPanel : LeftPanel;
        SetActive(start);
        start.FocusList();
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        settings.Left = LeftPanel.GetState();
        settings.Right = RightPanel.GetState();
        settings.RightPanelActive = active == RightPanel;
        settings.ShowHidden = FilePanel.ShowHidden;
        settings.Maximized = WindowState == WindowState.Maximized;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.Window = new WindowBounds { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
        if (settings.CommandHistory.Count > 100)
            settings.CommandHistory = settings.CommandHistory.Skip(settings.CommandHistory.Count - 100).ToList();
        settings.Save();
    }

    void SetActive(FilePanel p)
    {
        active = p;
        LeftPanel.IsActivePanel = p == LeftPanel;
        RightPanel.IsActivePanel = p == RightPanel;
        UpdatePrompt();
    }

    void UpdatePrompt() => PromptText.Text = active.CurrentPath + ">";

    void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    // =====================================================================
    // Команди — єдиний реєстр: клавіші, палітра, F-панель і меню беруться звідси
    // =====================================================================

    static Hotkey K(Key key, ModifierKeys mods = ModifierKeys.None) => Hotkey.Of(key, mods);
    const ModifierKeys Ctrl = ModifierKeys.Control, Alt = ModifierKeys.Alt, Shift = ModifierKeys.Shift,
        CtrlShift = ModifierKeys.Control | ModifierKeys.Shift, AltShift = ModifierKeys.Alt | ModifierKeys.Shift;

    AppCommand Add(string title, string glyph, Action run, params Hotkey[] keys) => Add(title, glyph, run, null, keys);

    AppCommand Add(string title, string glyph, Action run, string? panelKeys, params Hotkey[] keys)
    {
        var c = new AppCommand { Title = title, Glyph = glyph, Run = run, Keys = keys, PanelKeys = panelKeys };
        commands.Add(c);
        foreach (var k in keys)
        {
            if (!keymap.TryAdd(k, c))
                throw new InvalidOperationException($"Комбінацію {k} призначено двічі: «{keymap[k].Title}» і «{title}».");
        }
        return c;
    }

    void InPanel(FilePanel p, Action<FilePanel> action)
    {
        SetActive(p);
        p.FocusList();
        action(p);
    }

    void BuildCommands()
    {
        cmdView = Add("Перегляд файлу", "\uE890", View, K(Key.F3));
        cmdEdit = Add("Редагувати файл", "\uE70F", Edit, K(Key.F4));
        cmdNewFile = Add("Новий файл", "\uE8A5", NewFile, K(Key.F4, Shift));
        cmdCopy = Add("Копіювати", "\uE8C8", () => CopyMove(false), K(Key.F5));
        cmdMove = Add("Перемістити", "\uE8DE", () => CopyMove(true), K(Key.F6));
        cmdRename = Add("Перейменувати", "\uE8AC", Rename, K(Key.F2), K(Key.F6, Shift));
        cmdMkDir = Add("Новий каталог", "\uE8F4", MakeDir, K(Key.F7), K(Key.N, CtrlShift));
        cmdDelete = Add("Видалити в кошик", "\uE74D", () => Delete(false), K(Key.F8), K(Key.Delete));
        Add("Видалити назавжди", "\uE74D", () => Delete(true), K(Key.F8, Shift), K(Key.Delete, Shift));
        cmdSearch = Add("Пошук файлів", "\uE721", ShowSearch, K(Key.F7, Alt));
        Add("Запакувати в ZIP", "\uE7B8", Pack, K(Key.F5, Alt));
        Add("Розпакувати ZIP", "\uE7B8", Unpack, K(Key.F9, Alt));
        Add("Властивості", "\uE946", Properties, K(Key.Enter, Alt));
        cmdFolderProps = Add("Властивості поточного каталогу", "\uE946", () => NativeMethods.ShowProperties(active.CurrentPath, Hwnd));
        Add("Контекстне меню Провідника", "\uE712", () => active.ShowContextMenuForCurrent(), K(Key.F10, Shift), K(Key.Apps));
        Add("Розмір каталогу під курсором", "\uEDA2", () => { if (active.CurrentItem is { } it) active.CalcSize(it); }, "Space");
        Add("Розмір усіх каталогів", "\uEDA2", () => active.CalcAllSizes(), K(Key.Enter, AltShift));
        Add("Відкрити", "\uE8E5", () => active.OpenCurrent(), "Enter");

        Add("Нова вкладка", "\uE710", () => { active.NewTab(); active.FocusList(); }, K(Key.T, Ctrl));
        Add("Закрити вкладку", "\uE711", () => { active.CloseTab(); active.FocusList(); }, K(Key.W, Ctrl));
        Add("Наступна вкладка", "\uE72A", () => { active.NextTab(1); active.FocusList(); }, K(Key.Tab, Ctrl));
        Add("Попередня вкладка", "\uE72B", () => { active.NextTab(-1); active.FocusList(); }, K(Key.Tab, CtrlShift));
        Add("Інша панель", "\uE8AB", SwitchPanel, K(Key.Tab));

        Add("Назад", "\uE72B", () => active.GoBack(), K(Key.Left, Alt));
        Add("Вперед", "\uE72A", () => active.GoForward(), K(Key.Right, Alt));
        Add("Вгору", "\uE74A", () => active.GoUp(), "Backspace", K(Key.Up, Alt), K(Key.PageUp, Ctrl));
        Add("Увійти в каталог", "\uE76C", () => active.EnterCurrentDir(), K(Key.PageDown, Ctrl));
        Add("У корінь диска", "\uE80F", () => active.GoRoot(), K(Key.Oem5, Ctrl));
        Add("Вибір диска — ліва панель", "\uEDA2", () => InPanel(LeftPanel, p => p.ShowDriveMenu()), K(Key.F1, Alt));
        Add("Вибір диска — права панель", "\uEDA2", () => InPanel(RightPanel, p => p.ShowDriveMenu()), K(Key.F2, Alt));
        Add("Ввести шлях", "\uE8AC", () => active.BeginEditPath(), K(Key.L, Ctrl));
        cmdBookmarks = Add("Закладки", "\uE734", ShowBookmarks, K(Key.D, Ctrl));
        cmdToggleBookmark = Add("Додати / прибрати закладку", "\uE735", ToggleBookmark, K(Key.D, CtrlShift));

        cmdRefresh = Add("Оновити панель", "\uE72C", () => active.Refresh(), K(Key.R, Ctrl));
        Add("Показати / сховати приховані файли", "\uE7B3", ToggleHidden, K(Key.H, Ctrl));
        Add("Фільтр у панелі", "\uE71C", () => active.ShowFilter(), K(Key.F, Ctrl));
        Add("Поміняти панелі місцями", "\uE8AB", SwapPanels, K(Key.U, Ctrl));
        Add("Той самий каталог в іншій панелі", "\uE8A0", () => Other.Navigate(active.CurrentPath, active.CurrentItem?.Name), K(Key.OemPlus, Ctrl));
        Add("Сортувати за ім’ям", "\uE8CB", () => active.SetSort(SortKey.Name), K(Key.F3, Ctrl));
        Add("Сортувати за типом", "\uE8CB", () => active.SetSort(SortKey.Ext), K(Key.F4, Ctrl));
        Add("Сортувати за датою", "\uE8CB", () => active.SetSort(SortKey.Date), K(Key.F5, Ctrl));
        Add("Сортувати за розміром", "\uE8CB", () => active.SetSort(SortKey.Size), K(Key.F6, Ctrl));

        Add("Позначити файл", "\uE73E", () => active.ToggleMarkCurrent(), "Insert / Space / Shift+↑↓ / Ctrl+клік");
        Add("Виділити все", "\uE8B3", () => active.MarkAll(true), K(Key.A, Ctrl));
        Add("Зняти виділення", "\uE8E6", () => active.MarkAll(false), K(Key.A, CtrlShift));
        Add("Виділити за маскою", "\uE710", () => MarkByMask(true), K(Key.Add));
        Add("Зняти виділення за маскою", "\uE738", () => MarkByMask(false), K(Key.Subtract));
        Add("Інвертувати виділення", "\uE8AB", () => active.InvertMarks(), K(Key.Multiply));

        Add("Копіювати в буфер обміну", "\uE8C8", () => ClipboardCopy(false), K(Key.C, Ctrl));
        Add("Вирізати в буфер обміну", "\uE8C6", () => ClipboardCopy(true), K(Key.X, Ctrl));
        cmdPaste = Add("Вставити з буфера обміну", "\uE77F", ClipboardPaste, K(Key.V, Ctrl));
        Add("Копіювати повний шлях", "\uE71B", CopyPath, K(Key.C, CtrlShift));

        Add("Командний рядок", "\uE756", FocusCommandLine, K(Key.E, Ctrl));
        Add("Вставити ім’я файлу в командний рядок", "\uE756", PutNameToCommandLine, K(Key.Enter, Ctrl));
        cmdTerminal = Add("Термінал у поточному каталозі", "\uE756", OpenTerminal, K(Key.Oem3, Ctrl));
        cmdExplorer = Add("Відкрити в Провіднику", "\uEC50", OpenExplorer, K(Key.E, CtrlShift));
        cmdPalette = Add("Палітра команд", "\uE721", ShowPalette, K(Key.F1), K(Key.P, CtrlShift), K(Key.P, Ctrl));
        Add("Вихід", "\uE7E8", Close, K(Key.X, Alt));
    }

    void BuildFKeyBar()
    {
        (AppCommand cmd, string label)[] bar =
        {
            (cmdPalette, "Команди"), (cmdRename, "Перейм."), (cmdView, "Перегляд"), (cmdEdit, "Редагув."),
            (cmdCopy, "Копіювати"), (cmdMove, "Перемістити"), (cmdMkDir, "Каталог"), (cmdDelete, "Видалити"),
            (cmdSearch, "Пошук"), (cmdBookmarks, "Закладки"),
        };
        foreach (var (cmd, label) in bar)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = cmd.Keys[0].ToString(), Foreground = Util.Res("AccentBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Util.Res("TextDimBrush") });
            var b = new Button { Content = sp, Style = (Style)FindResource("FKeyButton"), ToolTip = cmd.Title };
            b.Click += (_, _) => cmd.Run();
            FKeyBar.Children.Add(b);
        }
    }

    static MenuItem MenuItemFor(AppCommand c, bool enabled = true) => Util.MakeMenuItem(c.Glyph, c.Title, c.Gesture, c.Run, enabled);

    /// <summary>Меню каталогу (правий клік на порожньому місці панелі).</summary>
    void ShowBackgroundMenu(FilePanel panel)
    {
        SetActive(panel);
        bool canPaste;
        try { canPaste = Clipboard.ContainsFileDropList(); } catch { canPaste = false; }
        var m = new ContextMenu();
        m.Items.Add(MenuItemFor(cmdMkDir));
        m.Items.Add(MenuItemFor(cmdNewFile));
        m.Items.Add(MenuItemFor(cmdPaste, canPaste));
        m.Items.Add(new Separator());
        m.Items.Add(MenuItemFor(cmdRefresh));
        m.Items.Add(MenuItemFor(cmdTerminal));
        m.Items.Add(MenuItemFor(cmdExplorer));
        m.Items.Add(MenuItemFor(cmdFolderProps));
        m.PlacementTarget = panel;
        m.Placement = PlacementMode.MousePoint;
        m.IsOpen = true;
    }

    // =====================================================================
    // Клавіатура
    // =====================================================================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var hotkey = Hotkey.Of(key, Keyboard.Modifiers);
        if (!keymap.TryGetValue(hotkey, out var cmd)) return;
        // У полях вводу працюють лише F-клавіші та палітра команд; решта — редагування тексту.
        if (Keyboard.FocusedElement is TextBox && !hotkey.IsFunctionKey && cmd != cmdPalette) return;

        e.Handled = true;
        // Модальні діалоги відкриваємо після обробки поточного натискання,
        // щоб символ клавіші (наприклад «+») не потрапив у поле вводу діалогу.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, cmd.Run);
    }

    void SwitchPanel()
    {
        var o = Other;
        SetActive(o);
        o.FocusList();
    }

    // =====================================================================
    // Командний рядок
    // =====================================================================

    void FocusCommandLine()
    {
        CommandLine.Focus();
        CommandLine.CaretIndex = CommandLine.Text.Length;
    }

    void PutNameToCommandLine()
    {
        if (active.CurrentItem is not { IsParent: false } it) return;
        var name = it.Name.Contains(' ') ? $"\"{it.Name}\"" : it.Name;
        if (CommandLine.Text.Length > 0 && !CommandLine.Text.EndsWith(' ')) CommandLine.Text += " ";
        CommandLine.Text += name + " ";
        FocusCommandLine();
    }

    void CommandLine_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var hist = settings.CommandHistory;
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = true;
                var c = CommandLine.Text;
                CommandLine.Text = "";
                RunCommandLine(c);
                break;
            case Key.Escape:
                e.Handled = true;
                if (CommandLine.Text.Length > 0) CommandLine.Text = "";
                else active.FocusList();
                break;
            case Key.Tab:
                e.Handled = true;
                active.FocusList();
                break;
            case Key.Up when hist.Count > 0:
                e.Handled = true;
                historyIndex = Math.Max(0, historyIndex - 1);
                CommandLine.Text = hist[historyIndex];
                CommandLine.CaretIndex = CommandLine.Text.Length;
                break;
            case Key.Down when hist.Count > 0:
                e.Handled = true;
                historyIndex = Math.Min(hist.Count, historyIndex + 1);
                CommandLine.Text = historyIndex < hist.Count ? hist[historyIndex] : "";
                CommandLine.CaretIndex = CommandLine.Text.Length;
                break;
        }
    }

    void RunCommandLine(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return;
        var hist = settings.CommandHistory;
        hist.Remove(cmd);
        hist.Add(cmd);
        historyIndex = hist.Count;
        var dir = active.CurrentPath;

        if (Regex.IsMatch(cmd, @"^[a-zA-Z]:$"))
        {
            active.Navigate(cmd + "\\");
            return;
        }
        var cd = Regex.Match(cmd, @"^cd(?:\s+/d)?(?:\s+|(?=[\\./]))(.+)$", RegexOptions.IgnoreCase);
        if (cd.Success)
        {
            var arg = Environment.ExpandEnvironmentVariables(cd.Groups[1].Value.Trim().Trim('"'));
            active.Navigate(Path.Combine(dir, arg));
            return;
        }

        try
        {
            var candidate = Path.Combine(dir, cmd.Trim('"'));
            if (Directory.Exists(candidate))
            {
                active.Navigate(candidate);
                return;
            }
            if (File.Exists(candidate))
            {
                Util.ShellOpen(candidate, dir);
                return;
            }
        }
        catch
        {
            // не шлях — виконуємо як команду
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k " + cmd) { WorkingDirectory = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    // =====================================================================
    // Файлові операції
    // =====================================================================

    void View()
    {
        if (active.CurrentItem is not { IsParent: false } it) return;
        if (it.IsDirectory)
        {
            active.CalcSize(it);
            return;
        }
        new ViewerWindow(it.FullPath, false) { Owner = this }.Show();
    }

    void Edit()
    {
        if (active.CurrentItem is not { IsParent: false, IsDirectory: false } it) return;
        new ViewerWindow(it.FullPath, true) { Owner = this }.Show();
    }

    void NewFile()
    {
        var name = Dialogs.Input(this, "Новий файл", "Ім’я нового файлу:", "новий.txt", selectStem: true)?.Trim();
        if (string.IsNullOrEmpty(name)) { active.FocusList(); return; }
        try
        {
            var full = Path.Combine(active.CurrentPath, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) File.WriteAllBytes(full, Array.Empty<byte>());
            active.Refresh();
            active.SelectByName(Path.GetFileName(full));
            new ViewerWindow(full, true) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    void MakeDir()
    {
        var suggestion = active.CurrentItem is { IsParent: false } cur && !cur.IsDirectory ? Path.GetFileNameWithoutExtension(cur.Name) : "";
        var name = Dialogs.Input(this, "Новий каталог", "Ім’я нового каталогу (можна вкладені: a\\b\\c):", suggestion)?.Trim();
        if (string.IsNullOrEmpty(name)) { active.FocusList(); return; }
        try
        {
            Directory.CreateDirectory(Path.Combine(active.CurrentPath, name));
            active.Refresh();
            active.SelectByName(name.Split('\\', '/')[0]);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    void Rename()
    {
        if (active.CurrentItem is not { IsParent: false } it) return;
        var newName = Dialogs.Input(this, "Перейменування", $"Нове ім’я для «{it.Name}»:", it.Name, selectStem: !it.IsDirectory)?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == it.Name) { active.FocusList(); return; }
        try
        {
            var dst = Path.Combine(Path.GetDirectoryName(it.FullPath)!, newName);
            if (it.IsDirectory)
            {
                if (string.Equals(it.Name, newName, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = it.FullPath + ".~" + Guid.NewGuid().ToString("N")[..6];
                    Directory.Move(it.FullPath, tmp);
                    Directory.Move(tmp, dst);
                }
                else Directory.Move(it.FullPath, dst);
            }
            else File.Move(it.FullPath, dst);
            active.Refresh();
            active.SelectByName(Path.GetFileName(dst));
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
        }
    }

    void CopyMove(bool move)
    {
        var targets = active.GetTargets();
        if (targets.Count == 0) return;
        var verb = move ? "Перемістити" : "Копіювати";
        var input = Dialogs.Input(this, verb, $"{verb} {Util.Describe(targets.Select(t => t.Name).ToList())} до:", Util.AsDirTarget(Other.CurrentPath));
        if (ResolveInputPath(input) is not { } full) { active.FocusList(); return; }
        _ = Transfer(targets.Select(t => t.FullPath).ToArray(), full, move);
    }

    /// <summary>
    /// Шлях, введений у діалозі, відносно активної панелі: розкриває %ЗМІННІ%, знімає лапки,
    /// зберігає кінцевий «\» (ознака каталогу). null — скасовано, порожньо або некоректний шлях.
    /// </summary>
    string? ResolveInputPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        try
        {
            var full = Path.Combine(active.CurrentPath, text);
            return text.EndsWith('\\') || text.EndsWith('/') ? Util.AsDirTarget(full) : full;
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, ex.Message);
            return null;
        }
    }

    Task<bool> Transfer(string[] sources, string dest, bool move) =>
        RunOperation(move ? "Переміщення" : "Копіювання", (op, ct) => op.Transfer(sources, dest, move, ct));

    async void Delete(bool permanent)
    {
        var targets = active.GetTargets();
        if (targets.Count == 0) return;
        var what = Util.Describe(targets.Select(t => t.Name).ToList());
        var ok = permanent
            ? Dialogs.Confirm(this, "Видалення назавжди", $"Видалити {what} НАЗАВЖДИ?\nЦю дію неможливо скасувати.", "Видалити назавжди")
            : Dialogs.Confirm(this, "Видалення", $"Перемістити {what} до кошика?", "До кошика");
        if (!ok) { active.FocusList(); return; }

        var paths = targets.Select(t => t.FullPath).ToList();
        var panel = active;
        if (permanent)
        {
            await RunOperation("Видалення", (op, ct) => op.DeletePermanent(paths, ct));
        }
        else
        {
            int rc = await Task.Run(() => NativeMethods.Recycle(paths));
            if (rc != 0 && rc != 0x4C7 /* скасовано */) Dialogs.Error(this, $"Не вдалося видалити (код 0x{rc:X}).");
            LeftPanel.Refresh();
            RightPanel.Refresh();
        }
        panel.FocusList();
    }

    /// <returns>true — операцію завершено повністю: без скасування, без фатальної помилки й без помилок по файлах.</returns>
    async Task<bool> RunOperation(string title, Action<FileOperation, CancellationToken> work)
    {
        var state = new ProgressState();
        using var cts = new CancellationTokenSource();
        var op = new FileOperation(state, AskConflict);
        var pw = new ProgressWindow(this, title, state, cts);
        pw.Start();
        string? fatal = null;
        try
        {
            await Task.Run(() => work(op, cts.Token));
        }
        catch (OperationCanceledException)
        {
            ShowToast("Операцію скасовано");
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }
        finally
        {
            pw.Finish();
        }

        if (fatal != null) Dialogs.Error(this, fatal);
        else if (op.Errors.Count > 0)
        {
            var list = string.Join("\n", op.Errors.Take(15));
            if (op.Errors.Count > 15) list += $"\n… і ще {op.Errors.Count - 15}";
            Dialogs.Error(this, $"{title}: завершено з помилками ({op.Errors.Count}):\n\n{list}");
        }
        else if (!cts.IsCancellationRequested)
        {
            ShowToast($"{title}: готово  ·  {Util.Count(state.DoneFiles, "файл", "файли", "файлів")}" +
                      (state.TotalBytes > 0 ? $", {Util.FormatSize(state.DoneBytes)}" : ""));
        }
        LeftPanel.Refresh();
        RightPanel.Refresh();
        active.FocusList();
        return fatal == null && op.Errors.Count == 0 && !cts.IsCancellationRequested;
    }

    ConflictChoice AskConflict(string src, string dst) => Dispatcher.Invoke(() =>
    {
        static string Info(string p)
        {
            try
            {
                var fi = new FileInfo(p);
                return fi.Exists ? $"{Util.FormatSize(fi.Length)},  {fi.LastWriteTime:dd.MM.yyyy HH:mm:ss}" : "";
            }
            catch
            {
                return "";
            }
        }
        var srcInfo = Info(src);
        var msg = $"Файл уже існує:\n{dst}\n\n" +
                  (srcInfo.Length > 0 ? $"Новий:        {srcInfo}\n" : "") +
                  $"Існуючий:   {Info(dst)}";
        int r = Dialogs.Ask(this, "Конфлікт імен", msg,
            new[] { "Замінити", "Замінити всі", "Пропустити", "Пропустити всі", "Перейменувати", "Скасувати" }, 0, 5);
        return r switch
        {
            0 => ConflictChoice.Overwrite,
            1 => ConflictChoice.OverwriteAll,
            2 => ConflictChoice.Skip,
            3 => ConflictChoice.SkipAll,
            4 => ConflictChoice.Rename,
            _ => ConflictChoice.Cancel,
        };
    });

    void Pack()
    {
        var targets = active.GetTargets();
        if (targets.Count == 0) return;
        var baseName = targets.Count == 1
            ? (targets[0].IsDirectory ? targets[0].Name : Path.GetFileNameWithoutExtension(targets[0].Name))
            : new DirectoryInfo(active.CurrentPath).Name.TrimEnd(':', '\\');
        if (baseName.Length == 0) baseName = "архів";
        var dest = Dialogs.Input(this, "Пакування", $"Запакувати {Util.Describe(targets.Select(t => t.Name).ToList())} в архів:",
            Path.Combine(Other.CurrentPath, baseName + ".zip"), selectStem: true);
        if (ResolveInputPath(dest) is not { } full) { active.FocusList(); return; }
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) full += ".zip";
        var paths = targets.Select(t => t.FullPath).ToList();
        _ = RunOperation("Пакування", (op, ct) => op.Pack(paths, full, ct));
    }

    void Unpack()
    {
        var zips = active.GetTargets().Where(t => !t.IsDirectory && t.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                         .Select(t => t.FullPath).ToList();
        if (zips.Count == 0)
        {
            ShowToast("Виберіть ZIP-архів(и) для розпакування");
            return;
        }
        var suggested = Util.AsDirTarget(zips.Count == 1
            ? Path.Combine(Other.CurrentPath, Path.GetFileNameWithoutExtension(zips[0]))
            : Other.CurrentPath);
        var dest = Dialogs.Input(this, "Розпакування", $"Розпакувати {Util.Describe(zips.Select(Path.GetFileName).ToList()!)} до:", suggested);
        if (ResolveInputPath(dest) is not { } full) { active.FocusList(); return; }
        _ = RunOperation("Розпакування", (op, ct) => op.Unpack(zips, full, ct));
    }

    void Properties()
    {
        var it = active.CurrentItem;
        NativeMethods.ShowProperties(it == null || it.IsParent ? active.CurrentPath : it.FullPath, Hwnd);
    }

    void MarkByMask(bool mark)
    {
        var mask = Dialogs.Input(this, mark ? "Виділити" : "Зняти виділення",
            mark ? "Виділити файли за маскою (наприклад *.jpg; *.png):" : "Зняти виділення за маскою:", "*.*");
        if (!string.IsNullOrWhiteSpace(mask)) active.MarkByMask(mask, mark);
        active.FocusList();
    }

    // =====================================================================
    // Буфер обміну
    // =====================================================================

    void ClipboardCopy(bool cut)
    {
        var targets = active.GetTargets();
        if (targets.Count == 0) return;
        var sc = new StringCollection();
        sc.AddRange(targets.Select(t => t.FullPath).ToArray());
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try
        {
            Clipboard.SetDataObject(data, true);
            ShowToast($"{(cut ? "Вирізано" : "Скопійовано")} у буфер: {Util.Describe(targets.Select(t => t.Name).ToList())}");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Буфер обміну зайнятий: " + ex.Message);
        }
    }

    void ClipboardPaste()
    {
        string[] files;
        bool move = false;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.Read(b, 0, 4);
                move = (BitConverter.ToInt32(b, 0) & 2) != 0;
            }
        }
        catch
        {
            return;
        }
        if (files.Length == 0) return;
        _ = PasteAsync(files, move);
    }

    async Task PasteAsync(string[] files, bool move)
    {
        bool completed = await Transfer(files, Util.AsDirTarget(active.CurrentPath), move);
        // «Вирізати» знімаємо з буфера лише після повного успіху — інакше користувач може повторити вставку.
        if (move && completed)
        {
            try { Clipboard.Clear(); } catch { }
        }
    }

    void CopyPath()
    {
        var targets = active.GetTargets();
        var text = targets.Count == 0 ? active.CurrentPath : string.Join(Environment.NewLine, targets.Select(t => t.FullPath));
        try
        {
            Clipboard.SetText(text);
            ShowToast(targets.Count > 1 ? $"Скопійовано {targets.Count} шляхів" : "Шлях скопійовано: " + text);
        }
        catch { }
    }

    // =====================================================================
    // Інше
    // =====================================================================

    void ToggleHidden()
    {
        FilePanel.ShowHidden = !FilePanel.ShowHidden;
        UpdateHiddenButton();
        LeftPanel.Refresh();
        RightPanel.Refresh();
        ShowToast(FilePanel.ShowHidden ? "Приховані файли показано" : "Приховані файли сховано");
    }

    void UpdateHiddenButton()
    {
        HiddenButton.Content = FilePanel.ShowHidden ? "\uE7B3" : "\uED1A";
        HiddenButton.Foreground = Util.Res(FilePanel.ShowHidden ? "AccentBrush" : "TextDimBrush");
    }

    void SwapPanels()
    {
        var l = LeftPanel.CurrentPath;
        var r = RightPanel.CurrentPath;
        LeftPanel.Navigate(r);
        RightPanel.Navigate(l);
        active.FocusList();
    }

    void OpenTerminal()
    {
        var dir = active.CurrentPath;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{(dir.EndsWith('\\') ? dir + "." : dir)}\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, ex.Message);
            }
        }
    }

    void OpenExplorer()
    {
        var it = active.CurrentItem;
        if (it == null || it.IsParent) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{active.CurrentPath}\"") { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.FullPath}\"") { UseShellExecute = true });
    }

    void ShowSearch() => new SearchWindow(this, active.CurrentPath).Show();

    public void GoToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null) return;
        if (active.Navigate(dir, Path.GetFileName(path))) active.FocusList();
    }

    void ShowPalette()
    {
        var entries = commands.Select(c => new PaletteEntry(c.Title, c.Gesture, c.Glyph, c.Run)).ToList();
        foreach (var b in settings.Bookmarks)
        {
            var p = b;
            entries.Add(new PaletteEntry("Перейти: " + p, "закладка", "\uE734", () => { active.Navigate(p); active.FocusList(); }));
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            var root = d.Name;
            entries.Add(new PaletteEntry("Диск " + root, "", "\uEDA2", () => { active.Navigate(root); active.FocusList(); }));
        }
        new PaletteWindow(this, entries).Show();
    }

    void ShowBookmarks()
    {
        var m = new ContextMenu();
        int n = 1;
        foreach (var b in settings.Bookmarks)
        {
            var path = b;
            var mi = new MenuItem
            {
                Header = (n <= 9 ? $"_{n}   " : "     ") + Util.EscapeAccess(path),
                Icon = Util.Glyph("\uE734"),
                IsEnabled = Directory.Exists(path),
            };
            mi.Click += (_, _) =>
            {
                active.Navigate(path);
                active.FocusList();
            };
            m.Items.Add(mi);
            n++;
        }
        if (settings.Bookmarks.Count > 0) m.Items.Add(new Separator());
        bool has = settings.Bookmarks.Contains(active.CurrentPath, StringComparer.OrdinalIgnoreCase);
        m.Items.Add(Util.MakeMenuItem(has ? "\uE735" : "\uE734",
            has ? "Прибрати поточний каталог із закладок" : "Додати поточний каталог до закладок", cmdToggleBookmark.Gesture, ToggleBookmark));
        m.PlacementTarget = active;
        m.Placement = PlacementMode.Relative;
        m.HorizontalOffset = 60;
        m.VerticalOffset = 80;
        m.IsOpen = true;
    }

    void ToggleBookmark()
    {
        var p = active.CurrentPath;
        var existing = settings.Bookmarks.FirstOrDefault(b => string.Equals(b, p, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            settings.Bookmarks.Remove(existing);
            ShowToast("Закладку прибрано: " + p);
        }
        else
        {
            settings.Bookmarks.Add(p);
            ShowToast("Закладку додано: " + p);
        }
        settings.Save();
    }

    // ---------- Кнопки панелі інструментів ----------
    void Back_Click(object s, RoutedEventArgs e) => active.GoBack();
    void Forward_Click(object s, RoutedEventArgs e) => active.GoForward();
    void Up_Click(object s, RoutedEventArgs e) => active.GoUp();
    void Refresh_Click(object s, RoutedEventArgs e) => active.Refresh();
    void MkDir_Click(object s, RoutedEventArgs e) => MakeDir();
    void Copy_Click(object s, RoutedEventArgs e) => CopyMove(false);
    void Move_Click(object s, RoutedEventArgs e) => CopyMove(true);
    void Rename_Click(object s, RoutedEventArgs e) => Rename();
    void Delete_Click(object s, RoutedEventArgs e) => Delete(false);
    void Search_Click(object s, RoutedEventArgs e) => ShowSearch();
    void Bookmarks_Click(object s, RoutedEventArgs e) => ShowBookmarks();
    void Pack_Click(object s, RoutedEventArgs e) => Pack();
    void Swap_Click(object s, RoutedEventArgs e) => SwapPanels();
    void Hidden_Click(object s, RoutedEventArgs e) => ToggleHidden();
    void Terminal_Click(object s, RoutedEventArgs e) => OpenTerminal();
    void Palette_Click(object s, RoutedEventArgs e) => ShowPalette();
}
