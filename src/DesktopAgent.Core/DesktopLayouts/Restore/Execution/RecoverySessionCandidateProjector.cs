namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 持久化恢复候选经过 Core 权威解释后的结果。
///
/// HasCorruption=true 表示候选集合本身包含非法条目；
/// 此时 Unresolved 必须为空，调用方应 fail-closed。
/// </summary>
public sealed record RecoverySessionCandidateProjectionResult
{
    private IReadOnlyList<RecoverySessionRecord> _unresolved =
        Array.Empty<RecoverySessionRecord>();

    public IReadOnlyList<RecoverySessionRecord> Unresolved
    {
        get => _unresolved;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _unresolved = Array.AsReadOnly(value.ToArray());
        }
    }

    public bool HasCorruption { get; init; }

    public static RecoverySessionCandidateProjectionResult Corrupted() =>
        new()
        {
            Unresolved = Array.Empty<RecoverySessionRecord>(),
            HasCorruption = true,
        };

    public static RecoverySessionCandidateProjectionResult Clean(
        IReadOnlyList<RecoverySessionRecord> unresolved)
    {
        ArgumentNullException.ThrowIfNull(unresolved);
        return new RecoverySessionCandidateProjectionResult
        {
            Unresolved = unresolved,
            HasCorruption = false,
        };
    }
}

/// <summary>
/// 将持久化层返回的“未解决候选”重新投影为 Core 权威结果。
///
/// 权限边界：
/// - Lifecycle / Disposition 负责业务解释；
/// - Store 只负责提供候选；
/// - 本类型不读取桌面、不访问 Store、不执行 Writer，也不修改输入顺序。
/// </summary>
public static class RecoverySessionCandidateProjector
{
    public static RecoverySessionCandidateProjectionResult Project(
        IReadOnlyList<RecoverySessionRecord> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var unresolved = new List<RecoverySessionRecord>(candidates.Count);
        foreach (var record in candidates)
        {
            if (record is null)
                return RecoverySessionCandidateProjectionResult.Corrupted();

            if (record.GetDisposition().IsUnresolved)
                unresolved.Add(record);
        }

        return RecoverySessionCandidateProjectionResult.Clean(unresolved);
    }
}
