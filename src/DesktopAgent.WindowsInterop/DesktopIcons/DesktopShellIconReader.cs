using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using CSSTRRET = Windows.Win32.UI.Shell.Common.STRRET;
using static DesktopAgent.WindowsInterop.DesktopIcons.NativeShellMethods;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 通过 Shell COM 接口只读读取 Explorer 桌面视图中的图标项目。
///
/// COM 接口签名均对照 Microsoft.Windows.CsWin32 0.3.106 生成输出验证。
/// CsWin32 生成的 STRRET 在 Windows.Win32.UI.Shell.Common 命名空间下直接使用。
///
/// 主路径：FindWindowSW(CSIDL_DESKTOP) → IServiceProvider → IShellBrowser → IShellView → QI IFolderView
/// 降级路径：Item 枚举 + IServiceProvider → IShellBrowser → IShellView → QI IFolderView
/// </summary>
public sealed class DesktopShellIconReader : IDesktopIconReader
{
    public DesktopIconReadResult ReadDesktopIcons()
    {
        var stopwatch = Stopwatch.StartNew();

        using var shellWindows = new ShellWindowsLateBound();

        var (browser, shellView, folderView, desktopFolder, connectionMethod, primaryEx) =
            FindDesktopView(shellWindows);

        try
        {
            var hr = folderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr != 0 || itemCount <= 0)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(Array.Empty<DesktopIconDescriptor>(),
                    0, connectionMethod, stopwatch.Elapsed, primaryEx);
            }

