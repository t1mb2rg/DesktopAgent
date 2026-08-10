using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopFirstRunIntelligenceProviderTests
{
    [Fact]
    public void Request_EnforcesBoundedUniqueWorkingSet()
    {
        var duplicateItems = new[]
        {
            CreateFacts("same", "A.pdf"),
            CreateFacts("same", "B.pdf"),
        };
        Assert.Throws<ArgumentException>(() => new DesktopFirstRunUnderstandingRequest(duplicateItems));

        var tooManyItems = Enumerable.Range(0, DesktopFirstRunUnderstandingRequest.MaxItems + 1)
            .Select(index => CreateFacts($"stable-{index}", $"{index}.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopFirstRunUnderstandingRequest(tooManyItems));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopFirstRunUnderstandingRequest(
            new[] { CreateFacts("stable", "A.pdf") },
            latestUserGuidance: new string('x', DesktopFirstRunUnderstandingRequest.MaxUserGuidanceChars + 1)));
    }

    [Fact]
    public async Task Provider_Success_UsesEphemeralRefsAndMapsBackToLocalIdentity()
    {
        var existingZoneId = Guid.NewGuid();
        string? capturedBody = null;

        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return CompletionResponse("""
                {
                  "intent_summary": "用户希望开发工具集中在右侧，并把当前任务保持清晰。",
                  "desktop_summary": "桌面主要包含开发工具和课程资料。",
                  "categories": [
                    { "key": "development", "name": "开发", "description": "开发工具与项目入口" },
                    { "key": "course", "name": "课程", "description": "课程相关文档" }
                  ],
                  "zones": [
                    {
                      "key": "development-zone",
                      "name": "开发",
                      "reuse_existing_zone_ref": "existing-zone-0001",
                      "placement_hint": "right",
                      "size_hint": "large",
                      "category_keys": ["development"]
                    }
                  ],
                  "assignments": [
                    {
                      "item_ref": "item-0001",
                      "category_key": "development",
                      "zone_key": "development-zone",
                      "confidence": 0.95,
                      "reason": "这是开发工具快捷入口。"
                    }
                  ],
                  "questions": [
                    { "key": "active-work", "text": "正在处理的文件需要单独保留在中央吗？" }
                  ]
                }
                """);
        });

        var provider = CreateProvider(httpClient);
        var request = new DesktopFirstRunUnderstandingRequest(
            new[]
            {
                CreateFacts("stable-secret-vscode", "Visual Studio Code.lnk", ItemKind.Shortcut, ".lnk"),
                CreateFacts("stable-secret-report", "通信原理实验报告.pdf"),
            },
            new[] { new IntelligenceZoneOption(existingZoneId, "现有开发区") },
            currentIntentSummary: "用户之前说希望桌面按用途分组。",
            latestUserGuidance: "开发相关的放右边，课程资料不要混进去。");

        var result = await provider.UnderstandAsync(request);

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        var draft = Assert.IsType<DesktopUnderstandingDraft>(result.Draft);
        Assert.Equal("stable-secret-vscode", Assert.Single(draft.Assignments).StableId);
        Assert.Equal(existingZoneId, Assert.Single(draft.Zones).ExistingZoneId);
        Assert.Equal(DesktopZonePlacementHint.Right, draft.Zones[0].PlacementHint);
        Assert.Equal(DesktopZoneSizeHint.Large, draft.Zones[0].SizeHint);
        Assert.Single(draft.Questions);

        Assert.NotNull(capturedBody);
        using var requestJson = JsonDocument.Parse(capturedBody!);
        var userContent = requestJson.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;

        const string inputMarker = "Input JSON:\n";
        var markerIndex = userContent.IndexOf(inputMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var contextJson = userContent[(markerIndex + inputMarker.Length)..];
        using var contextDocument = JsonDocument.Parse(contextJson);
        var context = contextDocument.RootElement;

        Assert.Equal(
            "开发相关的放右边，课程资料不要混进去。",
            context.GetProperty("latest_user_guidance").GetString());
        Assert.Equal(
            "用户之前说希望桌面按用途分组。",
            context.GetProperty("previous_intent_summary").GetString());
        Assert.Equal(
            "item-0001",
            context.GetProperty("items")[0].GetProperty("item_ref").GetString());
        Assert.Equal(
            "existing-zone-0001",
            context.GetProperty("existing_zones")[0].GetProperty("existing_zone_ref").GetString());
        Assert.DoesNotContain("stable-secret-vscode", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(existingZoneId.ToString(), contextJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_BrokenSemanticPayloads_AreRejectedTogether()
    {
        var responses = new Queue<string>(new[]
        {
            CreateUnderstandingJson(
                assignmentItemRef: "item-9999",
                existingZoneRef: "existing-zone-0001",
                placementHint: "right"),
            CreateUnderstandingJson(
                assignmentItemRef: "item-0001",
                existingZoneRef: "existing-zone-9999",
                placementHint: "right"),
            CreateUnderstandingJson(
                assignmentItemRef: "item-0001",
                existingZoneRef: "existing-zone-0001",
                placementHint: "pixel-perfect-left-ish"),
            """
            {
              "intent_summary": "用户希望按用途整理桌面。",
              "desktop_summary": "桌面主要是开发项目。",
              "categories": [null],
              "zones": [],
              "assignments": [],
              "questions": []
            }
            """,
        });

        using var httpClient = CreateClient((_, _) =>
            Task.FromResult(CompletionResponse(responses.Dequeue())));
        var provider = CreateProvider(httpClient);
        var request = new DesktopFirstRunUnderstandingRequest(
            new[] { CreateFacts("stable-local", "Visual Studio Code.lnk", ItemKind.Shortcut, ".lnk") },
            new[] { new IntelligenceZoneOption(Guid.NewGuid(), "开发") });

        var results = new[]
        {
            await provider.UnderstandAsync(request),
            await provider.UnderstandAsync(request),
            await provider.UnderstandAsync(request),
            await provider.UnderstandAsync(request),
        };

        Assert.All(
            results,
            result =>
            {
                Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
                Assert.Null(result.Draft);
            });
    }

    [Fact]
    public void Draft_RejectsBrokenCrossReferencesWithoutAddingExecutionSemantics()
    {
        var category = new DesktopUnderstandingCategoryDraft("development", "开发", "开发相关");
        var brokenZone = new DesktopUnderstandingZoneDraft(
            "dev-zone",
            "开发",
            null,
            DesktopZonePlacementHint.Right,
            DesktopZoneSizeHint.Medium,
            new[] { "missing-category" });

        Assert.Throws<ArgumentException>(() => new DesktopUnderstandingDraft(
            "用户偏好开发内容。",
            "桌面包含开发项目。",
            new[] { category },
            new[] { brokenZone }));
    }

    private static OpenAiCompatibleDesktopFirstRunIntelligenceProvider CreateProvider(HttpClient httpClient)
        => new(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new NullSecretResolver());

    private static DesktopItemFacts CreateFacts(
        string stableId,
        string displayName,
        ItemKind itemKind = ItemKind.File,
        string? extension = ".pdf")
        => new(
            stableId,
            displayName,
            itemKind,
            extension,
            isSystemItem: false,
            isFileSystemItem: true);

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new DelegateHandler(handler))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static HttpResponseMessage CompletionResponse(string messageContent)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = messageContent } },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        };

    private static string CreateUnderstandingJson(
        string assignmentItemRef,
        string existingZoneRef,
        string placementHint)
        => $$"""
            {
              "intent_summary": "用户希望按用途整理桌面。",
              "desktop_summary": "桌面主要是开发项目。",
              "categories": [
                { "key": "development", "name": "开发", "description": "开发相关" }
              ],
              "zones": [
                {
                  "key": "development-zone",
                  "name": "开发",
                  "reuse_existing_zone_ref": "{{existingZoneRef}}",
                  "placement_hint": "{{placementHint}}",
                  "size_hint": "medium",
                  "category_keys": ["development"]
                }
              ],
              "assignments": [
                {
                  "item_ref": "{{assignmentItemRef}}",
                  "category_key": "development",
                  "zone_key": "development-zone",
                  "confidence": 0.9,
                  "reason": "开发工具"
                }
              ],
              "questions": []
            }
            """;

    private sealed class NullSecretResolver : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
