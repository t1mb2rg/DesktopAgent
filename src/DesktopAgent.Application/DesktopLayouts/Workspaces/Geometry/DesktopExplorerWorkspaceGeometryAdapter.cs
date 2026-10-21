using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry;

public enum DesktopExplorerWorkspaceGeometryIssueCode
{
    NoDisplays = 0,
    PrimaryDisplayCountInvalid = 1,
    InvalidDisplayGeometry = 2,
    InvalidIconIdentity = 3,
    UnknownIconDisplay = 4,
    IconOutsideAssignedDisplay = 5,
    GridPhaseUnavailable = 6,
    GridPhaseConflict = 7,
    DisplayHasNoNativeSlots = 8,

    /// <summary>
    /// 非致命通知（不阻塞 Compile）：项目落在所有显示器范围之外。
    /// 真机实测（2026-10-14）：显示器排布非矩形时，桌面列表视图仍横跨虚拟桌面并集，
    /// 图标会停在没有任何显示器覆盖的缝隙里（本机 18 个）。这类项目不在任何 WorkArea 内，
    /// 因此不影响任何一屏的 slot surface，只是"本轮不可规划"。
    /// </summary>
    IconOutsideAllDisplays = 9,
}

public sealed record DesktopExplorerWorkspaceGeometryIssue(
    DesktopExplorerWorkspaceGeometryIssueCode Code,
    string Message,
    string? DisplayRef = null,
    string? ItemDisplayName = null);

/// <summary>
/// 单块显示器最终采用的网格相位来自哪里，以及它的证据强度。
/// Measured 表示来自 Diagnostics 物理探针写入的实测事实；Inferred 只表示图标位置推断，
/// 它仍然是不可授权执行的降级证据。
/// </summary>
public sealed record DesktopExplorerGridProvenance(
    string DeviceName,
    ExplorerGridEvidenceSource Source,
    PixelPoint Spacing,
    PixelPoint Phase,
    int SampleCount,
    int ConsensusCount);

/// <summary>
/// 一条被显式降级的网格事实：它没有被本次几何构建采用，以及为什么。
/// 降级必须可见，而不是静默丢弃。
/// </summary>
public sealed record DesktopExplorerGridFactDemotion(
    string DeviceName,
    string Reason,
    string FactTopologyFingerprint,
    string CurrentTopologyIdentity);

/// <summary>
/// Explorer Geometry Adapter 的 fail-closed 结果。失败只阻塞后续 Compile；
/// DesktopObservation 与 Semantic Understanding 本身仍然是有效的当前事实。
/// </summary>
public sealed class DesktopExplorerWorkspaceGeometryResult
{
    public ExplorerWorkspaceGeometry? Geometry { get; }
    public IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue> Issues { get; }

    /// <summary>
    /// 非致命通知：不阻塞 Compile，但必须可见（例如落在所有显示器之外、本轮无法规划的桌面项目）。
    /// </summary>
    public IReadOnlyList<DesktopExplorerWorkspaceGeometryIssue> Notices { get; }

    /// <summary>不含 phase 的拓扑身份（存储键）。它不等于 GeometryFingerprint。</summary>
    public string? TopologyIdentity { get; }

    /// <summary>每块显示器最终采用的相位来源。DeviceName 与 Geometry.Displays 一一对应。</summary>
    public IReadOnlyList<DesktopExplorerGridProvenance> Provenance { get; }

    /// <summary>被显式降级、未被采用的事实。</summary>
    public IReadOnlyList<DesktopExplorerGridFactDemotion> DemotedFacts { get; }

    public bool Succeeded => Geometry is not null && Issues.Count == 0;

