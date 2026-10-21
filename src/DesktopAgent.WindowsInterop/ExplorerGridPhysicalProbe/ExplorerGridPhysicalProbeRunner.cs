using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

/// <summary>
/// Explorer 网格真机探测（校准）。它只编排现有 Observation 与 primitive Writer，
/// 由诊断工作台与产品校准面板共用；不参与产品 Workspace、AI、Recovery 或自动执行 authority，
/// 唯一产物是"这台机器上图标会落在哪"的 Measured 事实。
/// </summary>
public sealed class ExplorerGridPhysicalProbeRunner
{
    private const int ShortcutObservationAttempts = 20;

    // Explorer 的布局回写会让整个桌面在数秒内处于“两次采样不相同”的状态（真机第 7 次运行：写入开始后
    // 8 个既有图标被挪动，稳定观测连续 4 轮失败并中止了整轮）。预算要覆盖这段窗口，用尽仍不稳定才失败。
    private const int SettledObservationAttempts = 24;
    /// <summary>seed 落点被 Explorer 回写到别的显示器时，最多再把同一个快捷方式挪回去几次。</summary>
    private const int MaximumSeedAttempts = 3;
    private static readonly TimeSpan SeedRetryDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ShortcutObservationDelay = TimeSpan.FromMilliseconds(250);

    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopObservationSource _observationSource;
    private readonly IStableDesktopObservationSource _stableObservationSource;
    private readonly IDesktopIconPositionWriter _writer;
    private readonly StaThreadExecutor _staExecutor;
    private readonly IExplorerGridProbeShortcutStore _shortcutStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public ExplorerGridPhysicalProbeRunner(
        IDisplayTopologyReader topologyReader,
        IDesktopObservationSource observationSource,
        IStableDesktopObservationSource stableObservationSource,
        IDesktopIconPositionWriter writer,
        StaThreadExecutor staExecutor,
        IExplorerGridProbeShortcutStore shortcutStore,
        TimeProvider timeProvider)
    {
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _stableObservationSource = stableObservationSource ?? throw new ArgumentNullException(nameof(stableObservationSource));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _shortcutStore = shortcutStore ?? throw new ArgumentNullException(nameof(shortcutStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ExplorerGridPhysicalProbeReport> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            return EmptyFailure(startedAt, "已有 Explorer Grid Physical Probe 正在运行。");
        }

        var moves = new List<ExplorerGridProbeMove>();
        var passiveSamples = new List<ExplorerGridProbePassiveSample>();
        var displayResults = new List<ExplorerGridProbeDisplayResult>();
        var cleanup = new List<ExplorerGridProbeCleanupResult>();
        var shortcuts = Array.Empty<ExplorerGridProbeShortcut>();
        var sessions = new List<ProbeSession>();
        var expectedProbePositions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        DesktopObservation? baseline = null;
        string? failure = null;
        var cancelled = false;
        var blocked = false;
        var shortcutsDeleted = false;
        var userIconsUnchanged = false;
        var userIconsRepairedCount = 0;
        var unrepairedUserIconCount = 0;

        try
        {
            baseline = await CaptureStableRequiredAsync(
                "初始 baseline",
                expectedTransitionStableIds: null,
                toleratedExistingItemStableIds: null,
                cancellationToken);
            ValidateInitialObservation(baseline);
            EnsureTopologyUnchanged(baseline.Displays);
            progress?.Report(
                $"Baseline 已冻结：{baseline.Displays.Count} 块显示器，{baseline.Icons.Count} 个既有桌面项目；准备创建专用测试快捷方式。");

            shortcuts = (await _shortcutStore.CreateAsync(
                    baseline.Displays.Count,
                    cancellationToken))
                .ToArray();
            if (shortcuts.Length != baseline.Displays.Count)
                throw new ProbeAbortException("Shortcut Store 未返回一屏一个的专用测试快捷方式。");

            var probeObservation = await WaitForOwnedShortcutsAsync(
                baseline,
                shortcuts,
                cancellationToken);
            var probeIcons = ResolveOwnedShortcuts(probeObservation, shortcuts);
            EnsureUserIconSetIsIntact(baseline, probeObservation, probeIcons.Keys);

            var orderedDisplays = OrderDisplays(baseline.Displays);
            foreach (var icon in probeIcons.Values)
                expectedProbePositions.Add(icon.StableId, icon.PixelPosition);
            for (var index = 0; index < orderedDisplays.Count; index++)
            {
                var icon = probeIcons[NormalizePath(shortcuts[index].FilePath)];
                if (!baseline.Displays.Any(display => display.WorkArea.Contains(icon.PixelPosition)))
                {
                    throw new ProbeAbortException(
                        $"专用测试快捷方式 {icon.StableId} 的 Explorer 初始位置越出所有 WorkArea：" +
                        $"({icon.PixelPosition.X},{icon.PixelPosition.Y})。");
                }

                sessions.Add(new ProbeSession(
                    orderedDisplays[index],
                    icon.StableId,
                    icon.PixelPosition));
            }

            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spacing = baseline.PositioningCapabilities.Spacing;
                progress?.Report($"{session.Display.DeviceName}: 正在采集中心 seed…");
                var seedRequest = FindSafeSeedRequest(
                    baseline,
                    expectedProbePositions,
                    session,
                    spacing);
                var seedAttempts = 0;
                var seed = seedRequest;
                while (true)
                {
                    seedAttempts++;
                    seed = (await ExecuteMoveAsync(
                        baseline,
                        shortcuts,
                        expectedProbePositions,
                        session,
                        ExplorerGridProbeMoveKind.Seed,
                        ordinal: seedAttempts - 1,
                        seedRequest,
                        requireExact: false,
                        moves,
                        progress,
                        cancellationToken)).Observed;
                    // 落点跑到别的显示器时不再重试：那说明 Explorer 不接受把项目留在这块屏
                    // （真机实测 DISPLAY2 的 seed 每次都被放回 DISPLAY1 的 (2,702)），
                    // 继续重试只会反复移动用户图标而不可能成功。落点仍在本屏 Bounds 内
                    // （只是没落进 WorkArea）时保留重试。
                    if (session.Display.WorkArea.Contains(seed) ||
                        !session.Display.Bounds.Contains(seed) ||
                        seedAttempts >= MaximumSeedAttempts)
                        break;

                    // 真机实测：快捷方式刚创建时 Explorer 会把它按"新项目"再摆一次位
                    // （DISPLAY2 请求 (-540,730)，落点却被放回 DISPLAY1 的 (2,702)）。
                    // 这不是几何矛盾，但会让该屏没有任何实测样本——先等它稳定，再把同一个图标挪回去重试；
                    // 只有重试用尽才判定"这块屏测不到"。
                    progress?.Report(
                        $"{session.Display.DeviceName}: seed 落点被 Explorer 放到 ({seed.X},{seed.Y})，" +
                        $"不在本屏 WorkArea；等待后重试（第 {seedAttempts} 次，最多 {MaximumSeedAttempts} 次）。");
                    await Task.Delay(SeedRetryDelay, _timeProvider, cancellationToken);
                }

                if (!session.Display.WorkArea.Contains(seed))
                {
                    // 真机实测：Explorer 会把探针快捷方式的落点异步回写到别的显示器
                    // （DISPLAY2 请求 (-540,730)，落点却是 DISPLAY1 的 (2,702)）。
                    // 这不是几何矛盾，而是"这块屏测不到"：该屏没有任何实测样本，
                    // 因此本屏标记为未实测，继续测其余屏；事实写入门槛要求每块屏都实测通过。
                    // 原因里带上该屏自己的锚点情况：没有既有图标的屏无法形成晶格共识，
                    // 它的 seed 相位只能来自探针自己的落点（真机 DISPLAY2 就是这样被算到 DISPLAY1 的晶格上）。
                    var anchors = ComputeIconLatticeConsensus(
                        baseline,
                        session.Display,
                        spacing,
                        expectedProbePositions);
                    var reason =
                        $"seed 请求 ({seedRequest.X},{seedRequest.Y}) 的落点被 Explorer 放到了 ({seed.X},{seed.Y})，" +
                        $"不在 {session.Display.DeviceName} 的 WorkArea 内" +
                        $"（该屏 WorkArea 内既有图标锚点 {anchors.SampleCount} 个" +
                        (anchors.IsReliable
                            ? $"，已形成晶格 phase=({anchors.Phase!.Value.X},{anchors.Phase!.Value.Y})"
                            : "，不足以形成晶格共识") + "）";
                    displayResults.Add(new ExplorerGridProbeDisplayResult(
                        session.Display.DeviceName,
                        spacing,
                        ExplorerGridProbePlan.PhaseOf(seed, spacing),
                        0,
                        0,
                        0,
                        ObservedSpacingConstant: false,
                        ObservedPhaseConstant: false)
                    {
                        IsMeasured = false,
                        UnmeasuredReason = reason,
                        PhaseVerified = false,
                    });
                    progress?.Report($"{session.Display.DeviceName}: {reason}；跳过该屏，继续测量其余屏。");
                    continue;
                }

                // 相位是"每块屏各自工作区原点"的属性，绝不跨显示器比较：
                // 任务栏只占一块屏时，三块屏的绝对相位本来就不同（真机实测 D1=(2,16)、D3=(35,84)）。
                var phase = ExplorerGridProbePlan.PhaseOf(seed, spacing);
                var consensus = ComputeIconLatticeConsensus(
                    baseline,
                    session.Display,
                    spacing,
                    expectedProbePositions);
                if (consensus.IsReliable && consensus.Phase != phase)
                {
                    // 真机实测（2026-10-14）：DISPLAY3 的 seed 落点推断出 (2,16) 晶格，而该屏 46/48 个既有图标
                    // 在 (35,84) 晶格上，两者相差不是 spacing 的整数倍。此时测得的落点不能代表该屏既有图标
                    // 所在的晶格，写出的"事实"会让布局预测把用户图标算错，所以本屏不写事实。但这是"这块屏
                    // 测不到"，不是几何矛盾，因此不中止整轮（用户 2026-10-14 拍板：事实逐屏写入，未实测屏由
                    // 策略层拒绝执行）。
                    var reason =
                        $"seed 落点推断 phase=({phase.X},{phase.Y}) 与该屏 {consensus.Support}/{consensus.SampleCount} 个既有图标的晶格 " +
                        $"phase=({consensus.Phase!.Value.X},{consensus.Phase!.Value.Y}) 不一致（桌面图标不在探针将要测量的网格上）";
                    displayResults.Add(new ExplorerGridProbeDisplayResult(
                        session.Display.DeviceName,
                        spacing,
                        phase,
                        0,
                        0,
                        0,
                        ObservedSpacingConstant: false,
                        ObservedPhaseConstant: false)
                    {
                        IsMeasured = false,
                        UnmeasuredReason = reason,
                        PhaseVerified = false,
                    });
                    progress?.Report($"{session.Display.DeviceName}: {reason}；跳过该屏，继续测量其余屏。");
                    continue;
                }

                var plan = ExplorerGridProbePlan.Create(
                    session.Display,
                    spacing,
                    phase,
                    seed,
                    GetOccupants(baseline, expectedProbePositions, session.StableId));
                var horizontal = new List<PixelPoint>(plan.Horizontal.Count);
                var skippedLandings = 0;
                foreach (var (slot, ordinal) in EnumerateCenterOut(plan.Horizontal, seed))
                {
                    var landing = await ObservePlannedSlotAsync(
                        baseline,
                        shortcuts,
                        expectedProbePositions,
                        session,
                        ExplorerGridProbeMoveKind.Horizontal,
                        ordinal,
                        slot,
                        moves,
                        passiveSamples,
                        progress,
                        cancellationToken);
                    if (!landing.IsExact)
                    {
                        skippedLandings++;
                        progress?.Report(
                            $"{session.Display.DeviceName} horizontal[{ordinal}]: " +
                            $"落点偏离计划槽位 ({slot.Position.X},{slot.Position.Y})，该样本不计数。");
                        continue;
                    }

                    EnsurePhase(phase, spacing, landing.Observed, session.Display.DeviceName, "horizontal", ordinal);
                    horizontal.Add(landing.Observed);
                }
                EnsureHorizontalSpacing(horizontal, spacing, session.Display.DeviceName);

                var vertical = new List<PixelPoint>(plan.Vertical.Count);
                foreach (var (slot, ordinal) in EnumerateCenterOut(plan.Vertical, seed))
                {
                    var landing = await ObservePlannedSlotAsync(
                        baseline,
                        shortcuts,
                        expectedProbePositions,
                        session,
                        ExplorerGridProbeMoveKind.Vertical,
                        ordinal,
                        slot,
                        moves,
                        passiveSamples,
                        progress,
                        cancellationToken);
                    if (!landing.IsExact)
                    {
                        skippedLandings++;
                        progress?.Report(
                            $"{session.Display.DeviceName} vertical[{ordinal}]: " +
                            $"落点偏离计划槽位 ({slot.Position.X},{slot.Position.Y})，该样本不计数。");
                        continue;
                    }

                    EnsurePhase(phase, spacing, landing.Observed, session.Display.DeviceName, "vertical", ordinal);
                    vertical.Add(landing.Observed);
                }
                EnsureVerticalSpacing(vertical, spacing, session.Display.DeviceName);

                var cornerLandings = new List<PixelPoint>(plan.Corners.Count);
                for (var index = 0; index < plan.Corners.Count; index++)
                {
                    var landing = await ObservePlannedSlotAsync(
                        baseline,
                        shortcuts,
                        expectedProbePositions,
                        session,
                        ExplorerGridProbeMoveKind.Corner,
                        index,
                        plan.Corners[index],
                        moves,
                        passiveSamples,
                        progress,
                        cancellationToken);
                    if (!landing.IsExact)
                    {
                        skippedLandings++;
                        progress?.Report(
                            $"{session.Display.DeviceName} corner[{index}]: " +
                            $"落点偏离计划槽位 ({plan.Corners[index].Position.X},{plan.Corners[index].Position.Y})，该样本不计数。");
                        continue;
                    }

                    EnsurePhase(phase, spacing, landing.Observed, session.Display.DeviceName, "corner", index);
                    cornerLandings.Add(landing.Observed);
                }

                // 逐屏判定"这一屏有没有拿到合格事实"：每轴至少 3 个主动落点。
                // 达不到就是"这块屏测不到"：该屏不写事实，但其余屏的扫描继续。
                var minimumActiveSamples = ExplorerGridMeasurement.MinimumMeasuredActiveSamplesPerAxis;
                var displayIsMeasured = horizontal.Count >= minimumActiveSamples &&
                    vertical.Count >= minimumActiveSamples;
                displayResults.Add(new ExplorerGridProbeDisplayResult(
                    session.Display.DeviceName,
                    spacing,
                    phase,
                    horizontal.Count,
                    vertical.Count,
                    cornerLandings.Count,
                    ObservedSpacingConstant: true,
                    ObservedPhaseConstant: true)
                {
                    IsMeasured = displayIsMeasured,
                    UnmeasuredReason = displayIsMeasured
                        ? null
                        : $"主动落点样本不足（horizontal={horizontal.Count} vertical={vertical.Count}，每轴至少 " +
                          $"{minimumActiveSamples}）：Explorer 没有把足够的落点留在计划槽位上",
                    ActiveScanSampleCount = horizontal.Count + vertical.Count + cornerLandings.Count,
                    PassiveOccupiedSampleCount = passiveSamples.Count(sample =>
                        string.Equals(sample.DisplayDeviceName, session.Display.DeviceName, StringComparison.Ordinal)),
                    ActiveHorizontalSampleCount = horizontal.Count,
                    ActiveVerticalSampleCount = vertical.Count,
                    ActiveCornerSampleCount = cornerLandings.Count,
                    SkippedLandingCount = skippedLandings,
                    ConsensusPhase = consensus.Phase,
                    ConsensusSupport = consensus.Support,
                    ConsensusSampleCount = consensus.SampleCount,
                    ConsensusIsReliable = consensus.IsReliable,
                    PhaseVerified = !consensus.IsReliable || consensus.Phase == phase,
                });
                progress?.Report(
                    $"{session.Display.DeviceName}: 十字与四角通过；spacing=({spacing.X},{spacing.Y}) phase=({phase.X},{phase.Y})" +
                    (consensus.IsReliable
                        ? $"；既有图标晶格校验通过（{consensus.Support}/{consensus.SampleCount}）。"
                        : "；该屏没有足够一致的既有图标晶格可供交叉校验。"));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            failure = "Probe 已取消；扫描已停止，正在尽力恢复并清理专用快捷方式。";
        }
        catch (ExplorerGridProbeBlockedException exception)
        {
            blocked = true;
            failure = exception.Message;
            progress?.Report($"Probe blocked without geometry verdict：{failure}");
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
            progress?.Report($"Probe fail-closed：{failure}");
        }
        finally
        {
            try
            {
                if (baseline is not null)
                {
                    await RestoreOwnedShortcutsAsync(
                        baseline,
                        shortcuts,
                        sessions,
                        expectedProbePositions,
                        moves,
                        cleanup,
                        progress);
                    var restoreIssue = cleanup.FirstOrDefault(static item => !item.RestoreExact);
                    if (restoreIssue is not null)
                    {
                        var text = $"专用快捷方式恢复未完整通过：{restoreIssue.ShortcutStableId}: {restoreIssue.Issue}";
                        failure = string.IsNullOrWhiteSpace(failure) ? text : $"{failure} | {text}";
                    }
                }

                if (shortcuts.Length > 0)
                {
                    await _shortcutStore.DeleteAsync(shortcuts, CancellationToken.None);
                    shortcutsDeleted = true;
                }

                if (baseline is not null)
                {
                    var ownedPaths = new HashSet<string>(StringComparer.Ordinal);
                    var finalObservation = await WaitForOwnedShortcutsGoneAsync(
                        baseline,
                        shortcuts,
                        CancellationToken.None);
                    var diff = DiffUserIcons(baseline, finalObservation, ownedPaths);
                    if (diff.IsClean)
                    {
                        userIconsUnchanged = true;
                    }
                    else if (diff.Missing == 0 && diff.Extra == 0)
                    {
                        // 真机实测（2026-10-14）：Probe 的连续写入会让 Explorer 重排桌面图标流——
                        // 整个左侧任务栏条带里的 46 个既有图标整体前移一个槽位，而删掉专用快捷方式
                        // 并不会让它自己复位（00:23 的 144 图标快照与 01:00 的对比：46 个图标各移位
                        // 一个槽位，x=14 列末尾的两个图标折到 x=90 列开头）。Explorer 的行为改不了，
                        // 但"运行结束时不改变用户的桌面"是探针的职责：这里把被挪动的图标放回运行前的
                        // 位置，再用一次新的稳定 Observation 复核；复核不通过就是 Failed，绝不静默放过。
                        progress?.Report(
                            $"检测到 {diff.Displaced.Count} 个既有桌面项目被 Explorer 的布局回写挪动；" +
                            "正在恢复到运行前的位置。");
                        foreach (var item in diff.Displaced)
                        {
                            var moveResult = await _staExecutor.RunAsync(
                                () => _writer.MoveDesktopIcon(item.StableId, item.Baseline));
                            if (string.Equals(moveResult.StableId, item.StableId, StringComparison.Ordinal) &&
                                moveResult.RequestedPosition == item.Baseline)
                            {
                                userIconsRepairedCount++;
                            }
                        }

                        var repairedObservation = await WaitForOwnedShortcutsGoneAsync(
                            baseline,
                            shortcuts,
                            CancellationToken.None);
                        var remaining = DiffUserIcons(baseline, repairedObservation, ownedPaths);
                        userIconsUnchanged = remaining.IsClean;
                        unrepairedUserIconCount = remaining.Displaced.Count + remaining.Missing + remaining.Extra;
                        if (!userIconsUnchanged)
                        {
                            var text =
                                $"既有桌面项目被 Explorer 挪动后未能全部恢复到运行前的位置：" +
                                $"仍偏离 {remaining.Displaced.Count} 个，缺失 {remaining.Missing} 个，" +
                                $"新增 {remaining.Extra} 个。";
                            failure = string.IsNullOrWhiteSpace(failure) ? text : $"{failure} | {text}";
                            progress?.Report(text);
                        }
                    }
                    else
                    {
                        var text =
                            $"既有桌面项目集合在 Probe 运行期间发生变化：缺失 {diff.Missing} 个，新增 {diff.Extra} 个；" +
                            "无法确认桌面已恢复到运行前状态。";
                        unrepairedUserIconCount = diff.Missing + diff.Extra + diff.Displaced.Count;
                        failure = string.IsNullOrWhiteSpace(failure) ? text : $"{failure} | {text}";
                        progress?.Report(text);
                    }
                }
            }
            catch (Exception cleanupException)
            {
                var cleanupFailure = $"清理/最终核验失败：{cleanupException.GetType().Name}: {cleanupException.Message}";
                failure = string.IsNullOrWhiteSpace(failure)
                    ? cleanupFailure
                    : $"{failure} | {cleanupFailure}";
                progress?.Report(cleanupFailure);
            }
            finally
            {
                _runGate.Release();
            }
        }

        var measuredDisplays = displayResults.Where(static result => result.IsMeasured).ToArray();
        var unmeasuredDisplays = displayResults.Where(static result => !result.IsMeasured).ToArray();

        // spacing 与逐屏相位只在真正测到的屏上聚合。未实测屏没有 spacing/phase 可言，
        // 不能让它们的占位值（ObservedSpacingConstant: false）否决别的屏已经测到的事实。
        var sharedSpacing = measuredDisplays.Length > 0 &&
            measuredDisplays.Select(static result => result.Spacing).Distinct().Count() == 1;
        var phaseVerifiedPerDisplay = measuredDisplays.Length > 0 &&
            measuredDisplays.All(static result => result.PhaseVerified);
        var complete = baseline is not null && displayResults.Count == baseline.Displays.Count;

        // 一个屏都没测到：整轮 Failed 必须带上"为什么"，否则报告里只剩一个空的原因。
        if (failure is null && measuredDisplays.Length == 0 && unmeasuredDisplays.Length > 0)
        {
            failure =
                "所有显示器都没有拿到实测样本，本轮不构成任何事实：" +
                string.Join(
                    "；",
                    unmeasuredDisplays.Select(static result =>
                        $"{result.DeviceName}（{result.UnmeasuredReason ?? "原因未记录"}）"));
        }

        // 逐屏判定：测到的屏写入事实，测不到的屏由策略层拒绝执行（绝不静默降级）。
        // 只有"一个合格样本都没有"或安全网（恢复 / 清理 / 既有图标）不成立时，整轮才 Failed。
        var status = cancelled
            ? ExplorerGridProbeRunStatus.Cancelled
            : blocked
                ? ExplorerGridProbeRunStatus.Blocked
                : failure is not null || !complete || !shortcutsDeleted || !userIconsUnchanged ||
                  measuredDisplays.Length == 0
                    ? ExplorerGridProbeRunStatus.Failed
                    : unmeasuredDisplays.Length == 0
                        ? ExplorerGridProbeRunStatus.Passed
                        : ExplorerGridProbeRunStatus.Partial;

        var topologyIdentity = baseline is null
            ? null
            : ExplorerGridTopologyIdentity.Compute(
                baseline.PositioningCapabilities.IsAutoArrangeEnabled,
                baseline.PositioningCapabilities.Spacing,
                baseline.Displays);

        return new ExplorerGridPhysicalProbeReport(
            status,
            startedAt,
            _timeProvider.GetUtcNow(),
            Array.AsReadOnly(displayResults.ToArray()),
            Array.AsReadOnly(moves.ToArray()),
            Array.AsReadOnly(passiveSamples.ToArray()),
            Array.AsReadOnly(cleanup.ToArray()),
            sharedSpacing,
            phaseVerifiedPerDisplay,
            shortcutsDeleted,
            userIconsUnchanged,
            failure)
        {
            TopologyIdentity = topologyIdentity,
            RepairedUserIconCount = userIconsRepairedCount,
            UnrepairedUserIconCount = unrepairedUserIconCount,
        };
    }

    private async Task<SlotLanding> ObservePlannedSlotAsync(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        Dictionary<string, PixelPoint> expectedProbePositions,
        ProbeSession session,
        ExplorerGridProbeMoveKind kind,
        int ordinal,
        ExplorerGridProbeSlot slot,
        List<ExplorerGridProbeMove> moves,
        List<ExplorerGridProbePassiveSample> passiveSamples,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (slot.OccupantStableId is null)
        {
            return await ExecuteMoveAsync(
                baseline,
                shortcuts,
                expectedProbePositions,
                session,
                kind,
                ordinal,
                slot.Position,
                requireExact: true,
                moves,
                progress,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observed = ResolveExpectedOccupantPosition(
            baseline,
            expectedProbePositions,
            slot.OccupantStableId);
        if (observed != slot.Position)
        {
            throw new ProbeAbortException(
                $"被动占位样本 {slot.OccupantStableId} 在读取前发生变化：" +
                $"expected=({slot.Position.X},{slot.Position.Y}) " +
                $"observed=({observed.X},{observed.Y})。");
        }

        passiveSamples.Add(new ExplorerGridProbePassiveSample(
            session.Display.DeviceName,
            kind,
            ordinal,
            observed,
            slot.OccupantStableId));
        progress?.Report(
            $"{session.Display.DeviceName} {kind}[{ordinal}]: " +
            $"occupied=({observed.X},{observed.Y})；只读采用既有占位样本，未调用 Writer。");
        return new SlotLanding(observed, IsExact: true, Retries: 0);
    }

    /// <summary>
    /// 一次落点的结果：<see cref="Observed"/> 是 settled Observation 的实际位置；
    /// <see cref="IsExact"/> 为 false 表示 Explorer 把探针快捷方式放到了别处（重试后仍如此）。
    /// </summary>
    private sealed record SlotLanding(PixelPoint Observed, bool IsExact, int Retries);

    private async Task<SlotLanding> ExecuteMoveAsync(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        Dictionary<string, PixelPoint> expectedProbePositions,
        ProbeSession session,
        ExplorerGridProbeMoveKind kind,
        int ordinal,
        PixelPoint requested,
        bool requireExact,
        List<ExplorerGridProbeMove> moves,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTopologyUnchanged(baseline.Displays);
        if (!session.Display.WorkArea.Contains(requested))
        {
            throw new ProbeAbortException(
                $"{session.Display.DeviceName} {kind}[{ordinal}] 请求位置越出 WorkArea：" +
                $"({requested.X},{requested.Y})。");
        }
        EnsureLandingSlotFree(
            baseline,
            expectedProbePositions,
            session.StableId,
            requested,
            baseline.PositioningCapabilities.Spacing);

        var result = await _staExecutor.RunAsync(
            () => _writer.MoveDesktopIcon(session.StableId, requested));
        var moveIndex = moves.Count;
        moves.Add(new ExplorerGridProbeMove(
            session.Display.DeviceName,
            session.StableId,
            kind,
            ordinal,
            result.PreviousPosition,
            result.RequestedPosition,
            result.ActualPosition,
            result.IsExact));
        progress?.Report(
            $"{session.Display.DeviceName} {kind}[{ordinal}]: " +
            $"requested=({requested.X},{requested.Y}) " +
            $"immediateObserved=({result.ActualPosition.X},{result.ActualPosition.Y})；等待 Explorer settled Observation…");

        if (!string.Equals(result.StableId, session.StableId, StringComparison.Ordinal))
            throw new ProbeAbortException("Writer 返回了不同的 StableId。");
        if (result.PreviousPosition != session.CurrentPosition)
        {
            // 真机证据（2026-10-14 第 7/8 次运行）：Explorer 的布局回写会自行把本项目自己的专用快捷方式
            // 挪到别处，于是"移动前它应该在哪"这个预期会过期。Writer 报告的 PreviousPosition 就是它此刻的
            // 真实位置，以它为准继续；这次真实位移仍如实记入上面 moves 的 previous 列，不会丢证据。
            progress?.Report(
                $"{session.Display.DeviceName} {kind}[{ordinal}]: 专用快捷方式在移动前已被 Explorer 挪到 " +
                $"({result.PreviousPosition.X},{result.PreviousPosition.Y})；" +
                $"预期 ({session.CurrentPosition.X},{session.CurrentPosition.Y})，以实测位置为准继续。");
            session.CurrentPosition = result.PreviousPosition;
        }
        if (result.RequestedPosition != requested)
            throw new ProbeAbortException("Writer 返回的 RequestedPosition 与调用参数不一致。");
        if (!session.Display.WorkArea.Contains(result.ActualPosition))
        {
            throw new ProbeAbortException(
                $"Explorer observed 位置越出目标 WorkArea：({result.ActualPosition.X},{result.ActualPosition.Y})。");
        }
        session.WasMoved = true;
        var observation = await CaptureStableRequiredAsync(
            $"{session.Display.DeviceName} {kind}[{ordinal}] settled Observation",
            new HashSet<string>([session.StableId], StringComparer.Ordinal),
            ToleratedExistingItemStableIds(baseline, shortcuts),
            cancellationToken);
        var settled = ValidateSettledProbeObservation(
            baseline,
            observation,
            shortcuts,
            expectedProbePositions,
            session.StableId);
        var settledExact = settled == requested;
        moves[moveIndex] = moves[moveIndex] with
        {
            SettledObserved = settled,
            SettledIsExact = settledExact,
        };
        progress?.Report(
            $"{session.Display.DeviceName} {kind}[{ordinal}]: " +
            $"settledObserved=({settled.X},{settled.Y}) settledExact={settledExact}");

        if (!session.Display.WorkArea.Contains(settled))
        {
            // 真机实测：Explorer 会把探针快捷方式的 settled 落点回写到别的显示器
            // （DISPLAY2 请求 (-540,730) 立即落点正确，settled 却是 DISPLAY1 的 (2,702)）。
            // 这不是几何矛盾，只是"这一格没测到"：位置安全性仍由 ValidateSettledProbeObservation 保证，
            // 因此继续走"重试一次 → 仍偏离则丢弃该样本"的路径，而不是中止整轮。
            progress?.Report(
                $"Explorer settled 落点越出目标 WorkArea：请求 ({requested.X},{requested.Y})，" +
                $"实测 ({settled.X},{settled.Y})；按未测到处理。");
        }

        session.CurrentPosition = settled;
        expectedProbePositions[session.StableId] = settled;

        if (!requireExact || settledExact)
            return new SlotLanding(settled, settledExact, Retries: 0);

        // Explorer 会异步回写它自己保存的桌面布局：在目标机器上实测到它把探针快捷方式挪回"记住"的槽位，
        // 甚至挪到另一套旧晶格上（请求 (1066,702) 却落在 (1002,688)）。落点偏离请求本身不是几何矛盾：
        // 安全条件已由 ValidateSettledProbeObservation 检查过（用户图标未动、其他探针未动、仍在 WorkArea 内），
        // 所以这里只把偏离当作"这一格没测到"——重试一次，仍偏离就丢弃该样本并继续扫描。
        // 证据仍然只来自精确落点（精确落点的相位由 EnsurePhase 校验，矛盾会立即停止），
        // 事实写入门槛（每轴 ≥3 个实测样本）也不变。
        progress?.Report(
            $"{session.Display.DeviceName} {kind}[{ordinal}]: " +
            $"settled 落在 ({settled.X},{settled.Y}) 而不是请求槽位 ({requested.X},{requested.Y})，重试一次…");
        var retry = await ExecuteMoveAsync(
            baseline,
            shortcuts,
            expectedProbePositions,
            session,
            kind,
            ordinal,
            requested,
            requireExact: false,
            moves,
            progress,
            cancellationToken);

        if (retry.Observed == requested)
            return new SlotLanding(requested, IsExact: true, Retries: 1);

        progress?.Report(
            $"{session.Display.DeviceName} {kind}[{ordinal}]: " +
            $"重试后仍被 Explorer 放到 ({retry.Observed.X},{retry.Observed.Y})（请求 ({requested.X},{requested.Y})），" +
            $"跳过该落点，不计入样本。");
        return new SlotLanding(retry.Observed, IsExact: false, Retries: 1);
    }

    private async Task RestoreOwnedShortcutsAsync(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        IReadOnlyList<ProbeSession> sessions,
        Dictionary<string, PixelPoint> expectedProbePositions,
        List<ExplorerGridProbeMove> moves,
        ICollection<ExplorerGridProbeCleanupResult> cleanup,
        IProgress<string>? progress)
    {
        var topologySafe = IsTopologyUnchanged(baseline.Displays);
        foreach (var session in sessions.Reverse())
        {
            if (!session.WasMoved)
            {
                cleanup.Add(new ExplorerGridProbeCleanupResult(
                    session.StableId,
                    session.OriginalPosition,
                    null,
                    RestoreAttempted: false,
                    RestoreExact: true,
                    Issue: null));
                continue;
            }

            if (!topologySafe)
            {
                cleanup.Add(new ExplorerGridProbeCleanupResult(
                    session.StableId,
                    session.OriginalPosition,
                    null,
                    RestoreAttempted: false,
                    RestoreExact: false,
                    Issue: "显示拓扑已变化；为避免在旧坐标系继续写入，跳过位置恢复并仅删除专用快捷方式。"));
                continue;
            }

            try
            {
                var result = await _staExecutor.RunAsync(
                    () => _writer.MoveDesktopIcon(session.StableId, session.OriginalPosition));
                var moveIndex = moves.Count;
                moves.Add(new ExplorerGridProbeMove(
                    session.Display.DeviceName,
                    session.StableId,
                    ExplorerGridProbeMoveKind.Restore,
                    0,
                    result.PreviousPosition,
                    result.RequestedPosition,
                    result.ActualPosition,
                    result.IsExact));
                var stableObservation = await CaptureStableRequiredAsync(
                    $"{session.Display.DeviceName} Restore settled Observation",
                    new HashSet<string>([session.StableId], StringComparer.Ordinal),
                    ToleratedExistingItemStableIds(baseline, shortcuts),
                    CancellationToken.None);
                var settled = ValidateSettledProbeObservation(
                    baseline,
                    stableObservation,
                    shortcuts,
                    expectedProbePositions,
                    session.StableId);
                var settledExact = settled == session.OriginalPosition;
                moves[moveIndex] = moves[moveIndex] with
                {
                    SettledObserved = settled,
                    SettledIsExact = settledExact,
                };
                expectedProbePositions[session.StableId] = settled;
                cleanup.Add(new ExplorerGridProbeCleanupResult(
                    session.StableId,
                    session.OriginalPosition,
                    settled,
                    RestoreAttempted: true,
                    RestoreExact: settledExact,
                    Issue: settledExact
                        ? null
                        : "Explorer settled Observation 未精确恢复原位置；随后仍会删除本次专用快捷方式。"));
                progress?.Report(
                    $"恢复 {session.StableId}: requested=({session.OriginalPosition.X},{session.OriginalPosition.Y}) " +
                    $"immediateObserved=({result.ActualPosition.X},{result.ActualPosition.Y}) " +
                    $"settledObserved=({settled.X},{settled.Y})");
            }
            catch (Exception exception)
            {
                cleanup.Add(new ExplorerGridProbeCleanupResult(
                    session.StableId,
                    session.OriginalPosition,
                    null,
                    RestoreAttempted: true,
                    RestoreExact: false,
                    Issue: $"{exception.GetType().Name}: {exception.Message}"));
            }
        }
    }

    private async Task<DesktopObservation> WaitForOwnedShortcutsAsync(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        CancellationToken cancellationToken)
    {
        string? lastIssue = null;
        for (var attempt = 0; attempt < ShortcutObservationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = await _observationSource.CaptureAsync(cancellationToken);
            if (capture.IsCaptured)
            {
                try
                {
                    var owned = ResolveOwnedShortcuts(capture.Observation!, shortcuts);
                    EnsureUserIconSetIsIntact(baseline, capture.Observation!, owned.Keys);
                    var allowedTransitions = owned.Values
                        .Select(static icon => icon.StableId)
                        .ToHashSet(StringComparer.Ordinal);
                    var settled = await CaptureStableRequiredAsync(
                        "专用测试快捷方式初始 settled Observation",
                        allowedTransitions,
                        ToleratedExistingItemStableIds(baseline, shortcuts),
                        cancellationToken);
                    var settledOwned = ResolveOwnedShortcuts(settled, shortcuts);
                    EnsureUserIconSetIsIntact(baseline, settled, settledOwned.Keys);
                    return settled;
                }
                catch (OwnedShortcutNotReadyException exception)
                {
                    lastIssue = exception.Message;
                }
            }
            else
            {
                lastIssue = string.Join(" | ", capture.Issues.Select(static issue => issue.Message));
            }

            await Task.Delay(ShortcutObservationDelay, cancellationToken);
        }

        throw new ProbeAbortException(
            $"Explorer 未在限定时间内完整呈现本次专用测试快捷方式。最后状态：{lastIssue ?? "未知"}");
    }

    private async Task<DesktopObservation> WaitForOwnedShortcutsGoneAsync(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        CancellationToken cancellationToken)
    {
        var ownedPaths = shortcuts
            .Select(shortcut => NormalizePath(shortcut.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? lastIssue = null;
        for (var attempt = 0; attempt < ShortcutObservationAttempts; attempt++)
        {
            var capture = await _observationSource.CaptureAsync(cancellationToken);
            if (capture.IsCaptured)
            {
                var remaining = capture.Observation!.Icons
                    .Where(icon => TryNormalizePath(icon.ParsingName, out var path) && ownedPaths.Contains(path))
                    .ToArray();
                if (remaining.Length == 0)
                    return capture.Observation!;
                lastIssue = $"仍观察到 {remaining.Length} 个专用快捷方式。";
            }
            else
            {
                lastIssue = string.Join(" | ", capture.Issues.Select(static issue => issue.Message));
            }

            await Task.Delay(ShortcutObservationDelay, cancellationToken);
        }

        throw new ProbeAbortException(
            $"专用测试快捷方式删除后的 Explorer 核验超时。最后状态：{lastIssue ?? "未知"}");
    }

    private async Task<DesktopObservation> CaptureStableRequiredAsync(
        string stage,
        IReadOnlySet<string>? expectedTransitionStableIds,
        IReadOnlySet<string>? toleratedExistingItemStableIds,
        CancellationToken cancellationToken)
    {
        string? lastExpectedTransition = null;
        for (var attempt = 0; attempt < SettledObservationAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(ShortcutObservationDelay, _timeProvider, cancellationToken);

            var result = await _stableObservationSource.CaptureStableAsync(cancellationToken);
            if (result.IsStable)
                return result.StableObservation!;

            if (result.Status == StableDesktopObservationStatus.Unavailable)
            {
                throw new ProbeAbortException(
                    $"{stage} unavailable：" +
                    string.Join(
                        " | ",
                        result.CaptureFailures.Select(failure =>
                            $"{failure.Phase}: {failure.Issue.Message}")));
            }

            // 可以继续等待的两种变化：
            //   (a) 刚刚被移动的专用快捷方式仍在落位（expectedTransitionStableIds）；
            //   (b) 既有桌面项目被 Explorer 的布局回写挪动（toleratedExistingItemStableIds）——这是真机上
            //       反复出现的环境行为，位移会在运行结束时被自愈恢复并复核，不该让整轮扫描作废。
            // 项目集合变化（IconAdded / IconRemoved）与任何其它变化仍然立即失败。
            var onlyExpectedTransition =
                (expectedTransitionStableIds is not null || toleratedExistingItemStableIds is not null) &&
                result.StabilityIssues.All(issue =>
                    issue.Code == DesktopObservationStabilityIssueCode.IconChanged &&
                    issue.StableId is not null &&
                    ((expectedTransitionStableIds?.Contains(issue.StableId) ?? false) ||
                     (toleratedExistingItemStableIds?.Contains(issue.StableId) ?? false)));
            if (!onlyExpectedTransition)
            {
                throw new ProbeAbortException(
                    $"{stage} 检测到非预期桌面变化：" +
                    string.Join(" | ", result.StabilityIssues.Select(static issue => issue.Message)));
            }

            lastExpectedTransition = string.Join(
                " | ",
                result.StabilityIssues.Select(static issue => issue.Message));
        }

        throw new ProbeAbortException(
            $"{stage} 在 {SettledObservationAttempts} 轮双采样内未稳定。" +
            $"最后变化：{lastExpectedTransition ?? "未知"}");
    }

    /// <summary>
    /// baseline 在创建专用快捷方式之前冻结，因此它只包含既有桌面项目。真机证据表明 Explorer 的布局回写
    /// 会在一次运行中同时挪动既有项目和本项目自己创建的其它专用快捷方式（2026-10-14 第 7/8 次运行：
    /// DISPLAY3 的 seed 落位期间 39 个项目一起位移）；这些位移会在运行结束时被自愈恢复并复核，
    /// 所以扫描期间可以继续等待而不是中止。集合变化（IconAdded / IconRemoved）仍然立即失败。
    /// </summary>
    private static HashSet<string> ToleratedExistingItemStableIds(
        DesktopObservation baseline,
        IReadOnlyList<ExplorerGridProbeShortcut>? probeShortcuts = null)
    {
        var tolerated = new HashSet<string>(
            baseline.Icons.Select(static icon => icon.StableId),
            StringComparer.OrdinalIgnoreCase);

        if (probeShortcuts is not null)
        {
            foreach (var shortcut in probeShortcuts)
                tolerated.Add(NormalizePath(shortcut.FilePath));
        }

        return tolerated;
    }

    private static void ValidateInitialObservation(DesktopObservation observation)
    {
        if (observation.Displays.Count == 0)
            throw new ProbeAbortException("未观察到显示器。");
        if (observation.PositioningCapabilities.IsAutoArrangeEnabled)
            throw new ProbeAbortException("桌面启用了自动排列图标；Physical Probe 拒绝写入。");
        if (observation.Displays.Count(static display => display.IsPrimary) != 1)
            throw new ProbeAbortException("显示拓扑必须恰好包含一块主显示器。");
    }

    private static PixelPoint ValidateSettledProbeObservation(
        DesktopObservation baseline,
        DesktopObservation current,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
        Dictionary<string, PixelPoint> expectedProbePositions,
        string movingStableId)
    {
        if (!DisplayTopologyComparer.AreEquivalent(baseline.Displays, current.Displays))
            throw new ProbeAbortException("显示拓扑在 Probe 运行期间发生变化。");
        if (current.PositioningCapabilities.IsAutoArrangeEnabled ||
            current.PositioningCapabilities.Spacing != baseline.PositioningCapabilities.Spacing)
        {
            throw new ProbeAbortException("Explorer 定位能力在 Probe 运行期间发生变化。");
        }

        var owned = ResolveOwnedShortcuts(current, shortcuts);
        EnsureUserIconSetIsIntact(baseline, current, owned.Keys);
        PixelPoint? settled = null;
        foreach (var icon in owned.Values)
        {
            if (string.Equals(icon.StableId, movingStableId, StringComparison.Ordinal))
            {
                settled = icon.PixelPosition;
                continue;
            }

            // 真机上 Explorer 的布局回写会自行把本项目其它专用快捷方式一起挪走（2026-10-14 真机运行）。
            // 这不是 Writer 的错误——Writer 的返回契约（StableId / PreviousPosition / RequestedPosition）
            // 已在 ExecuteMoveAsync 中逐项核对——因此以本次观察到的位置为新基准继续扫描。
            // 真实位移仍完整记录在 Moves 的 previous 列与 cleanup 结果里。
            expectedProbePositions[icon.StableId] = icon.PixelPosition;
        }

        return settled ?? throw new ProbeAbortException(
            $"settled Observation 未包含当前专用快捷方式 {movingStableId}。");
    }

    private static Dictionary<string, DesktopIconDescriptor> ResolveOwnedShortcuts(
        DesktopObservation observation,
        IReadOnlyList<ExplorerGridProbeShortcut> shortcuts)
    {
        var result = new Dictionary<string, DesktopIconDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in shortcuts)
        {
            var path = NormalizePath(shortcut.FilePath);
            var matches = observation.Icons
                .Where(icon => TryNormalizePath(icon.ParsingName, out var iconPath) &&
                    string.Equals(iconPath, path, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
                throw new OwnedShortcutNotReadyException($"尚未观察到 {path}。");
            if (matches.Length != 1)
                throw new ProbeAbortException($"专用快捷方式 {path} 在 Observation 中不唯一。");

            var icon = matches[0];
            if (!icon.IsFileSystemItem || icon.IsSystemItem)
                throw new ProbeAbortException($"专用快捷方式 {path} 的 Shell 身份不符合预期。");
            result.Add(path, icon);
        }

        return result;
    }

    /// <summary>运行前后被挪动的既有桌面项目（只用于"恢复到运行前位置"，不用于测量）。</summary>
    private sealed record DisplacedUserIcon(
        string StableId,
        string DisplayName,
        PixelPoint Baseline,
        PixelPoint Observed);

    /// <summary>既有桌面项目在运行前后的差异。</summary>
    private sealed record UserIconDiff(IReadOnlyList<DisplacedUserIcon> Displaced, int Missing, int Extra)
    {
        public bool IsClean => Displaced.Count == 0 && Missing == 0 && Extra == 0;
    }

    /// <summary>
    /// 比较运行前后的既有桌面项目集合与位置，只给差异、不做判断：
    /// 位置被挪动的项目、运行前存在但现在已不在的项目（缺失）、运行后新增的项目。
    /// </summary>
    private static UserIconDiff DiffUserIcons(
        DesktopObservation baseline,
        DesktopObservation current,
        IReadOnlyCollection<string> ownedPaths)
    {
        var currentUserIcons = current.Icons
            .Where(icon => !TryNormalizePath(icon.ParsingName, out var path) || !ownedPaths.Contains(path))
            .ToDictionary(static icon => icon.StableId, StringComparer.Ordinal);
        var displaced = new List<DisplacedUserIcon>();
        var missing = 0;
        foreach (var original in baseline.Icons)
        {
            if (!currentUserIcons.TryGetValue(original.StableId, out var observed))
            {
                missing++;
                continue;
            }

            if (observed.PixelPosition != original.PixelPosition)
            {
                displaced.Add(new DisplacedUserIcon(
                    original.StableId,
                    original.DisplayName,
                    original.PixelPosition,
                    observed.PixelPosition));
            }
        }

        var extra = currentUserIcons.Count - (baseline.Icons.Count - missing);
        return new UserIconDiff(displaced, missing, extra < 0 ? 0 : extra);
    }

    /// <summary>
    /// 运行中途的检查：既有桌面项目的<b>集合</b>不能被改变——缺失或新增都说明有 Probe 之外的东西在动桌面。
    /// 位置漂移刻意不在这里中止：真机实测（2026-10-14）证明 Explorer 会在探针连续写入的过程中把图标流
    /// 整体前移一个槽位（左侧任务栏条带里 46 个图标 × 一个间距），这是环境行为而不是几何矛盾。
    /// 每一轮结束时由收尾的"恢复+复核"把它们放回运行前的位置，复核不通过才判 Failed；
    /// 中途中止只会让一次本来能自愈的运行白跑，并且把桌面留在一个已被挪动的中间状态。
    /// </summary>
    private static void EnsureUserIconSetIsIntact(
        DesktopObservation baseline,
        DesktopObservation current,
        IReadOnlyCollection<string> ownedPaths)
    {
        var diff = DiffUserIcons(baseline, current, ownedPaths);
        if (diff.Missing > 0 || diff.Extra > 0)
        {
            throw new ProbeAbortException(
                $"既有桌面项目集合在 Probe 运行期间发生变化：缺失 {diff.Missing} 个，新增 {diff.Extra} 个。");
        }
    }

    private void EnsureTopologyUnchanged(IReadOnlyList<DisplayDescriptor> baseline)
    {
        if (!IsTopologyUnchanged(baseline))
            throw new ProbeAbortException("显示拓扑在写入前发生变化；立即停止。");
    }

    private bool IsTopologyUnchanged(IReadOnlyList<DisplayDescriptor> baseline)
    {
        try
        {
            return DisplayTopologyComparer.AreEquivalent(baseline, _topologyReader.GetDisplays());
        }
        catch
        {
            return false;
        }
    }

    private static void EnsurePhase(
        PixelPoint expected,
        PixelPoint spacing,
        PixelPoint observed,
        string display,
        string axis,
        int index)
    {
        var actual = ExplorerGridProbePlan.PhaseOf(observed, spacing);
        if (actual != expected)
        {
            throw new ProbeAbortException(
                $"{display} {axis}[{index}] observed phase=({actual.X},{actual.Y}) " +
                $"不等于 seed phase=({expected.X},{expected.Y})。");
        }
    }

    /// <summary>
    /// 校验同一轴的落点仍然构成同一条 native-slot 线：同一个 Y，X 方向相邻落点的间距必须是 spacing.X 的
    /// 正数整数倍，并且至少有一对相邻落点正好相差一个 spacing.X（间距本身被直接观测到）。
    /// 允许"缺口"是因为 Explorer 偶尔会异步回写它自己保存的桌面布局，把某一次落点放回它记得的槽位；
    /// 那次落点已被丢弃（见 SkippedLandingCount），缺口本身不算间距证据，但也不该让后面已经测准的样本作废。
    /// </summary>
    private static void EnsureHorizontalSpacing(
        IReadOnlyCollection<PixelPoint> observations,
        PixelPoint spacing,
        string display)
    {
        var ordered = observations.OrderBy(static point => point.X).ToArray();
        var sawOneSpacing = false;
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var delta = current.X - previous.X;
            if (current.Y != previous.Y || delta <= 0 || delta % spacing.X != 0)
            {
                throw new ProbeAbortException(
                    $"{display} observed horizontal spacing 非恒定：" +
                    $"previous=({previous.X},{previous.Y}) current=({current.X},{current.Y}) " +
                    $"expectedDelta=({spacing.X},0) 的正数整数倍。");
            }

            sawOneSpacing |= delta == spacing.X;
        }

        if (ordered.Length >= 3 && !sawOneSpacing)
        {
            throw new ProbeAbortException(
                $"{display} observed horizontal spacing 非恒定：相邻落点间距都不是一个 " +
                $"spacing=({spacing.X},0)，无法证明网格间距。");
        }
    }

    /// <summary>纵向版 <see cref="EnsureHorizontalSpacing"/>：同一个 X，Y 方向间距为 spacing.Y 的正数整数倍。</summary>
    private static void EnsureVerticalSpacing(
        IReadOnlyCollection<PixelPoint> observations,
        PixelPoint spacing,
        string display)
    {
        var ordered = observations.OrderBy(static point => point.Y).ToArray();
        var sawOneSpacing = false;
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var delta = current.Y - previous.Y;
            if (current.X != previous.X || delta <= 0 || delta % spacing.Y != 0)
            {
                throw new ProbeAbortException(
                    $"{display} observed vertical spacing 非恒定：" +
                    $"previous=({previous.X},{previous.Y}) current=({current.X},{current.Y}) " +
                    $"expectedDelta=(0,{spacing.Y}) 的正数整数倍。");
            }

            sawOneSpacing |= delta == spacing.Y;
        }

        if (ordered.Length >= 3 && !sawOneSpacing)
        {
            throw new ProbeAbortException(
                $"{display} observed vertical spacing 非恒定：相邻落点间距都不是一个 " +
                $"spacing=(0,{spacing.Y})，无法证明网格间距。");
        }
    }

    private static IEnumerable<(ExplorerGridProbeSlot Slot, int Ordinal)> EnumerateCenterOut(
        IReadOnlyList<ExplorerGridProbeSlot> slots,
        PixelPoint seed) =>
        slots
            .Select(static (slot, ordinal) => (Slot: slot, Ordinal: ordinal))
            .OrderBy(entry =>
                Math.Abs((long)entry.Slot.Position.X - seed.X) +
                Math.Abs((long)entry.Slot.Position.Y - seed.Y))
            .ThenBy(static entry => entry.Ordinal);

    private static PixelPoint FindSafeSeedRequest(
        DesktopObservation baseline,
        IReadOnlyDictionary<string, PixelPoint> expectedProbePositions,
        ProbeSession session,
        PixelPoint spacing)
    {
        var occupied = GetOccupants(baseline, expectedProbePositions, session.StableId);
        var center = session.Display.WorkArea.Center;
        if (ExplorerGridProbePlan.IsLandingSlotFree(center, spacing, occupied))
            return center;

        // seed 候选槽位的相位必须来自"这块屏自己的既有图标晶格"，而不是探针项目自己的落点：
        // 真机实测（2026-10-14）DISPLAY2 的 seed 请求被 Explorer 放回 DISPLAY1 的 (2,702)，
        // 旧代码于是把 DISPLAY3 的候选槽位算到 (2,16) 晶格上，而该屏 46/48 个既有图标在 (35,84)
        // 晶格上——请求槽位与用户的图标流天生错位，该屏因此永远测不出（也永远不该写）事实。
        var lattice = ComputeIconLatticeConsensus(
            baseline,
            session.Display,
            spacing,
            expectedProbePositions);
        var phaseHint = lattice.IsReliable && lattice.Phase is { } latticePhase
            ? latticePhase
            : ExplorerGridProbePlan.PhaseOf(session.OriginalPosition, spacing);
        var candidates = EnumerateNativeSlots(session.Display, spacing, phaseHint)
            .Where(point => ExplorerGridProbePlan.IsLandingSlotFree(point, spacing, occupied))
            .OrderBy(point =>
                Math.Abs((long)point.X - center.X) + Math.Abs((long)point.Y - center.Y))
            .ToArray();
        return candidates.FirstOrDefault(new PixelPoint(int.MinValue, int.MinValue)) is var selected &&
            selected.X != int.MinValue
                ? selected
                : throw new ExplorerGridProbeBlockedException(
                    $"显示器 {session.Display.DeviceName} 找不到无占用的 seed 落点；" +
                    "为避免与既有桌面图标相撞，Physical Probe 以 Blocked 结束，不产生几何失败结论。");
    }

    private static void EnsureLandingSlotFree(
        DesktopObservation baseline,
        IReadOnlyDictionary<string, PixelPoint> expectedProbePositions,
        string movingStableId,
        PixelPoint requested,
        PixelPoint spacing)
    {
        var occupied = GetOccupants(baseline, expectedProbePositions, movingStableId);
        if (!ExplorerGridProbePlan.IsLandingSlotFree(requested, spacing, occupied))
        {
            throw new ProbeAbortException(
                $"请求落点 ({requested.X},{requested.Y}) 与既有桌面项目或其他测试快捷方式的占用矩形相撞；" +
                "Physical Probe 在调用 Writer 前停止。");
        }
    }

    private static IReadOnlyList<ExplorerGridProbeOccupant> GetOccupants(
        DesktopObservation baseline,
        IReadOnlyDictionary<string, PixelPoint> expectedProbePositions,
        string movingStableId) =>
        baseline.Icons
            .Select(static icon => new ExplorerGridProbeOccupant(icon.StableId, icon.PixelPosition))
            .Concat(expectedProbePositions
                .Where(pair => !string.Equals(pair.Key, movingStableId, StringComparison.Ordinal))
                .Select(static pair => new ExplorerGridProbeOccupant(pair.Key, pair.Value)))
            .ToArray();

    private static PixelPoint ResolveExpectedOccupantPosition(
        DesktopObservation baseline,
        IReadOnlyDictionary<string, PixelPoint> expectedProbePositions,
        string stableId)
    {
        var baselineIcon = baseline.Icons.FirstOrDefault(icon =>
            string.Equals(icon.StableId, stableId, StringComparison.Ordinal));
        if (baselineIcon is not null)
            return baselineIcon.PixelPosition;
        if (expectedProbePositions.TryGetValue(stableId, out var probePosition))
            return probePosition;
        throw new ProbeAbortException($"被动占位样本 {stableId} 已不在冻结的占用图中。");
    }

    private static IEnumerable<PixelPoint> EnumerateNativeSlots(
        DisplayDescriptor display,
        PixelPoint spacing,
        PixelPoint phase)
    {
        for (var y = AlignUpToPhase(display.WorkArea.Top, spacing.Y, phase.Y);
             (long)y + spacing.Y <= display.WorkArea.Bottom;
             y = checked(y + spacing.Y))
        {
            for (var x = AlignUpToPhase(display.WorkArea.Left, spacing.X, phase.X);
                 (long)x + spacing.X <= display.WorkArea.Right;
                 x = checked(x + spacing.X))
            {
                yield return new PixelPoint(x, y);
            }
        }
    }

    private static int AlignUpToPhase(int value, int spacing, int phase)
    {
        var remainder = value % spacing;
        if (remainder < 0)
            remainder += spacing;
        return checked(value + ((phase - remainder + spacing) % spacing));
    }

    private static IReadOnlyList<DisplayDescriptor> OrderDisplays(
        IReadOnlyList<DisplayDescriptor> displays)
    {
        var primary = displays.Single(static display => display.IsPrimary);
        return new[] { primary }
            .Concat(displays
                .Where(static display => !display.IsPrimary)
                .OrderBy(static display => display.Bounds.Left)
                .ThenBy(static display => display.Bounds.Top)
                .ThenBy(static display => display.DeviceName, StringComparer.Ordinal))
            .ToArray();
    }

    /// <summary>
    /// 用该屏既有桌面图标（排除探针自己的快捷方式）的晶格众数交叉校验相位。
    /// 只统计位于该屏 WorkArea 内的图标：相位描述的是图标左上角的落点，
    /// 工作区之外的图标（例如停留在任务栏条带里的）不属于该屏 native slot 晶格。
    /// 晶格不可信（样本不足或众数占比不足 60%）时返回 IsReliable=false，
    /// 此时相位只由探针自身落点判定，不额外拦截。
    /// </summary>
    private static ExplorerGridPhaseConsensus ComputeIconLatticeConsensus(
        DesktopObservation baseline,
        DisplayDescriptor display,
        PixelPoint spacing,
        IReadOnlyDictionary<string, PixelPoint> expectedProbePositions)
    {
        var positions = baseline.Icons
            .Where(icon => !expectedProbePositions.ContainsKey(icon.StableId))
            .Where(icon => display.WorkArea.Contains(icon.PixelPosition))
            .Select(icon => icon.PixelPosition)
            .ToArray();
        var inference = DesktopExplorerGridPhaseResolver.TryInfer(positions, spacing);
        return inference is null
            ? new ExplorerGridPhaseConsensus(false, null, 0, positions.Length)
            : new ExplorerGridPhaseConsensus(
                true,
                inference.Phase,
                inference.ConsensusCount,
                inference.SampleCount);
    }

    private ExplorerGridPhysicalProbeReport EmptyFailure(DateTimeOffset startedAt, string failure)
        => new(
            ExplorerGridProbeRunStatus.Failed,
            startedAt,
            _timeProvider.GetUtcNow(),
            Array.Empty<ExplorerGridProbeDisplayResult>(),
            Array.Empty<ExplorerGridProbeMove>(),
            Array.Empty<ExplorerGridProbePassiveSample>(),
            Array.Empty<ExplorerGridProbeCleanupResult>(),
            SharedSpacing: false,
            PhaseVerifiedPerDisplay: false,
            OwnedShortcutsDeleted: true,
            UserIconsUnchanged: true,
            failure);

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            normalized = string.Empty;
            return false;
        }

        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch
        {
            normalized = string.Empty;
            return false;
        }
    }

    private sealed class ProbeSession(
        DisplayDescriptor display,
        string stableId,
        PixelPoint originalPosition)
    {
        public DisplayDescriptor Display { get; } = display;
        public string StableId { get; } = stableId;
        public PixelPoint OriginalPosition { get; } = originalPosition;
        public PixelPoint CurrentPosition { get; set; } = originalPosition;
        public bool WasMoved { get; set; }
    }

    private sealed class ProbeAbortException(string message) : InvalidOperationException(message);
    private sealed class OwnedShortcutNotReadyException(string message) : Exception(message);
}
