using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Application.Intelligence;

/// <summary>
/// 首次桌面理解的一份可审计提案。
/// 这是用户确认前的语义草案，不是 DesktopLayoutTemplate，也不是执行计划。
/// </summary>
public sealed class DesktopUnderstandingProposal
{
    public const int MaxZoneCount = DesktopZoneLayoutPatternGeometry.MaxZoneCount;
    public const int MaxQuestionCount = 3;
    public const int MaxSummaryLength = 1_000;
    public const int MaxQuestionLength = 300;

    private readonly IReadOnlyList<DesktopUnderstandingZoneProposal> _zones;
    private readonly IReadOnlyList<DesktopUnderstandingItemAssignment> _assignments;
    private readonly IReadOnlyList<string> _questions;

    public string Summary { get; }
    public DesktopZoneLayoutPattern LayoutPattern { get; }
    public IReadOnlyList<DesktopUnderstandingZoneProposal> Zones => _zones;
    public IReadOnlyList<DesktopUnderstandingItemAssignment> Assignments => _assignments;
    public IReadOnlyList<string> Questions => _questions;
    public double Confidence { get; }

    public DesktopUnderstandingProposal(
        string summary,
        DesktopZoneLayoutPattern layoutPattern,
        IEnumerable<DesktopUnderstandingZoneProposal> zones,
        IEnumerable<DesktopUnderstandingItemAssignment> assignments,
        IEnumerable<string>? questions,
        double confidence)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > MaxSummaryLength)
            throw new ArgumentException($"Summary 必须为 1..{MaxSummaryLength} 个非空白字符。", nameof(summary));
        if (!Enum.IsDefined(layoutPattern))
            throw new ArgumentOutOfRangeException(nameof(layoutPattern));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence));

        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(assignments);
        var copiedZones = zones.ToArray();
        var copiedAssignments = assignments.ToArray();
        var copiedQuestions = questions?.ToArray() ?? Array.Empty<string>();

        if (copiedZones.Length is < 1 or > MaxZoneCount || copiedZones.Any(static zone => zone is null))
            throw new ArgumentException($"Zones 必须包含 1..{MaxZoneCount} 个非 null 项。", nameof(zones));
        if (copiedAssignments.Length is < 1 or > DesktopUnderstandingRequest.MaxItemCount ||
            copiedAssignments.Any(static assignment => assignment is null))
        {
            throw new ArgumentException(
                $"Assignments 必须包含 1..{DesktopUnderstandingRequest.MaxItemCount} 个非 null 项。",
                nameof(assignments));
        }
        if (copiedQuestions.Length > MaxQuestionCount ||
            copiedQuestions.Any(static question =>
                string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestionLength))
        {
            throw new ArgumentException(
                $"Questions 最多 {MaxQuestionCount} 条，每条 1..{MaxQuestionLength} 个非空白字符。",
                nameof(questions));
        }

        if (copiedZones.Select(static zone => zone.Key).Distinct(StringComparer.Ordinal).Count() != copiedZones.Length)
            throw new ArgumentException("Zones 的 Key 必须唯一。", nameof(zones));
        if (copiedAssignments.Select(static assignment => assignment.ItemRef).Distinct(StringComparer.Ordinal).Count() != copiedAssignments.Length)
            throw new ArgumentException("Assignments 的 ItemRef 必须唯一。", nameof(assignments));

        var zoneKeys = copiedZones.Select(static zone => zone.Key).ToHashSet(StringComparer.Ordinal);
        if (copiedAssignments.Any(assignment => !zoneKeys.Contains(assignment.ZoneKey)))
            throw new ArgumentException("Assignments 只能引用本 Proposal 内存在的 ZoneKey。", nameof(assignments));

        Summary = summary;
        LayoutPattern = layoutPattern;
        _zones = Array.AsReadOnly(copiedZones);
        _assignments = Array.AsReadOnly(copiedAssignments);
        _questions = Array.AsReadOnly(copiedQuestions);
        Confidence = confidence;
    }
}
