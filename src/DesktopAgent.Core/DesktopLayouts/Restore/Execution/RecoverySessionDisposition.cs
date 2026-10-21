namespace DesktopAgent.Core.DesktopLayouts.Restore.Execution;

/// <summary>
/// 一个持久化恢复会话接下来应如何被处理。
///
/// 这是从历史 Lifecycle 派生出的行为维度，不描述当前桌面的实时事实。
/// </summary>
public enum RecoverySessionHandling
{
    /// <summary>不需要恢复处理。</summary>
    None = 0,

    /// <summary>需要重新观察真实桌面并自动对账；典型场景是崩溃残留的 Executing。</summary>
    AutomaticReconciliation = 1,

    /// <summary>自动安全内核已经停止，需要人工介入。</summary>
    ManualIntervention = 2,
}

/// <summary>
/// 上一次持久化恢复判断留下的安全分类。
///
/// 注意：PositionUncertain 是“上一次判断无法证明位置”的历史结论，
/// 不是对当前桌面实时位置的声明；重新恢复时仍必须 fresh observe。
/// </summary>
public enum RecoverySessionSafetyClass
{
    /// <summary>没有遗留安全告警。</summary>
    None = 0,

    /// <summary>当前失败是已知的不完整恢复，而不是未知位置。</summary>
    KnownIncomplete = 1,

    /// <summary>上一次恢复无法安全证明真实位置。</summary>
    PositionUncertain = 2,
}

/// <summary>
/// 将持久化 Lifecycle 投影为彼此正交的“处理方式 + 安全分类”。
///
/// Lifecycle 是持久化语义源；业务层判断是否未解决、是否需要人工、
/// 是否属于位置不确定时，应使用本投影，而不是持久化第二份布尔状态。
/// </summary>
public readonly record struct RecoverySessionDisposition(
    RecoverySessionHandling Handling,
    RecoverySessionSafetyClass SafetyClass)
{
    public bool IsUnresolved => Handling != RecoverySessionHandling.None;

    public bool RequiresManualRecovery =>
        Handling == RecoverySessionHandling.ManualIntervention;

    public bool IsPositionUncertain =>
        SafetyClass == RecoverySessionSafetyClass.PositionUncertain;

    public static RecoverySessionDisposition FromLifecycle(RecoverySessionLifecycle lifecycle) =>
        lifecycle switch
        {
            RecoverySessionLifecycle.Ready
                => new(RecoverySessionHandling.None, RecoverySessionSafetyClass.None),

            RecoverySessionLifecycle.Executing
                => new(RecoverySessionHandling.AutomaticReconciliation, RecoverySessionSafetyClass.None),

            RecoverySessionLifecycle.Completed
                or RecoverySessionLifecycle.FailedAndRolledBack
                or RecoverySessionLifecycle.AcceptedCurrentState
                => new(RecoverySessionHandling.None, RecoverySessionSafetyClass.None),

            RecoverySessionLifecycle.FailedRollbackIncomplete
                => new(RecoverySessionHandling.ManualIntervention, RecoverySessionSafetyClass.KnownIncomplete),

            RecoverySessionLifecycle.PositionUncertain
                => new(RecoverySessionHandling.ManualIntervention, RecoverySessionSafetyClass.PositionUncertain),

            _ => throw new ArgumentOutOfRangeException(
                nameof(lifecycle), lifecycle, $"未定义的 RecoverySessionLifecycle 值：{(int)lifecycle}。"),
        };
}

/// <summary>
/// RecoverySessionRecord 的行为投影入口。
///
/// Record 只持久化 Lifecycle；Handling / SafetyClass 以及相关布尔摘要都在读取时现场投影。
/// </summary>
public static class RecoverySessionDispositionExtensions
{
    public static RecoverySessionDisposition GetDisposition(this RecoverySessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RecoverySessionDisposition.FromLifecycle(record.Lifecycle);
    }

    /// <summary>
    /// 只变更权威 Lifecycle。派生处置语义由 GetDisposition() 现场计算。
    /// </summary>
    public static RecoverySessionRecord WithProjectedLifecycle(
        this RecoverySessionRecord record,
        RecoverySessionLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = RecoverySessionDisposition.FromLifecycle(lifecycle);

        return record with
        {
            Lifecycle = lifecycle,
        };
    }
}