            // Items → IEnumIDList
            hr = folderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr != 0 || enumObj is not IEnumIDList enumerator)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(Array.Empty<DesktopIconDescriptor>(),
                    0, connectionMethod, stopwatch.Elapsed, primaryEx);
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
            finally { SafeRelease(ref enumerator); }
        }
        finally
        {
            SafeRelease(ref desktopFolder);
            SafeRelease(ref folderView);
            SafeRelease(ref shellView);
            SafeRelease(ref browser);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  查找桌面视图
    // ═══════════════════════════════════════════════════════════

    private static (object? browser, object? view, IFolderView? folderView,
        IShellFolder? folder, ConnectionMethod method, Exception? primaryEx)
        FindDesktopView(ShellWindowsLateBound shellWindows)
    {
        Exception? primaryEx = null;
        try
        {
            var (b, v, fv, f) = FindDesktopViewViaFindWindowSW(shellWindows);
            return (b, v, fv, f, ConnectionMethod.FindWindowSW, null);
        }
        catch (Exception ex) { primaryEx = ex; Debug.WriteLine($"[M1-02] 主路径失败→降级。{FormatException(ex)}"); }

        try
        {
            var (b, v, fv, f) = FindDesktopViewViaEnumeration(shellWindows);
            return (b, v, fv, f, ConnectionMethod.EnumerationFallback, primaryEx);
        }
        catch (Exception fallbackEx)
        {
            var msg = new StringBuilder();
            msg.AppendLine("主路径和降级路径均失败。");
            msg.AppendLine("── 主路径异常 ──"); msg.AppendLine(FormatException(primaryEx!));
            msg.AppendLine("── 降级路径异常 ──"); msg.AppendLine(FormatException(fallbackEx));
            throw new InvalidOperationException(msg.ToString());
        }
    }

    /// <summary>
    /// 主路径：FindWindowSW(CSIDL_DESKTOP) → IServiceProvider → IShellBrowser → IShellView。
    /// 阶段 3 后执行四项 QI 诊断（IUnknown/IShellView/IFolderView/IFolderView2）。
    /// </summary>
    private static (object? browser, object? view, IFolderView folderView, IShellFolder folder)
        FindDesktopViewViaFindWindowSW(ShellWindowsLateBound shellWindows)
    {
        // [1] FindWindowSW — pvarLoc=CSIDL_DESKTOP, pvarLocRoot=VT_EMPTY
        object dispatch;
        int hwnd;
        try { dispatch = shellWindows.FindWindowSW(SWC_DESKTOP, SWFO_NEEDDISPATCH, out hwnd)!; }
        catch (Exception ex) { throw new InvalidOperationException("[1] FindWindowSW 失败 hwnd=?", ex); }
        if (dispatch is null) throw new InvalidOperationException("[1] FindWindowSW 返回 null。");

        // 记录桌面窗口信息
        var hwndClass = GetWindowClassName((nint)hwnd);

        // [2] IServiceProvider → QueryService → IShellBrowser
        var sp = dispatch as IServiceProvider;
        if (sp is null)
        {
            SafeRelease(ref dispatch);
            throw new InvalidOperationException("[2] 不支持 IServiceProvider。");
        }

        object? browserObj;
        int hr;
        try { hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out browserObj); }
        catch (Exception ex)
        {
            SafeRelease(ref dispatch);
            throw new InvalidOperationException("[2] QueryService 异常", ex);
        }
        if (hr != 0 || browserObj is not IShellBrowser browser)
        {
            SafeRelease(ref dispatch);
            throw new InvalidOperationException($"[2] QueryService HR=0x{hr:X8}  hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");
        }

        // [3] QueryActiveShellView → IShellView
        IShellView? shellView;
        try { hr = browser.QueryActiveShellView(out shellView); }
        catch (Exception ex) { throw new InvalidOperationException($"[3] QueryActiveShellView 异常  hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}", ex); }
        if (hr != 0 || shellView is null)
            throw new InvalidOperationException($"[3] QueryActiveShellView HR=0x{hr:X8}  hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");

        // ── [3b] 四项 QI 诊断 ──
        var qiDiag = DiagnoseShellViewInterfaces(shellView, hwnd, hwndClass);

        // [4] QI IShellView → IFolderView
        IFolderView? folderView;
        try { folderView = shellView as IFolderView; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[4] QI IFolderView 失败。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\nQI诊断:\n{qiDiag}", ex);
        }
        if (folderView is null)
            throw new InvalidOperationException(
                $"[4] QI IFolderView 返回 null。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\nQI诊断:\n{qiDiag}");

        // [5] GetFolder → IShellFolder
        hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
        if (hr != 0 || sfObj is not IShellFolder desktopFolder)
            throw new InvalidOperationException($"[5] GetFolder HR=0x{hr:X8}  hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");

        return (browser, shellView, folderView, desktopFolder);
    }

    /// <summary>
    /// 降级路径：Item 枚举 → IServiceProvider → IShellBrowser → IShellView → QI IFolderView。
    /// 每一项不支持时跳过，记录原因。
    /// </summary>
    private static (object? browser, object? view, IFolderView folderView, IShellFolder folder)
        FindDesktopViewViaEnumeration(ShellWindowsLateBound shellWindows)
    {
        var sb = new StringBuilder(256);
        var count = shellWindows.GetCount();
        var skipReasons = new List<string>();

        for (var i = 0; i < count; i++)
        {
            object? item = null;
            try { item = shellWindows.GetItem(i); } catch { continue; }
            if (item is null) continue;

            // Item → IServiceProvider（不直接 QI IShellBrowser）
            var sp = item as IServiceProvider;
            if (sp is null) { SafeRelease(ref item); skipReasons.Add($"#{i}: no IServiceProvider"); continue; }

            var hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
            if (hr != 0 || browserObj is not IShellBrowser browser)
            { SafeRelease(ref item); skipReasons.Add($"#{i}: QueryService HR=0x{hr:X8}"); continue; }

            hr = browser.GetWindow(out var hwnd);
            if (hr != 0 || hwnd == nint.Zero) { SafeRelease(ref browser); skipReasons.Add($"#{i}: GetWindow failed"); continue; }

            sb.Clear(); GetClassName(hwnd, sb, 256);
            if (!string.Equals(sb.ToString(), "Progman", StringComparison.Ordinal))
            { SafeRelease(ref browser); skipReasons.Add($"#{i}: class={sb}"); continue; }

            hr = browser.QueryActiveShellView(out var shellView);
            if (hr != 0 || shellView is null) { SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView HR=0x{hr:X8}"); continue; }

            IFolderView? folderView = shellView as IFolderView;
            if (folderView is null) { SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: QI IFolderView failed"); continue; }

            hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
            if (hr != 0 || sfObj is not IShellFolder desktopFolder)
            { SafeRelease(ref folderView); SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: GetFolder HR=0x{hr:X8}"); continue; }

            return (browser, shellView, folderView, desktopFolder);
        }

        throw new InvalidOperationException(
            $"降级枚举未找到桌面视图。共 {count} 项，跳过原因：{string.Join("; ", skipReasons)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  QI 诊断
    // ═══════════════════════════════════════════════════════════

    private static string DiagnoseShellViewInterfaces(object shellViewObj, int hwnd, string hwndClass)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── QI 诊断 (hwnd=0x{hwnd:X} class={hwndClass}) ──");

        // 获取 IUnknown 身份指针
        nint pUnk = nint.Zero;
        try
        {
            pUnk = Marshal.GetIUnknownForObject(shellViewObj);

            // 依次测试四个接口
            TestQI(pUnk, IidIUnknown, "IUnknown", sb);
            TestQI(pUnk, IidIShellView, "IShellView", sb);
            TestQI(pUnk, IidIFolderView, "IFolderView", sb);
            TestQI(pUnk, IidIFolderView2, "IFolderView2", sb);
        }
        finally
        {
            if (pUnk != nint.Zero) Marshal.Release(pUnk);
        }
        return sb.ToString();
    }

    private static void TestQI(nint pUnk, Guid iid, string label, StringBuilder sb)
    {
        nint ptr = nint.Zero;
        try
        {
            var hr = Marshal.QueryInterface(pUnk, iid, out ptr);
            sb.AppendLine($"  {label,-14} → HR=0x{hr:X8}  ptr=0x{ptr:X}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  {label,-14} → EXCEPTION: {ex.Message}");
        }
        finally
        {
            if (ptr != nint.Zero) Marshal.Release(ptr);
        }
    }

    private static string GetWindowClassName(nint hwnd)
    {
        if (hwnd == nint.Zero) return "(null)";
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    //  图标读取
    // ═══════════════════════════════════════════════════════════

    private static DesktopIconDescriptor? ReadSingleIcon(
        IFolderView folderView, IShellFolder desktopFolder, nint pidl)
    {
        var hr = folderView.GetItemPosition(pidl, out Point pt);
        if (hr != 0) pt = default;

        var position = new PixelPoint(pt.X, pt.Y);
        var displayName = GetDisplayNameSafe(desktopFolder, pidl, SHGDN_NORMAL);
        var parsingName = GetDisplayNameSafe(desktopFolder, pidl, SHGDN_FORPARSING);
        var attributes = GetItemAttributes(desktopFolder, pidl);

        var isFileSystem = (attributes & SFGAO_FILESYSTEM) != 0;
        var isFolder = (attributes & SFGAO_FOLDER) != 0;
        var isLink = (attributes & SFGAO_LINK) != 0;
        var isSystem = (attributes & SFGAO_SYSTEM) != 0;

        return new DesktopIconDescriptor(
            StableId: BuildStableId(parsingName, isFileSystem, isLink, displayName),
            DisplayName: displayName, ParsingName: parsingName,
            ItemKind: ClassifyItem(isLink, isFolder, isFileSystem, displayName, parsingName),
            PixelPosition: position, MonitorDeviceName: null,
            IsSystemItem: isSystem && !isFileSystem, IsFileSystemItem: isFileSystem);
    }

    /// <summary>
    /// STRRET → string。StrRetToBuf 成功后自动释放 pOleStr，
    /// 失败时由 FreeStrRetIfNeeded 手动释放，杜绝 double-free。
    /// </summary>
    private static unsafe string GetDisplayNameSafe(IShellFolder folder, nint pidl, uint flags)
    {
        folder.GetDisplayNameOf(pidl, flags, out CSSTRRET strRet);
        var sb = new StringBuilder(512);
        var hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
        var succeeded = hr == 0 && sb.Length > 0;

        if (succeeded) return sb.ToString();

        // StrRetToBuf 未成功 → 手动回退
        string result;
        if (strRet.uType == 0)
            result = Marshal.PtrToStringUni((nint)strRet.Anonymous.pOleStr.Value) ?? string.Empty;
        else if (strRet.uType == 2)
        {
            var ptr = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref strRet.Anonymous.cStr[0]);
            result = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }
        else
            result = string.Empty;

        // 手动释放（StrRetToBuf 未成功时 pOleStr 未释放）
        FreeStrRetIfNeeded(ref strRet, strRetToBufSucceeded: false);
        return result;
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

    private static string BuildStableId(string? parsingName, bool isFileSystem, bool isLink, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(parsingName))
        {
            if (isFileSystem && parsingName.Length >= 2 && parsingName[1] == ':')
                return parsingName.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            return parsingName;
        }
        return $"display:{displayName}";
    }

    // ═══════════════════════════════════════════════════════════
    //  RCW 生命周期
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 每个 COM 对象的所有者负责调用一次 ReleaseComObject。
    /// 不通过 as 转换引用重复释放（同一 RCW 身份）。
    /// dispatch/sp 是同 RCW → 只释放 dispatch。
    /// shellView/folderView 是同一 RCW → 只释放 shellView。
    /// </summary>
    private static void SafeRelease<T>(ref T? comObject) where T : class
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
            comObject = null;
        }
    }

    internal static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        FormatEx(ex, sb, 0);
        return sb.ToString();
    }

    private static void FormatEx(Exception ex, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append("Type: ").AppendLine(ex.GetType().FullName);
        sb.Append(indent).Append("Message: ").AppendLine(ex.Message);
        if (ex is COMException ce) sb.Append(indent).Append("HRESULT: 0x").AppendLine(ce.ErrorCode.ToString("X8"));
        sb.Append(indent).AppendLine("StackTrace:");
        var lines = (ex.StackTrace ?? "(null)").Split('\n');
        for (var i = 0; i < Math.Min(lines.Length, 8); i++) sb.Append(indent).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
        if (lines.Length > 8) sb.Append(indent).Append("  ... (").Append(lines.Length - 8).AppendLine(" more)");
        if (ex is AggregateException ag) { foreach (var ie in ag.InnerExceptions) { sb.AppendLine(); FormatEx(ie, sb, depth + 1); } }
        else if (ex.InnerException is not null) { sb.AppendLine(); FormatEx(ex.InnerException, sb, depth + 1); }
    }
}
