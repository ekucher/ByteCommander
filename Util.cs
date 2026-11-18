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

    /// <summary>Відкрити файл на читання, не заважаючи іншим програмам його писати, перейменовувати чи видаляти.</summary>
    public static FileStream OpenShared(string path, FileOptions options = FileOptions.None) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, options);

    /// <summary>
    /// Пише новий вміст у тимчасовий файл поруч із <paramref name="dst"/> і лише після успіху атомарно
    /// замінює ним призначення. При збої чи скасуванні наявний файл лишається недоторканим.
    /// </summary>
    /// <param name="keepDestinationMetadata">
    /// Редагування наявного файлу на місці: зберегти його атрибути, дату створення й права (File.Replace).
    /// Якщо в файлу є інші імена — жорсткі посилання чи він сам символьне посилання, — заміна розірвала б
    /// зв’язок, тож готовий вміст копіюється поверх наявного файлу (його бачать усі імена).
    /// Без цього прапорця призначення просто замінюється (атрибути виставляє той, хто викликає).
    /// </param>
    public static void WriteViaTemp(string dst, Action<string> write, bool keepDestinationMetadata = false)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(dst)!, $".{Path.GetFileName(dst)}.{Guid.NewGuid():N}.bctmp");
        try
        {
            write(tmp);
            if (File.Exists(dst) && keepDestinationMetadata)
            {
                if (HasOtherNames(dst)) OverwriteInPlace(tmp, dst);
                else File.Replace(tmp, dst, null, ignoreMetadataErrors: true);
                return;
            }
            if (File.Exists(dst)) ClearReadOnly(dst);
            File.Move(tmp, dst, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>До вмісту файлу ведуть інші імена: він — символьне посилання або має кілька жорстких посилань.</summary>
    static bool HasOtherNames(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null || Services.NativeMethods.HardLinkCount(path) > 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Переписати вміст наявного файлу (вже повністю підготовленим тимчасовим) без заміни самого файлу.
    /// Відкриття з FileMode.Open іде за символьним посиланням до цілі.
    /// </summary>
    static void OverwriteInPlace(string source, string dst)
    {
        using var input = OpenShared(source, FileOptions.SequentialScan);
        using var output = new FileStream(dst, FileMode.Open, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
        output.SetLength(output.Position);
    }

    public static void ClearReadOnly(string path)
    {
        try
        {
            var a = File.GetAttributes(path);
            if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // якщо не вдалося — наступна операція повідомить про справжню причину
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

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
        w.SetResourceReference(Control.BackgroundProperty, ThemeKeys.BgBrush);
        w.SetResourceReference(Control.ForegroundProperty, ThemeKeys.TextBrush);
        ApplyUiFont(w);
    }

    /// <summary>Шрифт інтерфейсу застосунку (як у головному вікні).</summary>
    public static void ApplyUiFont(Control c)
    {
        c.FontFamily = Resource<FontFamily>(StyleKeys.UiFont);
        c.FontSize = 13;
    }

    /// <summary>Ресурс застосунку за ключем (див. <see cref="ThemeKeys"/>, <see cref="StyleKeys"/>).</summary>
    public static T Resource<T>(string key) => (T)Application.Current.FindResource(key);

    public static FontFamily IconFont => Resource<FontFamily>(StyleKeys.IconFont);

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
