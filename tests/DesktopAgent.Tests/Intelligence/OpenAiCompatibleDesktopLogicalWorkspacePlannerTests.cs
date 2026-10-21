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
        "Create one complete Desired Workspace Plan from this compact Provider-safe input JSON:\n";

    [Fact]
    public async Task Provider_SendsCompactLogicalWorkspaceAndLocallyBindsValidatedPlanIdentity()
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
        Assert.Equal("desktop-logical-workspace-plan-v1-compact", context.GetProperty("contract").GetString());
        Assert.Equal("把可移动项目放到副屏，主屏中央留白。", context.GetProperty("guidance").GetString());
        Assert.Equal(2, context.GetProperty("categories").GetArrayLength());
        Assert.Equal("工作资料", context.GetProperty("categories")[0].GetProperty("n").GetString());
        Assert.Equal(2, context.GetProperty("displays").GetArrayLength());
        Assert.True(context.GetProperty("displays")[0].GetProperty("p").GetBoolean());
        Assert.Equal(3, context.GetProperty("items").GetArrayLength());

        var firstItem = context.GetProperty("items")[0];
        Assert.Equal(2, firstItem.GetProperty("o").GetArrayLength());
        Assert.True(firstItem.GetProperty("u").GetBoolean());
        Assert.False(firstItem.TryGetProperty("f", out _));

        var fixedItem = context.GetProperty("items")[1];
        Assert.True(fixedItem.GetProperty("f").GetBoolean());

        var simpleItem = context.GetProperty("items")[2];
        Assert.False(simpleItem.TryGetProperty("o", out _));
        Assert.False(simpleItem.TryGetProperty("u", out _));
        Assert.False(simpleItem.TryGetProperty("f", out _));
        Assert.False(context.TryGetProperty("semantic", out _));
        Assert.False(context.TryGetProperty("retry", out _));
        Assert.False(context.TryGetProperty("observed_workspace", out _));
        Assert.DoesNotContain(workspace.ObservationId.ToString(), requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.TopologyFingerprint, requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.GridFingerprint, requestBody, StringComparison.Ordinal);

        Assert.DoesNotContain("stable-private-id", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\.\\DISPLAY", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pixel_position", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writer_command", requestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_PreservesFreshSemanticZoneIntentInCompactContext()
    {
        string? requestBody = null;
        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(CreateSemanticCompliantPlanJson()));
        });
        var workspace = CreateWorkspace();
        var provider = CreateProvider(httpClient);

        var result = await provider.PlanAsync(new DesktopLogicalWorkspacePlanningRequest(
            workspace,
            "按已理解的工作区整理到副屏。",
            semanticIntent: CreateSemanticIntent()));

        Assert.True(result.Succeeded);
        var context = ExtractUserContext(requestBody!);
        var semantic = context.GetProperty("semantic");
        Assert.Single(semantic.GetProperty("z").EnumerateArray());
        Assert.Equal("work", semantic.GetProperty("z")[0].GetProperty("k").GetString());
        Assert.Equal("工作区", semantic.GetProperty("z")[0].GetProperty("n").GetString());
        Assert.Equal(2, semantic.GetProperty("i").GetArrayLength());
        Assert.Equal("item-0001", semantic.GetProperty("i")[0].GetProperty("r").GetString());
        Assert.Equal("work", semantic.GetProperty("i")[0].GetProperty("z").GetString());
    }

    [Fact]
    public async Task Provider_RejectsPlanThatCollapsesFreshSemanticZoneIntent()
    {
        using var httpClient = CreateClient((_, _) => Task.FromResult(
            JsonResponse(CreateCompletionJson(CreateValidPlanJson()))));
        var provider = CreateProvider(httpClient);

        var result = await provider.PlanAsync(new DesktopLogicalWorkspacePlanningRequest(
            CreateWorkspace(),
            "按已理解的工作区整理到副屏。",
            semanticIntent: CreateSemanticIntent()));

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.False(result.Succeeded);
        Assert.False(result.CanRetryValidation);
        Assert.Contains("语义", result.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Contains("item-0003", result.DiagnosticMessage, StringComparison.Ordinal);
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
    public async Task Coordinator_RetriesOnlyStructuredValidationFailureAndSendsCompactSafeFeedback()
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
        var retry = retryContext.GetProperty("retry");
        Assert.Equal(JsonValueKind.Object, retry.ValueKind);
        var issues = retry.GetProperty("issues").EnumerateArray().ToArray();
        Assert.Contains(issues, static issue =>
            issue.GetProperty("c").GetString() ==
            nameof(DesktopDesiredWorkspacePlanIssueCode.FixedItemMoveRequested));
        Assert.Contains(issues, static issue =>
            issue.GetProperty("c").GetString() ==
            nameof(DesktopDesiredWorkspacePlanIssueCode.TargetCellCollision));

        var rejected = retry.GetProperty("previous");
        Assert.Equal(3, rejected.GetProperty("i").GetArrayLength());
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
            workspace.Items,
            workspace.Categories);
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

    private static DesktopLogicalWorkspaceSemanticPlanningIntent CreateSemanticIntent() => new(
        [
            new DesktopLogicalWorkspaceSemanticZoneIntent(
                "work",
                "工作区",
                ["work"]),
        ],
        [
            new DesktopLogicalWorkspaceSemanticItemIntent("item-0001", "work"),
            new DesktopLogicalWorkspaceSemanticItemIntent("item-0003", "work"),
        ]);

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
        ],
        [
            new DesktopObservedLogicalCategory("work", "工作资料", "需要集中处理的文档和笔记"),
            new DesktopObservedLogicalCategory("system", "系统项目", "不可移动的系统桌面项目"),
        ]);

    private static string CreateValidPlanJson() => """
        {
          "z": [
            { "k": "work", "n": "Work", "d": "secondary_1", "b": [0, 0, 4, 4] }
          ],
          "r": [
            { "k": "wallpaper-center", "p": "keep wallpaper visible", "d": "primary", "b": [1, 1, 2, 2] }
          ],
          "i": [
            { "r": "item-0001", "s": "p", "d": "secondary_1", "c": [0, 0], "z": "work", "y": "move work to secondary" },
            { "r": "item-0002", "s": "k" },
            { "r": "item-0003", "s": "k" }
          ]
        }
        """;

    private static string CreateSemanticCompliantPlanJson() => """
        {
          "z": [
            { "k": "work", "n": "工作区", "d": "secondary_1", "b": [0, 0, 4, 4] }
          ],
          "r": [
            { "k": "wallpaper-center", "p": "keep wallpaper visible", "d": "primary", "b": [1, 1, 2, 2] }
          ],
          "i": [
            { "r": "item-0001", "s": "p", "d": "secondary_1", "c": [0, 0], "z": "work" },
            { "r": "item-0002", "s": "k" },
            { "r": "item-0003", "s": "p", "d": "secondary_1", "c": [1, 0], "z": "work" }
          ]
        }
        """;

    private static string CreateCollisionPlanJson() => """
        {
          "z": [
            { "k": "work", "n": "Work", "d": "secondary_1", "b": [0, 0, 4, 4] }
          ],
          "r": [],
          "i": [
            { "r": "item-0001", "s": "p", "d": "secondary_1", "c": [0, 0], "z": "work" },
            { "r": "item-0002", "s": "p", "d": "secondary_1", "c": [1, 0], "z": "work" },
            { "r": "item-0003", "s": "p", "d": "secondary_1", "c": [0, 0], "z": "work" }
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
