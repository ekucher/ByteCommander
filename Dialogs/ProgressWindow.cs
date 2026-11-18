using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ByteCommander.Services;

namespace ByteCommander;

/// <summary>Немодальне вікно прогресу; з’являється лише якщо операція триває довше ~0,4 с.</summary>
public sealed class ProgressWindow : Window
{
    readonly ProgressState st;
    readonly Window owner;
    readonly DispatcherTimer timer;
    readonly Stopwatch sw = Stopwatch.StartNew();
    readonly TextBlock phase = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    readonly TextBlock file = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock stats = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1000, Margin = new Thickness(0, 14, 0, 0) };
    bool finished, closed;

    public ProgressWindow(Window owner, string title, ProgressState state, CancellationTokenSource cts)
    {
        this.owner = owner;
        st = state;
        Title = title;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Util.Res("BgBrush");
        Foreground = Util.Res("TextBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        FontSize = 13;

        file.Foreground = stats.Foreground = Util.Res("TextDimBrush");
        var cancel = new Button { Content = "Скасувати", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 110, Margin = new Thickness(0, 18, 0, 0) };
        cancel.Click += (_, _) =>
        {
            cts.Cancel();
            cancel.IsEnabled = false;
            phase.Text = "Скасування…";
        };

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(phase);
        root.Children.Add(file);
        root.Children.Add(bar);
        root.Children.Add(stats);
        root.Children.Add(cancel);
        Content = root;

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => Tick();
        Closing += (_, _) => { if (!finished) cts.Cancel(); };
        Closed += (_, _) => closed = true;
    }

    public void Start() => timer.Start();

    void Tick()
    {
        if (finished || closed) return;
        if (!IsVisible && sw.ElapsedMilliseconds > 400)
        {
            try { Show(); } catch { return; }
        }
        Update();
    }

    void Update()
    {
        double frac = st.TotalBytes > 0 ? (double)st.DoneBytes / st.TotalBytes
            : st.TotalFiles > 0 ? (double)st.DoneFiles / st.TotalFiles : 0;
        frac = Math.Clamp(frac, 0, 1);
        bar.Value = frac * 1000;
        phase.Text = $"{st.Phase}  {frac * 100:0}%";
        file.Text = st.Current;

        double t = sw.Elapsed.TotalSeconds;
        double speed = t > 0.5 ? st.DoneBytes / t : 0;
        var parts = new List<string>();
        if (st.TotalBytes > 0) parts.Add($"{Util.FormatSize(st.DoneBytes)} з {Util.FormatSize(st.TotalBytes)}");
        parts.Add($"файлів {st.DoneFiles} з {st.TotalFiles}");
        if (speed > 0) parts.Add($"{Util.FormatSize((long)speed)}/с");
        if (speed > 0 && st.TotalBytes > st.DoneBytes)
            parts.Add("залишилось " + TimeSpan.FromSeconds((st.TotalBytes - st.DoneBytes) / speed).ToString(@"hh\:mm\:ss"));
        stats.Text = string.Join("  ·  ", parts);

        if (owner.TaskbarItemInfo is { } tb)
        {
            tb.ProgressState = TaskbarItemProgressState.Normal;
            tb.ProgressValue = frac;
        }
    }

    public void Finish()
    {
        finished = true;
        timer.Stop();
        if (owner.TaskbarItemInfo is { } tb) tb.ProgressState = TaskbarItemProgressState.None;
        if (IsVisible) Close();
    }
}
