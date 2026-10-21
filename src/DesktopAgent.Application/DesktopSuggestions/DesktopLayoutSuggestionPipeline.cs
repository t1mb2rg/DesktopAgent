using DesktopAgent.Application.DesktopObservations;

namespace DesktopAgent.Application.DesktopSuggestions;

public sealed record DesktopLayoutSuggestionBindingSnapshot(
    long Revision,
    DesktopLayoutSuggestionBinding? Binding)
{
    public bool IsConfigured => Binding is not null;
}

public enum DesktopLayoutSuggestionPipelineStatus
{
    BaselineReestablished = 0,
    NoChanges = 1,
    ObservationUnstable = 2,
    ObservationUnavailable = 3,
    NotConfigured = 4,
    Suggested = 5,
    NoActionNeeded = 6,
    BuildRejected = 7,
    BindingChangedDuringBuild = 8,
    BuilderFailed = 9,
}

public sealed record DesktopLayoutSuggestionPipelineResult
{
    public DesktopLayoutSuggestionPipelineStatus Status { get; }
    public DesktopObservationDiagnosticsResult Diagnostics { get; }
    public DesktopLayoutSuggestionBindingSnapshot BindingSnapshot { get; }
    public DesktopLayoutSuggestionBuildResult? BuildResult { get; }
    public DesktopLayoutSuggestionLifecycleSnapshot Lifecycle { get; }
    public string? EscapedException { get; }

    public DesktopLayoutSuggestion? CurrentSuggestion => Lifecycle.CurrentSuggestion;

    public DesktopLayoutSuggestionPipelineResult(
        DesktopLayoutSuggestionPipelineStatus status,
        DesktopObservationDiagnosticsResult diagnostics,
        DesktopLayoutSuggestionBindingSnapshot bindingSnapshot,
        DesktopLayoutSuggestionBuildResult? buildResult,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle,
        string? escapedException = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(bindingSnapshot);
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (status == DesktopLayoutSuggestionPipelineStatus.Suggested
            && (buildResult?.Status != DesktopLayoutSuggestionBuildStatus.Suggested
                || lifecycle.State != DesktopLayoutSuggestionPresentationState.Active
                || lifecycle.CurrentSuggestion is null
                || !ReferenceEquals(buildResult.Suggestion, lifecycle.CurrentSuggestion)))
        {
            throw new ArgumentException(
                "Suggested pipeline result 必须包含同一 Suggested build 与 Active lifecycle。",
                nameof(status));
        }

        if (status == DesktopLayoutSuggestionPipelineStatus.NoActionNeeded
            && buildResult?.Status != DesktopLayoutSuggestionBuildStatus.NoActionNeeded)
        {
            throw new ArgumentException(
                "NoActionNeeded pipeline result 必须携带对应 build result。",
                nameof(status));
        }

        if (status == DesktopLayoutSuggestionPipelineStatus.BuildRejected
            && buildResult is null)
        {
            throw new ArgumentException(
                "BuildRejected 必须携带 build result。",
                nameof(buildResult));
        }

        if (status == DesktopLayoutSuggestionPipelineStatus.BuilderFailed
            && string.IsNullOrWhiteSpace(escapedException))
        {
            throw new ArgumentException(
                "BuilderFailed 必须携带 EscapedException。",
                nameof(escapedException));
        }

        if (status != DesktopLayoutSuggestionPipelineStatus.BuilderFailed
            && escapedException is not null)
        {
            throw new ArgumentException(
                "只有 BuilderFailed 可以携带 EscapedException。",
                nameof(escapedException));
        }

        Status = status;
        Diagnostics = diagnostics;
        BindingSnapshot = bindingSnapshot;
        BuildResult = buildResult;
        Lifecycle = lifecycle;
        EscapedException = escapedException;
    }
}

