using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using CSSTRRET = Windows.Win32.UI.Shell.Common.STRRET;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

// ────────────────────────────────────────────────────────────────
//  COM 接口定义 — 签名均对照 Microsoft.Windows.CsWin32 0.3.106
//  生成输出验证。CsWin32 生成类型位于 obj/generated/ 下。
//  以下接口与 CsWin32 生成的 internal 接口 vtable 一致，
//  但不使用 unsafe 指针以避免大范围代码重构。
// ────────────────────────────────────────────────────────────────

#region ShellWindowsLateBound — IShellWindows 迟绑定

internal sealed class ShellWindowsLateBound : IDisposable
{
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private readonly object _comObject;
    private readonly Type _comType;
    private bool _disposed;

    public ShellWindowsLateBound()
    {
        var type = Type.GetTypeFromCLSID(ClsidShellWindows)
            ?? throw new InvalidOperationException("无法获取 IShellWindows 类型。");
        _comObject = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建 IShellWindows COM 对象。");
        _comType = _comObject.GetType();
    }

    /// <summary>
    /// FindWindowSW(pvarLoc=CSIDL_DESKTOP Int32, pvarLocRoot=VT_EMPTY, SWC_DESKTOP, SWFO_NEEDDISPATCH).
    /// pvarLoc 使用 CSIDL_DESKTOP 整数值，pvarLocRoot 为 null（true VT_EMPTY）。
    /// </summary>
    public object? FindWindowSW(int swClass, int swfwOptions, out int phwnd)
    {
        ThrowIfDisposed();
        object pvarLoc = NativeShellMethods.CSIDL_DESKTOP;   // int → VT_I4
        object pvarLocRoot = null!;       // null → true VT_EMPTY
        object boxedHwnd = 0;

        var args = new object[] { pvarLoc, pvarLocRoot, swClass, boxedHwnd, swfwOptions };
        var mods = new ParameterModifier[] { new ParameterModifier(5) };
        mods[0][0] = true; // pvarLoc     — [in] VARIANT*, ref
        mods[0][1] = true; // pvarLocRoot — [in] VARIANT*, ref
        mods[0][3] = true; // phwnd       — [out] long*, out

