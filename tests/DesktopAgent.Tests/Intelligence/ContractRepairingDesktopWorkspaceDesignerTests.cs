using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;

namespace DesktopAgent.Tests.Intelligence;

public sealed class ContractRepairingDesktopWorkspaceDesignerTests
{
    [Fact]
    public async Task InvalidProviderBlueprint_IsRepairedOnceWithinSameRevision()
    {
        var original = CreateRequest();
        DesktopWorkspaceDesignRequest? repairRequest = null;
        var calls = 0;
        var inner = new StubDesigner(request =>
        {
            calls++;
            if (calls == 1)
            {
                return DesktopWorkspaceDesignProviderResult.Failure(
                    IntelligenceProviderStatus.InvalidResponse,
                    "Provider Workspace Blueprint 未通过本地设计合同：Top + Height 不能超过 1。");
            }

            repairRequest = request;
            return DesktopWorkspaceDesignProviderResult.Success(
                request,
                CreateValidBlueprint(request.RevisionTarget));
        });
        var sut = new ContractRepairingDesktopWorkspaceDesigner(inner);

        var result = await sut.DesignAsync(original);

        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);
        Assert.NotNull(repairRequest);
        Assert.Equal(original.RevisionTarget, repairRequest!.RevisionTarget);
        Assert.Same(original.PreviousBlueprint, repairRequest.PreviousBlueprint);
        Assert.Equal(original.Displays, repairRequest.Displays);
        Assert.Equal(original.Categories, repairRequest.Categories);
        Assert.StartsWith(original.UserGuidance, repairRequest.UserGuidance, StringComparison.Ordinal);
        Assert.Contains("SAME requested revision", repairRequest.UserGuidance, StringComparison.Ordinal);
        Assert.Contains("left + width <= 1", repairRequest.UserGuidance, StringComparison.Ordinal);
        Assert.Contains("top + height <= 1", repairRequest.UserGuidance, StringComparison.Ordinal);
        Assert.Equal(original.RevisionTarget.Revision, result.Blueprint!.Revision);
    }

    [Fact]
    public async Task Timeout_IsNotRetriedAsContractRepair()
    {
        var calls = 0;
        var inner = new StubDesigner(_ =>
        {
            calls++;
            return DesktopWorkspaceDesignProviderResult.Failure(
                IntelligenceProviderStatus.Timeout,
                "timeout");
        });
        var sut = new ContractRepairingDesktopWorkspaceDesigner(inner);

        var result = await sut.DesignAsync(CreateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(IntelligenceProviderStatus.Timeout, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SecondInvalidResponse_StopsAfterSingleRepairAttempt()
    {
        var calls = 0;
        var inner = new StubDesigner(_ =>
        {
            calls++;
            return DesktopWorkspaceDesignProviderResult.Failure(
                IntelligenceProviderStatus.InvalidResponse,
                "still invalid");
        });
        var sut = new ContractRepairingDesktopWorkspaceDesigner(inner);

        var result = await sut.DesignAsync(CreateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.Equal(2, calls);
    }

    private static DesktopWorkspaceDesignRequest CreateRequest()
        => new(
            new DesktopWorkspaceDesignRevisionTarget(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                1),
            "桌面包含工作资料。",
            "整理主屏，但不要执行。",
            [
                new DesktopWorkspaceDesignDisplay(
                    "primary",
                    "主屏",
                    true,
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1),
                    16d / 9d),
            ],
            [new DesktopWorkspaceDesignCategorySummary("work", "工作", "工作资料", 3)]);

    private static DesktopWorkspaceBlueprint CreateValidBlueprint(
        DesktopWorkspaceDesignRevisionTarget target)
        => new(
            target.BlueprintId,
            target.Revision,
            "工作区",
            displayPolicies:
            [new DesktopWorkspaceBlueprintDisplayPolicy("primary", DesktopWorkspaceDisplayPolicyKind.Design)],
            zones:
            [
                new DesktopWorkspaceBlueprintZone(
                    "work-zone",
                    "Work",
                    "primary",
                    new DesktopWorkspaceRelativeRect(0, 0, 1, 1)),
            ],
            categoryRules:
            [new DesktopWorkspaceBlueprintCategoryRule("work", "work-zone")],
            parentRevision: target.ParentRevision);

    private sealed class StubDesigner(
        Func<DesktopWorkspaceDesignRequest, DesktopWorkspaceDesignProviderResult> handler)
        : IDesktopWorkspaceDesigner
    {
        public Task<DesktopWorkspaceDesignProviderResult> DesignAsync(
            DesktopWorkspaceDesignRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request));
    }
}
