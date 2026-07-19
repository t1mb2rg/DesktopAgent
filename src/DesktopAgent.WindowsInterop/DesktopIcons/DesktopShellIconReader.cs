using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using static DesktopAgent.WindowsInterop.DesktopIcons.NativeShellMethods;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 通过 Shell COM 接口只读读取 Explorer 桌面视图中的图标项目与物理像素坐标。
///
/// 主路径调用链（阶段 1–6）：
///   [1] ShellWindowsLateBound.FindWindowSW → IDispatch
///   [2] QI IServiceProvider → QueryService(SID_STopLevelBrowser) → IShellBrowser
///   [3] QueryActiveShellView → IShellView
///   [4] QI IShellView → IFolderView   ← 注意：不使用 GetItemObject
///   [5] IFolderView.Items (IEnumIDList) 枚举 PIDL
///   [6] IFolderView.GetItemPosition + IShellFolder 名称解析
///
/// 降级路径：Item 枚举 + IServiceProvider + QI IFolderView
/// </summary>
public sealed class DesktopShellIconReader : IDesktopIconReader
{
    private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellView = new("000214E3-0000-0000-C000-000000000046");
    private static readonly Guid IidIFolderView = new("CDE725B0-CCC9-4519-9179-325D72FAB4CE");
    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIEnumIDList = new("000214F2-0000-0000-C000-000000000046");

    public DesktopIconReadResult ReadDesktopIcons()
    {
        var stopwatch = Stopwatch.StartNew();

        using var shellWindows = new ShellWindowsLateBound();

        var (browser, shellView, folderView, desktopFolder, connectionMethod, primaryEx) =
            FindDesktopView(shellWindows);

        try
        {
            // [5] ItemCount
            var hr = folderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr != 0 || itemCount <= 0)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(
                    Array.Empty<DesktopIconDescriptor>(), 0, connectionMethod,
                    stopwatch.Elapsed, primaryEx);
            }

