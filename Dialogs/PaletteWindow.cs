using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ByteCommander;

public sealed record PaletteEntry(string Title, string Hint, string Glyph, Action Run);

/// <summary>Палітра команд у стилі VS Code (Ctrl+Shift+P / F1): нечіткий пошук по командах, закладках і дисках.</summary>
public sealed class PaletteWindow : Window
{
    readonly List<PaletteEntry> all;
    readonly TextBox box = new() { FontSize = 15, Padding = new Thickness(10, 8, 10, 8) };
    readonly ListBox list = new() { MaxHeight = 440, Margin = new Thickness(0, 8, 0, 0) };
    Action? chosen;
    bool closing;

    public PaletteWindow(Window owner, List<PaletteEntry> entries)
    {
        all = entries;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 660;
        SizeToContent = SizeToContent.Height;
        SetResourceReference(BackgroundProperty, ThemeKeys.PanelAltBrush);
        SetResourceReference(BorderBrushProperty, ThemeKeys.AccentBrush);
        BorderThickness = new Thickness(1);
        SetResourceReference(ForegroundProperty, ThemeKeys.TextBrush);
        Util.ApplyUiFont(this);
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(box, Dock.Top);
        root.Children.Add(box);
        root.Children.Add(list);
        Content = root;

        box.TextChanged += (_, _) => Filter();
        box.PreviewKeyDown += Box_PreviewKeyDown;
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListBoxItem item) Choose(item);
        };
        Deactivated += (_, _) => { if (!closing) Close(); };
        Closing += (_, _) => closing = true;
        Closed += (_, _) => { if (chosen != null) owner.Dispatcher.BeginInvoke(chosen); };

        // Розміщення по центру зверху вікна-власника (з урахуванням DPI і максимізації).
        var src = PresentationSource.FromVisual(owner);
        if (src?.CompositionTarget != null)
        {
            var p = src.CompositionTarget.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
            Left = p.X + (owner.ActualWidth - Width) / 2;
            Top = p.Y + 48;
        }

        Loaded += (_, _) =>
        {
            Filter();
            box.Focus();
        };
    }

    void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                if (list.SelectedItem is ListBoxItem item) Choose(item);
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(10);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-10);
                e.Handled = true;
                break;
        }
    }

    void Move(int delta)
    {
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    void Choose(ListBoxItem item)
    {
        chosen = ((PaletteEntry)item.Tag).Run;
        Close();
    }

    void Filter()
    {
        var q = box.Text.Trim();
        list.Items.Clear();
        foreach (var (e, _) in all.Select(e => (e, Score(e, q))).Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).Take(200))
            list.Items.Add(MakeRow(e));
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    static int Score(PaletteEntry e, string q)
    {
        if (q.Length == 0) return 1;
        if (e.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        var hay = e.Title + " " + e.Hint;
        int idx = hay.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 80 - Math.Min(idx, 40);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase))) return 30;
        int j = 0;
        foreach (var c in hay)
            if (j < q.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(q[j])) j++;
        return j == q.Length ? 10 : 0;
    }

    static ListBoxItem MakeRow(PaletteEntry e)
    {
        var row = new DockPanel();
        var glyph = Util.Glyph(e.Glyph, 14);
        glyph.Width = 28;
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.AccentBrush);
        var hint = new TextBlock { Text = e.Hint, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextDimBrush);
        DockPanel.SetDock(glyph, Dock.Left);
        DockPanel.SetDock(hint, Dock.Right);
        row.Children.Add(glyph);
        row.Children.Add(hint);
        row.Children.Add(new TextBlock { Text = e.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return new ListBoxItem { Content = row, Tag = e };
    }
}
