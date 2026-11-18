using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ByteCommander.Models;
using ByteCommander.Services;

namespace ByteCommander.Controls;

/// <summary>Одна файлова панель: вкладки, хлібні крихти, список, позначення, швидкий пошук, фільтр, drag&amp;drop.</summary>
public partial class FilePanel : UserControl
{
    public static bool ShowHidden;

    public ObservableCollection<PanelTab> Tabs { get; } = new();
    public PanelTab ActiveTab { get; private set; } = new();
    public string CurrentPath => ActiveTab.Location;
    public FileItem? CurrentItem => List.SelectedItem as FileItem;

    public event EventHandler? Activated;
    public event EventHandler? PathChanged;
    /// <summary>Правий клік на порожньому місці / «..» — меню каталогу будує головне вікно зі своїх команд.</summary>
    public event Action<FilePanel>? BackgroundMenuRequested;
    /// <summary>У контекстному меню Провідника вибрано «Перейменувати».</summary>
    public event Action<FilePanel>? RenameRequested;
    /// <summary>(панель, файли, каталог призначення, переміщення?)</summary>
    public event Action<FilePanel, string[], string, bool>? FilesDropped;

    List<FileItem> allItems = new();
    ObservableCollection<FileItem> items = new();
    string displayedPath = "";
    SortKey sortBy = SortKey.Name;
    bool sortDesc;
    readonly Dictionary<SortKey, (GridViewColumn column, string title)> sortColumns;
    string filter = "";
    bool suppressFilter;
    FileSystemWatcher? watcher;
    readonly DispatcherTimer refreshTimer;
    readonly DispatcherTimer quickTimer;
    string quick = "";
    bool isActivePanel;
    Point dragStart;
    FileItem? dragItem;
    static FilePanel? dragSource;

    public FilePanel()
    {
        InitializeComponent();
        TabsList.ItemsSource = Tabs;
        sortColumns = new()
        {
            [SortKey.Name] = (ColName, "Ім’я"),
            [SortKey.Ext] = (ColExt, "Тип"),
            [SortKey.Size] = (ColSize, "Розмір"),
            [SortKey.Date] = (ColDate, "Змінено"),
        };
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            Refresh();
        };
        quickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        quickTimer.Tick += (_, _) => ClearQuickSearch();
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Activated?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsActivePanel
    {
        get => isActivePanel;
        set
        {
            isActivePanel = value;
            Root.SetResourceReference(Border.BorderBrushProperty, value ? "AccentBrush" : "LineBrush");
        }
    }

    Window? Owner => Window.GetWindow(this);

    // =====================================================================
    // Стан
    // =====================================================================

    public void Init(PanelState state, string fallback)
    {
        sortBy = Enum.IsDefined(state.SortBy) ? state.SortBy : SortKey.Name;
        sortDesc = state.SortDesc;
        foreach (var p in state.Tabs)
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                Tabs.Add(new PanelTab { Location = p });
        if (Tabs.Count == 0) Tabs.Add(new PanelTab { Location = fallback });
        UpdateHeaders();
        ActivateTab(Tabs[Math.Clamp(state.ActiveTab, 0, Tabs.Count - 1)]);
    }

    public PanelState GetState() => new()
    {
        Tabs = Tabs.Select(t => t.Location).ToList(),
        ActiveTab = Math.Max(0, Tabs.IndexOf(ActiveTab)),
        SortBy = sortBy,
        SortDesc = sortDesc,
    };

    // =====================================================================
    // Навігація
    // =====================================================================

    public bool Navigate(string path, string? selectName = null, bool addHistory = true)
    {
        string full;
        List<FileItem> list;
        try
        {
            full = Util.NormalizeDir(path);
            list = LoadItems(full);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, $"Не вдалося відкрити «{path}»:\n{ex.Message}");
            return false;
        }

