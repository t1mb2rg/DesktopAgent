using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;

namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

public enum DesktopWorkspaceApplyFreshCheckStatus
{
    Ready = 0,
    InvalidConfirmation = 1,
    TopologyChanged = 2,
    GeometryChanged = 3,
    ItemSetChanged = 4,
    ItemStateChanged = 5,
    InvalidFreshFacts = 6,
}

/// <summary>
/// 用户对一份 Frozen Preview 的显式确认。它只绑定“用户看过哪一份精确预览”，
/// 本身不授予 Writer / Recovery / Execution authority，也不跨重启持久化。
/// </summary>
public sealed record DesktopWorkspaceApplyConfirmation
{
    public Guid ConfirmationId { get; }
    public DateTimeOffset ConfirmedAtUtc { get; }
    public DesktopWorkspaceFrozenPreview FrozenPreview { get; }

    private DesktopWorkspaceApplyConfirmation(
        Guid confirmationId,
        DateTimeOffset confirmedAtUtc,
        DesktopWorkspaceFrozenPreview frozenPreview)
    {
        if (confirmationId == Guid.Empty)
            throw new ArgumentException("ConfirmationId 不能为 Guid.Empty。", nameof(confirmationId));
        if (confirmedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ConfirmedAtUtc 必须为 UTC。", nameof(confirmedAtUtc));

        ConfirmationId = confirmationId;
        ConfirmedAtUtc = confirmedAtUtc;
        FrozenPreview = frozenPreview ?? throw new ArgumentNullException(nameof(frozenPreview));
    }

    public static DesktopWorkspaceApplyConfirmation Create(
        DesktopWorkspaceFrozenPreview frozenPreview,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(frozenPreview);
        return new DesktopWorkspaceApplyConfirmation(
            Guid.NewGuid(),
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            frozenPreview);
    }
}

public sealed record DesktopWorkspaceApplyFreshCheckResult
{
    private readonly IReadOnlyList<string> _issues;

    public DesktopWorkspaceApplyFreshCheckStatus Status { get; }
    public IReadOnlyList<string> Issues => _issues;
    public bool IsReady => Status == DesktopWorkspaceApplyFreshCheckStatus.Ready;

    private DesktopWorkspaceApplyFreshCheckResult(
        DesktopWorkspaceApplyFreshCheckStatus status,
        IEnumerable<string>? issues = null)
    {
        Status = status;
        _issues = Array.AsReadOnly((issues ?? Array.Empty<string>()).ToArray());
        if (status == DesktopWorkspaceApplyFreshCheckStatus.Ready && _issues.Count != 0)
            throw new ArgumentException("Ready 结果不能包含 issue。", nameof(issues));
        if (status != DesktopWorkspaceApplyFreshCheckStatus.Ready && _issues.Count == 0)
            throw new ArgumentException("非 Ready 结果至少需要一个 issue。", nameof(issues));
    }

    internal static DesktopWorkspaceApplyFreshCheckResult Ready()
        => new(DesktopWorkspaceApplyFreshCheckStatus.Ready);

    internal static DesktopWorkspaceApplyFreshCheckResult Rejected(
        DesktopWorkspaceApplyFreshCheckStatus status,
        params string[] issues)
        => new(status, issues);
}

/// <summary>
/// Frozen Preview → User Confirm → Fresh Check 的纯 Core 安全门。
///
/// Fresh Check 不重新设计、不重新 Compile，也不接受“近似等价”。只有当前 Windows 事实仍与
/// 用户实际看过的 Frozen Preview 起点完全一致时，才允许后续 execution service 继续准备写入。
/// </summary>
public static class DesktopWorkspaceApplyFreshCheck
{
    public static DesktopWorkspaceApplyFreshCheckResult Evaluate(
        DesktopWorkspaceApplyConfirmation confirmation,
        ExplorerWorkspaceGeometry freshGeometry,
        IEnumerable<DesktopWorkspaceCompilationItemFact> freshItemFacts)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(freshGeometry);
        ArgumentNullException.ThrowIfNull(freshItemFacts);

        var preview = confirmation.FrozenPreview;
        var plan = preview.ExactPlan;
        var certificate = preview.RealizationCertificate;

