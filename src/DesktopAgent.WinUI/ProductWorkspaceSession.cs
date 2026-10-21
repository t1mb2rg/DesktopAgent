using DesktopAgent.Core.DesktopLayouts.Workspaces.Editing;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Workspaces;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WinUI;

/// <summary>由产品窗口持有，生命周期长于 AI View；保存队列不依赖页面是否可见。</summary>
internal sealed class ProductWorkspaceSession
{
    private readonly JsonDesktopWorkspaceSessionStore _store = new(Path.Combine(
        Path.GetDirectoryName(DesktopAgentDataPaths.DefaultDatabasePath)!, "workspace-session.json"));
    private readonly object _saveGate = new();
    private Task _saveQueue = Task.CompletedTask;
    private Task? _initializeTask;
    private bool _canSave;
    private readonly AiDesktopUnderstandingService _understandingService = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task<AiDesktopUnderstandingProductResult>? _understandingTask;
    private CancellationTokenSource? _activeUnderstanding;

    internal DesktopWorkspaceDesignSession Session { get; private set; } = new();
    internal DesktopAgent.Core.DesktopObservations.DesktopObservation? DesignObservation { get; private set; }
    internal AiDesktopUnderstandingProductResult? LatestUnderstanding { get; set; }
    internal DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry.ExplorerWorkspaceGeometry? DesignGeometry { get; private set; }
    internal string? PersistenceMessage { get; private set; }
    internal bool UnderstandingInProgress { get; private set; }
    internal string? UnderstandingMessage { get; private set; }
    internal AiDesktopUnderstandingProductStatus? UnderstandingStatus { get; private set; }
    internal event Action? Changed;
    internal event Action? DesktopEditorRequested;
    internal void RequestDesktopEditor() => DesktopEditorRequested?.Invoke();

