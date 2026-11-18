using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ByteCommander;

/// <summary>Прості модальні діалоги у темному стилі.</summary>
public static class Dialogs
{
    static Window Make(Window? owner, string title)
    {
        owner ??= Application.Current?.MainWindow;
        var w = new Window
        {
            Title = title,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Background = Util.Res("BgBrush"),
            Foreground = Util.Res("TextBrush"),
            FontFamily = (FontFamily)Application.Current!.FindResource("UiFont"),
            FontSize = 13,
            UseLayoutRounding = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        if (owner != null && owner.IsVisible)
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return w;
    }

    static WrapPanel ButtonRow(Window w, string[] labels, int def, Action<int> onClick, out Button? defaultButton)
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        defaultButton = null;
        for (int i = 0; i < labels.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = labels[i], MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsDefault = i == def };
            if (i == def)
            {
                b.Style = (Style)Application.Current.FindResource("AccentButton");
                defaultButton = b;
            }
            b.Click += (_, _) => { onClick(idx); w.Close(); };
            row.Children.Add(b);
        }
        return row;
    }

    public static string? Input(Window? owner, string title, string prompt, string initial, bool selectStem = false)
    {
        var w = Make(owner, title);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 580, Margin = new Thickness(0, 0, 0, 10) });
        var tb = new TextBox { Text = initial, Width = 580 };
        root.Children.Add(tb);
        string? result = null;
        root.Children.Add(ButtonRow(w, new[] { "OK", "Скасувати" }, 0, i => { if (i == 0) result = tb.Text; }, out _));
        w.Content = root;
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } };
        w.Loaded += (_, _) =>
        {
            tb.Focus();
            int dot = initial.LastIndexOf('.');
            if (selectStem && dot > 0) tb.Select(0, dot);
            else tb.SelectAll();
        };
        w.ShowDialog();
        return result;
    }

    /// <returns>Індекс натиснутої кнопки або <paramref name="cancel"/> при Esc / закритті.</returns>
    public static int Ask(Window? owner, string title, string message, string[] buttons, int def = 0, int cancel = -1)
    {
        var w = Make(owner, title);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 380 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
        int result = cancel;
        root.Children.Add(ButtonRow(w, buttons, def, i => result = i, out var defButton));
        w.Content = root;
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; result = cancel; w.Close(); } };
        w.Loaded += (_, _) => defButton?.Focus();
        w.ShowDialog();
        return result;
    }

    public static void Error(Window? owner, string message) => Ask(owner, "Помилка", message, new[] { "OK" }, 0, 0);

    public static bool Confirm(Window? owner, string title, string message, string yes) =>
        Ask(owner, title, message, new[] { yes, "Скасувати" }, 0, 1) == 0;
}
