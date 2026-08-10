using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopUnderstandingProviderTests
{
    [Fact]
    public void Request_UsesExplicitBoundsInsteadOfSilentTruncation()
    {
        var tooMany = Enumerable.Range(1, DesktopUnderstandingRequest.MaxItemCount + 1)
            .Select(index => CreateFacts($"id-{index}", $"item-{index}.txt"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopUnderstandingRequest(tooMany));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopUnderstandingRequest(
            [CreateFacts("id", "item.txt")],
            new string('x', DesktopUnderstandingRequest.MaxUserIntentLength + 1)));
    }

    [Fact]
    public async Task Provider_Success_UsesEphemeralRefsAndReturnsLogicalProposalOnly()
    {
        string? capturedBody = null;
        var responseContent = """
        {
          "summary":"开发工具与课程资料适合分区。",
          "layout_pattern":"PrimaryRight",
          "zones":[
            {"key":"development","name":"开发","purpose":"开发工具和项目"},
            {"key":"course","name":"课程","purpose":"课程文档"}
          ],
          "assignments":[
            {"item_ref":"i1","zone_key":"development"},
            {"item_ref":"i2","zone_key":"course"}
          ],
          "questions":["当前正在处理的项目需要单独区域吗？"],
          "confidence":0.92
        }
        """;

        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(CreateCompletionJson(responseContent));
        });
        var provider = CreateProvider(httpClient);
        var request = new DesktopUnderstandingRequest(
            [
                CreateFacts("stable-dev-secret", "Visual Studio Code.lnk", ItemKind.Shortcut, ".lnk"),
                CreateFacts("stable-course-secret", "通信原理实验报告.pdf"),
            ],
            "开发放右边，课程资料单独放。 ");

        var result = await provider.UnderstandAsync(request);

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        var proposal = Assert.IsType<DesktopUnderstandingProposal>(result.Proposal);
        Assert.Equal(DesktopZoneLayoutPattern.PrimaryRight, proposal.LayoutPattern);
        Assert.Equal(2, proposal.Zones.Count);
        Assert.Equal(2, proposal.Assignments.Count);
        Assert.Single(proposal.Questions);
        Assert.Equal(0.92, proposal.Confidence);

        using var outerJson = JsonDocument.Parse(capturedBody!);
        var messages = outerJson.RootElement.GetProperty("messages");
        const string prefix = "Propose the first DesktopAgent organization draft for this desktop. Input JSON:\n";
        var userContent = messages[1].GetProperty("content").GetString()!;
        Assert.StartsWith(prefix, userContent, StringComparison.Ordinal);

        using var inputJson = JsonDocument.Parse(userContent[prefix.Length..]);
        var inputRoot = inputJson.RootElement;
        Assert.Equal("开发放右边，课程资料单独放。 ", inputRoot.GetProperty("user_intent").GetString());
        var items = inputRoot.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("i1", items[0].GetProperty("item_ref").GetString());
        Assert.Equal("i2", items[1].GetProperty("item_ref").GetString());
        Assert.False(items[0].TryGetProperty("stable_id", out _));
        Assert.DoesNotContain("stable-dev-secret", userContent, StringComparison.Ordinal);
        Assert.DoesNotContain("stable-course-secret", userContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_InvalidAssignmentsAndLayout_AreRejectedLocally()
    {
        var payloads = new Queue<string>(new[]
        {
            """
            {"summary":"x","layout_pattern":"Freeform","zones":[{"key":"a","name":"A","purpose":"A"}],"assignments":[{"item_ref":"i1","zone_key":"a"},{"item_ref":"i2","zone_key":"a"}],"questions":[],"confidence":0.5}
            """,
            """
            {"summary":"x","layout_pattern":"Grid","zones":[{"key":"a","name":"A","purpose":"A"}],"assignments":[{"item_ref":"i1","zone_key":"a"},{"item_ref":"i3","zone_key":"a"}],"questions":[],"confidence":0.5}
            """,
            """
            {"summary":"x","layout_pattern":"Grid","zones":[{"key":"a","name":"A","purpose":"A"}],"assignments":[{"item_ref":"i1","zone_key":"missing"},{"item_ref":"i2","zone_key":"a"}],"questions":[],"confidence":0.5}
            """,
        });

        using var httpClient = CreateClient((_, _) => Task.FromResult(
            JsonResponse(CreateCompletionJson(payloads.Dequeue()))));
        var provider = CreateProvider(httpClient);
        var request = new DesktopUnderstandingRequest(
            [CreateFacts("id-1", "one.pdf"), CreateFacts("id-2", "two.pdf")]);

        var badLayout = await provider.UnderstandAsync(request);
        var unknownItem = await provider.UnderstandAsync(request);
        var unknownZone = await provider.UnderstandAsync(request);

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, badLayout.Status);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, unknownItem.Status);
        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, unknownZone.Status);
        Assert.Null(badLayout.Proposal);
        Assert.Null(unknownItem.Proposal);
        Assert.Null(unknownZone.Proposal);
    }

    private static DesktopItemFacts CreateFacts(
        string stableId,
        string displayName,
        ItemKind itemKind = ItemKind.File,
        string? extension = ".pdf")
        => new(stableId, displayName, itemKind, extension, false, true);

    private static OpenAiCompatibleDesktopUnderstandingProvider CreateProvider(HttpClient httpClient)
        => new(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new NullSecretResolver());

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new DelegateHandler(handler))
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string CreateCompletionJson(string content)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } },
        });

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
