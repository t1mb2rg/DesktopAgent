using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Assignment;

/// <summary>
/// 完整的图标到分区分配计划。
/// InputIcons 是入口集合，ZoneAssignments 是分配结果，UnassignedIcons 是剩余图标。
/// </summary>
public sealed record DesktopLayoutAssignmentPlan
{
    public ResolvedDesktopLayoutTemplate ResolvedTemplate { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> InputIcons { get; }
    public IReadOnlyList<DesktopLayoutZoneAssignment> ZoneAssignments { get; }
    public IReadOnlyList<DesktopIconSnapshotItem> UnassignedIcons { get; }

    public int InputCount { get; }
    public int AssignedCount { get; }
    public int UnassignedCount { get; }
    public long TotalEffectiveCapacity { get; }
    public bool IsComplete { get; }

    public DesktopLayoutAssignmentPlan(
        ResolvedDesktopLayoutTemplate resolvedTemplate,
        IReadOnlyList<DesktopIconSnapshotItem> inputIcons,
        IReadOnlyList<DesktopLayoutZoneAssignment> zoneAssignments,
        IReadOnlyList<DesktopIconSnapshotItem> unassignedIcons)
    {
        if (resolvedTemplate is null) throw new ArgumentNullException(nameof(resolvedTemplate));
        if (inputIcons is null) throw new ArgumentNullException(nameof(inputIcons));
        if (zoneAssignments is null) throw new ArgumentNullException(nameof(zoneAssignments));
        if (unassignedIcons is null) throw new ArgumentNullException(nameof(unassignedIcons));

        var inputList = inputIcons.ToList();
        var zaList = zoneAssignments.ToList();
        var unassignedList = unassignedIcons.ToList();

        // ═══ InputIcons 验证 + 建立 Ordinal→Item 索引 ═══
        int? prevOrdinal = null;
        var inputByOrdinal = new Dictionary<int, DesktopIconSnapshotItem>();
        var inputStableIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < inputList.Count; i++)
        {
            if (inputList[i] is null)
                throw new ArgumentException($"InputIcons[{i}] 为 null。", nameof(inputIcons));
            if (!inputByOrdinal.TryAdd(inputList[i].Ordinal, inputList[i]))
                throw new ArgumentException(
                    $"InputIcons Ordinal {inputList[i].Ordinal} 重复。", nameof(inputIcons));
            if (!inputStableIds.Add(inputList[i].StableId))
                throw new ArgumentException(
                    $"InputIcons StableId \"{inputList[i].StableId}\" 重复。", nameof(inputIcons));
            if (prevOrdinal.HasValue && inputList[i].Ordinal <= prevOrdinal.Value)
                throw new ArgumentException(
                    $"InputIcons 未按 Ordinal 升序。", nameof(inputIcons));
            prevOrdinal = inputList[i].Ordinal;
        }

        // ═══ ZoneAssignments 数量 ═══
        if (zaList.Count != resolvedTemplate.Zones.Count)
            throw new ArgumentException(
                $"ZoneAssignments 数量 ({zaList.Count}) 不等于 ResolvedTemplate.Zones 数量 ({resolvedTemplate.Zones.Count})。",
                nameof(zoneAssignments));

        // ═══ ZoneAssignments：唯一、身份、SortOrder ASC ═══
        var seenZoneIds = new HashSet<Guid>();
        long? prevSort = null;
        for (var i = 0; i < zaList.Count; i++)
        {
            if (zaList[i] is null)
                throw new ArgumentException($"ZoneAssignments[{i}] 为 null。", nameof(zoneAssignments));

            if (!seenZoneIds.Add(zaList[i].Zone.Zone.ZoneId))
                throw new ArgumentException(
                    $"ZoneAssignments ZoneId \"{zaList[i].Zone.Zone.ZoneId:D}\" 重复（索引 {i}）。",
                    nameof(zoneAssignments));

            // 确认 Zone 来自同一 ResolvedTemplate
            var found = false;
            foreach (var rz in resolvedTemplate.Zones)
            {
                if (ReferenceEquals(zaList[i].Zone, rz)) { found = true; break; }
            }
            if (!found)
                throw new ArgumentException(
                    $"ZoneAssignments[{i}] 的 Zone 不属于此 ResolvedTemplate。", nameof(zoneAssignments));

            long so = zaList[i].Zone.Zone.SortOrder;
            if (prevSort.HasValue && so <= prevSort.Value)
                throw new ArgumentException(
                    $"ZoneAssignments 未按 Zone.SortOrder ASC：索引 {i - 1} SortOrder={prevSort}，索引 {i} SortOrder={so}。",
                    nameof(zoneAssignments));
            prevSort = so;
        }

        // ═══ Assigned 图标身份验证（(Ordinal, StableId) 完整匹配） ═══
        var assignedSet = new HashSet<(int Ordinal, string StableId)>();
        for (var zi = 0; zi < zaList.Count; zi++)
        {
            for (var ii = 0; ii < zaList[zi].Icons.Count; ii++)
            {
                var icon = zaList[zi].Icons[ii];
                var key = (icon.Ordinal, icon.StableId);
                if (!assignedSet.Add(key))
                    throw new ArgumentException(
                        $"图标 (Ordinal={icon.Ordinal}, StableId=\"{icon.StableId}\") 被分配多次。" +
                        $"Zone=\"{zaList[zi].Zone.Zone.ZoneId:D}\"。", nameof(zoneAssignments));

                if (!inputByOrdinal.TryGetValue(icon.Ordinal, out var expected))
                    throw new ArgumentException(
                        $"Assigned 图标 (Ordinal={icon.Ordinal}, StableId=\"{icon.StableId}\") " +
                        $"Ordinal 不在 InputIcons 中。Zone=\"{zaList[zi].Zone.Zone.ZoneId:D}\"。",
                        nameof(zoneAssignments));

                if (!StringComparer.Ordinal.Equals(expected.StableId, icon.StableId))
                    throw new ArgumentException(
                        $"Assigned 图标 Ordinal={icon.Ordinal} 的 StableId 不匹配：" +
                        $"预期 \"{expected.StableId}\"，实际 \"{icon.StableId}\"。" +
                        $"Zone=\"{zaList[zi].Zone.Zone.ZoneId:D}\"。",
                        nameof(zoneAssignments));
            }
        }

        // ═══ Unassigned 验证 ═══
        {
            int? unassignedPrev = null;
            var unassignedOrdinals = new HashSet<int>();
            var unassignedSids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < unassignedList.Count; i++)
            {
                if (unassignedList[i] is null)
                    throw new ArgumentException($"UnassignedIcons[{i}] 为 null。", nameof(unassignedIcons));

                if (!unassignedOrdinals.Add(unassignedList[i].Ordinal))
                    throw new ArgumentException(
                        $"UnassignedIcons Ordinal {unassignedList[i].Ordinal} 重复（索引 {i}）。",
                        nameof(unassignedIcons));

                if (!unassignedSids.Add(unassignedList[i].StableId))
                    throw new ArgumentException(
                        $"UnassignedIcons StableId \"{unassignedList[i].StableId}\" 重复（索引 {i}）。",
                        nameof(unassignedIcons));

                if (unassignedPrev.HasValue && unassignedList[i].Ordinal <= unassignedPrev.Value)
                    throw new ArgumentException(
                        $"UnassignedIcons 未按 Ordinal ASC：索引 {i - 1} Ordinal={unassignedPrev}，索引 {i} Ordinal={unassignedList[i].Ordinal}。",
                        nameof(unassignedIcons));

                // 身份匹配 InputIcons
                if (!inputByOrdinal.TryGetValue(unassignedList[i].Ordinal, out var expected))
                    throw new ArgumentException(
                        $"UnassignedIcons 图标 (Ordinal={unassignedList[i].Ordinal}, StableId=\"{unassignedList[i].StableId}\") " +
                        $"Ordinal 不在 InputIcons 中。",
                        nameof(unassignedIcons));

                if (!StringComparer.Ordinal.Equals(expected.StableId, unassignedList[i].StableId))
                    throw new ArgumentException(
                        $"UnassignedIcons 图标 Ordinal={unassignedList[i].Ordinal} 的 StableId 不匹配：" +
                        $"预期 \"{expected.StableId}\"，实际 \"{unassignedList[i].StableId}\"。",
                        nameof(unassignedIcons));

                // 不与 Assigned 重复
                var key = (unassignedList[i].Ordinal, unassignedList[i].StableId);
                if (assignedSet.Contains(key))
                    throw new ArgumentException(
                        $"图标 (Ordinal={key.Ordinal}, StableId=\"{key.StableId}\") 同时出现在 Assigned 和 Unassigned 中。",
                        nameof(unassignedIcons));

                unassignedPrev = unassignedList[i].Ordinal;
            }
        }

