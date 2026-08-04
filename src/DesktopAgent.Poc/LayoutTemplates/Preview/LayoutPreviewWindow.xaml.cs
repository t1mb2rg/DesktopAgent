using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.Displays;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Poc.LayoutTemplates.Preview;

public partial class LayoutPreviewWindow : Window
{
    private readonly IDesktopLayoutTemplateStore _templateStore;
    private readonly IDesktopLayoutIconRuleStore _ruleStore;
    private readonly IDisplayTopologyReader _displayTopologyReader;
    private readonly IDesktopIconReader _desktopIconReader;
    private readonly IDesktopIconPositionWriter _positioningCapabilitiesProvider;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly Func<TargetDisplayLayoutRestorePlan, Task<TargetDisplayLayoutExecutionRunResult>>? _executionCallback;

    // M2-03B-B 仅使用 GetCapabilities 的只读能力查询；本窗口永远不能调用 MoveDesktopIcon。
    private readonly ObservableCollection<LayoutPreviewTemplateRow> _templateRows = [];
    private readonly ObservableCollection<LayoutPreviewDisplayRow> _displayRows = [];
    private readonly ObservableCollection<LayoutPreviewItemRow> _itemRows = [];
    private readonly ObservableCollection<LayoutPreviewOutOfScopeRow> _oosRows = [];

    private bool _isBusy;
    private bool _storeReady;
    private bool _isClosing;
    private bool _suppressTemplateSelectionChanged;
    private CancellationTokenSource? _windowCts;
    private DesktopLayoutTemplate? _loadedTemplate;
    private DesktopLayoutIconRuleSet? _loadedRuleSet;
    private IReadOnlyList<DisplayDescriptor> _currentDisplays = Array.Empty<DisplayDescriptor>();
    private DesktopLayoutRuleAwarePreviewPlan? _ruleAwarePreviewPlan;
    private LayoutPreviewScene? _scene;
    private long _generation;
    private string? _selectedTargetDeviceName;

    // M2-04B 执行候选与结果
    private DesktopLayoutRuleAwareExecutionCandidateBuildResult? _candidateBuildResult;
    private DesktopLayoutRuleAwareExecutionCandidate? _activeCandidate;
    private TargetDisplayLayoutExecutionRunResult? _lastExecutionResult;
    private bool _requiresManualRecovery;
    private string? _escapedException;

    public bool RequiresManualRecovery => _requiresManualRecovery;
    public TargetDisplayLayoutExecutionRunResult? LastExecutionResult => _lastExecutionResult;
    public string? EscapedException => _escapedException;

    public LayoutPreviewWindow(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        IDesktopIconPositionWriter positioningCapabilitiesProvider,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        Func<TargetDisplayLayoutRestorePlan, Task<TargetDisplayLayoutExecutionRunResult>>? executionCallback = null)
    {
        InitializeComponent();

        _templateStore = templateStore;
        _ruleStore = ruleStore;
        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _positioningCapabilitiesProvider = positioningCapabilitiesProvider;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;
        _executionCallback = executionCallback;

        TemplateCombo.ItemsSource = _templateRows;
        DisplayCombo.ItemsSource = _displayRows;
        ItemsGrid.ItemsSource = _itemRows;
        OutOfScopeGrid.ItemsSource = _oosRows;

        _windowCts = new CancellationTokenSource();
        Loaded += OnLoaded;
        Closed += (_, _) => { _windowCts?.Dispose(); _windowCts = null; };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isBusy = true;
        UpdateButtons();
        try
        {
            await _templateStore.InitializeAsync(_windowCts?.Token ?? default);
            if (_isClosing) return;
            await _ruleStore.InitializeAsync(_windowCts?.Token ?? default);
            if (_isClosing) return;
            _storeReady = true;
            await RefreshTemplatesAsync();
            if (_isClosing) return;
            ReadDisplays();
            if (!_isClosing)
                StatusText.Text = "就绪。请选择模板并点击 生成预览。";
        }
        catch (OperationCanceledException) { /* 静默返回，窗口正在关闭 */ }
        catch (Exception ex)
        {
            _storeReady = false;
            if (!_isClosing)
                StatusText.Text = $"初始化失败：{ex.Message}。可点击 刷新数据 重试。";
        }
        finally { _isBusy = false; if (!_isClosing) UpdateButtons(); }
    }

    // ═══ 生成版本 ═══

    private long BumpGeneration() => Interlocked.Increment(ref _generation);

    private void InvalidatePreview()
    {
        BumpGeneration();
        _ruleAwarePreviewPlan = null;
        _scene = null;
        PreviewCanvas.Children.Clear();
        _itemRows.Clear();
        _oosRows.Clear();
        SummaryText.Text = "无有效预览。";
        DiagnosticsText.Text = "—";
        InvalidateExecutionState();
    }