/// <summary>
/// M3-03C/03D 只读 Suggestion pipeline：解释 observation diagnostics，维护 suggestion lifecycle，
/// 并在 ChangesDetected 时使用显式 binding 构建新 Suggestion。
/// 不依赖 WPF、WindowsInterop、Writer 或 ExecutionCoordinator。
/// </summary>
public sealed class DesktopLayoutSuggestionPipeline
{
    private readonly object _bindingGate = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly IDesktopLayoutSuggestionBuilder _builder;
    private readonly DesktopLayoutSuggestionLifecycle _lifecycle;

    private long _bindingRevision;
    private DesktopLayoutSuggestionBinding? _binding;

    public DesktopLayoutSuggestionPipeline(
        IDesktopLayoutSuggestionBuilder builder,
        DesktopLayoutSuggestionLifecycle lifecycle)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public DesktopLayoutSuggestionBindingSnapshot BindingSnapshot
    {
        get
        {
            lock (_bindingGate)
                return CreateBindingSnapshot();
        }
    }

    public DesktopLayoutSuggestionLifecycleSnapshot LifecycleSnapshot =>
        _lifecycle.Snapshot;

    public DesktopLayoutSuggestionBindingSnapshot SetBinding(
        DesktopLayoutSuggestionBinding? binding)
    {
        lock (_bindingGate)
        {
            if (Equals(_binding, binding))
                return CreateBindingSnapshot();

            _binding = binding;
            _bindingRevision++;

            // 与 ProcessAsync 最终 publish 使用相同锁顺序：bindingGate -> lifecycle gate。
            _lifecycle.MarkBindingChanged();
            return CreateBindingSnapshot();
        }
    }

    /// <summary>
    /// UI 显式 Reset baseline 时立即退休旧建议；baseline store 的实际 Reset 由协调层同时执行。
    /// </summary>
    public DesktopLayoutSuggestionLifecycleSnapshot MarkBaselineReset() =>
        _lifecycle.MarkBaselineReset();

    /// <summary>
    /// 02B run 连 diagnostics 都未能正常返回时，当前建议进入 Suspended。
    /// </summary>
    public DesktopLayoutSuggestionLifecycleSnapshot MarkObservationRunFailed() =>
        _lifecycle.Suspend(DesktopLayoutSuggestionSuspensionReason.ObservationRunFailed);

    public DesktopLayoutSuggestionLifecycleSnapshot DismissCurrent() =>
        _lifecycle.DismissCurrent();

