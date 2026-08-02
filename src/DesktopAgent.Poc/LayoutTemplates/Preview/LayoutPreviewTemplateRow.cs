using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.Displays;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public sealed class LayoutPreviewTemplateRow
{
    public DesktopLayoutTemplateSummary Summary { get; }
    public string DisplayText { get; }

    public LayoutPreviewTemplateRow(DesktopLayoutTemplateSummary summary)
    {
        Summary = summary;
        var idShort = summary.TemplateId.ToString("D")[..8] + "...";
        var kind = summary.TargetDisplayBinding.Kind.ToString();
        DisplayText = $"{summary.Name} (v{summary.Version}) [{kind}] {summary.ZoneCount}z  {idShort}";
    }
}

public sealed class LayoutPreviewDisplayRow
{
    public string DeviceName { get; }
    public string BoundsText { get; }
    public string WorkAreaText { get; }
    public string DpiText { get; }
    public bool IsPrimary { get; }
    public string AspectRatioText { get; }
    public string DisplayText { get; }

    public LayoutPreviewDisplayRow(DisplayDescriptor d)
    {
        DeviceName = d.DeviceName;
        BoundsText = $"({d.Bounds.Left},{d.Bounds.Top}) {d.Bounds.Width}×{d.Bounds.Height}";
        WorkAreaText = $"({d.WorkArea.Left},{d.WorkArea.Top}) {d.WorkArea.Width}×{d.WorkArea.Height}";
        DpiText = $"{d.DpiX}×{d.DpiY}";
        IsPrimary = d.IsPrimary;
        var ratio = (double)d.WorkArea.Width / d.WorkArea.Height;
        AspectRatioText = ratio switch
        {
            >= 1.77 and < 1.78 => "16:9",
            >= 1.59 and < 1.61 => "16:10",
            >= 1.32 and < 1.34 => "4:3",
            _ => $"{ratio:F2}:1"
        };
        DisplayText = $"{d.DeviceName} {(d.IsPrimary ? "[主屏]" : "")} {WorkAreaText} {AspectRatioText}";
    }
}
