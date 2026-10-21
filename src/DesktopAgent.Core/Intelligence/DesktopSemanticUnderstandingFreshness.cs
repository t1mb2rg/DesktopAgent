using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopObservations;

namespace DesktopAgent.Core.Intelligence;

/// <summary>
/// Persisted Semantic Understanding 相对 fresh DesktopObservation 的运行时 freshness。
/// 该状态永远重新计算，不持久化为数据库 authority。
/// </summary>
public enum DesktopSemanticUnderstandingFreshness
{
    Current = 0,
    PossiblyStale = 1,
    ClearlyStale = 2,
}

/// <summary>一次 freshness 比较的只读诊断结果。</summary>
public sealed record DesktopSemanticUnderstandingFreshnessResult(
    DesktopSemanticUnderstandingFreshness Freshness,
    string CurrentSourceFingerprint,
    int CurrentItemCount,
    int KnownAssignmentCount,
    int MissingKnownAssignmentCount)
{
    public bool IsCurrent => Freshness == DesktopSemanticUnderstandingFreshness.Current;
}

/// <summary>
/// 生成 Semantic Understanding 的来源事实指纹。
/// 只包含会影响“项目是什么”的稳定语义事实；刻意忽略 PixelPosition、MonitorDeviceName、
/// Explorer spacing 与显示器拓扑，因此纯位置变化不会让语义理解变 stale。
/// </summary>
public static class DesktopSemanticUnderstandingSourceFingerprint
{
    public static string Create(DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var builder = new StringBuilder();
        builder.Append("semantic-source-v1\n");
        foreach (var icon in observation.Icons.OrderBy(static icon => icon.StableId, StringComparer.Ordinal))
        {
            AppendField(builder, icon.StableId);
            AppendField(builder, icon.DisplayName);
            AppendField(builder, ((int)icon.ItemKind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendField(builder, icon.IsSystemItem ? "1" : "0");
            AppendField(builder, icon.IsFileSystemItem ? "1" : "0");
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendField(StringBuilder builder, string value)
    {
        builder.Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('|');
    }
}

/// <summary>
/// 使用 fresh DesktopObservation 判断一份持久化 Understanding 是否仍可视为当前语义事实。
/// Exact fingerprint 相同才是 Current；否则只根据已分类项目覆盖率与总项目数量变化给出
/// PossiblyStale / ClearlyStale，不尝试从历史推断“桌面到底发生了什么”。
/// </summary>
public static class DesktopSemanticUnderstandingFreshnessEvaluator
{
    private const double ClearlyStaleMissingAssignmentRatio = 0.20d;
    private const double ClearlyStaleItemCountDeltaRatio = 0.25d;

    public static DesktopSemanticUnderstandingFreshnessResult Evaluate(
        DesktopSemanticUnderstandingSnapshot snapshot,
        DesktopObservation currentObservation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentObservation);

        var currentFingerprint = DesktopSemanticUnderstandingSourceFingerprint.Create(currentObservation);
        var currentItemCount = currentObservation.Icons.Count;
        var currentIds = currentObservation.Icons
            .Select(static icon => icon.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var knownAssignments = snapshot.Understanding.Assignments;
        var missingKnown = knownAssignments.Count(assignment => !currentIds.Contains(assignment.StableId));

        if (string.Equals(
                snapshot.SourceStateFingerprint,
                currentFingerprint,
                StringComparison.Ordinal))
        {
            return new DesktopSemanticUnderstandingFreshnessResult(
                DesktopSemanticUnderstandingFreshness.Current,
                currentFingerprint,
                currentItemCount,
                knownAssignments.Count,
                missingKnown);
        }

        var missingRatio = knownAssignments.Count == 0
            ? 0d
            : (double)missingKnown / knownAssignments.Count;
        var countDeltaRatio = snapshot.SourceItemCount == 0
            ? 1d
            : Math.Abs((double)currentItemCount - snapshot.SourceItemCount) / snapshot.SourceItemCount;

        var freshness = missingRatio > ClearlyStaleMissingAssignmentRatio ||
                        countDeltaRatio > ClearlyStaleItemCountDeltaRatio
            ? DesktopSemanticUnderstandingFreshness.ClearlyStale
            : DesktopSemanticUnderstandingFreshness.PossiblyStale;

        return new DesktopSemanticUnderstandingFreshnessResult(
            freshness,
            currentFingerprint,
            currentItemCount,
            knownAssignments.Count,
            missingKnown);
    }
}
