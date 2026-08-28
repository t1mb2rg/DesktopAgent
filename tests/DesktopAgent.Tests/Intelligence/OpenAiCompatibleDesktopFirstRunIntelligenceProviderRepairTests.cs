using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAgent.Application.Intelligence;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Intelligence;

namespace DesktopAgent.Tests.Intelligence;

public sealed class OpenAiCompatibleDesktopFirstRunIntelligenceProviderRepairTests
{
    [Fact]
    public async Task MissingTopLevelCollection_IsRepairedOnce_WithPreciseProviderSafeDiagnostic()
    {
        var calls = 0;
        var bodies = new List<string>();
        using var httpClient = CreateClient(async (request, cancellationToken) =>
        {
            calls++;
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return calls == 1
                ? CompletionResponse("""
                    {
                      "intent_summary": "按用途整理桌面。",
                      "desktop_summary": "桌面包含开发工具。",
                      "categories": [
                        { "key": "dev", "name": "开发", "description": "开发工具" }
                      ],
                      "zones": [],
                      "assignments": []
                    }
                    """)
                : CompletionResponse(ValidUnderstandingJson());
        });
        var provider = CreateProvider(httpClient);

        var result = await provider.UnderstandAsync(new DesktopFirstRunUnderstandingRequest(
            [CreateFacts("LOCAL-SENSITIVE-STABLE-ID", "Visual Studio Code.lnk")]));

        Assert.Equal(IntelligenceProviderStatus.Success, result.Status);
        Assert.NotNull(result.Draft);
        Assert.Equal(2, calls);

        using var secondRequest = JsonDocument.Parse(bodies[1]);
        var repairPrompt = secondRequest.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        Assert.Contains("questions 字段缺失", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("All six top-level fields must be present", repairPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL-SENSITIVE-STABLE-ID", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("item-0001", repairPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepSemanticReferenceFailure_IsRejectedWithoutRepair()
    {
        var calls = 0;
        using var httpClient = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(CompletionResponse("""
                {
                  "intent_summary": "按用途整理桌面。",
                  "desktop_summary": "桌面包含开发工具。",
                  "categories": [
                    { "key": "dev", "name": "开发", "description": "开发工具" }
                  ],
                  "zones": [],
                  "assignments": [
                    {
                      "item_ref": "item-9999",
                      "category_key": "dev",
                      "zone_key": null,
                      "confidence": 0.9,
                      "reason": "开发工具"
                    }
                  ],
                  "questions": []
                }
                """));
        });
        var provider = CreateProvider(httpClient);

        var result = await provider.UnderstandAsync(new DesktopFirstRunUnderstandingRequest(
            [CreateFacts("stable-local", "Visual Studio Code.lnk")]));

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.Contains("不存在的 item_ref", result.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SecondEnvelopeFailure_StopsAfterSingleRepairAttempt()
    {
        var calls = 0;
        using var httpClient = CreateClient((_, _) =>
        {
            calls++;
            return Task.FromResult(CompletionResponse("""
                {
                  "intent_summary": "按用途整理桌面。",
                  "desktop_summary": "桌面包含开发工具。",
                  "categories": [
                    { "key": "dev", "name": "开发", "description": "开发工具" }
                  ],
                  "assignments": [],
                  "questions": []
                }
                """));
        });
        var provider = CreateProvider(httpClient);

        var result = await provider.UnderstandAsync(new DesktopFirstRunUnderstandingRequest(
            [CreateFacts("stable-local", "Visual Studio Code.lnk")]));

        Assert.Equal(IntelligenceProviderStatus.InvalidResponse, result.Status);
        Assert.Contains("zones 字段缺失", result.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    private static string ValidUnderstandingJson() => """
        {
          "intent_summary": "按用途整理桌面。",
          "desktop_summary": "桌面包含开发工具。",
          "categories": [
            { "key": "dev", "name": "开发", "description": "开发工具" }
          ],
          "zones": [],
          "assignments": [],
          "questions": []
        }
        """;

    private static OpenAiCompatibleDesktopFirstRunIntelligenceProvider CreateProvider(HttpClient httpClient)
        => new(
            httpClient,
            new OpenAiCompatibleProviderOptions(new Uri("https://example.test/v1"), "model"),
            new NullSecretResolver());

    private static DesktopItemFacts CreateFacts(string stableId, string displayName)
        => new(
            stableId,
            displayName,
            ItemKind.Shortcut,
            ".lnk",
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

    private sealed class NullSecretResolver : IIntelligenceSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
