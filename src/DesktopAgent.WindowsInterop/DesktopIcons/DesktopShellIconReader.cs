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

        var (ctx, connectionMethod, primaryEx) = FindDesktopView(shellWindows);
        using (ctx)
        {
            var hr = ctx.FolderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr < 0) throw new COMException("IFolderView.ItemCount failed", hr);
            if (itemCount <= 0)
            {
                stopwatch.Stop();
                return new DesktopIconReadResult(Array.Empty<DesktopIconDescriptor>(),
                    0, connectionMethod, stopwatch.Elapsed, primaryEx);
            }

            hr = ctx.FolderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr < 0)
            {
                SafeReleaseComObject(ref enumObj);
                throw new COMException("IFolderView.Items failed", hr);
            }
            if (enumObj is not IEnumIDList enumerator)
            {
                SafeReleaseComObject(ref enumObj);
                throw new InvalidOperationException("IFolderView.Items did not return IEnumIDList.");
            }
            ctx.SetEnumerator(enumerator);

            var icons = new List<DesktopIconDescriptor>(itemCount);
            var failedCount = 0;
            var enumIndex = 0;
            while (true)
            {
                nint pidl = nint.Zero;
                hr = enumerator.Next(1, out pidl, out var fetched);
                if (hr < 0) throw new COMException("IEnumIDList.Next failed", hr);
                if (hr == 1 || fetched == 0) break;
                enumIndex++;
                try
                {
                    var icon = ReadSingleIcon(ctx.FolderView, ctx.DesktopFolder, pidl);
                    if (icon is not null) icons.Add(icon);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    var hresultStr = ex is COMException ce ? $" HR=0x{ce.ErrorCode:X8}" : "";
                    Debug.WriteLine($"[M1-02] 图标 #{enumIndex} 读取失败:{hresultStr} {ex.GetType().Name} — {ex.Message}");
                }
                finally { if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl); }
            }
            stopwatch.Stop();
            return new DesktopIconReadResult(icons, failedCount, connectionMethod,
                stopwatch.Elapsed, primaryEx);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  查找桌面视图
    // ═══════════════════════════════════════════════════════════

    private static (DesktopViewContext ctx, ConnectionMethod method, Exception? primaryEx)
        FindDesktopView(ShellWindowsLateBound shellWindows)
    {
        var primaryCtx = new DesktopViewContext();
        try
        {
            FindDesktopViewViaFindWindowSW(shellWindows, primaryCtx);
            return (primaryCtx, ConnectionMethod.FindWindowSW, null);
        }
        catch (Exception ex)
        {
            primaryCtx.Dispose();
            Debug.WriteLine($"[M1-02] 主路径失败→降级。{FormatException(ex)}");

            var fallbackCtx = new DesktopViewContext();
            try
            {
                FindDesktopViewViaEnumeration(shellWindows, fallbackCtx);
                return (fallbackCtx, ConnectionMethod.EnumerationFallback, ex);
            }
            catch (Exception fallbackEx)
            {
                fallbackCtx.Dispose();
                var msg = new StringBuilder();
                msg.AppendLine("主路径和降级路径均失败。");
                msg.AppendLine("── 主路径异常 ──"); msg.AppendLine(FormatException(ex));
                msg.AppendLine("── 降级路径异常 ──"); msg.AppendLine(FormatException(fallbackEx));
                throw new InvalidOperationException(msg.ToString());
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  主路径
    // ═══════════════════════════════════════════════════════════

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
        if (hr < 0) { SafeReleaseComObject(ref browserObj); throw new InvalidOperationException(
            $"[2] QueryService HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); }
        var browser = browserObj as IShellBrowser;
        if (browser is null) { SafeReleaseComObject(ref browserObj); throw new InvalidOperationException(
            $"[2] QueryService 返回非 IShellBrowser。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); }
        ctx.SetBrowser(browser);

        // [3] QueryActiveShellView → IShellView
        hr = browser.QueryActiveShellView(out var shellView);
        if (hr < 0) { SafeRelease(ref shellView); throw new InvalidOperationException(
            $"[3] QueryActiveShellView HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); }
        if (shellView is null) throw new InvalidOperationException(
            $"[3] QueryActiveShellView 返回 null。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}");
        ctx.SetShellView(shellView);

        // [4] Acquire IFolderView（直接 QI + GetItemObject 降级，含 QI 诊断）
        AcquireFolderView(shellView, hwnd, hwndClass, ctx);

        // [5] GetFolder → IShellFolder
        hr = ctx.FolderView.GetFolder(IidIShellFolder, out var sfObj);
        if (hr < 0) { SafeReleaseComObject(ref sfObj); throw new InvalidOperationException(
            $"[5] GetFolder HR=0x{hr:X8} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); }
        var desktopFolder = sfObj as IShellFolder;
        if (desktopFolder is null) { SafeReleaseComObject(ref sfObj); throw new InvalidOperationException(
            $"[5] GetFolder 返回非 IShellFolder。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); }
        ctx.SetDesktopFolder(desktopFolder);
    }

    // ═══════════════════════════════════════════════════════════
    //  降级路径
    // ═══════════════════════════════════════════════════════════

    private static void FindDesktopViewViaEnumeration(
        ShellWindowsLateBound shellWindows, DesktopViewContext ctx)
    {
        var sb = new StringBuilder(256);
        var count = shellWindows.GetCount();
        if (count == 0)
            throw new InvalidOperationException(
                "ShellWindows 当前没有可枚举的普通 Shell 窗口。" +
                "桌面对象已由 FindWindowSW 找到，但 IFolderView 获取失败。" +
                "请检查 AcquireFolderView 诊断。");

        var skipReasons = new List<string>();

        for (var i = 0; i < count; i++)
        {
            object? item = null;
            try { item = shellWindows.GetItem(i); } catch { continue; }
            if (item is null) continue;

            var sp = item as IServiceProvider;
            if (sp is null) { SafeReleaseComObject(ref item); skipReasons.Add($"#{i}: no IServiceProvider"); continue; }

            var hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
            SafeReleaseComObject(ref item);
            if (hr < 0) { SafeReleaseComObject(ref browserObj); skipReasons.Add($"#{i}: QueryService HR=0x{hr:X8}"); continue; }
            var browser = browserObj as IShellBrowser;
            if (browser is null) { SafeReleaseComObject(ref browserObj); skipReasons.Add($"#{i}: QueryService not IShellBrowser"); continue; }

            hr = browser.GetWindow(out var hwnd);
            if (hr < 0 || hwnd == nint.Zero) { SafeRelease(ref browser); skipReasons.Add($"#{i}: GetWindow failed"); continue; }

            sb.Clear(); GetClassName(hwnd, sb, 256);
            if (!string.Equals(sb.ToString(), "Progman", StringComparison.Ordinal))
            { SafeRelease(ref browser); skipReasons.Add($"#{i}: class={sb}"); continue; }

            hr = browser.QueryActiveShellView(out var shellViewObj);
            if (hr < 0) { SafeRelease(ref shellViewObj); SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView HR=0x{hr:X8}"); continue; }
            var shellView = shellViewObj as IShellView;
            if (shellView is null) { SafeRelease(ref shellViewObj); SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView not IShellView"); continue; }

            // Acquire IFolderView（同一套 AcquireFolderView 逻辑）
            try
            {
                AcquireFolderView(shellView, (int)hwnd, sb.ToString(), ctx);
            }
            catch
            {
                SafeRelease(ref shellView); SafeRelease(ref browser);
                skipReasons.Add($"#{i}: AcquireFolderView failed");
                continue;
            }

            // GetFolder → IShellFolder
            hr = ctx.FolderView.GetFolder(IidIShellFolder, out var sfObj);
            if (hr < 0) { SafeReleaseComObject(ref sfObj); SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: GetFolder HR=0x{hr:X8}"); continue; }
            var desktopFolder = sfObj as IShellFolder;
            if (desktopFolder is null) { SafeReleaseComObject(ref sfObj); SafeRelease(ref shellView); SafeRelease(ref browser); skipReasons.Add($"#{i}: GetFolder not IShellFolder"); continue; }

            ctx.SetBrowser(browser);
            ctx.SetShellView(shellView);
            ctx.SetDesktopFolder(desktopFolder);
            return;
        }

        throw new InvalidOperationException(
            $"降级枚举未找到桌面视图。共 {count} 项，跳过：{string.Join("; ", skipReasons)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  AcquireFolderView — 统一 IFolderView 获取（主路径+降级共用）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从 IShellView 获取 IFolderView。
    /// 1. 四项 QI 诊断
    /// 2. Direct QI（shellView as IFolderView），捕获异常后仍尝试 GetItemObject
    /// 3. GetItemObject(SVGIO_BACKGROUND, IID_IFolderView)
    /// 4. 通过 HaveSameComIdentity 比较 IUnknown identity，决定释放策略
    /// 5. 所有权仅通过 ctx.SetFolderView 移交；异常时局部清理
    /// </summary>
    private static void AcquireFolderView(
        IShellView shellView, int hwnd, string hwndClass, DesktopViewContext ctx)
    {
        var qiDiag = DiagnoseShellViewInterfaces(shellView, hwnd, hwndClass);

        // ── 2. Direct QI ──
        Exception? directQiEx = null;
        IFolderView? folderView = null;
        try { folderView = shellView as IFolderView; }
        catch (Exception ex) { directQiEx = ex; }

        if (folderView is not null)
        {
            bool sameIdentity = HaveSameComIdentity(shellView, folderView);
            ctx.SetFolderView(folderView, sameComIdentityAsShellView: sameIdentity);
            return;
        }

        // 构建 direct QI 失败描述
        var directQiDesc = directQiEx is not null
            ? $"Direct QI: {directQiEx.GetType().Name}" +
              (directQiEx is COMException dce ? $" HR=0x{dce.ErrorCode:X8}" : "") +
              $" — {directQiEx.Message}"
            : "Direct QI: null";

        // ── 3. GetItemObject 降级 ──
        var hr = shellView.GetItemObject(SVGIO_BACKGROUND, IidIFolderView, out var fvObj);

        // 提取 folderView（可能在异常路径中需要释放 fvObj）
        IFolderView? gioFolderView = null;
        try
        {
            if (hr < 0) throw new InvalidOperationException(
                $"[4] IFolderView 获取失败。\nhwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\n" +
                $"{directQiDesc}\nGetItemObject(SVGIO_BACKGROUND, IID_IFolderView): HR=0x{hr:X8}\n" + qiDiag);

            gioFolderView = fvObj as IFolderView;
            if (gioFolderView is null) throw new InvalidOperationException(
                $"[4] IFolderView 获取失败。\nhwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\n" +
                $"{directQiDesc}\nGetItemObject(SVGIO_BACKGROUND, IID_IFolderView): HR=0x{hr:X8} but not IFolderView\n" + qiDiag);

            // 运行时比较 COM identity（不依赖获取方式假设）
            bool sameIdentity = HaveSameComIdentity(shellView, gioFolderView);

            // 所有权移交 ctx
            ctx.SetFolderView(gioFolderView, sameComIdentityAsShellView: sameIdentity);
        }
        catch
        {
            // SetFolderView 失败或比较/类型转换失败 → 释放尚未移交的 folderView
            // 仅当 gioFolderView 与 shellView 是不同 COM identity 时才释放
            // （同 identity 时 shellView 会由 ctx 外部释放）
            if (gioFolderView is not null && !HaveSameComIdentity(shellView, gioFolderView))
                SafeRelease<IFolderView>(ref gioFolderView);
            else if (gioFolderView is null)
                SafeReleaseComObject(ref fvObj); // fvObj 不是 IFolderView，释放原始 object
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  QI 诊断
    // ═══════════════════════════════════════════════════════════

    private static string DiagnoseShellViewInterfaces(object shellViewObj, int hwnd, string hwndClass)
    {
        var sb = new StringBuilder();
        sb.Append($"── QI 诊断 hwnd=0x{hwnd:X} class={hwndClass} ──");
        nint pUnk = nint.Zero;
        try
        {
            pUnk = Marshal.GetIUnknownForObject(shellViewObj);
            sb.AppendLine();
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
        if (hr < 0) throw new COMException("GetItemPosition failed", hr);

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

    private static string GetDisplayNameSafe(IShellFolder folder, nint pidl, uint flags)
    {
        var hr = folder.GetDisplayNameOf(pidl, flags, out CSSTRRET strRet);
        if (hr < 0) throw new COMException($"GetDisplayNameOf(flags=0x{flags:X4}) failed", hr);
        var sb = new StringBuilder(512);
        hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity);
        if (hr < 0) throw new COMException("StrRetToBuf failed", hr);
        return sb.ToString();
    }

    private static uint GetItemAttributes(IShellFolder folder, nint pidl)
    {
        uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_LINK | SFGAO_SYSTEM
                    | SFGAO_HIDDEN | SFGAO_READONLY | SFGAO_NONENUMERATED
                    | SFGAO_FILESYSANCESTOR | SFGAO_BROWSABLE;
        var pidls = new[] { pidl };
        var hr = folder.GetAttributesOf(1, pidls, ref attrs);
        if (hr < 0) throw new COMException("GetAttributesOf failed", hr);
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
    //  COM 身份与生命周期
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 通过比较 IUnknown 指针判断两个 COM 封装对象是否为同一 COM identity。
    /// 每个 GetIUnknownForObject 增加的引用计数各 Release 一次。
    /// </summary>
    private static bool HaveSameComIdentity(object left, object right)
    {
        nint leftUnk = nint.Zero;
        nint rightUnk = nint.Zero;
        try
        {
            leftUnk = Marshal.GetIUnknownForObject(left);
            rightUnk = Marshal.GetIUnknownForObject(right);
            return leftUnk == rightUnk;
        }
        finally
        {
            if (rightUnk != nint.Zero) Marshal.Release(rightUnk);
            if (leftUnk != nint.Zero) Marshal.Release(leftUnk);
        }
    }

    private static void SafeRelease<T>(ref T? comObject) where T : class
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        { Marshal.ReleaseComObject(comObject); comObject = null; }
    }

    private static void SafeReleaseComObject(ref object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        { Marshal.ReleaseComObject(comObj); comObj = null; }
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
//  DesktopViewContext
// ═══════════════════════════════════════════════════════════════

internal sealed class DesktopViewContext : IDisposable
{
    private object? _dispatch;
    private IShellBrowser? _browser;
    private IShellView? _shellView;
    private IFolderView? _folderView;
    private bool _folderViewSameComIdentity;
    private IShellFolder? _desktopFolder;
    private IEnumIDList? _enumerator;
    private bool _disposed;

    public IFolderView FolderView => _folderView!;
    public IShellFolder DesktopFolder => _desktopFolder!;

    public void SetDispatch(object d) { ThrowIfDisposed(); ThrowIfAlreadySet(_dispatch); _dispatch = d; }
    public void SetBrowser(IShellBrowser b) { ThrowIfDisposed(); ThrowIfAlreadySet(_browser); _browser = b; }
    public void SetShellView(IShellView sv) { ThrowIfDisposed(); ThrowIfAlreadySet(_shellView); _shellView = sv; }

    public void SetFolderView(IFolderView fv, bool sameComIdentityAsShellView)
    {
        ThrowIfDisposed();
        ThrowIfAlreadySet(_folderView);
        _folderView = fv;
        _folderViewSameComIdentity = sameComIdentityAsShellView;
    }

    public void SetDesktopFolder(IShellFolder sf) { ThrowIfDisposed(); ThrowIfAlreadySet(_desktopFolder); _desktopFolder = sf; }
    public void SetEnumerator(IEnumIDList e) { ThrowIfDisposed(); ThrowIfAlreadySet(_enumerator); _enumerator = e; }

    private static void ThrowIfAlreadySet(object? field)
    {
        if (field is not null) throw new InvalidOperationException(
            "DesktopViewContext field already set — overwrite would leak a COM object.");
    }

    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 逆序释放。folderView 若是独立 RCW 则先于 shellView 释放
        if (_enumerator is not null && Marshal.IsComObject(_enumerator))
            Marshal.ReleaseComObject(_enumerator);
        if (_desktopFolder is not null && Marshal.IsComObject(_desktopFolder))
            Marshal.ReleaseComObject(_desktopFolder);
        if (_folderView is not null && !_folderViewSameComIdentity && Marshal.IsComObject(_folderView))
            Marshal.ReleaseComObject(_folderView);
        if (_shellView is not null && Marshal.IsComObject(_shellView))
            Marshal.ReleaseComObject(_shellView);
        if (_browser is not null && Marshal.IsComObject(_browser))
            Marshal.ReleaseComObject(_browser);
        if (_dispatch is not null && Marshal.IsComObject(_dispatch))
            Marshal.ReleaseComObject(_dispatch);
    }
}
