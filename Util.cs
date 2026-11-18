using System.Diagnostics;
using System.IO.Enumeration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ByteCommander;

public static class Util
{
    static readonly string[] Units = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:0} {Units[i]}" : $"{v:0.#} {Units[i]}";
    }

    /// <summary>Українська множина: 1 файл, 2 файли, 5 файлів.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        var n1 = n % 10;
        if (n > 10 && n < 20) return many;
        if (n1 > 1 && n1 < 5) return few;
        if (n1 == 1) return one;
        return many;
    }

    public static string Count(long n, string one, string few, string many) => $"{n} {Plural(n, one, few, many)}";

    public static string Describe(IReadOnlyList<string> names) =>
        names.Count == 1 ? $"«{names[0]}»" : Count(names.Count, "об’єкт", "об’єкти", "об’єктів");

    public static string NormalizeDir(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            full = full.TrimEnd('\\', '/');
        return full;
    }

    static string Canon(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    public static bool PathEquals(string a, string b) =>
        string.Equals(Canon(a), Canon(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsSubPathOf(string child, string parent) =>
        (Canon(child) + "\\").StartsWith(Canon(parent) + "\\", StringComparison.OrdinalIgnoreCase);

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Екранування підкреслень, щоб WPF не робив із них клавіші доступу.</summary>
    public static string EscapeAccess(string s) => s.Replace("_", "__");

    /// <summary>"*.txt; *.log" → ["*.txt","*.log"]. Маска без * і ? шукається як підрядок.</summary>
    public static string[] SplitMasks(string s)
    {
        var parts = s.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m == "*.*" ? "*" : m)
            .Select(m => m.IndexOfAny(new[] { '*', '?' }) < 0 ? $"*{m}*" : m)
            .ToArray();
        return parts.Length == 0 ? new[] { "*" } : parts;
    }

    public static bool MatchAny(string[] masks, string name)
    {
        foreach (var m in masks)
            if (FileSystemName.MatchesSimpleExpression(m, name, ignoreCase: true)) return true;
        return false;
    }

    /// <summary>Рекурсивний обхід: з прихованими/системними, без падіння на недоступних, з обмеженням глибини.</summary>
    public static readonly EnumerationOptions DeepScan = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, MaxRecursionDepth = 64,
    };

    /// <summary>Усі файли каталогу рекурсивно (з перевіркою скасування між файлами).</summary>
    public static IEnumerable<FileInfo> ScanFiles(string dir, CancellationToken ct = default)
    {
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", DeepScan))
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
    }

    public static long DirectorySize(string path, CancellationToken ct = default) => ScanFiles(path, ct).Sum(f => f.Length);

    /// <summary>"C:\a" → "C:\a\": шлях, який операції сприймають як каталог призначення.</summary>
    public static string AsDirTarget(string path) => path.TrimEnd('\\', '/') + "\\";

    public static void ShellOpen(string path, string? workDir = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = workDir ?? Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155) // немає асоціації
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // скасовано користувачем
        {
        }
    }

    /// <summary>Тло й текст вікна через посилання на ресурси — вікно перефарбовується при зміні теми.</summary>
    public static void ApplyWindowTheme(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
    }

    public static FontFamily IconFont => (FontFamily)Application.Current.FindResource("IconFont");

    public static TextBlock Glyph(string glyph, double size = 14) => new()
    {
        Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
    };

    public static MenuItem MakeMenuItem(string glyph, string header, string? gesture, Action action, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            Icon = Glyph(glyph),
        };
        mi.Click += (_, _) => action();
        return mi;
    }
}