            // [5] Items → IEnumIDList
            hr = folderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr != 0 || enumObj is not IEnumIDList enumerator)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(
                    Array.Empty<DesktopIconDescriptor>(), 0, connectionMethod,
                    stopwatch.Elapsed, primaryEx);
            }

            try
            {
                var icons = new List<DesktopIconDescriptor>(itemCount);
                var failedCount = 0;

                while (true)
                {
                    nint pidl = nint.Zero;
                    hr = enumerator.Next(1, out pidl, out var fetched);
                    if (hr != 0 || fetched == 0) break;

                    try
                    {
                        var icon = ReadSingleIcon(folderView, desktopFolder, pidl);
                        if (icon is not null) icons.Add(icon);
                    }
                    catch { failedCount++; }
                    finally { if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl); }
                }

                stopwatch.Stop();
                return new DesktopIconReadResult(icons, failedCount, connectionMethod,
                    stopwatch.Elapsed, primaryEx);
            }
            finally { SafeReleaseComObject(enumerator); }
        }
        finally
        {
            SafeReleaseComObject(desktopFolder);
            SafeReleaseComObject(folderView);
            SafeReleaseComObject(shellView);
            SafeReleaseComObject(browser);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  查找桌面视图（含阶段诊断）
    // ═══════════════════════════════════════════════════════════

    private static (IShellBrowser browser, IShellView view, IFolderView folderView,
        IShellFolder folder, ConnectionMethod method, Exception? primaryEx)
        FindDesktopView(ShellWindowsLateBound shellWindows)
    {
        Exception? primaryEx = null;
        try
        {
            var result = FindDesktopViewViaFindWindowSW(shellWindows);
            return (result.browser, result.view, result.folderView, result.folder,
                    ConnectionMethod.FindWindowSW, null);
        }
        catch (Exception ex)
        {
            primaryEx = ex;
            Debug.WriteLine($"[M1-02] 主路径失败 → 降级。{FormatException(ex)}");
        }

        try
        {
            var result = FindDesktopViewViaEnumeration(shellWindows);
            return (result.browser, result.view, result.folderView, result.folder,
                    ConnectionMethod.EnumerationFallback, primaryEx);
        }
        catch (Exception fallbackEx)
        {
            var msg = new StringBuilder();
            msg.AppendLine("主路径 FindWindowSW 和降级枚举均无法找到桌面视图。");
            msg.AppendLine();
            msg.AppendLine("── 主路径异常 ──");
            msg.AppendLine(FormatException(primaryEx!));
            msg.AppendLine();
            msg.AppendLine("── 降级路径异常 ──");
            msg.AppendLine(FormatException(fallbackEx));
            throw new InvalidOperationException(msg.ToString());
        }
    }

    /// <summary>
    /// 主路径 — 每阶段独立 try/catch 以精确定位失败位置。
    /// 阶段 1: FindWindowSW → IDispatch
    /// 阶段 2: QI IServiceProvider → QueryService → IShellBrowser
    /// 阶段 3: QueryActiveShellView → IShellView
    /// 阶段 4: QI IShellView → IFolderView
    /// 阶段 5: IFolderView.GetFolder → IShellFolder (提前获取以便 name 解析)
    /// </summary>
    private static (IShellBrowser browser, IShellView view, IFolderView folderView, IShellFolder folder)
        FindDesktopViewViaFindWindowSW(ShellWindowsLateBound shellWindows)
    {
        nint desktopPidl = nint.Zero;

        try
        {
            desktopPidl = GetDesktopPidl();
            var pidlBytes = PidlToByteArray(desktopPidl);

            // ── [1] FindWindowSW ──
            object dispatch;
            try
            {
                dispatch = shellWindows.FindWindowSW(pidlBytes, SWC_DESKTOP, SWFO_NEEDDISPATCH, out _);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("[1] FindWindowSW 失败", ex);
            }
            if (dispatch is null)
                throw new InvalidOperationException("[1] FindWindowSW 返回 null IDispatch。");

            // ── [2] IServiceProvider → QueryService → IShellBrowser ──
            var sp = dispatch as IServiceProvider;
            if (sp is null)
                throw new InvalidOperationException("[2] FindWindowSW 返回的对象不支持 IServiceProvider。");

            int hr;
            object? browserObj;
            try
            {
                hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out browserObj);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("[2] QueryService(SID_STopLevelBrowser) 失败", ex);
            }
            if (hr != 0 || browserObj is not IShellBrowser browser)
                throw new InvalidOperationException(
                    $"[2] QueryService(SID_STopLevelBrowser) → HRESULT=0x{hr:X8}。");

            SafeReleaseComObject(sp);
            SafeReleaseComObject(dispatch);

            // ── [3] QueryActiveShellView → IShellView ──
            IShellView shellView;
            try
            {
                hr = browser.QueryActiveShellView(out var sv);
                shellView = sv!;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("[3] QueryActiveShellView 失败", ex);
            }
            if (hr != 0 || shellView is null)
                throw new InvalidOperationException(
                    $"[3] QueryActiveShellView → HRESULT=0x{hr:X8}。");

            // ── [4] QI IShellView → IFolderView ──
            IFolderView folderView;
            try
            {
                // 不使用 shellView.GetItemObject(SVGIO_BACKGROUND, …)。
                // 桌面 IShellView 对 GetItemObject 返回 E_NOINTERFACE。
                // 正确做法：直接 QI IFolderView。
                folderView = (IFolderView)shellView;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "[4] QI IShellView → IFolderView 失败。" +
                    " IShellView.GetItemObject 路径不支持桌面视图。", ex);
            }

            // ── [5] GetFolder → IShellFolder ──
            IShellFolder desktopFolder;
            try
            {
                hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
                desktopFolder = (IShellFolder)sfObj!;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("[5] GetFolder(IShellFolder) 失败", ex);
            }
            if (hr != 0 || desktopFolder is null)
                throw new InvalidOperationException(
                    $"[5] GetFolder(IShellFolder) → HRESULT=0x{hr:X8}。");

            return (browser, shellView, folderView, desktopFolder);
        }
        finally
        {
            if (desktopPidl != nint.Zero) ILFree(desktopPidl);
        }
    }

    /// <summary>
    /// 降级路径：ShellWindowsLateBound.GetCount / GetItem 枚举。
    /// 每一项通过 IServiceProvider → IShellBrowser → QI IFolderView 获取。
    /// </summary>
    private static (IShellBrowser browser, IShellView view, IFolderView folderView, IShellFolder folder)
        FindDesktopViewViaEnumeration(ShellWindowsLateBound shellWindows)
    {
        var sb = new StringBuilder(256);
        var count = shellWindows.GetCount();

        for (var i = 0; i < count; i++)
        {
            object item;
            try { item = shellWindows.GetItem(i)!; }
            catch { continue; }
            if (item is null) continue;

            // Item → IServiceProvider → IShellBrowser（不使用直接 QI）
            var sp2 = item as IServiceProvider;
            if (sp2 is null) { SafeReleaseComObject(item); continue; }

            var hr = sp2.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
            if (hr != 0 || browserObj is not IShellBrowser browser)
            { SafeReleaseComObject(sp2); SafeReleaseComObject(item); continue; }

            // Progman 类名检测
            hr = browser.GetWindow(out var hwnd);
            if (hr != 0 || hwnd == nint.Zero) { SafeReleaseComObject(browser); continue; }

            sb.Clear();
            GetClassName(hwnd, sb, 256);
            if (!string.Equals(sb.ToString(), "Progman", StringComparison.Ordinal))
            { SafeReleaseComObject(browser); continue; }

            // QueryActiveShellView → IShellView
            hr = browser.QueryActiveShellView(out var shellView);
            if (hr != 0 || shellView is null) { SafeReleaseComObject(browser); continue; }

            // QI IShellView → IFolderView
            IFolderView folderView;
            try { folderView = (IFolderView)shellView; }
            catch { SafeReleaseComObject(shellView); SafeReleaseComObject(browser); continue; }

            // GetFolder → IShellFolder
            hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
            if (hr != 0 || sfObj is not IShellFolder desktopFolder)
            { SafeReleaseComObject(folderView); SafeReleaseComObject(shellView); SafeReleaseComObject(browser); continue; }

            return (browser, shellView, folderView, desktopFolder);
        }

        throw new InvalidOperationException("降级枚举也未找到桌面视图。");
    }

    // ═══════════════════════════════════════════════════════════
    //  图标读取（IEnumIDList 枚举）
    // ═══════════════════════════════════════════════════════════

    private static DesktopIconDescriptor? ReadSingleIcon(
        IFolderView folderView, IShellFolder desktopFolder, nint pidl)
    {
        // GetItemPosition
        var hr = folderView.GetItemPosition(pidl, out var nativePt);
        if (hr != 0) nativePt = new NativePoint();

        var position = new PixelPoint(nativePt.X, nativePt.Y);
        var displayName = GetDisplayName(desktopFolder, pidl, SHGDN_NORMAL);
        var parsingName = GetDisplayName(desktopFolder, pidl, SHGDN_FORPARSING);
        var attributes = GetItemAttributes(desktopFolder, pidl);

        var isFileSystem = (attributes & SFGAO_FILESYSTEM) != 0;
        var isFolder = (attributes & SFGAO_FOLDER) != 0;
        var isLink = (attributes & SFGAO_LINK) != 0;
        var isSystem = (attributes & SFGAO_SYSTEM) != 0;

        return new DesktopIconDescriptor(
            StableId: BuildStableId(parsingName, isFileSystem, isLink, displayName),
            DisplayName: displayName,
            ParsingName: parsingName,
            ItemKind: ClassifyItem(isLink, isFolder, isFileSystem, displayName, parsingName),
            PixelPosition: position,
            MonitorDeviceName: null,
            IsSystemItem: isSystem && !isFileSystem,
            IsFileSystemItem: isFileSystem);
    }

    private static string GetDisplayName(IShellFolder folder, nint pidl, uint flags)
    {
        folder.GetDisplayNameOf(pidl, flags, out var strRet);
        try { return StrRetToString(pidl, ref strRet); }
        finally { FreeStrRet(ref strRet); }
    }

    private static uint GetItemAttributes(IShellFolder folder, nint pidl)
    {
        uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_LINK | SFGAO_SYSTEM
                    | SFGAO_HIDDEN | SFGAO_READONLY | SFGAO_NONENUMERATED
                    | SFGAO_FILESYSANCESTOR | SFGAO_BROWSABLE;
        var pidls = new[] { pidl };
        folder.GetAttributesOf(1, pidls, ref attrs);
        return attrs;
    }

    private static string StrRetToString(nint pidl, ref NativeStrRet strRet)
    {
        var sb = new StringBuilder(512);
        var hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
        if (hr == 0 && sb.Length > 0) return sb.ToString();
        return strRet.uType switch
        {
            0 => Marshal.PtrToStringUni(strRet.pOleStr) ?? string.Empty,
            2 => Marshal.PtrToStringAnsi(strRet.cStr) ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static void FreeStrRet(ref NativeStrRet strRet)
    {
        if (strRet.uType == 0 && strRet.pOleStr != nint.Zero)
        {
            Marshal.FreeCoTaskMem(strRet.pOleStr);
            strRet.pOleStr = nint.Zero;
        }
    }

    private static ItemKind ClassifyItem(bool isLink, bool isFolder,
        bool isFileSystem, string displayName, string? parsingName)
    {
        if (!isFileSystem) return ItemKind.SystemItem;
        if (isLink) return ItemKind.Shortcut;
        if (isFolder) return ItemKind.Folder;
        if (parsingName is not null && parsingName.StartsWith("::{", StringComparison.Ordinal))
            return ItemKind.SystemItem;
        return ItemKind.File;
    }

    private static string BuildStableId(string? parsingName, bool isFileSystem,
        bool isLink, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(parsingName))
        {
            if (isFileSystem && parsingName.Length >= 2 && parsingName[1] == ':')
                return parsingName.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            return parsingName;
        }
        return $"display:{displayName}";
    }

    internal static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        FormatExceptionRecursive(ex, sb, 0);
        return sb.ToString();
    }

    private static void FormatExceptionRecursive(Exception ex, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append("Type: ").AppendLine(ex.GetType().FullName);
        sb.Append(indent).Append("Message: ").AppendLine(ex.Message);

        if (ex is COMException comEx)
        {
            sb.Append(indent).Append("HRESULT: 0x").AppendLine(comEx.ErrorCode.ToString("X8"));
            sb.Append(indent).Append("Source: ").AppendLine(comEx.Source ?? "(null)");
        }

        sb.Append(indent).AppendLine("StackTrace:");
        var lines = (ex.StackTrace ?? "(null)").Split('\n');
        var maxLines = Math.Min(lines.Length, 8);
        for (var i = 0; i < maxLines; i++)
            sb.Append(indent).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
        if (lines.Length > maxLines)
            sb.Append(indent).Append("  ... (").Append(lines.Length - maxLines).AppendLine(" more lines)");

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            { sb.AppendLine(); FormatExceptionRecursive(inner, sb, depth + 1); }
        }
        else if (ex.InnerException is not null)
        { sb.AppendLine(); FormatExceptionRecursive(ex.InnerException, sb, depth + 1); }
    }

    private static void SafeReleaseComObject(object? comObject)
    {
        if (comObject is not null) Marshal.ReleaseComObject(comObject);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBuf(ref NativeStrRet pstr, nint pidl,
        StringBuilder pszBuf, uint cchBuf);
}