    private void InvalidateExecutionState()
    {
        _candidateBuildResult = null;
        _activeCandidate = null;
        _lastExecutionResult = null;
        CandidateGroup.Visibility = Visibility.Collapsed;
        CandidateSummaryText.Text = "";
        CandidateIssuesText.Text = "";
        StagingWarningText.Text = "";
        ExecutionConsentCheckBox.IsChecked = false;
        ExecutionResultGroup.Visibility = Visibility.Collapsed;
        ExecutionResultText.Text = "";
        ExecutionVerificationText.Text = "";
        ManualRecoveryWarning.Visibility = Visibility.Collapsed;
    }

    // ═══ 模板 ═══

    private async Task RefreshTemplatesAsync()
    {
        if (!_storeReady) return;

        // 清空前捕获选择 — _templateRows.Clear() 会同步触发
        // TemplateCombo_SelectionChanged 将 _loadedTemplate 置 null
        var previousTemplateId = _loadedTemplate?.TemplateId;

        var list = await _templateStore.ListAsync(200, _windowCts?.Token ?? default);
        if (_isClosing) return;

        _suppressTemplateSelectionChanged = true;
        try
        {
            _templateRows.Clear();
            foreach (var s in list) _templateRows.Add(new LayoutPreviewTemplateRow(s));

            // 恢复选择
            if (previousTemplateId.HasValue)
            {
                SelectTemplateInCombo(previousTemplateId.Value);
                // 如果找不到原模板 ID，SelectTemplateInCombo 会清除选择
                if (TemplateCombo.SelectedItem is null)
                {
                    _loadedTemplate = null;
                    InvalidatePreview();
                }
            }
            else
            {
                TemplateCombo.SelectedItem = null;
                _loadedTemplate = null;
                InvalidatePreview();
            }
        }
        finally { _suppressTemplateSelectionChanged = false; }

        // 恢复后显式加载一次完整模板（不在 suppress 范围内）
        if (previousTemplateId.HasValue && TemplateCombo.SelectedItem is not null)
        {
            var gen = BumpGeneration();
            await LoadTemplateAsync(previousTemplateId.Value, gen, _windowCts?.Token ?? default);
        }
    }

    private void SelectTemplateInCombo(Guid templateId)
    {
        foreach (var r in _templateRows)
        {
            if (r.Summary.TemplateId == templateId)
            {
                TemplateCombo.SelectedItem = r;
                return;
            }
        }
        TemplateCombo.SelectedItem = null;
        _loadedTemplate = null;
    }

    /// <summary>加载完整模板并更新 UI 状态。可用于 SelectionChanged 和刷新恢复。</summary>
    private async Task LoadTemplateAsync(Guid templateId, long gen, CancellationToken ct)
    {
        // 读入局部变量
        var template = await _templateStore.GetAsync(templateId, ct);
        if (_isClosing || Interlocked.Read(ref _generation) != gen) return;
        if (template is null)
        {
            _loadedTemplate = null; _loadedRuleSet = null; InvalidatePreview();
            if (!_isClosing) StatusText.Text = $"模板 {templateId:D} 不存在。";
            return;
        }

        DesktopLayoutIconRuleSet? ruleSet;
        try { ruleSet = await _ruleStore.GetAsync(templateId, ct); }
        catch
        {
            _loadedTemplate = null; _loadedRuleSet = null; InvalidatePreview();
            if (!_isClosing) StatusText.Text = "加载规则失败。";
            return;
        }
        if (_isClosing || Interlocked.Read(ref _generation) != gen) return;

        if (ruleSet is null || ruleSet.TemplateId != template.TemplateId)
        {
            _loadedTemplate = null; _loadedRuleSet = null; InvalidatePreview();
            if (!_isClosing) StatusText.Text = $"模板 {templateId:D} 的规则集不存在或 TemplateId 不一致。";
            return;
        }

        // 全部成功 → 一次性赋值
        _loadedTemplate = template;
        _loadedRuleSet = ruleSet;

        if (!_isClosing)
        {
            UpdateDisplayComboState();
            StatusText.Text = $"已加载模板：{template.Name} (v{template.Version})，" +
                $"{ruleSet.RuleCount} 条规则。点击 生成预览。";
        }
    }

