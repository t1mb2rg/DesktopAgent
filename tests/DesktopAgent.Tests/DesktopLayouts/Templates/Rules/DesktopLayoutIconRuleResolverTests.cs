using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Rules;

public sealed class DesktopLayoutIconRuleResolverTests
{
    // ═══ Helpers ═══

    private static DesktopIconSnapshotItem I(int ord, string sid, string? mon = "DISPLAY1")
        => new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(ord * 80, 0), mon, false, true);

    private static DesktopLayoutTemplate Tpl(Guid tid, params DesktopLayoutZone[] zones)
        => new(tid, "T", 1, TargetDisplayBinding.ForPrimaryDisplay(), zones);

    private static DesktopLayoutZone Z(Guid zid, int sortOrder = 0)
    {
        // 每个 Zone 使用不重叠的 Bounds
        return sortOrder switch
        {
            0 => new(zid, $"Z{sortOrder}",
                new NormalizedRect(0, 0, 5000, 10000), sortOrder,
                ZonePlacementFlow.ColumnMajor, null),
            1 => new(zid, $"Z{sortOrder}",
                new NormalizedRect(5000, 0, 10000, 10000), sortOrder,
                ZonePlacementFlow.ColumnMajor, null),
            _ => new(zid, $"Z{sortOrder}",
                new NormalizedRect((short)(sortOrder * 2500), 0,
                    (short)((sortOrder + 1) * 2500), 10000), sortOrder,
                ZonePlacementFlow.ColumnMajor, null)
        };
    }

    private static readonly Guid _tid = Guid.NewGuid();
    private static readonly Guid _zid0 = Guid.NewGuid();
    private static readonly Guid _zid1 = Guid.NewGuid();

    private static readonly DisplayDescriptor _dd = new("DISPLAY1",
        new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080), 96, 96, true);

    private DesktopLayoutTemplate MakeTemplate()
        => Tpl(_tid, Z(_zid0, 0), Z(_zid1, 1));

    private static DesktopLayoutIconRuleSet MakeRuleSet(params DesktopLayoutIconRule[] rules)
        => new(_tid, rules);

    // ═══ 空规则集 ═══

    [Fact] public void EmptyRuleSet_AllDefaultManaged()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            DesktopLayoutIconRuleSet.Empty(_tid));

        Assert.Equal(2, plan.InputIconCount);
        Assert.Equal(2, plan.TargetDisplayIconCount);
        Assert.Equal(0, plan.OutOfScopeIconCount);
        Assert.Equal(2, plan.DefaultManagedIconCount);
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(0, plan.ZoneOverrideCount);
        Assert.Equal(0, plan.UnmatchedRuleCount);
        Assert.False(plan.HasRules);
        Assert.False(plan.HasEffectiveRules);
    }

    // ═══ 规则分类 ═══

    [Fact] public void Exclude_ClassifiesCorrectly()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a")));

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
        Assert.Same(I(0, "a").StableId, "a");
    }

    [Fact] public void Pin_ClassifiesCorrectly()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("a")));

        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
    }

    [Fact] public void ZoneOverride_ClassifiesCorrectly()
    {
        var template = MakeTemplate();
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", _zid1)));

        Assert.Equal(1, plan.ZoneOverrideCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
        Assert.Same(template.Zones.First(z => z.ZoneId == _zid1),
            plan.ZoneOverrideMatches[0].TargetZone);
    }

    [Fact] public void MixedRules_ClassifiesCorrectly()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d") },
            MakeRuleSet(
                DesktopLayoutIconRule.ForExclude("a"),
                DesktopLayoutIconRule.ForPin("b"),
                DesktopLayoutIconRule.ForZoneOverride("c", _zid0)));

        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(1, plan.ZoneOverrideCount);
        Assert.Equal(1, plan.DefaultManagedIconCount); // "d" only
        Assert.Equal(3, plan.EffectiveRuleCount);
    }

    // ═══ StableId 精确匹配 ═══

    [Fact] public void ExactStableId_Match()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "MyApp.exe") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("MyApp.exe")));

        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(0, plan.DefaultManagedIconCount);
        Assert.Equal(0, plan.UnmatchedRuleCount);
    }

    [Fact] public void CaseSensitive_StableId_Mismatch()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "myapp.exe") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("MyApp.exe")));

        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
        Assert.Equal(1, plan.UnmatchedRuleCount);
    }

    [Fact] public void StableId_NotTrimmed()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "app") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin(" app")));

        // " app" 不等于 "app"
        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
    }

    [Fact] public void DisplayName_NotUsedForMatching()
    {
        // Icon DisplayName = "N_a", StableId = "a"
        // Rule targets "N_a" (DisplayName-like) — should NOT match
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("N_a")));

        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
        Assert.Equal(1, plan.UnmatchedRuleCount);
    }

    [Fact] public void ParsingName_NotUsedForMatching()
    {
        var icon = new DesktopIconSnapshotItem(0, "sid", "Display", "ParsingName",
            ItemKind.File, new PixelPoint(0, 0), "DISPLAY1", false, true);
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { icon },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("ParsingName")));

        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
    }

    [Fact] public void Ordinal_NotUsedForMatching()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(5, "app") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("5")));

        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.DefaultManagedIconCount);
    }

    // ═══ 显示器分区 ═══

    [Fact] public void NullMonitorDeviceName_GoesToOutOfScope()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: null), I(1, "b") },
            DesktopLayoutIconRuleSet.Empty(_tid));

        Assert.Equal(1, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
        Assert.Equal("b", plan.TargetDisplayIcons[0].StableId);
        Assert.Equal("a", plan.OutOfScopeIcons[0].StableId);
    }

    [Fact] public void OtherMonitor_GoesToOutOfScope()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: "DISPLAY2"), I(1, "b") },
            DesktopLayoutIconRuleSet.Empty(_tid));

        Assert.Equal(1, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
    }

    // ═══ OutOfScope 规则 ═══

    [Fact] public void OutOfScope_Exclude_RecordedButNotEffective()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: "OTHER") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a")));

        Assert.Equal(0, plan.ExcludedCount); // 不是 effective
        Assert.Equal(1, plan.OutOfScopeRuleMatchCount);
        Assert.False(plan.OutOfScopeRuleMatches[0].IsOnTargetDisplay);
    }

    [Fact] public void OutOfScope_Pin_RecordedButNotEffective()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: "OTHER") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("a")));

        Assert.Equal(0, plan.PinnedCount);
        Assert.Equal(1, plan.OutOfScopeRuleMatchCount);
    }

    [Fact] public void OutOfScope_Override_RecordedButNotEffective()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: "OTHER") },
            MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", _zid0)));

        Assert.Equal(0, plan.ZoneOverrideCount);
        Assert.Equal(1, plan.OutOfScopeRuleMatchCount);
    }

    // ═══ Unmatched 规则 ═══

    [Fact] public void UnmatchedRule_Recorded()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("no-such-icon")));

        Assert.Equal(0, plan.ExcludedCount);
        Assert.Equal(1, plan.UnmatchedRuleCount);
        Assert.Equal("no-such-icon", plan.UnmatchedRules[0].StableId);
        Assert.True(plan.HasUnmatchedRules);
    }

    // ═══ ZoneOverride 悬空 Zone 引用 ═══

    [Fact] public void Override_DanglingZone_Fails()
    {
        var nonExistentZid = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                new[] { I(0, "a") },
                MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", nonExistentZid))));
    }

    [Fact] public void Override_DanglingZone_NoMatchingIcon_StillFails()
    {
        // 即使没有图标匹配该规则，悬空 Override 也必须 fail
        var nonExistentZid = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                Array.Empty<DesktopIconSnapshotItem>(),
                MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("x", nonExistentZid))));
    }

    // ═══ 输入验证 ═══

    [Fact] public void RuleSet_TemplateIdMismatch_Fails()
    {
        var otherTid = Guid.NewGuid();
        var rs = new DesktopLayoutIconRuleSet(otherTid, Array.Empty<DesktopLayoutIconRule>());
        Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd, new[] { I(0, "a") }, rs));
    }

    [Fact] public void DuplicateIconOrdinal_Fails()
        => Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                new[] { I(0, "a"), I(0, "b") },
                DesktopLayoutIconRuleSet.Empty(_tid)));

    [Fact] public void DuplicateIconStableId_Fails()
        => Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                new[] { I(0, "dup"), I(1, "dup") },
                DesktopLayoutIconRuleSet.Empty(_tid)));

    [Fact] public void NullIcon_Fails()
        => Assert.Throws<ArgumentException>(() =>
            DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                new DesktopIconSnapshotItem[] { null! },
                DesktopLayoutIconRuleSet.Empty(_tid)));

    [Fact] public void NegativeOrdinal_Fails()
    {
        // DesktopIconSnapshotItem 构造时已拒绝负 Ordinal（ArgumentOutOfRangeException），
        // 因此 Resolver 层面无法接收到负 Ordinal 图标。
        // 此测试验证构造边界本身阻止了非法数据进入系统。
        Assert.Throws<ArgumentOutOfRangeException>(() => I(-1, "neg"));
    }

    // ═══ 确定性 ═══

    [Fact] public void InputIconOrder_DoesNotAffectOutput()
    {
        var plan1 = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(2, "c"), I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("b"), DesktopLayoutIconRule.ForExclude("a")));

        var plan2 = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b"), I(2, "c") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForPin("b")));

        // 集合内容相同（按 Ordinal 排序后）— 只比较 StableId 和 Ordinal
        Assert.True(plan1.TargetDisplayIcons.Select(i => i.StableId)
            .SequenceEqual(plan2.TargetDisplayIcons.Select(i => i.StableId)));
        Assert.True(plan1.DefaultManagedIcons.Select(i => i.StableId)
            .SequenceEqual(plan2.DefaultManagedIcons.Select(i => i.StableId)));
        Assert.Equal(plan1.PinnedCount, plan2.PinnedCount);
        Assert.Equal(plan1.ExcludedCount, plan2.ExcludedCount);
    }

    [Fact] public void RuleInputOrder_DoesNotAffectOutput()
    {
        var plan1 = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForPin("b"), DesktopLayoutIconRule.ForExclude("a")));

        var plan2 = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForPin("b")));

        Assert.Equal(plan1.PinnedCount, plan2.PinnedCount);
        Assert.Equal(plan1.ExcludedCount, plan2.ExcludedCount);
    }

    [Fact] public void ZoneOrder_DoesNotAffectMatching()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            Tpl(_tid, Z(_zid1, 1), Z(_zid0, 0)), // zones in reverse order
            _dd,
            new[] { I(0, "a") },
            MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", _zid0)));

        Assert.Equal(1, plan.ZoneOverrideCount);
        Assert.Equal(_zid0, plan.ZoneOverrideMatches[0].TargetZone!.ZoneId);
    }

    /// ═══ 引用保留 ═══

    [Fact] public void Override_TargetZone_PreservesTemplateReference()
    {
        var template = MakeTemplate();
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            template, _dd,
            new[] { I(0, "a") },
            MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", _zid1)));

        var templateZone = template.Zones.First(z => z.ZoneId == _zid1);
        Assert.Same(templateZone, plan.ZoneOverrideMatches[0].TargetZone);
    }

    [Fact] public void Match_PreservesIconReference()
    {
        var icons = new[] { I(0, "a") };
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd, icons,
            MakeRuleSet(DesktopLayoutIconRule.ForPin("a")));

        Assert.Same(icons[0], plan.PinnedMatches[0].Icon);
    }

    [Fact] public void Match_PreservesRuleReference()
    {
        var rule = DesktopLayoutIconRule.ForPin("a");
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a") },
            MakeRuleSet(rule));

        Assert.Same(rule, plan.PinnedMatches[0].Rule);
    }

    // ═══ Input 不被修改 ═══

    [Fact] public void InputIcons_NotModified()
    {
        var icons = new List<DesktopIconSnapshotItem> { I(0, "a"), I(1, "b") };
        var originalCount = icons.Count;
        DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd, icons,
            MakeRuleSet(DesktopLayoutIconRule.ForPin("a")));

        Assert.Equal(originalCount, icons.Count);
        Assert.Same(icons[0], icons[0]); // reference unchanged
    }

    // ═══ 确定性：重复调用字段级一致 ═══

    [Fact] public void SameInput_SameOutput()
    {
        DesktopLayoutIconRuleResolutionPlan Resolve()
            => DesktopLayoutIconRuleResolver.Resolve(
                MakeTemplate(), _dd,
                new[] { I(0, "a"), I(1, "b"), I(2, "c") },
                MakeRuleSet(
                    DesktopLayoutIconRule.ForExclude("a"),
                    DesktopLayoutIconRule.ForPin("b")));

        var p1 = Resolve();
        var p2 = Resolve();

        Assert.Equal(p1.InputIconCount, p2.InputIconCount);
        Assert.Equal(p1.TargetDisplayIconCount, p2.TargetDisplayIconCount);
        Assert.Equal(p1.DefaultManagedIconCount, p2.DefaultManagedIconCount);
        Assert.Equal(p1.PinnedCount, p2.PinnedCount);
        Assert.Equal(p1.ExcludedCount, p2.ExcludedCount);
        Assert.Equal(p1.UnmatchedRuleCount, p2.UnmatchedRuleCount);
        Assert.True(p1.DefaultManagedIcons.Select(i => i.StableId)
            .SequenceEqual(p2.DefaultManagedIcons.Select(i => i.StableId)));
    }

    // ═══ 图标严格分区 ═══

    [Fact] public void TargetDisplayIcons_PartitionIsExact()
    {
        var icons = new[] { I(0, "a"), I(1, "b"), I(2, "c", mon: "OTHER") };
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd, icons, DesktopLayoutIconRuleSet.Empty(_tid));

        Assert.Equal(3, plan.InputIconCount);
        Assert.Equal(2, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
        // 分区无遗漏
        var inputStableIds = new HashSet<string>(icons.Select(i => i.StableId), StringComparer.Ordinal);
        var partitionStableIds = new HashSet<string>(
            plan.TargetDisplayIcons.Concat(plan.OutOfScopeIcons).Select(i => i.StableId),
            StringComparer.Ordinal);
        Assert.True(inputStableIds.SetEquals(partitionStableIds));
    }

    [Fact] public void TargetDisplayIcons_Partition_NoOverlap()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForPin("b")));

        // DefaultManaged + Excluded + Pinned 覆盖全部 TargetDisplayIcons
        var partitioned = new HashSet<DesktopIconSnapshotItem>(
            plan.DefaultManagedIcons
                .Concat(plan.ExcludedMatches.Select(m => m.Icon))
                .Concat(plan.PinnedMatches.Select(m => m.Icon))
                .Concat(plan.ZoneOverrideMatches.Select(m => m.Icon)),
            ReferenceEqualityComparer.Instance);
        var targetSet = new HashSet<DesktopIconSnapshotItem>(
            plan.TargetDisplayIcons, ReferenceEqualityComparer.Instance);
        Assert.True(partitioned.SetEquals(targetSet));
    }

    // ═══ 规则严格分区 ═══

    [Fact] public void RuleSet_Partition_CoversAllRules()
    {
        var rules = new[]
        {
            DesktopLayoutIconRule.ForExclude("a"),
            DesktopLayoutIconRule.ForPin("b"),
            DesktopLayoutIconRule.ForPin("unmatched")
        };
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(rules));

        var matched = new HashSet<DesktopLayoutIconRule>(ReferenceEqualityComparer.Instance);
        foreach (var m in plan.ExcludedMatches) matched.Add(m.Rule);
        foreach (var m in plan.PinnedMatches) matched.Add(m.Rule);
        foreach (var m in plan.ZoneOverrideMatches) matched.Add(m.Rule);
        foreach (var m in plan.OutOfScopeRuleMatches) matched.Add(m.Rule);
        foreach (var u in plan.UnmatchedRules) matched.Add(u);

        Assert.Equal(rules.Length, matched.Count);
        foreach (var r in rules) Assert.Contains(r, matched);
    }

    [Fact] public void RuleSet_Partition_NoRuleDuplicated()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b") },
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("a"), DesktopLayoutIconRule.ForPin("b")));

        var allRules = plan.ExcludedMatches.Select(m => m.Rule)
            .Concat(plan.PinnedMatches.Select(m => m.Rule))
            .Concat(plan.ZoneOverrideMatches.Select(m => m.Rule))
            .Concat(plan.OutOfScopeRuleMatches.Select(m => m.Rule))
            .Concat(plan.UnmatchedRules)
            .ToList();

        Assert.Equal(allRules.Count, new HashSet<DesktopLayoutIconRule>(
            allRules, ReferenceEqualityComparer.Instance).Count);
    }

    // ═══ 外部等值对象不能通过引用不变量 ═══

    [Fact] public void ExternalEquivalentIcon_NotInPlan()
    {
        var icon = I(0, "a");
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { icon },
            DesktopLayoutIconRuleSet.Empty(_tid));

        // 构造一个等值但不相同的图标
        var external = I(0, "a");
        Assert.NotSame(icon, external);
        Assert.False(ReferenceEquals(plan.TargetDisplayIcons[0], external));
    }

    [Fact] public void ExternalEquivalentRule_NotInPlan()
    {
        var rule = DesktopLayoutIconRule.ForExclude("a");
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a") },
            MakeRuleSet(rule));

        var external = DesktopLayoutIconRule.ForExclude("a");
        Assert.NotSame(rule, external);
        Assert.False(ReferenceEquals(plan.ExcludedMatches[0].Rule, external));
    }

    // ═══ 统计字段 ═══

    [Fact] public void Stats_Accurate()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a"), I(1, "b"), I(2, "c"), I(3, "d", mon: "OTHER") },
            MakeRuleSet(
                DesktopLayoutIconRule.ForExclude("a"),
                DesktopLayoutIconRule.ForPin("b"),
                DesktopLayoutIconRule.ForZoneOverride("c", _zid0),
                DesktopLayoutIconRule.ForExclude("unmatched")));

        Assert.Equal(4, plan.InputIconCount);
        Assert.Equal(3, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.OutOfScopeIconCount);
        Assert.Equal(0, plan.DefaultManagedIconCount);
        Assert.Equal(1, plan.PinnedCount);
        Assert.Equal(1, plan.ExcludedCount);
        Assert.Equal(1, plan.ZoneOverrideCount);
        Assert.Equal(0, plan.OutOfScopeRuleMatchCount);
        Assert.Equal(1, plan.UnmatchedRuleCount);
        Assert.Equal(3, plan.EffectiveRuleCount);
        Assert.True(plan.HasRules);
        Assert.True(plan.HasEffectiveRules);
        Assert.True(plan.HasUnmatchedRules);
    }

    // ═══ OutOfScopeRuleMatches 中 Override 仍有 Zone 引用 ═══

    [Fact] public void OutOfScope_Override_StillHasZoneReference()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            new[] { I(0, "a", mon: "OTHER") },
            MakeRuleSet(DesktopLayoutIconRule.ForZoneOverride("a", _zid0)));

        Assert.Equal(1, plan.OutOfScopeRuleMatchCount);
        Assert.NotNull(plan.OutOfScopeRuleMatches[0].TargetZone);
        Assert.Equal(_zid0, plan.OutOfScopeRuleMatches[0].TargetZone!.ZoneId);
        Assert.False(plan.OutOfScopeRuleMatches[0].IsOnTargetDisplay);
    }

    // ═══ 空输入 ═══

    [Fact] public void EmptyIcons_Valid()
    {
        var plan = DesktopLayoutIconRuleResolver.Resolve(
            MakeTemplate(), _dd,
            Array.Empty<DesktopIconSnapshotItem>(),
            MakeRuleSet(DesktopLayoutIconRule.ForExclude("x")));

        Assert.Equal(0, plan.InputIconCount);
        Assert.Equal(0, plan.TargetDisplayIconCount);
        Assert.Equal(1, plan.UnmatchedRuleCount);
    }
}
