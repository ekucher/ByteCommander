using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ByteCommander.Services;

/// <summary>Нативне контекстне меню Провідника Windows для набору файлів з одного каталогу.</summary>
public static class ShellContextMenu
{
    const uint CmdFirst = 1, CmdLast = 0x7FFF;
    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const uint GCS_VERBW = 0x4;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000, CMIC_MASK_PTINVOKE = 0x20000000;
    const int WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_INITMENUPOPUP = 0x117, WM_MENUCHAR = 0x120;

    /// <returns>Verb вибраної команди, якщо її має обробити застосунок (наприклад "rename"), інакше null.</returns>
    public static string? Show(Window owner, IReadOnlyList<string> paths, Point? screenPoint)
    {
        if (paths.Count == 0) return null;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidls = new List<IntPtr>();
        var children = new IntPtr[paths.Count];
        IntPtr parentPtr = IntPtr.Zero, cmPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        object? parentObj = null;
        IContextMenu? cm = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            var iidFolder = typeof(IShellFolder).GUID;
            for (int i = 0; i < paths.Count; i++)
            {
                if (SHParseDisplayName(paths[i], IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
                pidls.Add(pidl);
                if (SHBindToParent(pidl, ref iidFolder, out var folder, out var child) != 0) return null;
                if (i == 0) parentPtr = folder; else Marshal.Release(folder);
                children[i] = child;
            }

            parentObj = Marshal.GetObjectForIUnknown(parentPtr);
            var parent = (IShellFolder)parentObj;
            var iidMenu = typeof(IContextMenu).GUID;
            if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out cmPtr) != 0) return null;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);

            hmenu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) flags |= CMF_EXTENDEDVERBS;
            cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, flags);

            // Підменю ("Надіслати", "Відкрити за допомогою") малюються власником — пересилаємо їм повідомлення.
            var cm2 = cm as IContextMenu2;
            var cm3 = cm as IContextMenu3;
            source = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (cm3 != null)
                    {
                        if (cm3.HandleMenuMsg2((uint)msg, wp, lp, out var res) == 0) { handled = true; return res; }
                    }
                    else if (cm2 != null && msg != WM_MENUCHAR)
                    {
                        if (cm2.HandleMenuMsg((uint)msg, wp, lp) == 0) { handled = true; return IntPtr.Zero; }
                    }
                }
                return IntPtr.Zero;
            };
            source?.AddHook(hook);

            POINT pt;
            if (screenPoint is { } sp) pt = new POINT { X = (int)sp.X, Y = (int)sp.Y };
            else GetCursorPos(out pt);

            int cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (cmd < CmdFirst) return null;

            uint id = (uint)cmd - CmdFirst;
            var verb = GetVerb(cm, id);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase)) return "rename";

            uint mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mask |= CMIC_MASK_SHIFT_DOWN;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mask |= CMIC_MASK_CONTROL_DOWN;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = (IntPtr)id,
                lpVerbW = (IntPtr)id,
                lpDirectoryW = Path.GetDirectoryName(paths[0]),
                nShow = 1,
                ptInvoke = pt,
            };
            cm.InvokeCommand(ref info);
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (parentObj != null) Marshal.ReleaseComObject(parentObj);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    static string? GetVerb(IContextMenu cm, uint id)
    {
        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt16(buf, 0);
            return cm.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, buf, 512) == 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ---------- COM / Win32 ----------
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT pt);
}