    private DesktopExplorerWorkspaceGeometryResult(
        ExplorerWorkspaceGeometry? geometry,
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue> issues,
        string? topologyIdentity,
        IEnumerable<DesktopExplorerGridProvenance>? provenance,
        IEnumerable<DesktopExplorerGridFactDemotion>? demotedFacts,
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var issueArray = issues.ToArray();
        if (issueArray.Any(static issue => issue is null))
            throw new ArgumentException("Issues 不能包含 null。", nameof(issues));

        var noticeArray = notices?.ToArray() ?? [];
        if (noticeArray.Any(static notice => notice is null))
            throw new ArgumentException("Notices 不能包含 null。", nameof(notices));

        var provenanceArray = provenance?.ToArray() ?? [];
        if (provenanceArray.Any(static item => item is null))
            throw new ArgumentException("Provenance 不能包含 null。", nameof(provenance));
        var demotionArray = demotedFacts?.ToArray() ?? [];
        if (demotionArray.Any(static item => item is null))
            throw new ArgumentException("DemotedFacts 不能包含 null。", nameof(demotedFacts));

        var isSuccess = geometry is not null && issueArray.Length == 0;
        var isFailure = geometry is null && issueArray.Length > 0;
        if (!isSuccess && !isFailure)
            throw new ArgumentException("GeometryResult 必须是完整成功或完整失败状态。");

        Geometry = geometry;
        Issues = Array.AsReadOnly(issueArray);
        // 通知与成败无关：成功、失败都要能说出"这一轮哪些项目没被规划"。
        Notices = Array.AsReadOnly(noticeArray);
        TopologyIdentity = topologyIdentity;
        // 只有成功构建出几何时才有资格声明相位来源；失败时不得留下半截 provenance。
        Provenance = Array.AsReadOnly(isSuccess ? provenanceArray : []);
        DemotedFacts = Array.AsReadOnly(demotionArray);
    }

    internal static DesktopExplorerWorkspaceGeometryResult Success(
        ExplorerWorkspaceGeometry geometry,
        string? topologyIdentity,
        IEnumerable<DesktopExplorerGridProvenance> provenance,
        IEnumerable<DesktopExplorerGridFactDemotion> demotedFacts,
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue>? notices = null)
        => new(
            geometry ?? throw new ArgumentNullException(nameof(geometry)),
            [],
            topologyIdentity,
            provenance,
            demotedFacts,
            notices);

    internal static DesktopExplorerWorkspaceGeometryResult Failure(
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue> issues,
        string? topologyIdentity = null,
        IEnumerable<DesktopExplorerGridFactDemotion>? demotedFacts = null,
        IEnumerable<DesktopExplorerWorkspaceGeometryIssue>? notices = null)
        => new(null, issues, topologyIdentity, null, demotedFacts, notices);
}

/// <summary>
/// ADR-0005 Explorer Geometry Adapter。
///
/// 它只把 fresh DesktopObservation 变成当前 Windows 可施工的 native slot surface：
/// WorkArea、spacing、phase、负坐标、fixed obstacles 与精确像素槽位。
///
/// 关键区别：当前可移动图标不需要先被解释成某个 CurrentCell。
/// 自由摆放 / boundary / tail item 仍然可以作为 Observation 事实存在；只要整体 phase
/// 有足够证据，Geometry Adapter 就能建立目标施工面。
/// </summary>
public static class DesktopExplorerWorkspaceGeometryAdapter
{
    private const double MinimumGridConsensusRatio = 0.60d;

    public static DesktopExplorerWorkspaceGeometryResult Build(DesktopObservation observation)
        => Build(observation, null);

    /// <summary>
    /// 用当前观测构建 native slot surface。
    ///
    /// <paramref name="facts"/> 是 Diagnostics 物理探针写入的网格实测事实。规则：
    /// 只有 Measured 且拓扑身份与当前拓扑一致的实测事实才会被采用（实测优先）；
    /// 其余事实一律进入 DemotedFacts 并回退到图标位置推断，绝不静默使用；
    /// 不传事实时行为与旧版本完全一致。
    /// </summary>
    public static DesktopExplorerWorkspaceGeometryResult Build(
        DesktopObservation observation,
        IReadOnlyList<ExplorerGridMeasurement>? facts)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (facts is not null && facts.Any(static fact => fact is null))
            throw new ArgumentException("Facts 不能包含 null。", nameof(facts));