    internal async Task RefreshDesignGeometryAsync()
    {
        using var sta = new StaThreadExecutor();
        var source = new WindowsDesktopObservationSource(new NativeDisplayTopologyReader(),
            new DesktopShellIconReader(), new DesktopShellIconPositioningCapabilitiesReader(), sta);
        var capture = await ProductGridCalibration.Protect(source).CaptureAsync();
        if (!capture.IsCaptured) throw new InvalidOperationException("实测网格不可用，请先校准桌面。");
        var reader = new DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry.DesktopExplorerGridFactReader(
            new DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry.SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath));
        var result = DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry.DesktopExplorerWorkspaceGeometryAdapter.Build(
            capture.Observation!, await reader.TryReadActiveAsync(capture.Observation!));
        if (!result.Succeeded) throw new InvalidOperationException("当前显示器网格不可用，请重新校准。");
        DesignObservation = capture.Observation;
        DesignGeometry = result.Geometry;
        Changed?.Invoke();
    }


    internal Task InitializeAsync() => _initializeTask ??= RestoreAsync();

    internal Task<AiDesktopUnderstandingProductResult> EnsureUnderstandingAsync(bool force = false)
    {
        lock (_saveGate)
        {
            if (_understandingTask is { IsCompleted: false }) return _understandingTask;
            return _understandingTask = UnderstandAsync(force);
        }
    }

    private async Task<AiDesktopUnderstandingProductResult> UnderstandAsync(bool force)
    {
        await InitializeAsync();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_saveGate) _activeUnderstanding = requestCancellation;
        UnderstandingInProgress = true;
        UnderstandingStatus = null;
        UnderstandingMessage = "正在检查桌面变化…";
        Changed?.Invoke();
        try
        {
            var settingsStore = new DesktopAgent.Persistence.Productization.JsonDesktopProductSettingsStore(DesktopAgentDataPaths.DefaultSettingsPath);
            await settingsStore.InitializeAsync(requestCancellation.Token);
            if (DesktopAgent.Application.Intelligence.DesktopProductIntelligenceOptionsFactory.Create(
                    await settingsStore.GetAsync(requestCancellation.Token)) is null)
            {
                UnderstandingStatus = AiDesktopUnderstandingProductStatus.AiDisabled;
                UnderstandingMessage = "AI 尚未配置或启用。完成设置后会自动理解桌面，无需先发送消息。";
                return new(UnderstandingStatus.Value, null, 0, null, null, null, UnderstandingMessage);
            }
            using (var sta = new StaThreadExecutor())
            {
                var source = new WindowsDesktopObservationSource(new NativeDisplayTopologyReader(), new DesktopShellIconReader(),
                    new DesktopShellIconPositioningCapabilitiesReader(), sta);
                var capture = await ProductGridCalibration.Protect(source).CaptureAsync(requestCancellation.Token);
                if (!capture.IsCaptured)
                {
                    UnderstandingStatus = AiDesktopUnderstandingProductStatus.DesktopUnavailable;
                    UnderstandingMessage = string.Join("；", capture.Issues.Select(issue => issue.Message));
                    return new(AiDesktopUnderstandingProductStatus.DesktopUnavailable, null, 0, null, null, null, UnderstandingMessage);
                }
                var factReader = new DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry.DesktopExplorerGridFactReader(
                    new DesktopAgent.Persistence.DesktopLayouts.Workspaces.Geometry.SqliteExplorerGridMeasurementStore(DesktopAgentDataPaths.DefaultDatabasePath));
                var geometry = DesktopAgent.Application.DesktopLayouts.Workspaces.Geometry.DesktopExplorerWorkspaceGeometryAdapter.Build(
                    capture.Observation!, await factReader.TryReadActiveAsync(capture.Observation!, requestCancellation.Token));
                DesignObservation = capture.Observation;
                DesignGeometry = geometry.Succeeded ? geometry.Geometry : null;
            }
            if (!force)
            {
                var restored = await _understandingService.RestoreLatestAsync(requestCancellation.Token);
                requestCancellation.Token.ThrowIfCancellationRequested();
                if (restored.IsCurrent && restored.Snapshot is { } snapshot)
                {
                    LatestUnderstanding = new(AiDesktopUnderstandingProductStatus.Success,
                        DesktopSemanticConversationDraftFactory.Create(snapshot.Understanding, Session.Snapshot().State.Intent.Summary),
                        snapshot.SourceItemCount, null, null, null, null, restored.CurrentObservation);
                    UnderstandingMessage = "桌面理解已就绪，沿用已有分类。";
                    UnderstandingStatus = AiDesktopUnderstandingProductStatus.Success;
                    if (Session.Snapshot().Messages.Count == 0)
                        Session.AppendMessage(DesktopWorkspaceMessageRole.Assistant, snapshot.Understanding.DesktopSummary);
                    return LatestUnderstanding;
                }
            }
            UnderstandingMessage = "正在后台理解桌面，已有设计草稿会保留…";
            Changed?.Invoke();
            var result = await _understandingService.AnalyzeAsync(Session.Snapshot().State.Intent.Summary, null, requestCancellation.Token);
            UnderstandingStatus = result.Status;
            requestCancellation.Token.ThrowIfCancellationRequested();
            if (result.Succeeded)
            {
                LatestUnderstanding = result;
                Session.InvalidateExternalContext();
                Session.AppendMessage(DesktopWorkspaceMessageRole.Assistant, result.Draft!.DesktopSummary);
                UnderstandingMessage = result.PersistenceWarning ?? "桌面理解已就绪。";
            }
            else UnderstandingMessage = result.Message ?? "桌面理解暂未完成，请检查 AI 配置后重试。";
            return result;
        }
        catch (Exception ex)
        {
            UnderstandingStatus = AiDesktopUnderstandingProductStatus.ProviderFailure;
            UnderstandingMessage = ex is OperationCanceledException ? "桌面理解已停止。" : $"桌面理解失败：{ex.Message}";
            return new(AiDesktopUnderstandingProductStatus.ProviderFailure, null, 0, null, null, null, UnderstandingMessage);
        }
        finally
        {
            lock (_saveGate)
            {
                if (ReferenceEquals(_activeUnderstanding, requestCancellation)) _activeUnderstanding = null;
                requestCancellation.Dispose();
            }
            UnderstandingInProgress = false;
            Changed?.Invoke();
        }
    }

    internal void StopBackgroundWork() => _shutdown.Cancel();

    internal void PauseUnderstanding()
    {
        DesignObservation = null;
        DesignGeometry = null;
        lock (_saveGate) _activeUnderstanding?.Cancel();
    }

    private async Task RestoreAsync()
    {
        try
        {
            var snapshot = await _store.LoadAsync();
            if (snapshot is not null) Session = DesktopWorkspaceDesignSession.Restore(snapshot);
            _canSave = true;
        }
        catch (Exception ex)
        {
            PersistenceMessage = $"无法恢复设计会话，原记录已保留：{ex.Message}";
        }
        Session.Changed += OnSessionChanged;
        Changed?.Invoke();
    }

    private void OnSessionChanged()
    {
        var snapshot = Session.Snapshot();
        if (_canSave)
        {
            lock (_saveGate)
                _saveQueue = SaveAfterAsync(_saveQueue, snapshot);
        }
        Changed?.Invoke();
    }

    private async Task SaveAfterAsync(Task preceding, DesktopWorkspaceSessionSnapshot snapshot)
    {
        await preceding.ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(snapshot).ConfigureAwait(false);
            PersistenceMessage = null;
        }
        catch (Exception ex) { PersistenceMessage = $"设计仍在内存中，但保存失败：{ex.Message}"; }
        Changed?.Invoke();
    }

    internal async Task FlushAsync()
    {
        Task<AiDesktopUnderstandingProductResult>? understanding;
        lock (_saveGate) understanding = _shutdown.IsCancellationRequested ? _understandingTask : null;
        if (understanding is not null) await understanding;
        while (true)
        {
            Task queue;
            lock (_saveGate) queue = _saveQueue;
            await queue;
            lock (_saveGate) if (ReferenceEquals(queue, _saveQueue)) return;
        }
    }
}