    private async void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateSelectionChanged) return;
        if (_isBusy) return;

        InvalidatePreview();
        if (TemplateCombo.SelectedItem is not LayoutPreviewTemplateRow row)
        {
            _loadedTemplate = null;
            UpdateDisplayComboState();
            return;
        }

        var gen = BumpGeneration();
        _isBusy = true;
        UpdateButtons();
        try
        {
            await LoadTemplateAsync(row.Summary.TemplateId, gen, _windowCts?.Token ?? default);
        }
        catch (OperationCanceledException) { /* 静默返回 */ }
        catch (Exception ex)
        {
            if (!_isClosing)
                StatusText.Text = $"加载模板失败：{ex.Message}";
            _loadedTemplate = null;
        }
        finally { _isBusy = false; if (!_isClosing) UpdateButtons(); }
    }

    // ═══ 显示器 ═══

    private void ReadDisplays()
    {
        try
        {
            _currentDisplays = _displayTopologyReader.GetDisplays();
            _displayRows.Clear();
            foreach (var d in _currentDisplays)
                _displayRows.Add(new LayoutPreviewDisplayRow(d));
            UpdateDisplayComboState();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取显示器失败：{ex.Message}";
        }
    }

    private void UpdateDisplayComboState()
    {
        if (_loadedTemplate is null)
        {
            DisplayCombo.IsEnabled = false;
            _selectedTargetDeviceName = null;
            return;
        }

        var binding = _loadedTemplate.TargetDisplayBinding;
        switch (binding.Kind)
        {
            case TargetDisplayBindingKind.SelectedTarget:
                DisplayCombo.IsEnabled = true;
                break;
            case TargetDisplayBindingKind.PrimaryDisplay:
                DisplayCombo.IsEnabled = false;
                _selectedTargetDeviceName = null;
                StatusText.Text = "模板绑定为 PrimaryDisplay，将解析到主显示器。";
                break;
            case TargetDisplayBindingKind.ExactDeviceName:
                DisplayCombo.IsEnabled = false;
                _selectedTargetDeviceName = null;
                StatusText.Text = $"模板绑定为 ExactDeviceName: \"{binding.DeviceName}\"。";
                break;
        }
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidatePreview();
        if (DisplayCombo.SelectedItem is LayoutPreviewDisplayRow dr)
            _selectedTargetDeviceName = dr.DeviceName;
        else
            _selectedTargetDeviceName = null;
    }

    // ═══ 生成预览 ═══

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        // 冻结 DisplayCombo 的当前选择，不与旧 _loadedTemplate 混淆
        var templateRow = TemplateCombo.SelectedItem as LayoutPreviewTemplateRow;
        if (templateRow is null) { StatusText.Text = "请先选择模板。"; return; }
        var selectedTemplateId = templateRow.Summary.TemplateId;
        var capturedDeviceName = DisplayCombo.SelectedItem is LayoutPreviewDisplayRow dr
            ? dr.DeviceName : null;

        InvalidatePreview();
        var gen = BumpGeneration();
        var ct = _windowCts?.Token ?? default;
        _isBusy = true;
        UpdateButtons();
        StatusText.Text = "正在读取…";

        try
        {
            // 1. Fresh re-read 完整模板和规则集（相同 generation）
            var template = await _templateStore.GetAsync(selectedTemplateId, ct);
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;
            if (template is null)
            {
                if (!_isClosing)
                    StatusText.Text = $"模板 {selectedTemplateId:D} 不存在。";
                return;
            }

            var ruleSet = await _ruleStore.GetAsync(selectedTemplateId, ct);
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;
            if (ruleSet is null || ruleSet.TemplateId != template.TemplateId)
            {
                if (!_isClosing)
                    StatusText.Text = $"模板 {selectedTemplateId:D} 的规则集不存在或 TemplateId 不一致。";
                return;
            }

            // 依据 fresh template 的绑定决定 selectedTarget（不用旧 _loadedTemplate）
            var localSelectedTarget = template.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
                ? capturedDeviceName : null;

            // 2. 读取显示器拓扑
            var displays1 = _displayTopologyReader.GetDisplays();
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 3. 解析目标显示器
            DisplayDescriptor targetDisplay;
            try
            {
                targetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
                    template.TargetDisplayBinding, displays1, localSelectedTarget);
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    StatusText.Text = $"目标显示器解析失败：{ex.Message}";
                return;
            }
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 4. STA 线程读取 — 内层异常由外层统一处理，不外泄
            var shellRead = await _staExecutor.RunAsync(() =>
            {
                var capabilities = _positioningCapabilitiesProvider.GetCapabilities();
                var iconRead = _desktopIconReader.ReadDesktopIcons();
                return new LayoutPreviewShellRead(capabilities, iconRead);
            });
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 5. 再次读取拓扑，检查一致性
            var displays2 = _displayTopologyReader.GetDisplays();
            if (!TopologyConsistent(displays1, displays2))
            {
                if (!_isClosing)
                    StatusText.Text = "读取期间显示器拓扑发生变化，请重试。";
                return;
            }
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            if (shellRead.IconRead.FailedItemCount > 0)
            {
                if (!_isClosing)
                    StatusText.Text = $"图标读取不完整：{shellRead.IconRead.FailedItemCount} 个失败。无法生成预览。";
                return;
            }

            // 6. 分配 MonitorDeviceName
            var assignedIcons = shellRead.IconRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays2);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();

            // 7. 创建快照 + 调用规则感知 PreviewPlanner
            var snapshot = _snapshotFactory.Create(
                assignedIcons, targetDisplay.DeviceName, shellRead.IconRead.ConnectionMethod);

            var plan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
                template, ruleSet, displays2, localSelectedTarget,
                shellRead.Capabilities.Spacing, snapshot.Icons);

            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 8. 创建 Transform 和 Scene
            var transform = new LayoutPreviewCanvasTransform(
                targetDisplay.WorkArea,
                PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : 800,
                PreviewCanvas.ActualHeight > 0 ? PreviewCanvas.ActualHeight : 600,
                20);

            _ruleAwarePreviewPlan = plan;
            _scene = LayoutPreviewSceneBuilder.Build(plan, transform);

            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 9. 更新 UI
            RenderScene();
            _itemRows.Clear();
            foreach (var r in _scene.ItemRows) _itemRows.Add(r);
            _oosRows.Clear();
            foreach (var r in _scene.OutOfScopeRows) _oosRows.Add(r);

            SummaryText.Text = FormatRuleAwareSummary(plan, shellRead);
            DiagnosticsText.Text = FormatRuleAwareDiagnostics(shellRead, ruleSet);
            StatusText.Text = plan.IsComplete
                ? (plan.HasChanges ? "预览完成：有图标需要移动。" : "预览完成：所有图标已就位。")
                : "预览完成：容量不足，部分图标未分配。";
        }
        catch (OperationCanceledException) { /* 静默返回，窗口正在关闭 */ }
        catch (Exception ex)
        {
            if (!_isClosing)
                StatusText.Text = $"生成预览失败：{ex.Message}";
        }
        finally { _isBusy = false; if (!_isClosing) UpdateButtons(); }
    }

    // ═══ 执行候选生成与执行 ═══

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_requiresManualRecovery)
        {
            StatusText.Text = "[M2-04B] 需要人工处理，执行已锁定。请关闭本窗口并查看主窗口诊断。";
            return;
        }
        if (_executionCallback is null)
        {
            StatusText.Text = "[M2-04B] 此预览窗口未连接执行引擎。";
            return;
        }
        if (_ruleAwarePreviewPlan is null)
        {
            StatusText.Text = "[M2-04B] 没有有效预览，请先生成预览。";
            return;
        }
        if (ExecutionConsentCheckBox.IsChecked != true)
        {
            StatusText.Text = "[M2-04B] 请先阅读风险并勾选确认框。";
            return;
        }

        // 清除上次执行结果
        _lastExecutionResult = null;
        ExecutionResultGroup.Visibility = Visibility.Collapsed;
        ManualRecoveryWarning.Visibility = Visibility.Collapsed;

        _isBusy = true;
        UpdateButtons();
        StatusText.Text = "[M2-04B] 正在重新读取桌面并生成执行候选…";

        try
        {
            // 1. 重新读取显示器拓扑
            var displays1 = _displayTopologyReader.GetDisplays();
            if (_isClosing) return;

            // 2. 解析目标显示器
            DisplayDescriptor targetDisplay;
            try
            {
                targetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
                    _ruleAwarePreviewPlan.Template.TargetDisplayBinding,
                    displays1,
                    _selectedTargetDeviceName);
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    StatusText.Text = $"[M2-04B] 目标显示器解析失败：{ex.Message}";
                return;
            }

            // 3. STA 线程读取桌面
            var shellRead = await _staExecutor.RunAsync(() =>
            {
                var capabilities = _positioningCapabilitiesProvider.GetCapabilities();
                var iconRead = _desktopIconReader.ReadDesktopIcons();
                return new LayoutPreviewShellRead(capabilities, iconRead);
            });
            if (_isClosing) return;

            // 4. 再次读取拓扑，检查一致性
            var displays2 = _displayTopologyReader.GetDisplays();
            if (!TopologyConsistent(displays1, displays2))
            {
                if (!_isClosing)
                    StatusText.Text = "[M2-04B] 读取期间显示器拓扑发生变化，请重试。";
                return;
            }

            // 5. 分配显示器归属
            var assignedIcons = shellRead.IconRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays2);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            var currentRead = new DesktopIconReadResult(
                assignedIcons, shellRead.IconRead.FailedItemCount,
                shellRead.IconRead.ConnectionMethod, shellRead.IconRead.ReadTime,
                shellRead.IconRead.PrimaryPathException);

            // 6. 构建 Candidate
            var buildResult = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
                _ruleAwarePreviewPlan, currentRead, displays2,
                shellRead.Capabilities, _snapshotFactory);

            _candidateBuildResult = buildResult;

            if (!buildResult.IsSuccess)
            {
                // 展示拒绝原因
                DisplayCandidateRejection(buildResult);
                StatusText.Text = "[M2-04B] 执行候选被拒绝。桌面状态在预览后已变化。请重新生成预览。";
                return;
            }

            var candidate = buildResult.Candidate!;
            _activeCandidate = candidate;

            // 7. 展示候选摘要与风险
            DisplayCandidate(candidate);

            // 8. 二次确认
            var stepsDetail = new System.Text.StringBuilder();
            foreach (var step in candidate.ExecutionPlan.Steps)
            {
                stepsDetail.AppendLine(
                    $"  [{step.StepIndex}] {step.DisplayName} ({step.StableId}) " +
                    $"({step.FromPosition.X},{step.FromPosition.Y}) → " +
                    $"({step.ToPosition.X},{step.ToPosition.Y}) " +
                    $"显示器={step.ExpectedMonitorDeviceName}");
            }

            var confirmMsg =
                $"确认执行批量桌面图标移动？\n\n" +
                $"模板: {candidate.SourcePreviewPlan.Template.Name} v{candidate.SourcePreviewPlan.Template.Version}\n" +
                $"目标显示器: {candidate.TargetDeviceName}\n" +
                $"需移动图标: {candidate.MoveRequiredCount} 个\n" +
                $"执行步骤: {candidate.ExecutionStepCount} 步\n" +
                $"条件: 无中转直接执行\n\n" +
                $"执行步骤:\n{stepsDetail}\n" +
                $"Coordinator 将再次读取桌面、重新规划、通过安全闸门、保存安全快照后执行。\n" +
                $"该操作不可撤销。";

            var dlgResult = MessageBox.Show(confirmMsg, "二次确认 — 批量桌面图标执行",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (dlgResult != MessageBoxResult.Yes)
            {
                StatusText.Text += "\n用户取消执行。";
                return;
            }

            // 9. 执行（通过回调 → MainWindow → Coordinator）
            StatusText.Text = "[M2-04B] 正在通过 Coordinator 执行…";
            TargetDisplayLayoutExecutionRunResult runResult;
            try
            {
                runResult = await _executionCallback(candidate.RestorePlan);
            }
            catch (Exception ex)
            {
                // 逃逸异常 — 无法确定是否已发生写入
                _requiresManualRecovery = true;
                _escapedException = $"{ex.GetType().Name}: {ex.Message}";
                _lastExecutionResult = null;

                ExecutionResultGroup.Visibility = Visibility.Visible;
                ExecutionResultText.Text = $"执行逃逸异常：{_escapedException}\n\n" +
                    "无法确定 Writer 是否已被调用。已进入人工处理锁。";
                ManualRecoveryWarning.Visibility = Visibility.Visible;
                StatusText.Text = $"[M2-04B] 执行逃逸异常：{_escapedException}。需要人工处理。";
                UpdateButtons();
                return;
            }

            _lastExecutionResult = runResult;

            // 10. 显示结果
            DisplayExecutionResult(runResult);

            // 11. 状态处理
            switch (runResult.Status)
            {
                case TargetDisplayLayoutExecutionRunStatus.Completed:
                    StatusText.Text = "[M2-04B] 执行成功。";
                    break;
                case TargetDisplayLayoutExecutionRunStatus.CompletedNoOp:
                    StatusText.Text = "[M2-04B] NoOp — 无需移动。";
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite:
                    StatusText.Text = "[M2-04B] Writer 未启动，无副作用。请重新生成预览。";
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack:
                    StatusText.Text = "[M2-04B] 正向失败但已确认回滚，无残留副作用。";
                    break;
                case TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete:
                case TargetDisplayLayoutExecutionRunStatus.PositionUncertain:
                    _requiresManualRecovery = true;
                    StatusText.Text = "[M2-04B] ⚠ 需要人工处理。请关闭本窗口并查看主窗口诊断。";
                    break;
            }
        }
        catch (OperationCanceledException) { /* 静默返回 */ }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                // 此 catch 只捕获第 1–8 步（Candidate 构建、确认对话框等）及第 10–11 步（结果展示）
                // 中的异常。第 9 步（回调/Writer）的逃逸异常已在内部 catch 中处理并设置人工处理锁。
                // 此处不设人工处理锁，因为 Writer 未在此路径中启动。
                StatusText.Text = $"[M2-04B] 阶段异常（Writer 未启动或已完成）：{ex.Message}。";
            }
        }
        finally
        {
            _isBusy = false;
            if (!_isClosing) UpdateButtons();
        }
    }

    private void DisplayCandidate(DesktopLayoutRuleAwareExecutionCandidate candidate)
    {
        CandidateGroup.Visibility = Visibility.Visible;
        ExecutionResultGroup.Visibility = Visibility.Collapsed;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"需移动图标: {candidate.MoveRequiredCount}");
        sb.AppendLine($"执行步骤: {candidate.ExecutionStepCount} (全部 MoveToTarget)");
        sb.AppendLine($"已就位: {candidate.AlreadyAtTargetCount}");
        sb.AppendLine($"固定/排除: {candidate.FixedCount}");
        sb.AppendLine($"恢复受阻: {candidate.RestoreBlockedCount}");
        sb.AppendLine($"恢复缺失: {candidate.RestoreMissingCount}");
        sb.AppendLine($"目标显示器: {candidate.TargetDeviceName}");
        sb.AppendLine($"IsNoOp: {candidate.IsNoOp}");
        sb.AppendLine($"IsExecutable: {candidate.IsExecutable}");

        CandidateSummaryText.Text = sb.ToString();
        CandidateIssuesText.Text = "";
        StagingWarningText.Text = candidate.UsesTemporaryStaging
            ? "⚠ 该计划需要临时中转，不允许批量执行。请重新生成预览。"
            : "";
    }

    private void DisplayCandidateRejection(
        DesktopLayoutRuleAwareExecutionCandidateBuildResult buildResult)
    {
        CandidateGroup.Visibility = Visibility.Visible;
        ExecutionResultGroup.Visibility = Visibility.Collapsed;

        CandidateSummaryText.Text = "执行候选生成被拒绝。";
        var issues = new System.Text.StringBuilder();
        foreach (var issue in buildResult.Issues)
        {
            issues.AppendLine($"  [{issue.Code}] {issue.Message}");
        }
        CandidateIssuesText.Text = issues.ToString();
        StagingWarningText.Text = "";
        ExecutionConsentCheckBox.IsChecked = false;
    }

    private void DisplayExecutionResult(TargetDisplayLayoutExecutionRunResult runResult)
    {
        ExecutionResultGroup.Visibility = Visibility.Visible;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Status: {runResult.Status}");
        sb.AppendLine($"SafetySnapshotId: {runResult.SafetySnapshotId}");
        sb.AppendLine($"SessionId: {runResult.Session?.SessionId}");
        sb.AppendLine($"FailedStepIndex: {runResult.FailedStepIndex}");
        sb.AppendLine($"RequiresManualRecovery: {runResult.RequiresManualRecovery}");
        sb.AppendLine($"IsPositionUncertain: {runResult.IsPositionUncertain}");
        sb.AppendLine($"ForwardAttempts: {runResult.ForwardAttempts.Count}");
        sb.AppendLine($"RollbackAttempts: {runResult.RollbackAttempts.Count}");

        if (runResult.ForwardFailureType is not null)
            sb.AppendLine($"ForwardFailure: {runResult.ForwardFailureType}: {runResult.ForwardFailureMessage}");
        if (runResult.RecoveryFailureType is not null)
            sb.AppendLine($"RecoveryFailure: {runResult.RecoveryFailureType}: {runResult.RecoveryFailureMessage}");

        ExecutionResultText.Text = sb.ToString();
        ExecutionVerificationText.Text = ""; // verification done in MainWindow

        ManualRecoveryWarning.Visibility = runResult.RequiresManualRecovery || runResult.IsPositionUncertain
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExecutionConsent_Changed(object sender, RoutedEventArgs e)
    {
        UpdateButtons();
    }

    // ═══ 画布渲染 ═══

    private void RenderScene()
    {
        PreviewCanvas.Children.Clear();
        if (_scene is null || _ruleAwarePreviewPlan is null) return;

        var t = _scene.Transform;

        // WorkArea 外框
        var (waLeft, waTop, waW, waH) = t.Map(_ruleAwarePreviewPlan.TargetDisplay.WorkArea);
        var waRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            StrokeThickness = 1, Fill = Brushes.Transparent,
            Width = waW, Height = waH
        };
        Canvas.SetLeft(waRect, waLeft);
        Canvas.SetTop(waRect, waTop);
        PreviewCanvas.Children.Add(waRect);

        // Zone
        foreach (var zv in _scene.Zones)
        {
            var zRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x78, 0xD4)),
                Width = zv.CanvasWidth, Height = zv.CanvasHeight
            };
            Canvas.SetLeft(zRect, zv.CanvasLeft);
            Canvas.SetTop(zRect, zv.CanvasTop);
            PreviewCanvas.Children.Add(zRect);

            var zLabel = new TextBlock
            {
                Text = $"{zv.Name}  ({zv.AssignedCount}/{zv.EffectiveCapacity})",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFA, 0xFA, 0xFA))
            };
            Canvas.SetLeft(zLabel, zv.CanvasLeft + 2);
            Canvas.SetTop(zLabel, zv.CanvasTop + 2);
            PreviewCanvas.Children.Add(zLabel);
        }

        // 图标
        foreach (var iv in _scene.Icons)
        {
            var curDot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                Stroke = Brushes.White, StrokeThickness = 1,
                ToolTip = iv.ToolTip
            };
            Canvas.SetLeft(curDot, iv.CurrentCanvasX - 4);
            Canvas.SetTop(curDot, iv.CurrentCanvasY - 4);
            PreviewCanvas.Children.Add(curDot);

            if (iv.State == DesktopLayoutPreviewItemState.MoveRequired
                && iv.TargetCanvasX.HasValue)
            {
                var tx = iv.TargetCanvasX.Value;
                var ty = iv.TargetCanvasY!.Value;
                var tgtDot = new Rectangle
                {
                    Width = 8, Height = 8,
                    Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)),
                    Stroke = Brushes.White, StrokeThickness = 1
                };
                Canvas.SetLeft(tgtDot, tx - 4);
                Canvas.SetTop(tgtDot, ty - 4);
                PreviewCanvas.Children.Add(tgtDot);

                var line = new Line
                {
                    X1 = iv.CurrentCanvasX, Y1 = iv.CurrentCanvasY,
                    X2 = tx, Y2 = ty,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                    StrokeThickness = 1.5
                };
                PreviewCanvas.Children.Add(line);
            }
        }
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ruleAwarePreviewPlan is null || _scene is null) return;
        if (PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;

        var transform = new LayoutPreviewCanvasTransform(
            _ruleAwarePreviewPlan.TargetDisplay.WorkArea,
            PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight, 20);
        _scene = LayoutPreviewSceneBuilder.Build(_ruleAwarePreviewPlan, transform);
        RenderScene();
    }

    // ═══ 按钮 ═══

    private void UpdateButtons()
    {
        if (_isClosing) return;
        GenerateButton.IsEnabled = !_isBusy && _storeReady && _loadedTemplate is not null;
        RefreshButton.IsEnabled = !_isBusy;
        TemplateCombo.IsEnabled = !_isBusy;
        DisplayCombo.IsEnabled = !_isBusy && _loadedTemplate?.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget;

        var canExecute = !_isBusy && _storeReady && _ruleAwarePreviewPlan is not null
            && _ruleAwarePreviewPlan.HasChanges && _ruleAwarePreviewPlan.IsComplete
            && _executionCallback is not null
            && !_requiresManualRecovery;
        ExecuteButton.IsEnabled = canExecute;
        if (_requiresManualRecovery)
            ExecuteButton.ToolTip = "需要人工处理，执行已锁定。";
        else if (_executionCallback is null)
            ExecuteButton.ToolTip = "此预览窗口未连接执行引擎。";
        else
            ExecuteButton.ToolTip = "基于当前预览生成执行候选、展示风险、二次确认后通过 Coordinator 批量执行。";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        InvalidatePreview();
        ReadDisplays();
        BumpGeneration();
        _isBusy = true;
        UpdateButtons();
        try
        {
            if (!_storeReady)
            {
                await _templateStore.InitializeAsync(_windowCts?.Token ?? default);
                if (_isClosing) return;
                _storeReady = true;
            }
            await RefreshTemplatesAsync();
            if (!_isClosing)
                StatusText.Text = "已刷新。";
        }
        catch (OperationCanceledException) { /* 静默返回 */ }
        catch (Exception ex)
        {
            if (!_isClosing)
                StatusText.Text = $"刷新失败：{ex.Message}";
        }
        finally { _isBusy = false; if (!_isClosing) UpdateButtons(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 关闭请求一律允许完成：标记 closing、增 generation、取消 CTS，
        // 不设置 e.Cancel。所有 await 结果必须检查 _isClosing 后丢弃。
        _isClosing = true;
        BumpGeneration();
        _windowCts?.Cancel();
        // STA read 无法中断时允许后台结束，但结果不能回写。
    }

    // ═══ 格式化 ═══

    private static string FormatSummary(DesktopLayoutPreviewPlan plan, LayoutPreviewShellRead read)
    {
        var auto = read.Capabilities.IsAutoArrangeEnabled
            ? "\n⚠ 自动排列已启用！本预览仅用于查看，执行时必须拒绝自动排列下的写入。\n" : "";
        var wa = plan.TargetDisplay.WorkArea;
        var ratio = (double)wa.Width / wa.Height;
        var ratioText = ratio switch
        {
            >= 1.77 and < 1.78 => "16:9",
            >= 1.59 and < 1.61 => "16:10",
            >= 1.32 and < 1.34 => "4:3",
            _ => $"{ratio:F2}:1"
        };
        return $"模板: {plan.Template.Name} v{plan.Template.Version}\n" +
            $"目标显示器: {plan.TargetDisplay.DeviceName}\n" +
            $"WorkArea: ({wa.Left},{wa.Top}) {wa.Width}x{wa.Height}  纵横比: {ratioText}\n" +
            $"间距: {read.Capabilities.Spacing.X}x{read.Capabilities.Spacing.Y}\n" +
            $"图标总数: {plan.InputIconCount}  |  目标: {plan.TargetDisplayIconCount}  |  OutOfScope: {plan.OutOfScopeIconCount}\n" +
            $"已分配: {plan.AssignedCount}  |  已就位: {plan.AlreadyAtTargetCount}  |  需移动: {plan.MoveRequiredCount}  |  未分配: {plan.UnassignedCount}\n" +
            $"IsComplete: {plan.IsComplete}  HasChanges: {plan.HasChanges}  IsNoOp: {plan.IsNoOp}\n" +
            $"图标读取耗时: {read.IconRead.ReadTime.TotalSeconds:F2}s  ConnectionMethod: {read.IconRead.ConnectionMethod}{auto}";
    }

    private static string FormatDiagnostics(LayoutPreviewShellRead read)
    {
        var auto = read.Capabilities.IsAutoArrangeEnabled
            ? "⚠ 自动排列已启用（只读预览可继续）\n" : "自动排列: 关闭\n";
        return $"Capabilities.间距: {read.Capabilities.Spacing.X}x{read.Capabilities.Spacing.Y}\n" +
            $"{auto}" +
            $"Capabilities.ConnectionMethod: {read.Capabilities.ConnectionMethod}\n" +
            $"Capabilities.PrimaryPathException: {read.Capabilities.PrimaryPathException?.Message ?? "无"}\n" +
            $"IconRead.ConnectionMethod: {read.IconRead.ConnectionMethod}\n" +
            $"IconRead.PrimaryPathException: {read.IconRead.PrimaryPathException?.Message ?? "无"}";
    }

    private static string FormatRuleAwareSummary(
        DesktopLayoutRuleAwarePreviewPlan plan, LayoutPreviewShellRead read)
    {
        var wa = plan.TargetDisplay.WorkArea;
        return $"模板: {plan.Template.Name} v{plan.Template.Version}\n" +
            $"目标显示器: {plan.TargetDisplay.DeviceName}\n" +
            $"WorkArea: ({wa.Left},{wa.Top}) {wa.Width}x{wa.Height}\n" +
            $"间距: {read.Capabilities.Spacing.X}x{read.Capabilities.Spacing.Y}\n" +
            $"图标总数: {plan.InputIconCount}  |  目标: {plan.TargetDisplayIconCount}  |  OOS: {plan.OutOfScopeIconCount}\n" +
            $"Excluded: {plan.ExcludedCount}  Pinned: {plan.PinnedCount}\n" +
            $"Override已分配: {plan.OverrideAssignedCount}  |  Override未分配: {plan.OverrideUnassignedCount}\n" +
            $"Default已分配: {plan.DefaultAssignedCount}  |  Default未分配: {plan.DefaultUnassignedCount}\n" +
            $"已就位: {plan.AlreadyAtTargetCount}  |  需移动: {plan.MoveRequiredCount}\n" +
            $"IsComplete: {plan.IsComplete}  HasChanges: {plan.HasChanges}\n" +
            $"ConnectionMethod: {read.IconRead.ConnectionMethod}";
    }

    private static string FormatRuleAwareDiagnostics(
        LayoutPreviewShellRead read, DesktopLayoutIconRuleSet ruleSet)
    {
        var rulesBreakdown = $"Exclude={ruleSet.Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.Exclude)} " +
            $"Pin={ruleSet.Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.Pin)} " +
            $"Override={ruleSet.Rules.Count(r => r.Kind == DesktopLayoutIconRuleKind.ZoneOverride)}";
        return $"Capabilities.间距: {read.Capabilities.Spacing.X}x{read.Capabilities.Spacing.Y}\n" +
            $"ConnectionMethod: {read.Capabilities.ConnectionMethod}\n" +
            $"TemplateId: {ruleSet.TemplateId:D}\n" +
            $"RuleCount: {ruleSet.RuleCount}  ({rulesBreakdown})";
    }

    private static bool TopologyConsistent(IReadOnlyList<DisplayDescriptor> a, IReadOnlyList<DisplayDescriptor> b)
    {
        if (a.Count != b.Count) return false;
        var aSorted = a.OrderBy(d => d.DeviceName, StringComparer.Ordinal).ToList();
        var bSorted = b.OrderBy(d => d.DeviceName, StringComparer.Ordinal).ToList();
        for (var i = 0; i < aSorted.Count; i++)
        {
            var da = aSorted[i]; var db = bSorted[i];
            if (!StringComparer.Ordinal.Equals(da.DeviceName, db.DeviceName)) return false;
            if (da.Bounds != db.Bounds || da.WorkArea != db.WorkArea) return false;
            if (da.DpiX != db.DpiX || da.DpiY != db.DpiY) return false;
            if (da.IsPrimary != db.IsPrimary) return false;
        }
        return true;
    }
}

internal sealed record LayoutPreviewShellRead(
    DesktopIconPositioningCapabilities Capabilities,
    DesktopIconReadResult IconRead);
