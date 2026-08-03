using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;

namespace DesktopAgent.Poc.LayoutTemplates.Editor;

/// <summary>
/// 单条规则编辑草稿。允许暂时无效，保存时 fail-closed。
/// </summary>
public sealed class LayoutIconRuleEditorDraft : INotifyPropertyChanged
{
    private string _stableId;
    public string StableId
    {
        get => _stableId;
        set { if (_stableId != value) { _stableId = value; OnPropertyChanged(); } }
    }

    private DesktopLayoutIconRuleKind _kind;
    public DesktopLayoutIconRuleKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsZoneOverride));
            // Exclude/Pin 时主动清除 TargetZoneId
            if (value != DesktopLayoutIconRuleKind.ZoneOverride)
                TargetZoneId = null;
        }
    }

    private Guid? _targetZoneId;
    public Guid? TargetZoneId
    {
        get => _targetZoneId;
        set { if (_targetZoneId != value) { _targetZoneId = value; OnPropertyChanged(); } }
    }

    public bool IsZoneOverride => Kind == DesktopLayoutIconRuleKind.ZoneOverride;

    public LayoutIconRuleEditorDraft(string stableId, DesktopLayoutIconRuleKind kind,
        Guid? targetZoneId)
    {
        _stableId = stableId;
        _kind = kind;
        _targetZoneId = targetZoneId;
    }

    public static LayoutIconRuleEditorDraft CreateNew() => new(
        "", DesktopLayoutIconRuleKind.Exclude, null);

    public static LayoutIconRuleEditorDraft FromRule(DesktopLayoutIconRule rule) => new(
        rule.StableId, rule.Kind, rule.TargetZoneId);

    /// <summary>尝试构建领域对象。返回 null + error 表示无效。</summary>
    public DesktopLayoutIconRule? TryBuild(out string? error)
    {
        error = null;

        if (StableId is null || StableId.Length == 0)
        {
            error = "StableId 不能为空。";
            return null;
        }

        if (!Enum.IsDefined(Kind))
        {
            error = $"无效的 RuleKind：{(int)Kind}。";
            return null;
        }

        Guid? targetZoneId;
        switch (Kind)
        {
            case DesktopLayoutIconRuleKind.Exclude:
            case DesktopLayoutIconRuleKind.Pin:
                targetZoneId = null;
                break;
            case DesktopLayoutIconRuleKind.ZoneOverride:
                if (!TargetZoneId.HasValue || TargetZoneId.Value == Guid.Empty)
                {
                    error = "ZoneOverride 必须指定有效的 TargetZoneId。";
                    return null;
                }
                targetZoneId = TargetZoneId.Value;
                break;
            default:
                error = $"未处理的 RuleKind：{(int)Kind}。";
                return null;
        }

        try
        {
            return new DesktopLayoutIconRule(StableId, Kind, targetZoneId);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
