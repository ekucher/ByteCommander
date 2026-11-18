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

public sealed record AppCommand(string Title, string Gesture, string Glyph, Action Run);

public partial class MainWindow : Window
{
    readonly AppSettings settings;
    readonly List<AppCommand> commands = new();
    readonly DispatcherTimer toastTimer;
    FilePanel active;
    int historyIndex;

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
            p.CommandRequested += OnPanelCommand;
            p.FilesDropped += (_, files, dir, move) => { var _t = Transfer(files, dir.TrimEnd('\\') + "\\", move); };
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
        if (settings.WinW is double w && settings.WinH is double h && settings.WinX is double x && settings.WinY is double y)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.IntersectsWith(new Rect(x, y, w, h)) && w > 400 && h > 300)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x;
                Top = y;
                Width = w;
                Height = h;
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

        var start = settings.ActiveSide == "Right" && args.Count == 0 ? RightPanel : LeftPanel;
        SetActive(start);
        start.FocusList();
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        settings.Left = LeftPanel.GetState();
        settings.Right = RightPanel.GetState();
        settings.ActiveSide = active == RightPanel ? "Right" : "Left";
        settings.ShowHidden = FilePanel.ShowHidden;
        settings.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.WinX = b.X;
        settings.WinY = b.Y;
        settings.WinW = b.Width;
        settings.WinH = b.Height;
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
    // Команди
    // =====================================================================

    void Add(string title, string gesture, string glyph, Action run) => commands.Add(new AppCommand(title, gesture, glyph, run));

    void BuildCommands()
    {
        Add("Перегляд файлу", "F3", "\uE890", View);
        Add("Редагувати файл", "F4", "\uE70F", Edit);
        Add("Новий файл", "Shift+F4", "\uE8A5", NewFile);
        Add("Копіювати", "F5", "\uE8C8", () => CopyMove(false));
        Add("Перемістити", "F6", "\uE8DE", () => CopyMove(true));
        Add("Перейменувати", "F2 / Shift+F6", "\uE8AC", Rename);
        Add("Новий каталог", "F7", "\uE8F4", MakeDir);
        Add("Видалити в кошик", "F8 / Del", "\uE74D", () => Delete(false));
        Add("Видалити назавжди", "Shift+Del", "\uE74D", () => Delete(true));
        Add("Пошук файлів", "Alt+F7", "\uE721", ShowSearch);
        Add("Запакувати в ZIP", "Alt+F5", "\uE7B8", Pack);
        Add("Розпакувати ZIP", "Alt+F9", "\uE7B8", Unpack);
        Add("Властивості", "Alt+Enter", "\uE946", Properties);
        Add("Контекстне меню Провідника", "Shift+F10", "\uE712", () => active.ShowContextMenuForCurrent());
        Add("Розмір каталогу під курсором", "Space", "\uEDA2", () => { if (active.CurrentItem is { } it) active.CalcSize(it); });
        Add("Розмір усіх каталогів", "Alt+Shift+Enter", "\uEDA2", () => active.CalcAllSizes());

        Add("Нова вкладка", "Ctrl+T", "\uE710", () => { active.NewTab(); active.FocusList(); });
        Add("Закрити вкладку", "Ctrl+W", "\uE711", () => { active.CloseTab(); active.FocusList(); });
        Add("Наступна вкладка", "Ctrl+Tab", "\uE72A", () => { active.NextTab(1); active.FocusList(); });
        Add("Попередня вкладка", "Ctrl+Shift+Tab", "\uE72B", () => { active.NextTab(-1); active.FocusList(); });

        Add("Назад", "Alt+←", "\uE72B", () => active.GoBack());
        Add("Вперед", "Alt+→", "\uE72A", () => active.GoForward());
        Add("Вгору", "Backspace", "\uE74A", () => active.GoUp());
        Add("У корінь диска", "Ctrl+\\", "\uE80F", () => active.GoRoot());
        Add("Вибір диска — ліва панель", "Alt+F1", "\uEDA2", () => { SetActive(LeftPanel); LeftPanel.FocusList(); LeftPanel.ShowDriveMenu(); });
        Add("Вибір диска — права панель", "Alt+F2", "\uEDA2", () => { SetActive(RightPanel); RightPanel.FocusList(); RightPanel.ShowDriveMenu(); });
        Add("Ввести шлях", "Ctrl+L", "\uE8AC", () => active.BeginEditPath());
        Add("Закладки", "Ctrl+D", "\uE734", ShowBookmarks);
        Add("Додати / прибрати закладку", "Ctrl+Shift+D", "\uE735", ToggleBookmark);

        Add("Оновити панель", "Ctrl+R", "\uE72C", () => active.Refresh());
        Add("Показати / сховати приховані файли", "Ctrl+H", "\uE7B3", ToggleHidden);
        Add("Фільтр у панелі", "Ctrl+F", "\uE71C", () => active.ShowFilter());
        Add("Поміняти панелі місцями", "Ctrl+U", "\uE8AB", SwapPanels);
        Add("Той самий каталог в іншій панелі", "Ctrl+=", "\uE8A0", () => Other.Navigate(active.CurrentPath, active.CurrentItem?.Name));
        Add("Сортувати за ім’ям", "Ctrl+F3", "\uE8CB", () => active.SetSort("Name"));
        Add("Сортувати за типом", "Ctrl+F4", "\uE8CB", () => active.SetSort("Ext"));
        Add("Сортувати за датою", "Ctrl+F5", "\uE8CB", () => active.SetSort("Date"));
        Add("Сортувати за розміром", "Ctrl+F6", "\uE8CB", () => active.SetSort("Size"));

        Add("Виділити все", "Ctrl+A", "\uE8B3", () => active.MarkAll(true));
        Add("Зняти виділення", "Ctrl+Shift+A", "\uE8E6", () => active.MarkAll(false));
        Add("Виділити за маскою", "Num +", "\uE710", () => MarkByMask(true));
        Add("Зняти виділення за маскою", "Num −", "\uE738", () => MarkByMask(false));
        Add("Інвертувати виділення", "Num *", "\uE8AB", () => active.InvertMarks());

        Add("Копіювати в буфер обміну", "Ctrl+C", "\uE8C8", () => ClipboardCopy(false));
        Add("Вирізати в буфер обміну", "Ctrl+X", "\uE8C6", () => ClipboardCopy(true));
        Add("Вставити з буфера обміну", "Ctrl+V", "\uE77F", ClipboardPaste);
        Add("Копіювати повний шлях", "Ctrl+Shift+C", "\uE71B", CopyPath);

        Add("Командний рядок", "Ctrl+E", "\uE756", FocusCommandLine);
        Add("Термінал у поточному каталозі", "Ctrl+`", "\uE756", OpenTerminal);
        Add("Відкрити в Провіднику", "Ctrl+Shift+E", "\uEC50", OpenExplorer);
        Add("Палітра команд", "Ctrl+Shift+P / F1", "\uE721", ShowPalette);
        Add("Вихід", "Alt+X", "\uE7E8", Close);
    }

    void BuildFKeyBar()
    {
        (string key, string label, Action run)[] keys =
        {
            ("F1", "Команди", ShowPalette),
            ("F2", "Перейм.", Rename),
            ("F3", "Перегляд", View),
            ("F4", "Редагув.", Edit),
            ("F5", "Копіювати", () => CopyMove(false)),
            ("F6", "Перемістити", () => CopyMove(true)),
            ("F7", "Каталог", MakeDir),
            ("F8", "Видалити", () => Delete(false)),
            ("Alt+F7", "Пошук", ShowSearch),
            ("Ctrl+D", "Закладки", ShowBookmarks),
        };
        foreach (var (key, label, run) in keys)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = key, Foreground = Util.Res("AccentBrush"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = Util.Res("TextDimBrush") });
            var b = new Button { Content = sp, Style = (Style)FindResource("FKeyButton") };
            b.Click += (_, _) => run();
            FKeyBar.Children.Add(b);
        }
    }

    void OnPanelCommand(FilePanel panel, string cmd)
    {
        SetActive(panel);
        switch (cmd)
        {
            case "mkdir": MakeDir(); break;
            case "newfile": NewFile(); break;
            case "paste": ClipboardPaste(); break;
            case "rename": Rename(); break;
            case "terminal": OpenTerminal(); break;
            case "explorer": OpenExplorer(); break;
            case "folderprops": NativeMethods.ShowProperties(panel.CurrentPath, Hwnd); break;
        }
    }

    // =====================================================================
    // Клавіатура
    // =====================================================================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool isF = key >= Key.F1 && key <= Key.F12;
        bool inText = Keyboard.FocusedElement is TextBox;
        if (inText && !isF && !(m == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.P)) return;

        Action? a = null;
        if (m == ModifierKeys.None)
        {
            a = key switch
            {
                Key.F1 => ShowPalette,
                Key.F2 => Rename,
                Key.F3 => View,
                Key.F4 => Edit,
                Key.F5 => () => CopyMove(false),
                Key.F6 => () => CopyMove(true),
                Key.F7 => MakeDir,
                Key.F8 or Key.Delete => () => Delete(false),
                Key.Tab => SwitchPanel,
                Key.Apps => () => active.ShowContextMenuForCurrent(),
                Key.Add => () => MarkByMask(true),
                Key.Subtract => () => MarkByMask(false),
                Key.Multiply => () => active.InvertMarks(),
                _ => null,
            };
        }
        else if (m == ModifierKeys.Shift)
        {
            a = key switch
            {
                Key.F4 => NewFile,
                Key.F6 => Rename,
                Key.F8 or Key.Delete => () => Delete(true),
                Key.F10 => () => active.ShowContextMenuForCurrent(),
                _ => null,
            };
        }
        else if (m == ModifierKeys.Alt)
        {
            a = key switch
            {
                Key.F1 => () => { SetActive(LeftPanel); LeftPanel.FocusList(); LeftPanel.ShowDriveMenu(); },
                Key.F2 => () => { SetActive(RightPanel); RightPanel.FocusList(); RightPanel.ShowDriveMenu(); },
                Key.F5 => Pack,
                Key.F7 => ShowSearch,
                Key.F9 => Unpack,
                Key.Enter => Properties,
                Key.Left => () => active.GoBack(),
                Key.Right => () => active.GoForward(),
                Key.Up => () => active.GoUp(),
                Key.X => Close,
                _ => null,
            };
        }
        else if (m == ModifierKeys.Control)
        {
            a = key switch
            {
                Key.T => () => { active.NewTab(); active.FocusList(); },
                Key.W => () => { active.CloseTab(); active.FocusList(); },
                Key.Tab => () => { active.NextTab(1); active.FocusList(); },
                Key.R => () => active.Refresh(),
                Key.H => ToggleHidden,
                Key.F => () => active.ShowFilter(),
                Key.L => () => active.BeginEditPath(),
                Key.U => SwapPanels,
                Key.D => ShowBookmarks,
                Key.E => FocusCommandLine,
                Key.A => () => active.MarkAll(true),
                Key.C => () => ClipboardCopy(false),
                Key.X => () => ClipboardCopy(true),
                Key.V => ClipboardPaste,
                Key.P => ShowPalette,
                Key.Enter => PutNameToCommandLine,
                Key.PageUp => () => active.GoUp(),
                Key.PageDown => () => active.EnterCurrentDir(),
                Key.OemPlus => () => Other.Navigate(active.CurrentPath, active.CurrentItem?.Name),
                Key.Oem3 => OpenTerminal,
                Key.Oem5 => () => active.GoRoot(),
                Key.F3 => () => active.SetSort("Name"),
                Key.F4 => () => active.SetSort("Ext"),
                Key.F5 => () => active.SetSort("Date"),
                Key.F6 => () => active.SetSort("Size"),
                _ => null,
            };
        }
        else if (m == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            a = key switch
            {
                Key.Tab => () => { active.NextTab(-1); active.FocusList(); },
                Key.P => ShowPalette,
                Key.D => ToggleBookmark,
                Key.A => () => active.MarkAll(false),
                Key.C => CopyPath,
                Key.E => OpenExplorer,
                Key.N => MakeDir,
                _ => null,
            };
        }
        else if (m == (ModifierKeys.Alt | ModifierKeys.Shift))
        {
            if (key == Key.Enter) a = () => active.CalcAllSizes();
        }

        if (a == null) return;
        e.Handled = true;
        // Модальні діалоги відкриваємо після обробки поточного натискання,
        // щоб символ клавіші (наприклад «+») не потрапив у поле вводу діалогу.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, a);
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
        var dest = Other.CurrentPath.TrimEnd('\\') + "\\";
        var input = Dialogs.Input(this, verb, $"{verb} {Util.Describe(targets.Select(t => t.Name).ToList())} до:", dest);
        if (string.IsNullOrWhiteSpace(input)) { active.FocusList(); return; }
        input = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        string full;
        try { full = Path.Combine(active.CurrentPath, input); }
        catch (Exception ex) { Dialogs.Error(this, ex.Message); return; }
        if (input.EndsWith('\\') || input.EndsWith('/')) full = full.TrimEnd('\\', '/') + "\\";
        _ = Transfer(targets.Select(t => t.FullPath).ToArray(), full, move);
    }

    Task Transfer(string[] sources, string dest, bool move) =>
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

    async Task RunOperation(string title, Action<FileOperation, CancellationToken> work)
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
        if (string.IsNullOrWhiteSpace(dest)) { active.FocusList(); return; }
        var full = Path.Combine(active.CurrentPath, dest.Trim().Trim('"'));
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
        var suggested = zips.Count == 1
            ? Path.Combine(Other.CurrentPath, Path.GetFileNameWithoutExtension(zips[0])) + "\\"
            : Other.CurrentPath.TrimEnd('\\') + "\\";
        var dest = Dialogs.Input(this, "Розпакування", $"Розпакувати {Util.Describe(zips.Select(Path.GetFileName).ToList()!)} до:", suggested);
        if (string.IsNullOrWhiteSpace(dest)) { active.FocusList(); return; }
        var full = Path.Combine(active.CurrentPath, dest.Trim().Trim('"'));
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
        await Transfer(files, active.CurrentPath.TrimEnd('\\') + "\\", move);
        if (move)
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
            has ? "Прибрати поточний каталог із закладок" : "Додати поточний каталог до закладок", "Ctrl+Shift+D", ToggleBookmark));
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
