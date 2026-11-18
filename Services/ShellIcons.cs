using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ByteCommander.Services;

/// <summary>Системні іконки файлів з кешем за розширенням.</summary>
public static class ShellIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Для цих типів іконка залежить від конкретного файлу, а не від розширення.
    static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".msc", ".appref-ms" };

    public static ImageSource? Get(string path, bool isDirectory)
    {
        if (isDirectory) return Cached("<dir>", () => Load("folder", FILE_DIR, true));

        var ext = Path.GetExtension(path);
        if (PerFile.Contains(ext))
            return Cached(path, () => Load(path, 0, false) ?? Load("x" + ext, FILE_NORMAL, true));

        var key = ext.Length == 0 ? "<noext>" : ext;
        return Cached(key, () => Load("x" + ext, FILE_NORMAL, true));
    }

    public static ImageSource? GetDrive(string root) => Cached("drive:" + root, () => Load(root, 0, false));

    const uint FILE_DIR = NativeMethods.FILE_ATTRIBUTE_DIRECTORY, FILE_NORMAL = NativeMethods.FILE_ATTRIBUTE_NORMAL;

    static ImageSource? Cached(string key, Func<ImageSource?> factory)
    {
        if (Cache.TryGetValue(key, out var img)) return img;
        img = factory();
        Cache[key] = img;
        return img;
    }

    static ImageSource? Load(string path, uint attrs, bool useAttributes)
    {
        var info = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        if (useAttributes) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
        var res = NativeMethods.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
