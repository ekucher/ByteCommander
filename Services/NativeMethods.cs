using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ByteCommander.Services;

public static class NativeMethods
{
    // ---------- Природне сортування як у Провіднику ----------
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    // ---------- Іконки ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- Кошик ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>Переміщує файли/каталоги до кошика. Повертає 0 у разі успіху.</summary>
    public static int Recycle(IReadOnlyList<string> paths)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op);
    }

    // ---------- Властивості файлу ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    public static void ShowProperties(string path, IntPtr hwnd)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = hwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        ShellExecuteEx(ref info);
    }

    // ---------- Жорсткі посилання ----------
    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>Скільки імен (жорстких посилань) має файл; 1 — звичайний файл.</summary>
    public static int HardLinkCount(string path)
    {
        using var fs = Util.OpenShared(path);
        return GetFileInformationByHandle(fs.SafeFileHandle, out var info) ? (int)info.NumberOfLinks : 1;
    }

    // ---------- Темний заголовок вікна ----------
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Оформлення заголовка вікна через DWM: темний/світлий режим, колір заголовка (Windows 11) і заокруглені кути.
    /// Що саме ставити, вирішує <see cref="ThemeManager"/>; тут лише виклики WinAPI.
    /// </summary>
    public static void SetWindowChrome(IntPtr hwnd, bool dark, System.Windows.Media.Color? caption)
    {
        if (hwnd == IntPtr.Zero) return;
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (caption is { } c)
        {
            int colorRef = c.R | c.G << 8 | c.B << 16;                  // COLORREF (0x00BBGGRR)
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
        }
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));        // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
    }
}
