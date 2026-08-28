using DesktopAgent.Application.DesktopLayouts.Workspaces;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;
using DesktopAgent.Core.Geometry;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.DesktopLayouts.Workspaces;

public sealed class DesktopWorkspaceDesignLoopCoordinatorTests
{
    [Fact]
    public async Task CapacityFailure_ReturnsProviderSafeDiagnostic_ThenRevisionTwoSucceeds()
    {
        var context = CreateContext(itemCount: 3);
        var designer = new ScriptedDesigner(request =>
        {
            if (request.RevisionTarget.Revision == 1)
            {
                Assert.Empty(request.RealizationDiagnostics);
                return Success(request, CreateBlueprint(request, zoneWidth: 0.10));
            }

            Assert.Equal(2, request.RevisionTarget.Revision);
            Assert.Equal(1, request.RevisionTarget.ParentRevision);
            Assert.NotNull(request.PreviousBlueprint);
            Assert.Equal(1, request.PreviousBlueprint!.Revision);

            var diagnostic = Assert.Single(request.RealizationDiagnostics);
            Assert.Equal(
                DesktopWorkspaceDesignRealizationDiagnosticCode.ZoneCapacityInsufficient,
                diagnostic.Code);
            Assert.Equal("primary", diagnostic.DisplayRef);
            Assert.Equal("tools", diagnostic.ElementKey);
            Assert.Equal(3, diagnostic.RequiredSlots);
            Assert.Equal(2, diagnostic.AvailableSlots);
            Assert.Null(diagnostic.ItemRef);
            Assert.DoesNotContain("StableId", diagnostic.Detail, StringComparison.OrdinalIgnoreCase);

            return Success(request, CreateBlueprint(request, zoneWidth: 0.20));
        });
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer);

        var result = await coordinator.DesignAndCompileAsync(
            context.Request,
            context.Semantics,
            context.Geometry,
            context.ItemFacts);