        bool focus = IsKeyboardFocusWithin;
        var oldTabPath = ActiveTab.Location;
        if (addHistory && oldTabPath.Length > 0 && !string.Equals(oldTabPath, full, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab.Back.Push(oldTabPath);
            ActiveTab.Forward.Clear();
        }
        bool changed = !string.Equals(displayedPath, full, StringComparison.OrdinalIgnoreCase);
        ActiveTab.Location = full;
        displayedPath = full;

        if (changed)
        {
            suppressFilter = true;
            FilterBox.Text = "";
            suppressFilter = false;
            filter = "";
            FilterBar.Visibility = Visibility.Collapsed;
            ClearQuickSearch();
            SetupWatcher(full);
        }

        allItems = list;
        Apply(selectName, focus);
        UpdatePathBar();
        UpdateStatus();
        PathChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    List<FileItem> LoadItems(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) throw new DirectoryNotFoundException("Каталог не існує.");

        // Перевірка доступу (EnumerationOptions.IgnoreInaccessible приховав би помилку для самого каталогу).
        using (var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) probe.MoveNext();

        var result = new List<FileItem>();
        if (di.Parent != null)
            result.Add(new FileItem { Name = "..", FullPath = di.Parent.FullName, IsDirectory = true, IsParent = true, Icon = ShellIcons.Get("", true) });

        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        foreach (var fsi in di.EnumerateFileSystemInfos("*", opts))
        {
            var attrs = fsi.Attributes;
            if (!ShowHidden && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            bool isDir = (attrs & FileAttributes.Directory) != 0;
            result.Add(new FileItem
            {
                Name = fsi.Name,
                FullPath = fsi.FullName,
                IsDirectory = isDir,
                Size = isDir ? 0 : ((FileInfo)fsi).Length,
                Modified = fsi.LastWriteTime,
                Attributes = attrs,
                Icon = ShellIcons.Get(fsi.FullName, isDir),
            });
        }
        return result;
    }

    void Apply(string? selectName, bool focus, int fallbackIndex = 0)
    {
        var parent = allItems.FirstOrDefault(i => i.IsParent);
        var sorted = allItems.Where(i => !i.IsParent &&
                                         (filter.Length == 0 || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                             .ToList();
        sorted.Sort(Compare);
        if (parent != null) sorted.Insert(0, parent);
        items = new ObservableCollection<FileItem>(sorted);
        List.ItemsSource = items;

        int idx = fallbackIndex;
        if (selectName != null)
        {
            int i = sorted.FindIndex(x => string.Equals(x.Name, selectName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) idx = i;
        }
        Select(idx, focus);
    }

    int Compare(FileItem a, FileItem b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        int r = sortBy switch
        {
            SortKey.Ext => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            SortKey.Size => a.EffectiveSize.CompareTo(b.EffectiveSize),
            SortKey.Date => a.Modified.CompareTo(b.Modified),
            _ => 0,
        };
        if (r == 0) r = NativeMethods.StrCmpLogicalW(a.Name, b.Name);
        return sortDesc ? -r : r;
    }

    public void Refresh()
    {
        if (displayedPath.Length == 0) return;
        var curName = CurrentItem?.Name;
        int curIdx = Math.Max(List.SelectedIndex, 0);
        var marked = items.Where(i => i.IsMarked).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sizes = allItems.Where(i => i.IsDirectory && i.DirSize is not null)
                            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First().DirSize, StringComparer.OrdinalIgnoreCase);
        bool focus = List.IsKeyboardFocusWithin;

        List<FileItem> list;
        try
        {
            list = LoadItems(displayedPath);
        }
        catch
        {
            // Каталог зник — піднімаємось до найближчого існуючого.
            string? p = displayedPath;
            while (p != null && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
            Navigate(p ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, false);
            return;
        }

        foreach (var i in list)
        {
            if (marked.Contains(i.Name)) i.IsMarked = true;
            if (sizes.TryGetValue(i.Name, out var s)) i.DirSize = s;
        }
        allItems = list;
        Apply(curName, focus, curIdx);
        UpdateFree();
        UpdateStatus();
    }

    void SetupWatcher(string path)
    {
        watcher?.Dispose();
        watcher = null;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            void Changed() => Dispatcher.InvokeAsync(ScheduleRefresh);
            w.Created += (_, _) => Changed();
            w.Deleted += (_, _) => Changed();
            w.Changed += (_, _) => Changed();
            w.Renamed += (_, _) => Changed();
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch
        {
            // мережеві/віртуальні шляхи можуть не підтримувати спостереження
        }
    }

    void ScheduleRefresh()
    {
        if (!refreshTimer.IsEnabled) refreshTimer.Start();
    }

    public void OpenCurrent()
    {
        var it = CurrentItem;
        if (it == null) return;
        if (it.IsParent) GoUp();
        else if (it.IsDirectory) Navigate(it.FullPath);
        else Util.ShellOpen(it.FullPath, CurrentPath);
    }

    public void EnterCurrentDir()
    {
        var it = CurrentItem;
        if (it == null) return;
        if (it.IsParent) GoUp();
        else if (it.IsDirectory) Navigate(it.FullPath);
    }

    public void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent == null) return;
        Navigate(parent.FullName, new DirectoryInfo(CurrentPath).Name);
    }

    public void GoRoot()
    {
        var r = Path.GetPathRoot(CurrentPath);
        if (r != null) Navigate(r);
    }

    public void GoBack() => HistoryStep(ActiveTab.Back, ActiveTab.Forward);
    public void GoForward() => HistoryStep(ActiveTab.Forward, ActiveTab.Back);

    void HistoryStep(Stack<string> from, Stack<string> to)
    {
        while (from.Count > 0)
        {
            var target = from.Pop();
            var cur = CurrentPath;
            string? sel = Util.PathEquals(Path.GetDirectoryName(cur) ?? "", target) ? Path.GetFileName(cur) : null;
            if (Navigate(target, sel, addHistory: false))
            {
                to.Push(cur);
                return;
            }
        }
    }

    // =====================================================================
    // Вкладки
    // =====================================================================

    void ActivateTab(PanelTab tab)
    {
        if (Tabs.Contains(ActiveTab)) ActiveTab.SelectedName = CurrentItem?.Name;
        foreach (var t in Tabs) t.IsActive = t == tab;
        ActiveTab = tab;
        if (!Navigate(tab.Location, tab.SelectedName, addHistory: false))
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, false);
    }

    public void NewTab(string? path = null, bool activate = true)
    {
        var t = new PanelTab { Location = path ?? CurrentPath };
        Tabs.Insert(Tabs.IndexOf(ActiveTab) + 1, t);
        if (activate) ActivateTab(t);
    }

    public void CloseTab(PanelTab? tab = null)
    {
        tab ??= ActiveTab;
        if (Tabs.Count <= 1) return;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab == ActiveTab) ActivateTab(Tabs[Math.Min(idx, Tabs.Count - 1)]);
    }

