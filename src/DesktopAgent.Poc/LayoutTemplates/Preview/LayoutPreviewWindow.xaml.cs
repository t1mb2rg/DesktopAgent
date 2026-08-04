using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Execution;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Rules;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Persistence.DesktopLayouts;
using DesktopAgent.Persistence.DesktopLayouts.Templates;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;
using DesktopAgent.WindowsInterop.Displays;

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

    // M2-04B 新增：执行协调器和安全快照基础设施
    private readonly TargetDisplayLayoutExecutionCoordinator _executionCoordinator;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IDesktopLayoutSafetySnapshotFactory _safetySnapshotFactory;

    private readonly ObservableCollection<LayoutPreviewTemplateRow> _templateRows = [];
    private readonly ObservableCollection<LayoutPreviewDisplayRow> _displayRows = [];
    private readonly ObservableCollection<LayoutPreviewItemRow> _itemRows = [];
    private readonly ObservableCollection<LayoutPreviewOutOfScopeRow> _oosRows = [];
    private readonly ObservableCollection<ExecutionStepRow> _execStepRows = [];

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

    // ═══ M2-04B: Candidate 与执行状态 ═══
    private DesktopLayoutRuleAwareExecutionCandidateBuildResult? _candidateBuildResult;
    private DesktopLayoutRuleAwareExecutionCandidate? _activeCandidate;
    private long _candidateGeneration; // 生成 Candidate 时的 generation
    private Guid _candidateTemplateId;
    private string? _candidateTargetDeviceName;
    private bool _executionInProgress;
    private bool _manualRecoveryRequired;

    // ═══ M2-04B: 回传给 MainWindow 的公开状态 ═══
    public bool RequiresManualRecovery => _manualRecoveryRequired;
    public TargetDisplayLayoutExecutionRunResult? LastExecutionResult { get; private set; }
    public string? EscapedException { get; private set; }

    public LayoutPreviewWindow(
        IDesktopLayoutTemplateStore templateStore,
        IDesktopLayoutIconRuleStore ruleStore,
        IDisplayTopologyReader displayTopologyReader,
        IDesktopIconReader desktopIconReader,
        IDesktopIconPositionWriter positioningCapabilitiesProvider,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        TargetDisplayLayoutExecutionCoordinator executionCoordinator,
        IDesktopLayoutSnapshotStore snapshotStore,
        IDesktopLayoutSafetySnapshotFactory safetySnapshotFactory)
    {
        InitializeComponent();

        _templateStore = templateStore;
        _ruleStore = ruleStore;
        _displayTopologyReader = displayTopologyReader;
        _desktopIconReader = desktopIconReader;
        _positioningCapabilitiesProvider = positioningCapabilitiesProvider;
        _staExecutor = staExecutor;
        _snapshotFactory = snapshotFactory;
        _executionCoordinator = executionCoordinator;
        _snapshotStore = snapshotStore;
        _safetySnapshotFactory = safetySnapshotFactory;

        TemplateCombo.ItemsSource = _templateRows;
        DisplayCombo.ItemsSource = _displayRows;
        ItemsGrid.ItemsSource = _itemRows;
        OutOfScopeGrid.ItemsSource = _oosRows;
        ExecStepsGrid.ItemsSource = _execStepRows;

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
            await _snapshotStore.InitializeAsync(_windowCts?.Token ?? default);
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
        InvalidateCandidate();
    }

    // ═══ Candidate 生命周期管理 ═══

    private void InvalidateCandidate()
    {
        BumpGeneration();
        _candidateBuildResult = null;
        _activeCandidate = null;
        _candidateGeneration = 0;
        _candidateTemplateId = Guid.Empty;
        _candidateTargetDeviceName = null;
        _execStepRows.Clear();
        CandidateSummaryText.Text = "尚未生成执行候选。点击 生成预览 后自动生成。";
        CandidateIssuesText.Visibility = Visibility.Collapsed;
        StagingWarningText.Visibility = Visibility.Collapsed;
        ExecutionConsentCheckBox.IsChecked = false;
    }

    private void SetCandidate(
        DesktopLayoutRuleAwareExecutionCandidateBuildResult buildResult,
        long gen)
    {
        _candidateBuildResult = buildResult;
        _candidateGeneration = gen;
        _candidateTemplateId = _loadedTemplate?.TemplateId ?? Guid.Empty;
        _candidateTargetDeviceName = _ruleAwarePreviewPlan?.TargetDisplay.DeviceName;

        if (buildResult.IsSuccess && buildResult.Candidate is not null)
        {
            _activeCandidate = buildResult.Candidate;
            DisplayCandidate(buildResult.Candidate);
        }
        else
        {
            _activeCandidate = null;
            DisplayCandidateRejection(buildResult);
        }
    }

    private void DisplayCandidate(DesktopLayoutRuleAwareExecutionCandidate c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"InputIconCount: {c.InputIconCount}");
        sb.AppendLine($"FixedCount: {c.FixedCount}  |  AssignedCount: {c.AssignedCount}");
        sb.AppendLine($"AlreadyAtTarget: {c.AlreadyAtTargetCount}  |  MoveRequired: {c.MoveRequiredCount}");
        sb.AppendLine($"RestoreBlocked: {c.RestoreBlockedCount}  |  RestoreMissing: {c.RestoreMissingCount}");
        sb.AppendLine($"ExecutionSteps: {c.ExecutionStepCount}");
        sb.AppendLine($"IsNoOp: {c.IsNoOp}  |  IsExecutable: {c.IsExecutable}");
        sb.AppendLine($"RequiresUserConfirmation: {c.RequiresUserConfirmation}");
        sb.AppendLine($"UsesTemporaryStaging: {c.UsesTemporaryStaging}");
        sb.AppendLine($"TargetDeviceName: {c.TargetDeviceName}");
        CandidateSummaryText.Text = sb.ToString().TrimEnd();

        // 中转警告
        if (c.UsesTemporaryStaging)
        {
            StagingWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            StagingWarningText.Visibility = Visibility.Collapsed;
        }

        CandidateIssuesText.Visibility = Visibility.Collapsed;

        // 执行步骤
        _execStepRows.Clear();
        foreach (var step in c.ExecutionPlan.Steps)
            _execStepRows.Add(new ExecutionStepRow(step));
    }

    private void DisplayCandidateRejection(
        DesktopLayoutRuleAwareExecutionCandidateBuildResult buildResult)
    {
        CandidateSummaryText.Text = $"Candidate 生成失败：BuildResult.Outcome = {buildResult.Outcome}";
        CandidateIssuesText.Text = string.Join("\n", buildResult.Issues.Select(i =>
            $"[{i.Code}] {i.Message}"));
        CandidateIssuesText.Visibility = Visibility.Visible;
        StagingWarningText.Visibility = Visibility.Collapsed;
        _execStepRows.Clear();
    }

    // ═══ 模板 ═══

    private async Task RefreshTemplatesAsync()
    {
        if (!_storeReady) return;

        var previousTemplateId = _loadedTemplate?.TemplateId;

        var list = await _templateStore.ListAsync(200, _windowCts?.Token ?? default);
        if (_isClosing) return;

        _suppressTemplateSelectionChanged = true;
        try
        {
            _templateRows.Clear();
            foreach (var s in list) _templateRows.Add(new LayoutPreviewTemplateRow(s));

            if (previousTemplateId.HasValue)
            {
                SelectTemplateInCombo(previousTemplateId.Value);
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

    private async Task LoadTemplateAsync(Guid templateId, long gen, CancellationToken ct)
    {
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

    // ═══ 生成预览 + Candidate ═══

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

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
            // 1. Fresh re-read 完整模板和规则集
            var template = await _templateStore.GetAsync(selectedTemplateId, ct);
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;
            if (template is null)
            {
                if (!_isClosing) StatusText.Text = $"模板 {selectedTemplateId:D} 不存在。";
                return;
            }

            var ruleSet = await _ruleStore.GetAsync(selectedTemplateId, ct);
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;
            if (ruleSet is null || ruleSet.TemplateId != template.TemplateId)
            {
                if (!_isClosing) StatusText.Text = $"模板 {selectedTemplateId:D} 的规则集不存在或 TemplateId 不一致。";
                return;
            }

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
                if (!_isClosing) StatusText.Text = $"目标显示器解析失败：{ex.Message}";
                return;
            }
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            // 4. STA 线程读取
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
                if (!_isClosing) StatusText.Text = "读取期间显示器拓扑发生变化，请重试。";
                return;
            }
            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            if (shellRead.IconRead.FailedItemCount > 0)
            {
                if (!_isClosing) StatusText.Text = $"图标读取不完整：{shellRead.IconRead.FailedItemCount} 个失败。无法生成预览。";
                return;
            }

            // 6. 分配 MonitorDeviceName
            var assignedIcons = shellRead.IconRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays2);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            var currentRead = new DesktopIconReadResult(
                assignedIcons, shellRead.IconRead.FailedItemCount,
                shellRead.IconRead.ConnectionMethod, shellRead.IconRead.ReadTime,
                shellRead.IconRead.PrimaryPathException);

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

            // ═══ 10. M2-04B: 同时 Build Candidate ═══
            var buildResult = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
                plan, currentRead, displays2, shellRead.Capabilities, _snapshotFactory);

            if (Interlocked.Read(ref _generation) != gen || _isClosing) return;

            SetCandidate(buildResult, gen);

            StatusText.Text = plan.IsComplete
                ? (plan.HasChanges ? "预览完成：有图标需要移动。" : "预览完成：所有图标已就位。")
                : "预览完成：容量不足，部分图标未分配。";
        }
        catch (OperationCanceledException) { /* 静默返回 */ }
        catch (Exception ex)
        {
            if (!_isClosing) StatusText.Text = $"生成预览失败：{ex.Message}";
        }
        finally { _isBusy = false; if (!_isClosing) UpdateButtons(); }
    }

    // ═══ 画布渲染 ═══

    private void RenderScene()
    {
        PreviewCanvas.Children.Clear();
        if (_scene is null || _ruleAwarePreviewPlan is null) return;

        var t = _scene.Transform;

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
        // 注意：Canvas 重绘不使 Candidate 失效
    }

    // ═══ 按钮状态 ═══

    private void UpdateButtons()
    {
        if (_isClosing) return;

        var busy = _isBusy || _executionInProgress;
        GenerateButton.IsEnabled = !busy && _storeReady && _loadedTemplate is not null;
        RefreshButton.IsEnabled = !busy;
        TemplateCombo.IsEnabled = !busy;
        DisplayCombo.IsEnabled = !busy && _loadedTemplate?.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget;
        ExecutionConsentCheckBox.IsEnabled = !busy && !_manualRecoveryRequired;

        UpdateExecuteButton();
    }

    private void UpdateExecuteButton()
    {
        if (_isClosing)
        {
            ExecuteButton.IsEnabled = false;
            return;
        }

        var busy = _isBusy || _executionInProgress;

        // 检查基本前置条件
        if (busy || _manualRecoveryRequired)
        {
            ExecuteButton.IsEnabled = false;
            UpdateExecuteHint(busy);
            return;
        }

        if (_activeCandidate is null || _candidateBuildResult is null
            || !_candidateBuildResult.IsSuccess)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "";
            return;
        }

        var c = _activeCandidate;

        // 检查 generation
        if (Interlocked.Read(ref _generation) != _candidateGeneration)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "预览已变化，请重新生成预览。";
            return;
        }

        // 检查 Candidate 状态
        if (!c.IsExecutable)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "该计划不可执行。";
            return;
        }
        if (c.IsNoOp)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "无需移动（NoOp）。";
            return;
        }
        if (c.UsesTemporaryStaging)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "该计划需要临时中转，将在 M2-04C 开放真实执行。";
            return;
        }

        // 批量 Gate 检查
        var gate = TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch(
            c.RestorePlan, c.ExecutionPlan);
        if (!gate.IsAllowed)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = $"执行闸门拒绝：{string.Join("; ", gate.RejectionReasons)}";
            return;
        }

        // 同意框
        if (ExecutionConsentCheckBox.IsChecked != true)
        {
            ExecuteButton.IsEnabled = false;
            ExecuteHintText.Text = "请先勾选同意框。";
            return;
        }

        ExecuteButton.IsEnabled = true;
        ExecuteHintText.Text = "";
    }

    private void UpdateExecuteHint(bool busy)
    {
        if (busy) ExecuteHintText.Text = "操作进行中…";
        else if (_manualRecoveryRequired) ExecuteHintText.Text = "需要人工处理，所有执行已禁用。";
    }

    private void ExecutionConsent_Changed(object sender, RoutedEventArgs e) => UpdateButtons();

    // ═══ 批量执行 ═══

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_executionInProgress) return;

        // 1. 再次验证窗口状态和批量 Gate
        if (_activeCandidate is null || _candidateBuildResult is null
            || !_candidateBuildResult.IsSuccess)
            return;

        var candidate = _activeCandidate;
        var gate = TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch(
            candidate.RestorePlan, candidate.ExecutionPlan);
        if (!gate.IsAllowed)
        {
            StatusText.Text = $"执行闸门拒绝：{string.Join("; ", gate.RejectionReasons)}";
            return;
        }

        if (Interlocked.Read(ref _generation) != _candidateGeneration)
        {
            StatusText.Text = "预览已变化，请重新生成预览后再执行。";
            return;
        }

        // 2. 捕获已确认 Candidate
        var confirmedCandidate = candidate;
        var confirmedTemplateId = _candidateTemplateId;
        var confirmedTargetDeviceName = _candidateTargetDeviceName;
        var confirmedTemplate = _loadedTemplate;

        // 3. 二次确认对话框
        var confirmMsg = $"确认执行批量桌面整理？\n\n" +
            $"模板：{confirmedCandidate.SourcePreviewPlan.Template.Name} v{confirmedCandidate.SourcePreviewPlan.Template.Version}\n" +
            $"目标显示器：{confirmedCandidate.TargetDeviceName}\n" +
            $"需要移动：{confirmedCandidate.MoveRequiredCount} 个图标\n" +
            $"执行步骤：{confirmedCandidate.ExecutionStepCount} 步\n" +
            $"使用中转：{(confirmedCandidate.UsesTemporaryStaging ? "是" : "否")}\n\n" +
            $"安全快照将在 Writer 前创建。\n" +
            $"真实桌面图标将被移动。\n" +
            $"失败时可能自动回滚。\n" +
            $"PositionUncertain 时需要人工处理。\n\n" +
            $"确认执行？";

        var dlgResult = MessageBox.Show(confirmMsg, "确认执行批量整理",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (dlgResult != MessageBoxResult.Yes)
        {
            StatusText.Text = "用户取消执行。";
            return;
        }

        // 4. 立即使旧 Candidate 失效
        InvalidateCandidate();
        var execGen = BumpGeneration();
        _executionInProgress = true;
        UpdateButtons();
        StatusText.Text = "正在重新读取并验证…";

        try
        {
            var ct = _windowCts?.Token ?? default;

            // 5. Fresh re-read
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            // a) 模板
            var freshTemplate = await _templateStore.GetAsync(confirmedTemplateId, ct);
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;
            if (freshTemplate is null)
            {
                StatusText.Text = "模板不存在，无法执行。";
                return;
            }

            // b) RuleSet
            var freshRuleSet = await _ruleStore.GetAsync(confirmedTemplateId, ct);
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;
            if (freshRuleSet is null || freshRuleSet.TemplateId != freshTemplate.TemplateId)
            {
                StatusText.Text = "规则集不存在或 TemplateId 不一致，无法执行。";
                return;
            }

            // c) 显示器拓扑
            var displays1 = _displayTopologyReader.GetDisplays();
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            // d) STA 读取
            var shellRead = await _staExecutor.RunAsync(() =>
            {
                var capabilities = _positioningCapabilitiesProvider.GetCapabilities();
                var iconRead = _desktopIconReader.ReadDesktopIcons();
                return new LayoutPreviewShellRead(capabilities, iconRead);
            });
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            // e) 拓扑一致性
            var displays2 = _displayTopologyReader.GetDisplays();
            if (!TopologyConsistent(displays1, displays2))
            {
                StatusText.Text = "执行前重新读取：显示器拓扑发生变化，Writer 未启动。请重新预览。";
                return;
            }
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            if (shellRead.IconRead.FailedItemCount > 0)
            {
                StatusText.Text = $"执行前重新读取不完整（{shellRead.IconRead.FailedItemCount} 个失败），Writer 未启动。请重新预览。";
                return;
            }

            // 6. 重新分配 MonitorDeviceName
            var assignedIcons = shellRead.IconRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, displays2);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            var freshRead = new DesktopIconReadResult(
                assignedIcons, shellRead.IconRead.FailedItemCount,
                shellRead.IconRead.ConnectionMethod, shellRead.IconRead.ReadTime,
                shellRead.IconRead.PrimaryPathException);

            // 7. 解析目标显示器
            var localSelectedTarget = freshTemplate.TargetDisplayBinding.Kind == TargetDisplayBindingKind.SelectedTarget
                ? confirmedCandidate.SourcePreviewPlan.TargetDisplay.DeviceName : null;
            DisplayDescriptor freshTargetDisplay;
            try
            {
                freshTargetDisplay = DesktopLayoutTargetDisplayResolver.Resolve(
                    freshTemplate.TargetDisplayBinding, displays2, localSelectedTarget);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"目标显示器解析失败：{ex.Message}，Writer 未启动。请重新预览。";
                return;
            }
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            // 8. 重新生成 PreviewPlan
            var freshSnapshot = _snapshotFactory.Create(
                assignedIcons, freshTargetDisplay.DeviceName, shellRead.IconRead.ConnectionMethod);
            var freshPlan = DesktopLayoutRuleAwarePreviewPlanner.Plan(
                freshTemplate, freshRuleSet, displays2, localSelectedTarget,
                shellRead.Capabilities.Spacing, freshSnapshot.Icons);
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            // 9. 重新 Build Candidate
            var freshBuildResult = DesktopLayoutRuleAwareExecutionCandidatePlanner.Build(
                freshPlan, freshRead, displays2, shellRead.Capabilities, _snapshotFactory);
            if (_isClosing || Interlocked.Read(ref _generation) != execGen) return;

            if (!freshBuildResult.IsSuccess || freshBuildResult.Candidate is null)
            {
                var issues = string.Join("; ", freshBuildResult.Issues.Select(i => $"[{i.Code}] {i.Message}"));
                StatusText.Text = $"执行前重建 Candidate 失败，Writer 未启动。Issues: {issues}\n请重新预览。";
                return;
            }

            var freshCandidate = freshBuildResult.Candidate;

            // 10. 语义比较
            var comparison = DesktopLayoutRuleAwareExecutionCandidateSemanticComparer.Compare(
                confirmedCandidate, freshCandidate);

            if (!comparison.IsEquivalent)
            {
                StatusText.Text = $"桌面状态在预览后发生变化，Writer 未启动。\n\n" +
                    $"变化详情：\n{string.Join("\n", comparison.Differences)}\n\n" +
                    $"请重新生成预览并重新确认。";
                ExecutionConsentCheckBox.IsChecked = false;
                return;
            }

            // 11. 调用 Coordinator
            StatusText.Text = "语义验证通过，正在执行…";
            UpdateButtons();

            TargetDisplayLayoutExecutionRunResult result;
            try
            {
                result = await _executionCoordinator.ExecuteWithScopeGateAsync(
                    freshCandidate.RestorePlan,
                    TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch,
                    CancellationToken.None); // 使用 None — 执行不可中途取消
            }
            catch (Exception ex)
            {
                // 逃逸异常 — 进入人工处理锁
                _manualRecoveryRequired = true;
                LastExecutionResult = null;
                EscapedException = $"{ex.GetType().Name}: {ex.Message}";
                DisplayExecutionResult(null, EscapedException);
                UpdateButtons();
                return;
            }

            LastExecutionResult = result;
            EscapedException = null;
            DisplayExecutionResult(result, null);
        }
        catch (OperationCanceledException) { /* 静默返回 */ }
        catch (Exception ex)
        {
            // Writer 前异常（fresh re-read/re-plan/semantic comparison）— 不进入人工处理锁
            // Writer 尚未启动，没有副作用
            EscapedException = $"{ex.GetType().Name}: {ex.Message}";
            LastExecutionResult = null;
            ExecutionResultText.Text = $"执行准备阶段失败（Writer 未启动，无副作用）：\n{ex.Message}\n请重新生成预览。";
            ManualRecoveryText.Visibility = Visibility.Collapsed;
            StatusText.Text = $"执行准备阶段失败：{ex.Message}。请重新预览。";
        }
        finally
        {
            _executionInProgress = false;
            if (!_isClosing) UpdateButtons();
        }
    }

    private void DisplayExecutionResult(
        TargetDisplayLayoutExecutionRunResult? result,
        string? escapedException)
    {
        var sb = new System.Text.StringBuilder();

        if (escapedException is not null)
        {
            sb.AppendLine($"逃逸异常: {escapedException}");
            sb.AppendLine("\n⚠ 无法确定异常发生在 Writer 前还是后。");
            sb.AppendLine("已进入人工处理锁。");
            ExecutionResultText.Text = sb.ToString().TrimEnd();
            ManualRecoveryText.Visibility = Visibility.Visible;
            VerificationResultText.Visibility = Visibility.Collapsed;
            return;
        }

        if (result is null)
        {
            ExecutionResultText.Text = "无执行结果。";
            return;
        }

        sb.AppendLine($"Status: {result.Status}");
        sb.AppendLine($"IsSuccess: {result.IsSuccess}");
        sb.AppendLine($"IsNoOp: {result.IsNoOp}");
        sb.AppendLine($"SafetySnapshotId: {result.SafetySnapshotId}");
        sb.AppendLine($"SessionId: {result.Session?.SessionId}");
        sb.AppendLine($"FailedStepIndex: {result.FailedStepIndex}");
        sb.AppendLine($"RequiresManualRecovery: {result.RequiresManualRecovery}");
        sb.AppendLine($"IsPositionUncertain: {result.IsPositionUncertain}");

        if (result.ForwardAttempts.Count > 0)
        {
            sb.AppendLine($"\nForwardAttempts ({result.ForwardAttempts.Count}):");
            foreach (var a in result.ForwardAttempts)
                sb.AppendLine($"  #{a.AttemptIndex} Step[{a.SourceStepIndex}] {a.Outcome}");
        }

        if (result.RollbackAttempts.Count > 0)
        {
            sb.AppendLine($"\nRollbackAttempts ({result.RollbackAttempts.Count}):");
            foreach (var a in result.RollbackAttempts)
                sb.AppendLine($"  #{a.AttemptIndex} Step[{a.SourceStepIndex}] {a.Outcome} (Phase: {a.Phase})");
        }

        // 验证结果
        if (result.VerificationResult is not null)
        {
            var vr = result.VerificationResult;
            sb.AppendLine($"\n--- 独立验证 ---");
            sb.AppendLine($"Phase: {vr.Phase}  Status: {vr.Status}");
            sb.AppendLine($"FailedItemCount: {vr.FailedItemCount}");
            sb.AppendLine($"VerifiedAtUtc: {vr.VerifiedAtUtc:O}");
            if (vr.Issues.Count > 0)
            {
                sb.AppendLine($"Issues ({vr.Issues.Count}):");
                foreach (var issue in vr.Issues)
                    sb.AppendLine($"  - {issue}");
            }
            VerificationResultText.Text = sb.ToString();
            VerificationResultText.Visibility = Visibility.Collapsed; // 合并到主文本
        }

        // 状态处理
        switch (result.Status)
        {
            case TargetDisplayLayoutExecutionRunStatus.Completed:
                sb.AppendLine("\n✓ 执行成功，独立验证通过。");
                sb.AppendLine("请重新生成预览。");
                InvalidateCandidate();
                ExecutionConsentCheckBox.IsChecked = false;
                ManualRecoveryText.Visibility = Visibility.Collapsed;
                break;

            case TargetDisplayLayoutExecutionRunStatus.CompletedNoOp:
                sb.AppendLine("\nNoOp 完成。");
                ManualRecoveryText.Visibility = Visibility.Collapsed;
                break;

            case TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite:
                sb.AppendLine("\nWriter 未开始，没有副作用。请重新预览。");
                InvalidateCandidate();
                ManualRecoveryText.Visibility = Visibility.Collapsed;
                break;

            case TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack:
                if (result.VerificationResult?.Status == ExecutionVerificationStatus.Passed)
                    sb.AppendLine("\n正向失败但已确认安全回滚，没有残留副作用。");
                else
                    sb.AppendLine("\n正向失败，回滚已执行但验证不完整。");
                InvalidateCandidate();
                ManualRecoveryText.Visibility = Visibility.Collapsed;
                break;

            case TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete:
            case TargetDisplayLayoutExecutionRunStatus.PositionUncertain:
                _manualRecoveryRequired = true;
                sb.AppendLine("\n⚠ 需要人工处理。");
                ManualRecoveryText.Visibility = Visibility.Visible;
                break;
        }

        ExecutionResultText.Text = sb.ToString().TrimEnd();
    }

    // ═══ 刷新 ═══

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
                await _ruleStore.InitializeAsync(_windowCts?.Token ?? default);
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
        // 执行中禁止关闭
        if (_executionInProgress)
        {
            e.Cancel = true;
            StatusText.Text = "执行进行中，无法关闭窗口。";
            return;
        }

        _isClosing = true;
        BumpGeneration();
        _windowCts?.Cancel();
    }

    // ═══ 格式化 ═══

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

// ═══ 支持类型 ═══

internal sealed record LayoutPreviewShellRead(
    DesktopIconPositioningCapabilities Capabilities,
    DesktopIconReadResult IconRead);

/// <summary>
/// 执行步骤行视图模型 — 不可变。
/// </summary>
public sealed class ExecutionStepRow
{
    public int StepIndex { get; }
    public string KindText { get; }
    public string StableIdTruncated { get; }
    public string DisplayName { get; }
    public int FromX { get; }
    public int FromY { get; }
    public int ToX { get; }
    public int ToY { get; }

    public ExecutionStepRow(TargetDisplayLayoutExecutionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        StepIndex = step.StepIndex;
        KindText = step.Kind.ToString();
        StableIdTruncated = step.StableId.Length <= 24
            ? step.StableId : step.StableId[..21] + "...";
        DisplayName = step.DisplayName ?? "";
        FromX = step.FromPosition.X;
        FromY = step.FromPosition.Y;
        ToX = step.ToPosition.X;
        ToY = step.ToPosition.Y;
    }
}