        try
        {
            var result = _comType.InvokeMember("FindWindowSW",
                BindingFlags.InvokeMethod, null, _comObject, args, mods,
                CultureInfo.InvariantCulture, null);
            phwnd = (int)args[3];
            return result;
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    public int GetCount()
    {
        ThrowIfDisposed();
        try
        {
            var result = _comType.InvokeMember("Count", BindingFlags.GetProperty,
                null, _comObject, null, null, CultureInfo.InvariantCulture, null);
            return result is int i ? i : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    public object? GetItem(int index)
    {
        ThrowIfDisposed();
        var args = new object[] { index };
        try
        {
            return _comType.InvokeMember("Item", BindingFlags.GetProperty,
                null, _comObject, args, null, CultureInfo.InvariantCulture, null);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    public void Dispose()
    {
        if (!_disposed && _comObject is not null && Marshal.IsComObject(_comObject))
        {
            Marshal.ReleaseComObject(_comObject);
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
}

#endregion

#region IServiceProvider

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig] int QueryService(
        [MarshalAs(UnmanagedType.LPStruct)] Guid guidService,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);
}

#endregion

#region IShellBrowser — 对照 CsWin32 Windows.Win32.UI.Shell.IShellBrowser

[ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow
    [PreserveSig] int GetWindow(out nint phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    // IShellBrowser
    [PreserveSig] int InsertMenusSB(nint hmenuShared, out nint lpMenuWidths);
    [PreserveSig] int SetMenuSB(nint hmenuShared, nint holeMenuReserved, nint hwndActiveObject);
    [PreserveSig] int RemoveMenusSB(nint hmenuShared);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    [PreserveSig] int TranslateAcceleratorSB(nint pmsg, ushort wID);  // WORD wID
    [PreserveSig] int BrowseObject(nint pidl, uint wFlags);
    [PreserveSig] int GetViewStateStream(uint grfMode, out nint ppStrm);
    [PreserveSig] int GetControlWindow(uint id, out nint phwnd);
    // SendControlMsg: WPARAM=nuint, LPARAM=nint — 对照 CsWin32
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, nuint wParam, nint lParam, out nint pret);
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? ppshv);
    [PreserveSig] int OnViewWindowActive(nint pshv);
    [PreserveSig] int SetToolbarItems(nint lpButtons, uint nButtons, uint uFlags);
}

#endregion

#region IShellView — 对照 CsWin32 Windows.Win32.UI.Shell.IShellView

[ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    // IOleWindow
    [PreserveSig] int GetWindow(out nint phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    // IShellView
    [PreserveSig] int TranslateAccelerator(nint pmsg);
    [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
    // CreateViewWindow: RECT* 使用 ref RECT 结构，对照 CsWin32
    [PreserveSig] int CreateViewWindow(nint psvPrevious, nint pfs, nint psb, ref RECT prcView, out nint phWnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(out nint pfs);
    [PreserveSig] int AddPropertySheetPages(uint dwReserved, nint pfn, uint lparam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(nint pidlItem, uint uFlags);
    [PreserveSig] int GetItemObject(uint uItem, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
}

#endregion

#region RECT — Win32 矩形结构

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

#endregion

#region IFolderView — 对照 CsWin32 Windows.Win32.UI.Shell.IFolderView

[ComImport, Guid("CDE725B0-CCC9-4519-9179-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint ViewMode);
    // GetFolder: REFIID → LPStruct Guid（对照 CsWin32 Guid*）
    [PreserveSig] int GetFolder([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    // Item: int iItemIndex, PITEMID_CHILD* ppidl — 对照 CsWin32 验证
    [PreserveSig] int Item(int iItemIndex, out nint ppidl);
    // ItemCount: _SVGIO uFlags
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    // Items: _SVGIO uFlags, REFIID, void** ppv
    [PreserveSig] int Items(uint uFlags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    // GetItemPosition: PCUITEMID_CHILD pidl, POINT* ppt — 对照 CsWin32 System.Drawing.Point*
    [PreserveSig] int GetItemPosition(nint pidl, out Point ppt);
    [PreserveSig] int GetSpacing(out Point ppt);
    [PreserveSig] int GetDefaultSpacing(out Point ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    // SelectAndPositionItems: 保留为 vtable 完整性，M1 不调用
    [PreserveSig] int SelectAndPositionItems(uint cidl, nint[] apidl, Point[]? apt, uint dwFlags);
}

#endregion

#region IShellFolder — 对照 CsWin32 Windows.Win32.UI.Shell.IShellFolder

[ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(nint hwnd, nint pbc,
        [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        out uint pchEaten, out nint ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(nint hwnd, uint grfFlags,
        [MarshalAs(UnmanagedType.Interface)] out IEnumIDList? ppenumIDList);
    [PreserveSig] int BindToObject(nint pidl, nint pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int BindToStorage(nint pidl, nint pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int CompareIDs(nint lParam, nint pidl1, nint pidl2);
    [PreserveSig] int CreateViewObject(nint hwndOwner,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, nint[] apidl, ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(nint hwndOwner, uint cidl, nint[] apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out uint rgfReserved,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    // GetDisplayNameOf: 使用 CsWin32 生成的 CSSTRRET (Windows.Win32.UI.Shell.Common.STRRET)
    [PreserveSig] int GetDisplayNameOf(nint pidl, uint uFlags, out CSSTRRET pName);
    [PreserveSig] int SetNameOf(nint hwnd, nint pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out nint ppidlOut);
}

#endregion

#region IEnumIDList — 对照 CsWin32 Windows.Win32.UI.Shell.IEnumIDList

[ComImport, Guid("000214F2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, out nint rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIDList? ppenum);
}

#endregion

#region NativeShellMethods

internal static class NativeShellMethods
{
    // FindWindowSW
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const int CSIDL_DESKTOP = 0x0000;

    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    // SVGIO — CsWin32: _SVGIO enum (U4)
    public const uint SVGIO_ALLVIEW = 0xFFFFFFFF;

    // SHGDN
    public const uint SHGDN_NORMAL = 0x0000;
    public const uint SHGDN_FORPARSING = 0x8000;

    // SFGAO
    public const uint SFGAO_SYSTEM = 0x00001000;
    public const uint SFGAO_LINK = 0x00010000;
    public const uint SFGAO_READONLY = 0x00040000;
    public const uint SFGAO_HIDDEN = 0x00080000;
    public const uint SFGAO_FILESYSANCESTOR = 0x10000000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;
    public const uint SFGAO_BROWSABLE = 0x08000000;
    public const uint SFGAO_NONENUMERATED = 0x00100000;

    // IID constants
    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IidIShellView = new("000214E3-0000-0000-C000-000000000046");
    public static readonly Guid IidIFolderView = new("CDE725B0-CCC9-4519-9179-325D72FAB4CE");
    public static readonly Guid IidIFolderView2 = new("1AF3A467-214F-4298-908E-06B03E0B39F9");
    public static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IidIEnumIDList = new("000214F2-0000-0000-C000-000000000046");

    // ─── CsWin32 生成的 STRRET 访问 helper ───
    // CSSTRRET.uType → anonymous union → pOleStr / uOffset / cStr

    /// <summary>从 CSSTRRET 提取字符串。StrRetToBuf 成功后自动释放 pOleStr。</summary>
    public static unsafe string StrRetToString(nint pidl, ref CSSTRRET strRet)
    {
        var sb = new System.Text.StringBuilder(512);
        var hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
        if (hr == 0 && sb.Length > 0)
            return sb.ToString();

        // StrRetToBuf 失败 → 手动回退（不重复释放 pOleStr）
        if (strRet.uType == 0)
            return Marshal.PtrToStringUni((nint)strRet.Anonymous.pOleStr.Value) ?? string.Empty;
        if (strRet.uType == 2)
        {
            var ptr = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref strRet.Anonymous.cStr[0]);
            return Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// 仅在 StrRetToBuf 未成功时释放 pOleStr。
    /// StrRetToBuf 成功时会自行释放 STRRET_WSTR 中的 pOleStr，
    /// 因此调用者必须避免 double-free。
    /// </summary>
    public static unsafe void FreeStrRetIfNeeded(ref CSSTRRET strRet, bool strRetToBufSucceeded)
    {
        if (!strRetToBufSucceeded && strRet.uType == 0)
        {
            var ptr = (nint)strRet.Anonymous.pOleStr.Value;
            if (ptr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
                strRet.Anonymous.pOleStr = default;
            }
        }
    }

    // ─── P/Invoke ───

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrRetToBuf(ref CSSTRRET pstr, nint pidl,
        System.Text.StringBuilder pszBuf, uint cchBuf);
}

#endregion
