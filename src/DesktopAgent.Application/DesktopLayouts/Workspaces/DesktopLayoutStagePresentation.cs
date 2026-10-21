namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>
/// 面板此刻处于哪一步。此前 19 个按钮、10 个下拉全部平铺在同一页里：
/// 用户既找不到入口，也不知道此刻该干什么。阶段把"一次只做一件事"变成结构。
/// </summary>
public enum DesktopLayoutStage
{
    /// <summary>这台机器能不能被信任：校准与站定授权。</summary>
    Prepare = 0,

    /// <summary>图标摆成什么样：布局、分区、目标屏。</summary>
    Design = 1,

    /// <summary>落到桌面：预览 → 应用。</summary>
    Execute = 2,
}

/// <summary>
/// 阶段的呈现表（唯一出处）：标题、一句话提示，以及哪些面归它管。
/// 界面只照着渲染，不再自己判断"这个按钮现在该不该在"。
/// </summary>
public static class DesktopLayoutStagePresentation
{
    public static IReadOnlyList<DesktopLayoutStage> Stages { get; } =
    [
        DesktopLayoutStage.Prepare,
        DesktopLayoutStage.Design,
        DesktopLayoutStage.Execute,
    ];

    /// <summary>
    /// 元素名 → 允许它可见的阶段。名字必须与 `LayoutManagementView.xaml` 的 x:Name 一致，
    /// 契约测试会逐个核对，避免表与界面各自漂移。
    /// </summary>
    private static readonly Dictionary<string, DesktopLayoutStage[]> ElementOwners = new(StringComparer.Ordinal)
    {
        // 阶段独占：只在它自己里面出现。
        ["PrepareStagePanel"] = [DesktopLayoutStage.Prepare],
        ["WorkspaceLibrarySection"] = [DesktopLayoutStage.Prepare, DesktopLayoutStage.Design, DesktopLayoutStage.Execute],
        ["LayoutEditorSurface"] = [DesktopLayoutStage.Design],
        ["LayoutCanvasCard"] = [DesktopLayoutStage.Design, DesktopLayoutStage.Execute],

        // 跨阶段共用：不属于任何单一阶段，但也不是"永远可见"。
        ["LayoutWorkbenchSurface"] = [DesktopLayoutStage.Design, DesktopLayoutStage.Execute],
    };

    /// <summary>
    /// 工作区详情面（`WorkspaceDetailSurface`）不在这张表里：它的可见性由详情流程自己开关，
    /// 阶段只保证"离开设计阶段一定收起"。把它写进阶段表会让两个写者互相打架。
    /// </summary>
    public static IReadOnlyList<string> GatedElementNames { get; } =
        ElementOwners.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();

    public static string Title(DesktopLayoutStage stage) => stage switch
    {
        DesktopLayoutStage.Prepare => "设备准备",
        DesktopLayoutStage.Design => "设计库",
        DesktopLayoutStage.Execute => "预览与应用",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "未知阶段。"),
    };

    public static string Hint(DesktopLayoutStage stage) => stage switch
    {
        DesktopLayoutStage.Prepare => "校准、显示器授权与恢复工具。查看设计和预览不需要在这里反复准备。",
        DesktopLayoutStage.Design => "从设计库选择方案；在 AI 助手中对话或调整画布，保存后生成预览并应用。",
        DesktopLayoutStage.Execute => "检查当前桌面的预览与移动明细，确认后再应用；桌面变化时需要刷新预览。",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "未知阶段。"),
    };

    /// <summary>阶段按钮的文案：当前阶段带一个实心点，其余不带。</summary>
    public static string StageButtonContent(DesktopLayoutStage stage, bool isActive)
        => isActive ? $"● {Title(stage)}" : Title(stage);

    public static bool IsVisibleInStage(DesktopLayoutStage stage, string elementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementName);

        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "未知阶段。");

        if (!ElementOwners.TryGetValue(elementName, out var owners))
        {
            throw new ArgumentOutOfRangeException(
                nameof(elementName),
                elementName,
                "这个元素不归任何阶段管：要么把它加进阶段表，要么别拿它问阶段。");
        }

        return owners.Contains(stage);
    }
}