        // ═══ 计数不变量 ═══
        long totalAssigned = 0;
        for (var i = 0; i < zaList.Count; i++)
            totalAssigned = checked(totalAssigned + zaList[i].AssignedCount);

        if (totalAssigned > int.MaxValue)
            throw new ArgumentException(
                $"已分配图标数 ({totalAssigned}) 超过 int.MaxValue。", nameof(zoneAssignments));

        if (totalAssigned + unassignedList.Count != inputList.Count)
            throw new ArgumentException(
                $"AssignedCount ({totalAssigned}) + UnassignedCount ({unassignedList.Count}) " +
                $"≠ InputCount ({inputList.Count})。", nameof(zoneAssignments));

        if (totalAssigned > resolvedTemplate.TotalEffectiveCapacity)
            throw new ArgumentException(
                $"已分配图标数 ({totalAssigned}) 超过总有效容量 ({resolvedTemplate.TotalEffectiveCapacity})。",
                nameof(zoneAssignments));

        // ═══ 整体确定性序列验证：ZoneAssignments(by SortOrder) + Unassigned = InputIcons ═══
        {
            var seqIdx = 0;
            for (var zi = 0; zi < zaList.Count; zi++)
            {
                for (var ii = 0; ii < zaList[zi].Icons.Count; ii++)
                {
                    if (seqIdx >= inputList.Count)
                        throw new ArgumentException(
                            $"整体序列第 {seqIdx} 个图标超出 InputIcons 范围。" +
                            $"来源：ZoneAssignments[{zi}]（SortOrder={zaList[zi].Zone.Zone.SortOrder}）的 Icons[{ii}]。",
                            nameof(zoneAssignments));

                    var actual = zaList[zi].Icons[ii];
                    var expected = inputList[seqIdx];
                    if (actual.Ordinal != expected.Ordinal
                        || !StringComparer.Ordinal.Equals(actual.StableId, expected.StableId))
                        throw new ArgumentException(
                            $"整体序列位置 {seqIdx} 不匹配：" +
                            $"预期 (Ordinal={expected.Ordinal}, StableId=\"{expected.StableId}\")，" +
                            $"实际 (Ordinal={actual.Ordinal}, StableId=\"{actual.StableId}\")。" +
                            $"来源：ZoneAssignments[{zi}]（SortOrder={zaList[zi].Zone.Zone.SortOrder}）的 Icons[{ii}]。",
                            nameof(zoneAssignments));

                    seqIdx++;
                }
            }

            for (var ui = 0; ui < unassignedList.Count; ui++)
            {
                if (seqIdx >= inputList.Count)
                    throw new ArgumentException(
                        $"整体序列第 {seqIdx} 个图标超出 InputIcons 范围。" +
                        $"来源：UnassignedIcons[{ui}]。",
                        nameof(unassignedIcons));

                var actual = unassignedList[ui];
                var expected = inputList[seqIdx];
                if (actual.Ordinal != expected.Ordinal
                    || !StringComparer.Ordinal.Equals(actual.StableId, expected.StableId))
                    throw new ArgumentException(
                        $"整体序列位置 {seqIdx} 不匹配：" +
                        $"预期 (Ordinal={expected.Ordinal}, StableId=\"{expected.StableId}\")，" +
                        $"实际 (Ordinal={actual.Ordinal}, StableId=\"{actual.StableId}\")。" +
                        $"来源：UnassignedIcons[{ui}]。",
                        nameof(unassignedIcons));

                seqIdx++;
            }

            if (seqIdx != inputList.Count)
                throw new ArgumentException(
                    $"整体序列结束后 seqIdx={seqIdx} ≠ InputCount={inputList.Count}。" +
                    $"存在遗漏或重复。", nameof(zoneAssignments));
        }

        ResolvedTemplate = resolvedTemplate;
        InputIcons = inputList.AsReadOnly();
        ZoneAssignments = zaList.AsReadOnly();
        UnassignedIcons = unassignedList.AsReadOnly();
        InputCount = inputList.Count;
        AssignedCount = (int)totalAssigned;
        UnassignedCount = unassignedList.Count;
        TotalEffectiveCapacity = resolvedTemplate.TotalEffectiveCapacity;
        IsComplete = unassignedList.Count == 0;
    }
}