        if (preview.Blueprint.BlueprintId != plan.BlueprintId ||
            preview.Blueprint.Revision != plan.BlueprintRevision ||
            certificate.BlueprintId != plan.BlueprintId ||
            certificate.BlueprintRevision != plan.BlueprintRevision ||
            !string.Equals(certificate.PlanFingerprint, plan.PlanFingerprint, StringComparison.Ordinal))
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.InvalidConfirmation,
                "Frozen Preview 内部的 Blueprint / Exact Plan / Realization Certificate identity 不一致。");
        }

        var topologyIssue = CompareTopology(preview, freshGeometry);
        if (topologyIssue is not null)
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.TopologyChanged,
                topologyIssue);
        }

        if (!string.Equals(
                plan.GeometryFingerprint,
                freshGeometry.GeometryFingerprint,
                StringComparison.Ordinal))
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.GeometryChanged,
                "Explorer 几何事实自精确预览后发生变化。请重新生成精确预览。 ");
        }

        var factArray = freshItemFacts.ToArray();
        if (factArray.Any(static fact => fact is null))
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.InvalidFreshFacts,
                "Fresh ItemFacts 不能包含 null。 ");
        }
        if (factArray.Select(static fact => fact.StableId)
            .Distinct(StringComparer.Ordinal).Count() != factArray.Length)
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.InvalidFreshFacts,
                "Fresh ItemFacts 包含重复 StableId。 ");
        }

        var expectedIds = preview.Items
            .Select(static item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var freshIds = factArray
            .Select(static fact => fact.StableId)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedIds.SetEquals(freshIds))
        {
            var missing = expectedIds.Except(freshIds, StringComparer.Ordinal).Take(3).ToArray();
            var added = freshIds.Except(expectedIds, StringComparer.Ordinal).Take(3).ToArray();
            var details = new List<string>();
            if (missing.Length > 0)
                details.Add($"缺少：{string.Join(", ", missing)}");
            if (added.Length > 0)
                details.Add($"新增：{string.Join(", ", added)}");
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.ItemSetChanged,
                "桌面项目集合自精确预览后发生变化。" +
                (details.Count == 0 ? string.Empty : $" {string.Join("；", details)}。"));
        }

        var freshById = factArray.ToDictionary(static fact => fact.StableId, StringComparer.Ordinal);
        var changedDetails = new List<string>();
        var changedCount = 0;
        foreach (var expected in preview.Items.OrderBy(static item => item.StableId, StringComparer.Ordinal))
        {
            var fresh = freshById[expected.StableId];
            var differences = new List<string>();
            if (!string.Equals(expected.SourceDisplayRef, fresh.CurrentDisplayRef, StringComparison.Ordinal))
                differences.Add($"显示器 {expected.SourceDisplayRef} → {fresh.CurrentDisplayRef}");
            if (expected.SourcePixelPosition != fresh.PixelPosition)
                differences.Add($"位置 ({expected.SourcePixelPosition.X},{expected.SourcePixelPosition.Y}) → ({fresh.PixelPosition.X},{fresh.PixelPosition.Y})");
            if (expected.IsMovable != fresh.IsMovable)
                differences.Add($"可移动 {expected.IsMovable} → {fresh.IsMovable}");

            if (differences.Count == 0)
                continue;

            changedCount++;
            if (changedDetails.Count < 5)
                changedDetails.Add($"{expected.DisplayName}：{string.Join("，", differences)}");
        }

        if (changedCount > 0)
        {
            return DesktopWorkspaceApplyFreshCheckResult.Rejected(
                DesktopWorkspaceApplyFreshCheckStatus.ItemStateChanged,
                $"共 {changedCount} 个项目的精确预览起点与当前桌面不一致（列出前 {changedDetails.Count} 项）：" +
                $"{string.Join("；", changedDetails)}。Writer 未启动，请依据当前桌面重新生成精确预览。");
        }

        return DesktopWorkspaceApplyFreshCheckResult.Ready();
    }

    private static string? CompareTopology(
        DesktopWorkspaceFrozenPreview preview,
        ExplorerWorkspaceGeometry freshGeometry)
    {
        if (preview.Displays.Count != freshGeometry.Displays.Count)
            return "显示器数量自精确预览后发生变化。 ";

        var freshByRef = freshGeometry.Displays.ToDictionary(
            static display => display.DisplayRef,
            StringComparer.Ordinal);
        foreach (var expected in preview.Displays)
        {
            if (!freshByRef.TryGetValue(expected.DisplayRef, out var fresh))
                return $"显示器 {expected.DisplayRef} 已不在当前拓扑中。 ";
            if (expected.IsPrimary != fresh.IsPrimary ||
                expected.PixelBounds != fresh.Bounds ||
                expected.WorkArea != fresh.WorkArea)
            {
                return $"显示器 {expected.DisplayRef} 的主屏身份、边界或工作区发生变化。 ";
            }
        }

        return null;
    }
}
