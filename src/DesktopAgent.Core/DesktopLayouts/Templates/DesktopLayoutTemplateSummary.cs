namespace DesktopAgent.Core.DesktopLayouts.Templates;

/// <summary>
/// 模板摘要，用于列表展示。不含完整 Zones 数据。
/// </summary>
public sealed record DesktopLayoutTemplateSummary
{
    public Guid TemplateId { get; }
    public string Name { get; }
    public int Version { get; }
    public TargetDisplayBinding TargetDisplayBinding { get; }
    public int ZoneCount { get; }

    public DesktopLayoutTemplateSummary(
        Guid templateId,
        string name,
        int version,
        TargetDisplayBinding targetDisplayBinding,
        int zoneCount)
    {
        if (templateId == Guid.Empty)
            throw new ArgumentException("TemplateId 不能为 Guid.Empty。", nameof(templateId));
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Name 不能为 null、空字符串或空白。", nameof(name));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version), version,
                "Version 必须大于 0。");
        if (targetDisplayBinding is null)
            throw new ArgumentNullException(nameof(targetDisplayBinding));
        if (zoneCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(zoneCount), zoneCount,
                "ZoneCount 必须大于 0。");

        TemplateId = templateId;
        Name = name;
        Version = version;
        TargetDisplayBinding = targetDisplayBinding;
        ZoneCount = zoneCount;
    }
}