        var issues = ValidateObservation(observation);
        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues);

        var notices = new List<DesktopExplorerWorkspaceGeometryIssue>();
        var displayEntries = CreateDisplayEntries(observation.Displays);
        var entriesByDeviceName = displayEntries.ToDictionary(
            static entry => entry.Display.DeviceName,
            StringComparer.Ordinal);

        var spacing = observation.PositioningCapabilities.Spacing;
        var topologyIdentity = TryComputeTopologyIdentity(observation, spacing);
        var (demotedFacts, authoritativeFacts) = ClassifyFacts(
            facts,
            topologyIdentity,
            entriesByDeviceName);

        foreach (var icon in observation.Icons)
        {
            // 落在所有显示器之外的桌面项目：显示器排布非矩形时，桌面列表视图仍横跨整个虚拟桌面并集，
            // 图标会停在没有任何显示器覆盖的缝隙里（真机实测 2026-10-14：本机 18 个）。
            // 这类项目不在任何屏的 WorkArea 内，因此不影响任何一屏的 slot surface；
            // 它们只是"这一轮不可规划"，记为通知并保持原位，不能升级成整机 fail closed。
            if (!observation.Displays.Any(display => display.Bounds.Contains(icon.PixelPosition)))
            {
                notices.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.IconOutsideAllDisplays,
                    $"项目 \"{icon.DisplayName}\" 的位置 ({icon.PixelPosition.X},{icon.PixelPosition.Y}) "
                        + "不在任何显示器范围内，本轮不参与规划（保持原位）。",
                    ItemDisplayName: icon.DisplayName));
                continue;
            }

            if (icon.MonitorDeviceName is null ||
                !entriesByDeviceName.TryGetValue(icon.MonitorDeviceName, out var entry))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.UnknownIconDisplay,
                    $"项目 \"{icon.DisplayName}\" 无法归属到当前显示器拓扑。",
                    ItemDisplayName: icon.DisplayName));
                continue;
            }

            if (!entry.Display.Bounds.Contains(icon.PixelPosition))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.IconOutsideAssignedDisplay,
                    $"项目 \"{icon.DisplayName}\" 的物理位置不在其归属显示器 {entry.DisplayRef} 内。",
                    entry.DisplayRef,
                    icon.DisplayName));
            }
        }

        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues, topologyIdentity, demotedFacts, notices);

        var inferences = new Dictionary<string, DesktopExplorerGridPhaseResolver.Inference?>(StringComparer.Ordinal);
        foreach (var entry in displayEntries)
        {
            inferences[entry.Display.DeviceName] = TryInferForDisplay(
                observation,
                entry.Display.DeviceName,
                spacing);
        }

        var phaseByDeviceName = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        var provenance = new List<DesktopExplorerGridProvenance>(displayEntries.Count);

        if (authoritativeFacts.Count > 0)
        {
            // 实测优先：拿到实测事实的显示器用事实相位；其余显示器必须自己形成可靠推断，
            // 否则该显示器 fail closed（不同显示器允许不同相位）。
            foreach (var entry in displayEntries)
            {
                var deviceName = entry.Display.DeviceName;
                if (authoritativeFacts.TryGetValue(deviceName, out var fact))
                {
                    phaseByDeviceName[deviceName] = fact.Phase;
                    provenance.Add(new DesktopExplorerGridProvenance(
                        deviceName,
                        ExplorerGridEvidenceSource.Measured,
                        fact.Spacing,
                        fact.Phase,
                        fact.SampleCount,
                        fact.ConsensusCount));
                    continue;
                }

                var inferred = inferences[deviceName];
                if (inferred is null)
                {
                    issues.Add(new(
                        DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable,
                        $"显示器 {entry.DisplayRef} 既没有可用的实测网格事实，" +
                        "当前图标位置也没有形成足够可靠的 Explorer phase，拒绝猜测 native slot surface。",
                        entry.DisplayRef));
                    continue;
                }

                phaseByDeviceName[deviceName] = inferred.Phase;
                provenance.Add(new DesktopExplorerGridProvenance(
                    deviceName,
                    ExplorerGridEvidenceSource.Inferred,
                    spacing,
                    inferred.Phase,
                    inferred.SampleCount,
                    inferred.ConsensusCount));
            }

            if (issues.Count > 0)
                return DesktopExplorerWorkspaceGeometryResult.Failure(issues, topologyIdentity, demotedFacts, notices);
        }
        else
        {
            // 没有任何可用实测事实：完全沿用既有行为（共享相位 + 全局兜底推断）。
            if (!TryResolveSharedGridPhase(
                    observation,
                    displayEntries,
                    spacing,
                    inferences,
                    out var phase,
                    out var phaseConflict))
            {
                issues.Add(phaseConflict
                    ? new(
                        DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseConflict,
                        "多块显示器形成可靠但不一致的 Explorer phase，拒绝猜测 native slot surface。")
                    : new(
                        DesktopExplorerWorkspaceGeometryIssueCode.GridPhaseUnavailable,
                        "当前图标位置没有形成足够可靠的 Explorer phase，拒绝猜测 native slot surface。"));
                return DesktopExplorerWorkspaceGeometryResult.Failure(issues, topologyIdentity, demotedFacts, notices);
            }

            foreach (var entry in displayEntries)
            {
                var deviceName = entry.Display.DeviceName;
                var inferred = inferences[deviceName];
                phaseByDeviceName[deviceName] = phase;
                provenance.Add(new DesktopExplorerGridProvenance(
                    deviceName,
                    ExplorerGridEvidenceSource.Inferred,
                    spacing,
                    phase,
                    inferred?.SampleCount ?? 0,
                    inferred?.ConsensusCount ?? 0));
            }
        }

        var displayGeometries = new List<ExplorerDisplayGeometry>(displayEntries.Count);
        foreach (var entry in displayEntries)
        {
            var displayPhase = phaseByDeviceName[entry.Display.DeviceName];
            var origin = new PixelPoint(
                AlignUpToPhase(entry.Display.WorkArea.Left, spacing.X, displayPhase.X),
                AlignUpToPhase(entry.Display.WorkArea.Top, spacing.Y, displayPhase.Y));
            var columns = CountFullyContainedSlots(origin.X, entry.Display.WorkArea.Right, spacing.X);
            var rows = CountFullyContainedSlots(origin.Y, entry.Display.WorkArea.Bottom, spacing.Y);
            if (columns <= 0 || rows <= 0)
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.DisplayHasNoNativeSlots,
                    $"显示器 {entry.DisplayRef} 的 WorkArea 无法形成安全 Explorer-native slot。",
                    entry.DisplayRef));
                continue;
            }

            var slots = CreateSlots(entry.DisplayRef, origin, spacing, columns, rows);
            var fixedObstacles = CreateFixedObstacles(
                observation,
                entry,
                origin,
                spacing,
                columns,
                rows,
                slots);

            displayGeometries.Add(new ExplorerDisplayGeometry(
                entry.DisplayRef,
                entry.Display.DeviceName,
                entry.Display.Bounds,
                entry.Display.WorkArea,
                entry.Display.IsPrimary,
                entry.Display.DpiX,
                entry.Display.DpiY,
                spacing,
                displayPhase,
                origin,
                columns,
                rows,
                slots,
                fixedObstacles));
        }

        if (issues.Count > 0)
            return DesktopExplorerWorkspaceGeometryResult.Failure(issues, topologyIdentity, demotedFacts, notices);

        var fingerprint = ComputeGeometryFingerprint(
            observation.PositioningCapabilities.IsAutoArrangeEnabled,
            spacing,
            displayGeometries);
        return DesktopExplorerWorkspaceGeometryResult.Success(
            new ExplorerWorkspaceGeometry(
                observation.ObservationId,
                fingerprint,
                observation.PositioningCapabilities.IsAutoArrangeEnabled,
                spacing,
                displayGeometries),
            topologyIdentity,
            provenance,
            demotedFacts,
            notices);
    }

    /// <summary>
    /// 拓扑身份是存储键，它必须不含 phase（否则存储相位会参与验证自身，形成循环）。
    /// 身份不可计算时返回 null，此时任何事实都无法被采用。
    ///
    /// 公开出来是为了让"读事实"的产品路径用同一口径推导键；两处口径必须一致。
    /// </summary>
    public static string? ComputeTopologyIdentity(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return ExplorerGridTopologyIdentity.TryComputeFor(observation);
    }

    private static string? TryComputeTopologyIdentity(
        DesktopObservation observation,
        PixelPoint spacing)
    {
        _ = spacing;
        return ComputeTopologyIdentity(observation);
    }

    /// <summary>
    /// 把传入的事实分成"可采用"和"必须显式降级"两类。降级不写进几何，但必须可见。
    /// </summary>
    private static (
        IReadOnlyList<DesktopExplorerGridFactDemotion> Demotions,
        IReadOnlyDictionary<string, ExplorerGridMeasurement> Authoritative) ClassifyFacts(
        IReadOnlyList<ExplorerGridMeasurement>? facts,
        string? topologyIdentity,
        IReadOnlyDictionary<string, DisplayEntry> entriesByDeviceName)
    {
        var demotions = new List<DesktopExplorerGridFactDemotion>();
        var candidates = new Dictionary<string, ExplorerGridMeasurement>(StringComparer.Ordinal);
        if (facts is null || facts.Count == 0)
            return (Array.AsReadOnly(demotions.ToArray()), candidates);

        foreach (var fact in facts)
        {
            if (topologyIdentity is null)
            {
                demotions.Add(Demote(fact, "当前观测无法计算拓扑身份，事实一律降级。", topologyIdentity));
                continue;
            }

            if (!entriesByDeviceName.ContainsKey(fact.DeviceName))
            {
                demotions.Add(Demote(fact, "事实所属显示器不在当前拓扑中，已降级。", topologyIdentity));
                continue;
            }

            if (fact.Source != ExplorerGridEvidenceSource.Measured)
            {
                demotions.Add(Demote(
                    fact,
                    "非实测来源的网格事实不能授权几何构建，已降级到图标位置推断。",
                    topologyIdentity));
                continue;
            }

            if (!fact.MatchesTopology(topologyIdentity))
            {
                demotions.Add(Demote(
                    fact,
                    "事实记录的拓扑身份与当前拓扑不一致（stale），已降级到图标位置推断。",
                    topologyIdentity));
                continue;
            }

            if (candidates.TryGetValue(fact.DeviceName, out var existing))
            {
                // 同一显示器多条实测事实：只采用最新的一条，其余可见地降级。
                if (fact.ObservedAtUtc > existing.ObservedAtUtc)
                {
                    demotions.Add(Demote(
                        existing,
                        "同一显示器存在更新的实测事实，本条已降级。",
                        topologyIdentity));
                    candidates[fact.DeviceName] = fact;
                }
                else
                {
                    demotions.Add(Demote(
                        fact,
                        "同一显示器存在更新的实测事实，本条已降级。",
                        topologyIdentity));
                }

                continue;
            }

            candidates[fact.DeviceName] = fact;
        }

        return (Array.AsReadOnly(demotions.ToArray()), candidates);
    }

    private static DesktopExplorerGridFactDemotion Demote(
        ExplorerGridMeasurement fact,
        string reason,
        string? topologyIdentity)
        => new(
            fact.DeviceName,
            reason,
            fact.TopologyFingerprint,
            topologyIdentity ?? string.Empty);

    private static DesktopExplorerGridPhaseResolver.Inference? TryInferForDisplay(
        DesktopObservation observation,
        string deviceName,
        PixelPoint spacing)
    {
        var positions = observation.Icons
            .Where(icon => string.Equals(
                icon.MonitorDeviceName,
                deviceName,
                StringComparison.Ordinal))
            .Select(static icon => icon.PixelPosition)
            .ToArray();
        return DesktopExplorerGridPhaseResolver.TryInfer(
            positions,
            spacing,
            MinimumGridConsensusRatio);
    }

    private static List<DesktopExplorerWorkspaceGeometryIssue> ValidateObservation(
        DesktopObservation observation)
    {
        var issues = new List<DesktopExplorerWorkspaceGeometryIssue>();
        if (observation.Displays.Count == 0)
        {
            issues.Add(new(
                DesktopExplorerWorkspaceGeometryIssueCode.NoDisplays,
                "DesktopObservation 没有显示器，无法建立 Explorer 几何。"));
            return issues;
        }

        var primaryCount = observation.Displays.Count(static display => display.IsPrimary);
        if (primaryCount != 1)
        {
            issues.Add(new(
                DesktopExplorerWorkspaceGeometryIssueCode.PrimaryDisplayCountInvalid,
                $"Explorer 几何要求恰好一块主显示器，当前 {primaryCount} 块。"));
        }

        foreach (var display in observation.Displays)
        {
            if (string.IsNullOrWhiteSpace(display.DeviceName) ||
                !IsPositive(display.Bounds) ||
                !IsPositive(display.WorkArea) ||
                !Contains(display.Bounds, display.WorkArea) ||
                display.DpiX == 0 ||
                display.DpiY == 0)
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.InvalidDisplayGeometry,
                    "显示器身份、Bounds、WorkArea 或 DPI 无效，无法建立 Explorer 几何。"));
            }
        }

        foreach (var icon in observation.Icons)
        {
            if (icon is null ||
                string.IsNullOrEmpty(icon.StableId) ||
                string.IsNullOrWhiteSpace(icon.DisplayName) ||
                !Enum.IsDefined(icon.ItemKind))
            {
                issues.Add(new(
                    DesktopExplorerWorkspaceGeometryIssueCode.InvalidIconIdentity,
                    "桌面项目缺少有效本地身份或显示名称。",
                    ItemDisplayName: icon?.DisplayName));
            }
        }

        return issues;
    }

    private static bool TryResolveSharedGridPhase(
        DesktopObservation observation,
        IReadOnlyList<DisplayEntry> displayEntries,
        PixelPoint spacing,
        IReadOnlyDictionary<string, DesktopExplorerGridPhaseResolver.Inference?> inferences,
        out PixelPoint phase,
        out bool conflict)
    {
        var reliable = new List<DesktopExplorerGridPhaseResolver.Inference>();
        foreach (var entry in displayEntries)
        {
            var inference = inferences[entry.Display.DeviceName];
            if (inference is not null)
                reliable.Add(inference);
        }

        if (reliable.Count > 0)
        {
            var shared = reliable[0].Phase;
            conflict = reliable.Skip(1).Any(inference => inference.Phase != shared);
            phase = shared;
            return !conflict;
        }

        var global = DesktopExplorerGridPhaseResolver.TryInfer(
            observation.Icons.Select(static icon => icon.PixelPosition).ToArray(),
            spacing,
            MinimumGridConsensusRatio);
        if (global is null)
        {
            phase = default;
            conflict = false;
            return false;
        }

        phase = global.Phase;
        conflict = false;
        return true;
    }

    private static IReadOnlyList<ExplorerNativeSlot> CreateSlots(
        string displayRef,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows)
    {
        var slots = new ExplorerNativeSlot[checked(columns * rows)];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var position = new PixelPoint(
                    checked((int)((long)origin.X + (long)column * spacing.X)),
                    checked((int)((long)origin.Y + (long)row * spacing.Y)));
                slots[index++] = new ExplorerNativeSlot(
                    CreateSlotId(displayRef, column, row),
                    column,
                    row,
                    position);
            }
        }

        return Array.AsReadOnly(slots);
    }

    private static IReadOnlyList<ExplorerFixedObstacle> CreateFixedObstacles(
        DesktopObservation observation,
        DisplayEntry entry,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows,
        IReadOnlyList<ExplorerNativeSlot> slots)
    {
        var slotIds = slots.ToDictionary(
            static slot => (slot.Column, slot.Row),
            static slot => slot.SlotId);
        var obstacles = observation.Icons
            .Where(icon =>
                string.Equals(icon.MonitorDeviceName, entry.Display.DeviceName, StringComparison.Ordinal) &&
                !DesktopWorkspaceLocalItemFactProjector.IsMovable(icon))
            .OrderBy(static icon => icon.StableId, StringComparer.Ordinal)
            .Select(icon => new ExplorerFixedObstacle(
                icon.StableId,
                icon.DisplayName,
                icon.PixelPosition,
                GetBlockedSlotIds(
                    icon.PixelPosition,
                    origin,
                    spacing,
                    columns,
                    rows,
                    slotIds)))
            .ToArray();
        return Array.AsReadOnly(obstacles);
    }

    private static IReadOnlyList<string> GetBlockedSlotIds(
        PixelPoint position,
        PixelPoint origin,
        PixelPoint spacing,
        int columns,
        int rows,
        IReadOnlyDictionary<(int Column, int Row), string> slotIds)
    {
        var firstColumn = FloorDivide((long)position.X - origin.X, spacing.X);
        var firstRow = FloorDivide((long)position.Y - origin.Y, spacing.Y);
        var lastColumn = FloorDivide(
            (long)position.X + spacing.X - 1 - origin.X,
            spacing.X);
        var lastRow = FloorDivide(
            (long)position.Y + spacing.Y - 1 - origin.Y,
            spacing.Y);

        firstColumn = Math.Max(0, firstColumn);
        firstRow = Math.Max(0, firstRow);
        lastColumn = Math.Min(columns - 1L, lastColumn);
        lastRow = Math.Min(rows - 1L, lastRow);
        if (firstColumn > lastColumn || firstRow > lastRow)
            return [];

        var blocked = new List<string>();
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                blocked.Add(slotIds[(checked((int)column), checked((int)row))]);
            }
        }

        return Array.AsReadOnly(blocked.ToArray());
    }

    private static IReadOnlyList<DisplayEntry> CreateDisplayEntries(
        IReadOnlyList<DisplayDescriptor> displays)
    {
        var primary = displays.Single(static display => display.IsPrimary);
        var entries = new List<DisplayEntry>(displays.Count)
        {
            new("primary", primary),
        };

        var secondaryIndex = 1;
        foreach (var display in displays
                     .Where(static display => !display.IsPrimary)
                     .OrderBy(static display => display.Bounds.Left)
                     .ThenBy(static display => display.Bounds.Top)
                     .ThenBy(static display => display.DeviceName, StringComparer.Ordinal))
        {
            entries.Add(new($"secondary_{secondaryIndex++}", display));
        }

        return Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>
    /// Plan 身份：它必须包含每块显示器实际采用的 phase（plan 有效性依赖相位），
    /// 因此它与用于存储键的拓扑身份是两个不同的身份，不能互相替代。
    /// </summary>
    private static string ComputeGeometryFingerprint(
        bool isAutoArrangeEnabled,
        PixelPoint spacing,
        IReadOnlyList<ExplorerDisplayGeometry> displays)
    {
        var canonicalDisplays = string.Join(
            "\n",
            displays
                .OrderBy(static display => display.DisplayRef, StringComparer.Ordinal)
                .Select(display =>
                {
                    var obstacles = string.Join(
                        ",",
                        display.FixedObstacles
                            .OrderBy(static obstacle => obstacle.StableId, StringComparer.Ordinal)
                            .Select(obstacle =>
                                $"{obstacle.StableId}@{obstacle.PixelPosition.X}:{obstacle.PixelPosition.Y}" +
                                $"[{string.Join(";", obstacle.BlockedSlotIds.OrderBy(static id => id, StringComparer.Ordinal))}]"));
                    return
                        $"{display.DisplayRef}|{display.DeviceName}|" +
                        $"{display.Bounds.Left}|{display.Bounds.Top}|{display.Bounds.Right}|{display.Bounds.Bottom}|" +
                        $"{display.WorkArea.Left}|{display.WorkArea.Top}|{display.WorkArea.Right}|{display.WorkArea.Bottom}|" +
                        $"{display.DpiX}|{display.DpiY}|{(display.IsPrimary ? 1 : 0)}|" +
                        $"{display.Phase.X}|{display.Phase.Y}|" +
                        $"{display.GridOrigin.X}|{display.GridOrigin.Y}|{display.Columns}|{display.Rows}|{obstacles}";
                }));

        var canonical =
            $"auto={(isAutoArrangeEnabled ? 1 : 0)}|spacing={spacing.X}:{spacing.Y}\n" +
            canonicalDisplays;
        return $"explorer-geometry-v1:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    private static string CreateSlotId(string displayRef, int column, int row)
        => $"{displayRef}:slot:{column:D4}:{row:D4}";

    private static int AlignUpToPhase(int value, int spacing, int phase)
    {
        var residue = PositiveModulo(value, spacing);
        var delta = (phase - residue + spacing) % spacing;
        return checked((int)((long)value + delta));
    }

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }

    private static int CountFullyContainedSlots(int origin, int exclusiveEnd, int spacing)
    {
        var available = (long)exclusiveEnd - origin;
        return available <= 0 ? 0 : checked((int)(available / spacing));
    }

    private static long FloorDivide(long value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool IsPositive(PixelRect rect)
        => (long)rect.Right - rect.Left > 0 && (long)rect.Bottom - rect.Top > 0;

    private static bool Contains(PixelRect outer, PixelRect inner)
        => inner.Left >= outer.Left && inner.Top >= outer.Top &&
           inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private sealed record DisplayEntry(string DisplayRef, DisplayDescriptor Display);
}
