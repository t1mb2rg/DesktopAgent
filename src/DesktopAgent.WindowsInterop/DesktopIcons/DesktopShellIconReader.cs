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
            if (hr < 0) throw new COMException("IFolderView2.ItemCount failed", hr);
            if (itemCount <= 0)
            { stopwatch.Stop(); return new DesktopIconReadResult(Array.Empty<DesktopIconDescriptor>(), 0, connectionMethod, stopwatch.Elapsed, primaryEx); }

            hr = ctx.FolderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr < 0) { SafeReleaseComObject(ref enumObj); throw new COMException("IFolderView2.Items failed", hr); }
            if (enumObj is not IEnumIDList enumerator) { SafeReleaseComObject(ref enumObj); throw new InvalidOperationException("IFolderView2.Items did not return IEnumIDList."); }
            ctx.SetEnumerator(enumerator);

            var icons = new List<DesktopIconDescriptor>(itemCount);
            var failedCount = 0; var enumIndex = 0;
            while (true)
            {
                nint pidl = nint.Zero;
                hr = enumerator.Next(1, out pidl, out var fetched);
                if (hr < 0) throw new COMException("IEnumIDList.Next failed", hr);
                if (hr == 1 || fetched == 0) break;
                enumIndex++;
                try { var icon = ReadSingleIcon(ctx.FolderView, ctx.DesktopFolder, pidl); if (icon is not null) icons.Add(icon); }
                catch (Exception ex)
                {
                    failedCount++; var hresultStr = ex is COMException ce ? $" HR=0x{ce.ErrorCode:X8}" : "";
                    Debug.WriteLine($"[M1-02] 图标 #{enumIndex} 读取失败:{hresultStr} {ex.GetType().Name} — {ex.Message}");
                }
                finally { if (pidl != nint.Zero) Marshal.FreeCoTaskMem(pidl); }
            }
            stopwatch.Stop();
            return new DesktopIconReadResult(icons, failedCount, connectionMethod, stopwatch.Elapsed, primaryEx);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  查找桌面视图
    // ═══════════════════════════════════════════════════════════

    internal static (DesktopViewContext ctx, ConnectionMethod method, Exception? primaryEx)
        FindDesktopView(ShellWindowsLateBound shellWindows)
    {
        var primaryCtx = new DesktopViewContext();
        try
        {
            FindDesktopViewViaFindWindowSW(shellWindows, primaryCtx);
            return (primaryCtx, ConnectionMethod.FindWindowSW, null);
        }
        catch (Exception primaryEx)
        {
            primaryCtx.Dispose();
            Debug.WriteLine($"[M1-02] 主路径失败→降级。{FormatException(primaryEx)}");

            try
            {
                var fallbackCtx = FindDesktopViewViaEnumeration(shellWindows);
                return (fallbackCtx, ConnectionMethod.EnumerationFallback, primaryEx);
            }
            catch (Exception fallbackEx)
            {
                var msg = new StringBuilder();
                msg.AppendLine("主路径和降级路径均失败。");
                msg.AppendLine("── 主路径异常 ──"); msg.AppendLine(FormatException(primaryEx));
                msg.AppendLine("── 降级路径异常 ──"); msg.AppendLine(FormatException(fallbackEx));
                throw new InvalidOperationException(
                    msg.ToString(), new AggregateException(primaryEx, fallbackEx));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  主路径
    // ═══════════════════════════════════════════════════════════

    private static void FindDesktopViewViaFindWindowSW(ShellWindowsLateBound shellWindows, DesktopViewContext ctx)
    {
        object dispatch = shellWindows.FindWindowSW(SWC_DESKTOP, SWFO_NEEDDISPATCH, out int hwnd)
            ?? throw new InvalidOperationException("[1] FindWindowSW 返回 null。");
        ctx.SetDispatch(dispatch); var hwndClass = GetWindowClassName((nint)hwnd);

        var sp = dispatch as IServiceProvider ?? throw new InvalidOperationException("[2] 不支持 IServiceProvider。");
        int hr = sp.QueryService(SID_STopLevelBrowser, IidIShellBrowser, out var browserObj);
        if (hr < 0) { SafeReleaseComObject(ref browserObj); throw new InvalidOperationException($"[2] QueryService HR=0x{hr:X8}"); }
        var browser = browserObj as IShellBrowser;
        if (browser is null) { SafeReleaseComObject(ref browserObj); throw new InvalidOperationException("[2] QueryService 返回非 IShellBrowser。"); }
        ctx.SetBrowser(browser);

        hr = browser.QueryActiveShellView(out var shellView);
        if (hr < 0) { SafeRelease(ref shellView); throw new InvalidOperationException($"[3] QueryActiveShellView HR=0x{hr:X8}"); }
        if (shellView is null) throw new InvalidOperationException("[3] QueryActiveShellView 返回 null。");
        ctx.SetShellView(shellView);

        FolderViewAcquisition acq;
        try { acq = AcquireFolderView(shellView, hwnd, hwndClass); }
        catch (Exception ex) { throw new InvalidOperationException($"[4] AcquireFolderView 失败。hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}", ex); }

        try { ctx.SetFolderView(acq.FolderView, acq.IdentityRelation); }
        catch
        { ReleaseAcquiredFolderView(ref acq); throw; }

        hr = ctx.FolderView.GetFolder(IidIShellFolder, out var sfObj);
        if (hr < 0) { SafeReleaseComObject(ref sfObj); throw new InvalidOperationException($"[5] GetFolder HR=0x{hr:X8}"); }
        var desktopFolder = sfObj as IShellFolder;
        if (desktopFolder is null) { SafeReleaseComObject(ref sfObj); throw new InvalidOperationException("[5] GetFolder 返回非 IShellFolder。"); }
        ctx.SetDesktopFolder(desktopFolder);
    }

    // ═══════════════════════════════════════════════════════════
    //  降级路径 — 每个候选独立 context
    // ═══════════════════════════════════════════════════════════

    private static DesktopViewContext FindDesktopViewViaEnumeration(ShellWindowsLateBound shellWindows)
    {
        var sb = new StringBuilder(256);
        var count = shellWindows.GetCount();
        if (count == 0) throw new InvalidOperationException("ShellWindows 当前没有可枚举的普通 Shell 窗口。桌面对象已由 FindWindowSW 找到，但 IFolderView2 获取失败。");
        var skipReasons = new List<string>();

        for (var i = 0; i < count; i++)
        {
            object? item = null; try { item = shellWindows.GetItem(i); } catch { continue; }
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
            sb.Clear(); GetClassName(hwnd, sb, 256); var hwndClass = sb.ToString();
            if (!string.Equals(hwndClass, "Progman", StringComparison.Ordinal)) { SafeRelease(ref browser); skipReasons.Add($"#{i}: class={hwndClass}"); continue; }
            hr = browser.QueryActiveShellView(out var shellViewObj);
            if (hr < 0) { SafeRelease(ref shellViewObj); SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView HR=0x{hr:X8}"); continue; }
            var shellView = shellViewObj as IShellView;
            if (shellView is null) { SafeRelease(ref shellViewObj); SafeRelease(ref browser); skipReasons.Add($"#{i}: QueryActiveShellView not IShellView"); continue; }

            var candidateCtx = new DesktopViewContext();
            try
            {
                candidateCtx.SetBrowser(browser);
                candidateCtx.SetShellView(shellView);
                FolderViewAcquisition acq;
                try { acq = AcquireFolderView(shellView, (int)hwnd, hwndClass); }
                catch (Exception ex)
                { var hs = ex is COMException ce ? $" HR=0x{ce.ErrorCode:X8}" : ""; skipReasons.Add($"#{i}: AcquireFolderView{hs} {ex.GetType().Name} — {ex.Message} hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); candidateCtx.Dispose(); continue; }

                try { candidateCtx.SetFolderView(acq.FolderView, acq.IdentityRelation); }
                catch { ReleaseAcquiredFolderView(ref acq); candidateCtx.Dispose(); skipReasons.Add($"#{i}: SetFolderView failed hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); continue; }

                hr = candidateCtx.FolderView.GetFolder(IidIShellFolder, out var sfObj);
                if (hr < 0) { SafeReleaseComObject(ref sfObj); skipReasons.Add($"#{i}: GetFolder HR=0x{hr:X8}"); candidateCtx.Dispose(); continue; }
                var desktopFolder = sfObj as IShellFolder;
                if (desktopFolder is null) { SafeReleaseComObject(ref sfObj); skipReasons.Add($"#{i}: GetFolder not IShellFolder"); candidateCtx.Dispose(); continue; }
                candidateCtx.SetDesktopFolder(desktopFolder);
                return candidateCtx;
            }
            catch { candidateCtx.Dispose(); skipReasons.Add($"#{i}: unexpected hwnd={hwnd}(0x{hwnd:X}) class={hwndClass}"); continue; }
        }
        throw new InvalidOperationException($"降级枚举未找到桌面视图。共 {count} 项，跳过：{string.Join("; ", skipReasons)}");
    }

    // ═══════════════════════════════════════════════════════════
    //  FolderViewAcquisition
    // ═══════════════════════════════════════════════════════════

    internal sealed record FolderViewAcquisition(
        IFolderView2Compat FolderView,
        ComIdentityRelation IdentityRelation,
        string Method,
        string Diagnostics);

    // ═══════════════════════════════════════════════════════════
    //  AcquireFolderView — 不接触 DesktopViewContext
    // ═══════════════════════════════════════════════════════════

    private static FolderViewAcquisition AcquireFolderView(IShellView shellView, int hwnd, string hwndClass)
    {
        var qiDiag = DiagnoseShellViewInterfaces(shellView, hwnd, hwndClass);

        // ── Direct QI ──
        Exception? directQiEx = null; IFolderView2Compat? folderView = null;
        try { folderView = shellView as IFolderView2Compat; } catch (Exception ex) { directQiEx = ex; }

        if (folderView is not null)
        {
            var (rel, cmpEx) = CompareComIdentity(shellView, folderView);
            var diag = BuildAcquisitionDiagnostics(qiDiag, "DirectQI(IFolderView2)", rel, cmpEx);
            return new FolderViewAcquisition(folderView, rel, "DirectQI(IFolderView2)", diag);
        }

        var directQiDesc = directQiEx is not null
            ? $"Direct QI: {directQiEx.GetType().Name}" + (directQiEx is COMException dce ? $" HR=0x{dce.ErrorCode:X8}" : "") + $" — {directQiEx.Message}"
            : "Direct QI: null";

        // ── GetItemObject 降级 ──
        var hr = shellView.GetItemObject(SVGIO_BACKGROUND, IidIFolderView2, out var fvObj);
        if (hr < 0) { SafeReleaseComObject(ref fvObj); throw new InvalidOperationException($"[4] IFolderView2 获取失败。\nhwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\n{directQiDesc}\nGetItemObject: HR=0x{hr:X8}\n{qiDiag}"); }

        IFolderView2Compat? gioFv = null; ComIdentityRelation? cachedRel = null;
        try
        {
            gioFv = fvObj as IFolderView2Compat;
            if (gioFv is null) { SafeReleaseComObject(ref fvObj); throw new InvalidOperationException($"[4] IFolderView2 获取失败。\nhwnd={hwnd}(0x{hwnd:X}) class={hwndClass}\n{directQiDesc}\nGetItemObject: HR=0x{hr:X8} but not IFolderView2Compat\n{qiDiag}"); }

            var (rel, cmpEx) = CompareComIdentity(shellView, gioFv);
            cachedRel = rel;
            var diag = BuildAcquisitionDiagnostics(qiDiag, "GetItemObject(IFolderView2)", rel, cmpEx);
            return new FolderViewAcquisition(gioFv, rel, "GetItemObject(IFolderView2)", diag);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // identity 比较或构造异常 → 用已缓存的 cachedRel 清理，不重新比较
            // cachedRel 为 null 表示 CompareComIdentity 之前就失败了
            ReleaseAfterFailedAcquisition(gioFv, ref fvObj, cachedRel);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Identity 比较（安全三态）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 比较两个 COM 对象的 IUnknown identity。
    /// finally 中 Marshal.Release 的异常不会覆盖原始比较异常。
    /// 任一清理异常发生时 Relation 强制为 Unknown。
    /// Unknown 触发条件：GetIUnknownForObject 失败、指针比较异常、
    /// 或 IUnknown 引用清理异常（非进程级致命异常）。
    /// </summary>
    private static (ComIdentityRelation Relation, Exception? ComparisonException)
        CompareComIdentity(object left, object right)
    {
        nint leftUnk = nint.Zero;
        nint rightUnk = nint.Zero;

        var relation = ComIdentityRelation.Unknown;
        Exception? comparisonException = null;
        Exception? cleanupException = null;

        try
        {
            leftUnk = Marshal.GetIUnknownForObject(left);
            rightUnk = Marshal.GetIUnknownForObject(right);

            relation = leftUnk == rightUnk
                ? ComIdentityRelation.Same
                : ComIdentityRelation.Different;
        }
        catch (Exception ex)
        {
            relation = ComIdentityRelation.Unknown;
            comparisonException = ex;
        }
        finally
        {
            // 分别尝试释放两个 IUnknown 引用，一个失败不阻止另一个
            try
            {
                if (rightUnk != nint.Zero)
                    Marshal.Release(rightUnk);
            }
            catch (Exception ex) { cleanupException = ex; }

            try
            {
                if (leftUnk != nint.Zero)
                    Marshal.Release(leftUnk);
            }
            catch (Exception ex)
            {
                cleanupException = cleanupException is null
                    ? ex
                    : new AggregateException(cleanupException, ex);
            }
        }

        if (cleanupException is not null)
        {
            relation = ComIdentityRelation.Unknown;

            comparisonException = comparisonException is null
                ? cleanupException
                : new AggregateException(
                    "COM identity 比较和 IUnknown 清理均发生异常。",
                    comparisonException, cleanupException);
        }

        return (relation, comparisonException);
    }

    private static string BuildAcquisitionDiagnostics(string qiDiag, string method, ComIdentityRelation rel, Exception? cmpEx)
    {
        var sb = new StringBuilder(qiDiag);
        sb.AppendLine();
        sb.Append($"  Method={method}  Identity={rel}");
        if (cmpEx is not null)
        {
            FormatIdentityException(cmpEx, "  IdentityException", sb);
        }
        return sb.ToString();
    }

    private static void FormatIdentityException(Exception ex, string label, StringBuilder sb)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine();
                sb.Append($"{label}: {inner.GetType().Name}");
                if (inner is COMException ce) sb.Append($" HR=0x{ce.ErrorCode:X8}");
                sb.Append($" — {inner.Message}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.Append($"{label}: {ex.GetType().Name}");
            if (ex is COMException ce) sb.Append($" HR=0x{ce.ErrorCode:X8}");
            sb.Append($" — {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  所有权移交失败清理
    // ═══════════════════════════════════════════════════════════

    /// <summary>SetFolderView 失败时，根据已缓存的 IdentityRelation 清理。</summary>
    private static void ReleaseAcquiredFolderView(ref FolderViewAcquisition acq)
    {
        switch (acq.IdentityRelation)
        {
            case ComIdentityRelation.Different:
                { var fv = acq.FolderView; SafeRelease(ref fv); }
                break;
            case ComIdentityRelation.Same:
                // 不单独释放，由 shellView 所有者释放
                break;
            case ComIdentityRelation.Unknown:
                // 禁止 ReleaseComObject，清除局部强引用交由 GC
                Debug.WriteLine($"[M1-02] 无法确定 folderView COM identity，延迟给 RCW/GC。Method={acq.Method}");
                break;
        }
    }

    /// <summary>AcquireFolderView 内部异常时，用已缓存的 cachedRel 清理（不重新比较）。</summary>
    private static void ReleaseAfterFailedAcquisition(IFolderView2Compat? gioFv, ref object? fvObj, ComIdentityRelation? cachedRel)
    {
        if (cachedRel.HasValue)
        {
            switch (cachedRel.Value)
            {
                case ComIdentityRelation.Different:
                    { var fv = gioFv; SafeRelease(ref fv); }
                    break;
                case ComIdentityRelation.Same:
                    break;
                case ComIdentityRelation.Unknown:
                    Debug.WriteLine("[M1-02] identity Unknown，folderView 延迟给 RCW/GC。");
                    break;
            }
        }
        else
        {
            // 连 identity 都没比较出来 → 不猜测，清理 object 别名
            if (gioFv is null) SafeReleaseComObject(ref fvObj);
            else Debug.WriteLine("[M1-02] identity comparison never completed，folderView 延迟给 RCW/GC。");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  QI 诊断
    // ═══════════════════════════════════════════════════════════

    private static string DiagnoseShellViewInterfaces(object shellViewObj, int hwnd, string hwndClass)
    {
        var sb = new StringBuilder(); sb.Append($"── QI 診断 hwnd=0x{hwnd:X} class={hwndClass} ──"); nint pUnk = nint.Zero;
        try { pUnk = Marshal.GetIUnknownForObject(shellViewObj); sb.AppendLine(); TestQI(pUnk, IidIUnknown, "IUnknown", sb); TestQI(pUnk, IidIShellView, "IShellView", sb); TestQI(pUnk, IidIFolderView, "IFolderView", sb); TestQI(pUnk, IidIFolderView2, "IFolderView2", sb); }
        finally { if (pUnk != nint.Zero) Marshal.Release(pUnk); }
        return sb.ToString();
    }
    private static void TestQI(nint pUnk, Guid iid, string label, StringBuilder sb)
    { nint ptr = nint.Zero; try { var hr = Marshal.QueryInterface(pUnk, iid, out ptr); sb.AppendLine($"  {label,-14} → HR=0x{hr:X8}  ptr=0x{ptr:X}"); } catch (Exception ex) { sb.AppendLine($"  {label,-14} → EXCEPTION: {ex.Message}"); } finally { if (ptr != nint.Zero) Marshal.Release(ptr); } }
    private static string GetWindowClassName(nint hwnd)
    { if (hwnd == nint.Zero) return "(null)"; var sb = new StringBuilder(256); GetClassName(hwnd, sb, 256); return sb.ToString(); }

    // ═══════════════════════════════════════════════════════════
    //  图标读取
    // ═══════════════════════════════════════════════════════════

    internal static DesktopIconDescriptor? ReadSingleIcon(IFolderView2Compat folderView, IShellFolder desktopFolder, nint pidl)
    {
        var hr = folderView.GetItemPosition(pidl, out Point pt); if (hr < 0) throw new COMException("GetItemPosition failed", hr);
        var position = new PixelPoint(pt.X, pt.Y);
        var displayName = GetDisplayNameSafe(desktopFolder, pidl, SHGDN_NORMAL);
        var parsingName = GetDisplayNameSafe(desktopFolder, pidl, SHGDN_FORPARSING);
        var attributes = GetItemAttributes(desktopFolder, pidl);
        var isFileSystem = (attributes & SFGAO_FILESYSTEM) != 0; var isFolder = (attributes & SFGAO_FOLDER) != 0; var isLink = (attributes & SFGAO_LINK) != 0; var isSystem = (attributes & SFGAO_SYSTEM) != 0;
        return new DesktopIconDescriptor(StableId: BuildStableId(parsingName, isFileSystem, isLink, displayName), DisplayName: displayName, ParsingName: parsingName, ItemKind: ClassifyItem(isLink, isFolder, isFileSystem, displayName, parsingName), PixelPosition: position, MonitorDeviceName: null, IsSystemItem: isSystem && !isFileSystem, IsFileSystemItem: isFileSystem);
    }
    private static string GetDisplayNameSafe(IShellFolder folder, nint pidl, uint flags)
    { var hr = folder.GetDisplayNameOf(pidl, flags, out CSSTRRET strRet); if (hr < 0) throw new COMException($"GetDisplayNameOf failed", hr); var sb = new StringBuilder(512); hr = StrRetToBuf(ref strRet, pidl, sb, (uint)sb.Capacity); if (hr < 0) throw new COMException("StrRetToBuf failed", hr); return sb.ToString(); }
    private static uint GetItemAttributes(IShellFolder folder, nint pidl)
    { uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER | SFGAO_LINK | SFGAO_SYSTEM | SFGAO_HIDDEN | SFGAO_READONLY | SFGAO_NONENUMERATED | SFGAO_FILESYSANCESTOR | SFGAO_BROWSABLE; var pidls = new[] { pidl }; var hr = folder.GetAttributesOf(1, pidls, ref attrs); if (hr < 0) throw new COMException("GetAttributesOf failed", hr); return attrs; }
    private static ItemKind ClassifyItem(bool isLink, bool isFolder, bool isFileSystem, string displayName, string? parsingName)
    { if (!isFileSystem) return ItemKind.SystemItem; if (isLink) return ItemKind.Shortcut; if (isFolder) return ItemKind.Folder; if (parsingName is not null && parsingName.StartsWith("::{", StringComparison.Ordinal)) return ItemKind.SystemItem; return ItemKind.File; }
    private static string BuildStableId(string? parsingName, bool isFileSystem, bool isLink, string displayName)
    { if (!string.IsNullOrWhiteSpace(parsingName)) { if (isFileSystem && parsingName.Length >= 2 && parsingName[1] == ':') return parsingName.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(); return parsingName; } return $"display:{displayName}"; }

    // ═══════════════════════════════════════════════════════════
    //  COM 生命周期
    // ═══════════════════════════════════════════════════════════

    private static void SafeRelease<T>(ref T? comObject) where T : class
    { if (comObject is not null && Marshal.IsComObject(comObject)) { Marshal.ReleaseComObject(comObject); comObject = null; } }
    private static void SafeReleaseComObject(ref object? comObj)
    { if (comObj is not null && Marshal.IsComObject(comObj)) { Marshal.ReleaseComObject(comObj); comObj = null; } }

    internal static string FormatException(Exception ex)
    { var sb = new StringBuilder(); FormatEx(ex, sb, 0); return sb.ToString(); }
    private static void FormatEx(Exception ex, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2); sb.Append(indent).Append("Type: ").AppendLine(ex.GetType().FullName); sb.Append(indent).Append("Message: ").AppendLine(ex.Message);
        if (ex is COMException ce) sb.Append(indent).Append("HRESULT: 0x").AppendLine(ce.ErrorCode.ToString("X8"));
        sb.Append(indent).AppendLine("StackTrace:"); var lines = (ex.StackTrace ?? "(null)").Split('\n');
        for (var i = 0; i < Math.Min(lines.Length, 8); i++) sb.Append(indent).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
        if (lines.Length > 8) sb.Append(indent).Append("  ... (").Append(lines.Length - 8).AppendLine(" more)");
        if (ex is AggregateException ag) { foreach (var ie in ag.InnerExceptions) { sb.AppendLine(); FormatEx(ie, sb, depth + 1); } }
        else if (ex.InnerException is not null) { sb.AppendLine(); FormatEx(ex.InnerException, sb, depth + 1); }
    }
}

// ═══════════════════════════════════════════════════════════════
//  ComIdentityRelation — 三态 COM identity 关系
// ═══════════════════════════════════════════════════════════════

internal enum ComIdentityRelation { Same, Different, Unknown }

// ═══════════════════════════════════════════════════════════════
//  DesktopViewContext
// ═══════════════════════════════════════════════════════════════

internal sealed class DesktopViewContext : IDisposable
{
    private object? _dispatch;
    private IShellBrowser? _browser;
    private IShellView? _shellView;
    private IFolderView2Compat? _folderView;
    private ComIdentityRelation _folderViewIdentityRelation = ComIdentityRelation.Unknown;
    private IShellFolder? _desktopFolder;
    private IEnumIDList? _enumerator;
    private bool _disposed;

    public IFolderView2Compat FolderView => _folderView!;
    public IShellFolder DesktopFolder => _desktopFolder!;

    public void SetDispatch(object d) { ThrowIfDisposed(); ThrowIfAlreadySet(_dispatch); _dispatch = d; }
    public void SetBrowser(IShellBrowser b) { ThrowIfDisposed(); ThrowIfAlreadySet(_browser); _browser = b; }
    public void SetShellView(IShellView sv) { ThrowIfDisposed(); ThrowIfAlreadySet(_shellView); _shellView = sv; }

    public void SetFolderView(IFolderView2Compat fv, ComIdentityRelation identityRelation)
    { ThrowIfDisposed(); ThrowIfAlreadySet(_folderView); _folderView = fv; _folderViewIdentityRelation = identityRelation; }

    public void SetDesktopFolder(IShellFolder sf) { ThrowIfDisposed(); ThrowIfAlreadySet(_desktopFolder); _desktopFolder = sf; }
    public void SetEnumerator(IEnumIDList e) { ThrowIfDisposed(); ThrowIfAlreadySet(_enumerator); _enumerator = e; }

    private static void ThrowIfAlreadySet(object? field) { if (field is not null) throw new InvalidOperationException("DesktopViewContext field already set — overwrite would leak a COM object."); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_enumerator is not null && Marshal.IsComObject(_enumerator)) Marshal.ReleaseComObject(_enumerator);
        if (_desktopFolder is not null && Marshal.IsComObject(_desktopFolder)) Marshal.ReleaseComObject(_desktopFolder);

        switch (_folderViewIdentityRelation)
        {
            case ComIdentityRelation.Different:
                if (_folderView is not null && Marshal.IsComObject(_folderView)) Marshal.ReleaseComObject(_folderView);
                break;
            case ComIdentityRelation.Same:
                break;
            case ComIdentityRelation.Unknown:
                if (_folderView is not null) Debug.WriteLine("[M1-02] DesktopViewContext Dispose: folderView identity Unknown，为避免 double-release 延迟给 RCW/GC。");
                break;
        }

        if (_shellView is not null && Marshal.IsComObject(_shellView)) Marshal.ReleaseComObject(_shellView);
        if (_browser is not null && Marshal.IsComObject(_browser)) Marshal.ReleaseComObject(_browser);
        if (_dispatch is not null && Marshal.IsComObject(_dispatch)) Marshal.ReleaseComObject(_dispatch);
    }
}
