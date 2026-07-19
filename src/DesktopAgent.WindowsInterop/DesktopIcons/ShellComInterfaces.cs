using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

// ────────────────────────────────────────────────────────────────
//  Shell COM 接口定义
//  仅包含 M1-02 读取桌面图标所需的成员。
//  每个接口均标注 IID、用途与资源所有权约定。
//
//  关于 IShellWindows 互操作：
//  不再通过 [DispId] 属性定义 IShellWindows managed 接口。
//  SHDocVw 类型库中自动化 DISPID 使用 0x60020000 偏移，
//  而非简单的序号 8（8 是 SWC_DESKTOP 常量，不是 DISPID）。
//
//  改用 ShellWindowsLateBound 通过 IDispatch.GetIDsOfNames
//  解析成员名 → DISPID，完全避免 DISPID 猜测。
// ────────────────────────────────────────────────────────────────

#region ShellWindowsLateBound — IShellWindows 迟绑定封装

/// <summary>
/// 对 IShellWindows COM 对象进行迟绑定 IDispatch 调用的封装。
/// 不使用 managed 接口 + [DispId]，而是通过 Type.InvokeMember
/// 走 IDispatch.GetIDsOfNames → Invoke，由 COM 运行时从已注册类型库
/// 解析正确的 DISPID。这避免了猜测 0x60020000 偏移 DISPID 的风险。
/// </summary>
public sealed class ShellWindowsLateBound : IDisposable
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
        _comType = _comObject.GetType(); // __ComObject
    }

    /// <summary>
    /// 调用 IShellWindows.FindWindowSW。
    /// pvarLoc 和 pvarLocRoot 传递为 [in] VARIANT（ref 语义）。
    /// phwnd 传递为 out int。
    /// 返回 IDispatch*。
    /// </summary>
    public object? FindWindowSW(byte[] pidlBytes, int swClass, int swfwOptions, out int phwnd)
    {
        ThrowIfDisposed();

        // args 顺序必须与 IDL 中参数顺序一致：
        // [in] VARIANT* pvarLoc, [in] VARIANT* pvarLocRoot,
        // [in] long swClass, [out] long* phwnd, [in] long swfwOptions,
        // [out, retval] IDispatch** ppdispOut
        object boxedPidl = pidlBytes;     // byte[] → VARIANT(VT_ARRAY|VT_UI1)
        object boxedRoot = Type.Missing;  // VT_EMPTY
        object boxedHwnd = 0;             // out long → 占位

        var args = new object[] { boxedPidl, boxedRoot, swClass, boxedHwnd, swfwOptions };

        // ParameterModifier 标记 ref/out
        // 0=pvarLoc(ref), 1=pvarLocRoot(ref), 3=phwnd(out)
        var mods = new ParameterModifier[] { new ParameterModifier(5) };
        mods[0][0] = true; // pvarLoc  — [in] VARIANT* → ref
        mods[0][1] = true; // pvarLocRoot — [in] VARIANT* → ref
        mods[0][3] = true; // phwnd  — [out] long* → out

        try
        {
            var result = _comType.InvokeMember(
                "FindWindowSW",
                BindingFlags.InvokeMethod,
                null,
                _comObject,
                args,
                mods,
                CultureInfo.InvariantCulture,
                null);

            phwnd = (int)args[3];
            return result;
        }
        catch (TargetInvocationException ex)
        {
            // 展开 TargetInvocationException 以暴露原始 COM 异常
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>获取 IShellWindows.Count。</summary>
    public int GetCount()
    {
        ThrowIfDisposed();
        try
        {
            var result = _comType.InvokeMember(
                "Count",
                BindingFlags.GetProperty,
                null,
                _comObject,
                null,
                null,
                CultureInfo.InvariantCulture,
                null);
            return result is int i ? i : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>获取 IShellWindows.Item(index)。返回 IDispatch*。
    /// Item 是 IShellWindows 的默认索引属性（DISPID_VALUE=0），
    /// 应使用 GetProperty 而非 InvokeMethod。
    /// index 是 [in] VARIANT，不应以 ref 传递。</summary>
    public object? GetItem(int index)
    {
        ThrowIfDisposed();
        object boxedIndex = index;
        var args = new object[] { boxedIndex };

        try
        {
            return _comType.InvokeMember(
                "Item",
                BindingFlags.GetProperty,
                null,
                _comObject,
                args,
                null,  // 无 ParameterModifier — 参数按值传递
                CultureInfo.InvariantCulture,
                null);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_comObject is not null && Marshal.IsComObject(_comObject))
            {
                Marshal.ReleaseComObject(_comObject);
            }
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

#endregion

#region IServiceProvider — COM 服务定位器

/// <summary>
/// IServiceProvider：COM 服务定位器接口。
/// IID: {6D5140C1-7436-11CE-8034-00AA006009FA}
/// </summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IServiceProvider
{
    [PreserveSig]
    int QueryService(
        [MarshalAs(UnmanagedType.LPStruct)] Guid guidService,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);
}

#endregion

#region IShellBrowser — Shell 浏览器

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellBrowser
{
    [PreserveSig] int GetWindow(out nint phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    [PreserveSig] int InsertMenusSB(nint hmenuShared, out nint lpMenuWidths);
    [PreserveSig] int SetMenuSB(nint hmenuShared, nint holeMenuReserved, nint hwndActiveObject);
    [PreserveSig] int RemoveMenusSB(nint hmenuShared);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
    [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    [PreserveSig] int TranslateAcceleratorSB(nint pmsg, ushort wID);
    [PreserveSig] int BrowseObject(nint pidl, uint wFlags);
    [PreserveSig] int GetViewStateStream(uint grfMode, out nint ppStrm);
    [PreserveSig] int GetControlWindow(uint id, out nint phwnd);
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, ushort wParam, ushort lParam, out nint pret);
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? ppshv);
    [PreserveSig] int OnViewWindowActive(nint pshv);
    [PreserveSig] int SetToolbarItems(nint lpButtons, uint nButtons, uint uFlags);
}

#endregion

#region IShellView — Shell 视图

[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellView
{
    [PreserveSig] int GetWindow(out nint phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    [PreserveSig] int TranslateAccelerator(nint pmsg);
    [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(nint psvPrevious, nint pfs, nint psb, ref nint prcView, out nint phWnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(out nint pfs);
    [PreserveSig] int AddPropertySheetPages(uint dwReserved, nint pfn, uint lparam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(nint pidlItem, uint uFlags);
    [PreserveSig] int GetItemObject(uint uItem, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
}

#endregion

#region IFolderView — 文件夹视图接口

/// <summary>
/// IFolderView：从 shobjidl_core.h / Windows SDK IDL 提取，
/// 仅包含 M1-02 所需成员。vtable 顺序必须与原生接口一致。
/// IID: {CDE725B0-CCC9-4519-9179-325D72FAB4CE}
/// 继承自 IUnknown。
///
/// 通过 IShellView → QueryInterface(IID_IFolderView) 获取。
/// 不使用 IShellView.GetItemObject(SVGIO_BACKGROUND, IID_IFolderView2)——
/// 该路径在桌面视图上返回 E_NOINTERFACE。
/// </summary>
[ComImport]
[Guid("CDE725B0-CCC9-4519-9179-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderView
{
    // ═══ IUnknown: vtable 0-2 ═══ (由 CLR 自动处理)

    // ═══ IFolderView 方法: vtable 3-16 ═══

    /// <summary>IFolderView::GetCurrentViewMode — vtable 3</summary>
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);

    /// <summary>IFolderView::SetCurrentViewMode — vtable 4</summary>
    [PreserveSig] int SetCurrentViewMode(uint ViewMode);

    /// <summary>IFolderView::GetFolder — vtable 5。获取此视图对应的 IShellFolder。</summary>
    [PreserveSig] int GetFolder([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    /// <summary>IFolderView::Item — vtable 6。按索引获取项目。</summary>
    [PreserveSig] int Item(uint uItem, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    /// <summary>IFolderView::ItemCount — vtable 7。获取项目数。</summary>
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);

    /// <summary>IFolderView::Items — vtable 8。获取项目枚举器。</summary>
    [PreserveSig] int Items(uint uFlags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);

    /// <summary>IFolderView::GetSelectionMarkedItem — vtable 9</summary>
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);

    /// <summary>IFolderView::GetFocusedItem — vtable 10</summary>
    [PreserveSig] int GetFocusedItem(out int piItem);

    /// <summary>IFolderView::GetItemPosition — vtable 11。获取 PIDL 对应图标的位置。</summary>
    [PreserveSig] int GetItemPosition(nint pidl, out NativePoint ppt);

    /// <summary>IFolderView::GetSpacing — vtable 12</summary>
    [PreserveSig] int GetSpacing(out NativePoint ppt);

    /// <summary>IFolderView::GetDefaultSpacing — vtable 13</summary>
    [PreserveSig] int GetDefaultSpacing(out NativePoint ppt);

    /// <summary>IFolderView::GetAutoArrange — vtable 14</summary>
    [PreserveSig] int GetAutoArrange();

    /// <summary>IFolderView::SelectItem — vtable 15</summary>
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);

    /// <summary>
    /// IFolderView::SelectAndPositionItems — vtable 16。
    /// 注意：本轮 M1 不调用此方法。保留仅为 vtable 完整性。
    /// </summary>
    [PreserveSig] int SelectAndPositionItems(uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] nint[] apidl,
        [MarshalAs(UnmanagedType.LPArray)] NativePoint[] apt,
        uint dwFlags);
}

#endregion

#region IShellFolder — Shell 文件夹

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(nint hwnd, nint pbc,
        [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        out uint pchEaten, out nint ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(nint hwnd, uint grfFlags, [MarshalAs(UnmanagedType.Interface)] out IEnumIDList? ppenumIDList);
    [PreserveSig] int BindToObject(nint pidl, nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int BindToStorage(nint pidl, nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int CompareIDs(nint lParam, nint pidl1, nint pidl2);
    [PreserveSig] int CreateViewObject(nint hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] nint[] apidl,
        ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(nint hwndOwner, uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] nint[] apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out uint rgfReserved,
        [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    [PreserveSig] int GetDisplayNameOf(nint pidl, uint uFlags, out NativeStrRet pName);
    [PreserveSig] int SetNameOf(nint hwnd, nint pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags,
        out nint ppidlOut);
}

#endregion

#region IEnumIDList — PIDL 枚举器

[ComImport]
[Guid("000214F2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, out nint rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIDList? ppenum);
}

#endregion

#region NativeShellMethods

public static class NativeShellMethods
{
    // ─── FindWindowSW 常量 ───
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;

    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public const int CSIDL_DESKTOP = 0x0000;

    // SVGI 常量
    public const uint SVGIO_BACKGROUND = 0x00000000;
    public const uint SVGIO_ALLVIEW = 0xFFFFFFFF;

    // SHGDN 常量
    public const uint SHGDN_NORMAL = 0x0000;
    public const uint SHGDN_FORPARSING = 0x8000;

    // SFGAO 属性常量
    public const uint SFGAO_SYSTEM = 0x00001000;
    public const uint SFGAO_LINK = 0x00010000;
    public const uint SFGAO_READONLY = 0x00040000;
    public const uint SFGAO_HIDDEN = 0x00080000;
    public const uint SFGAO_FILESYSANCESTOR = 0x10000000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;
    public const uint SFGAO_BROWSABLE = 0x08000000;
    public const uint SFGAO_NONENUMERATED = 0x00100000;

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetKnownFolderIDList(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags, nint hToken, out nint ppidl);

    /// <summary>已弃用 API — 仅作为 SHGetKnownFolderIDList 回退。</summary>
    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetSpecialFolderLocation(
        nint hwndOwner, int nFolder, out nint ppidl);

    [DllImport("shell32.dll")]
    public static extern void ILFree(nint pidl);

    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    /// <summary>获取桌面 PIDL（优先 SHGetKnownFolderIDList，回退 SHGetSpecialFolderLocation）。调用者 ILFree 释放。</summary>
    public static nint GetDesktopPidl()
    {
        var hr = SHGetKnownFolderIDList(FOLDERID_Desktop, 0, nint.Zero, out var pidl);
        if (hr == 0 && pidl != nint.Zero) return pidl;
        hr = SHGetSpecialFolderLocation(nint.Zero, CSIDL_DESKTOP, out pidl);
        if (hr == 0 && pidl != nint.Zero) return pidl;
        return nint.Zero;
    }

    /// <summary>将 ITEMIDLIST 复制为 byte[]，供 FindWindowSW VARIANT 使用。</summary>
    public static byte[] PidlToByteArray(nint pidl)
    {
        if (pidl == nint.Zero) return Array.Empty<byte>();
        var totalSize = 0;
        var cursor = pidl;
        while (true)
        {
            var cb = (ushort)Marshal.ReadInt16(cursor);
            totalSize += cb;
            if (cb == 0) break;
            cursor = nint.Add(cursor, cb);
        }
        if (totalSize == 0) return Array.Empty<byte>();
        var bytes = new byte[totalSize];
        Marshal.Copy(pidl, bytes, 0, totalSize);
        return bytes;
    }
}

#endregion

#region 辅助结构体

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint { public int X; public int Y; }

[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
public struct NativeStrRet
{
    [FieldOffset(0)] public uint uType;
    [FieldOffset(4)] public nint pOleStr;
    [FieldOffset(4)] public uint uOffset;
    [FieldOffset(4)] public nint cStr;
}

#endregion