    public void NextTab(int dir)
    {
        if (Tabs.Count < 2) return;
        int i = (Tabs.IndexOf(ActiveTab) + dir + Tabs.Count) % Tabs.Count;
        ActivateTab(Tabs[i]);
    }

    void NewTab_Click(object sender, RoutedEventArgs e)
    {
        NewTab();
        FocusList();
    }

    void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PanelTab t) return;
        if (t != ActiveTab) ActivateTab(t);
        FocusList();
        e.Handled = true;
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is PanelTab t)
        {
            CloseTab(t);
            e.Handled = true;
        }
    }

    void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PanelTab t) CloseTab(t);
    }

    // =====================================================================
    // Курсор і фокус
    // =====================================================================

    void Select(int idx, bool focus)
    {
        if (items.Count == 0) return;
        idx = Math.Clamp(idx, 0, items.Count - 1);
        List.SelectedIndex = idx;
        List.ScrollIntoView(items[idx]);
        if (focus) FocusCurrent();
    }

    void FocusCurrent()
    {
        if (List.SelectedIndex < 0)
        {
            List.Focus();
            return;
        }
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) is ListViewItem c) c.Focus();
        else List.Focus();
    }

    public void FocusList()
    {
        if (List.SelectedIndex < 0 && items.Count > 0) List.SelectedIndex = 0;
        FocusCurrent();
    }

    void MoveCursor(int delta) => Select(List.SelectedIndex + delta, true);

    public void SelectByName(string name, bool focus = true)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Select(i, focus);
                return;
            }
        }
        if (focus) FocusList();
    }

    // =====================================================================
    // Позначення
    // =====================================================================

    /// <summary>Позначені елементи, а якщо їх немає — елемент під курсором.</summary>
    public List<FileItem> GetTargets()
    {
        var marked = items.Where(i => i.IsMarked).ToList();
        if (marked.Count > 0) return marked;
        var c = CurrentItem;
        return c == null || c.IsParent ? new List<FileItem>() : new List<FileItem> { c };
    }

    void ToggleMark(FileItem? it)
    {
        if (it == null || it.IsParent) return;
        it.IsMarked = !it.IsMarked;
        UpdateStatus();
    }

    public void MarkAll(bool mark)
    {
        foreach (var i in items) if (!i.IsParent) i.IsMarked = mark;
        UpdateStatus();
    }

    public void InvertMarks()
    {
        foreach (var i in items) if (!i.IsParent && !i.IsDirectory) i.IsMarked = !i.IsMarked;
        UpdateStatus();
    }

    public void MarkByMask(string mask, bool mark)
    {
        var masks = Util.SplitMasks(mask);
        foreach (var i in items)
        {
            if (i.IsParent || (mark && i.IsDirectory)) continue;
            if (Util.MatchAny(masks, i.Name)) i.IsMarked = mark;
        }
        UpdateStatus();
    }

    public void CalcSize(FileItem it)
    {
        if (!it.IsDirectory || it.IsParent || it.IsSizeCalculating) return;
        it.IsSizeCalculating = true;
        var path = it.FullPath;
        Task.Run(() => Util.DirectorySize(path)).ContinueWith(t =>
        {
            it.DirSize = t.IsCompletedSuccessfully ? t.Result : null;
            UpdateStatus();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void CalcAllSizes()
    {
        foreach (var i in items) if (i.IsDirectory && !i.IsParent) CalcSize(i);
    }

    /// <summary>Позначити / зняти позначку з елемента під курсором і перейти до наступного (як Insert).</summary>
    public void ToggleMarkCurrent()
    {
        ToggleMark(CurrentItem);
        MoveCursor(1);
    }

    public void SetSort(SortKey key)
    {
        if (sortBy == key) sortDesc = !sortDesc;
        else
        {
            sortBy = key;
            sortDesc = key is SortKey.Size or SortKey.Date;
        }
        UpdateHeaders();
        Apply(CurrentItem?.Name, List.IsKeyboardFocusWithin);
    }

    void UpdateHeaders()
    {
        foreach (var (key, (column, title)) in sortColumns)
            column.Header = title + (key == sortBy ? (sortDesc ? "  ↓" : "  ↑") : "");
    }

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Column == null) return;
        foreach (var (key, (column, _)) in sortColumns)
        {
            if (column == h.Column)
            {
                SetSort(key);
                return;
            }
        }
    }

    // =====================================================================
    // Хлібні крихти, шлях, диски, статус
    // =====================================================================

    void UpdatePathBar()
    {
        var path = CurrentPath;
        var root = Path.GetPathRoot(path) ?? path;
        DriveText.Text = root.TrimEnd('\\');
        DriveIcon.Source = ShellIcons.GetDrive(root);

        CrumbPanel.Children.Clear();
        var segs = new List<(string name, string path)>();
        for (var di = new DirectoryInfo(path); di != null; di = di.Parent)
            segs.Insert(0, (di.Parent == null ? di.FullName.TrimEnd('\\') : di.Name, di.FullName));

        for (int i = 0; i < segs.Count; i++)
        {
            if (i > 0)
            {
                var sep = Util.Glyph("", 9);
                sep.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
                sep.Margin = new Thickness(1, 1, 1, 0);
                CrumbPanel.Children.Add(sep);
            }
            var target = segs[i].path;
            var b = new Button { Content = new TextBlock { Text = segs[i].name }, Style = (Style)FindResource("CrumbButton") };
            if (i == segs.Count - 1) b.FontWeight = FontWeights.SemiBold;
            b.Click += (_, _) =>
            {
                Navigate(target);
                FocusList();
            };
            CrumbPanel.Children.Add(b);
        }
        CrumbScroll.UpdateLayout();
        CrumbScroll.ScrollToRightEnd();
        UpdateFree();
    }

    void UpdateFree()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(CurrentPath)!);
            FreeText.Text = $"{Util.FormatSize(d.AvailableFreeSpace)} вільно";
            FreeText.ToolTip = $"{d.VolumeLabel}  ·  вільно {Util.FormatSize(d.AvailableFreeSpace)} з {Util.FormatSize(d.TotalSize)}";
        }
        catch
        {
            FreeText.Text = "";
            FreeText.ToolTip = null;
        }
    }

    void UpdateStatus()
    {
        int files = 0, dirs = 0;
        long total = 0, markedSize = 0;
        int marked = 0;
        foreach (var i in items)
        {
            if (i.IsParent) continue;
            if (i.IsDirectory) dirs++;
            else
            {
                files++;
                total += i.Size;
            }
            if (i.IsMarked)
            {
                marked++;
                markedSize += i.EffectiveSize;
            }
        }
        StatusLeft.Text = marked > 0
            ? $"Виділено {marked} з {files + dirs}  ·  {Util.FormatSize(markedSize)}"
            : $"{Util.Count(dirs, "каталог", "каталоги", "каталогів")}, {Util.Count(files, "файл", "файли", "файлів")}  ·  {Util.FormatSize(total)}";
        StatusRight.Text = filter.Length > 0 ? $"фільтр: «{filter}»" : "";
    }

    public void BeginEditPath()
    {
        PathBox.Text = CurrentPath;
        PathBox.Visibility = Visibility.Visible;
        CrumbScroll.Visibility = Visibility.Hidden;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    void EndEditPath()
    {
        PathBox.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
    }

    void PathBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginEditPath();
        e.Handled = true;
    }

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var t = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
            EndEditPath();
            if (t.Length == 0) { FocusList(); return; }
            string full;
            try { full = Path.Combine(CurrentPath, t); }
            catch { full = t; }
            if (File.Exists(full)) Navigate(Path.GetDirectoryName(full)!, Path.GetFileName(full));
            else Navigate(full);
            FocusList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEditPath();
            FocusList();
        }
    }

    void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditPath();

    void DriveButton_Click(object sender, RoutedEventArgs e) => ShowDriveMenu();

    public void ShowDriveMenu()
    {
        var menu = new ContextMenu();
        foreach (var d in DriveInfo.GetDrives())
        {
            var root = d.Name;
            string label, extra = "";
            try
            {
                if (d.IsReady)
                {
                    label = string.IsNullOrEmpty(d.VolumeLabel) ? DriveTypeName(d.DriveType) : d.VolumeLabel;
                    extra = $"{Util.FormatSize(d.AvailableFreeSpace)} з {Util.FormatSize(d.TotalSize)}";
                }
                else label = DriveTypeName(d.DriveType) + " — не готовий";
            }
            catch
            {
                label = DriveTypeName(d.DriveType);
            }
            var mi = new MenuItem
            {
                Header = $"_{root[0]}:   {Util.EscapeAccess(label)}",
                InputGestureText = extra,
                Icon = new Image { Source = ShellIcons.GetDrive(root), Width = 16, Height = 16 },
            };
            mi.Click += (_, _) =>
            {
                Navigate(root);
                FocusList();
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        void Place(string glyph, string title, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            menu.Items.Add(Util.MakeMenuItem(glyph, title, null, () =>
            {
                Navigate(path);
                FocusList();
            }));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Place("", "Домашній каталог", home);
        Place("", "Робочий стіл", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Place("", "Документи", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Place("", "Завантаження", Path.Combine(home, "Downloads"));
        Place("", "Зображення", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Place("", "OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Place("", "Тимчасові файли", Path.GetTempPath());

        menu.PlacementTarget = DriveButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    static string DriveTypeName(DriveType t) => t switch
    {
        DriveType.Fixed => "Локальний диск",
        DriveType.Removable => "Знімний диск",
        DriveType.Network => "Мережевий диск",
        DriveType.CDRom => "Оптичний привід",
        DriveType.Ram => "RAM-диск",
        _ => "Диск",
    };

    // =====================================================================
    // Фільтр і швидкий пошук
    // =====================================================================

    public void ShowFilter()
    {
        FilterBar.Visibility = Visibility.Visible;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    void CloseFilter()
    {
        suppressFilter = true;
        FilterBox.Text = "";
        suppressFilter = false;
        FilterBar.Visibility = Visibility.Collapsed;
        filter = "";
        Apply(CurrentItem?.Name, false);
        UpdateStatus();
        FocusList();
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressFilter) return;
        filter = FilterBox.Text.Trim();
        Apply(CurrentItem?.Name, false);
        UpdateStatus();
    }

    void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFilter();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            FocusList();
            e.Handled = true;
        }
    }

    void DoQuickSearch()
    {
        quickTimer.Stop();
        quickTimer.Start();
        QuickSearchText.Text = "Пошук: " + quick;
        QuickSearchBorder.Visibility = Visibility.Visible;
        int idx = -1;
        for (int i = 0; i < items.Count && idx < 0; i++)
            if (items[i].Name.StartsWith(quick, StringComparison.OrdinalIgnoreCase)) idx = i;
        for (int i = 0; i < items.Count && idx < 0; i++)
            if (items[i].Name.Contains(quick, StringComparison.OrdinalIgnoreCase)) idx = i;
        QuickSearchBorder.SetResourceReference(Border.BackgroundProperty, idx >= 0 ? "AccentBrush" : "DangerBrush");
        if (idx >= 0) Select(idx, true);
    }

    void ClearQuickSearch()
    {
        quick = "";
        quickTimer.Stop();
        QuickSearchBorder.Visibility = Visibility.Collapsed;
    }

    // =====================================================================
    // Клавіатура
    // =====================================================================

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var m = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when m == ModifierKeys.None:
                e.Handled = true;
                ClearQuickSearch();
                OpenCurrent();
                break;
            case Key.Back when m == ModifierKeys.None:
                e.Handled = true;
                if (quick.Length > 0)
                {
                    quick = quick[..^1];
                    if (quick.Length == 0) ClearQuickSearch();
                    else DoQuickSearch();
                }
                else GoUp();
                break;
            case Key.Insert when m == ModifierKeys.None:
                e.Handled = true;
                ToggleMark(CurrentItem);
                MoveCursor(1);
                break;
            case Key.Space when m == ModifierKeys.None:
                e.Handled = true;
                if (quick.Length > 0)
                {
                    // під час швидкого пошуку пробіл — частина імені
                    quick += " ";
                    DoQuickSearch();
                    break;
                }
                if (CurrentItem is not { } it) break;
                if (it.IsDirectory && !it.IsParent && it.DirSize is null && !it.IsSizeCalculating && !it.IsMarked) CalcSize(it);
                ToggleMark(it);
                MoveCursor(1);
                break;
            case Key.Up or Key.Down when m == ModifierKeys.Shift:
                e.Handled = true;
                ToggleMark(CurrentItem);
                MoveCursor(e.Key == Key.Up ? -1 : 1);
                break;
            case Key.Escape when m == ModifierKeys.None:
                if (quick.Length > 0)
                {
                    ClearQuickSearch();
                    e.Handled = true;
                }
                else if (FilterBar.Visibility == Visibility.Visible)
                {
                    CloseFilter();
                    e.Handled = true;
                }
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End:
                ClearQuickSearch();
                break;
        }
    }

    void List_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        var t = e.Text;
        // пробіл обробляється в List_PreviewKeyDown
        if (string.IsNullOrEmpty(t) || char.IsControl(t[0]) || t == " ") return;
        // + − * з цифрового блоку — команди виділення (їх уже виконало головне вікно), а не символи пошуку.
        // Ті самі символи з основної клавіатури, напр. «-», шукаються як звичайно.
        if (Keyboard.IsKeyDown(Key.Add) || Keyboard.IsKeyDown(Key.Subtract) || Keyboard.IsKeyDown(Key.Multiply)) return;
        quick += t;
        DoQuickSearch();
        e.Handled = true;
    }

    // =====================================================================
    // Миша, контекстне меню, drag & drop
    // =====================================================================

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    static FileItem? ItemFromEvent(RoutedEventArgs e) =>
        FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as FileItem;

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ItemFromEvent(e) != null) OpenCurrent();
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var it = ItemFromEvent(e);
        dragStart = e.GetPosition(List);
        dragItem = it;
        if (it == null) return;

        var m = Keyboard.Modifiers;
        if (m == ModifierKeys.Control)
        {
            ToggleMark(it);
            Select(items.IndexOf(it), true);
            e.Handled = true;
            dragItem = null;
        }
        else if (m == ModifierKeys.Shift && CurrentItem != null)
        {
            int a = items.IndexOf(CurrentItem), b = items.IndexOf(it);
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                if (!items[i].IsParent) items[i].IsMarked = true;
            Select(b, true);
            UpdateStatus();
            e.Handled = true;
            dragItem = null;
        }
    }

    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => dragItem = null;

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragItem == null || dragItem.IsParent) return;
        var p = e.GetPosition(List);
        if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var targets = dragItem.IsMarked ? items.Where(i => i.IsMarked).ToList() : new List<FileItem> { dragItem };
        dragItem = null;
        var data = new DataObject(DataFormats.FileDrop, targets.Select(t => t.FullPath).ToArray());
        dragSource = this;
        try
        {
            DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            dragSource = null;
        }
    }

    void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Середня кнопка на каталозі — відкрити у фоновій вкладці.
        if (e.ChangedButton == MouseButton.Middle && ItemFromEvent(e) is { IsDirectory: true, IsParent: false } it)
        {
            NewTab(it.FullPath, activate: false);
            e.Handled = true;
        }
    }

    void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var it = ItemFromEvent(e);
        if (it == null || it.IsParent)
        {
            ShowBackgroundMenu();
            return;
        }
        List<FileItem> targets;
        if (it.IsMarked) targets = items.Where(i => i.IsMarked).ToList();
        else
        {
            Select(items.IndexOf(it), true);
            targets = new List<FileItem> { it };
        }
        ShowShellMenu(targets, null);
    }

    public void ShowContextMenuForCurrent()
    {
        var targets = GetTargets();
        if (targets.Count == 0)
        {
            ShowBackgroundMenu();
            return;
        }
        Point pt;
        if (List.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, List.SelectedIndex)) is ListViewItem c)
            pt = c.PointToScreen(new Point(40, c.ActualHeight));
        else
            pt = List.PointToScreen(new Point(40, 40));
        ShowShellMenu(targets, pt);
    }

    void ShowShellMenu(List<FileItem> targets, Point? screenPt)
    {
        if (Owner is not { } w) return;
        var verb = ShellContextMenu.Show(w, targets.Select(t => t.FullPath).ToList(), screenPt);
        if (verb == "rename") RenameRequested?.Invoke(this);
    }

    void ShowBackgroundMenu() => BackgroundMenuRequested?.Invoke(this);

    string? DropTargetDir(DragEventArgs e)
    {
        var it = ItemFromEvent(e);
        if (it is { IsDirectory: true }) return it.FullPath;
        if (dragSource == this) return null; // на порожнє місце своєї ж панелі — нічого
        return CurrentPath;
    }

    static DragDropEffects ChooseEffect(DragEventArgs e, string[] files, string target)
    {
        bool move;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) move = true;
        else if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) move = false;
        else move = Util.SameVolume(files[0], target); // як у Провіднику: той самий диск — переміщення

        foreach (var f in files)
        {
            if (Util.PathEquals(f, target) || Util.IsSubPathOf(target, f)) return DragDropEffects.None;
            if (move && Util.PathEquals(Path.GetDirectoryName(f) ?? "", target)) return DragDropEffects.None;
        }

        var eff = move ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & eff) == 0)
            eff = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        return eff;
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && DropTargetDir(e) is { } target)
            e.Effects = ChooseEffect(e, files, target);
        e.Handled = true;
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (DropTargetDir(e) is not { } target) return;
        var eff = ChooseEffect(e, files, target);
        if (eff == DragDropEffects.None) return;
        // Джерелу повідомляємо «копію»: переміщення виконуємо самі, щоб Провідник нічого не видаляв.
        e.Effects = DragDropEffects.Copy;
        bool move = eff == DragDropEffects.Move;
        Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(this, files, target, move));
    }

    void List_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double others = ColExt.ActualWidth + ColSize.ActualWidth + ColDate.ActualWidth + ColAttr.ActualWidth;
        ColName.Width = Math.Max(140, List.ActualWidth - others - 24);
    }
}
