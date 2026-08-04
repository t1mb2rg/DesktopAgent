using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

/// <summary>
/// TryBuild 的结果。
/// </summary>
public sealed record LayoutTemplateDraftBuildResult
{
    public bool IsValid { get; init; }
    public DesktopLayoutTemplate? Template { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// RuleSet TryBuild 的结果。
/// </summary>
public sealed record LayoutRuleSetDraftBuildResult
{
    public bool IsValid { get; init; }
    public DesktopLayoutIconRuleSet? RuleSet { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 模板编辑草稿。允许属性暂时无效，保存时 fail-closed。
/// 不含 WPF 控件引用，实现 INotifyPropertyChanged。
/// </summary>
public sealed class LayoutTemplateEditorDraft : INotifyPropertyChanged
{
    public Guid TemplateId { get; private set; }

    private string _name;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); SetDirty(); } }
    }

    private int _version;
    public int Version
    {
        get => _version;
        set { if (_version != value) { _version = value; OnPropertyChanged(); SetDirty(); } }
    }

    private TargetDisplayBindingKind _bindingKind;
    public TargetDisplayBindingKind BindingKind
    {
        get => _bindingKind;
        set { if (_bindingKind != value) { _bindingKind = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsExactDeviceName)); SetDirty(); } }
    }

    private string _exactDeviceName;
    public string ExactDeviceName
    {
        get => _exactDeviceName;
        set { if (_exactDeviceName != value) { _exactDeviceName = value; OnPropertyChanged(); SetDirty(); } }
    }

    public bool IsExactDeviceName => BindingKind == TargetDisplayBindingKind.ExactDeviceName;

    public ObservableCollection<LayoutZoneEditorDraft> Zones { get; } = [];

    public ObservableCollection<LayoutIconRuleEditorDraft> Rules { get; } = [];

    public int RuleCount => Rules.Count;
    public int ExcludeRuleCount => Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.Exclude);
    public int PinRuleCount => Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.Pin);
    public int ZoneOverrideRuleCount => Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.ZoneOverride);

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set { if (_isDirty != value) { _isDirty = value; OnPropertyChanged(); } }
    }

    private bool _isPersisted;
    public bool IsPersisted
    {
        get => _isPersisted;
        private set { if (_isPersisted != value) { _isPersisted = value; OnPropertyChanged(); } }
    }

    public LayoutTemplateEditorDraft(
        Guid templateId, string name, int version,
        TargetDisplayBindingKind bindingKind, string exactDeviceName,
        IEnumerable<LayoutZoneEditorDraft> zones)
    {
        TemplateId = templateId;
        _name = name;
        _version = version;
        _bindingKind = bindingKind;
        _exactDeviceName = exactDeviceName;

        foreach (var zone in zones)
        {
            Zones.Add(zone);
            SubscribeZone(zone);
        }

        Zones.CollectionChanged += OnZonesCollectionChanged;
        Rules.CollectionChanged += OnRulesCollectionChanged;
    }

    public static LayoutTemplateEditorDraft CreateNew()
    {
        var draft = new LayoutTemplateEditorDraft(
            Guid.NewGuid(), "新布局模板", 1,
            TargetDisplayBindingKind.SelectedTarget, "",
            new[]
            {
                new LayoutZoneEditorDraft(
                    Guid.NewGuid(), "默认分区",
                    0, 0, NormalizedZoneEditorGeometry.NormalizedMax, NormalizedZoneEditorGeometry.NormalizedMax,
                    0, ZonePlacementFlow.ColumnMajor, null)
            });
        draft.IsDirty = true;
        draft.IsPersisted = false;
        return draft;
    }

    public static LayoutTemplateEditorDraft FromTemplate(DesktopLayoutTemplate template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));

        var zones = new List<LayoutZoneEditorDraft>();
        foreach (var zone in template.Zones)
            zones.Add(LayoutZoneEditorDraft.FromZone(zone));

        var bindingKind = template.TargetDisplayBinding.Kind;
        var deviceName = bindingKind == TargetDisplayBindingKind.ExactDeviceName
            ? template.TargetDisplayBinding.DeviceName! : "";

        var draft = new LayoutTemplateEditorDraft(
            template.TemplateId, template.Name, template.Version,
            bindingKind, deviceName, zones);
        draft.IsDirty = false;
        draft.IsPersisted = true;
        return draft;
    }

    public static LayoutTemplateEditorDraft FromTemplateAndRuleSet(
        DesktopLayoutTemplate template, DesktopLayoutIconRuleSet ruleSet)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (ruleSet is null) throw new ArgumentNullException(nameof(ruleSet));
        if (template.TemplateId != ruleSet.TemplateId)
            throw new ArgumentException(
                $"TemplateId 不一致：Template={template.TemplateId:D}，RuleSet={ruleSet.TemplateId:D}。");

        var draft = FromTemplate(template);
        foreach (var rule in ruleSet.Rules)
            draft.Rules.Add(LayoutIconRuleEditorDraft.FromRule(rule));
        draft.IsDirty = false;
        draft.IsPersisted = true;
        return draft;
    }

    public LayoutTemplateDraftBuildResult TryBuild()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("模板 Name 不能为空白。");

        if (Version <= 0)
            errors.Add($"模板 Version 必须 > 0，实际 {Version}。");

        if (BindingKind != TargetDisplayBindingKind.SelectedTarget
            && BindingKind != TargetDisplayBindingKind.PrimaryDisplay
            && BindingKind != TargetDisplayBindingKind.ExactDeviceName)
            errors.Add($"不支持的 TargetDisplayBindingKind: {BindingKind}。");

        if (BindingKind == TargetDisplayBindingKind.ExactDeviceName)
        {
            if (string.IsNullOrEmpty(ExactDeviceName))
                errors.Add("ExactDeviceName 模式下 ExactDeviceName 不能为空。");
        }

        if (Zones.Count == 0)
            errors.Add("至少需要一个 Zone。");

        // 验证 Zone
        var validZones = new List<DesktopLayoutZone>();
        for (var i = 0; i < Zones.Count; i++)
        {
            var zd = Zones[i];
            try
            {
                var zone = zd.Build();
                validZones.Add(zone);
            }
            catch (Exception ex)
            {
                errors.Add($"Zone[{i}] \"{zd.Name}\" (ZoneId={zd.ZoneId:D}) 构建失败：{ex.Message}");
            }
        }

        if (errors.Count > 0 || validZones.Count != Zones.Count)
            return new LayoutTemplateDraftBuildResult
            {
                IsValid = false,
                Template = null,
                Errors = errors.AsReadOnly()
            };

        // 构造 DesktopLayoutTemplate
        TargetDisplayBinding binding;
        try
        {
            binding = BindingKind switch
            {
                TargetDisplayBindingKind.SelectedTarget =>
                    TargetDisplayBinding.ForSelectedTarget(),
                TargetDisplayBindingKind.PrimaryDisplay =>
                    TargetDisplayBinding.ForPrimaryDisplay(),
                TargetDisplayBindingKind.ExactDeviceName =>
                    TargetDisplayBinding.ForExactDeviceName(ExactDeviceName),
                _ => throw new InvalidOperationException(
                    $"不支持的 TargetDisplayBindingKind: {BindingKind}。")
            };
        }
        catch (Exception ex)
        {
            errors.Add($"TargetDisplayBinding 构造失败：{ex.Message}");
            return new LayoutTemplateDraftBuildResult
            {
                IsValid = false, Template = null, Errors = errors.AsReadOnly()
            };
        }

        try
        {
            var template = new DesktopLayoutTemplate(
                TemplateId, Name, Version, binding, validZones);
            return new LayoutTemplateDraftBuildResult
            {
                IsValid = true,
                Template = template,
                Errors = errors.AsReadOnly()
            };
        }
        catch (Exception ex)
        {
            errors.Add($"DesktopLayoutTemplate 构造失败：{ex.Message}");
            return new LayoutTemplateDraftBuildResult
            {
                IsValid = false, Template = null, Errors = errors.AsReadOnly()
            };
        }
    }

    public LayoutRuleSetDraftBuildResult TryBuildRuleSet()
    {
        var errors = new List<string>();
        var rules = new List<DesktopLayoutIconRule>();
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < Rules.Count; i++)
        {
            var rd = Rules[i];
            var rule = rd.TryBuild(out var error);
            if (rule is null)
            {
                errors.Add($"Rule[{i}] (StableId=\"{rd.StableId}\") 构建失败：{error}");
                continue;
            }

            if (!seenStableIds.Add(rule.StableId))
            {
                errors.Add($"Rule[{i}] StableId \"{rd.StableId}\" 重复。");
                continue;
            }

            // ZoneOverride 验证 TargetZoneId 存在于当前草稿 Zones
            if (rule.Kind == DesktopLayoutIconRuleKind.ZoneOverride)
            {
                var zid = rule.TargetZoneId!.Value;
                var found = false;
                foreach (var z in Zones)
                {
                    if (z.ZoneId == zid) { found = true; break; }
                }
                if (!found)
                {
                    errors.Add($"Rule[{i}] ZoneOverride TargetZoneId={zid:D} 不在当前 Zones 中。");
                    continue;
                }
            }

            rules.Add(rule);
        }

        if (errors.Count > 0)
            return new LayoutRuleSetDraftBuildResult
            {
                IsValid = false, RuleSet = null, Errors = errors.AsReadOnly()
            };

        try
        {
            var ruleSet = new DesktopLayoutIconRuleSet(TemplateId, rules);
            return new LayoutRuleSetDraftBuildResult
            {
                IsValid = true, RuleSet = ruleSet, Errors = Array.Empty<string>()
            };
        }
        catch (Exception ex)
        {
            errors.Add($"DesktopLayoutIconRuleSet 构造失败：{ex.Message}");
            return new LayoutRuleSetDraftBuildResult
            {
                IsValid = false, RuleSet = null, Errors = errors.AsReadOnly()
            };
        }
    }

    public void MarkSaved()
    {
        IsDirty = false;
        IsPersisted = true;
    }

    // ═══ Dirty tracking ═══

    private readonly HashSet<LayoutZoneEditorDraft> _subscribedZones =
        new(ReferenceEqualityComparer.Instance);

    private void SetDirty() => IsDirty = true;

    private void SubscribeZone(LayoutZoneEditorDraft zone)
    {
        if (_subscribedZones.Add(zone))
            zone.PropertyChanged += OnZonePropertyChanged;
    }

    private void UnsubscribeZone(LayoutZoneEditorDraft zone)
    {
        if (_subscribedZones.Remove(zone))
            zone.PropertyChanged -= OnZonePropertyChanged;
    }

    private void SyncZoneSubscriptions()
    {
        var toRemove = new List<LayoutZoneEditorDraft>();
        foreach (var existing in _subscribedZones)
        {
            if (!Zones.Contains(existing)) // ObservableCollection 使用默认 Equal，需手动遍历
            {
                var found = false;
                foreach (var z in Zones)
                {
                    if (ReferenceEquals(z, existing)) { found = true; break; }
                }
                if (!found) toRemove.Add(existing);
            }
        }
        foreach (var item in toRemove)
            UnsubscribeZone(item);

        foreach (var zone in Zones)
        {
            if (!_subscribedZones.Contains(zone))
                SubscribeZone(zone);
        }
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e) => SetDirty();

    private void OnZonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncZoneSubscriptions();
        SetDirty();
    }

    // ═══ Rule Dirty tracking ═══

    private readonly HashSet<LayoutIconRuleEditorDraft> _subscribedRules =
        new(ReferenceEqualityComparer.Instance);

    private void SubscribeRule(LayoutIconRuleEditorDraft rule)
    {
        if (_subscribedRules.Add(rule))
            rule.PropertyChanged += OnRulePropertyChanged;
    }

    private void UnsubscribeRule(LayoutIconRuleEditorDraft rule)
    {
        if (_subscribedRules.Remove(rule))
            rule.PropertyChanged -= OnRulePropertyChanged;
    }

    private void SyncRuleSubscriptions()
    {
        var toRemove = new List<LayoutIconRuleEditorDraft>();
        foreach (var existing in _subscribedRules)
        {
            var found = false;
            foreach (var r in Rules)
                if (ReferenceEquals(r, existing)) { found = true; break; }
            if (!found) toRemove.Add(existing);
        }
        foreach (var item in toRemove)
            UnsubscribeRule(item);

        foreach (var rule in Rules)
            if (!_subscribedRules.Contains(rule))
                SubscribeRule(rule);
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SetDirty();
        if (e.PropertyName is null || e.PropertyName == nameof(LayoutIconRuleEditorDraft.Kind))
            NotifyRuleStatsChanged();
    }

    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncRuleSubscriptions();
        SetDirty();
        NotifyRuleStatsChanged();
    }

    private void NotifyRuleStatsChanged()
    {
        OnPropertyChanged(nameof(RuleCount));
        OnPropertyChanged(nameof(ExcludeRuleCount));
        OnPropertyChanged(nameof(PinRuleCount));
        OnPropertyChanged(nameof(ZoneOverrideRuleCount));
    }

    // ═══ INotifyPropertyChanged ═══

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
