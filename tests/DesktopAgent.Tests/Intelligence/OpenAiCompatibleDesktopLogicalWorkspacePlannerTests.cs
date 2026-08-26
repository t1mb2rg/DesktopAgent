using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts.Workspaces;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopLogicalWorkspacePlannerTests
{
    private const string UserPromptPrefix =
        "Create one complete Desired Workspace Plan from this Provider-safe input JSON:\n";

    [Fact]
    public async Task Provider_SendsOnlyLogicalWorkspaceAndLocallyBindsValidatedPlanIdentity()
    {
        string? requestBody = null;
        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(CreateValidPlanJson()));
        });
        var workspace = CreateWorkspace();
        var provider = CreateProvider(httpClient);

        var result = await provider.PlanAsync(new DesktopLogicalWorkspacePlanningRequest(
            workspace,
            "把可移动项目放到副屏，主屏中央留白。"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.ValidatedPlan);
        Assert.Null(result.RejectedPlan);
        Assert.Empty(result.ValidationIssues);
        Assert.NotEqual(Guid.Empty, result.ValidatedPlan.PlanId);
        Assert.Equal(workspace.ObservationId, result.ValidatedPlan.SourceObservationId);
        Assert.Equal(workspace.TopologyFingerprint, result.ValidatedPlan.TopologyFingerprint);
        Assert.Equal(workspace.GridFingerprint, result.ValidatedPlan.GridFingerprint);
        Assert.Equal("secondary_1", result.ValidatedPlan.Items[0].TargetDisplayRef);

        var context = ExtractUserContext(requestBody!);
        Assert.Equal("desktop-logical-workspace-plan-v1", context.GetProperty("contract").GetString());
        var observed = context.GetProperty("observed_workspace");
        Assert.Equal(2, observed.GetProperty("displays").GetArrayLength());
        Assert.Equal(3, observed.GetProperty("items").GetArrayLength());
        Assert.Equal(2, observed.GetProperty("items")[0].GetProperty("occupied_cells").GetArrayLength());
        Assert.False(context.TryGetProperty("retry_feedback", out _));

        Assert.DoesNotContain("stable-private-id", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\.\\DISPLAY", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixel_position", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer_command", requestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_ValidatorRejectsCollisionAndFixedMoveAsStructuredRetryableIssues()
    {
        using var httpClient = CreateClient((_, _) => Task.FromResult(
            JsonResponse(CreateCompletionJson(CreateCollisionPlanJson()))));
        var provider = CreateProvider(httpClient);

        var result = await provider.PlanAsync(new DesktopLogicalWorkspacePlanningRequest(
            CreateWorkspace(),
            "都放到副屏。"));

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.False(result.Succeeded);
        Assert.True(result.CanRetryValidation);
        Assert.Null(result.ValidatedPlan);
        Assert.NotNull(result.RejectedPlan);
        Assert.Contains(result.ValidationIssues, static issue =>
            issue.Code == DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested);
        Assert.Contains(result.ValidationIssues, static issue =>
            issue.Code == DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision);
    }

    [Fact]
    public async Task Coordinator_RetriesOnlyStructuredValidationFailureAndSendsSafeFeedback()
    {
        var responses = new Queue<string>([CreateCollisionPlanJson(), CreateValidPlanJson()]);
        var requestBodies = new List<string>();
        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse(CreateCompletionJson(responses.Dequeue()));
        });
        var coordinator = new DesktopLogicalWorkspacePlanningCoordinator(
            CreateProvider(httpClient),
            maxValidationRetries: 1);

        var result = await coordinator.PlanAsync(CreateWorkspace(), "都放到副屏，但固定项不动。");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, requestBodies.Count);

        var retryContext = ExtractUserContext(requestBodies[1]);
        var retry = retryContext.GetProperty("retry_feedback");
        Assert.Equal(JsonValueKind.Object, retry.ValueKind);
        var issues = retry.GetProperty("validation_issues").EnumerateArray().ToArray();
        Assert.Contains(issues, static issue =>
            issue.GetProperty("code").GetString() ==
            nameof(DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested));
        Assert.Contains(issues, static issue =>
            issue.GetProperty("code").GetString() ==
            nameof(DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision));

        var rejected = retry.GetProperty("previous_rejected_plan");
        Assert.Equal(3, rejected.GetProperty("items").GetArrayLength());
        Assert.False(rejected.TryGetProperty("plan_id", out _));
        Assert.False(rejected.TryGetProperty("source_observation_id", out _));
    }

    [Fact]
    public async Task Coordinator_DoesNotRetryMalformedProviderPayload()
    {
        var calls = 0;
        using var httpClient = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(CreateCompletionJson("not-json")));
        });
        var coordinator = new DesktopLogicalWorkspacePlanningCoordinator(
            CreateProvider(httpClient),
            maxValidationRetries: 2);

        var result = await coordinator.PlanAsync(CreateWorkspace(), "整理桌面。");

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(1, calls);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.FinalResult.Status);
        Assert.False(result.FinalResult.CanRetryValidation);
        Assert.Null(result.FinalResult.RejectedPlan);
    }

    [Fact]
    public void RetryRequest_RejectsMissingIssuesAndStaleWorkspaceBinding()
    {
        var workspace = CreateWorkspace();
        var plan = new DesktopDesiredWorkspacePlan(
            Guid.NewGuid(),
            workspace.ObservationId,
            workspace.TopologyFingerprint,
            workspace.GridFingerprint);
        var issue = new DesktopDesiredWorkspacePlanIssue(
            DesktopDesiredWorkspacePlanIssueCode.MissingItemRef,
            "missing");

        Assert.Throws<ArgumentException>(() => new DesktopLogicalWorkspacePlanningRequest(
            workspace,
            "retry",
            plan));

        var otherWorkspace = new DesktopObservedLogicalWorkspace(
            Guid.NewGuid(),
            workspace.TopologyFingerprint,
            workspace.GridFingerprint,
            workspace.Displays,
            workspace.Items);
        Assert.Throws<ArgumentException>(() => new DesktopLogicalWorkspacePlanningRequest(
            otherWorkspace,
            "retry",
            plan,
            [issue]));
    }

    private static OpenAiCompatibleDesktopLogicalWorkspacePlanner CreateProvider(HttpClient httpClient) =>
        new(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new StubSecretResolver());

    private static DesktopObservedLogicalWorkspace CreateWorkspace() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "topology-v1:SAFE",
        "grid-v1:SAFE",
        [
            new DesktopObservedLogicalDisplay(
                "primary",
                true,
                new DesktopLogicalTopologyRect(0, 0, 5000, 10000),
                new DesktopLogicalGridSize(4, 4)),
            new DesktopObservedLogicalDisplay(
                "secondary_1",
                false,
                new DesktopLogicalTopologyRect(5000, 0, 10000, 10000),
                new DesktopLogicalGridSize(4, 4)),
        ],
        [
            new DesktopObservedLogicalItem(
                "item-0001",
                "Report.pdf",
                ItemKind.File,
                "primary",
                new DesktopLogicalGridCell(0, 0),
                isMovable: true,
                isGridAligned: false,
                occupiedCells: [
                    new DesktopLogicalGridCell(0, 0),
                    new DesktopLogicalGridCell(1, 0),
                ],
                categoryKey: "work"),
            new DesktopObservedLogicalItem(
                "item-0002",
                "Recycle Bin",
                ItemKind.SystemItem,
                "primary",
                new DesktopLogicalGridCell(3, 3),
                isMovable: false,
                categoryKey: "system"),
            new DesktopObservedLogicalItem(
                "item-0003",
                "Notes.txt",
                ItemKind.File,
                "secondary_1",
                new DesktopLogicalGridCell(3, 3),
                isMovable: true,
                categoryKey: "work"),
        ]);

    private static string CreateValidPlanJson() => """
        {
          "zones": [
            {
              "zone_key": "work",
              "name": "Work",
              "display_ref": "secondary_1",
              "bounds": { "column": 0, "row": 0, "width": 4, "height": 4 }
            }
          ],
          "reserved_regions": [
            {
              "region_key": "wallpaper-center",
              "purpose": "keep wallpaper visible",
              "display_ref": "primary",
              "bounds": { "column": 1, "row": 1, "width": 2, "height": 2 }
            }
          ],
          "items": [
            {
              "item_ref": "item-0001",
              "state": "place",
              "target_display_ref": "secondary_1",
              "target_cell": { "column": 0, "row": 0 },
              "zone_key": "work",
              "reason": "move work to secondary"
            },
            { "item_ref": "item-0002", "state": "keep" },
            { "item_ref": "item-0003", "state": "keep" }
          ]
        }
        """;

    private static string CreateCollisionPlanJson() => """
        {
          "zones": [
            {
              "zone_key": "work",
              "name": "Work",
              "display_ref": "secondary_1",
              "bounds": { "column": 0, "row": 0, "width": 4, "height": 4 }
            }
          ],
          "reserved_regions": [],
          "items": [
            {
              "item_ref": "item-0001",
              "state": "place",
              "target_display_ref": "secondary_1",
              "target_cell": { "column": 0, "row": 0 },
              "zone_key": "work"
            },
            {
              "item_ref": "item-0002",
              "state": "place",
              "target_display_ref": "secondary_1",
              "target_cell": { "column": 1, "row": 0 },
              "zone_key": "work"
            },
            {
              "item_ref": "item-0003",
              "state": "place",
              "target_display_ref": "secondary_1",
              "target_cell": { "column": 0, "row": 0 },
              "zone_key": "work"
            }
          ]
        }
        """;

    private static JsonElement ExtractUserContext(string requestBody)
    {
        using var requestJson = JsonDocument.Parse(requestBody);
        var userContent = requestJson.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        Assert.StartsWith(UserPromptPrefix, userContent, StringComparison.Ordinal);
        using var contextJson = JsonDocument.Parse(userContent[UserPromptPrefix.Length..]);
        return contextJson.RootElement.Clone();
    }

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new DelegateHandler(handler))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string CreateCompletionJson(string messageContent) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                message = new
                {
                    content = messageContent,
                },
            },
        },
    });

    private sealed class StubSecretResolver : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
