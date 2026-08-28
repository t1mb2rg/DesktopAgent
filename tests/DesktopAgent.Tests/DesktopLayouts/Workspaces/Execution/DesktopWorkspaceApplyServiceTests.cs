using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Equivalence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Preview;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;
using DesktopAgent.WindowsInterop.DesktopLayouts.Workspaces.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces.Execution;

public sealed class DesktopWorkspaceApplyServiceTests
{
    [Fact]
    public async Task Apply_LegacyRecoveryUnresolved_BlocksBeforeWorkspaceExecutionDelegate()
    {
        var confirmation = CreateConfirmation();
        var legacy = new FakeRecoveryStore(new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            Lifecycle = RecoverySessionLifecycle.Executing,
        });
        var executeCount = 0;
        var service = new DesktopWorkspaceApplyService(
            legacy,
            (_, _) =>
            {
                executeCount++;
                return Task.FromResult(DesktopWorkspaceExecutionRunResult.NoOp());
            });

        var result = await service.ApplyAsync(confirmation);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.FailedBeforeWrite, result.Status);
        Assert.False(result.WriterStarted);
        Assert.Equal("UnresolvedLegacyRecovery", result.ErrorType);
        Assert.Equal(0, executeCount);
        Assert.Equal(1, legacy.ListCount);
    }

    [Fact]
    public async Task Apply_NoLegacyRecovery_DelegatesOnceWithNonCancelableExecutionToken()
    {
        var confirmation = CreateConfirmation();
        var legacy = new FakeRecoveryStore();
        var executeCount = 0;
        bool? executionTokenCanBeCanceled = null;
        var service = new DesktopWorkspaceApplyService(
            legacy,
            (_, executionToken) =>
            {
                executeCount++;
                executionTokenCanBeCanceled = executionToken.CanBeCanceled;
                return Task.FromResult(DesktopWorkspaceExecutionRunResult.NoOp());
            });

        using var callerCancellation = new CancellationTokenSource();
        var result = await service.ApplyAsync(confirmation, callerCancellation.Token);

        Assert.Equal(DesktopWorkspaceExecutionRunStatus.NoOp, result.Status);
        Assert.Equal(1, executeCount);
        Assert.False(executionTokenCanBeCanceled);
    }

    private static DesktopWorkspaceApplyConfirmation CreateConfirmation()
    {
        var geometry = Geometry(Display("primary", "DISPLAY1", true, columns: 3, rows: 1));
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact(
                "a",
                "primary",
                new PixelPoint(80, 0),
                isMovable: true),
            new DesktopWorkspaceCompilationItemFact(
                "b",
                "primary",
                new PixelPoint(0, 0),
                isMovable: true),
        };
        var blueprint = new DesktopWorkspaceBlueprint(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            revision: 1,
            designSummary: "交换两个项目的位置。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    DesktopWorkspaceAnchorKind.TopLeft,
                    DesktopWorkspacePackingOrder.RowMajor),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("dev", "tools")]);
        var semantics = new DesktopSemanticUnderstanding(
            "两个开发项目。",
            [new DesktopSemanticCategory("dev", "开发", "开发工具")],
            facts.Select(fact => new DesktopSemanticAssignment(
                fact.StableId,
                "dev",
                1,
                "test")).ToArray());

        var compilation = DesktopWorkspaceCompiler.Compile(
            blueprint,
            semantics,
            geometry,
            facts);
        Assert.True(
            compilation.Succeeded,
            string.Join("；", compilation.Issues.Select(static issue => issue.Message)));
        var plan = Assert.IsType<CompiledWorkspacePlan>(compilation.Plan);
        var equivalence = DesktopWorkspaceSemanticEquivalenceChecker.Check(
            blueprint,
            geometry,
            plan,
            facts);
        Assert.True(
            equivalence.Equivalent,
            string.Join("；", equivalence.Issues.Select(static issue => issue.Message)));
        var certificate = Assert.IsType<WorkspaceRealizationCertificate>(equivalence.Certificate);
        var source = new DesktopWorkspacePreviewSourceSnapshot(
            geometry.ObservationId,
            facts,
            facts.Select(fact => new DesktopWorkspacePreviewItemPresentation(
                fact.StableId,
                fact.StableId.ToUpperInvariant())).ToArray());
        var preview = DesktopWorkspaceFrozenPreviewFactory.Create(
            blueprint,
            geometry,
            plan,
            certificate,
            source);

        return DesktopWorkspaceApplyConfirmation.Create(preview);
    }

    private static ExplorerWorkspaceGeometry Geometry(params ExplorerDisplayGeometry[] displays)
        => new(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "geometry:workspace-apply-service-test",
            isAutoArrangeEnabled: false,
            new PixelPoint(80, 80),
            displays);

    private static ExplorerDisplayGeometry Display(
        string displayRef,
        string deviceName,
        bool isPrimary,
        int columns,
        int rows)
    {
        var spacing = new PixelPoint(80, 80);
        var bounds = new PixelRect(0, 0, columns * spacing.X, rows * spacing.Y);
        var slots = new List<ExplorerNativeSlot>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"{displayRef}:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(column * spacing.X, row * spacing.Y)));
            }
        }

        return new ExplorerDisplayGeometry(
            displayRef,
            deviceName,
            bounds,
            bounds,
            isPrimary,
            dpiX: 96,
            dpiY: 96,
            spacing,
            phase: new PixelPoint(0, 0),
            gridOrigin: new PixelPoint(0, 0),
            columns,
            rows,
            slots);
    }

    private sealed class FakeRecoveryStore : IRecoverySessionStore
    {
        private readonly IReadOnlyList<RecoverySessionRecord> _unresolved;
        public int ListCount { get; private set; }

        public FakeRecoveryStore(params RecoverySessionRecord[] unresolved)
            => _unresolved = unresolved;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SaveAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord> UpdateAsync(
            RecoverySessionRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RecoverySessionRecord?> GetAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<RecoverySessionRecord?>(null);

        public Task<IReadOnlyList<RecoverySessionRecord>> ListUnresolvedAsync(
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return Task.FromResult<IReadOnlyList<RecoverySessionRecord>>(
                _unresolved.Take(limit).ToArray());
        }
    }
}
