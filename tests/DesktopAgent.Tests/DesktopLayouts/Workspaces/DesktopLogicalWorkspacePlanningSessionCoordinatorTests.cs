using System.Text.Json;
using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.DesktopLayouts.Workspaces;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopLogicalWorkspacePlanningSessionCoordinatorTests
{
    [Fact]
    public async Task Plan_ValidProviderPlan_ReturnsOpaqueSessionWithLocalAuthority()
    {
        var planner = new DelegatePlanner(request =>
            DesktopLogicalWorkspacePlanningProviderResult.Success(CreateKeepPlan(request.Workspace)));
        var coordinator = new DesktopLogicalWorkspacePlanningSessionCoordinator(planner);

        var result = await coordinator.PlanAsync(CreateObservation(), "保持当前位置。");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Session);
        Assert.Equal(1, result.Session.ProviderAttemptCount);
        Assert.Equal(result.Session.Workspace.ObservationId, result.Session.ValidatedPlan.SourceObservationId);
        Assert.Equal("secret-stable-a", result.Session.Authority.ResolveItem("item-0001").StableId);
        Assert.Equal(@"\\.\DISPLAY1", result.Session.Authority.ResolveDisplay("primary").DeviceName);

        var json = JsonSerializer.Serialize(result.Session);
        Assert.Contains("item-0001", json, StringComparison.Ordinal);
        Assert.Contains("secondary_1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-stable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DISPLAY2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GridOrigin", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PixelPosition", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_ProjectionFailure_DoesNotCallProviderOrRetainAuthority()
    {
        var planner = new DelegatePlanner(_ =>
            throw new Xunit.Sdk.XunitException("Projection 失败后不应调用 Provider。"));
        var coordinator = new DesktopLogicalWorkspacePlanningSessionCoordinator(planner);

        var result = await coordinator.PlanAsync(CreateObservation(icons: []), "整理桌面。");

        Assert.Equal(DesktopLogicalWorkspacePlanningSessionStatus.ProjectionFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        Assert.Null(result.PlanningRun);
        Assert.Contains(result.ProjectionIssues, static issue =>
            issue.Code == DesktopLogicalWorkspaceProjectionIssueCode.GridPhaseUnavailable);
        Assert.Equal(0, planner.CallCount);
    }

    [Fact]
    public async Task Plan_PlannerClaimsInvalidSuccess_SessionBoundaryRejectsIt()
    {
        var planner = new DelegatePlanner(request =>
            DesktopLogicalWorkspacePlanningProviderResult.Success(
                new DesktopDesiredWorkspacePlan(
                    Guid.NewGuid(),
                    request.Workspace.ObservationId,
                    request.Workspace.TopologyFingerprint,
                    request.Workspace.GridFingerprint,
                    items: [])));
        var coordinator = new DesktopLogicalWorkspacePlanningSessionCoordinator(planner);

        var result = await coordinator.PlanAsync(CreateObservation(), "整理桌面。");

        Assert.Equal(DesktopLogicalWorkspacePlanningSessionStatus.PlanningFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        Assert.NotNull(result.PlanningRun);
        Assert.True(result.PlanningRun.FinalResult.CanRetryValidation);
        Assert.Null(result.PlanningRun.FinalResult.ValidatedPlan);
        Assert.NotNull(result.PlanningRun.FinalResult.RejectedPlan);
        Assert.Contains(result.PlanningRun.FinalResult.ValidationIssues, static issue =>
            issue.Code == DesktopDesiredWorkspacePlanIssueCode.MissingItemRef);
    }

    [Fact]
    public async Task Plan_ProviderFailure_ReturnsNoSession()
    {
        var planner = new DelegatePlanner(_ =>
            DesktopLogicalWorkspacePlanningProviderResult.Failure(
                IntelligenceProviderStatus.Unavailable,
                "offline",
                503));
        var coordinator = new DesktopLogicalWorkspacePlanningSessionCoordinator(planner);

        var result = await coordinator.PlanAsync(CreateObservation(), "整理桌面。");

        Assert.Equal(DesktopLogicalWorkspacePlanningSessionStatus.PlanningFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        Assert.Empty(result.ProjectionIssues);
        Assert.Equal(IntelligenceProviderStatus.Unavailable, result.PlanningRun!.FinalResult.Status);
        Assert.Equal(503, result.PlanningRun.FinalResult.HttpStatusCode);
        Assert.Equal(1, result.PlanningRun.AttemptCount);
    }

    private static DesktopDesiredWorkspacePlan CreateKeepPlan(
        DesktopObservedLogicalWorkspace workspace) =>
        new(
            Guid.NewGuid(),
            workspace.ObservationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            items: workspace.Items.Select(static item =>
                DesktopDesiredWorkspaceItem.Keep(item.ItemRef)));

    private static DesktopObservation CreateObservation(
        IReadOnlyList<DesktopIconDescriptor>? icons = null) =>
        new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
            [
                new DisplayDescriptor(
                    @"\\.\DISPLAY1",
                    new PixelRect(-1_000, 0, 0, 600),
                    new PixelRect(-1_000, 0, 0, 600),
                    96,
                    96,
                    true),
                new DisplayDescriptor(
                    @"\\.\DISPLAY2",
                    new PixelRect(0, 0, 800, 600),
                    new PixelRect(0, 0, 800, 600),
                    144,
                    144,
                    false),
            ],
            new DesktopIconPositioningCapabilities(
                false,
                new PixelPoint(100, 100),
                ConnectionMethod.FindWindowSW,
                null),
            icons ??
            [
                new DesktopIconDescriptor(
                    "secret-stable-a",
                    "Secondary.txt",
                    @"C:\Secret\Secondary.txt",
                    ItemKind.File,
                    new PixelPoint(10, 20),
                    @"\\.\DISPLAY2",
                    false,
                    true),
                new DesktopIconDescriptor(
                    "secret-stable-b",
                    "Primary.txt",
                    @"C:\Secret\Primary.txt",
                    ItemKind.File,
                    new PixelPoint(-990, 20),
                    @"\\.\DISPLAY1",
                    false,
                    true),
                new DesktopIconDescriptor(
                    "secret-stable-system",
                    "回收站",
                    "::{645FF040-5081-101B-9F08-00AA002F954E}",
                    ItemKind.SystemItem,
                    new PixelPoint(-890, 120),
                    @"\\.\DISPLAY1",
                    true,
                    false),
            ],
            ConnectionMethod.FindWindowSW,
            TimeSpan.FromMilliseconds(10));

    private sealed class DelegatePlanner : IDesktopLogicalWorkspacePlanner
    {
        private readonly Func<
            DesktopLogicalWorkspacePlanningRequest,
            DesktopLogicalWorkspacePlanningProviderResult> _plan;

        internal int CallCount { get; private set; }

        internal DelegatePlanner(Func<
            DesktopLogicalWorkspacePlanningRequest,
            DesktopLogicalWorkspacePlanningProviderResult> plan) =>
            _plan = plan;

        public Task<DesktopLogicalWorkspacePlanningProviderResult> PlanAsync(
            DesktopLogicalWorkspacePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_plan(request));
        }
    }
}