    public async Task<DesktopLayoutSuggestionPipelineResult> ProcessAsync(
        DesktopObservationDiagnosticsResult diagnostics,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        await _processGate.WaitAsync(ct);
        try
        {
            switch (diagnostics.Status)
            {
                case DesktopObservationDiagnosticsStatus.BaselineEstablished:
                {
                    var observationId = diagnostics.BaselineUpdate!.CurrentObservation.ObservationId;
                    var lifecycle = _lifecycle.MarkBaselineReestablished(observationId);
                    return Result(
                        DesktopLayoutSuggestionPipelineStatus.BaselineReestablished,
                        diagnostics,
                        SnapshotBinding(),
                        null,
                        lifecycle);
                }

                case DesktopObservationDiagnosticsStatus.NoChanges:
                {
                    var lifecycle = _lifecycle.ConfirmNoDesktopChanges();
                    return Result(
                        DesktopLayoutSuggestionPipelineStatus.NoChanges,
                        diagnostics,
                        SnapshotBinding(),
                        null,
                        lifecycle);
                }

                case DesktopObservationDiagnosticsStatus.Unstable:
                {
                    var lifecycle = _lifecycle.Suspend(
                        DesktopLayoutSuggestionSuspensionReason.ObservationUnstable);
                    return Result(
                        DesktopLayoutSuggestionPipelineStatus.ObservationUnstable,
                        diagnostics,
                        SnapshotBinding(),
                        null,
                        lifecycle);
                }

                case DesktopObservationDiagnosticsStatus.Unavailable:
                {
                    var lifecycle = _lifecycle.Suspend(
                        DesktopLayoutSuggestionSuspensionReason.ObservationUnavailable);
                    return Result(
                        DesktopLayoutSuggestionPipelineStatus.ObservationUnavailable,
                        diagnostics,
                        SnapshotBinding(),
                        null,
                        lifecycle);
                }

                case DesktopObservationDiagnosticsStatus.ChangesDetected:
                    return await ProcessChangesAsync(diagnostics, ct);

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(diagnostics),
                        diagnostics.Status,
                        "未知 diagnostics status。");
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<DesktopLayoutSuggestionPipelineResult> ProcessChangesAsync(
        DesktopObservationDiagnosticsResult diagnostics,
        CancellationToken ct)
    {
        var baseline = diagnostics.BaselineUpdate!;
        var currentObservation = baseline.CurrentObservation;
        var changeSet = baseline.ChangeSet!;

        // 只要 fresh stable Diff 已证明事实改变，旧建议先退休。
        _lifecycle.MarkDesktopChanged(currentObservation.ObservationId);

        DesktopLayoutSuggestionBindingSnapshot bindingSnapshot;
        lock (_bindingGate)
            bindingSnapshot = CreateBindingSnapshot();

        if (bindingSnapshot.Binding is null)
        {
            return Result(
                DesktopLayoutSuggestionPipelineStatus.NotConfigured,
                diagnostics,
                bindingSnapshot,
                null,
                _lifecycle.Snapshot);
        }

        DesktopLayoutSuggestionBuildResult buildResult;
        try
        {
            buildResult = await _builder.BuildAsync(
                bindingSnapshot.Binding,
                changeSet,
                currentObservation,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DesktopLayoutSuggestionPipelineResult(
                DesktopLayoutSuggestionPipelineStatus.BuilderFailed,
                diagnostics,
                bindingSnapshot,
                null,
                _lifecycle.Snapshot,
                ex.ToString());
        }

        lock (_bindingGate)
        {
            // 关键 stale-config gate：异步 build 期间 binding 一旦变化，旧结果永不发布。
            if (_bindingRevision != bindingSnapshot.Revision
                || !Equals(_binding, bindingSnapshot.Binding))
            {
                return Result(
                    DesktopLayoutSuggestionPipelineStatus.BindingChangedDuringBuild,
                    diagnostics,
                    CreateBindingSnapshot(),
                    buildResult,
                    _lifecycle.Snapshot);
            }

            if (buildResult.Status == DesktopLayoutSuggestionBuildStatus.Suggested)
            {
                var lifecycle = _lifecycle.Publish(buildResult.Suggestion!);
                return Result(
                    DesktopLayoutSuggestionPipelineStatus.Suggested,
                    diagnostics,
                    CreateBindingSnapshot(),
                    buildResult,
                    lifecycle);
            }

            if (buildResult.Status == DesktopLayoutSuggestionBuildStatus.NoActionNeeded)
            {
                return Result(
                    DesktopLayoutSuggestionPipelineStatus.NoActionNeeded,
                    diagnostics,
                    CreateBindingSnapshot(),
                    buildResult,
                    _lifecycle.Snapshot);
            }

            return Result(
                DesktopLayoutSuggestionPipelineStatus.BuildRejected,
                diagnostics,
                CreateBindingSnapshot(),
                buildResult,
                _lifecycle.Snapshot);
        }
    }

    private DesktopLayoutSuggestionBindingSnapshot SnapshotBinding()
    {
        lock (_bindingGate)
            return CreateBindingSnapshot();
    }

    private DesktopLayoutSuggestionBindingSnapshot CreateBindingSnapshot() =>
        new(_bindingRevision, _binding);

    private static DesktopLayoutSuggestionPipelineResult Result(
        DesktopLayoutSuggestionPipelineStatus status,
        DesktopObservationDiagnosticsResult diagnostics,
        DesktopLayoutSuggestionBindingSnapshot bindingSnapshot,
        DesktopLayoutSuggestionBuildResult? buildResult,
        DesktopLayoutSuggestionLifecycleSnapshot lifecycle) =>
        new(status, diagnostics, bindingSnapshot, buildResult, lifecycle);
}
