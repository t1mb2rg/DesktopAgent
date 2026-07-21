using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using CSSTRRET = Windows.Win32.UI.Shell.Common.STRRET;
using static DesktopAgent.WindowsInterop.DesktopIcons.NativeShellMethods;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

public sealed class DesktopShellIconReader : IDesktopIconReader
{
    public DesktopIconReadResult ReadDesktopIcons()
    {
        var stopwatch = Stopwatch.StartNew();
        using var shellWindows = new ShellWindowsLateBound();
        var ctx = new DesktopViewContext();

        try
        {
            var (connectionMethod, primaryEx) = FindDesktopView(shellWindows, ctx);

            var hr = ctx.FolderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr < 0) throw new COMException($"IFolderView.ItemCount failed", hr);
            if (itemCount <= 0)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(Array.Empty<DesktopIconDescriptor>(),
                    0, connectionMethod, stopwatch.Elapsed, primaryEx);
            }

            hr = ctx.FolderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr < 0) throw new COMException($"IFolderView.Items failed", hr);
            if (enumObj is not IEnumIDList enumerator)
                throw new InvalidOperationException("IFolderView.Items did not return IEnumIDList.");
            ctx.SetEnumerator(enumerator);

            var icons = new List<DesktopIconDescriptor>(itemCount);
            var failedCount = 0;
            while (true)
            {
                nint pidl = nint.Zero;
                hr = enumerator.Next(1, out pidl, out var fetched);
                if (hr < 0) throw new COMException($"IEnumIDList.Next failed", hr);
                if (hr == 1 || fetched == 0) break; // S_FALSE or no items
                try
                {
                    var icon = ReadSingleIcon(ctx.FolderView, ctx.DesktopFolder, pidl);
                    if (icon is not null) icons.Add(icon);
                }
                catch { failedCount++; }
                finally { if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl); }
            }
            stopwatch.Stop();
            return new DesktopIconReadResult(icons, failedCount, connectionMethod,
                stopwatch.Elapsed, primaryEx);
        }
        finally { ctx.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════
    //  查找桌面视图
    // ═══════════════════════════════════════════════════════════

    private static (ConnectionMethod method, Exception? primaryEx)
        FindDesktopView(ShellWindowsLateBound shellWindows, DesktopViewContext ctx)
    {
        Exception? primaryEx = null;
        try
        {
            FindDesktopViewViaFindWindowSW(shellWindows, ctx);
            return (ConnectionMethod.FindWindowSW, null);
        }
        catch (Exception ex)
        {
            primaryEx = ex;
            Debug.WriteLine($"[M1-02] 主路径失败→降级。{FormatException(ex)}");
        }

        // 主路径失败，清理后再尝试降级
        ctx.Dispose();

        try
        {
            FindDesktopViewViaEnumeration(shellWindows, ctx);
            return (ConnectionMethod.EnumerationFallback, primaryEx);
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
    /// 主路径。成功后 ctx 拥有 dispatch / browser / shellView / desktopFolder。
    /// </summary>
    private static void FindDesktopViewViaFindWindowSW(
        ShellWindowsLateBound shellWindows, DesktopViewContext ctx)
    {
        // [1] FindWindowSW
        object dispatch = shellWindows.FindWindowSW(SWC_DESKTOP, SWFO_NEEDDISPATCH, out int hwnd)
            ?? throw new InvalidOperationException("[1] FindWindowSW 返回 null。");
        ctx.SetDispatch(dispatch);
        var hwndClass = GetWindowClassName((nint)hwnd);

        // [2] IServiceProvider → QueryService → IShellBrowser
        var sp = dispatch as IServiceProvider
            ?? throw new InvalidOperationException("[2] 不支持 IServiceProvider。");

        int hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
        if (hr < 0 || browserObj is not IShellBrowser browser)
            throw new InvalidOperationException(
                $"[2] QueryService HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");
        ctx.SetBrowser(browser);

        // [3] QueryActiveShellView → IShellView
        hr = browser.QueryActiveShellView(out var shellView);
        if (hr < 0 || shellView is null)
            throw new InvalidOperationException(
                $"[3] QueryActiveShellView HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");
        ctx.SetShellView(shellView);

        // [3b] QI 诊断 (仅 Debug，不改变控制流)
        var qiDiag = DiagnoseShellViewInterfaces(shellView, hwnd, hwndClass);
        Debug.WriteLine(qiDiag);

        // [4] QI IShellView → IFolderView
        if (shellView is not IFolderView folderView)
            throw new InvalidOperationException(
                $"[4] QI IFolderView 失败。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\n{qiDiag}");
        // folderView 与 shellView 是同一 RCW，不单独保存

        // [5] GetFolder → IShellFolder
        hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
        if (hr < 0 || sfObj is not IShellFolder desktopFolder)
            throw new InvalidOperationException(
                $"[5] GetFolder HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");
        ctx.SetDesktopFolder(desktopFolder);
    }

    /// <summary>
    /// 降级路径。成功后 ctx 拥有 dispatch / browser / shellView / desktopFolder。
    /// 每次迭代的 item 在 QueryService 成功后立即释放（不随成功结果返回）。
    /// </summary>
    private static void FindDesktopViewViaEnumeration(
        ShellWindowsLateBound shellWindows, DesktopViewContext ctx)
    {
        var sb = new StringBuilder(256);
        var count = shellWindows.GetCount();
        var skipReasons = new List<string>();

        for (var i = 0; i < count; i++)
        {
            object? item = null;
            try { item = shellWindows.GetItem(i); } catch { continue; }
            if (item is null) continue;

            var sp = item as IServiceProvider;
            if (sp is null) { SafeRelease(ref item); skipReasons.Add($"#{i}: no IServiceProvider"); continue; }

            var hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
            SafeRelease(ref item); // QueryService 完成后 item/sp 不再需要
            if (hr < 0) { skipReasons.Add($"#{i}: QueryService HR=0x{hr:X8}"); continue; }

            var browser = browserObj as IShellBrowser;
            if (browser is null) { skipReasons.Add($"#{i}: QueryService not IShellBrowser"); continue; }

            // <-- browser 在此保证非 null
            hr = browser.GetWindow(out var hwnd);
            if (hr < 0 || hwnd == nint.Zero)
            { SafeRelease(ref browser); skipReasons.Add($"#{i}: GetWindow failed"); continue; }

            sb.Clear(); GetClassName(hwnd, sb, 256);
            if (!string.Equals(sb.ToString(), "Progman", StringComparison.Ordinal))
            { SafeRelease(ref browser); skipReasons.Add($"#{i}: class={sb}"); continue; }

            hr = browser.QueryActiveShellView(out var shellViewObj);
            if (hr < 0) { SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView HR=0x{hr:X8}"); continue; }

            var shellView = shellViewObj as IShellView;
            if (shellView is null) { SafeRelease(ref shellViewObj); SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView not IShellView"); continue; }

            // <-- shellView 在此保证非 null
            var folderView = shellView as IFolderView;
            if (folderView is null) { SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: QI IFolderView null"); continue; }

            hr = folderView.GetFolder(IidIShellFolder, out var sfObj);
            if (hr < 0) { SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: GetFolder HR=0x{hr:X8}"); continue; }

            var desktopFolder = sfObj as IShellFolder;
            if (desktopFolder is null) { SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: GetFolder not IShellFolder"); continue; }

            // 成功 — 所有对象移交 ctx
            ctx.SetBrowser(browser);
            ctx.SetShellView(shellView);
            ctx.SetDesktopFolder(desktopFolder);
            return;
        }

        throw new InvalidOperationException(
            $"降级枚举未找到桌面视图。共 {count} 项，跳过：{string.Join("; ", skipReasons)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  QI 诊断
    // ═══════════════════════════════════════════════════════════

    private static string DiagnoseShellViewInterfaces(object shellViewObj, int hwnd, string hwndClass)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── QI 诊断 (hwnd=0x{hwnd:X} class={hwndClass}) ──");
        nint pUnk = nint.Zero;
        try
        {
            pUnk = Marshal.GetIUnknownForObject(shellViewObj);
            TestQI(pUnk, IidIUnknown, "IUnknown", sb);
            TestQI(pUnk, IidIShellView, "IShellView", sb);
            TestQI(pUnk, IidIFolderView, "IFolderView", sb);
            TestQI(pUnk, IidIFolderView2, "IFolderView2", sb);
        }
        finally { if (pUnk != nint.Zero) Marshal.Release(pUnk); }
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
        catch (Exception ex) { sb.AppendLine($"  {label,-14} → EXCEPTION: {ex.Message}"); }
        finally { if (ptr != nint.Zero) Marshal.Release(ptr); }
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
        if (hr < 0) throw new COMException($"GetItemPosition failed", hr);

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

    private static unsafe string GetDisplayNameSafe(IShellFolder folder, nint pidl, uint flags)
    {
        var hr = folder.GetDisplayNameOf(pidl, flags, out CSSTRRET strRet);
        if (hr < 0) throw new COMException($"GetDisplayNameOf(flags=0x{flags:X4}) failed", hr);

        var sb = new StringBuilder(512);
        hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
        if (hr >= 0 && sb.Length > 0)
            return sb.ToString();

        // StrRetToBuf 失败 → 手动回退
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

        FreeStrRetIfNeeded(ref strRet, strRetToBufSucceeded: false);
        return result;
    }

    private static uint GetItemAttributes(IShellFolder folder, nint pidl)
    {
        uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_LINK | SFGAO_SYSTEM
                    | SFGAO_HIDDEN | SFGAO_READONLY | SFGAO_NONENUMERATED
                    | SFGAO_FILESYSANCESTOR | SFGAO_BROWSABLE;
        var pidls = new[] { pidl };
        var hr = folder.GetAttributesOf(1, pidls, ref attrs);
        if (hr < 0) throw new COMException($"GetAttributesOf failed", hr);
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

// ═══════════════════════════════════════════════════════════════
//  DesktopViewContext — 统一 COM 所有权
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 持有桌面视图查找过程中获得的 COM 对象。
/// Dispose 时按逆序各释放一次。同一 RCW（如 shellView/folderView）
/// 只保存并释放一份。
/// </summary>
internal sealed class DesktopViewContext : IDisposable
{
    private object? _dispatch;         // FindWindowSW 返回
    private IShellBrowser? _browser;   // QueryService 获得
    private IShellView? _shellView;    // QueryActiveShellView 获得（也是 folderView 的 RCW）
    private IShellFolder? _desktopFolder; // GetFolder 获得
    private IEnumIDList? _enumerator;  // IFolderView.Items 获得

    private bool _disposed;

    public IFolderView FolderView => (IFolderView)_shellView!;

    public IShellFolder DesktopFolder => _desktopFolder!;

    public void SetDispatch(object d) => _dispatch = d;
    public void SetBrowser(IShellBrowser b) => _browser = b;
    public void SetShellView(IShellView sv) => _shellView = sv;
    public void SetDesktopFolder(IShellFolder sf) => _desktopFolder = sf;
    public void SetEnumerator(IEnumIDList e) => _enumerator = e;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 逆序释放，每个独立 RCW 一次
        if (_enumerator is not null && Marshal.IsComObject(_enumerator))
            Marshal.ReleaseComObject(_enumerator);
        if (_desktopFolder is not null && Marshal.IsComObject(_desktopFolder))
            Marshal.ReleaseComObject(_desktopFolder);
        if (_shellView is not null && Marshal.IsComObject(_shellView))
            Marshal.ReleaseComObject(_shellView);
        if (_browser is not null && Marshal.IsComObject(_browser))
            Marshal.ReleaseComObject(_browser);
        if (_dispatch is not null && Marshal.IsComObject(_dispatch))
            Marshal.ReleaseComObject(_dispatch);
    }
}
