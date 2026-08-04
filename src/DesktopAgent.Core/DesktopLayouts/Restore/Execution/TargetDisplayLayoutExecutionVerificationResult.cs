namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 执行后的独立验证阶段。
/// </summary>
public enum ExecutionVerificationPhase
{
    /// <summary>Forward 完成后的验证。</summary>
    Forward,
    /// <summary>Rollback 完成后的验证。</summary>
    Rollback,
}

/// <summary>
/// 执行后的独立验证状态。
/// </summary>
public enum ExecutionVerificationStatus
{
    /// <summary>验证通过 — 所有图标符合预期位置和显示器。</summary>
    Passed,
    /// <summary>验证失败 — 位置或身份不匹配。</summary>
    Failed,
    /// <summary>读取或身份不可信 — 无法做出确定性结论。</summary>
    Uncertain,
}

/// <summary>
/// 执行后独立完整重读的验证结果。纯数据，不含 PIDL/COM/HWND/WPF。
/// </summary>
public sealed record TargetDisplayLayoutExecutionVerificationResult
{
    public ExecutionVerificationPhase Phase { get; }
    public ExecutionVerificationStatus Status { get; }
    public int FailedItemCount { get; }
    public IReadOnlyList<string> Issues { get; }
    public DateTimeOffset VerifiedAtUtc { get; }

    public TargetDisplayLayoutExecutionVerificationResult(
        ExecutionVerificationPhase phase,
        ExecutionVerificationStatus status,
        int failedItemCount,
        IReadOnlyList<string> issues,
        DateTimeOffset verifiedAtUtc)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentException($"无效的 Phase 枚举值：{(int)phase}。", nameof(phase));
        if (!Enum.IsDefined(status))
            throw new ArgumentException($"无效的 Status 枚举值：{(int)status}。", nameof(status));
        if (failedItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(failedItemCount));
        ArgumentNullException.ThrowIfNull(issues);

        Phase = phase;
        Status = status;
        FailedItemCount = failedItemCount;
        Issues = issues.ToList().AsReadOnly();
        VerifiedAtUtc = verifiedAtUtc;
    }

    public static TargetDisplayLayoutExecutionVerificationResult ForwardPassed(
        DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Forward,
            ExecutionVerificationStatus.Passed, 0,
            Array.Empty<string>(), verifiedAtUtc);

    public static TargetDisplayLayoutExecutionVerificationResult ForwardFailed(
        int failedItemCount, IReadOnlyList<string> issues, DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Forward,
            ExecutionVerificationStatus.Failed, failedItemCount,
            issues, verifiedAtUtc);

    public static TargetDisplayLayoutExecutionVerificationResult ForwardUncertain(
        int failedItemCount, IReadOnlyList<string> issues, DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Forward,
            ExecutionVerificationStatus.Uncertain, failedItemCount,
            issues, verifiedAtUtc);

    public static TargetDisplayLayoutExecutionVerificationResult RollbackPassed(
        DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Rollback,
            ExecutionVerificationStatus.Passed, 0,
            Array.Empty<string>(), verifiedAtUtc);

    public static TargetDisplayLayoutExecutionVerificationResult RollbackFailed(
        int failedItemCount, IReadOnlyList<string> issues, DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Rollback,
            ExecutionVerificationStatus.Failed, failedItemCount,
            issues, verifiedAtUtc);

    public static TargetDisplayLayoutExecutionVerificationResult RollbackUncertain(
        int failedItemCount, IReadOnlyList<string> issues, DateTimeOffset verifiedAtUtc)
        => new(ExecutionVerificationPhase.Rollback,
            ExecutionVerificationStatus.Uncertain, failedItemCount,
            issues, verifiedAtUtc);
}