        Assert.True(result.Succeeded);
        Assert.Equal(DesktopWorkspaceDesignLoopStatus.Success, result.Status);
        Assert.Equal(2, result.DesignAttempts);
        Assert.Equal(2, result.Blueprint!.Revision);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Certificate);
        Assert.Equal(result.Plan!.PlanFingerprint, result.Certificate!.PlanFingerprint);
        Assert.Equal(2, designer.Requests.Count);
    }

    [Fact]
    public async Task RepeatedDesignFailure_IsBoundedAndKeepsImmutableRevisionChain()
    {
        var context = CreateContext(itemCount: 3);
        var designer = new ScriptedDesigner(request =>
            Success(request, CreateBlueprint(request, zoneWidth: 0.10)));
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer, maxDesignAttempts: 3);

        var result = await coordinator.DesignAndCompileAsync(
            context.Request,
            context.Semantics,
            context.Geometry,
            context.ItemFacts);

        Assert.False(result.Succeeded);
        Assert.Equal(DesktopWorkspaceDesignLoopStatus.DesignUnresolved, result.Status);
        Assert.Equal(3, result.DesignAttempts);
        Assert.Equal(new[] { 1, 2, 3 }, designer.Requests.Select(static request => request.RevisionTarget.Revision));
        Assert.Null(designer.Requests[0].PreviousBlueprint);
        Assert.Equal(1, designer.Requests[1].PreviousBlueprint!.Revision);
        Assert.Equal(2, designer.Requests[2].PreviousBlueprint!.Revision);
        Assert.All(designer.Requests.Skip(1), request => Assert.NotEmpty(request.RealizationDiagnostics));
        Assert.Contains(
            result.CompileIssues,
            issue => issue.Code == DesktopWorkspaceCompileIssueCode.ZoneCapacityInsufficient);
    }

    [Fact]
    public async Task NonDesignFixableCompileFailure_DoesNotBurnRedesignAttempts()
    {
        var context = CreateContext(itemCount: 1, autoArrangeEnabled: true);
        var designer = new ScriptedDesigner(request =>
            Success(request, CreateBlueprint(request, zoneWidth: 1.0)));
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer);

        var result = await coordinator.DesignAndCompileAsync(
            context.Request,
            context.Semantics,
            context.Geometry,
            context.ItemFacts);

        Assert.False(result.Succeeded);
        Assert.Equal(DesktopWorkspaceDesignLoopStatus.CompileBlocked, result.Status);
        Assert.Equal(1, result.DesignAttempts);
        Assert.Single(designer.Requests);
        Assert.Contains(
            result.CompileIssues,
            issue => issue.Code == DesktopWorkspaceCompileIssueCode.AutoArrangeEnabled);
    }

    [Fact]
    public async Task NonMovableFailure_UsesProviderSafeItemRefAndNeverLeaksStableId()
    {
        const string localStableId = "LOCAL-SENSITIVE-STABLE-ID";
        var blueprintId = Guid.NewGuid();
        var request = CreateRequest(
            blueprintId,
            itemCount: 1,
            criticalItems:
            [
                new DesktopWorkspaceDesignCriticalItem(
                    "item-0001",
                    "Recycle Bin",
                    "system",
                    "primary",
                    isMovable: false),
            ]);
        var semantics = new DesktopSemanticUnderstanding(
            "一个不可移动系统项目。",
            [new DesktopSemanticCategory("system", "系统项目", "Windows 系统桌面项目")],
            [new DesktopSemanticAssignment(localStableId, "system", 1, "system")]);
        var facts = new[]
        {
            new DesktopWorkspaceCompilationItemFact(
                localStableId,
                "primary",
                new PixelPoint(30, 50),
                isMovable: false),
        };
        var bindings = new[]
        {
            new DesktopWorkspaceCompilationItemRefBinding("item-0001", localStableId),
        };
        var geometry = CreateGeometry(autoArrangeEnabled: false);

        var designer = new ScriptedDesigner(current =>
        {
            if (current.RevisionTarget.Revision == 1)
                return Success(current, CreateBlueprint(current, 1.0, categoryKey: "system"));

            var diagnostic = Assert.Single(current.RealizationDiagnostics);
            Assert.Equal(
                DesktopWorkspaceDesignRealizationDiagnosticCode.NonMovableItemRequiresExplicitPolicy,
                diagnostic.Code);
            Assert.Equal("item-0001", diagnostic.ItemRef);
            Assert.Equal("system", diagnostic.CategoryKey);
            Assert.DoesNotContain(localStableId, diagnostic.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(localStableId, diagnostic.ItemRef!, StringComparison.Ordinal);

            return Success(
                current,
                CreateBlueprint(
                    current,
                    1.0,
                    categoryKey: "system",
                    itemOverrides:
                    [
                        new DesktopWorkspaceBlueprintItemOverride(
                            "item-0001",
                            DesktopWorkspaceItemOverrideKind.Keep),
                    ]));
        });
        var coordinator = new DesktopWorkspaceDesignLoopCoordinator(designer);

        var result = await coordinator.DesignAndCompileAsync(
            request,
            semantics,
            geometry,
            facts,
            bindings);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.DesignAttempts);
        var item = Assert.Single(result.Plan!.Items);
        Assert.Equal(DesktopWorkspaceCompiledItemDisposition.Keep, item.Disposition);

        var diagnosticTypeProperties = typeof(DesktopWorkspaceDesignRealizationDiagnostic)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();
        foreach (var forbidden in new[] { "StableId", "DeviceName", "Pixel", "Cell", "Grid", "Writer" })
        {
            Assert.DoesNotContain(
                diagnosticTypeProperties,
                property => property.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static TestContext CreateContext(int itemCount, bool autoArrangeEnabled = false)
    {
        var blueprintId = Guid.NewGuid();
        var request = CreateRequest(blueprintId, itemCount);
        var semantics = new DesktopSemanticUnderstanding(
            $"{itemCount} 个开发项目。",
            [new DesktopSemanticCategory("dev", "开发工具", "IDE、终端和工程工具")],
            Enumerable.Range(1, itemCount)
                .Select(index => new DesktopSemanticAssignment($"item-{index}", "dev", 1, "dev")));
        var facts = Enumerable.Range(1, itemCount)
            .Select(index => new DesktopWorkspaceCompilationItemFact(
                $"item-{index}",
                "primary",
                new PixelPoint(30 + (index - 1) * 80, 50),
                isMovable: true))
            .ToArray();
        return new TestContext(
            request,
            semantics,
            CreateGeometry(autoArrangeEnabled),
            facts);
    }

    private static DesktopWorkspaceDesignRequest CreateRequest(
        Guid blueprintId,
        int itemCount,
        IEnumerable<DesktopWorkspaceDesignCriticalItem>? criticalItems = null)
        => new(
            new DesktopWorkspaceDesignRevisionTarget(blueprintId, 1),
            "桌面包含待整理项目。",
            "生成一个清晰、可落地的工作区设计。",
            [
                new DesktopWorkspaceDesignDisplay(
                    "primary",
                    "主屏",
                    isPrimary: true,
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    2.0),
            ],
            [new DesktopWorkspaceDesignCategorySummary("dev", "开发工具", "IDE、终端和工程工具", itemCount)],
            criticalItems);

    private static DesktopWorkspaceBlueprint CreateBlueprint(
        DesktopWorkspaceDesignRequest request,
        double zoneWidth,
        string categoryKey = "dev",
        IEnumerable<DesktopWorkspaceBlueprintItemOverride>? itemOverrides = null)
        => new(
            request.RevisionTarget.BlueprintId,
            request.RevisionTarget.Revision,
            "测试 Workspace Blueprint。",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "tools",
                    "Tools",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, zoneWidth, 1)),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule(categoryKey, "tools")],
            itemOverrides: itemOverrides,
            parentRevision: request.RevisionTarget.ParentRevision);

    private static ExplorerWorkspaceGeometry CreateGeometry(bool autoArrangeEnabled)
    {
        const int columns = 10;
        const int rows = 2;
        var spacing = new PixelPoint(80, 80);
        var slots = new List<ExplorerNativeSlot>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                slots.Add(new ExplorerNativeSlot(
                    $"primary:slot:{column:D4}:{row:D4}",
                    column,
                    row,
                    new PixelPoint(30 + column * spacing.X, 50 + row * spacing.Y)));
            }
        }

        var display = new ExplorerDisplayGeometry(
            "primary",
            "DISPLAY1",
            new PixelRect(0, 0, 800, 240),
            new PixelRect(0, 0, 800, 240),
            isPrimary: true,
            dpiX: 96,
            dpiY: 96,
            spacing,
            new PixelPoint(30, 50),
            new PixelPoint(30, 50),
            columns,
            rows,
            slots);
        return new ExplorerWorkspaceGeometry(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "geometry:design-loop-test",
            autoArrangeEnabled,
            spacing,
            [display]);
    }

    private static DesktopWorkspaceDesignProviderResult Success(
        DesktopWorkspaceDesignRequest request,
        DesktopWorkspaceBlueprint blueprint)
        => DesktopWorkspaceDesignProviderResult.Success(request, blueprint);

    private sealed record TestContext(
        DesktopWorkspaceDesignRequest Request,
        DesktopSemanticUnderstanding Semantics,
        ExplorerWorkspaceGeometry Geometry,
        IReadOnlyList<DesktopWorkspaceCompilationItemFact> ItemFacts);

    private sealed class ScriptedDesigner : IDesktopWorkspaceDesigner
    {
        private readonly Func<DesktopWorkspaceDesignRequest, DesktopWorkspaceDesignProviderResult> _handler;

        internal List<DesktopWorkspaceDesignRequest> Requests { get; } = [];

        internal ScriptedDesigner(
            Func<DesktopWorkspaceDesignRequest, DesktopWorkspaceDesignProviderResult> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public Task<DesktopWorkspaceDesignProviderResult> DesignAsync(
            DesktopWorkspaceDesignRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
