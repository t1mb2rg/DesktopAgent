namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 持久化恢复候选经过 Core 权威解释后的结果。
///
/// HasCorruption=true 表示候选中至少一条记录的旧兼容字段与 Lifecycle 投影冲突；
/// 此时 Unresolved 必须为空，调用方应 fail-closed，而不是尝试从冲突字段猜业务语义。
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
/// - RequiresManualRecovery / IsPositionUncertain 仅负责兼容一致性校验；
/// - 兼容字段与投影冲突时只允许得出 corruption，不允许生成新的业务状态。
///
/// 本类型不读取桌面、不访问 Store、不执行 Writer，也不修改输入顺序。
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
            if (record is null || !record.HasConsistentCompatibilityFlags())
                return RecoverySessionCandidateProjectionResult.Corrupted();

            if (record.GetDisposition().IsUnresolved)
                unresolved.Add(record);
        }

        return RecoverySessionCandidateProjectionResult.Clean(unresolved);
    }
}
